using OrderManager.Backend.Lib.Workflow;

namespace OrderManager.Backend.Tests;

/// <summary>
/// An order ships as one unit — it only leaves the factory once every line item is
/// finished. This is the order-wide gate, distinct from "all steps within a single
/// line item are done".
/// </summary>
public class LineItemCompletionTests
{
    private static readonly IReadOnlySet<string> Terminal =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FINISHED" };

    [Fact]
    public void AllComplete_WhenEveryLineItemIsFinished()
    {
        Assert.True(LineItemCompletion.AllComplete(["FINISHED", "FINISHED", "FINISHED"], Terminal));
    }

    [Fact]
    public void NotComplete_WhenOneLineItemIsStillInProgress()
    {
        // The case that matters: two of three done is not done.
        Assert.False(LineItemCompletion.AllComplete(["FINISHED", "FINISHED", "CARPENTRY"], Terminal));
    }

    [Fact]
    public void NotComplete_WhenOneLineItemHasNotStarted()
    {
        Assert.False(LineItemCompletion.AllComplete(["FINISHED", "PENDING"], Terminal));
    }

    [Fact]
    public void NotComplete_WhenNoLineItemsExist()
    {
        // Must not pass on a vacuous truth — an order with no items is malformed,
        // not finished.
        Assert.False(LineItemCompletion.AllComplete([], Terminal));
    }

    [Fact]
    public void Complete_ForASingleFinishedLineItem()
    {
        Assert.True(LineItemCompletion.AllComplete(["FINISHED"], Terminal));
    }

    [Fact]
    public void TerminalStatusMatching_IsCaseInsensitive()
    {
        Assert.True(LineItemCompletion.AllComplete(["finished"], Terminal));
    }

    [Fact]
    public void IncompleteStatuses_NamesWhatIsBlocking()
    {
        var blocking = LineItemCompletion.IncompleteStatuses(
            ["FINISHED", "CARPENTRY", "PENDING", "CARPENTRY"], Terminal);

        Assert.Equal(2, blocking.Count);
        Assert.Contains("CARPENTRY", blocking);
        Assert.Contains("PENDING", blocking);
        Assert.DoesNotContain("FINISHED", blocking);
    }

    // ---------- Terminal status derivation ----------

    [Fact]
    public void ProductionTemplate_TreatsOnlyFinishedAsTerminal()
    {
        var template = WorkflowTemplate.Parse("""
        {
          "initialStatus": "PENDING",
          "statuses": [
            { "code": "PENDING", "name": "Pending" },
            { "code": "CARPENTRY", "name": "Carpentry" },
            { "code": "FINISHED", "name": "Finished" }
          ],
          "transitions": [
            { "from": "PENDING", "to": "CARPENTRY" },
            { "from": "CARPENTRY", "to": "FINISHED" }
          ]
        }
        """);

        Assert.Equal(["FINISHED"], template.TerminalStatuses);
    }

    [Fact]
    public void TerminalStatuses_HandlesSeveralEndStates()
    {
        // Outsourced and imported items may finish in their own end states.
        var template = WorkflowTemplate.Parse("""
        {
          "initialStatus": "PENDING",
          "statuses": [
            { "code": "PENDING", "name": "Pending" },
            { "code": "FINISHED", "name": "Finished" },
            { "code": "RECEIVED", "name": "Received" }
          ],
          "transitions": [
            { "from": "PENDING", "to": "FINISHED" },
            { "from": "PENDING", "to": "RECEIVED" }
          ]
        }
        """);

        Assert.Equal(2, template.TerminalStatuses.Count);
        Assert.Contains("FINISHED", template.TerminalStatuses);
        Assert.Contains("RECEIVED", template.TerminalStatuses);
    }
}

/// <summary>
/// The gate is declared in template config, so the validator must surface which rule
/// matched for the endpoint to act on it.
/// </summary>
public class TransitionGateTests
{
    private readonly TransitionValidator _validator = new();
    private static readonly string[] AnyRole = ["salesperson"];

    private static WorkflowTemplate GatedTemplate() => WorkflowTemplate.Parse("""
    {
      "initialStatus": "NEW",
      "statuses": [
        { "code": "NEW", "name": "New" },
        { "code": "IN_PRODUCTION", "name": "In Production" },
        { "code": "POST_PRODUCTION", "name": "Post-Production" }
      ],
      "transitions": [
        { "from": "NEW", "to": "IN_PRODUCTION" },
        { "from": "IN_PRODUCTION", "to": "POST_PRODUCTION", "requiresAllLineItemsComplete": true }
      ]
    }
    """);

    [Fact]
    public void ParsesTheGateFlagFromTemplateJson()
    {
        var gated = GatedTemplate().Transitions.Single(t => t.To == "POST_PRODUCTION");

        Assert.True(gated.RequiresAllLineItemsComplete);
    }

    [Fact]
    public void UngatedTransitionsDefaultToNoGate()
    {
        var ungated = GatedTemplate().Transitions.Single(t => t.To == "IN_PRODUCTION");

        Assert.False(ungated.RequiresAllLineItemsComplete);
    }

    [Fact]
    public void AllowedDecision_ExposesTheMatchedRule_SoTheGateCanBeApplied()
    {
        var decision = _validator.Validate(GatedTemplate(), "IN_PRODUCTION", "POST_PRODUCTION", AnyRole);

        Assert.True(decision.IsAllowed);
        Assert.NotNull(decision.MatchedRule);
        Assert.True(decision.MatchedRule!.RequiresAllLineItemsComplete);
    }

    [Fact]
    public void AllowedDecision_ForAnUngatedTransition_CarriesNoGate()
    {
        var decision = _validator.Validate(GatedTemplate(), "NEW", "IN_PRODUCTION", AnyRole);

        Assert.True(decision.IsAllowed);
        Assert.False(decision.MatchedRule!.RequiresAllLineItemsComplete);
    }

    [Fact]
    public void DeniedDecision_CarriesNoMatchedRule()
    {
        var decision = _validator.Validate(GatedTemplate(), "NEW", "POST_PRODUCTION", AnyRole);

        Assert.False(decision.IsAllowed);
        Assert.Null(decision.MatchedRule);
    }

    [Fact]
    public void SeededProcessTemplateV2_GatesLeavingProduction()
    {
        // Mirrors sql/008_process_template_v2.sql — the transition out of production
        // is the one that must wait for every line item.
        var template = WorkflowTemplate.Parse("""
        {
          "initialStatus": "NEW",
          "statuses": [
            { "code": "NEW", "name": "New Order Capture" },
            { "code": "IN_PRODUCTION", "name": "In Production" },
            { "code": "POST_PRODUCTION", "name": "Post-Production" },
            { "code": "READY_TO_INVOICE", "name": "Ready to Invoice" },
            { "code": "READY_TO_DELIVER", "name": "Ready to Deliver" },
            { "code": "DELIVERED", "name": "Delivered" }
          ],
          "transitions": [
            { "from": "NEW", "to": "IN_PRODUCTION" },
            { "from": "IN_PRODUCTION", "to": "POST_PRODUCTION", "requiresAllLineItemsComplete": true },
            { "from": "POST_PRODUCTION", "to": "READY_TO_INVOICE" },
            { "from": "READY_TO_INVOICE", "to": "READY_TO_DELIVER" },
            { "from": "READY_TO_DELIVER", "to": "DELIVERED" },
            { "from": "POST_PRODUCTION", "to": "IN_PRODUCTION", "revert": true }
          ]
        }
        """);

        var gated = template.Transitions.Where(t => t.RequiresAllLineItemsComplete).ToList();

        var only = Assert.Single(gated);
        Assert.Equal("IN_PRODUCTION", only.From);
        Assert.Equal("POST_PRODUCTION", only.To);
    }
}
