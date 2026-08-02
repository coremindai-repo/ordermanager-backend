using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OrderManager.Backend.Lib;
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

builder.Services.AddOpenTelemetry()
    .UseFunctionsWorkerDefaults()
    .UseAzureMonitorExporter();

builder.Build().Run();
