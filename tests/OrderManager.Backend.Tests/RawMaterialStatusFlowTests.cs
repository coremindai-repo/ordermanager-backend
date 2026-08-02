using OrderManager.Backend.Lib.RawMaterials;

namespace OrderManager.Backend.Tests;

/// <summary>
/// Raw material procurement is a fixed sub-process (contract §6) — deliberately not
/// templatized, so unlike order statuses this chain is guaranteed by code and these
/// tests are the only thing protecting it.
/// </summary>
public class RawMaterialStatusFlowTests
{
    [Fact]
    public void TheChainIsTheOneTheContractSpecifies()
    {
        Assert.Equal(
            ["requested", "sent_to_supplier", "order_placed", "order_accepted", "received"],
            RawMaterialStatusFlow.Ordered);
    }

    [Fact]
    public void NewRequestsStartAsRequested()
    {
        Assert.Equal("requested", RawMaterialStatusFlow.Initial);
    }

    // ---------- Forward, one step at a time ----------

    [Theory]
    [InlineData("requested", "sent_to_supplier")]
    [InlineData("sent_to_supplier", "order_placed")]
    [InlineData("order_placed", "order_accepted")]
    [InlineData("order_accepted", "received")]
    public void AllowsEachStepInSequence(string from, string to)
    {
        Assert.True(RawMaterialStatusFlow.CanTransition(from, to));
    }

    [Fact]
    public void WalksTheWholeChain()
    {
        var current = RawMaterialStatusFlow.Initial;

        while (RawMaterialStatusFlow.Next(current) is { } next)
        {
            Assert.True(RawMaterialStatusFlow.CanTransition(current, next));
            current = next;
        }

        Assert.Equal(RawMaterialStatusFlow.Terminal, current);
    }

    // ---------- Skipping ----------

    [Theory]
    [InlineData("requested", "order_placed")]
    [InlineData("requested", "received")]
    [InlineData("sent_to_supplier", "order_accepted")]
    [InlineData("order_placed", "received")]
    public void RejectsSkippingAhead(string from, string to)
    {
        // Materials cannot be "received" from a request that was never placed.
        Assert.False(RawMaterialStatusFlow.CanTransition(from, to));
    }

    // ---------- Backwards and standing still ----------

    [Theory]
    [InlineData("received", "order_accepted")]
    [InlineData("order_accepted", "order_placed")]
    [InlineData("sent_to_supplier", "requested")]
    public void RejectsGoingBackwards(string from, string to)
    {
        Assert.False(RawMaterialStatusFlow.CanTransition(from, to));
    }

    [Theory]
    [InlineData("requested")]
    [InlineData("order_placed")]
    [InlineData("received")]
    public void RejectsRestatingTheCurrentStatus(string status)
    {
        // Guards against a double tap overwriting timestamps.
        Assert.False(RawMaterialStatusFlow.CanTransition(status, status));
    }

    [Fact]
    public void NothingFollowsReceived()
    {
        Assert.Null(RawMaterialStatusFlow.Next("received"));

        foreach (var status in RawMaterialStatusFlow.Ordered)
        {
            Assert.False(RawMaterialStatusFlow.CanTransition("received", status));
        }
    }

    // ---------- Unknown input ----------

    [Theory]
    [InlineData("cancelled")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsUnknownStatuses(string? status)
    {
        Assert.False(RawMaterialStatusFlow.IsKnown(status));
        Assert.False(RawMaterialStatusFlow.CanTransition("requested", status ?? ""));
    }

    [Fact]
    public void RejectsAnUnknownSourceStatus()
    {
        Assert.False(RawMaterialStatusFlow.CanTransition("cancelled", "received"));
    }

    // ---------- Case handling ----------

    [Fact]
    public void MatchesStatusesCaseInsensitively()
    {
        Assert.True(RawMaterialStatusFlow.IsKnown("REQUESTED"));
        Assert.True(RawMaterialStatusFlow.CanTransition("Requested", "SENT_TO_SUPPLIER"));
    }

    [Fact]
    public void CanonicalisesToTheStoredCasing()
    {
        // Whatever the client sends, the stored value is the canonical lower-case form.
        Assert.Equal("sent_to_supplier", RawMaterialStatusFlow.Canonical("SENT_TO_SUPPLIER"));
        Assert.Equal("received", RawMaterialStatusFlow.Canonical("Received"));
    }

    [Fact]
    public void NextIsCaseInsensitive()
    {
        Assert.Equal("order_placed", RawMaterialStatusFlow.Next("SENT_TO_SUPPLIER"));
    }
}
