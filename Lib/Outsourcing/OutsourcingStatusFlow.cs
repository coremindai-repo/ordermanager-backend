namespace OrderManager.Backend.Lib.Outsourcing;

/// <summary>
/// Outsourcing / import requests are a FIXED sub-process, deliberately not templatized
/// — contract §6 groups them with raw materials under "fixed sub-processes", so the
/// chain lives in code, mirroring <see cref="RawMaterials.RawMaterialStatusFlow"/>.
///
///   placed → accepted → received_finished
///                    └→ received_semi_finished
///
/// Unlike raw materials this chain BRANCHES at the end, and the branch matters beyond
/// the request itself: finished goods are done, whereas semi-finished goods still need
/// factory work. Which terminal is reached decides where the linked line items go.
///
/// Note what is NOT here: the line items' own statuses. Those live in
/// production_step_templates, because line-item transitions validate against that
/// template and the order-level completeness gate derives its terminal statuses from
/// it. Hard-coding them here would put the same state in two places and break the gate.
/// </summary>
public static class OutsourcingStatusFlow
{
    public const string Initial = "placed";
    public const string Accepted = "accepted";
    public const string ReceivedFinished = "received_finished";
    public const string ReceivedSemiFinished = "received_semi_finished";

    public static readonly string[] All =
        [Initial, Accepted, ReceivedFinished, ReceivedSemiFinished];

    /// <summary>Both receipt states end the request; neither leads anywhere further.</summary>
    public static readonly string[] Terminal = [ReceivedFinished, ReceivedSemiFinished];

    public static bool IsKnown(string? status) =>
        status is not null && All.Contains(status, StringComparer.OrdinalIgnoreCase);

    public static bool IsTerminal(string status) =>
        Terminal.Contains(status, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// placed → accepted, then accepted → a receipt state. No skipping (goods cannot
    /// arrive from a request the supplier never accepted), no going back, and no
    /// restating the current status.
    /// </summary>
    /// <param name="method">
    /// `outsource` or `import`. Only outsourcing can return semi-finished goods — an
    /// outsourcing supplier may do part of the job, whereas an import always arrives
    /// complete. Omitting it permits both, which is only appropriate where the method
    /// is genuinely unknown.
    /// </param>
    public static bool CanTransition(string from, string to, string? method = null)
    {
        if (!IsKnown(from) || !IsKnown(to))
        {
            return false;
        }

        return NextOptions(from, method).Contains(to, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What may follow this status — empty once received.</summary>
    public static IReadOnlyList<string> NextOptions(string from, string? method = null)
    {
        if (Equals(from, Initial))
        {
            return [Accepted];
        }

        if (Equals(from, Accepted))
        {
            // The branch — but only outsourcing has one. An import supplier does not
            // partially build something and send it back for finishing.
            return CanReturnSemiFinished(method)
                ? [ReceivedFinished, ReceivedSemiFinished]
                : [ReceivedFinished];
        }

        return [];
    }

    /// <summary>
    /// Semi-finished receipt is outsourcing-only. A null method is treated permissively
    /// because it means "not stated" rather than "import".
    /// </summary>
    public static bool CanReturnSemiFinished(string? method) =>
        method is null || string.Equals(method, "outsource", StringComparison.OrdinalIgnoreCase);

    public static string Canonical(string status) =>
        All.First(s => string.Equals(s, status, StringComparison.OrdinalIgnoreCase));

    private static bool Equals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
