using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using OrderManager.Backend.Lib;
using OrderManager.Backend.Lib.Workflow;

namespace OrderManager.Backend.Functions;

/// <summary>
/// POST /api/order-line-items/{lineItemId}/production-plan (contract §5) —
/// sets the item's method and the ordered list of steps it requires.
/// </summary>
public class SetProductionPlan(
    ISqlConnectionFactory connectionFactory,
    JwtService jwtService,
    ITemplateProvider templateProvider)
{
    public record ProductionPlanRequest(string Method, List<string>? Steps);

    private record LineItemRow(Guid Id, Guid OrderId, string CurrentStatus);

    private static readonly string[] ValidMethods = ["factory", "outsource", "import"];

    [Function("SetProductionPlan")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "order-line-items/{lineItemId}/production-plan")] HttpRequest req,
        string lineItemId)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);

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

        if (body.Steps is null || body.Steps.Count == 0)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "At least one production step is required");
        }

        if (body.Steps.Count != body.Steps.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "Production steps must not repeat");
        }

        var template = await templateProvider.GetActiveAsync(TemplateKind.ProductionStep);

        // Every requested step must exist in the client's template, and cannot be the
        // initial lifecycle status.
        var selectable = template.Statuses
            .Where(s => !string.Equals(s.Code, template.InitialStatus, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(s => s.Code, s => s.Code, StringComparer.OrdinalIgnoreCase);

        var canonicalSteps = new List<string>();
        foreach (var step in body.Steps)
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
            "SELECT id AS Id, order_id AS OrderId, current_status AS CurrentStatus FROM order_line_items WHERE id = @Id",
            new { Id = id });

        if (item is null)
        {
            throw new AppException(StatusCodes.Status404NotFound, "NOT_FOUND", $"Line item {id} not found");
        }

        // Re-planning is allowed while nothing has been worked on yet. Once a step has
        // started, changing the plan would orphan recorded work and photos, so it is
        // refused rather than silently discarding them.
        var workStarted = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM order_line_item_steps WHERE line_item_id = @Id AND status <> 'pending'",
            new { Id = id });

        if (workStarted > 0)
        {
            throw new AppException(StatusCodes.Status409Conflict, "PLAN_LOCKED",
                "Production has already started on this item — its plan can no longer be changed");
        }

        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync(
            "DELETE FROM order_line_item_steps WHERE line_item_id = @Id",
            new { Id = id }, transaction);

        for (var i = 0; i < canonicalSteps.Count; i++)
        {
            await connection.ExecuteAsync(
                @"INSERT INTO order_line_item_steps (line_item_id, step_name, sequence, status)
                  VALUES (@LineItemId, @StepName, @Sequence, 'pending')",
                new { LineItemId = id, StepName = canonicalSteps[i], Sequence = i + 1 },
                transaction);
        }

        await connection.ExecuteAsync(
            "UPDATE order_line_items SET method = @Method, updated_at = SYSUTCDATETIME() WHERE id = @Id",
            new { Id = id, Method = body.Method.ToLowerInvariant() },
            transaction);

        transaction.Commit();

        var steps = await connection.QueryAsync(
            @"SELECT id, step_name, sequence, status FROM order_line_item_steps
              WHERE line_item_id = @Id ORDER BY sequence",
            new { Id = id });

        return new OkObjectResult(new
        {
            lineItemId = id,
            method = body.Method.ToLowerInvariant(),
            steps = steps.Select(s => new
            {
                stepId = (Guid)s.id,
                stepName = (string)s.step_name,
                sequence = (int)s.sequence,
                status = (string)s.status,
            }).ToList(),
        });
    }
}
