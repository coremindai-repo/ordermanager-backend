namespace OrderManager.Backend.Lib.Orders;

/// <summary>Dimensions as the salesperson entered them, in their chosen unit.</summary>
public sealed record DimensionsInput(decimal? Length, decimal? Breadth, decimal? Height, string? Unit);

/// <summary>Dimensions normalised to centimetres, ready to store.</summary>
public sealed record DimensionsInCentimetres(
    decimal? Length, decimal? Breadth, decimal? Height, string? EnteredUnit)
{
    public static readonly DimensionsInCentimetres None = new(null, null, null, null);
}

/// <summary>
/// Converts entered dimensions to the canonical centimetres actually stored.
///
/// The client works in metres and centimetres. Conversion happens HERE, once, on write —
/// not in the mobile app, where a bug in one released version would permanently corrupt
/// data, and not at query time, where every future report would have to remember to do
/// it. The entered unit is kept only so values can be shown back the way they were typed.
///
/// Pure, so the rounding and validation behaviour is pinned by tests rather than
/// discovered later in a report that quietly disagrees with the app.
/// </summary>
public static class LineItemDimensions
{
    public const string Centimetres = "cm";
    public const string Metres = "m";

    public static readonly string[] SupportedUnits = [Metres, Centimetres];

    public static bool IsSupportedUnit(string? unit) =>
        unit is not null && SupportedUnits.Contains(unit, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the values to store, or throws <see cref="AppException"/> (400) if the
    /// input is inconsistent — a dimension without a unit, or a unit we cannot convert.
    /// </summary>
    public static DimensionsInCentimetres Normalise(DimensionsInput? input)
    {
        if (input is null)
        {
            return DimensionsInCentimetres.None;
        }

        var hasAnyValue = input.Length is not null || input.Breadth is not null || input.Height is not null;

        if (!hasAnyValue && input.Unit is null)
        {
            return DimensionsInCentimetres.None;
        }

        if (hasAnyValue && input.Unit is null)
        {
            throw new AppException(Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest,
                "VALIDATION_ERROR",
                "dimensions.unit is required when any dimension is supplied — a measurement without a unit cannot be stored or displayed back");
        }

        if (!hasAnyValue)
        {
            throw new AppException(Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest,
                "VALIDATION_ERROR",
                "dimensions.unit was supplied with no dimensions");
        }

        if (!IsSupportedUnit(input.Unit))
        {
            throw new AppException(Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest,
                "VALIDATION_ERROR",
                $"dimensions.unit must be one of: {string.Join(", ", SupportedUnits)}");
        }

        foreach (var (name, value) in new[]
                 {
                     ("length", input.Length), ("breadth", input.Breadth), ("height", input.Height),
                 })
        {
            if (value is <= 0)
            {
                throw new AppException(Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest,
                    "VALIDATION_ERROR", $"dimensions.{name} must be greater than zero");
            }
        }

        var unit = input.Unit!.ToLowerInvariant();

        return new DimensionsInCentimetres(
            ToCentimetres(input.Length, unit),
            ToCentimetres(input.Breadth, unit),
            ToCentimetres(input.Height, unit),
            unit);
    }

    private static decimal? ToCentimetres(decimal? value, string unit)
    {
        if (value is null)
        {
            return null;
        }

        // Two decimal places matches the stored column, so what is read back is exactly
        // what was stored rather than a rounded version of it.
        return unit == Metres
            ? decimal.Round(value.Value * 100m, 2)
            : decimal.Round(value.Value, 2);
    }
}
