using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OrderManager.Backend.Lib.Notifications;

/// <summary>An event worth telling someone about (API-INTERFACE-CONTRACT.md §11).</summary>
public sealed record NotificationEvent(
    string Type,
    string Title,
    string? Body,
    Guid? OrderId = null,
    Guid? LineItemId = null);

public interface INotificationService
{
    /// <summary>
    /// Resolves who should hear about this event and records it. Returns how many
    /// recipients were logged.
    /// </summary>
    Task<int> NotifyAsync(NotificationEvent notification);
}

/// <summary>
/// Resolves recipients from notification_recipients, records the notification, and
/// pushes it via Expo.
///
/// `notifications_log.dispatched_at` is stamped only for users a push actually reached,
/// so the log distinguishes "we decided to notify" from "we got it to a device". Rows
/// are written whether or not delivery succeeds (CLAUDE.md §7) — the in-app
/// notification list reads from here regardless of whether the OS push was seen.
///
/// Delivery is not fatal: a failure here must never roll back the status change that
/// triggered it — the user's refresh button is the fallback.
/// </summary>
public sealed class NotificationService(
    ISqlConnectionFactory connectionFactory,
    IConfiguration configuration,
    PushDispatcher dispatcher,
    ILogger<NotificationService> logger) : INotificationService
{
    private readonly Guid _clientId = Guid.Parse(
        configuration["CLIENT_ID"] ?? throw new InvalidOperationException("CLIENT_ID is not configured"));

    public async Task<int> NotifyAsync(NotificationEvent notification)
    {
        using var connection = connectionFactory.CreateConnection();

        // A recipient row names either a role (everyone holding it) or one user.
        var recipients = (await connection.QueryAsync<Guid>(
            @"SELECT DISTINCT u.id
              FROM notification_recipients nr
              LEFT JOIN user_roles ur ON ur.role = nr.recipient_role
              JOIN users u ON u.id = COALESCE(nr.recipient_user_id, ur.user_id)
              WHERE nr.client_id = @ClientId
                AND nr.event_type = @EventType
                AND nr.active = 1
                AND u.active = 1",
            new { ClientId = _clientId, EventType = notification.Type })).ToList();

        if (recipients.Count == 0)
        {
            // Configurable routing means it can be configured to nothing. Say so
            // loudly — a handoff that silently reaches nobody is worse than a failure,
            // because everything downstream still looks like it worked.
            logger.LogWarning(
                "Notification '{EventType}' for order {OrderId} resolved to NO recipients — check notification_recipients for client {ClientId}",
                notification.Type, notification.OrderId, _clientId);

            // Also to stdout: this host runs with telemetryMode=OpenTelemetry, under
            // which worker ILogger output goes to Azure Monitor and never appears in
            // the console or the Azure log stream. That is fine for routine logs, but
            // this one means a handoff reached nobody — the exact failure that looks
            // like success from every other angle — so it gets a channel you cannot
            // miss while developing.
            Console.WriteLine(
                $"[warning] Notification '{notification.Type}' resolved to NO recipients. " +
                $"Nobody was told. Check notification_recipients for client {_clientId}.");

            return 0;
        }

        // Recorded before dispatch, so a push failure still leaves the notification in
        // the in-app list rather than losing it.
        foreach (var userId in recipients)
        {
            await connection.ExecuteAsync(
                @"INSERT INTO notifications_log (user_id, type, order_id, line_item_id, title, body, dispatched_at)
                  VALUES (@UserId, @Type, @OrderId, @LineItemId, @Title, @Body, NULL)",
                new
                {
                    UserId = userId,
                    notification.Type,
                    notification.OrderId,
                    notification.LineItemId,
                    notification.Title,
                    notification.Body,
                });
        }

        PushDispatchResult result;
        try
        {
            result = await dispatcher.DispatchAsync(recipients, notification);
        }
        catch (Exception ex)
        {
            // Expo unreachable, or a whole-batch rejection. The rows above stand with
            // dispatched_at NULL, which is exactly what that column is for.
            logger.LogError(ex,
                "Push dispatch for '{EventType}' failed entirely; {Count} notification(s) recorded but undelivered",
                notification.Type, recipients.Count);
            return recipients.Count;
        }

        if (result.DeliveredToUserIds.Count > 0)
        {
            await connection.ExecuteAsync(
                @"UPDATE notifications_log SET dispatched_at = SYSUTCDATETIME()
                  WHERE user_id IN @UserIds AND type = @Type AND dispatched_at IS NULL",
                new { UserIds = result.DeliveredToUserIds, notification.Type });
        }

        logger.LogInformation(
            "'{EventType}': {Recipients} recipient(s), {Delivered} delivered, {Failed} failed, {Pruned} dead token(s) pruned",
            notification.Type, recipients.Count, result.Delivered, result.Failed, result.TokensPruned);

        return recipients.Count;
    }
}
