using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using OrderManager.Backend.Lib;

namespace OrderManager.Backend.Functions;

/// <summary>
/// GET /api/notifications (contract §10) — the caller's own notification log.
///
/// Read from `notifications_log`, which records what the server decided to send
/// regardless of whether the OS push was ever delivered or seen. That is the point:
/// the in-app list stays correct even when a push is lost, and `dispatchedAt` exposes
/// which ones actually reached a device.
/// </summary>
public class GetNotifications(ISqlConnectionFactory connectionFactory, JwtService jwtService)
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    [Function("GetNotifications")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "notifications")] HttpRequest req)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);

        var limit = DefaultLimit;
        if (req.Query["limit"].FirstOrDefault() is { } raw && !string.IsNullOrWhiteSpace(raw))
        {
            if (!int.TryParse(raw, out limit) || limit < 1 || limit > MaxLimit)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                    $"limit must be a number between 1 and {MaxLimit}");
            }
        }

        using var connection = connectionFactory.CreateConnection();

        // Always scoped to the caller — a notification log is personal, and there is no
        // role that grants sight of someone else's.
        var rows = await connection.QueryAsync(
            @"SELECT TOP (@Limit) n.id, n.type, n.title, n.body, n.order_id, n.line_item_id,
                     n.sent_at, n.dispatched_at, o.order_number
              FROM notifications_log n
              LEFT JOIN orders o ON o.id = n.order_id
              WHERE n.user_id = @UserId
              ORDER BY n.sent_at DESC",
            new { UserId = caller.UserId, Limit = limit });

        var notifications = rows.Select(n => new
        {
            notificationId = (Guid)n.id,
            type = (string)n.type,
            title = (string)n.title,
            body = (string?)n.body,
            orderId = (Guid?)n.order_id,
            orderNumber = (string?)n.order_number,
            lineItemId = (Guid?)n.line_item_id,
            sentAt = TimeFormat.Utc((DateTime)n.sent_at),
            // Null when the notification was recorded but never reached a device.
            dispatchedAt = n.dispatched_at is null ? null : TimeFormat.Utc((DateTime)n.dispatched_at),
        }).ToList();

        return new OkObjectResult(new { notifications, count = notifications.Count });
    }
}
