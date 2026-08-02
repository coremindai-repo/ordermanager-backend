using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OrderManager.Backend.Lib;
using OrderManager.Backend.Lib.Notifications;
using OrderManager.Backend.Lib.Orders;
using OrderManager.Backend.Lib.Photos;
using OrderManager.Backend.Lib.Soho;
using OrderManager.Backend.Lib.Workflow;
using OrderManager.Backend.Middleware;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.UseMiddleware<ExceptionHandlingMiddleware>();

builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddSingleton<JwtService>();

// Singleton: TemplateProvider's cache is deliberately per-worker-instance and lives
// for the life of the process. Template changes require a redeploy — see TemplateProvider.
builder.Services.AddSingleton<ITemplateProvider, TemplateProvider>();
builder.Services.AddSingleton<TransitionValidator>();
builder.Services.AddSingleton<OrderReader>();
builder.Services.AddSingleton<IPhotoStorage, PhotoStorage>();

// Records notifications but sends none — Azure Notification Hubs is Epic 7, which
// replaces this implementation behind the same interface.
builder.Services.AddSingleton<INotificationService, NotificationService>();

// SOHO: no real client exists yet (the client has not supplied their API). The stub
// is opt-in via SOHO_MODE=stub and never the default — an unconfigured deployment
// must fail customer submissions cleanly rather than mint placeholder references
// into real data (CLAUDE.md §8).
var sohoIsStubbed = string.Equals(builder.Configuration["SOHO_MODE"], "stub", StringComparison.OrdinalIgnoreCase);

if (sohoIsStubbed)
{
    builder.Services.AddSingleton<ISohoClient, StubSohoClient>();
}
else
{
    builder.Services.AddSingleton<ISohoClient, UnconfiguredSohoClient>();
}

builder.Services.AddOpenTelemetry()
    .UseFunctionsWorkerDefaults()
    .UseAzureMonitorExporter();

var app = builder.Build();

// Announce the SOHO mode at startup, so a stubbed deployment says so up front rather
// than being discovered later by someone puzzling over CUS-STUB… order numbers.
// Also exposed on GET /api/health.
//
// Written to stdout rather than through ILogger: this runs before the worker has
// connected to the Functions host, so an ILogger message here has nowhere to go and
// is silently dropped. Worker stdout is captured by both `func start` and the Azure
// log stream.
// ASCII only: non-ASCII characters get mangled by the console/log-stream encoding.
Console.WriteLine(sohoIsStubbed
    ? "[startup] SOHO_MODE=stub - SOHO IS STUBBED. Customer orders receive placeholder (CUS-STUB...) references. Not suitable for real client users."
    : "[startup] SOHO_MODE not set - customer order submission will be rejected with 503 until a real SOHO client is configured. Stock orders are unaffected.");

app.Run();
