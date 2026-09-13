using VramMonitor.Core.Model;

namespace VramMonitor.Core.Tests.Support;

/// <summary>Terse builders so tests read as data, not as plumbing.</summary>
internal static class Build
{
    public static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    public static ProcessSessionId Session(long id) => new(id);

    public static long Mb(double megabytes) => (long)(megabytes * 1024 * 1024);

    /// <summary>A sample at <paramref name="atSeconds"/> past <see cref="T0"/>.</summary>
    public static GpuSample Sample(
        double atSeconds,
        ProbeOutcome outcome = ProbeOutcome.Ok,
        long? totalDedicated = null,
        params ProcessObservation[] observations) =>
        new(T0.AddSeconds(atSeconds), Interval, outcome, totalDedicated, observations);

    /// <summary>A measured observation.</summary>
    public static ProcessObservation Obs(long sessionId, double dedicatedMb, double? sharedMb = null) =>
        new(Session(sessionId), Mb(dedicatedMb), sharedMb is null ? null : Mb(sharedMb.Value));

    /// <summary>An observation that is present but unreadable -- a gap, never a zero.</summary>
    public static ProcessObservation Missing(long sessionId) => new(Session(sessionId), null, null);

    public static ProcessSessionInfo SessionInfo(
        long sessionId,
        string path = @"C:\apps\app.exe",
        uint pid = 100,
        long creationTicks = 1) =>
        new(
            Session(sessionId),
            pid,
            creationTicks,
            new GpuId("luid_0x0_0x1"),
            new ProcessIdentity(path.ToLowerInvariant(), Path.GetFileNameWithoutExtension(path), path,
                Path.GetFileName(path), ProcessIdentityKind.ExecutablePath),
            T0,
            T0,
            T0);
}
