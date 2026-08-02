using OrderManager.Backend.Lib.Workflow;

namespace OrderManager.Backend.Tests;

/// <summary>
/// Exercises the v4 order-type branch. The JSON mirrors sql/010_process_template_v4.sql
/// verbatim — if that seed changes, update these in the same commit.
///
/// The rule being protected: invoicing applies to customer orders only, and happens
/// immediately after production, before any dispatch decision. Stock orders never
/// touch the two invoice statuses.
/// </summary>
public class ProcessTemplateV4Tests
{
    private readonly TransitionValidator _validator = new();
    private static readonly string[] AnyRole = ["salesperson"];

    private const string Customer = "customer";
    private const string Stock = "stock";

    private const string V4Json = """
    {
      "initialStatus": "NEW",
      "statuses": [
        { "code": "NEW", "name": "New Order Capture" },
        { "code": "IN_PRODUCTION", "name": "In Production" },
        { "code": "READY_TO_INVOICE", "name": "Ready to Invoice" },
        { "code": "READY_TO_DELIVER", "name": "Ready to Deliver" },
        { "code": "KEEP_IN_FACTORY", "name": "Keep in Factory" },
        { "code": "SENT_TO_WAREHOUSE", "name": "Sent to Warehouse" },
        { "code": "IN_TRANSIT", "name": "In Transit" },
        { "code": "SENT_TO_STORE", "name": "Sent to Store" },
        { "code": "RECEIVED_IN_STORE", "name": "Received in Store" },
        { "code": "OUT_FOR_DELIVERY", "name": "Out for Delivery" },
        { "code": "DELIVERED", "name": "Delivered" }
      ],
      "transitions": [
        { "from": "NEW", "to": "IN_PRODUCTION" },
        { "from": "IN_PRODUCTION", "to": "READY_TO_INVOICE",
          "orderTypes": ["customer"], "requiresAllLineItemsComplete": true },
        { "from": "READY_TO_INVOICE", "to": "READY_TO_DELIVER", "orderTypes": ["customer"] },
        { "from": "READY_TO_DELIVER", "to": "KEEP_IN_FACTORY", "orderTypes": ["customer"] },
        { "from": "READY_TO_DELIVER", "to": "SENT_TO_WAREHOUSE", "orderTypes": ["customer"] },
        { "from": "IN_PRODUCTION", "to": "KEEP_IN_FACTORY",
          "orderTypes": ["stock"], "requiresAllLineItemsComplete": true },
        { "from": "IN_PRODUCTION", "to": "SENT_TO_WAREHOUSE",
          "orderTypes": ["stock"], "requiresAllLineItemsComplete": true },
        { "from": "KEEP_IN_FACTORY", "to": "SENT_TO_WAREHOUSE" },
        { "from": "KEEP_IN_FACTORY", "to": "IN_TRANSIT", "requiresDestinationStore": true },
        { "from": "SENT_TO_WAREHOUSE", "to": "IN_TRANSIT", "requiresDestinationStore": true },
        { "from": "IN_TRANSIT", "to": "SENT_TO_STORE" },
        { "from": "SENT_TO_STORE", "to": "RECEIVED_IN_STORE" },
        { "from": "RECEIVED_IN_STORE", "to": "OUT_FOR_DELIVERY" },
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

    private static WorkflowTemplate V4() => WorkflowTemplate.Parse(V4Json);

    private bool Allowed(string from, string to, string orderType) =>
        _validator.Validate(V4(), from, to, AnyRole, orderType: orderType).IsAllowed;

    private TransitionDecision Decide(string from, string to, string orderType) =>
        _validator.Validate(V4(), from, to, AnyRole, orderType: orderType);

    private static readonly string[] SharedTail =
        ["KEEP_IN_FACTORY", "IN_TRANSIT", "SENT_TO_STORE", "RECEIVED_IN_STORE", "OUT_FOR_DELIVERY", "DELIVERED"];

    // ---------- Customer path ----------

    [Fact]
    public void CustomerOrder_InvoicesImmediatelyAfterProduction_ThenShips()
    {
        string[] path =
        [
            "NEW", "IN_PRODUCTION", "READY_TO_INVOICE", "READY_TO_DELIVER",
            "KEEP_IN_FACTORY", "IN_TRANSIT", "SENT_TO_STORE", "RECEIVED_IN_STORE",
            "OUT_FOR_DELIVERY", "DELIVERED",
        ];

        for (var i = 0; i < path.Length - 1; i++)
        {
            Assert.True(Allowed(path[i], path[i + 1], Customer), $"customer: {path[i]} -> {path[i + 1]}");
        }
    }

    [Fact]
    public void CustomerOrder_CannotSkipInvoicing_StraightIntoTheLogisticsChain()
    {
        // The requirement stated explicitly: a customer order can't skip these.
        Assert.False(Allowed("IN_PRODUCTION", "KEEP_IN_FACTORY", Customer));
        Assert.False(Allowed("IN_PRODUCTION", "SENT_TO_WAREHOUSE", Customer));
    }

    [Theory]
    [InlineData("KEEP_IN_FACTORY")]
    [InlineData("SENT_TO_WAREHOUSE")]
    public void CustomerOrder_EntersLogisticsOnlyAfterReadyToDeliver(string entryPoint)
    {
        Assert.True(Allowed("READY_TO_DELIVER", entryPoint, Customer));
    }

    [Fact]
    public void CustomerOrder_SkippingReadyToDeliver_IsRejected()
    {
        Assert.False(Allowed("READY_TO_INVOICE", "KEEP_IN_FACTORY", Customer));
    }

    // ---------- Stock path ----------

    [Fact]
    public void StockOrder_GoesStraightFromProductionIntoLogistics()
    {
        string[] path =
        [
            "NEW", "IN_PRODUCTION", "SENT_TO_WAREHOUSE", "IN_TRANSIT",
            "SENT_TO_STORE", "RECEIVED_IN_STORE", "OUT_FOR_DELIVERY", "DELIVERED",
        ];

        for (var i = 0; i < path.Length - 1; i++)
        {
            Assert.True(Allowed(path[i], path[i + 1], Stock), $"stock: {path[i]} -> {path[i + 1]}");
        }
    }

    [Fact]
    public void StockOrder_CanNeverReachReadyToInvoice()
    {
        // The headline guarantee: no status in the whole template leads a stock order
        // into an invoice status.
        var template = V4();

        foreach (var from in template.Statuses.Select(s => s.Code))
        {
            foreach (var invoiceStatus in new[] { "READY_TO_INVOICE", "READY_TO_DELIVER" })
            {
                Assert.False(
                    Allowed(from, invoiceStatus, Stock),
                    $"stock order must never reach {invoiceStatus}, but {from} -> {invoiceStatus} was allowed");
            }
        }
    }

    [Fact]
    public void StockOrder_AttemptingToInvoice_IsReportedAsAnOrderTypeProblem()
    {
        var decision = Decide("IN_PRODUCTION", "READY_TO_INVOICE", Stock);

        Assert.Equal(TransitionOutcome.OrderTypeNotPermitted, decision.Outcome);
        Assert.Contains("stock", decision.Message);
    }

    [Fact]
    public void StockOrder_AttemptingToInvoice_MapsTo409IllegalTransition()
    {
        // Not actionable — unlike a missing store there is nothing the user can fix.
        var exception = TransitionOutcomeMapper.ToException(Decide("IN_PRODUCTION", "READY_TO_INVOICE", Stock));

        Assert.Equal(409, exception.StatusCode);
        Assert.Equal("ILLEGAL_TRANSITION", exception.Code);
    }

    // ---------- The tail is shared ----------

    [Fact]
    public void BothOrderTypes_ShareTheLogisticsTail()
    {
        for (var i = 0; i < SharedTail.Length - 1; i++)
        {
            Assert.True(Allowed(SharedTail[i], SharedTail[i + 1], Customer), $"customer tail: {SharedTail[i]}");
            Assert.True(Allowed(SharedTail[i], SharedTail[i + 1], Stock), $"stock tail: {SharedTail[i]}");
        }
    }

    [Fact]
    public void BothOrderTypes_ShareTheReverts()
    {
        (string From, string To)[] reverts =
        [
            ("KEEP_IN_FACTORY", "IN_PRODUCTION"),
            ("SENT_TO_WAREHOUSE", "IN_PRODUCTION"),
            ("IN_TRANSIT", "SENT_TO_WAREHOUSE"),
            ("SENT_TO_STORE", "IN_TRANSIT"),
            ("RECEIVED_IN_STORE", "SENT_TO_STORE"),
            ("OUT_FOR_DELIVERY", "RECEIVED_IN_STORE"),
        ];

        foreach (var (from, to) in reverts)
        {
            Assert.True(Allowed(from, to, Customer), $"customer revert: {from} -> {to}");
            Assert.True(Allowed(from, to, Stock), $"stock revert: {from} -> {to}");
        }
    }

    // ---------- Gates survive the branch ----------

    [Fact]
    public void BothBranchesOutOfProduction_StillRequireEveryLineItemComplete()
    {
        Assert.True(Decide("IN_PRODUCTION", "READY_TO_INVOICE", Customer).MatchedRule!.RequiresAllLineItemsComplete);
        Assert.True(Decide("IN_PRODUCTION", "KEEP_IN_FACTORY", Stock).MatchedRule!.RequiresAllLineItemsComplete);
        Assert.True(Decide("IN_PRODUCTION", "SENT_TO_WAREHOUSE", Stock).MatchedRule!.RequiresAllLineItemsComplete);
    }

    [Fact]
    public void DispatchStillRequiresADestinationStore_ForBothOrderTypes()
    {
        foreach (var orderType in new[] { Customer, Stock })
        {
            Assert.True(Decide("KEEP_IN_FACTORY", "IN_TRANSIT", orderType).MatchedRule!.RequiresDestinationStore);
            Assert.True(Decide("SENT_TO_WAREHOUSE", "IN_TRANSIT", orderType).MatchedRule!.RequiresDestinationStore);
        }
    }

    // ---------- Shape ----------

    [Fact]
    public void DeliveredIsTheOnlyDeadEnd()
    {
        Assert.Equal(["DELIVERED"], V4().TerminalStatuses);
    }

    [Fact]
    public void EveryStatusIsReachableByAtLeastOneOrderType()
    {
        var template = V4();

        foreach (var orderType in new[] { Customer, Stock })
        {
            var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { template.InitialStatus };
            var queue = new Queue<string>([template.InitialStatus]);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var next in template.Statuses
                             .Select(s => s.Code)
                             .Where(to => Allowed(current, to, orderType) && reached.Add(to)))
                {
                    queue.Enqueue(next);
                }
            }

            // A stock order legitimately never reaches the invoice statuses.
            var expectedUnreachable = orderType == Stock
                ? new[] { "READY_TO_INVOICE", "READY_TO_DELIVER" }
                : [];

            var unreachable = template.Statuses.Select(s => s.Code).Where(c => !reached.Contains(c)).ToList();
            Assert.Equal(expectedUnreachable, unreachable);
        }
    }

    [Fact]
    public void OnlyTheBranchingEdgesAreOrderTypeRestricted()
    {
        var restricted = V4().Transitions
            .Where(t => t.OrderTypes is { Count: > 0 })
            .Select(t => $"{t.From}->{t.To}")
            .OrderBy(x => x)
            .ToList();

        Assert.Equal(
            ["IN_PRODUCTION->KEEP_IN_FACTORY", "IN_PRODUCTION->READY_TO_INVOICE",
             "IN_PRODUCTION->SENT_TO_WAREHOUSE", "READY_TO_DELIVER->KEEP_IN_FACTORY",
             "READY_TO_DELIVER->SENT_TO_WAREHOUSE", "READY_TO_INVOICE->READY_TO_DELIVER"],
            restricted);
    }

    [Fact]
    public void AnUnknownOrderType_CannotTakeARestrictedEdge()
    {
        // Defensive: an order_type outside customer/stock must not slip through a
        // restricted edge just because it is unrecognised.
        Assert.False(Allowed("IN_PRODUCTION", "READY_TO_INVOICE", "consignment"));
        Assert.False(Allowed("IN_PRODUCTION", "KEEP_IN_FACTORY", "consignment"));
    }
}
