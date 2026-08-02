namespace OrderManager.Backend.Lib.Soho;

public sealed record SohoLineItem(string ItemName, string? Description);

public sealed record SohoDraftOrderRequest(
    Guid LocalOrderId,
    Guid? StoreId,
    IReadOnlyList<SohoLineItem> LineItems);

/// <summary>
/// SOHO is the client's external sales system. For customer orders it issues the
/// Sales Order number that becomes the app's order number (contract §7).
///
/// ⚠ NO REAL IMPLEMENTATION EXISTS YET. The client has not provided their API, so
/// the only implementations today are <see cref="StubSohoClient"/> (placeholder
/// numbers, dev/test only) and <see cref="UnconfiguredSohoClient"/> (fails cleanly).
/// When the real API arrives, add a live implementation of this interface and
/// register it — nothing outside this folder should need to change.
/// </summary>
public interface ISohoClient
{
    /// <summary>Creates a draft sales order and returns its SOHO order number.</summary>
    Task<string> CreateDraftSalesOrderAsync(SohoDraftOrderRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compensating action: voids a draft created by
    /// <see cref="CreateDraftSalesOrderAsync"/> when the local write then fails, so a
    /// failed submission does not leave an orphan draft in SOHO (CLAUDE.md §3).
    /// </summary>
    Task CancelDraftSalesOrderAsync(string sohoOrderNumber, CancellationToken cancellationToken = default);
}
