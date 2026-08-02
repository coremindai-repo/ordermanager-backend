namespace OrderManager.Backend.Lib.Workflow;

public enum TransitionOutcome
{
    Allowed,

    /// <summary>Target status is not defined by this template at all — a client bug, so 400.</summary>
    UnknownTargetStatus,

    /// <summary>The entity is sitting in a status this template does not define.</summary>
    UnknownCurrentStatus,

    /// <summary>No edge exists. Covers skipping a stage and un-allowed backward moves.</summary>
    TransitionNotAllowed,

    /// <summary>An edge exists but excludes the item's method.</summary>
    MethodNotPermitted,

    /// <summary>An edge exists and applies, but the caller holds none of its allowed roles.</summary>
    RoleNotPermitted,
}

public sealed record TransitionDecision(TransitionOutcome Outcome, string Message)
{
    public bool IsAllowed => Outcome == TransitionOutcome.Allowed;

    public static TransitionDecision Allow() => new(TransitionOutcome.Allowed, "Transition allowed");
}

/// <summary>
/// Pure transition validation — no SQL, no HTTP, no framework types. Every
/// status-changing endpoint in the system depends on this, so it is deliberately
/// dependency-free and exhaustively unit-tested (CLAUDE.md §5).
/// </summary>
public sealed class TransitionValidator
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <param name="method">
    /// The line item's chosen method, or null for order-level transitions. An edge
    /// that restricts methods can never be satisfied by a null method.
    /// </param>
    public TransitionDecision Validate(
        WorkflowTemplate template,
        string currentStatus,
        string targetStatus,
        IReadOnlyCollection<string> callerRoles,
        string? method = null)
    {
        var known = template.Statuses.Select(s => s.Code).ToHashSet(Comparer);

        if (!known.Contains(targetStatus))
        {
            return new TransitionDecision(
                TransitionOutcome.UnknownTargetStatus,
                $"'{targetStatus}' is not a status defined by the active template");
        }

        if (!known.Contains(currentStatus))
        {
            return new TransitionDecision(
                TransitionOutcome.UnknownCurrentStatus,
                $"Current status '{currentStatus}' is not defined by the active template");
        }

        var edges = template.Transitions
            .Where(t => Comparer.Equals(t.From, currentStatus) && Comparer.Equals(t.To, targetStatus))
            .ToList();

        if (edges.Count == 0)
        {
            return new TransitionDecision(
                TransitionOutcome.TransitionNotAllowed,
                $"'{currentStatus}' cannot transition to '{targetStatus}' under the active template");
        }

        var applicable = edges.Where(e => AppliesToMethod(e, method)).ToList();
        if (applicable.Count == 0)
        {
            return new TransitionDecision(
                TransitionOutcome.MethodNotPermitted,
                $"'{currentStatus}' cannot transition to '{targetStatus}' for method '{method ?? "(none)"}'");
        }

        if (!applicable.Any(e => RolesPermit(e, callerRoles)))
        {
            return new TransitionDecision(
                TransitionOutcome.RoleNotPermitted,
                $"Caller's roles do not permit transitioning '{currentStatus}' to '{targetStatus}'");
        }

        return TransitionDecision.Allow();
    }

    private static bool AppliesToMethod(TransitionRule rule, string? method)
    {
        // No restriction listed — the edge applies to every method.
        if (rule.Methods is null || rule.Methods.Count == 0)
        {
            return true;
        }

        return method is not null && rule.Methods.Contains(method, Comparer);
    }

    private static bool RolesPermit(TransitionRule rule, IReadOnlyCollection<string> callerRoles)
    {
        // Omitted allowedRoles means "any authenticated role", not "deny all".
        if (rule.AllowedRoles is null || rule.AllowedRoles.Count == 0)
        {
            return true;
        }

        return rule.AllowedRoles.Any(allowed => callerRoles.Contains(allowed, Comparer));
    }
}
