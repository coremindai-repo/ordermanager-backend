using OrderManager.Backend.Lib.Outsourcing;

namespace OrderManager.Backend.Tests;

/// <summary>
/// Outsourcing/import requests are a fixed sub-process (contract §6), hard-coded like
/// raw materials — so these tests are the only thing protecting the chain. Unlike raw
/// materials it BRANCHES at the end, and the branch decides what happens to the linked
/// line items, so the branch cases carry the most weight.
/// </summary>
public class OutsourcingStatusFlowTests
{
    [Fact]
    public void TheChainIsTheOneTheContractSpecifies()
    {
        Assert.Equal(
            ["placed", "accepted", "received_finished", "received_semi_finished"],
            OutsourcingStatusFlow.All);
    }

    [Fact]
    public void NewRequestsStartAsPlaced()
    {
        Assert.Equal("placed", OutsourcingStatusFlow.Initial);
    }

    // ---------- Forward ----------

    [Fact]
    public void PlacedLeadsOnlyToAccepted()
    {
        Assert.Equal(["accepted"], OutsourcingStatusFlow.NextOptions("placed"));
        Assert.True(OutsourcingStatusFlow.CanTransition("placed", "accepted"));
    }

    [Fact]
    public void AcceptedBranchesToEitherReceiptState_ForOutsourcing()
    {
        var options = OutsourcingStatusFlow.NextOptions("accepted", "outsource");

        Assert.Equal(2, options.Count);
        Assert.Contains("received_finished", options);
        Assert.Contains("received_semi_finished", options);
    }

    // ---------- Semi-finished is outsourcing-only ----------

    [Fact]
    public void ImportsCannotBeReceivedSemiFinished()
    {
        // An outsourcing supplier may do only part of the job; an import always arrives
        // complete. Allowing it would put an imported item into SEMI_FINISHED, which
        // the production template gives it no way out of.
        Assert.False(OutsourcingStatusFlow.CanTransition("accepted", "received_semi_finished", "import"));
    }

    [Fact]
    public void ImportsCanStillBeReceivedFinished()
    {
        Assert.True(OutsourcingStatusFlow.CanTransition("accepted", "received_finished", "import"));
    }

    [Fact]
    public void AnImportRequestNeverOffersTheSemiFinishedOption()
    {
        var options = OutsourcingStatusFlow.NextOptions("accepted", "import");

        Assert.Equal(["received_finished"], options);
    }

    [Theory]
    [InlineData("outsource", true)]
    [InlineData("Outsource", true)]
    [InlineData("import", false)]
    [InlineData("IMPORT", false)]
    [InlineData("factory", false)]
    public void OnlyOutsourcingCanReturnSemiFinished(string method, bool expected)
    {
        Assert.Equal(expected, OutsourcingStatusFlow.CanReturnSemiFinished(method));
    }

    [Fact]
    public void AnUnstatedMethodIsTreatedPermissively()
    {
        // Null means "not stated", not "import" — callers that genuinely do not know
        // the method should not be silently narrowed.
        Assert.True(OutsourcingStatusFlow.CanReturnSemiFinished(null));
        Assert.True(OutsourcingStatusFlow.CanTransition("accepted", "received_semi_finished"));
    }

    [Theory]
    [InlineData("received_finished")]
    [InlineData("received_semi_finished")]
    public void AcceptedCanReachEitherReceipt(string receipt)
    {
        Assert.True(OutsourcingStatusFlow.CanTransition("accepted", receipt));
    }

    // ---------- Skipping ----------

    [Theory]
    [InlineData("received_finished")]
    [InlineData("received_semi_finished")]
    public void RejectsReceivingWithoutAcceptance(string receipt)
    {
        // Goods cannot arrive from a request the supplier never accepted.
        Assert.False(OutsourcingStatusFlow.CanTransition("placed", receipt));
    }

    // ---------- Terminal ----------

    [Theory]
    [InlineData("received_finished")]
    [InlineData("received_semi_finished")]
    public void BothReceiptStatesAreTerminal(string receipt)
    {
        Assert.True(OutsourcingStatusFlow.IsTerminal(receipt));
        Assert.Empty(OutsourcingStatusFlow.NextOptions(receipt));

        foreach (var status in OutsourcingStatusFlow.All)
        {
            Assert.False(OutsourcingStatusFlow.CanTransition(receipt, status));
        }
    }

    [Fact]
    public void TheOneReceiptStateCannotBecomeTheOther()
    {
        // A semi-finished receipt is not upgraded to finished by re-reporting it — the
        // item finishes through the factory steps instead.
        Assert.False(OutsourcingStatusFlow.CanTransition("received_semi_finished", "received_finished"));
        Assert.False(OutsourcingStatusFlow.CanTransition("received_finished", "received_semi_finished"));
    }

    [Theory]
    [InlineData("placed")]
    [InlineData("accepted")]
    public void InFlightStatusesAreNotTerminal(string status)
    {
        Assert.False(OutsourcingStatusFlow.IsTerminal(status));
    }

    // ---------- Backwards and standing still ----------

    [Theory]
    [InlineData("accepted", "placed")]
    [InlineData("received_finished", "accepted")]
    [InlineData("received_semi_finished", "placed")]
    public void RejectsGoingBackwards(string from, string to)
    {
        Assert.False(OutsourcingStatusFlow.CanTransition(from, to));
    }

    [Theory]
    [InlineData("placed")]
    [InlineData("accepted")]
    [InlineData("received_finished")]
    public void RejectsRestatingTheCurrentStatus(string status)
    {
        Assert.False(OutsourcingStatusFlow.CanTransition(status, status));
    }

    // ---------- Unknown input ----------

    [Theory]
    [InlineData("cancelled")]
    [InlineData("received")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsUnknownStatuses(string? status)
    {
        Assert.False(OutsourcingStatusFlow.IsKnown(status));
    }

    [Fact]
    public void DoesNotShareRawMaterialStatusNames()
    {
        // 'received' belongs to the raw material chain; using it here would be a
        // silent cross-wiring between two separate sub-processes.
        Assert.False(OutsourcingStatusFlow.IsKnown("received"));
        Assert.False(OutsourcingStatusFlow.IsKnown("sent_to_supplier"));
    }

    // ---------- Case handling ----------

    [Fact]
    public void MatchesAndCanonicalisesCaseInsensitively()
    {
        Assert.True(OutsourcingStatusFlow.IsKnown("PLACED"));
        Assert.True(OutsourcingStatusFlow.CanTransition("Placed", "ACCEPTED"));
        Assert.Equal("received_finished", OutsourcingStatusFlow.Canonical("RECEIVED_FINISHED"));
    }
}
