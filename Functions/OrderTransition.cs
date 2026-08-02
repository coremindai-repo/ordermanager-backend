using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using OrderManager.Backend.Lib;
using OrderManager.Backend.Lib.Workflow;

namespace OrderManager.Backend.Functions;

/// <summary>
/// The generic order-level transition endpoint (API-INTERFACE-CONTRACT.md §4) —
/// validated against the client's active process_template.
/// </summary>
public class OrderTransition(
    ISqlConnectionFactory connectionFactory,
    JwtService jwtService,
    ITemplateProvider templateProvider,
    TransitionValidator validator)
{
    public record TransitionRequest(string TargetStatus, string? Notes, string[]? PhotoUrls);

    private record OrderRow(Guid Id, string CurrentStatus, Guid? StoreId, string OrderType);

    [Function("OrderTransition")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders/{orderId}/transition")] HttpRequest req,
        string orderId)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);

        if (!Guid.TryParse(orderId, out var id))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "orderId must be a GUID");
        }

        var body = await req.ReadFromJsonAsync<TransitionRequest>();
        if (string.IsNullOrWhiteSpace(body?.TargetStatus))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "targetStatus is required");
        }

        using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync();

        var order = await connection.QuerySingleOrDefaultAsync<OrderRow>(
            @"SELECT id AS Id, current_status AS CurrentStatus, store_id AS StoreId, order_type AS OrderType
              FROM orders WHERE id = @Id",
            new { Id = id });

        if (order is null)
        {
            throw new AppException(StatusCodes.Status404NotFound, "NOT_FOUND", $"Order {id} not found");
        }

        var template = await templateProvider.GetActiveAsync(TemplateKind.Process);

        // orderType drives the customer/stock branch: invoicing applies to customer
        // orders only, so stock orders route straight into the logistics chain.
        var decision = validator.Validate(
            template, order.CurrentStatus, body.TargetStatus, caller.Roles, orderType: order.OrderType);
        if (!decision.IsAllowed)
        {
            throw TransitionOutcomeMapper.ToException(decision);
        }

        // Order-wide gate the validator cannot check itself (it holds no DB access):
        // an order leaves the factory as one unit, so every line item must be finished
        // — not merely every step within some single line item.
        if (decision.MatchedRule?.RequiresAllLineItemsComplete == true)
        {
            await EnsureAllLineItemsCompleteAsync(connection, id);
        }

        // Statuses are kept store-agnostic ("In Transit", not "Sent to Kochi"), so the
        // destination has to exist as data before goods start moving towards it.
        if (decision.MatchedRule?.RequiresDestinationStore == true && order.StoreId is null)
        {
            throw TransitionOutcomeMapper.ToException(new TransitionDecision(
                TransitionOutcome.DestinationStoreRequired,
                "This order has no destination store — set one via POST /api/orders/{orderId}/destination-store before dispatching"));
        }

        // Persist the template's spelling, not whatever casing the client sent.
        var targetStatus = template.ResolveStatusCode(body.TargetStatus);

        using var transaction = connection.BeginTransaction();

        // The WHERE clause pins the status we validated against, so two callers
        // transitioning the same order concurrently cannot both win.
        var updatedAt = await connection.QuerySingleOrDefaultAsync<DateTime?>(
            @"UPDATE orders SET current_status = @To, updated_at = SYSUTCDATETIME()
              OUTPUT inserted.updated_at
              WHERE id = @Id AND current_status = @From",
            new { Id = id, To = targetStatus, From = order.CurrentStatus },
            transaction);

        if (updatedAt is null)
        {
            throw new AppException(
                StatusCodes.Status409Conflict,
                "ILLEGAL_TRANSITION",
                "Order status changed concurrently — reload and retry");
        }

        await connection.ExecuteAsync(
            @"INSERT INTO order_status_history (order_id, from_status, to_status, user_id, notes, photo_urls)
              VALUES (@OrderId, @From, @To, @UserId, @Notes, @PhotoUrls)",
            new
            {
                OrderId = id,
                From = order.CurrentStatus,
                To = targetStatus,
                UserId = caller.UserId,
                body.Notes,
                PhotoUrls = body.PhotoUrls is null ? null : JsonSerializer.Serialize(body.PhotoUrls),
            },
            transaction);

        transaction.Commit();

        // Epic 7 hooks in here: notification-worthy transitions fire a push after the
        // write succeeds (CLAUDE.md §7). Intentionally not implemented in this epic.

        return new OkObjectResult(new
        {
            orderId = id,
            previousStatus = order.CurrentStatus,
            currentStatus = targetStatus,
            updatedAt = TimeFormat.Utc(updatedAt.Value),
        });
    }

    private async Task EnsureAllLineItemsCompleteAsync(SqlConnection connection, Guid orderId)
    {
        var statuses = (await connection.QueryAsync<string>(
            "SELECT current_status FROM order_line_items WHERE order_id = @Id",
            new { Id = orderId })).ToList();

        // "Finished" is whatever the production template treats as terminal, so a
        // client with different final stages needs no code change here.
        var productionTemplate = await templateProvider.GetActiveAsync(TemplateKind.ProductionStep);
        var terminal = productionTemplate.TerminalStatuses;

        if (LineItemCompletion.AllComplete(statuses, terminal))
        {
            return;
        }

        var blocking = LineItemCompletion.IncompleteStatuses(statuses, terminal);
        var detail = statuses.Count == 0
            ? "the order has no line items"
            : $"line items still at: {string.Join(", ", blocking)}";

        // Routed through the mapper so status/code mapping lives in exactly one place.
        throw TransitionOutcomeMapper.ToException(new TransitionDecision(
            TransitionOutcome.LineItemsIncomplete,
            $"Every line item must be complete before this transition — {detail}"));
    }
}
