using OrderManager.Backend.Lib.Workflow;

namespace OrderManager.Backend.Tests;

/// <summary>
/// The invoicing handoff is declared in the template rather than hard-coded, so that
/// which status notifies (and therefore which client process triggers the accountant)
/// is config. These mirror sql/012_process_template_v5.sql.
/// </summary>
public class NotifyEventTests
{
    private readonly TransitionValidator _validator = new();
    private static readonly string[] AnyRole = ["salesperson"];

    private const string V5Fragment = """
    {
      "initialStatus": "NEW",
      "statuses": [
        { "code": "NEW", "name": "New Order Capture" },
        { "code": "IN_PRODUCTION", "name": "In Production" },
        { "code": "READY_TO_INVOICE", "name": "Ready to Invoice" },
        { "code": "READY_TO_DELIVER", "name": "Ready to Deliver" },
        { "code": "KEEP_IN_FACTORY", "name": "Keep in Factory" }
      ],
      "transitions": [
        { "from": "NEW", "to": "IN_PRODUCTION" },
        { "from": "IN_PRODUCTION", "to": "READY_TO_INVOICE",
          "orderTypes": ["customer"], "requiresAllLineItemsComplete": true,
          "notifyEvent": "invoice_ready" },
        { "from": "READY_TO_INVOICE", "to": "READY_TO_DELIVER", "orderTypes": ["customer"] },
        { "from": "IN_PRODUCTION", "to": "KEEP_IN_FACTORY",
          "orderTypes": ["stock"], "requiresAllLineItemsComplete": true }
      ]
    }
    """;

    private static WorkflowTemplate Template() => WorkflowTemplate.Parse(V5Fragment);

    [Fact]
    public void ParsesTheNotifyEventFromTemplateJson()
    {
        var rule = Template().Transitions.Single(t => t.To == "READY_TO_INVOICE");

        Assert.Equal("invoice_ready", rule.NotifyEvent);
    }

    [Fact]
    public void TransitionsWithoutANotifyEvent_NotifyNobody()
    {
        foreach (var rule in Template().Transitions.Where(t => t.To != "READY_TO_INVOICE"))
        {
            Assert.Null(rule.NotifyEvent);
        }
    }

    [Fact]
    public void TheInvoicingHandoffIsSurfacedOnTheAllowedDecision()
    {
        // The endpoint reads NotifyEvent off the matched rule, so it must survive
        // validation rather than needing a second template lookup.
        var decision = _validator.Validate(
            Template(), "IN_PRODUCTION", "READY_TO_INVOICE", AnyRole, orderType: "customer");

        Assert.True(decision.IsAllowed);
        Assert.Equal("invoice_ready", decision.MatchedRule!.NotifyEvent);
    }

    [Fact]
    public void AStockOrderNeverTriggersTheInvoicingHandoff()
    {
        // It cannot reach READY_TO_INVOICE at all, so the accountant is never told to
        // invoice something that will never be invoiced.
        var decision = _validator.Validate(
            Template(), "IN_PRODUCTION", "READY_TO_INVOICE", AnyRole, orderType: "stock");

        Assert.False(decision.IsAllowed);
        Assert.Null(decision.MatchedRule);
    }

    [Fact]
    public void AStockOrdersOwnRouteNotifiesNobody()
    {
        var decision = _validator.Validate(
            Template(), "IN_PRODUCTION", "KEEP_IN_FACTORY", AnyRole, orderType: "stock");

        Assert.True(decision.IsAllowed);
        Assert.Null(decision.MatchedRule!.NotifyEvent);
    }

    [Fact]
    public void ExactlyOneTransitionNotifies()
    {
        var notifying = Template().Transitions.Where(t => t.NotifyEvent is not null).ToList();

        var only = Assert.Single(notifying);
        Assert.Equal("IN_PRODUCTION", only.From);
        Assert.Equal("READY_TO_INVOICE", only.To);
    }

    [Fact]
    public void NotifyEventNamesMatchTheContractPushTypes()
    {
        // Contract §11 fixes the set of push types; a typo here would produce
        // notification_recipients rows that never match anything.
        string[] contractTypes =
            ["order_status_changed", "invoice_ready", "raw_material_received", "item_assigned"];

        foreach (var rule in Template().Transitions.Where(t => t.NotifyEvent is not null))
        {
            Assert.Contains(rule.NotifyEvent, contractTypes);
        }
    }
}
