using OrderManager.Backend.Lib;

namespace OrderManager.Backend.Tests;

public class OrderNumberFormatterTests
{
    private static readonly DateTime August2026 = new(2026, 8, 2, 14, 30, 0, DateTimeKind.Utc);

    // ---------- Customer ----------

    [Fact]
    public void CustomerOrder_PrefixesTheSohoNumber()
    {
        Assert.Equal("CUS-4471", OrderNumberFormatter.ForCustomerOrder("4471"));
    }

    [Fact]
    public void CustomerOrder_TrimsSurroundingWhitespace()
    {
        Assert.Equal("CUS-4471", OrderNumberFormatter.ForCustomerOrder("  4471 "));
    }

    [Fact]
    public void CustomerOrder_AppendsAPrefixedSohoNumberVerbatim()
    {
        // Documents the known open question: if SOHO's real numbers carry their own
        // prefix we get a doubled-up form, to be revisited when their API lands.
        Assert.Equal("CUS-SO-4471", OrderNumberFormatter.ForCustomerOrder("SO-4471"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void CustomerOrder_RejectsMissingSohoNumber(string? soho)
    {
        // Guards CLAUDE.md §8: never create a customer order without a real reference.
        Assert.Throws<ArgumentException>(() => OrderNumberFormatter.ForCustomerOrder(soho!));
    }

    // ---------- Stock ----------

    [Fact]
    public void StockOrder_UsesYearMonthAndPaddedSequence()
    {
        Assert.Equal("STK-2608-0042", OrderNumberFormatter.ForStockOrder(August2026, 42));
    }

    [Fact]
    public void StockOrder_PadsToFourDigits()
    {
        Assert.Equal("STK-2608-0001", OrderNumberFormatter.ForStockOrder(August2026, 1));
    }

    [Fact]
    public void StockOrder_GrowsBeyondFourDigitsRatherThanTruncating()
    {
        Assert.Equal("STK-2608-123456", OrderNumberFormatter.ForStockOrder(August2026, 123456));
    }

    [Fact]
    public void StockOrder_SequenceDoesNotResetAcrossMonths()
    {
        // The date segment is cosmetic; uniqueness comes from the continuous sequence.
        var august = OrderNumberFormatter.ForStockOrder(new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc), 43);
        var september = OrderNumberFormatter.ForStockOrder(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), 44);

        Assert.Equal("STK-2608-0043", august);
        Assert.Equal("STK-2609-0044", september);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void StockOrder_RejectsNonPositiveSequence(long sequence)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OrderNumberFormatter.ForStockOrder(August2026, sequence));
    }

    [Fact]
    public void PrefixesDistinguishTheTwoOrderTypes()
    {
        var customer = OrderNumberFormatter.ForCustomerOrder("4471");
        var stock = OrderNumberFormatter.ForStockOrder(August2026, 42);

        Assert.StartsWith("CUS-", customer);
        Assert.StartsWith("STK-", stock);
        Assert.NotEqual(customer, stock);
    }
}

public class TimeFormatTests
{
    [Fact]
    public void Utc_AddsTheZMarkerToUnspecifiedKindValues()
    {
        // SYSUTCDATETIME() values arrive as Kind=Unspecified; without this they would
        // serialise with no Z and be read as local time by the mobile app.
        var fromSql = new DateTime(2026, 8, 2, 8, 17, 2, DateTimeKind.Unspecified);

        Assert.EndsWith("Z", TimeFormat.Utc(fromSql));
    }

    [Fact]
    public void Utc_DoesNotShiftTheClock()
    {
        var fromSql = new DateTime(2026, 8, 2, 8, 17, 2, DateTimeKind.Unspecified);

        Assert.StartsWith("2026-08-02T08:17:02", TimeFormat.Utc(fromSql));
    }
}
