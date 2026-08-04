using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using OrderManager.Backend.Lib;

namespace OrderManager.Backend.Functions;

/// <summary>
/// GET /api/order-history (contract §10) — order and line-item status history, scoped
/// by role per §3.
///
/// Reads the append-only history tables that every transition writes to. This is the
/// payoff for keeping them complete: CLAUDE.md §4 warns against thinning them out for
/// convenience, and this endpoint is why.
/// </summary>
public class GetOrderHistory(ISqlConnectionFactory connectionFactory, JwtService jwtService)
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    [Function("GetOrderHistory")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "order-history")] HttpRequest req)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);

        Guid? orderId = null;
        if (req.Query["orderId"].FirstOrDefault() is { } rawOrderId && !string.IsNullOrWhiteSpace(rawOrderId))
        {
            if (!Guid.TryParse(rawOrderId, out var parsed))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "orderId must be a GUID");
            }
            orderId = parsed;
        }

        var from = ParseDate(req.Query["from"].FirstOrDefault(), "from");
        var to = ParseDate(req.Query["to"].FirstOrDefault(), "to");

        if (from is not null && to is not null && from > to)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "from must not be after to");
        }

        var limit = DefaultLimit;
        if (req.Query["limit"].FirstOrDefault() is { } rawLimit && !string.IsNullOrWhiteSpace(rawLimit))
        {
            if (!int.TryParse(rawLimit, out limit) || limit < 1 || limit > MaxLimit)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                    $"limit must be a number between 1 and {MaxLimit}");
            }
        }

        // A salesperson sees history for their own orders only; supervisory roles see
        // all of it (contract §3). Enforced in the query, not by the caller asking.
        var restrictToOwn = !AccessScope.CanViewAllOrders(caller.Roles);

        using var connection = connectionFactory.CreateConnection();

        // Order-level and line-item-level history are unioned so a reader sees one
        // chronological story per order rather than having to stitch two lists together.
        var rows = await connection.QueryAsync(
            @"SELECT TOP (@Limit) * FROM (
                SELECT h.id, 'order' AS scope, h.order_id, o.order_number,
                       CAST(NULL AS UNIQUEIDENTIFIER) AS line_item_id,
                       CAST(NULL AS NVARCHAR(200)) AS item_name,
                       h.from_status, h.to_status, h.notes, h.created_at,
                       u.first_name, u.last_name
                FROM order_status_history h
                JOIN orders o ON o.id = h.order_id
                JOIN users u ON u.id = h.user_id
                WHERE (@OrderId IS NULL OR h.order_id = @OrderId)
                  AND (@RestrictToOwn = 0 OR o.created_by = @UserId)
                  AND o.is_test_data = 0

                UNION ALL

                SELECT h.id, 'lineItem' AS scope, li.order_id, o.order_number,
                       h.line_item_id, li.item_name,
                       h.from_status, h.to_status, h.notes, h.created_at,
                       u.first_name, u.last_name
                FROM line_item_status_history h
                JOIN order_line_items li ON li.id = h.line_item_id
                JOIN orders o ON o.id = li.order_id
                JOIN users u ON u.id = h.user_id
                WHERE (@OrderId IS NULL OR li.order_id = @OrderId)
                  AND (@RestrictToOwn = 0 OR o.created_by = @UserId)
                  AND o.is_test_data = 0
              ) history
              WHERE (@From IS NULL OR created_at >= @From)
                AND (@To IS NULL OR created_at < @To)
              ORDER BY created_at DESC",
            new
            {
                OrderId = orderId,
                RestrictToOwn = restrictToOwn ? 1 : 0,
                UserId = caller.UserId,
                From = from,
                To = to,
                Limit = limit,
            });

        var entries = rows.Select(h => new
        {
            entryId = (Guid)h.id,
            scope = (string)h.scope,
            orderId = (Guid)h.order_id,
            orderNumber = (string)h.order_number,
            lineItemId = (Guid?)h.line_item_id,
            itemName = (string?)h.item_name,
            // Null on the first entry — creation, which has no prior status.
            fromStatus = (string?)h.from_status,
            toStatus = (string)h.to_status,
            notes = (string?)h.notes,
            changedBy = $"{h.first_name} {h.last_name}",
            changedAt = TimeFormat.Utc((DateTime)h.created_at),
        }).ToList();

        return new OkObjectResult(new { entries, count = entries.Count });
    }

    private static DateTime? ParseDate(string? raw, string field)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                $"{field} must be an ISO-8601 date or timestamp");
        }

        return parsed;
    }
}
