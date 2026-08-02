using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace OrderManager.Backend.Lib.Notifications;

public sealed record ExpoPushMessage(string To, string Title, string? Body, object? Data);

/// <summary>Expo's per-message outcome. Errors arrive here, not as an HTTP failure.</summary>
public sealed record ExpoPushReceipt(string PushToken, bool Ok, string? ErrorCode, string? Message)
{
    /// <summary>The device has uninstalled the app or the token was replaced.</summary>
    public bool IsDeviceNotRegistered =>
        string.Equals(ErrorCode, "DeviceNotRegistered", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Credential problems affect every push to that platform, not one device — a
    /// misconfiguration rather than a dead token.
    /// </summary>
    public bool IsCredentialProblem =>
        ErrorCode is "MismatchSenderId" or "InvalidCredentials";
}

public interface IExpoPushClient
{
    Task<IReadOnlyList<ExpoPushReceipt>> SendAsync(
        IReadOnlyList<ExpoPushMessage> messages, CancellationToken cancellationToken = default);
}

/// <summary>
/// Posts to the Expo Push API. Expo brokers on to FCM/APNs, so this backend holds no
/// Firebase or Apple credentials (CLAUDE.md §7).
///
/// ⚠ A 200 from Expo does NOT mean delivered. Per-token failures — a dead token, a
/// credential mismatch — come back inside the response body with `status: "error"`,
/// so the body must be read. Treating the status code as success is the mistake this
/// class exists to prevent.
/// </summary>
public sealed class ExpoPushClient(HttpClient httpClient) : IExpoPushClient
{
    public const string SendUrl = "https://exp.host/--/api/v2/push/send";

    /// <summary>Expo accepts at most 100 messages per request.</summary>
    private const int MaxBatchSize = 100;

    public async Task<IReadOnlyList<ExpoPushReceipt>> SendAsync(
        IReadOnlyList<ExpoPushMessage> messages, CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
        {
            return [];
        }

        var receipts = new List<ExpoPushReceipt>();

        foreach (var batch in Chunk(messages, MaxBatchSize))
        {
            var payload = batch
                .Select(m => new { to = m.To, title = m.Title, body = m.Body, data = m.Data })
                .ToList();

            var response = await httpClient.PostAsJsonAsync(SendUrl, payload, cancellationToken);

            // A non-200 is a whole-batch failure — nothing was delivered.
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    $"Expo push request failed with {(int)response.StatusCode}: {Truncate(detail, 500)}");
            }

            var parsed = await response.Content.ReadFromJsonAsync<ExpoResponse>(cancellationToken);

            for (var i = 0; i < batch.Count; i++)
            {
                var ticket = parsed?.Data is not null && i < parsed.Data.Count ? parsed.Data[i] : null;

                if (ticket is null)
                {
                    receipts.Add(new ExpoPushReceipt(batch[i].To, false, "NoReceipt",
                        "Expo returned no result for this message"));
                    continue;
                }

                // Expo returns results positionally, but on errors it also echoes the
                // token back in details.expoPushToken. Prefer the echo: it is
                // authoritative, whereas index alignment is an assumption — and getting
                // it wrong here would prune a healthy device's token instead of the
                // dead one.
                var token = ticket.Details?.ExpoPushToken ?? batch[i].To;

                var ok = string.Equals(ticket.Status, "ok", StringComparison.OrdinalIgnoreCase);
                receipts.Add(new ExpoPushReceipt(token, ok, ok ? null : ticket.Details?.Error, ticket.Message));
            }
        }

        return receipts;
    }

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
        {
            yield return source.Skip(i).Take(size).ToList();
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private sealed record ExpoResponse
    {
        [JsonPropertyName("data")]
        public List<ExpoTicket>? Data { get; init; }
    }

    private sealed record ExpoTicket
    {
        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("details")]
        public ExpoTicketDetails? Details { get; init; }
    }

    private sealed record ExpoTicketDetails
    {
        [JsonPropertyName("error")]
        public string? Error { get; init; }

        /// <summary>Echoed back by Expo on errors — more reliable than index alignment.</summary>
        [JsonPropertyName("expoPushToken")]
        public string? ExpoPushToken { get; init; }
    }
}
