using OrderManager.Backend.Lib.Workflow;

namespace OrderManager.Backend.Tests;

public class TransitionValidatorTests
{
    private readonly TransitionValidator _validator = new();

    private static readonly string[] NoRoles = [];
    private static readonly string[] AnyRole = ["salesperson"];

    private static WorkflowTemplate Build(string[] statuses, params TransitionRule[] transitions) => new()
    {
        InitialStatus = statuses[0],
        Statuses = [.. statuses.Select(code => new StatusDefinition { Code = code, Name = code })],
        Transitions = transitions,
    };

    /// <summary>A → B → C, with one explicit revert edge C → B.</summary>
    private static WorkflowTemplate LinearWithRevert() => Build(
        ["A", "B", "C"],
        new TransitionRule { From = "A", To = "B" },
        new TransitionRule { From = "B", To = "C" },
        new TransitionRule { From = "C", To = "B", Revert = true });

    // ---------- Core legality ----------

    [Fact]
    public void Allows_LegalForwardTransition()
    {
        var decision = _validator.Validate(LinearWithRevert(), "A", "B", AnyRole);

        Assert.True(decision.IsAllowed);
        Assert.Equal(TransitionOutcome.Allowed, decision.Outcome);
    }

    [Fact]
    public void Rejects_SkippingAStage()
    {
        var decision = _validator.Validate(LinearWithRevert(), "A", "C", AnyRole);

        Assert.False(decision.IsAllowed);
        Assert.Equal(TransitionOutcome.TransitionNotAllowed, decision.Outcome);
    }

    [Fact]
    public void Rejects_BackwardTransition_WithoutRevertAllowance()
    {
        // B → A is backward and has no edge at all.
        var decision = _validator.Validate(LinearWithRevert(), "B", "A", AnyRole);

        Assert.Equal(TransitionOutcome.TransitionNotAllowed, decision.Outcome);
    }

    [Fact]
    public void Allows_BackwardTransition_WithExplicitRevertAllowance()
    {
        var decision = _validator.Validate(LinearWithRevert(), "C", "B", AnyRole);

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public void Rejects_TransitionToSameStatus()
    {
        var decision = _validator.Validate(LinearWithRevert(), "B", "B", AnyRole);

        Assert.Equal(TransitionOutcome.TransitionNotAllowed, decision.Outcome);
    }

    [Fact]
    public void Rejects_AnyTransitionOutOfTerminalStatus()
    {
        // C's only outgoing edge is the revert to B; nothing leads onward.
        var decision = _validator.Validate(LinearWithRevert(), "C", "A", AnyRole);

        Assert.Equal(TransitionOutcome.TransitionNotAllowed, decision.Outcome);
    }

    // ---------- Unknown statuses ----------

    [Fact]
    public void Rejects_UnknownTargetStatus()
    {
        var decision = _validator.Validate(LinearWithRevert(), "A", "NOPE", AnyRole);

        Assert.Equal(TransitionOutcome.UnknownTargetStatus, decision.Outcome);
    }

    [Fact]
    public void Rejects_UnknownCurrentStatus()
    {
        // Entity is parked in a status this template does not define.
        var decision = _validator.Validate(LinearWithRevert(), "LEGACY", "B", AnyRole);

        Assert.Equal(TransitionOutcome.UnknownCurrentStatus, decision.Outcome);
    }

    [Fact]
    public void UnknownTargetStatus_IsReportedBefore_UnknownCurrentStatus()
    {
        // Ordering is deliberate: the target is client-supplied, so a bad target is
        // the more actionable error to surface.
        var decision = _validator.Validate(LinearWithRevert(), "LEGACY", "NOPE", AnyRole);

        Assert.Equal(TransitionOutcome.UnknownTargetStatus, decision.Outcome);
    }

    // ---------- Role gating ----------

    [Fact]
    public void Allows_AnyRole_WhenAllowedRolesOmitted()
    {
        var template = Build(["A", "B"], new TransitionRule { From = "A", To = "B" });

        Assert.True(_validator.Validate(template, "A", "B", ["factory_supervisor"]).IsAllowed);
    }

    [Fact]
    public void Allows_CallerWithNoRolesAtAll_WhenAllowedRolesOmitted()
    {
        // Omitted allowedRoles means "any authenticated role", not "deny all".
        var template = Build(["A", "B"], new TransitionRule { From = "A", To = "B" });

        Assert.True(_validator.Validate(template, "A", "B", NoRoles).IsAllowed);
    }

    [Fact]
    public void Allows_WhenAllowedRolesIsEmptyArray()
    {
        var template = Build(
            ["A", "B"],
            new TransitionRule { From = "A", To = "B", AllowedRoles = [] });

        Assert.True(_validator.Validate(template, "A", "B", NoRoles).IsAllowed);
    }

    [Fact]
    public void Allows_WhenCallerHoldsTheAllowedRole()
    {
        var template = Build(
            ["A", "B"],
            new TransitionRule { From = "A", To = "B", AllowedRoles = ["store_manager"] });

        Assert.True(_validator.Validate(template, "A", "B", ["store_manager"]).IsAllowed);
    }

    [Fact]
    public void Allows_WhenCallerHoldsOneOfSeveralRoles()
    {
        var template = Build(
            ["A", "B"],
            new TransitionRule { From = "A", To = "B", AllowedRoles = ["company_manager"] });

        Assert.True(_validator.Validate(template, "A", "B", ["salesperson", "company_manager"]).IsAllowed);
    }

    [Fact]
    public void Rejects_WhenCallerLacksEveryAllowedRole()
    {
        var template = Build(
            ["A", "B"],
            new TransitionRule { From = "A", To = "B", AllowedRoles = ["store_manager", "company_manager"] });

        var decision = _validator.Validate(template, "A", "B", ["salesperson"]);

        Assert.Equal(TransitionOutcome.RoleNotPermitted, decision.Outcome);
    }

    // ---------- Method gating (line items) ----------

    [Fact]
    public void Allows_AnyMethod_WhenMethodsOmitted()
    {
        var template = Build(["A", "B"], new TransitionRule { From = "A", To = "B" });

        Assert.True(_validator.Validate(template, "A", "B", AnyRole, "import").IsAllowed);
    }

    [Fact]
    public void Allows_WhenMethodMatches()
    {
        var template = Build(
            ["A", "B"],
            new TransitionRule { From = "A", To = "B", Methods = ["factory"] });

        Assert.True(_validator.Validate(template, "A", "B", AnyRole, "factory").IsAllowed);
    }

    [Fact]
    public void Rejects_WhenMethodDoesNotMatch()
    {
        var template = Build(
            ["A", "B"],
            new TransitionRule { From = "A", To = "B", Methods = ["factory"] });

        var decision = _validator.Validate(template, "A", "B", AnyRole, "outsource");

        Assert.Equal(TransitionOutcome.MethodNotPermitted, decision.Outcome);
    }

    [Fact]
    public void Rejects_WhenEdgeRestrictsMethod_ButCallerSuppliedNone()
    {
        // A method-restricted edge can never be satisfied by an entity with no method.
        var template = Build(
            ["A", "B"],
            new TransitionRule { From = "A", To = "B", Methods = ["factory"] });

        var decision = _validator.Validate(template, "A", "B", AnyRole, method: null);

        Assert.Equal(TransitionOutcome.MethodNotPermitted, decision.Outcome);
    }

    // ---------- Several edges for the same move ----------

    [Fact]
    public void Allows_WhenAnyOneOfSeveralEdgesPermits()
    {
        var template = Build(
            ["A", "B"],
            new TransitionRule { From = "A", To = "B", AllowedRoles = ["store_manager"] },
            new TransitionRule { From = "A", To = "B", AllowedRoles = ["factory_supervisor"] });

        Assert.True(_validator.Validate(template, "A", "B", ["factory_supervisor"]).IsAllowed);
    }

    [Fact]
    public void Reports_RoleNotPermitted_WhenMethodAppliesButRolesDoNot()
    {
        var template = Build(
            ["A", "B"],
            new TransitionRule { From = "A", To = "B", Methods = ["factory"], AllowedRoles = ["store_manager"] });

        var decision = _validator.Validate(template, "A", "B", ["salesperson"], "factory");

        Assert.Equal(TransitionOutcome.RoleNotPermitted, decision.Outcome);
    }

    [Fact]
    public void Reports_MethodNotPermitted_WhenMethodExcludesTheOnlyRoleMatchingEdge()
    {
        var template = Build(
            ["A", "B"],
            new TransitionRule { From = "A", To = "B", Methods = ["factory"], AllowedRoles = ["store_manager"] });

        var decision = _validator.Validate(template, "A", "B", ["store_manager"], "import");

        Assert.Equal(TransitionOutcome.MethodNotPermitted, decision.Outcome);
    }

    // ---------- Case handling ----------

    [Theory]
    [InlineData("a", "B")]
    [InlineData("A", "b")]
    [InlineData("a", "b")]
    public void StatusCodes_AreMatchedCaseInsensitively(string current, string target)
    {
        var template = Build(["A", "B"], new TransitionRule { From = "A", To = "B" });

        Assert.True(_validator.Validate(template, current, target, AnyRole).IsAllowed);
    }

    [Fact]
    public void Roles_AreMatchedCaseInsensitively()
    {
        var template = Build(
            ["A", "B"],
            new TransitionRule { From = "A", To = "B", AllowedRoles = ["Store_Manager"] });

        Assert.True(_validator.Validate(template, "A", "B", ["store_manager"]).IsAllowed);
    }

    [Fact]
    public void Methods_AreMatchedCaseInsensitively()
    {
        var template = Build(
            ["A", "B"],
            new TransitionRule { From = "A", To = "B", Methods = ["Factory"] });

        Assert.True(_validator.Validate(template, "A", "B", AnyRole, "FACTORY").IsAllowed);
    }
}
