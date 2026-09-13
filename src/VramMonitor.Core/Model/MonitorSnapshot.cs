using VramMonitor.Core.Analysis;
using VramMonitor.Core.Configuration;

namespace VramMonitor.Core.Model;

/// <summary>
/// Lightweight monitor health (spec section 15). Counters are cumulative since the process started, which is
/// why they are produced by the sampler rather than derived from the history window by the analyzer.
/// </summary>
public sealed record MonitorHealth(
    DateTimeOffset? LastSuccessfulSampleUtc = null,
    int FailedProbeCount = 0,
    int PartialProbeCount = 0,
    int SkippedCycles = 0,
    int LateCycles = 0,
    TimeSpan LastProbeDuration = default,
    TimeSpan AverageProbeDuration = default,
    TimeSpan MaxProbeDuration = default,
    string? BlockingError = null)
{
    public static readonly MonitorHealth Empty = new();
}

/// <summary>
/// The immutable state handed to the UI and to exporters once per sampling cycle.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is a copy.</b> The rolling store's collections are mutated by the sampler on the next
/// tick, so publishing references to them would leave the UI thread reading torn state mid-trim. Copying
/// roughly 360 references per cycle is nothing against the sampling budget, and it removes the need for any
/// lock: export simply reads a snapshot that cannot change underneath it while monitoring continues
/// (spec section 17).
/// </para>
/// <para>
/// <see cref="Samples"/> is the <em>visible</em> window only. The extra grace tail the store retains exists
/// solely for demotion hysteresis and is never shown or exported.
/// </para>
/// </remarks>
public sealed record MonitorSnapshot(
    GpuInfo? Gpu,
    MonitorSettings Settings,
    DateTimeOffset TakenUtc,
    IReadOnlyList<GpuSample> Samples,
    IReadOnlyDictionary<ProcessSessionId, ProcessSessionInfo> Sessions,
    AnalysisResult Analysis,
    MonitorHealth Health)
{
    public static MonitorSnapshot Empty(MonitorSettings settings) => new(
        null, settings, DateTimeOffset.UtcNow, [], new Dictionary<ProcessSessionId, ProcessSessionInfo>(),
        AnalysisResult.Empty, MonitorHealth.Empty);
}
