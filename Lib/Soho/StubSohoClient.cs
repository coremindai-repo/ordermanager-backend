using Microsoft.Extensions.Logging;

namespace OrderManager.Backend.Lib.Soho;

/// <summary>
/// ⚠ STUB — NOT A REAL INTEGRATION. Returns invented sales order numbers so that
/// POST /api/orders and everything downstream can be built and tested before the
/// client provides their SOHO API.
///
/// Only used when SOHO_MODE=stub is set explicitly; see <see cref="UnconfiguredSohoClient"/>
/// for what happens otherwise.
///
/// Placeholder numbers are deliberately prefixed "STUB" so stub-issued orders are
/// obvious in the database and in the UI (they surface as e.g. CUS-STUB471203).
/// A realistic-looking fake is exactly the thing that gets mistaken for a real SOHO
/// reference months later, so this one refuses to look real.
/// </summary>
public sealed class StubSohoClient(ILogger<StubSohoClient> logger) : ISohoClient
{
    public Task<string> CreateDraftSalesOrderAsync(SohoDraftOrderRequest request, CancellationToken cancellationToken = default)
    {
        var placeholder = $"STUB{Random.Shared.Next(100000, 999999)}";

        logger.LogWarning(
            "SOHO STUB issued placeholder sales order number {Placeholder} for local order {LocalOrderId} — this is NOT a real SOHO reference",
            placeholder, request.LocalOrderId);

        return Task.FromResult(placeholder);
    }

    public Task CancelDraftSalesOrderAsync(string sohoOrderNumber, CancellationToken cancellationToken = default)
    {
        // Nothing to undo — no draft was ever created anywhere. Logged so the
        // compensation path is visibly exercised in local testing.
        logger.LogWarning("SOHO STUB would cancel draft sales order {SohoOrderNumber}", sohoOrderNumber);
        return Task.CompletedTask;
    }
}
