using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;
using OrderManager.Backend.Lib;

namespace OrderManager.Backend.Middleware;

public class ExceptionHandlingMiddleware : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var logger = context.GetLogger<ExceptionHandlingMiddleware>();
            logger.LogError(ex, "Unhandled exception in {FunctionName}", context.FunctionDefinition.Name);

            var httpContext = context.GetHttpContext();
            if (httpContext is null)
            {
                throw;
            }

            var (statusCode, code, message) = ex switch
            {
                AppException appEx => (appEx.StatusCode, appEx.Code, appEx.Message),
                _ => (StatusCodes.Status500InternalServerError, "INTERNAL_ERROR", "An unexpected error occurred"),
            };

            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = "application/json";
            await httpContext.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                error = new { code, message },
            }));
        }
    }
}
