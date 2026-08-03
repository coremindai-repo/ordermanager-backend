using OrderManager.Backend.Lib.Workflow;

namespace OrderManager.Backend.Tests;

/// <summary>
/// The plan lock was narrowed from "no changes once work has started" to "no
/// DESTRUCTIVE changes", so a claimed semi-finished item can have its remaining steps
/// planned under its new order.
///
/// The refusal side is the point of these tests: the append path must not become a way
/// to quietly discard completed work.
/// </summary>
public class ProductionPlanChangeTests
{
    private static ExistingStep Done(string name, int seq) => new(name, seq, "complete");
    private static ExistingStep Started(string name, int seq) => new(name, seq, "started");
    private static ExistingStep Pending(string name, int seq) => new(name, seq, "pending");

    // ---------- Appending is allowed ----------

    [Fact]
    public void AllowsAddingAStepToAPlanWithCompletedWork()
    {
        // The claimed semi-finished case: carpentry and polishing done elsewhere,
        // upholstery still needed under the new order.
        var decision = ProductionPlanChange.Evaluate(
            [Done("CARPENTRY", 1), Done("POLISHING", 2)],
            ["CARPENTRY", "POLISHING", "UPHOLSTERY"]);

        Assert.True(decision.IsAllowed);
        Assert.Equal(["UPHOLSTERY"], decision.StepsToAdd);
        Assert.Empty(decision.StepsToRemove);
    }

    [Fact]
    public void AllowsAddingSeveralStepsAtOnce()
    {
        var decision = ProductionPlanChange.Evaluate(
            [Done("CARPENTRY", 1)],
            ["CARPENTRY", "POLISHING", "UPHOLSTERY"]);

        Assert.True(decision.IsAllowed);
        Assert.Equal(["POLISHING", "UPHOLSTERY"], decision.StepsToAdd);
    }

    [Fact]
    public void AllowsRemovingAStepThatHasNoWorkAgainstIt()
    {
        // Re-planning what has not been started yet is not destructive.
        var decision = ProductionPlanChange.Evaluate(
            [Done("CARPENTRY", 1), Pending("POLISHING", 2)],
            ["CARPENTRY", "UPHOLSTERY"]);

        Assert.True(decision.IsAllowed);
        Assert.Equal(["UPHOLSTERY"], decision.StepsToAdd);
        Assert.Equal(["POLISHING"], decision.StepsToRemove);
    }

    [Fact]
    public void AllowsWholesaleReplanningWhenNothingHasStarted()
    {
        var decision = ProductionPlanChange.Evaluate(
            [Pending("CARPENTRY", 1), Pending("POLISHING", 2)],
            ["UPHOLSTERY"]);

        Assert.True(decision.IsAllowed);
        Assert.Equal(["UPHOLSTERY"], decision.StepsToAdd);
        Assert.Equal(["CARPENTRY", "POLISHING"], decision.StepsToRemove);
    }

    [Fact]
    public void AllowsAnUnchangedPlan()
    {
        var decision = ProductionPlanChange.Evaluate(
            [Done("CARPENTRY", 1)],
            ["CARPENTRY"]);

        Assert.True(decision.IsAllowed);
        Assert.Empty(decision.StepsToAdd);
        Assert.Empty(decision.StepsToRemove);
    }

    // ---------- The refusals that matter ----------

    [Fact]
    public void RefusesDroppingACompletedStep()
    {
        // The exact abuse the narrowed guard must still catch: using the append path to
        // sneak a completed step out of the plan.
        var decision = ProductionPlanChange.Evaluate(
            [Done("CARPENTRY", 1), Done("POLISHING", 2)],
            ["UPHOLSTERY"]);

        Assert.False(decision.IsAllowed);
        Assert.Contains("CARPENTRY", decision.Message);
        Assert.Contains("POLISHING", decision.Message);
    }

    [Fact]
    public void RefusesDroppingAStartedStep()
    {
        // In-progress work counts too — it has assigned names and possibly photos.
        var decision = ProductionPlanChange.Evaluate(
            [Started("CARPENTRY", 1)],
            ["POLISHING"]);

        Assert.False(decision.IsAllowed);
        Assert.Contains("CARPENTRY", decision.Message);
    }

    [Fact]
    public void RefusesDroppingOneCompletedStepWhileAddingAnother()
    {
        // A partial swap is still destructive; adding something does not license
        // removing something else.
        var decision = ProductionPlanChange.Evaluate(
            [Done("CARPENTRY", 1), Done("POLISHING", 2)],
            ["CARPENTRY", "UPHOLSTERY"]);

        Assert.False(decision.IsAllowed);
        Assert.Contains("POLISHING", decision.Message);
    }

    [Fact]
    public void RefusesAnEmptyPlanWhenWorkExists()
    {
        var decision = ProductionPlanChange.Evaluate([Done("CARPENTRY", 1)], []);

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void RefusalReportsNothingToApply()
    {
        // A refused decision must not carry work for the caller to accidentally apply.
        var decision = ProductionPlanChange.Evaluate([Done("CARPENTRY", 1)], ["UPHOLSTERY"]);

        Assert.Empty(decision.StepsToAdd);
        Assert.Empty(decision.StepsToRemove);
    }

    [Fact]
    public void StepNamesAreMatchedCaseInsensitively()
    {
        // A casing difference must not read as "this completed step is being removed".
        var decision = ProductionPlanChange.Evaluate(
            [Done("CARPENTRY", 1)],
            ["carpentry", "POLISHING"]);

        Assert.True(decision.IsAllowed);
        Assert.Equal(["POLISHING"], decision.StepsToAdd);
    }

    // ---------- Method changes ----------

    [Fact]
    public void MethodMayChangeBeforeAnyWork()
    {
        Assert.True(ProductionPlanChange.CanChangeMethod([Pending("CARPENTRY", 1)]));
        Assert.True(ProductionPlanChange.CanChangeMethod([]));
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("started")]
    public void MethodIsFixedOnceWorkExists(string status)
    {
        // The recorded steps were performed under the old method; switching would leave
        // that history describing a route the item never took.
        Assert.False(ProductionPlanChange.CanChangeMethod([new ExistingStep("CARPENTRY", 1, status)]));
    }
}
