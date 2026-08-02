namespace OrderManager.Backend.Lib;

public static class TimeFormat
{
    /// <summary>
    /// Formats a UTC timestamp read from SQL as a round-trip ISO-8601 string with the
    /// trailing Z. Values from SYSUTCDATETIME() arrive as Kind=Unspecified, which
    /// would otherwise serialise without the marker and be read as local time by the
    /// mobile app.
    /// </summary>
    public static string Utc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("o");
}
