using System.Globalization;

namespace VramMonitor.App.Formatting;

/// <summary>Display formatting. Never renders an unknown value as a zero (spec section 5.2).</summary>
public static class Format
{
    /// <summary>What the UI shows when a value is genuinely unknown, rather than zero.</summary>
    public const string Unknown = "—";   // em dash

    private const double Megabyte = 1024 * 1024;
    private const double Gigabyte = 1024 * 1024 * 1024;

    public static string Bytes(long? value)
    {
        if (value is not { } bytes) return Unknown;

        return bytes >= Gigabyte
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes / Gigabyte:0.00} GB")
            : string.Create(CultureInfo.CurrentCulture, $"{bytes / Megabyte:0} MB");
    }

    /// <summary>Compact form for the tray tooltip, which must stay narrow.</summary>
    public static string BytesCompact(long bytes) =>
        bytes >= Gigabyte
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes / Gigabyte:0.0} GB")
            : string.Create(CultureInfo.CurrentCulture, $"{bytes / Megabyte:0} MB");

    public static double Megabytes(long bytes) => bytes / Megabyte;

    public static string Duration(TimeSpan value)
    {
        if (value <= TimeSpan.Zero) return "–";      // en dash: none accumulated
        if (value.TotalMinutes < 1) return string.Create(CultureInfo.CurrentCulture, $"{value.TotalSeconds:0} s");
        if (value.TotalHours < 1)
        {
            return string.Create(CultureInfo.CurrentCulture, $"{(int)value.TotalMinutes}m {value.Seconds:00}s");
        }

        return string.Create(CultureInfo.CurrentCulture, $"{(int)value.TotalHours}h {value.Minutes:00}m");
    }

    public static string Time(DateTimeOffset? value) =>
        value is null ? Unknown : value.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>Describes how stale a reading is, so a old value is never passed off as current.</summary>
    public static string Age(DateTimeOffset? asOf, DateTimeOffset now)
    {
        if (asOf is null) return string.Empty;

        TimeSpan age = now - asOf.Value;
        return age < TimeSpan.FromSeconds(30)
            ? string.Empty
            : string.Create(CultureInfo.CurrentCulture, $" (as of {Time(asOf)})");
    }
}
