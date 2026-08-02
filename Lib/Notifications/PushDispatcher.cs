using Microsoft.Extensions.Logging;

namespace OrderManager.Backend.Lib.Notifications;

public sealed record PushDispatchResult(
    int Delivered,
    int Failed,
    int TokensPruned,
    IReadOnlyList<Guid> DeliveredToUserIds);

/// <summary>
/// Sends a notification to every device belonging to a set of users, and prunes tokens
/// Expo reports as dead.
///
/// Separated from <see cref="NotificationService"/> so the send-and-prune decisions are
/// unit-testable without a live Expo endpoint or a database — the parts most likely to
/// be wrong are the ones reading Expo's per-token results, and those deserve tests that
/// do not need infrastructure.
/// </summary>
public sealed class PushDispatcher(
    IExpoPushClient pushClient,
    IDeviceTokenStore tokenStore,
    ILogger<PushDispatcher> logger)
{
    public async Task<PushDispatchResult> DispatchAsync(
        IReadOnlyList<Guid> userIds, NotificationEvent notification)
    {
        var tokens = await tokenStore.GetForUsersAsync(userIds);
        if (tokens.Count == 0)
        {
            logger.LogInformation(
                "'{EventType}' had {UserCount} recipient(s) but no registered devices — nothing to push",
                notification.Type, userIds.Count);
            return new PushDispatchResult(0, 0, 0, []);
        }

        var messages = tokens
            .Select(t => new ExpoPushMessage(
                t.PushToken,
                notification.Title,
                notification.Body,
                // Custom fields travel under `data`; the app reads them from
                // notification.request.content.data (contract §11).
                new
                {
                    type = notification.Type,
                    orderId = notification.OrderId,
                    lineItemId = notification.LineItemId,
                }))
            .ToList();

        var receipts = await pushClient.SendAsync(messages);

        var byToken = tokens.ToDictionary(t => t.PushToken, t => t.UserId, StringComparer.Ordinal);
        var delivered = new List<Guid>();
        var failed = 0;
        var pruned = 0;

        foreach (var receipt in receipts)
        {
            if (receipt.Ok)
            {
                if (byToken.TryGetValue(receipt.PushToken, out var userId))
                {
                    delivered.Add(userId);
                }
                continue;
            }

            failed++;

            if (receipt.IsDeviceNotRegistered)
            {
                // Pruned on the first report rather than after repeated failures: a
                // device that genuinely comes back re-registers on next login, so
                // there is no cost to removing it eagerly, and keeping it means
                // retrying a known-dead token on every future notification.
                await tokenStore.DeleteAsync(receipt.PushToken);
                pruned++;

                logger.LogInformation(
                    "Pruned dead push token for user {UserId} — Expo reported DeviceNotRegistered",
                    byToken.GetValueOrDefault(receipt.PushToken));
                continue;
            }

            if (receipt.IsCredentialProblem)
            {
                // Not one dead device — every push to that platform is failing. Goes to
                // stdout as well, per CLAUDE.md §2: worker ILogger output does not reach
                // the console or Azure log stream under telemetryMode=OpenTelemetry, and
                // this is exactly the failure that otherwise looks like success.
                logger.LogError(
                    "Expo rejected a push with {ErrorCode} — this is a credentials problem affecting ALL pushes to that platform, fix in the mobile repo via EAS. {Message}",
                    receipt.ErrorCode, receipt.Message);

                Console.WriteLine(
                    $"[warning] Expo returned {receipt.ErrorCode}. This affects EVERY push to that platform, " +
                    $"not one device. Credentials are configured in the mobile repo via `eas credentials`. {receipt.Message}");
                continue;
            }

            logger.LogWarning(
                "Push to a device of user {UserId} failed: {ErrorCode} {Message}",
                byToken.GetValueOrDefault(receipt.PushToken), receipt.ErrorCode, receipt.Message);
        }

        return new PushDispatchResult(delivered.Count, failed, pruned, delivered.Distinct().ToList());
    }
}
