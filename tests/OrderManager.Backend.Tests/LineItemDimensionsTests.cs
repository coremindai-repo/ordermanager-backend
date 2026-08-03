using OrderManager.Backend.Lib;
using OrderManager.Backend.Lib.Orders;

namespace OrderManager.Backend.Tests;

/// <summary>
/// Dimensions are stored canonically in centimetres so aggregates work without grouping
/// or conversion. The conversion happens once, here, on write — these tests pin the
/// behaviour so a report cannot quietly disagree with what the app displayed.
/// </summary>
public class LineItemDimensionsTests
{
    private static DimensionsInput Input(decimal? l, decimal? b, decimal? h, string? unit) => new(l, b, h, unit);

    // ---------- Conversion ----------

    [Fact]
    public void CentimetresAreStoredAsEntered()
    {
        var result = LineItemDimensions.Normalise(Input(200, 90, 75, "cm"));

        Assert.Equal(200m, result.Length);
        Assert.Equal(90m, result.Breadth);
        Assert.Equal(75m, result.Height);
        Assert.Equal("cm", result.EnteredUnit);
    }

    [Fact]
    public void MetresAreConvertedToCentimetres()
    {
        var result = LineItemDimensions.Normalise(Input(2, 0.9m, 0.75m, "m"));

        Assert.Equal(200m, result.Length);
        Assert.Equal(90m, result.Breadth);
        Assert.Equal(75m, result.Height);
    }

    [Fact]
    public void TheEnteredUnitIsPreservedSoValuesCanBeShownBackAsTyped()
    {
        // 2 m is stored as 200 cm but should still display as "2 m".
        var result = LineItemDimensions.Normalise(Input(2, null, null, "m"));

        Assert.Equal(200m, result.Length);
        Assert.Equal("m", result.EnteredUnit);
    }

    [Fact]
    public void MixedUnitsAggregateCorrectlyBecauseBothLandInCentimetres()
    {
        // The whole point: 2 m and 150 cm are directly comparable once stored.
        var inMetres = LineItemDimensions.Normalise(Input(2, null, null, "m"));
        var inCentimetres = LineItemDimensions.Normalise(Input(150, null, null, "cm"));

        Assert.True(inMetres.Length > inCentimetres.Length);
        Assert.Equal(175m, (inMetres.Length!.Value + inCentimetres.Length!.Value) / 2);
    }

    [Fact]
    public void ConversionRoundsToTheStoredPrecision()
    {
        // The column is DECIMAL(10,2); rounding here means what is read back is exactly
        // what was stored rather than a rounded version of it.
        var result = LineItemDimensions.Normalise(Input(1.2345m, null, null, "m"));

        Assert.Equal(123.45m, result.Length);
    }

    [Fact]
    public void UnitMatchingIsCaseInsensitiveAndStoredLowercase()
    {
        var result = LineItemDimensions.Normalise(Input(2, null, null, "M"));

        Assert.Equal(200m, result.Length);
        Assert.Equal("m", result.EnteredUnit);
    }

    // ---------- Partial dimensions ----------

    [Fact]
    public void AnyAxisMayBeOmitted()
    {
        var result = LineItemDimensions.Normalise(Input(200, null, null, "cm"));

        Assert.Equal(200m, result.Length);
        Assert.Null(result.Breadth);
        Assert.Null(result.Height);
    }

    [Fact]
    public void NoDimensionsAtAllIsFine()
    {
        Assert.Equal(DimensionsInCentimetres.None, LineItemDimensions.Normalise(null));
        Assert.Equal(DimensionsInCentimetres.None, LineItemDimensions.Normalise(Input(null, null, null, null)));
    }

    // ---------- Rejections ----------

    [Fact]
    public void RejectsADimensionWithNoUnit()
    {
        // A measurement with no unit cannot be stored canonically or displayed back.
        var exception = Assert.Throws<AppException>(() => LineItemDimensions.Normalise(Input(200, null, null, null)));

        Assert.Equal(400, exception.StatusCode);
        Assert.Contains("unit is required", exception.Message);
    }

    [Fact]
    public void RejectsAUnitWithNoDimensions()
    {
        Assert.Throws<AppException>(() => LineItemDimensions.Normalise(Input(null, null, null, "cm")));
    }

    [Theory]
    [InlineData("in")]
    [InlineData("ft")]
    [InlineData("mm")]
    [InlineData("metres")]
    public void RejectsUnsupportedUnits(string unit)
    {
        // The client works in metres and centimetres only. Accepting anything else would
        // mean storing a value we cannot convert, silently corrupting every aggregate.
        var exception = Assert.Throws<AppException>(() => LineItemDimensions.Normalise(Input(10, null, null, unit)));

        Assert.Contains("must be one of", exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void RejectsNonPositiveMeasurements(decimal value)
    {
        Assert.Throws<AppException>(() => LineItemDimensions.Normalise(Input(value, null, null, "cm")));
    }

    [Fact]
    public void RejectionNamesTheOffendingAxis()
    {
        var exception = Assert.Throws<AppException>(() => LineItemDimensions.Normalise(Input(100, -1, null, "cm")));

        Assert.Contains("breadth", exception.Message);
    }
}
