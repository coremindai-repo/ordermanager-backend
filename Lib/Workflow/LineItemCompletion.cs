namespace OrderManager.Backend.Lib.Workflow;

/// <summary>
/// An order ships as a single unit: it only leaves the factory once every line item
/// on it is finished, and the whole order then moves to one store. This is the
/// order-wide check behind that rule — distinct from "all steps within one line item
/// are done", which only tells you about a single item.
///
/// Pure so the boundary cases (an order with no items, mixed statuses, unknown
/// statuses) are unit-tested rather than inferred.
/// </summary>
public static class LineItemCompletion
{
    /// <summary>
    /// True only if there is at least one line item and every one of them sits in a
    /// terminal production status.
    /// </summary>
    /// <param name="lineItemStatuses">Current status of every line item on the order.</param>
    /// <param name="terminalStatuses">
    /// Statuses counting as finished — normally
    /// <see cref="WorkflowTemplate.TerminalStatuses"/> of the production step template.
    /// </param>
    public static bool AllComplete(IEnumerable<string> lineItemStatuses, IReadOnlySet<string> terminalStatuses)
    {
        var statuses = lineItemStatuses.ToList();

        // An order with no line items is not "complete" — it is malformed. Returning
        // true here would let an empty order sail through the gate on a vacuous truth.
        if (statuses.Count == 0)
        {
            return false;
        }

        return statuses.All(terminalStatuses.Contains);
    }

    /// <summary>Statuses blocking the transition, for a message naming what to finish.</summary>
    public static IReadOnlyList<string> IncompleteStatuses(
        IEnumerable<string> lineItemStatuses,
        IReadOnlySet<string> terminalStatuses) =>
        lineItemStatuses.Where(s => !terminalStatuses.Contains(s)).Distinct().ToList();
}
