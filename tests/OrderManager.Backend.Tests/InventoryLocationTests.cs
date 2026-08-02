using OrderManager.Backend.Lib.Inventory;

namespace OrderManager.Backend.Tests;

/// <summary>
/// Inventory is a read model over stock orders, so an item's location is derived from
/// its order's position in the post-production chain rather than stored. This mapping
/// is the derivation.
/// </summary>
public class InventoryLocationTests
{
    private const string Kochi = "Kochi";

    [Theory]
    [InlineData("NEW")]
    [InlineData("IN_PRODUCTION")]
    [InlineData("KEEP_IN_FACTORY")]
    public void GoodsNotYetDispatchedAreAtTheFactory(string orderStatus)
    {
        var location = InventoryLocationMapper.From(orderStatus, Kochi);

        Assert.Equal(InventoryLocationMapper.Factory, location.Kind);
        Assert.Equal("Factory", location.Label);
    }

    [Fact]
    public void WarehousedGoodsReportTheWarehouse()
    {
        var location = InventoryLocationMapper.From("SENT_TO_WAREHOUSE", Kochi);

        Assert.Equal(InventoryLocationMapper.Warehouse, location.Kind);
    }

    [Fact]
    public void GoodsInTransitNameTheirDestination()
    {
        // "In transit" alone does not tell a salesperson where it will turn up.
        var location = InventoryLocationMapper.From("IN_TRANSIT", Kochi);

        Assert.Equal(InventoryLocationMapper.InTransit, location.Kind);
        Assert.Contains(Kochi, location.Label);
    }

    [Theory]
    [InlineData("SENT_TO_STORE")]
    [InlineData("RECEIVED_IN_STORE")]
    [InlineData("OUT_FOR_DELIVERY")]
    public void GoodsAtOrLeavingAStoreReportThatStore(string orderStatus)
    {
        var location = InventoryLocationMapper.From(orderStatus, Kochi);

        Assert.Equal(InventoryLocationMapper.Store, location.Kind);
        Assert.Equal(Kochi, location.Label);
    }

    [Fact]
    public void OutForDeliveryIsStillInventory()
    {
        // On a van but not yet handed over — still ours.
        Assert.False(InventoryLocationMapper.HasLeftInventory("OUT_FOR_DELIVERY"));
    }

    [Fact]
    public void DeliveredGoodsHaveLeftInventory()
    {
        Assert.True(InventoryLocationMapper.HasLeftInventory("DELIVERED"));
    }

    [Fact]
    public void HasLeftInventoryIsCaseInsensitive()
    {
        Assert.True(InventoryLocationMapper.HasLeftInventory("delivered"));
    }

    // ---------- Degrading honestly ----------

    [Fact]
    public void AnUnmappedStatusReportsItselfRatherThanGuessing()
    {
        // If a future template adds a stage, the gap must be visible — mislabelling it
        // "Factory" would send someone to the wrong building.
        var location = InventoryLocationMapper.From("AWAITING_CUSTOMS", Kochi);

        Assert.Equal(InventoryLocationMapper.Unknown, location.Kind);
        Assert.Equal("AWAITING_CUSTOMS", location.Label);
    }

    [Fact]
    public void AMissingStoreNameIsStatedRatherThanLeftBlank()
    {
        // store_id is nullable, so an order can reach transit without a destination set.
        var location = InventoryLocationMapper.From("IN_TRANSIT", null);

        Assert.Contains("unassigned", location.Label);
    }

    [Fact]
    public void StatusMatchingIsCaseInsensitive()
    {
        Assert.Equal(InventoryLocationMapper.Factory, InventoryLocationMapper.From("keep_in_factory", Kochi).Kind);
    }
}
