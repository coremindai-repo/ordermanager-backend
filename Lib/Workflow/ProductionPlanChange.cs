namespace OrderManager.Backend.Lib.Workflow;

public sealed record ExistingStep(string StepName, int Sequence, string Status)
{
    /// <summary>Work has been recorded against this step — started or complete.</summary>
    public bool HasWork => !string.Equals(Status, "pending", StringComparison.OrdinalIgnoreCase);
}

public sealed record PlanChangeDecision(
    bool IsAllowed,
    string? Message,
    IReadOnlyList<string> StepsToAdd,
    IReadOnlyList<string> StepsToRemove);

/// <summary>
/// Decides whether a requested production plan may replace the current one.
///
/// The original rule was "no changes once work has started", which existed to stop
/// recorded work and photos being orphaned. That is too strict for a claimed
/// semi-finished item: it arrives with completed steps and needs the *remaining* ones
/// planned under its new order.
///
/// So the rule is narrowed to **no destructive changes**. Appending is fine; removing
/// or resetting a step that already has work against it is not — which preserves the
/// original intent while allowing production to continue.
///
/// Pure, because "can this plan change" is precisely the sort of rule that should not
/// need a database to test.
/// </summary>
public static class ProductionPlanChange
{
    public static PlanChangeDecision Evaluate(
        IReadOnlyList<ExistingStep> existing,
        IReadOnlyList<string> requested)
    {
        var requestedSet = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Any step with work recorded against it must still be present. Dropping one
        // would orphan its photos, assigned names and timestamps.
        var wouldDestroy = existing
            .Where(s => s.HasWork && !requestedSet.Contains(s.StepName))
            .Select(s => s.StepName)
            .ToList();

        if (wouldDestroy.Count > 0)
        {
            return new PlanChangeDecision(
                false,
                $"Cannot remove step(s) that already have work recorded: {string.Join(", ", wouldDestroy)}. " +
                "Steps may be added to a plan in progress, but completed or started work cannot be discarded.",
                [], []);
        }

        var existingByName = existing.ToDictionary(s => s.StepName, s => s, StringComparer.OrdinalIgnoreCase);

        // Genuinely new steps, in the order requested.
        var toAdd = requested.Where(r => !existingByName.ContainsKey(r)).ToList();

        // Only steps with no work may be dropped.
        var toRemove = existing
            .Where(s => !s.HasWork && !requestedSet.Contains(s.StepName))
            .Select(s => s.StepName)
            .ToList();

        return new PlanChangeDecision(true, null, toAdd, toRemove);
    }

    /// <summary>
    /// Whether the item's method may still be changed. Once work exists the method is
    /// fixed — the recorded steps were performed under it, and switching (say) factory
    /// to outsource would leave that history describing a route the item never took.
    /// </summary>
    public static bool CanChangeMethod(IReadOnlyList<ExistingStep> existing) =>
        !existing.Any(s => s.HasWork);
}
