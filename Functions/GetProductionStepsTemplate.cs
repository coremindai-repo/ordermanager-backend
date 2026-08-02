using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using OrderManager.Backend.Lib;
using OrderManager.Backend.Lib.Workflow;

namespace OrderManager.Backend.Functions;

/// <summary>
/// GET /api/production-steps-template (contract §5) — drives the
/// "This item will require" checklist on the production plan screen.
/// </summary>
public class GetProductionStepsTemplate(JwtService jwtService, ITemplateProvider templateProvider)
{
    [Function("GetProductionStepsTemplate")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "production-steps-template")] HttpRequest req)
    {
        AuthHelper.RequireCaller(req, jwtService);

        var template = await templateProvider.GetActiveAsync(TemplateKind.ProductionStep);

        // The initial status is a lifecycle marker, not a factory step anyone performs,
        // so it is excluded from the checklist the supervisor picks from.
        var steps = template.Statuses
            .Where(s => !string.Equals(s.Code, template.InitialStatus, StringComparison.OrdinalIgnoreCase))
            .Select(s => new { code = s.Code, name = s.Name })
            .ToList();

        return new OkObjectResult(new { initialStatus = template.InitialStatus, steps });
    }
}
