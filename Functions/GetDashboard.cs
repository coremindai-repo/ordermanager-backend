using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using OrderManager.Backend.Lib;
using OrderManager.Backend.Lib.Workflow;

namespace OrderManager.Backend.Functions;

/// <summary>
/// GET /api/dashboard — per-status order counts for the dashboard tab badges.
///
/// GET /api/orders already returns the orders behind each tab; this returns just the
/// counts, so a dashboard showing several tabs does not have to fetch every order in
/// each of them to render a number.
///
/// Statuses come from the active process template rather than a hard-coded list, so a
/// template change is reflected without a code change — including statuses with zero
/// orders, which a GROUP BY alone would omit and leave the tab missing from the UI.
///
/// Documented in API-INTERFACE-CONTRACT.md §10 (GET /api/dashboard).
/// </summary>
public class GetDashboard(
    ISqlConnectionFactory connectionFactory,
    JwtService jwtService,
    ITemplateProvider templateProvider)
{
    [Function("GetDashboard")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard")] HttpRequest req)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);

        var requestedMine = string.Equals(req.Query["mine"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
        var restrictToOwn = AccessScope.RestrictOrdersToOwn(caller.Roles, requestedMine);

        var template = await templateProvider.GetActiveAsync(TemplateKind.Process);

        using var connection = connectionFactory.CreateConnection();

        var counts = (await connection.QueryAsync(
            @"SELECT current_status, COUNT(*) AS count
              FROM orders
              WHERE (@RestrictToOwn = 0 OR created_by = @UserId)
                AND is_test_data = 0
              GROUP BY current_status",
            new { RestrictToOwn = restrictToOwn ? 1 : 0, UserId = caller.UserId }))
            .ToDictionary(r => (string)r.current_status, r => (int)r.count, StringComparer.OrdinalIgnoreCase);

        // Driven off the template so every tab appears, including empty ones.
        var byStatus = template.Statuses
            .Select(s => new
            {
                status = s.Code,
                name = s.Name,
                count = counts.GetValueOrDefault(s.Code, 0),
            })
            .ToList();

        // Any status not in the template — an order stranded by a template change —
        // would otherwise vanish from the dashboard entirely.
        var unrecognised = counts.Keys
            .Where(k => !template.Statuses.Any(s => string.Equals(s.Code, k, StringComparison.OrdinalIgnoreCase)))
            .Select(k => new { status = k, name = k, count = counts[k] })
            .ToList();

        return new OkObjectResult(new
        {
            byStatus,
            unrecognisedStatuses = unrecognised,
            total = counts.Values.Sum(),
            scope = restrictToOwn ? "own" : "all",
        });
    }
}
