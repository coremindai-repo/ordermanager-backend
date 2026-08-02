using OrderManager.Backend.Lib.Workflow;

namespace OrderManager.Backend.Tests;

/// <summary>
/// The line-item side of Epic 6. Unlike the request chain this IS templatized, because
/// line-item transitions already validate against production_step_templates and the
/// order-level completeness gate derives its terminal statuses from it.
///
/// Mirrors sql/016_production_step_template_v3.sql.
/// </summary>
public class ProductionTemplateV3Tests
{
    private readonly TransitionValidator _validator = new();
    private static readonly string[] AnyRole = ["factory_supervisor"];

    private const string Factory = "factory";
    private const string Outsource = "outsource";
    private const string Import = "import";

    private const string V3Json = """
    {
      "initialStatus": "PENDING",
      "statuses": [
        { "code": "PENDING", "name": "Pending" },
        { "code": "WITH_SUPPLIER", "name": "With Supplier" },
        { "code": "SEMI_FINISHED", "name": "Received Semi-Finished" },
        { "code": "CARPENTRY", "name": "Carpentry" },
        { "code": "POLISHING", "name": "Polishing" },
        { "code": "UPHOLSTERY", "name": "Upholstery" },
        { "code": "FINISHED", "name": "Finished" }
      ],
      "transitions": [
        { "from": "PENDING", "to": "CARPENTRY", "methods": ["factory"] },
        { "from": "PENDING", "to": "POLISHING", "methods": ["factory"] },
        { "from": "PENDING", "to": "UPHOLSTERY", "methods": ["factory"] },
        { "from": "PENDING", "to": "WITH_SUPPLIER", "methods": ["outsource", "import"] },
        { "from": "WITH_SUPPLIER", "to": "FINISHED", "methods": ["outsource", "import"] },
        { "from": "WITH_SUPPLIER", "to": "SEMI_FINISHED", "methods": ["outsource"] },
        { "from": "SEMI_FINISHED", "to": "CARPENTRY", "methods": ["outsource"] },
        { "from": "SEMI_FINISHED", "to": "POLISHING", "methods": ["outsource"] },
        { "from": "SEMI_FINISHED", "to": "UPHOLSTERY", "methods": ["outsource"] },
        { "from": "CARPENTRY", "to": "POLISHING" },
        { "from": "CARPENTRY", "to": "UPHOLSTERY" },
        { "from": "CARPENTRY", "to": "FINISHED" },
        { "from": "POLISHING", "to": "UPHOLSTERY" },
        { "from": "POLISHING", "to": "FINISHED" },
        { "from": "UPHOLSTERY", "to": "FINISHED" }
      ]
    }
    """;

    private static WorkflowTemplate V3() => WorkflowTemplate.Parse(V3Json);

    private bool Allowed(string from, string to, string method) =>
        _validator.Validate(V3(), from, to, AnyRole, method: method).IsAllowed;

    // ---------- Factory path unchanged ----------

    [Fact]
    public void FactoryItemsStillWalkTheirOriginalPath()
    {
        Assert.True(Allowed("PENDING", "CARPENTRY", Factory));
        Assert.True(Allowed("CARPENTRY", "UPHOLSTERY", Factory));
        Assert.True(Allowed("UPHOLSTERY", "FINISHED", Factory));
    }

    [Fact]
    public void FactoryItemsCannotGoToASupplier()
    {
        Assert.False(Allowed("PENDING", "WITH_SUPPLIER", Factory));
    }

    // ---------- Outsource / import path ----------

    [Theory]
    [InlineData(Outsource)]
    [InlineData(Import)]
    public void OutsourcedItemsGoOutToTheSupplier(string method)
    {
        Assert.True(Allowed("PENDING", "WITH_SUPPLIER", method));
    }

    [Theory]
    [InlineData(Outsource)]
    [InlineData(Import)]
    public void OutsourcedItemsCannotSkipTheirSupplierStage(string method)
    {
        // The whole point of restricting PENDING -> factory steps to method=factory.
        Assert.False(Allowed("PENDING", "CARPENTRY", method));
        Assert.False(Allowed("PENDING", "POLISHING", method));
        Assert.False(Allowed("PENDING", "UPHOLSTERY", method));
    }

    [Theory]
    [InlineData(Outsource)]
    [InlineData(Import)]
    public void FinishedGoodsComeBackDone(string method)
    {
        Assert.True(Allowed("WITH_SUPPLIER", "FINISHED", method));
    }

    [Fact]
    public void OutsourcedGoodsMayComeBackNeedingWork()
    {
        // An outsourcing supplier may do only part of the job.
        Assert.True(Allowed("WITH_SUPPLIER", "SEMI_FINISHED", Outsource));
    }

    [Fact]
    public void ImportedGoodsAreNeverSemiFinished()
    {
        // An import always arrives complete. Letting one in would strand it: v3 gives
        // imports no route out of SEMI_FINISHED.
        Assert.False(Allowed("WITH_SUPPLIER", "SEMI_FINISHED", Import));
    }

    [Fact]
    public void FactoryItemsAreNeverSemiFinishedEither()
    {
        // A part-built factory item is work in progress sitting on a production step,
        // not a returned semi-finished state.
        foreach (var from in new[] { "PENDING", "CARPENTRY", "POLISHING", "UPHOLSTERY" })
        {
            Assert.False(Allowed(from, "SEMI_FINISHED", Factory), $"factory {from} -> SEMI_FINISHED");
        }
    }

    // ---------- The re-entry the wireframes describe ----------

    [Theory]
    [InlineData("CARPENTRY")]
    [InlineData("POLISHING")]
    [InlineData("UPHOLSTERY")]
    public void SemiFinishedItemsReEnterTheFactoryChecklistAtAnyStep(string step)
    {
        // Outsourcing only — the only route into SEMI_FINISHED is also the only route out.
        Assert.True(Allowed("SEMI_FINISHED", step, Outsource));
        Assert.False(Allowed("SEMI_FINISHED", step, Import));
    }

    [Theory]
    [InlineData(Outsource)]
    [InlineData(Import)]
    public void OnceBackInTheChecklistTheyBehaveExactlyLikeFactoryItems(string method)
    {
        // The onward edges carry no method restriction, which is what makes the rejoin
        // work without duplicating the factory chain for each method.
        Assert.True(Allowed("CARPENTRY", "POLISHING", method));
        Assert.True(Allowed("POLISHING", "UPHOLSTERY", method));
        Assert.True(Allowed("UPHOLSTERY", "FINISHED", method));
        Assert.True(Allowed("CARPENTRY", "FINISHED", method));
    }

    [Theory]
    [InlineData(Outsource)]
    [InlineData(Import)]
    public void SemiFinishedItemsCannotDeclareThemselvesFinishedWithoutDoingTheWork(string method)
    {
        Assert.False(Allowed("SEMI_FINISHED", "FINISHED", method));
    }

    // ---------- The completeness gate still works ----------

    [Fact]
    public void FinishedIsStillTheOnlyTerminalStatus()
    {
        // LineItemCompletion derives its terminal set from here, so an outsourced item
        // counts as complete on exactly the same condition as a factory one.
        Assert.Equal(["FINISHED"], V3().TerminalStatuses);
    }

    [Fact]
    public void AnItemStillWithASupplierDoesNotCountAsComplete()
    {
        var terminal = V3().TerminalStatuses;

        Assert.False(LineItemCompletion.AllComplete(["FINISHED", "WITH_SUPPLIER"], terminal));
        Assert.False(LineItemCompletion.AllComplete(["SEMI_FINISHED"], terminal));
    }

    [Fact]
    public void AMixOfFactoryAndOutsourcedItemsCompletesOnlyWhenAllReachFinished()
    {
        var terminal = V3().TerminalStatuses;

        Assert.True(LineItemCompletion.AllComplete(["FINISHED", "FINISHED"], terminal));
        Assert.False(LineItemCompletion.AllComplete(["FINISHED", "CARPENTRY"], terminal));
    }

    // ---------- Shape ----------

    [Fact]
    public void NoMethodCanReachAStatusItShouldNot()
    {
        // Factory items should never touch the supplier statuses at all.
        foreach (var from in new[] { "PENDING", "CARPENTRY", "POLISHING", "UPHOLSTERY", "FINISHED" })
        {
            Assert.False(Allowed(from, "WITH_SUPPLIER", Factory), $"factory {from} -> WITH_SUPPLIER");
            Assert.False(Allowed(from, "SEMI_FINISHED", Factory), $"factory {from} -> SEMI_FINISHED");
        }
    }

    [Fact]
    public void AnItemWithNoMethodSetCannotStartDownEitherBranch()
    {
        // Method is chosen on the production plan; until then the item goes nowhere.
        var decision = _validator.Validate(V3(), "PENDING", "CARPENTRY", AnyRole, method: null);

        Assert.False(decision.IsAllowed);
        Assert.Equal(TransitionOutcome.MethodNotPermitted, decision.Outcome);
    }
}
