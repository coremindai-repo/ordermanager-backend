namespace OrderManager.Backend.Lib.Inventory;

public sealed record InventoryLocation(string Kind, string Label);

/// <summary>
/// Where a stock item physically is, derived from its order's position in the
/// post-production chain. There is no inventory table — inventory is a read model over
/// stock orders, so location is inferred rather than stored, and there is no second
/// source of truth to drift out of step.
///
/// ⚠ This mapping tracks the process template's post-production statuses. If those
/// change (a new v6 adding or renaming a stage), revisit it — an unmapped status falls
/// back to reporting itself rather than guessing a location, which is visible but not
/// useful.
/// </summary>
public static class InventoryLocationMapper
{
    public const string Factory = "factory";
    public const string Warehouse = "warehouse";
    public const string InTransit = "in_transit";
    public const string Store = "store";
    public const string Unknown = "unknown";

    /// <summary>
    /// Statuses at which the goods have left our hands entirely — no longer inventory.
    /// </summary>
    public static bool HasLeftInventory(string orderStatus) =>
        string.Equals(orderStatus, "DELIVERED", StringComparison.OrdinalIgnoreCase);

    public static InventoryLocation From(string orderStatus, string? storeName)
    {
        var store = string.IsNullOrWhiteSpace(storeName) ? "an unassigned store" : storeName;

        return orderStatus.ToUpperInvariant() switch
        {
            // Still being made, or finished and held at the factory.
            "NEW" or "IN_PRODUCTION" or "KEEP_IN_FACTORY" => new InventoryLocation(Factory, "Factory"),

            "SENT_TO_WAREHOUSE" => new InventoryLocation(Warehouse, "Warehouse"),

            // On the move: the destination is worth showing, since "in transit" alone
            // does not tell a salesperson when it will be reachable.
            "IN_TRANSIT" => new InventoryLocation(InTransit, $"In transit to {store}"),

            // Arrived at, or dispatched from, the destination store. Out for delivery is
            // still ours until it is delivered.
            "SENT_TO_STORE" or "RECEIVED_IN_STORE" or "OUT_FOR_DELIVERY" =>
                new InventoryLocation(Store, store),

            // Not guessed. A status this mapper does not know about is reported as
            // itself, so the gap is visible rather than mislabelled as "Factory".
            _ => new InventoryLocation(Unknown, orderStatus),
        };
    }
}
