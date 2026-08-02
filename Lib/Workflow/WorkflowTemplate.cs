using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrderManager.Backend.Lib.Workflow;

public sealed record StatusDefinition
{
    public required string Code { get; init; }
    public required string Name { get; init; }
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
