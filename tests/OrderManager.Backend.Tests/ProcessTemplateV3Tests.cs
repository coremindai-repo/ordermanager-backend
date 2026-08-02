using OrderManager.Backend.Lib.Workflow;

namespace OrderManager.Backend.Tests;

/// <summary>
/// Exercises the v3 logistics chain. The JSON mirrors sql/009_process_template_v3.sql
/// verbatim — if that seed changes, update these in the same commit.
/// </summary>
public class ProcessTemplateV3Tests
{
    private readonly TransitionValidator _validator = new();
    private static readonly string[] AnyRole = ["salesperson"];

    private const string V3Json = """
    {
      "initialStatus": "NEW",
      "statuses": [
        { "code": "NEW", "name": "New Order Capture" },
        { "code": "IN_PRODUCTION", "name": "In Production" },
        { "code": "KEEP_IN_FACTORY", "name": "Keep in Factory" },
        { "code": "SENT_TO_WAREHOUSE", "name": "Sent to Warehouse" },
        { "code": "IN_TRANSIT", "name": "In Transit" },
        { "code": "SENT_TO_STORE", "name": "Sent to Store" },
        { "code": "RECEIVED_IN_STORE", "name": "Received in Store" },
        { "code": "READY_TO_INVOICE", "name": "Ready to Invoice" },
        { "code": "READY_TO_DELIVER", "name": "Ready to Deliver" },
        { "code": "OUT_FOR_DELIVERY", "name": "Out for Delivery" },
        { "code": "DELIVERED", "name": "Delivered" }
      ],
      "transitions": [
        { "from": "NEW", "to": "IN_PRODUCTION" },
        { "from": "IN_PRODUCTION", "to": "KEEP_IN_FACTORY", "requiresAllLineItemsComplete": true },
        { "from": "IN_PRODUCTION", "to": "SENT_TO_WAREHOUSE", "requiresAllLineItemsComplete": true },
        { "from": "KEEP_IN_FACTORY", "to": "SENT_TO_WAREHOUSE" },
        { "from": "KEEP_IN_FACTORY", "to": "IN_TRANSIT", "requiresDestinationStore": true },
        { "from": "SENT_TO_WAREHOUSE", "to": "IN_TRANSIT", "requiresDestinationStore": true },
        { "from": "IN_TRANSIT", "to": "SENT_TO_STORE" },
        { "from": "SENT_TO_STORE", "to": "RECEIVED_IN_STORE" },
        { "from": "RECEIVED_IN_STORE", "to": "READY_TO_INVOICE" },
        { "from": "RECEIVED_IN_STORE", "to": "OUT_FOR_DELIVERY" },
        { "from": "READY_TO_INVOICE", "to": "READY_TO_DELIVER" },
        { "from": "READY_TO_DELIVER", "to": "OUT_FOR_DELIVERY" },
        { "from": "OUT_FOR_DELIVERY", "to": "DELIVERED" },
        { "from": "KEEP_IN_FACTORY", "to": "IN_PRODUCTION", "revert": true },
        { "from": "SENT_TO_WAREHOUSE", "to": "IN_PRODUCTION", "revert": true },
        { "from": "IN_TRANSIT", "to": "SENT_TO_WAREHOUSE", "revert": true },
        { "from": "SENT_TO_STORE", "to": "IN_TRANSIT", "revert": true },
        { "from": "RECEIVED_IN_STORE", "to": "SENT_TO_STORE", "revert": true },
        { "from": "OUT_FOR_DELIVERY", "to": "RECEIVED_IN_STORE", "revert": true }
      ]
    }
    """;

    private static WorkflowTemplate V3() => WorkflowTemplate.Parse(V3Json);

    private bool Allowed(string from, string to) => _validator.Validate(V3(), from, to, AnyRole).IsAllowed;

    // ---------- The happy path through the chain ----------

    [Fact]
    public void WalksTheFullLogisticsChain_ViaInvoicing()
    {
        string[] path =
        [
            "NEW", "IN_PRODUCTION", "SENT_TO_WAREHOUSE", "IN_TRANSIT", "SENT_TO_STORE",
            "RECEIVED_IN_STORE", "READY_TO_INVOICE", "READY_TO_DELIVER", "OUT_FOR_DELIVERY", "DELIVERED",
        ];

        for (var i = 0; i < path.Length - 1; i++)
        {
            Assert.True(Allowed(path[i], path[i + 1]), $"{path[i]} -> {path[i + 1]} should be allowed");
        }
    }

    [Fact]
    public void GoodsCanGoStraightOutFromTheStore_WithoutPassingThroughInvoicing()
    {
        Assert.True(Allowed("RECEIVED_IN_STORE", "OUT_FOR_DELIVERY"));
    }

    [Fact]
    public void FactoryHoldIsOptional_GoodsMayGoDirectToWarehouse()
    {
        Assert.True(Allowed("IN_PRODUCTION", "SENT_TO_WAREHOUSE"));
        Assert.True(Allowed("IN_PRODUCTION", "KEEP_IN_FACTORY"));
    }

    [Fact]
    public void GoodsMayDispatchDirectlyFromTheFactory_SkippingTheWarehouse()
    {
        Assert.True(Allowed("KEEP_IN_FACTORY", "IN_TRANSIT"));
    }

    // ---------- Confirmed exclusions ----------

    [Fact]
    public void CannotSkipStraightFromProductionToTransit()
    {
        // Dispatch is always an explicit decision, never implicit.
        Assert.False(Allowed("IN_PRODUCTION", "IN_TRANSIT"));
    }

    [Fact]
    public void CannotSkipTheStoreReceiptConfirmation()
    {
        // SENT_TO_STORE means despatched; RECEIVED_IN_STORE is a separate confirmation
        // that it actually arrived.
        Assert.False(Allowed("SENT_TO_STORE", "OUT_FOR_DELIVERY"));
    }

    [Fact]
    public void CannotJumpFromTransitToDelivered()
    {
        Assert.False(Allowed("IN_TRANSIT", "DELIVERED"));
    }

    [Fact]
    public void PostProductionIsGoneInV3()
    {
        var decision = _validator.Validate(V3(), "POST_PRODUCTION", "READY_TO_INVOICE", AnyRole);

        // An order stranded in the removed status reports this, which is exactly why
        // sql/009 refuses to run while any order still sits there.
        Assert.Equal(TransitionOutcome.UnknownCurrentStatus, decision.Outcome);
    }

    // ---------- Reverts ----------

    [Theory]
    [InlineData("KEEP_IN_FACTORY", "IN_PRODUCTION")]      // defect found before dispatch
    [InlineData("SENT_TO_WAREHOUSE", "IN_PRODUCTION")]    // rework after reaching the warehouse
    [InlineData("IN_TRANSIT", "SENT_TO_WAREHOUSE")]       // shipment recalled
    [InlineData("SENT_TO_STORE", "IN_TRANSIT")]           // wrong store, moving on
    [InlineData("RECEIVED_IN_STORE", "SENT_TO_STORE")]    // marked received in error
    [InlineData("OUT_FOR_DELIVERY", "RECEIVED_IN_STORE")] // failed delivery, back at store
    public void AllowsTheAgreedReverts(string from, string to)
    {
        Assert.True(Allowed(from, to));
    }

    [Fact]
    public void EveryReverseEdgeIsMarkedAsARevert()
    {
        // Guards against a backward move sneaking in as an ordinary forward edge.
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["NEW"] = 0, ["IN_PRODUCTION"] = 1, ["KEEP_IN_FACTORY"] = 2, ["SENT_TO_WAREHOUSE"] = 3,
            ["IN_TRANSIT"] = 4, ["SENT_TO_STORE"] = 5, ["RECEIVED_IN_STORE"] = 6,
            ["READY_TO_INVOICE"] = 7, ["READY_TO_DELIVER"] = 8, ["OUT_FOR_DELIVERY"] = 9, ["DELIVERED"] = 10,
        };

        foreach (var t in V3().Transitions.Where(t => order[t.From] > order[t.To]))
        {
            Assert.True(t.Revert, $"{t.From} -> {t.To} moves backward but is not marked revert");
        }
    }

    [Fact]
    public void RejectsUnagreedBackwardMoves()
    {
        Assert.False(Allowed("DELIVERED", "OUT_FOR_DELIVERY"));
        Assert.False(Allowed("READY_TO_DELIVER", "READY_TO_INVOICE"));
        Assert.False(Allowed("IN_TRANSIT", "KEEP_IN_FACTORY"));
    }

    // ---------- Gates ----------

    [Fact]
    public void BothRoutesOutOfProduction_RequireEveryLineItemComplete()
    {
        foreach (var to in new[] { "KEEP_IN_FACTORY", "SENT_TO_WAREHOUSE" })
        {
            var decision = _validator.Validate(V3(), "IN_PRODUCTION", to, AnyRole);
            Assert.True(decision.MatchedRule!.RequiresAllLineItemsComplete, $"IN_PRODUCTION -> {to} must be gated");
        }
    }

    [Fact]
    public void BothRoutesIntoTransit_RequireADestinationStore()
    {
        foreach (var from in new[] { "KEEP_IN_FACTORY", "SENT_TO_WAREHOUSE" })
        {
            var decision = _validator.Validate(V3(), from, "IN_TRANSIT", AnyRole);
            Assert.True(decision.MatchedRule!.RequiresDestinationStore, $"{from} -> IN_TRANSIT must require a store");
        }
    }

    [Fact]
    public void NoOtherTransitionCarriesAGate()
    {
        var gated = V3().Transitions
            .Where(t => t.RequiresAllLineItemsComplete || t.RequiresDestinationStore)
            .Select(t => $"{t.From}->{t.To}")
            .OrderBy(x => x)
            .ToList();

        Assert.Equal(
            ["IN_PRODUCTION->KEEP_IN_FACTORY", "IN_PRODUCTION->SENT_TO_WAREHOUSE",
             "KEEP_IN_FACTORY->IN_TRANSIT", "SENT_TO_WAREHOUSE->IN_TRANSIT"],
            gated);
    }

    [Fact]
    public void RevertingOutOfTransit_DoesNotRequireAStore()
    {
        // Recalling a shipment must not be blocked by the gate that governs sending it.
        var decision = _validator.Validate(V3(), "IN_TRANSIT", "SENT_TO_WAREHOUSE", AnyRole);

        Assert.True(decision.IsAllowed);
        Assert.False(decision.MatchedRule!.RequiresDestinationStore);
    }

    // ---------- Shape ----------

    [Fact]
    public void DeliveredIsTheOnlyDeadEnd()
    {
        Assert.Equal(["DELIVERED"], V3().TerminalStatuses);
    }

    [Fact]
    public void NoStatusNamesAStore()
    {
        // Destination is carried by orders.store_id; baking store names into statuses
        // would multiply the list every time a store is added.
        foreach (var status in V3().Statuses)
        {
            Assert.DoesNotContain("KOCHI", status.Code, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BANGALORE", status.Code, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void EveryStatusIsReachableFromTheInitialStatus()
    {
        // Catches a status added to the list but never wired into the graph.
        var template = V3();
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { template.InitialStatus };
        var queue = new Queue<string>([template.InitialStatus]);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in template.Transitions
                         .Where(t => string.Equals(t.From, current, StringComparison.OrdinalIgnoreCase))
                         .Select(t => t.To)
                         .Where(to => reached.Add(to)))
            {
                queue.Enqueue(next);
            }
        }

        var unreachable = template.Statuses.Select(s => s.Code).Where(c => !reached.Contains(c)).ToList();
        Assert.Empty(unreachable);
    }
}
