using VramMonitor.Core.Model;

namespace VramMonitor.Core.Tests.Support;

/// <summary>Builds a retained history plus its session records, so tests read as scenarios.</summary>
internal sealed class Hist
{
    private readonly List<GpuSample> _samples = [];
    private readonly Dictionary<ProcessSessionId, ProcessSessionInfo> _sessions = [];

    private TimeSpan _interval = TimeSpan.FromSeconds(10);

    public IReadOnlyList<GpuSample> Samples => _samples;
    public IReadOnlyDictionary<ProcessSessionId, ProcessSessionInfo> Sessions => _sessions;

    public static Hist New() => new();

    public Hist Interval(double seconds)
    {
        _interval = TimeSpan.FromSeconds(seconds);
        return this;
    }

    /// <summary>Registers a session belonging to the application at <paramref name="path"/>.</summary>
    public Hist App(long sessionId, string path, uint pid = 0)
    {
        var id = new ProcessSessionId(sessionId);
        _sessions[id] = new ProcessSessionInfo(
            id,
            pid == 0 ? (uint)sessionId : pid,
            sessionId,
            new GpuId("luid_0x0_0x1"),
            new ProcessIdentity(
                path.ToLowerInvariant(),
                System.IO.Path.GetFileNameWithoutExtension(path),
                path,
                System.IO.Path.GetFileName(path),
                ProcessIdentityKind.ExecutablePath),
            Build.T0,
            Build.T0,
            Build.T0);
        return this;
    }

    /// <summary>A normal sample. Values are megabytes; null means present-but-unreadable.</summary>
    public Hist At(double seconds, params (long Session, double? Mb)[] observations)
    {
        var obs = observations
            .Select(o => new ProcessObservation(
                new ProcessSessionId(o.Session),
                o.Mb is null ? null : Build.Mb(o.Mb.Value),
                null))
            .ToArray();

        bool anyMissing = obs.Any(o => o.DedicatedBytes is null);
        return Add(seconds, anyMissing ? ProbeOutcome.Partial : ProbeOutcome.Ok, obs);
    }

    /// <summary>A sample where the whole probe failed: nothing was observed about anyone.</summary>
    public Hist Failed(double seconds) => Add(seconds, ProbeOutcome.Failed, []);

    private Hist Add(double seconds, ProbeOutcome outcome, ProcessObservation[] observations)
    {
        _samples.Add(new GpuSample(
            Build.T0.AddSeconds(seconds),
            _interval,
            outcome,
            TotalDedicatedBytes: Build.Mb(8000),
            observations));
        return this;
    }

    public DateTimeOffset Now => _samples[^1].TimestampUtc;
}
