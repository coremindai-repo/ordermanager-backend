using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using OrderManager.Backend.Lib;
using OrderManager.Backend.Lib.Inventory;

namespace OrderManager.Backend.Functions;

/// <summary>
/// GET /api/inventory?query=&amp;status=finished|semi_finished (contract §9).
/// Available to all authenticated users — no role restriction.
///
/// **Inventory is a read model over stock orders, not a table.** There is no separate
/// inventory store to populate or keep in step: what physically exists is exactly the
/// finished and semi-finished line items of `order_type = 'stock'` orders that have not
/// yet been delivered. Deriving it means the count cannot drift from the orders it
/// describes, and nothing has to be maintained by hand.
///
/// Customer-order items are deliberately excluded — they are committed to a named
/// customer, so showing them as available stock would have staff promising goods that
/// are already sold.
/// </summary>
public class GetInventory(ISqlConnectionFactory connectionFactory, JwtService jwtService)
{
    /// <summary>
    /// The contract's two inventory statuses map onto production statuses. Items still
    /// mid-production (CARPENTRY, POLISHING, …) are work in progress, not stock, so they
    /// do not appear at all.
    /// </summary>
    private static readonly Dictionary<string, string> StatusMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["finished"] = "FINISHED",
        ["semi_finished"] = "SEMI_FINISHED",
    };

    [Function("GetInventory")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "inventory")] HttpRequest req)
    {
        AuthHelper.RequireCaller(req, jwtService);

        var query = req.Query["query"].FirstOrDefault();
        var status = req.Query["status"].FirstOrDefault();

        if (status is not null && !StatusMap.ContainsKey(status))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                $"status must be one of: {string.Join(", ", StatusMap.Keys)}");
        }

        using var connection = connectionFactory.CreateConnection();

        var rows = await connection.QueryAsync(
            @"SELECT li.id, li.item_name, li.current_status AS item_status, li.method,
                     o.id AS order_id, o.order_number, o.current_status AS order_status,
                     s.name AS store_name, li.updated_at
              FROM order_line_items li
              JOIN orders o ON o.id = li.order_id
              LEFT JOIN stores s ON s.id = o.store_id
              WHERE o.order_type = 'stock'
                AND li.current_status IN ('FINISHED', 'SEMI_FINISHED')
                AND o.current_status <> 'DELIVERED'
                AND (@ItemStatus IS NULL OR li.current_status = @ItemStatus)
                AND (@Query IS NULL OR li.item_name LIKE @Pattern)
              ORDER BY li.item_name",
            new
            {
                ItemStatus = status is null ? null : StatusMap[status],
                Query = string.IsNullOrWhiteSpace(query) ? null : query,
                // Escaped so a user typing % or _ searches for those characters rather
                // than matching everything.
                Pattern = string.IsNullOrWhiteSpace(query)
                    ? null
                    : "%" + query.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%",
            });

        var items = rows.Select(r =>
        {
            var location = InventoryLocationMapper.From((string)r.order_status, (string?)r.store_name);

            return new
            {
                lineItemId = (Guid)r.id,
                productName = (string)r.item_name,
                // Reported in the contract's vocabulary, not the template's.
                status = string.Equals((string)r.item_status, "FINISHED", StringComparison.OrdinalIgnoreCase)
                    ? "finished"
                    : "semi_finished",
                location = location.Label,
                locationKind = location.Kind,
                method = (string?)r.method,
                // Kept so a salesperson can trace a physical item back to its order.
                orderId = (Guid)r.order_id,
                orderNumber = (string)r.order_number,
                orderStatus = (string)r.order_status,
                updatedAt = TimeFormat.Utc((DateTime)r.updated_at),
            };
        }).ToList();

        return new OkObjectResult(new { items, count = items.Count });
    }
}
