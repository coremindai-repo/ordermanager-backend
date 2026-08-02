using OrderManager.Backend.Lib.Workflow;

namespace OrderManager.Backend.Tests;

/// <summary>
/// Exercises the actual templates seeded for the pilot client. The JSON below mirrors
/// sql/005_seed_templates.sql verbatim — if that seed changes, these tests should be
/// updated in the same commit.
/// </summary>
public class PilotTemplateTests
{
    private readonly TransitionValidator _validator = new();
    private static readonly string[] AnyRole = ["salesperson"];

    private const string ProcessJson = """
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
        { "from": "IN_PRODUCTION", "to": "POST_PRODUCTION" },
        { "from": "POST_PRODUCTION", "to": "READY_TO_INVOICE" },
        { "from": "READY_TO_INVOICE", "to": "READY_TO_DELIVER" },
        { "from": "READY_TO_DELIVER", "to": "DELIVERED" },
        { "from": "POST_PRODUCTION", "to": "IN_PRODUCTION", "revert": true }
      ]
    }
    """;

    private const string ProductionJson = """
    {
      "initialStatus": "PENDING",
      "statuses": [
        { "code": "PENDING", "name": "Pending" },
        { "code": "CARPENTRY", "name": "Carpentry" },
        { "code": "POLISHING", "name": "Polishing" },
        { "code": "UPHOLSTERY", "name": "Upholstery" },
        { "code": "FINISHED", "name": "Finished" }
      ],
      "transitions": [
        { "from": "PENDING", "to": "CARPENTRY" },
        { "from": "PENDING", "to": "POLISHING" },
        { "from": "PENDING", "to": "UPHOLSTERY" },
        { "from": "CARPENTRY", "to": "POLISHING" },
        { "from": "CARPENTRY", "to": "UPHOLSTERY" },
        { "from": "CARPENTRY", "to": "FINISHED" },
        { "from": "POLISHING", "to": "UPHOLSTERY" },
        { "from": "POLISHING", "to": "FINISHED" },
        { "from": "UPHOLSTERY", "to": "FINISHED" }
      ]
    }
    """;

    // ---------- Parsing ----------

    [Fact]
    public void ProcessTemplate_ParsesStatusesAndTransitions()
    {
        var template = WorkflowTemplate.Parse(ProcessJson);

        Assert.Equal("NEW", template.InitialStatus);
        Assert.Equal(6, template.Statuses.Count);
        Assert.Equal(6, template.Transitions.Count);
        Assert.Equal("New Order Capture", template.Statuses[0].Name);
    }

    [Fact]
    public void ProcessTemplate_ParsesRevertFlag()
    {
        var template = WorkflowTemplate.Parse(ProcessJson);

        var revert = Assert.Single(template.Transitions, t => t.Revert);
        Assert.Equal("POST_PRODUCTION", revert.From);
        Assert.Equal("IN_PRODUCTION", revert.To);
    }

    [Fact]
    public void SeededTemplates_CarryNoRoleOrMethodRestrictions()
    {
        // Documents the seed decision: role gating is supported but deliberately not
        // seeded, so no transition is role-restricted until the client confirms who
        // performs each step.
        foreach (var json in new[] { ProcessJson, ProductionJson })
        {
            var template = WorkflowTemplate.Parse(json);
            Assert.All(template.Transitions, t =>
            {
                Assert.True(t.AllowedRoles is null or { Count: 0 });
                Assert.True(t.Methods is null or { Count: 0 });
            });
        }
    }

    // ---------- Exhaustive edge checks ----------

    [Fact]
    public void ProcessTemplate_AllowsExactlyTheIntendedTransitions()
    {
        var template = WorkflowTemplate.Parse(ProcessJson);
        string[] stages =
            ["NEW", "IN_PRODUCTION", "POST_PRODUCTION", "READY_TO_INVOICE", "READY_TO_DELIVER", "DELIVERED"];

        var expected = new HashSet<(string From, string To)>();
        for (var i = 0; i < stages.Length - 1; i++)
        {
            expected.Add((stages[i], stages[i + 1]));
        }
        expected.Add(("POST_PRODUCTION", "IN_PRODUCTION")); // the one revert allowance

        // Every one of the 36 ordered pairs, not just the happy path.
        foreach (var from in stages)
        {
            foreach (var to in stages)
            {
                var allowed = _validator.Validate(template, from, to, AnyRole).IsAllowed;

                Assert.True(
                    expected.Contains((from, to)) == allowed,
                    $"{from} -> {to}: expected allowed={expected.Contains((from, to))}, got {allowed}");
            }
        }
    }

    [Fact]
    public void ProcessTemplate_WalksTheFullHappyPath()
    {
        var template = WorkflowTemplate.Parse(ProcessJson);
        string[] path =
            ["NEW", "IN_PRODUCTION", "POST_PRODUCTION", "READY_TO_INVOICE", "READY_TO_DELIVER", "DELIVERED"];

        for (var i = 0; i < path.Length - 1; i++)
        {
            Assert.True(_validator.Validate(template, path[i], path[i + 1], AnyRole).IsAllowed);
        }
    }

    [Fact]
    public void ProductionTemplate_AllowsExactlyTheIntendedTransitions()
    {
        var template = WorkflowTemplate.Parse(ProductionJson);
        string[] steps = ["PENDING", "CARPENTRY", "POLISHING", "UPHOLSTERY", "FINISHED"];

        var expected = new HashSet<(string From, string To)>
        {
            ("PENDING", "CARPENTRY"),
            ("PENDING", "POLISHING"),
            ("PENDING", "UPHOLSTERY"),
            ("CARPENTRY", "POLISHING"),
            ("CARPENTRY", "UPHOLSTERY"),
            ("CARPENTRY", "FINISHED"),
            ("POLISHING", "UPHOLSTERY"),
            ("POLISHING", "FINISHED"),
            ("UPHOLSTERY", "FINISHED"),
        };

        foreach (var from in steps)
        {
            foreach (var to in steps)
            {
                var allowed = _validator.Validate(template, from, to, AnyRole, "factory").IsAllowed;

                Assert.True(
                    expected.Contains((from, to)) == allowed,
                    $"{from} -> {to}: expected allowed={expected.Contains((from, to))}, got {allowed}");
            }
        }
    }

    [Fact]
    public void ProductionTemplate_AllowsSkippingStepsAnItemDidNotSelect()
    {
        // An item that needs carpentry then upholstery, but no polishing.
        var template = WorkflowTemplate.Parse(ProductionJson);

        Assert.True(_validator.Validate(template, "PENDING", "CARPENTRY", AnyRole, "factory").IsAllowed);
        Assert.True(_validator.Validate(template, "CARPENTRY", "UPHOLSTERY", AnyRole, "factory").IsAllowed);
        Assert.True(_validator.Validate(template, "UPHOLSTERY", "FINISHED", AnyRole, "factory").IsAllowed);
    }

    [Fact]
    public void ProductionTemplate_RejectsMovingBackToAnEarlierStep()
    {
        var template = WorkflowTemplate.Parse(ProductionJson);

        var decision = _validator.Validate(template, "UPHOLSTERY", "CARPENTRY", AnyRole, "factory");

        Assert.Equal(TransitionOutcome.TransitionNotAllowed, decision.Outcome);
    }

    [Fact]
    public void ProductionTemplate_AppliesToEveryMethod_SinceNoneAreRestricted()
    {
        var template = WorkflowTemplate.Parse(ProductionJson);

        foreach (var method in new[] { "factory", "outsource", "import" })
        {
            Assert.True(_validator.Validate(template, "PENDING", "CARPENTRY", AnyRole, method).IsAllowed);
        }
    }
}
