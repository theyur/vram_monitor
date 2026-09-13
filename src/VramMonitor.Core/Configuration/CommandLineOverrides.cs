using System.Globalization;

namespace VramMonitor.Core.Configuration;

/// <summary>
/// Command-line overrides that apply to the current launch only and are never written back to the settings
/// file (spec section 14).
/// </summary>
public sealed record CommandLineOverrides
{
    public double? IntervalSeconds { get; init; }
    public double? HistoryMinutes { get; init; }
    public double? FloorMegabytes { get; init; }
    public double? AggressiveMinutes { get; init; }
    public double? GraceSeconds { get; init; }
    public int? ChartTopApplications { get; init; }

    /// <summary>An adapter ordinal, or a case-insensitive substring of its description.</summary>
    public string? Gpu { get; init; }

    public bool StartHidden { get; init; }

    /// <summary>Arguments that were not recognised, surfaced rather than silently ignored.</summary>
    public IReadOnlyList<string> Unrecognised { get; init; } = [];

    public static CommandLineOverrides Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var result = new CommandLineOverrides();
        var unrecognised = new List<string>();

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            string? value = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[i + 1]
                : null;

            switch (arg)
            {
                case "--interval-seconds" when TryNumber(value, out double v):
                    result = result with { IntervalSeconds = v }; i++; break;
                case "--history-minutes" when TryNumber(value, out double v):
                    result = result with { HistoryMinutes = v }; i++; break;
                case "--floor-mb" when TryNumber(value, out double v):
                    result = result with { FloorMegabytes = v }; i++; break;
                case "--aggressive-minutes" when TryNumber(value, out double v):
                    result = result with { AggressiveMinutes = v }; i++; break;
                case "--grace-seconds" when TryNumber(value, out double v):
                    result = result with { GraceSeconds = v }; i++; break;
                case "--chart-top-n" when TryNumber(value, out double v):
                    result = result with { ChartTopApplications = (int)v }; i++; break;
                case "--gpu" when value is not null:
                    result = result with { Gpu = value }; i++; break;
                case "--start-hidden":
                    result = result with { StartHidden = true }; break;
                default:
                    unrecognised.Add(arg); break;
            }
        }

        return result with { Unrecognised = unrecognised };
    }

    /// <summary>Applies the overrides on top of persisted settings, then re-validates.</summary>
    public MonitorSettings ApplyTo(MonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return (settings with
        {
            SampleInterval = IntervalSeconds is { } s ? TimeSpan.FromSeconds(s) : settings.SampleInterval,
            HistoryWindow = HistoryMinutes is { } h ? TimeSpan.FromMinutes(h) : settings.HistoryWindow,
            MonitoringFloorBytes = FloorMegabytes is { } f
                ? (long)(f * MonitorSettings.BytesPerMegabyte)
                : settings.MonitoringFloorBytes,
            AggressiveDurationThreshold = AggressiveMinutes is { } a
                ? TimeSpan.FromMinutes(a)
                : settings.AggressiveDurationThreshold,
            DemotionGracePeriod = GraceSeconds is { } g
                ? TimeSpan.FromSeconds(g)
                : settings.DemotionGracePeriod,
            ChartTopApplications = ChartTopApplications ?? settings.ChartTopApplications,
        }).Validated();
    }

    // Invariant culture on purpose: "--floor-mb 12.5" must mean the same thing on a machine whose
    // decimal separator is a comma.
    private static bool TryNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
