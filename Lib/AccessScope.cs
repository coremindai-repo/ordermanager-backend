namespace OrderManager.Backend.Lib;

/// <summary>
/// Who may see records they did not create. Contract §3 is explicit that the server is
/// the source of truth here — the mobile app hiding a tab is a UX convenience, not a
/// security control — so this is applied on every list endpoint.
///
/// Extracted because the same rule was being restated at each call site, which is how
/// two endpoints end up disagreeing about who counts as a supervisor.
/// </summary>
public static class AccessScope
{
    /// <summary>
    /// Roles that see all orders. Per contract §3: factory_supervisor sees all items in
    /// production, store_manager sees all orders, company_manager sees everything
    /// store_manager does. A plain salesperson sees only their own.
    /// </summary>
    private static readonly string[] AllOrdersRoles =
        ["factory_supervisor", "store_manager", "company_manager"];

    /// <summary>
    /// Roles that see all procurement records. Narrower than orders: contract §3 gives
    /// raw-material procurement to store_manager (and company_manager above them),
    /// while a factory_supervisor sees only "raw-material requests they raise".
    /// </summary>
    private static readonly string[] AllProcurementRoles =
        ["store_manager", "company_manager"];

    public static bool CanViewAllOrders(IReadOnlyCollection<string> roles) =>
        HasAny(roles, AllOrdersRoles);

    public static bool CanViewAllProcurement(IReadOnlyCollection<string> roles) =>
        HasAny(roles, AllProcurementRoles);

    /// <summary>
    /// Whether an order listing should be limited to the caller's own records.
    /// A caller who cannot see all orders is always restricted, whatever `mine` says;
    /// one who can may still opt in to their own via `mine=true`.
    /// </summary>
    public static bool RestrictOrdersToOwn(IReadOnlyCollection<string> roles, bool requestedMine) =>
        !CanViewAllOrders(roles) || requestedMine;

    private static bool HasAny(IReadOnlyCollection<string> roles, string[] permitted) =>
        roles.Any(r => permitted.Contains(r, StringComparer.OrdinalIgnoreCase));
}
