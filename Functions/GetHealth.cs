using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using OrderManager.Backend.Lib.Soho;

namespace OrderManager.Backend.Functions;

/// <summary>
/// GET /api/health — liveness plus a visible statement of which SOHO client is
/// actually wired up, so a stubbed deployment is obvious at a glance rather than
/// something you have to go and read app settings to discover.
///
/// Anonymous by design: it carries no customer data, and requiring a token would
/// make it useless for uptime checks.
/// </summary>
public class GetHealth(ISohoClient sohoClient)
{
    [Function("GetHealth")]
    public IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req)
    {
        // Reported from the resolved client rather than from configuration, so this
        // reflects what is genuinely running, not what someone intended to configure.
        var (mode, isPlaceholder) = sohoClient switch
        {
            StubSohoClient => ("stub", true),
            UnconfiguredSohoClient => ("unconfigured", true),
            _ => ("live", false),
        };

        return new OkObjectResult(new
        {
            status = "ok",
            soho = new
            {
                mode,
                isPlaceholder,
                warning = isPlaceholder
                    ? "SOHO is not a real integration. Customer orders either receive placeholder references (stub) or are rejected (unconfigured). Must be resolved before onboarding real client users."
                    : null,
            },
        });
    }
}
