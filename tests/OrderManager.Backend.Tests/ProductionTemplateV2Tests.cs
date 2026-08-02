using OrderManager.Backend.Lib.Workflow;

namespace OrderManager.Backend.Tests;

/// <summary>
/// The line-item side of Epic 6. Unlike the request chain this IS templatized, because
/// line-item transitions already validate against production_step_templates and the
/// order-level completeness gate derives its terminal statuses from it.
///
/// Mirrors sql/014_production_step_template_v2.sql.
/// </summary>
public class ProductionTemplateV2Tests
{
    private readonly TransitionValidator _validator = new();
    private static readonly string[] AnyRole = ["factory_supervisor"];

    private const string Factory = "factory";
    private const string Outsource = "outsource";
    private const string Import = "import";

    private const string V2Json = """
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
        { "from": "WITH_SUPPLIER", "to": "SEMI_FINISHED", "methods": ["outsource", "import"] },
        { "from": "SEMI_FINISHED", "to": "CARPENTRY", "methods": ["outsource", "import"] },
        { "from": "SEMI_FINISHED", "to": "POLISHING", "methods": ["outsource", "import"] },
        { "from": "SEMI_FINISHED", "to": "UPHOLSTERY", "methods": ["outsource", "import"] },
        { "from": "CARPENTRY", "to": "POLISHING" },
        { "from": "CARPENTRY", "to": "UPHOLSTERY" },
        { "from": "CARPENTRY", "to": "FINISHED" },
        { "from": "POLISHING", "to": "UPHOLSTERY" },
        { "from": "POLISHING", "to": "FINISHED" },
        { "from": "UPHOLSTERY", "to": "FINISHED" }
      ]
    }
    """;

    private static WorkflowTemplate V2() => WorkflowTemplate.Parse(V2Json);

    private bool Allowed(string from, string to, string method) =>
        _validator.Validate(V2(), from, to, AnyRole, method: method).IsAllowed;

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

    [Theory]
    [InlineData(Outsource)]
    [InlineData(Import)]
    public void SemiFinishedGoodsComeBackNeedingWork(string method)
    {
        Assert.True(Allowed("WITH_SUPPLIER", "SEMI_FINISHED", method));
    }

    // ---------- The re-entry the wireframes describe ----------

    [Theory]
    [InlineData(Outsource, "CARPENTRY")]
    [InlineData(Outsource, "POLISHING")]
    [InlineData(Outsource, "UPHOLSTERY")]
    [InlineData(Import, "CARPENTRY")]
    [InlineData(Import, "POLISHING")]
    [InlineData(Import, "UPHOLSTERY")]
    public void SemiFinishedItemsReEnterTheFactoryChecklistAtAnyStep(string method, string step)
    {
        Assert.True(Allowed("SEMI_FINISHED", step, method));
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
        Assert.Equal(["FINISHED"], V2().TerminalStatuses);
    }

    [Fact]
    public void AnItemStillWithASupplierDoesNotCountAsComplete()
    {
        var terminal = V2().TerminalStatuses;

        Assert.False(LineItemCompletion.AllComplete(["FINISHED", "WITH_SUPPLIER"], terminal));
        Assert.False(LineItemCompletion.AllComplete(["SEMI_FINISHED"], terminal));
    }

    [Fact]
    public void AMixOfFactoryAndOutsourcedItemsCompletesOnlyWhenAllReachFinished()
    {
        var terminal = V2().TerminalStatuses;

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
        var decision = _validator.Validate(V2(), "PENDING", "CARPENTRY", AnyRole, method: null);

        Assert.False(decision.IsAllowed);
        Assert.Equal(TransitionOutcome.MethodNotPermitted, decision.Outcome);
    }
}
