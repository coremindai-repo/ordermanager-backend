using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using OrderManager.Backend.Lib;
using OrderManager.Backend.Lib.Workflow;

namespace OrderManager.Backend.Functions;

/// <summary>
/// POST /api/order-line-items/{lineItemId}/production-plan (contract §5) —
/// sets the item's method and the ordered list of steps it requires.
///
/// factory_supervisor only: they are the one choosing method and steps on the design
/// screens, same as every other production-side decision in CLAUDE.md §5's role table.
///
/// Also where NEW -> IN_PRODUCTION fires (process template v7): nothing else in the
/// system ever triggers it, and this is the real-world moment production starts — see
/// <see cref="AdvanceOrderIfStillNewAsync"/>.
/// </summary>
public class SetProductionPlan(
    ISqlConnectionFactory connectionFactory,
    JwtService jwtService,
    ITemplateProvider templateProvider,
    TransitionValidator validator,
    ILogger<SetProductionPlan> logger)
{
    public record ProductionPlanRequest(string Method, List<string>? Steps);

    private record LineItemRow(Guid Id, Guid OrderId, string CurrentStatus, string? Method, string OrderStatus, string OrderType);

    private static readonly string[] ValidMethods = ["factory", "outsource", "import"];

    [Function("SetProductionPlan")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "order-line-items/{lineItemId}/production-plan")] HttpRequest req,
        string lineItemId)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);
        AuthHelper.RequireRole(caller, "factory_supervisor");

        if (!Guid.TryParse(lineItemId, out var id))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "lineItemId must be a GUID");
        }

        var body = await req.ReadFromJsonAsync<ProductionPlanRequest>();
        if (body is null || string.IsNullOrWhiteSpace(body.Method) ||
            !ValidMethods.Contains(body.Method, StringComparer.OrdinalIgnoreCase))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                $"method must be one of: {string.Join(", ", ValidMethods)}");
        }

        var requestedMethod = body.Method.ToLowerInvariant();
        var requestedSteps = body.Steps ?? [];

        // Only a factory item performs in-house steps. An outsource/import item's work
        // happens entirely at the supplier, so an empty (or omitted) steps array is the
        // correct, expected input for those methods — not an omission to reject. This
        // used to require >=1 step for every method, which meant the normal outsource/
        // import case (no factory steps at all) hit a validation error it never should
        // have reached.
        if (requestedMethod == "factory" && requestedSteps.Count == 0)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "At least one production step is required for method 'factory'");
        }

        if (requestedSteps.Count != requestedSteps.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "Production steps must not repeat");
        }

        var template = await templateProvider.GetActiveAsync(TemplateKind.ProductionStep);

        // Every requested step must be a status the template marks selectable. Lifecycle
        // states (PENDING, WITH_SUPPLIER, SEMI_FINISHED, FINISHED) are set by the system
        // — planning one as a step would create a meaningless work item a supervisor
        // could then mark complete.
        var selectable = template.SelectableSteps
            .ToDictionary(s => s.Code, s => s.Code, StringComparer.OrdinalIgnoreCase);

        var canonicalSteps = new List<string>();
        foreach (var step in requestedSteps)
        {
            if (!selectable.TryGetValue(step, out var canonical))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                    $"'{step}' is not a production step defined by the active template");
            }
            canonicalSteps.Add(canonical);
        }

        using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync();

        var item = await connection.QuerySingleOrDefaultAsync<LineItemRow>(
            @"SELECT li.id AS Id, li.order_id AS OrderId, li.current_status AS CurrentStatus, li.method AS Method,
                     o.current_status AS OrderStatus, o.order_type AS OrderType
              FROM order_line_items li
              JOIN orders o ON o.id = li.order_id
              WHERE li.id = @Id",
            new { Id = id });

        if (item is null)
        {
            throw new AppException(StatusCodes.Status404NotFound, "NOT_FOUND", $"Line item {id} not found");
        }

        // A plan in progress may be ADDED to but not cut back. This matters for a
        // claimed semi-finished item: it arrives with completed steps and needs its
        // remaining ones planned under the new order. Removing or resetting work that
        // has already happened is still refused — that is what the original lock was
        // protecting, and it still holds.
        var existing = (await connection.QueryAsync<ExistingStep>(
            @"SELECT step_name AS StepName, sequence AS Sequence, status AS Status
              FROM order_line_item_steps WHERE line_item_id = @Id ORDER BY sequence",
            new { Id = id })).ToList();

        var change = ProductionPlanChange.Evaluate(existing, canonicalSteps);
        if (!change.IsAllowed)
        {
            throw new AppException(StatusCodes.Status409Conflict, "PLAN_LOCKED", change.Message!);
        }

        if (!ProductionPlanChange.CanChangeMethod(existing) &&
            !string.Equals(item.Method, requestedMethod, StringComparison.OrdinalIgnoreCase))
        {
            // The recorded steps were performed under the old method; switching now
            // would leave that history describing a route the item never took.
            throw new AppException(StatusCodes.Status409Conflict, "PLAN_LOCKED",
                $"Production has already started under method '{item.Method}' — it cannot be changed to '{requestedMethod}'");
        }

        using var transaction = connection.BeginTransaction();

        foreach (var stepName in change.StepsToRemove)
        {
            await connection.ExecuteAsync(
                "DELETE FROM order_line_item_steps WHERE line_item_id = @Id AND step_name = @StepName",
                new { Id = id, StepName = stepName }, transaction);
        }

        // Appended steps continue the sequence rather than renumbering, so existing
        // steps keep the position they were worked in.
        var nextSequence = existing.Count == 0 ? 1 : existing.Max(s => s.Sequence) + 1;

        foreach (var stepName in change.StepsToAdd)
        {
            await connection.ExecuteAsync(
                @"INSERT INTO order_line_item_steps (line_item_id, step_name, sequence, status)
                  VALUES (@LineItemId, @StepName, @Sequence, 'pending')",
                new { LineItemId = id, StepName = stepName, Sequence = nextSequence++ },
                transaction);
        }

        await connection.ExecuteAsync(
            "UPDATE order_line_items SET method = @Method, updated_at = SYSUTCDATETIME() WHERE id = @Id",
            new { Id = id, Method = requestedMethod },
            transaction);

        await AdvanceOrderIfStillNewAsync(connection, transaction, item, caller);

        transaction.Commit();

        var steps = await connection.QueryAsync(
            @"SELECT id, step_name, sequence, status FROM order_line_item_steps
              WHERE line_item_id = @Id ORDER BY sequence",
            new { Id = id });

        return new OkObjectResult(new
        {
            lineItemId = id,
            method = requestedMethod,
            steps = steps.Select(s => new
            {
                stepId = (Guid)s.id,
                stepName = (string)s.step_name,
                sequence = (int)s.sequence,
                status = (string)s.status,
            }).ToList(),
        });
    }

    /// <summary>
    /// Fires NEW -> IN_PRODUCTION the first time any line item on the order gets a
    /// production plan set — process template v7. Nothing else in the system ever
    /// triggers this transition (mobile traced every order sitting at NEW forever, with
    /// no screen or automatic path to move it); this is the real-world moment
    /// production starts, symmetric with the order leaving production only once every
    /// item is complete (requiresAllLineItemsComplete).
    ///
    /// Idempotent: a no-op once the order has moved past NEW, so later re-plans and a
    /// semi-finished item's return to the factory checklist do not attempt this again.
    /// Runs through the same validator and writes the same order_status_history row as
    /// a manual transition — no template rule is bypassed. Best-effort like
    /// OutsourcingRequests.AdvanceLineItemsAsync: the supervisor's actual request (set
    /// this item's plan) must not fail because a secondary bookkeeping step could not
    /// apply, so a validation failure here is logged, not thrown.
    /// </summary>
    private async Task AdvanceOrderIfStillNewAsync(
        SqlConnection connection, SqlTransaction transaction, LineItemRow item, Caller caller)
    {
        if (!string.Equals(item.OrderStatus, "NEW", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var processTemplate = await templateProvider.GetActiveAsync(TemplateKind.Process);

        var decision = validator.Validate(
            processTemplate, item.OrderStatus, "IN_PRODUCTION", caller.Roles, orderType: item.OrderType);

        if (!decision.IsAllowed)
        {
            logger.LogWarning(
                "Order {OrderId} not advanced to IN_PRODUCTION after a production plan on line item {LineItemId}: {Reason}",
                item.OrderId, item.Id, decision.Message);
            return;
        }

        var canonicalStatus = processTemplate.ResolveStatusCode("IN_PRODUCTION");

        var updated = await connection.ExecuteAsync(
            @"UPDATE orders SET current_status = @To, updated_at = SYSUTCDATETIME()
              WHERE id = @OrderId AND current_status = @From",
            new { OrderId = item.OrderId, To = canonicalStatus, From = item.OrderStatus },
            transaction);

        if (updated == 0)
        {
            // Changed concurrently (e.g. two line items planned at once) — the other
            // caller's write already advanced it, so there is nothing left to do here.
            return;
        }

        await connection.ExecuteAsync(
            @"INSERT INTO order_status_history (order_id, from_status, to_status, user_id, notes)
              VALUES (@OrderId, @From, @To, @UserId, 'Automatic: first production plan set on the order')",
            new { OrderId = item.OrderId, From = item.OrderStatus, To = canonicalStatus, UserId = caller.UserId },
            transaction);
    }
}
