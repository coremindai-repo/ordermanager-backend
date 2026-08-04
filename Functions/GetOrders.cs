using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using OrderManager.Backend.Lib;

namespace OrderManager.Backend.Functions;

/// <summary>
/// GET /api/orders — dashboard list (contract §4).
/// Query params: type, status, tab, mine.
/// </summary>
public class GetOrders(ISqlConnectionFactory connectionFactory, JwtService jwtService)
{
    [Function("GetOrders")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "orders")] HttpRequest req)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);

        var type = req.Query["type"].FirstOrDefault();
        var status = req.Query["status"].FirstOrDefault();
        // `tab` is the dashboard's grouping (Ready to Invoice, In Production, …), which
        // maps onto status; treated as a fallback when an explicit status isn't given.
        var tab = req.Query["tab"].FirstOrDefault();
        var effectiveStatus = string.IsNullOrWhiteSpace(status) ? tab : status;

        var requestedMine = string.Equals(req.Query["mine"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
        var restrictToOwn = AccessScope.RestrictOrdersToOwn(caller.Roles, requestedMine);

        if (type is not null && !type.Equals("customer", StringComparison.OrdinalIgnoreCase)
                             && !type.Equals("stock", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "type must be \"customer\" or \"stock\"");
        }

        using var connection = connectionFactory.CreateConnection();

        var rows = await connection.QueryAsync(
            @"SELECT o.id, o.order_number, o.order_type, o.current_status, o.created_at, o.updated_at,
                     s.name AS store_name,
                     u.first_name, u.last_name,
                     (SELECT COUNT(1) FROM order_line_items li WHERE li.order_id = o.id) AS line_item_count
              FROM orders o
              LEFT JOIN stores s ON s.id = o.store_id
              JOIN users u ON u.id = o.created_by
              WHERE (@Type IS NULL OR o.order_type = @Type)
                AND (@Status IS NULL OR o.current_status = @Status)
                AND (@RestrictToOwn = 0 OR o.created_by = @UserId)
                AND o.is_test_data = 0
              ORDER BY o.created_at DESC",
            new
            {
                Type = type?.ToLowerInvariant(),
                Status = effectiveStatus,
                RestrictToOwn = restrictToOwn ? 1 : 0,
                UserId = caller.UserId,
            });

        var orders = rows.Select(o => new
        {
            orderId = (Guid)o.id,
            orderNumber = (string)o.order_number,
            orderType = (string)o.order_type,
            currentStatus = (string)o.current_status,
            storeName = (string?)o.store_name,
            salespersonName = $"{o.first_name} {o.last_name}",
            lineItemCount = (int)o.line_item_count,
            createdAt = TimeFormat.Utc((DateTime)o.created_at),
            updatedAt = TimeFormat.Utc((DateTime)o.updated_at),
        }).ToList();

        return new OkObjectResult(new { orders, count = orders.Count });
    }
}
