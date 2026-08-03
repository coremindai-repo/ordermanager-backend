using OrderManager.Backend.Lib;

namespace OrderManager.Backend.Tests;

/// <summary>
/// Who may see records they did not create. Contract §3 is explicit that the server
/// enforces this and the app hiding a tab is not a security control — so a mistake here
/// leaks other people's orders rather than merely showing a wrong screen.
///
/// Extracted from the endpoints in Epic 8; before that the rule was restated at each
/// call site, which is how two endpoints end up disagreeing about who is a supervisor.
/// </summary>
public class AccessScopeTests
{
    // ---------- Orders ----------

    [Theory]
    [InlineData("factory_supervisor")]
    [InlineData("store_manager")]
    [InlineData("company_manager")]
    public void SupervisoryRolesSeeAllOrders(string role)
    {
        Assert.True(AccessScope.CanViewAllOrders([role]));
    }

    [Fact]
    public void APlainSalespersonSeesOnlyTheirOwnOrders()
    {
        Assert.False(AccessScope.CanViewAllOrders(["salesperson"]));
    }

    [Fact]
    public void ASalespersonWhoAlsoHoldsASupervisoryRoleSeesAll()
    {
        // Contract §3: "default behavior for salesperson unless they hold a
        // supervisory role too".
        Assert.True(AccessScope.CanViewAllOrders(["salesperson", "store_manager"]));
    }

    [Fact]
    public void ACallerWithNoRolesSeesOnlyTheirOwn()
    {
        // Fail closed: absence of a role must never mean absence of a restriction.
        Assert.False(AccessScope.CanViewAllOrders([]));
    }

    [Fact]
    public void AnUnrecognisedRoleGrantsNothing()
    {
        Assert.False(AccessScope.CanViewAllOrders(["auditor"]));
    }

    // ---------- Procurement is narrower than orders ----------

    [Theory]
    [InlineData("store_manager")]
    [InlineData("company_manager")]
    public void ProcurementRolesSeeAllRequests(string role)
    {
        Assert.True(AccessScope.CanViewAllProcurement([role]));
    }

    [Fact]
    public void AFactorySupervisorSeesAllOrdersButOnlyTheirOwnRequests()
    {
        // The one asymmetry worth pinning down: §3 gives factory_supervisor all items
        // in production, but only "raw-material requests they raise".
        Assert.True(AccessScope.CanViewAllOrders(["factory_supervisor"]));
        Assert.False(AccessScope.CanViewAllProcurement(["factory_supervisor"]));
    }

    [Fact]
    public void ASalespersonSeesNeither()
    {
        Assert.False(AccessScope.CanViewAllOrders(["salesperson"]));
        Assert.False(AccessScope.CanViewAllProcurement(["salesperson"]));
    }

    // ---------- The `mine` flag ----------

    [Fact]
    public void MineTrueRestrictsEvenASupervisor()
    {
        // Opt-in narrowing: a manager asking for "my orders" gets their own.
        Assert.True(AccessScope.RestrictOrdersToOwn(["company_manager"], requestedMine: true));
    }

    [Fact]
    public void MineFalseDoesNotWidenASalesperson()
    {
        // The important direction: a caller cannot opt OUT of their restriction by
        // omitting `mine`.
        Assert.True(AccessScope.RestrictOrdersToOwn(["salesperson"], requestedMine: false));
    }

    [Fact]
    public void ASupervisorNotAskingForMineSeesEverything()
    {
        Assert.False(AccessScope.RestrictOrdersToOwn(["store_manager"], requestedMine: false));
    }

    // ---------- Raw material requests: standalone vs tied to a line item ----------
    //
    // A request raised against a line item describes that ITEM, so it follows the item
    // rather than its raiser: a second supervisor picking the item up must be able to see
    // materials are already on order instead of duplicating the request. A standalone
    // stock-level request keeps the original raiser-scoped rule untouched.

    private static readonly Guid Caller = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SomeoneElse = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData("store_manager")]
    [InlineData("company_manager")]
    public void ProcurementSeesEveryRequestLinkedOrNot(string role)
    {
        Assert.True(AccessScope.CanViewRawMaterialRequest(
            [role], Caller, requestedBy: SomeoneElse, linkedOrderCreatedBy: null));
        Assert.True(AccessScope.CanViewRawMaterialRequest(
            [role], Caller, requestedBy: SomeoneElse, linkedOrderCreatedBy: SomeoneElse));
    }

    [Fact]
    public void ARaiserAlwaysSeesTheirOwnRequest()
    {
        Assert.True(AccessScope.CanViewRawMaterialRequest(
            ["factory_supervisor"], Caller, requestedBy: Caller, linkedOrderCreatedBy: null));
    }

    [Fact]
    public void AStandaloneRequestStaysWithItsRaiser()
    {
        // The pre-existing rule, pinned so the new item-linked branch cannot widen it:
        // one supervisor still cannot see another's stock-level request.
        Assert.False(AccessScope.CanViewRawMaterialRequest(
            ["factory_supervisor"], Caller, requestedBy: SomeoneElse, linkedOrderCreatedBy: null));
    }

    [Fact]
    public void AnItemLinkedRequestIsVisibleToAnyoneWhoCanSeeTheItem()
    {
        // The change: a supervisor sees all orders, so they see a colleague's request
        // once it names a line item — even though the identical standalone request above
        // is hidden from them.
        Assert.True(AccessScope.CanViewRawMaterialRequest(
            ["factory_supervisor"], Caller, requestedBy: SomeoneElse, linkedOrderCreatedBy: SomeoneElse));
    }

    [Fact]
    public void ASalespersonSeesALinkedRequestOnlyOnTheirOwnOrder()
    {
        Assert.True(AccessScope.CanViewRawMaterialRequest(
            ["salesperson"], Caller, requestedBy: SomeoneElse, linkedOrderCreatedBy: Caller));

        Assert.False(AccessScope.CanViewRawMaterialRequest(
            ["salesperson"], Caller, requestedBy: SomeoneElse, linkedOrderCreatedBy: SomeoneElse));
    }

    [Fact]
    public void ARaiserKeepsVisibilityEvenWhenLinkedToAnOrderTheyCannotSee()
    {
        // Linking must only ever widen visibility. Whoever raised a request can always
        // still see it, whatever order it was attached to.
        Assert.True(AccessScope.CanViewRawMaterialRequest(
            ["salesperson"], Caller, requestedBy: Caller, linkedOrderCreatedBy: SomeoneElse));
    }

    [Fact]
    public void ACallerWithNoRolesSeesOnlyRequestsTheyRaised()
    {
        // Fail closed. A null link must not become a wildcard.
        Assert.False(AccessScope.CanViewRawMaterialRequest(
            [], Caller, requestedBy: SomeoneElse, linkedOrderCreatedBy: null));
        Assert.False(AccessScope.CanViewRawMaterialRequest(
            [], Caller, requestedBy: SomeoneElse, linkedOrderCreatedBy: SomeoneElse));
    }

    // ---------- Case handling ----------

    [Fact]
    public void RolesAreMatchedCaseInsensitively()
    {
        // Roles come from a JWT claim; a casing difference must not silently widen or
        // narrow what someone can see.
        Assert.True(AccessScope.CanViewAllOrders(["Store_Manager"]));
        Assert.True(AccessScope.CanViewAllProcurement(["COMPANY_MANAGER"]));
    }
}
