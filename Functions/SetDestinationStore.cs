using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using OrderManager.Backend.Lib;

namespace OrderManager.Backend.Functions;

/// <summary>
/// POST /api/orders/{orderId}/destination-store — post-production routing.
///
/// Sets the order's destination store, the action behind the picker that
/// GET /api/stores feeds (contract §8). Routing is modelled at order level because
/// that is where the data model puts it (`orders.store_id`, CLAUDE.md §4); line items
/// carry no store of their own.
///
/// Documented in API-INTERFACE-CONTRACT.md §8 (POST /api/orders/{orderId}/destination-store)
/// — confirmed order-level, not per line item: "an order's line items ship together."
///
/// factory_supervisor only: this sets the prerequisite for the `-> IN_TRANSIT` dispatch
/// transition, which the process template already gates to factory_supervisor (CLAUDE.md
/// §5) — the same role decides where dispatched goods go as performs the factory-floor
/// movements around it.
/// </summary>
public class SetDestinationStore(ISqlConnectionFactory connectionFactory, JwtService jwtService)
{
    public record DestinationStoreRequest(Guid? StoreId);

    [Function("SetDestinationStore")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders/{orderId}/destination-store")] HttpRequest req,
        string orderId)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);
        AuthHelper.RequireRole(caller, "factory_supervisor");

        if (!Guid.TryParse(orderId, out var id))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "orderId must be a GUID");
        }

        var body = await req.ReadFromJsonAsync<DestinationStoreRequest>();
        if (body?.StoreId is null)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "storeId is required");
        }

        using var connection = connectionFactory.CreateConnection();

        var storeName = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT name FROM stores WHERE id = @Id AND active = 1", new { Id = body.StoreId });

        if (storeName is null)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                $"Store {body.StoreId} does not exist or is inactive");
        }

        var updatedAt = await connection.QuerySingleOrDefaultAsync<DateTime?>(
            @"UPDATE orders SET store_id = @StoreId, updated_at = SYSUTCDATETIME()
              OUTPUT inserted.updated_at
              WHERE id = @Id",
            new { Id = id, StoreId = body.StoreId });

        if (updatedAt is null)
        {
            throw new AppException(StatusCodes.Status404NotFound, "NOT_FOUND", $"Order {id} not found");
        }

        return new OkObjectResult(new
        {
            orderId = id,
            store = new { storeId = body.StoreId, name = storeName },
            updatedAt = TimeFormat.Utc(updatedAt.Value),
        });
    }
}
