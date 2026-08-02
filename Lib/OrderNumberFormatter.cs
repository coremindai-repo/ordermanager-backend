namespace OrderManager.Backend.Lib;

/// <summary>
/// Order number scheme (see CLAUDE.md §4):
///   Customer — CUS-{SOHO sales order number}, e.g. CUS-4471
///   Stock    — STK-{yyMM}-{sequence:D4},      e.g. STK-2608-0042
///
/// The stock sequence is continuous and never resets; the yyMM segment exists for
/// human readability only, so uniqueness never depends on the date.
/// </summary>
public static class OrderNumberFormatter
{
    public const string CustomerPrefix = "CUS";
    public const string StockPrefix = "STK";

    /// <summary>
    /// NOTE: SOHO's real number format is unknown. If it turns out to carry its own
    /// prefix (e.g. "SO-4471") this produces "CUS-SO-4471" — revisit when the real
    /// API lands rather than stripping characters on a guess.
    /// </summary>
    public static string ForCustomerOrder(string sohoOrderNumber)
    {
        if (string.IsNullOrWhiteSpace(sohoOrderNumber))
        {
            throw new ArgumentException("SOHO order number is required for a customer order", nameof(sohoOrderNumber));
        }

        return $"{CustomerPrefix}-{sohoOrderNumber.Trim()}";
    }

    public static string ForStockOrder(DateTime utcNow, long sequence)
    {
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Stock order sequence must be positive");
        }

        // D4 is a minimum width, not a cap — the sequence simply grows past 9999.
        return $"{StockPrefix}-{utcNow:yyMM}-{sequence:D4}";
    }
}
