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
/// The generic line-item transition endpoint (API-INTERFACE-CONTRACT.md §4) —
/// validated against the client's active production_step_template plus the item's
/// chosen method (CLAUDE.md §5).
/// </summary>
public class LineItemTransition(
    ISqlConnectionFactory connectionFactory,
    JwtService jwtService,
    ITemplateProvider templateProvider,
    TransitionValidator validator)
{
    public record TransitionRequest(string TargetStatus, string? Notes, string[]? PhotoUrls);

    private record LineItemRow(Guid Id, string CurrentStatus, string? Method);

    [Function("LineItemTransition")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "order-line-items/{lineItemId}/transition")] HttpRequest req,
        string lineItemId)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);

        if (!Guid.TryParse(lineItemId, out var id))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "lineItemId must be a GUID");
        }

        var body = await req.ReadFromJsonAsync<TransitionRequest>();
        if (string.IsNullOrWhiteSpace(body?.TargetStatus))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "targetStatus is required");
        }

        using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync();

        var item = await connection.QuerySingleOrDefaultAsync<LineItemRow>(
            @"SELECT id AS Id, current_status AS CurrentStatus, method AS Method
              FROM order_line_items WHERE id = @Id",
            new { Id = id });

        if (item is null)
        {
            throw new AppException(StatusCodes.Status404NotFound, "NOT_FOUND", $"Line item {id} not found");
        }

        var template = await templateProvider.GetActiveAsync(TemplateKind.ProductionStep);

        var decision = validator.Validate(template, item.CurrentStatus, body.TargetStatus, caller.Roles, item.Method);
        if (!decision.IsAllowed)
        {
            throw TransitionOutcomeMapper.ToException(decision);
        }

        var targetStatus = template.ResolveStatusCode(body.TargetStatus);

        using var transaction = connection.BeginTransaction();

        var updatedAt = await connection.QuerySingleOrDefaultAsync<DateTime?>(
            @"UPDATE order_line_items SET current_status = @To, current_step = @To, updated_at = SYSUTCDATETIME()
              OUTPUT inserted.updated_at
              WHERE id = @Id AND current_status = @From",
            new { Id = id, To = targetStatus, From = item.CurrentStatus },
            transaction);

        if (updatedAt is null)
        {
            throw new AppException(
                StatusCodes.Status409Conflict,
                "ILLEGAL_TRANSITION",
                "Line item status changed concurrently — reload and retry");
        }

        await connection.ExecuteAsync(
            @"INSERT INTO line_item_status_history (line_item_id, from_status, to_status, user_id, notes, photo_urls)
              VALUES (@LineItemId, @From, @To, @UserId, @Notes, @PhotoUrls)",
            new
            {
                LineItemId = id,
                From = item.CurrentStatus,
                To = targetStatus,
                UserId = caller.UserId,
                body.Notes,
                PhotoUrls = body.PhotoUrls is null ? null : JsonSerializer.Serialize(body.PhotoUrls),
            },
            transaction);

        transaction.Commit();

        // Epic 7 notification hook (item assigned / ready) goes here.

        return new OkObjectResult(new
        {
            lineItemId = id,
            previousStatus = item.CurrentStatus,
            currentStatus = targetStatus,
            updatedAt = TimeFormat.Utc(updatedAt.Value),
        });
    }
}
