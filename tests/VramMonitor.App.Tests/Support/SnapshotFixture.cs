using VramMonitor.Core.Analysis;
using VramMonitor.Core.Configuration;
using VramMonitor.Core.Model;

namespace VramMonitor.App.Tests.Support;

/// <summary>
/// Hand-built snapshots for chart tests: analysis output written directly rather than derived, so a test can
/// put a point exactly where it needs one.
/// </summary>
internal static class SnapshotFixture
{
    public const long Mb = 1024 * 1024;

    /// <summary>A fixed instant on a whole second, so tick labels are predictable.</summary>
    public static readonly DateTimeOffset End = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public static MonitorSettings Settings(TimeSpan? window = null, TimeSpan? interval = null) => new MonitorSettings
    {
        HistoryWindow = window ?? TimeSpan.FromMinutes(60),
        SampleInterval = interval ?? TimeSpan.FromSeconds(10),
    }.Validated();

    public static SeriesPoint Measured(DateTimeOffset at, double megabytes) =>
        new(at, (long)(megabytes * Mb), PointState.Measured);

    public static SeriesPoint Missing(DateTimeOffset at) => new(at, 0, PointState.Missing);

    public static SeriesPoint Absent(DateTimeOffset at) => new(at, 0, PointState.Absent);

    public static SessionView Session(uint pid, params SeriesPoint[] series) => new(
        new ProcessSessionId(pid), pid, null, End.AddHours(-1), End, null, null, series);

    public static ApplicationView Consumer(string key, SeriesPoint[] series, params SessionView[] sessions) => new(
        key, key + ".exe", null, ProcessIdentityKind.ExecutableName, null, null, null, 100 * Mb, null,
        TimeSpan.Zero, false, sessions.Length, sessions, series);

    public static MonitorSnapshot Snapshot(
        MonitorSettings? settings = null,
        DateTimeOffset? takenUtc = null,
        IReadOnlyList<ApplicationView>? apps = null,
        IReadOnlyList<SeriesPoint>? total = null,
        IReadOnlyList<DisruptionMarker>? markers = null)
    {
        IReadOnlyList<ApplicationView> consumers = apps ?? [];
        var analysis = new AnalysisResult(
            [], consumers, [], [.. consumers.Select(a => a.Key)],
            new TotalSeries(total ?? [], null), markers ?? [], null);

        return new MonitorSnapshot(
            null, settings ?? Settings(), takenUtc ?? End, [],
            new Dictionary<ProcessSessionId, ProcessSessionInfo>(), analysis, MonitorHealth.Empty);
    }
}
