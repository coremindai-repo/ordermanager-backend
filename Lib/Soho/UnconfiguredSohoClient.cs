using Microsoft.AspNetCore.Http;

namespace OrderManager.Backend.Lib.Soho;

/// <summary>
/// The default when SOHO_MODE is not set to a known mode. Customer order submission
/// fails with 503 rather than proceeding.
///
/// This is the deliberate safe default: CLAUDE.md §8 requires that a SOHO outage
/// fail cleanly rather than "silently create an order without a valid SOHO
/// reference". Defaulting to the stub instead would mean a misconfigured production
/// deploy quietly minting fake references into real client data.
///
/// Stock orders are unaffected — they never touch SOHO.
/// </summary>
public sealed class UnconfiguredSohoClient : ISohoClient
{
    private const string Message =
        "SOHO integration is not configured. Customer orders cannot be submitted until a SOHO client is wired up (set SOHO_MODE=stub for non-production testing).";

    public Task<string> CreateDraftSalesOrderAsync(SohoDraftOrderRequest request, CancellationToken cancellationToken = default) =>
        throw new AppException(StatusCodes.Status503ServiceUnavailable, "SOHO_UNAVAILABLE", Message);

    public Task CancelDraftSalesOrderAsync(string sohoOrderNumber, CancellationToken cancellationToken = default) =>
        // Nothing can have been created, so there is nothing to compensate.
        Task.CompletedTask;
}
