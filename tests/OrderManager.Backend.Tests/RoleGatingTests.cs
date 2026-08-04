using OrderManager.Backend.Lib;
using OrderManager.Backend.Lib.Workflow;

namespace OrderManager.Backend.Tests;

/// <summary>
/// Role gating on transitions, per the client's confirmed mapping. Mirrors
/// sql/017_process_template_v6.sql and sql/018_production_step_template_v4.sql.
///
/// Contract §3 is explicit that the server is the source of truth here — the app hiding
/// a button is a UX convenience — so a gap in this file is a gap in the only control
/// that exists.
/// </summary>
public class RoleGatingTests
{
    private readonly TransitionValidator _validator = new();

    private const string Sales = "salesperson";
    private const string Factory = "factory_supervisor";
    private const string Store = "store_manager";
    private const string Company = "company_manager";

    // ---------- Process template v6 ----------

    private const string ProcessJson = """
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
        { "from": "NEW", "to": "IN_PRODUCTION", "allowedRoles": ["salesperson", "company_manager"] },
        { "from": "IN_PRODUCTION", "to": "READY_TO_INVOICE", "orderTypes": ["customer"],
          "requiresAllLineItemsComplete": true, "notifyEvent": "invoice_ready",
          "allowedRoles": ["factory_supervisor"] },
        { "from": "READY_TO_INVOICE", "to": "READY_TO_DELIVER", "orderTypes": ["customer"],
          "allowedRoles": ["store_manager", "company_manager"] },
        { "from": "READY_TO_DELIVER", "to": "KEEP_IN_FACTORY", "orderTypes": ["customer"],
          "allowedRoles": ["factory_supervisor"] },
        { "from": "READY_TO_DELIVER", "to": "SENT_TO_WAREHOUSE", "orderTypes": ["customer"],
          "allowedRoles": ["factory_supervisor"] },
        { "from": "IN_PRODUCTION", "to": "KEEP_IN_FACTORY", "orderTypes": ["stock"],
          "requiresAllLineItemsComplete": true, "allowedRoles": ["factory_supervisor"] },
        { "from": "IN_PRODUCTION", "to": "SENT_TO_WAREHOUSE", "orderTypes": ["stock"],
          "requiresAllLineItemsComplete": true, "allowedRoles": ["factory_supervisor"] },
        { "from": "KEEP_IN_FACTORY", "to": "SENT_TO_WAREHOUSE", "allowedRoles": ["factory_supervisor"] },
        { "from": "KEEP_IN_FACTORY", "to": "IN_TRANSIT", "requiresDestinationStore": true,
          "allowedRoles": ["factory_supervisor"] },
        { "from": "SENT_TO_WAREHOUSE", "to": "IN_TRANSIT", "requiresDestinationStore": true,
          "allowedRoles": ["factory_supervisor"] },
        { "from": "IN_TRANSIT", "to": "SENT_TO_STORE", "allowedRoles": ["store_manager", "company_manager"] },
        { "from": "SENT_TO_STORE", "to": "RECEIVED_IN_STORE", "allowedRoles": ["store_manager", "company_manager"] },
        { "from": "RECEIVED_IN_STORE", "to": "OUT_FOR_DELIVERY", "allowedRoles": ["store_manager", "company_manager"] },
        { "from": "OUT_FOR_DELIVERY", "to": "DELIVERED", "allowedRoles": ["store_manager", "company_manager"] },
        { "from": "KEEP_IN_FACTORY", "to": "IN_PRODUCTION", "revert": true, "allowedRoles": ["factory_supervisor"] },
        { "from": "SENT_TO_WAREHOUSE", "to": "IN_PRODUCTION", "revert": true, "allowedRoles": ["factory_supervisor"] },
        { "from": "IN_TRANSIT", "to": "SENT_TO_WAREHOUSE", "revert": true, "allowedRoles": ["factory_supervisor"] },
        { "from": "SENT_TO_STORE", "to": "IN_TRANSIT", "revert": true, "allowedRoles": ["store_manager", "company_manager"] },
        { "from": "RECEIVED_IN_STORE", "to": "SENT_TO_STORE", "revert": true, "allowedRoles": ["store_manager", "company_manager"] },
        { "from": "OUT_FOR_DELIVERY", "to": "RECEIVED_IN_STORE", "revert": true, "allowedRoles": ["store_manager", "company_manager"] }
      ]
    }
    """;

    private const string ProductionJson = """
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
        { "from": "PENDING", "to": "CARPENTRY", "methods": ["factory"], "allowedRoles": ["factory_supervisor"] },
        { "from": "PENDING", "to": "POLISHING", "methods": ["factory"], "allowedRoles": ["factory_supervisor"] },
        { "from": "PENDING", "to": "UPHOLSTERY", "methods": ["factory"], "allowedRoles": ["factory_supervisor"] },
        { "from": "PENDING", "to": "WITH_SUPPLIER", "methods": ["outsource", "import"], "allowedRoles": ["company_manager"] },
        { "from": "WITH_SUPPLIER", "to": "FINISHED", "methods": ["outsource", "import"], "allowedRoles": ["company_manager"] },
        { "from": "WITH_SUPPLIER", "to": "SEMI_FINISHED", "methods": ["outsource"], "allowedRoles": ["company_manager"] },
        { "from": "SEMI_FINISHED", "to": "CARPENTRY", "methods": ["outsource"], "allowedRoles": ["factory_supervisor"] },
        { "from": "SEMI_FINISHED", "to": "POLISHING", "methods": ["outsource"], "allowedRoles": ["factory_supervisor"] },
        { "from": "SEMI_FINISHED", "to": "UPHOLSTERY", "methods": ["outsource"], "allowedRoles": ["factory_supervisor"] },
        { "from": "CARPENTRY", "to": "POLISHING", "allowedRoles": ["factory_supervisor"] },
        { "from": "CARPENTRY", "to": "UPHOLSTERY", "allowedRoles": ["factory_supervisor"] },
        { "from": "CARPENTRY", "to": "FINISHED", "allowedRoles": ["factory_supervisor"] },
        { "from": "POLISHING", "to": "UPHOLSTERY", "allowedRoles": ["factory_supervisor"] },
        { "from": "POLISHING", "to": "FINISHED", "allowedRoles": ["factory_supervisor"] },
        { "from": "UPHOLSTERY", "to": "FINISHED", "allowedRoles": ["factory_supervisor"] }
      ]
    }
    """;

    private static WorkflowTemplate Process() => WorkflowTemplate.Parse(ProcessJson);
    private static WorkflowTemplate Production() => WorkflowTemplate.Parse(ProductionJson);

    private TransitionDecision Order(string from, string to, string role, string orderType = "stock") =>
        _validator.Validate(Process(), from, to, [role], orderType: orderType);

    private TransitionDecision Item(string from, string to, string role, string method = "factory") =>
        _validator.Validate(Production(), from, to, [role], method: method);

    // ---------- Every gated transition: allowed role succeeds, others get 403 ----------

    public static TheoryData<string, string, string, string, string[]> OrderTransitions() => new()
    {
        // from, to, orderType, permittedRole, deniedRoles
        { "NEW", "IN_PRODUCTION", "stock", Sales, [Factory, Store] },
        { "NEW", "IN_PRODUCTION", "stock", Company, [Factory, Store] },
        { "IN_PRODUCTION", "READY_TO_INVOICE", "customer", Factory, [Sales, Store, Company] },
        { "READY_TO_INVOICE", "READY_TO_DELIVER", "customer", Store, [Sales, Factory] },
        { "READY_TO_INVOICE", "READY_TO_DELIVER", "customer", Company, [Sales, Factory] },
        { "IN_PRODUCTION", "KEEP_IN_FACTORY", "stock", Factory, [Sales, Store, Company] },
        { "IN_PRODUCTION", "SENT_TO_WAREHOUSE", "stock", Factory, [Sales, Store, Company] },
        { "KEEP_IN_FACTORY", "SENT_TO_WAREHOUSE", "stock", Factory, [Sales, Store, Company] },
        { "KEEP_IN_FACTORY", "IN_TRANSIT", "stock", Factory, [Sales, Store, Company] },
        { "SENT_TO_WAREHOUSE", "IN_TRANSIT", "stock", Factory, [Sales, Store, Company] },
        { "IN_TRANSIT", "SENT_TO_STORE", "stock", Store, [Sales, Factory] },
        { "SENT_TO_STORE", "RECEIVED_IN_STORE", "stock", Company, [Sales, Factory] },
        { "RECEIVED_IN_STORE", "OUT_FOR_DELIVERY", "stock", Store, [Sales, Factory] },
        { "OUT_FOR_DELIVERY", "DELIVERED", "stock", Company, [Sales, Factory] },
    };

    [Theory]
    [MemberData(nameof(OrderTransitions))]
    public void OrderTransitionsAreGatedToTheRightRoles(
        string from, string to, string orderType, string permitted, string[] denied)
    {
        Assert.True(Order(from, to, permitted, orderType).IsAllowed,
            $"{permitted} should be able to move {from} -> {to}");

        foreach (var role in denied)
        {
            var decision = Order(from, to, role, orderType);
            Assert.Equal(TransitionOutcome.RoleNotPermitted, decision.Outcome);
            Assert.Equal(403, TransitionOutcomeMapper.ToException(decision).StatusCode);
        }
    }

    // ---------- Reverts specifically ----------

    public static TheoryData<string, string, string, string[]> Reverts() => new()
    {
        // A revert carries the same roles as the forward move it undoes.
        { "KEEP_IN_FACTORY", "IN_PRODUCTION", Factory, [Sales, Store, Company] },
        { "SENT_TO_WAREHOUSE", "IN_PRODUCTION", Factory, [Sales, Store, Company] },
        { "IN_TRANSIT", "SENT_TO_WAREHOUSE", Factory, [Sales, Store, Company] },
        { "SENT_TO_STORE", "IN_TRANSIT", Store, [Sales, Factory] },
        { "RECEIVED_IN_STORE", "SENT_TO_STORE", Company, [Sales, Factory] },
        { "OUT_FOR_DELIVERY", "RECEIVED_IN_STORE", Store, [Sales, Factory] },
    };

    [Theory]
    [MemberData(nameof(Reverts))]
    public void RevertsAreGatedToo(string from, string to, string permitted, string[] denied)
    {
        // Easy to leave ungated: reverts are the edges least exercised in testing, and
        // an ungated revert lets anyone undo work they were not allowed to do.
        Assert.True(Order(from, to, permitted).IsAllowed);

        foreach (var role in denied)
        {
            Assert.Equal(TransitionOutcome.RoleNotPermitted, Order(from, to, role).Outcome);
        }
    }

    [Fact]
    public void EveryRevertCarriesTheSameRolesAsTheMoveItUndoes()
    {
        var template = Process();

        foreach (var revert in template.Transitions.Where(t => t.Revert))
        {
            var forward = template.Transitions.FirstOrDefault(t =>
                !t.Revert &&
                string.Equals(t.From, revert.To, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.To, revert.From, StringComparison.OrdinalIgnoreCase));

            if (forward is null)
            {
                continue; // no direct forward counterpart to mirror
            }

            Assert.Equal(
                forward.AllowedRoles?.OrderBy(r => r),
                revert.AllowedRoles?.OrderBy(r => r));
        }
    }

    // ---------- Production steps ----------

    [Theory]
    [InlineData("PENDING", "CARPENTRY")]
    [InlineData("CARPENTRY", "POLISHING")]
    [InlineData("POLISHING", "UPHOLSTERY")]
    [InlineData("UPHOLSTERY", "FINISHED")]
    [InlineData("CARPENTRY", "FINISHED")]
    public void FactoryStepsAreFactorySupervisorOnly(string from, string to)
    {
        Assert.True(Item(from, to, Factory).IsAllowed);

        foreach (var role in new[] { Sales, Store, Company })
        {
            Assert.Equal(TransitionOutcome.RoleNotPermitted, Item(from, to, role).Outcome);
        }
    }

    [Fact]
    public void SemiFinishedReEntryIsFactorySupervisorOnly()
    {
        Assert.True(Item("SEMI_FINISHED", "CARPENTRY", Factory, "outsource").IsAllowed);
        Assert.Equal(TransitionOutcome.RoleNotPermitted,
            Item("SEMI_FINISHED", "CARPENTRY", Company, "outsource").Outcome);
    }

    // ---------- The coupling that would fail silently ----------

    [Theory]
    [InlineData("PENDING", "WITH_SUPPLIER", "outsource")]
    [InlineData("WITH_SUPPLIER", "FINISHED", "outsource")]
    [InlineData("WITH_SUPPLIER", "SEMI_FINISHED", "outsource")]
    [InlineData("PENDING", "WITH_SUPPLIER", "import")]
    [InlineData("WITH_SUPPLIER", "FINISHED", "import")]
    public void SupplierEdgesMustPermitCompanyManager(string from, string to, string method)
    {
        // These line-item moves are driven by the outsourcing request endpoints, which
        // are company_manager's. The auto-advance skips and logs on refusal rather than
        // failing, so gating these to factory_supervisor would leave every linked item
        // behind while the request still reported success.
        Assert.True(Item(from, to, Company, method).IsAllowed,
            $"{from} -> {to} must permit company_manager or the outsourcing auto-advance silently skips");
    }

    [Fact]
    public void SupplierEdgesAreNotOpenToTheFactorySupervisor()
    {
        // The converse: a supervisor does not place or receive outsourcing work.
        Assert.Equal(TransitionOutcome.RoleNotPermitted,
            Item("PENDING", "WITH_SUPPLIER", Factory, "outsource").Outcome);
    }

    // ---------- Multi-role users ----------

    [Fact]
    public void AUserHoldingBothRolesCanDoBoth()
    {
        var both = new[] { Factory, Store };

        Assert.True(_validator.Validate(Process(), "IN_PRODUCTION", "KEEP_IN_FACTORY", both, orderType: "stock").IsAllowed);
        Assert.True(_validator.Validate(Process(), "IN_TRANSIT", "SENT_TO_STORE", both, orderType: "stock").IsAllowed);
    }

    [Fact]
    public void NoTransitionIsLeftUngated()
    {
        // The gap this whole change closes: an unlisted transition is open to everyone.
        foreach (var template in new[] { Process(), Production() })
        {
            var ungated = template.Transitions
                .Where(t => t.AllowedRoles is null or { Count: 0 })
                .Select(t => $"{t.From}->{t.To}")
                .ToList();

            Assert.Empty(ungated);
        }
    }
}

/// <summary>
/// The hard-coded sub-processes (raw materials, outsourcing) have no template, so their
/// gating lives in the endpoints via <see cref="AuthHelper.RequireRole"/>.
/// </summary>
public class RequireRoleTests
{
    private static Caller With(params string[] roles) => new(Guid.NewGuid(), roles);

    [Fact]
    public void PermitsACallerHoldingAnAllowedRole()
    {
        AuthHelper.RequireRole(With("store_manager"), "store_manager", "company_manager");
    }

    [Fact]
    public void PermitsWhenOnlyOneOfSeveralRolesMatches()
    {
        AuthHelper.RequireRole(With("salesperson", "company_manager"), "company_manager");
    }

    [Fact]
    public void RejectsWithForbiddenNotUnauthorized()
    {
        // 403, not 401 — the caller is authenticated, just not permitted.
        var exception = Assert.Throws<AppException>(() =>
            AuthHelper.RequireRole(With("salesperson"), "store_manager", "company_manager"));

        Assert.Equal(403, exception.StatusCode);
        Assert.Equal("FORBIDDEN", exception.Code);
    }

    [Fact]
    public void RejectsACallerWithNoRoles()
    {
        Assert.Throws<AppException>(() => AuthHelper.RequireRole(With(), "company_manager"));
    }

    [Fact]
    public void MatchesRolesCaseInsensitively()
    {
        AuthHelper.RequireRole(With("Company_Manager"), "company_manager");
    }

    [Fact]
    public void TheMessageNamesWhatIsRequired()
    {
        var exception = Assert.Throws<AppException>(() =>
            AuthHelper.RequireRole(With("salesperson"), "store_manager", "company_manager"));

        Assert.Contains("store_manager", exception.Message);
        Assert.Contains("company_manager", exception.Message);
    }
}

/// <summary>
/// Outsourcing/import is company_manager territory per contract §3, with no per-record
/// nuance like raw materials' item-linked-vs-standalone split — so List, Create and
/// UpdateStatus all gate on the same single role via <see cref="AuthHelper.RequireRole"/>.
///
/// List had no gate at all until this was caught: any authenticated user — a salesperson
/// included — could enumerate every outsourcing/import request, supplier names and notes
/// included. This closes that the same way <see cref="RoleGatingTests"/> closes ungated
/// template transitions: enumerate every denied role explicitly rather than trusting a
/// single "it 403s" case.
/// </summary>
public class OutsourcingRequestsAccessTests
{
    private static Caller With(params string[] roles) => new(Guid.NewGuid(), roles);

    public static TheoryData<string> DeniedRoles() => new()
    {
        "salesperson",
        "store_manager",
        "factory_supervisor",
    };

    [Theory]
    [MemberData(nameof(DeniedRoles))]
    public void NonCompanyManagerRolesAreForbidden(string role)
    {
        var exception = Assert.Throws<AppException>(() =>
            AuthHelper.RequireRole(With(role), "company_manager"));

        Assert.Equal(403, exception.StatusCode);
        Assert.Equal("FORBIDDEN", exception.Code);
    }

    [Fact]
    public void CompanyManagerIsPermitted()
    {
        AuthHelper.RequireRole(With("company_manager"), "company_manager");
    }

    [Fact]
    public void AUserHoldingCompanyManagerAlongsideAnotherRoleIsStillPermitted()
    {
        AuthHelper.RequireRole(With("store_manager", "company_manager"), "company_manager");
    }
}

/// <summary>
/// POST /api/orders had no role check at all until the endpoint-access sweep (CLAUDE.md
/// §2) found it. Contract §3's visibility table lists "order creation" only under
/// salesperson; the closest action-table row (NEW -> IN_PRODUCTION) adds company_manager.
/// Neither factory_supervisor nor store_manager is ever named as able to raise an order.
/// </summary>
public class CreateOrderAccessTests
{
    private static Caller With(params string[] roles) => new(Guid.NewGuid(), roles);

    public static TheoryData<string> PermittedRoles() => new() { "salesperson", "company_manager" };

    public static TheoryData<string> DeniedRoles() => new() { "factory_supervisor", "store_manager" };

    [Theory]
    [MemberData(nameof(PermittedRoles))]
    public void SalespersonAndCompanyManagerMayCreateOrders(string role)
    {
        AuthHelper.RequireRole(With(role), "salesperson", "company_manager");
    }

    [Theory]
    [MemberData(nameof(DeniedRoles))]
    public void OtherRolesAreForbiddenFromCreatingOrders(string role)
    {
        var exception = Assert.Throws<AppException>(() =>
            AuthHelper.RequireRole(With(role), "salesperson", "company_manager"));

        Assert.Equal(403, exception.StatusCode);
        Assert.Equal("FORBIDDEN", exception.Code);
    }
}

/// <summary>
/// Three production-side actions the sweep found with no role check at all: production
/// step updates (UpdateProductionStep), destination routing (SetDestinationStore), and
/// production plans (SetProductionPlan). Grouped in one class because it is the same
/// rule enforced three times, not three different rules — CLAUDE.md §5's role table
/// gives all production-side decisions to factory_supervisor, and the SAS-issuing
/// sibling of the step-update endpoint (GetPhotoUploadUrl) already had this exact gate,
/// which is what made the step-update endpoint's gap visible as a gap rather than a
/// design choice.
/// </summary>
public class FactorySupervisorOnlyProductionActionsTests
{
    private static Caller With(params string[] roles) => new(Guid.NewGuid(), roles);

    public static TheoryData<string> DeniedRoles() => new()
    {
        "salesperson",
        "store_manager",
        "company_manager",
    };

    [Theory]
    [MemberData(nameof(DeniedRoles))]
    public void NonFactorySupervisorRolesAreForbidden(string role)
    {
        var exception = Assert.Throws<AppException>(() =>
            AuthHelper.RequireRole(With(role), "factory_supervisor"));

        Assert.Equal(403, exception.StatusCode);
        Assert.Equal("FORBIDDEN", exception.Code);
    }

    [Fact]
    public void FactorySupervisorIsPermitted()
    {
        AuthHelper.RequireRole(With("factory_supervisor"), "factory_supervisor");
    }
}
