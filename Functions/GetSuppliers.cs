using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using OrderManager.Backend.Lib;

namespace OrderManager.Backend.Functions;

/// <summary>
/// GET /api/suppliers — the predefined picker the outsourcing/import screens use.
/// Optional `method` filter (outsource|import) since not every supplier serves both.
///
/// Documented in API-INTERFACE-CONTRACT.md §6 (GET /api/suppliers).
/// </summary>
public class GetSuppliers(ISqlConnectionFactory connectionFactory, JwtService jwtService)
{
    [Function("GetSuppliers")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "suppliers")] HttpRequest req)
    {
        AuthHelper.RequireCaller(req, jwtService);

        var method = req.Query["method"].FirstOrDefault();
        if (method is not null && method is not ("outsource" or "import"))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "method must be \"outsource\" or \"import\"");
        }

        using var connection = connectionFactory.CreateConnection();

        var rows = await connection.QueryAsync(
            @"SELECT id, name, contact, supports_outsource, supports_import
              FROM suppliers
              WHERE active = 1
                AND (@Method IS NULL
                     OR (@Method = 'outsource' AND supports_outsource = 1)
                     OR (@Method = 'import' AND supports_import = 1))
              ORDER BY name",
            new { Method = method });

        var suppliers = rows.Select(s => new
        {
            supplierId = (Guid)s.id,
            name = (string)s.name,
            contact = (string?)s.contact,
            supportsOutsource = (bool)s.supports_outsource,
            supportsImport = (bool)s.supports_import,
        }).ToList();

        return new OkObjectResult(new { suppliers, count = suppliers.Count });
    }
}
