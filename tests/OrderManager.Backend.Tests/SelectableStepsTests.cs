using OrderManager.Backend.Lib.Workflow;

namespace OrderManager.Backend.Tests;

/// <summary>
/// Only genuine units of factory work may be chosen as production steps.
///
/// Before v5 the template exposed every non-initial status as selectable, so the "this
/// item will require" checklist offered WITH_SUPPLIER, SEMI_FINISHED and FINISHED — and
/// the production plan accepted them, creating meaningless work items a supervisor could
/// mark complete. These tests hold the boundary.
///
/// Mirrors sql/021_production_step_template_v5.sql.
/// </summary>
public class SelectableStepsTests
{
    private const string V5Json = """
    {
      "initialStatus": "PENDING",
      "statuses": [
        { "code": "PENDING", "name": "Pending" },
        { "code": "WITH_SUPPLIER", "name": "With Supplier" },
        { "code": "SEMI_FINISHED", "name": "Received Semi-Finished" },
        { "code": "CARPENTRY", "name": "Carpentry", "selectableAsStep": true },
        { "code": "POLISHING", "name": "Polishing", "selectableAsStep": true },
        { "code": "UPHOLSTERY", "name": "Upholstery", "selectableAsStep": true },
        { "code": "FINISHED", "name": "Finished" }
      ],
      "transitions": [
        { "from": "PENDING", "to": "CARPENTRY", "methods": ["factory"] }
      ]
    }
    """;

    private static WorkflowTemplate V5() => WorkflowTemplate.Parse(V5Json);

    [Fact]
    public void OnlyRealFactoryWorkIsSelectable()
    {
        var codes = V5().SelectableSteps.Select(s => s.Code).ToList();

        Assert.Equal(["CARPENTRY", "POLISHING", "UPHOLSTERY"], codes);
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("WITH_SUPPLIER")]
    [InlineData("SEMI_FINISHED")]
    [InlineData("FINISHED")]
    public void LifecycleStatusesAreNotSelectable(string code)
    {
        // These are set by the system — WITH_SUPPLIER and SEMI_FINISHED by the
        // outsourcing flow, FINISHED by a line-item transition. None is work anyone
        // performs, so none may be planned as a step.
        Assert.DoesNotContain(V5().SelectableSteps, s => s.Code == code);
    }

    [Fact]
    public void SelectableStepsAreStillRealStatuses()
    {
        // The checklist is a subset of the template's statuses, not a separate list —
        // a planned step must be a status the item can actually transition into.
        var template = V5();

        Assert.All(template.SelectableSteps,
            step => Assert.Contains(template.Statuses, s => s.Code == step.Code));
    }

    [Fact]
    public void TheFlagDefaultsToFalseWhenAbsent()
    {
        // Deliberate: a template that forgets the flag yields an empty checklist —
        // visibly broken — rather than silently offering lifecycle statuses as work.
        var template = WorkflowTemplate.Parse("""
        {
          "initialStatus": "PENDING",
          "statuses": [
            { "code": "PENDING", "name": "Pending" },
            { "code": "CARPENTRY", "name": "Carpentry" }
          ],
          "transitions": [ { "from": "PENDING", "to": "CARPENTRY" } ]
        }
        """);

        Assert.Empty(template.SelectableSteps);
    }

    [Fact]
    public void TerminalStatusDerivationIsUnaffected()
    {
        // SelectableAsStep is about the checklist; it must not disturb the completeness
        // gate, which derives terminal statuses from the transition graph.
        var template = WorkflowTemplate.Parse("""
        {
          "initialStatus": "PENDING",
          "statuses": [
            { "code": "PENDING", "name": "Pending" },
            { "code": "CARPENTRY", "name": "Carpentry", "selectableAsStep": true },
            { "code": "FINISHED", "name": "Finished" }
          ],
          "transitions": [
            { "from": "PENDING", "to": "CARPENTRY" },
            { "from": "CARPENTRY", "to": "FINISHED" }
          ]
        }
        """);

        Assert.Equal(["FINISHED"], template.TerminalStatuses);
        Assert.Single(template.SelectableSteps);
    }
}
