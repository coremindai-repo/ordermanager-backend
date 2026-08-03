using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrderManager.Backend.Lib.Workflow;

public sealed record StatusDefinition
{
    public required string Code { get; init; }
    public required string Name { get; init; }

    /// <summary>
    /// Whether a supervisor may choose this as a production step on the "this item will
    /// require" checklist. Only meaningful on production step templates.
    ///
    /// Most statuses are NOT selectable: PENDING, WITH_SUPPLIER, SEMI_FINISHED and
    /// FINISHED are lifecycle states the system sets — WITH_SUPPLIER and SEMI_FINISHED
    /// come from the outsourcing flow, FINISHED from a line-item transition. Only the
    /// genuine units of factory work are selectable.
    ///
    /// Defaults to FALSE deliberately. A template that forgets the flag yields an empty
    /// checklist — visibly broken — rather than silently offering lifecycle statuses as
    /// if they were work.
    /// </summary>
    public bool SelectableAsStep { get; init; }
}

/// <summary>
/// One legal move. Every legal move is an explicit edge — which is what makes
/// "skipping a stage" illegal (no edge exists) and "moving backward" illegal
/// unless an edge exists with <see cref="Revert"/> set.
/// </summary>
public sealed record TransitionRule
{
    public required string From { get; init; }
    public required string To { get; init; }

    /// <summary>Marks this edge as a deliberate backward move (CLAUDE.md §5).</summary>
    public bool Revert { get; init; }

    /// <summary>
    /// Roles permitted to perform this transition. Null or empty means no role
    /// restriction — any authenticated caller may perform it.
    /// </summary>
    public IReadOnlyList<string>? AllowedRoles { get; init; }

    /// <summary>
    /// Line-item methods (factory/outsource/import) this edge applies to. Null or
    /// empty means it applies regardless of method. Only meaningful for production
    /// step templates; order-level templates leave it unset.
    /// </summary>
    public IReadOnlyList<string>? Methods { get; init; }

    /// <summary>
    /// Order-level gate: this transition is refused unless every line item on the
    /// order has reached a terminal production status. An order ships as one unit —
    /// it only leaves the factory once all of its items are finished — so the check
    /// is across the whole order, not within a single line item.
    ///
    /// Which transitions carry the gate is template config, not code, so a client
    /// whose process differs does not need a code change.
    /// </summary>
    public bool RequiresAllLineItemsComplete { get; init; }

    /// <summary>
    /// Order-level gate: refused unless the order has a destination store set
    /// (`orders.store_id`). Statuses stay generic — "In Transit", never "Sent to
    /// Kochi" — so the destination is carried as a field rather than multiplied into
    /// the status list every time a store is added. That only works if the field is
    /// actually populated before the goods move, which is what this enforces.
    /// </summary>
    public bool RequiresDestinationStore { get; init; }

    /// <summary>
    /// Order types (customer/stock) this edge applies to. Null or empty means it
    /// applies to both. Used to branch the process: invoicing applies only to
    /// customer orders, so stock orders route around it entirely.
    ///
    /// The order-level counterpart of <see cref="Methods"/>.
    /// </summary>
    public IReadOnlyList<string>? OrderTypes { get; init; }

    /// <summary>
    /// Notification-worthy event fired after this transition commits — one of the push
    /// types in API-INTERFACE-CONTRACT.md §11 (e.g. "invoice_ready"). Null means the
    /// transition notifies nobody.
    ///
    /// Kept in the template so that which status hands off to whom is config, not
    /// code: a client whose invoicing trigger is a different status needs no change
    /// here. Who receives it is separately configurable in notification_recipients.
    /// </summary>
    public string? NotifyEvent { get; init; }
}

public sealed record WorkflowTemplate
{
    public required string InitialStatus { get; init; }
    public required IReadOnlyList<StatusDefinition> Statuses { get; init; }
    public required IReadOnlyList<TransitionRule> Transitions { get; init; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The statuses a supervisor may actually pick as production steps — the "this item
    /// will require" checklist, and the only values accepted by the production plan.
    /// </summary>
    public IReadOnlyList<StatusDefinition> SelectableSteps =>
        Statuses.Where(s => s.SelectableAsStep).ToList();

    /// <summary>
    /// Statuses with no outgoing transition — the end of the line. Derived from the
    /// transition graph rather than named explicitly, so a template that adds a new
    /// final stage does not also have to remember to declare it terminal.
    /// </summary>
    public IReadOnlySet<string> TerminalStatuses =>
        Statuses
            .Select(s => s.Code)
            .Where(code => !Transitions.Any(t => string.Equals(t.From, code, StringComparison.OrdinalIgnoreCase)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the template's canonical casing for a status code. Callers may send
    /// any casing; what gets persisted to the entity and its history should always be
    /// the template's own spelling, so stored statuses stay consistent.
    /// </summary>
    public string ResolveStatusCode(string code) =>
        Statuses.First(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase)).Code;

    public static WorkflowTemplate Parse(string templateJson)
    {
        var template = JsonSerializer.Deserialize<WorkflowTemplate>(templateJson, SerializerOptions)
            ?? throw new InvalidOperationException("template_json deserialized to null");

        if (template.Statuses.Count == 0)
        {
            throw new InvalidOperationException("Template defines no statuses");
        }

        return template;
    }
}
