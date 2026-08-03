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

        // Only genuine units of work. PENDING, WITH_SUPPLIER, SEMI_FINISHED and FINISHED
        // are lifecycle states the system sets — offering them on the checklist would let
        // a supervisor plan "With Supplier" as though it were carpentry.
        var steps = template.SelectableSteps
            .Select(s => new { code = s.Code, name = s.Name })
            .ToList();

        return new OkObjectResult(new { initialStatus = template.InitialStatus, steps });
    }
}
