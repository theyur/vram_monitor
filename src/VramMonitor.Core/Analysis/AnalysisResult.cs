using VramMonitor.Core.Model;

namespace VramMonitor.Core.Analysis;

/// <summary>Why a point is absent from a series. Drives what the chart shows, per spec section 13.</summary>
public enum PointState
{
    /// <summary>A real measurement.</summary>
    Measured,

    /// <summary>The process had no counter instance: it exited, or stopped using the GPU. No marker.</summary>
    Absent,

    /// <summary>The process was present but unreadable, or the whole probe failed. Gap plus a marker.</summary>
    Missing,
}

/// <summary>One point of a chart series. <see cref="Value"/> is meaningful only when measured.</summary>
public readonly record struct SeriesPoint(DateTimeOffset TimestampUtc, long Value, PointState State)
{
    public bool HasValue => State is PointState.Measured;
}

/// <summary>What a disruption marker on the time axis means. Spec sections 13.2 to 13.4 plus sleep gaps.</summary>
public enum DisruptionKind
{
    /// <summary>The entire probe failed. One marker covers every series.</summary>
    ProbeFailed,

    /// <summary>Some measurements were unreadable. Only the affected series gap.</summary>
    PartialSample,

    /// <summary>
    /// No probe ran for much longer than the interval, typically machine sleep. Not a failure, so it is
    /// deliberately distinct from the other two.
    /// </summary>
    SamplingPaused,
}

public sealed record DisruptionMarker(DateTimeOffset TimestampUtc, DisruptionKind Kind)
{
    public string Text => Kind switch
    {
        DisruptionKind.ProbeFailed => "Probe failed",
        DisruptionKind.PartialSample => "Partial sample",
        DisruptionKind.SamplingPaused => "Sampling paused",
        _ => "Disruption",
    };
}

/// <summary>A drill-down row: one process lifetime belonging to an application.</summary>
public sealed record SessionView(
    ProcessSessionId SessionId,
    uint Pid,
    DateTimeOffset? ProcessStartUtc,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    long? CurrentDedicatedBytes,
    long? SharedBytes,
    IReadOnlyList<SeriesPoint> Series);

/// <summary>
/// One application row. All figures are computed over the visible history window only; the extra grace tail
/// the store retains for hysteresis never contributes to a displayed number.
/// </summary>
public sealed record ApplicationView(
    string Key,
    string DisplayName,
    string? ExecutablePath,
    ProcessIdentityKind IdentityKind,
    long? CurrentDedicatedBytes,
    DateTimeOffset? CurrentAsOfUtc,
    long? AverageDedicatedBytes,
    long? PeakDedicatedBytes,
    long? SharedBytes,
    TimeSpan CumulativeAggressiveTime,
    bool IsAggressive,
    int SessionCount,
    IReadOnlyList<SessionView> Sessions,
    IReadOnlyList<SeriesPoint> Series)
{
    /// <summary>True when the newest visible value for this application is not a measurement.</summary>
    public bool IsCurrentUnknown => CurrentDedicatedBytes is null;
}

/// <summary>One tray tooltip row. Shows the raw current value; smoothing only governs ordering.</summary>
public sealed record TrayEntry(string Key, string DisplayName, long CurrentDedicatedBytes);

/// <summary>The contextual total-VRAM series, plotted against its own right-hand axis.</summary>
public sealed record TotalSeries(IReadOnlyList<SeriesPoint> Points, long? AdapterCapacityBytes);

public sealed record AnalysisResult(
    IReadOnlyList<ApplicationView> Aggressive,
    IReadOnlyList<ApplicationView> Other,
    IReadOnlyList<TrayEntry> TrayTop5,
    IReadOnlyList<string> ChartedKeys,
    TotalSeries Total,
    IReadOnlyList<DisruptionMarker> Markers,
    DateTimeOffset? LatestSampleUtc)
{
    public static readonly AnalysisResult Empty = new(
        [], [], [], [], new TotalSeries([], null), [], null);

    /// <summary>Every retained consumer, aggressive or not. This is the universe of spec section 7.</summary>
    public IEnumerable<ApplicationView> AllConsumers => Aggressive.Concat(Other);
}
