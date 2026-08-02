using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using OrderManager.Backend.Lib;

namespace OrderManager.Backend.Functions;

/// <summary>
/// GET /api/stores — active stores for post-production routing pickers (contract §8).
/// Adding a store is a row insert, not a deploy.
/// </summary>
public class GetStores(ISqlConnectionFactory connectionFactory, JwtService jwtService)
{
    [Function("GetStores")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "stores")] HttpRequest req)
    {
        AuthHelper.RequireCaller(req, jwtService);

        using var connection = connectionFactory.CreateConnection();

        var rows = await connection.QueryAsync(
            "SELECT id, name, location FROM stores WHERE active = 1 ORDER BY name");

        var stores = rows.Select(s => new
        {
            storeId = (Guid)s.id,
            name = (string)s.name,
            location = (string?)s.location,
        }).ToList();

        return new OkObjectResult(new { stores });
    }
}
