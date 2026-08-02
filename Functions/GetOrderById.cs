using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using OrderManager.Backend.Lib;
using OrderManager.Backend.Lib.Orders;

namespace OrderManager.Backend.Functions;

/// <summary>GET /api/orders/{orderId} — full order detail (contract §4).</summary>
public class GetOrderById(JwtService jwtService, OrderReader orderReader)
{
    [Function("GetOrderById")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "orders/{orderId}")] HttpRequest req,
        string orderId)
    {
        AuthHelper.RequireCaller(req, jwtService);

        if (!Guid.TryParse(orderId, out var id))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "orderId must be a GUID");
        }

        var order = await orderReader.GetDetailAsync(id);
        if (order is null)
        {
            throw new AppException(StatusCodes.Status404NotFound, "NOT_FOUND", $"Order {id} not found");
        }

        return new OkObjectResult(order);
    }
}
