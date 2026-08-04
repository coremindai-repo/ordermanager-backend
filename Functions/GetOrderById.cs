using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using OrderManager.Backend.Lib;
using OrderManager.Backend.Lib.Orders;

namespace OrderManager.Backend.Functions;

/// <summary>GET /api/orders/{orderId} — full order detail (contract §4).</summary>
public class GetOrderById(
    ISqlConnectionFactory connectionFactory,
    JwtService jwtService,
    OrderReader orderReader)
{
    [Function("GetOrderById")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "orders/{orderId}")] HttpRequest req,
        string orderId)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);

        if (!Guid.TryParse(orderId, out var id))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "orderId must be a GUID");
        }

        using var connection = connectionFactory.CreateConnection();

        // Same rule GetOrders/ListLineItems/GetDashboard apply when listing — reused here
        // rather than reinvented, so "which orders can I see one of" cannot drift from
        // "which orders show up in my list" (AccessScope.CanAccessOrder's own doc comment).
        var createdBy = await connection.ExecuteScalarAsync<Guid?>(
            "SELECT created_by FROM orders WHERE id = @Id", new { Id = id });

        if (createdBy is null)
        {
            throw new AppException(StatusCodes.Status404NotFound, "NOT_FOUND", $"Order {id} not found");
        }

        if (!AccessScope.CanAccessOrder(caller.Roles, caller.UserId, createdBy.Value))
        {
            throw new AppException(StatusCodes.Status403Forbidden, "FORBIDDEN",
                "You may only view orders you raised");
        }

        var order = await orderReader.GetDetailAsync(id);
        if (order is null)
        {
            throw new AppException(StatusCodes.Status404NotFound, "NOT_FOUND", $"Order {id} not found");
        }

        return new OkObjectResult(order);
    }
}
