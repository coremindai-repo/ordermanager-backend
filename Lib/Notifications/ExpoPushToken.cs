namespace OrderManager.Backend.Lib.Notifications;

/// <summary>
/// Expo push tokens, the only kind this backend handles. The mobile app ships via
/// Expo, so Expo brokers delivery to FCM/APNs — no Firebase or Apple credentials exist
/// in this repo (CLAUDE.md §7).
///
/// Shape: <c>ExponentPushToken[xxxxxxxxxxxxxxxxxxxxxx]</c>, or the legacy
/// <c>ExpoPushToken[...]</c> prefix Expo also issues.
/// </summary>
public static class ExpoPushToken
{
    private const string ModernPrefix = "ExponentPushToken[";
    private const string LegacyPrefix = "ExpoPushToken[";

    /// <summary>
    /// Checks the token is one Expo will accept. Catching a raw FCM/APNs token here
    /// matters because Expo reports bad tokens *inside* a 200 response rather than as
    /// an HTTP error, so an invalid one would otherwise look like a successful send.
    /// </summary>
    public static bool IsValid(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var value = token.Trim();

        var prefix = value.StartsWith(ModernPrefix, StringComparison.Ordinal) ? ModernPrefix
            : value.StartsWith(LegacyPrefix, StringComparison.Ordinal) ? LegacyPrefix
            : null;

        if (prefix is null || !value.EndsWith(']'))
        {
            return false;
        }

        // Must actually contain something between the brackets.
        return value.Length > prefix.Length + 1;
    }
}
