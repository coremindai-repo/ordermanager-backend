namespace OrderManager.Backend.Lib.RawMaterials;

/// <summary>
/// Raw material procurement is a FIXED sub-process, deliberately not templatized —
/// API-INTERFACE-CONTRACT.md §6 states this explicitly, so the chain lives in code
/// rather than in a template row:
///
///   requested → sent_to_supplier → order_placed → order_accepted → received
///
/// Supplier contact happens on WhatsApp or the phone. The app only records the status
/// that resulted, which is why every step is a plain forward move with no side effects
/// beyond the record itself.
///
/// Pure, so the illegal cases are unit-tested rather than assumed.
/// </summary>
public static class RawMaterialStatusFlow
{
    public const string Initial = "requested";

    /// <summary>In order — the sequence is meaningful, unlike a set of statuses.</summary>
    public static readonly string[] Ordered =
        ["requested", "sent_to_supplier", "order_placed", "order_accepted", "received"];

    public const string Terminal = "received";

    public static bool IsKnown(string? status) =>
        status is not null && Ordered.Contains(status, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Forward by exactly one step. No skipping (a request cannot be "received" before
    /// it was ever placed), no going back, and no restating the current status — which
    /// would otherwise let a double tap overwrite timestamps.
    /// </summary>
    public static bool CanTransition(string from, string to)
    {
        var fromIndex = IndexOf(from);
        var toIndex = IndexOf(to);

        if (fromIndex < 0 || toIndex < 0)
        {
            return false;
        }

        return toIndex == fromIndex + 1;
    }

    /// <summary>The only status reachable from here, or null at the end of the chain.</summary>
    public static string? Next(string from)
    {
        var index = IndexOf(from);
        return index >= 0 && index < Ordered.Length - 1 ? Ordered[index + 1] : null;
    }

    public static string Canonical(string status) => Ordered[IndexOf(status)];

    private static int IndexOf(string status) =>
        Array.FindIndex(Ordered, s => string.Equals(s, status, StringComparison.OrdinalIgnoreCase));
}
