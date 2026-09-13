using VramMonitor.Core.Abstractions;
using VramMonitor.Core.Model;

namespace VramMonitor.Core.Tests.Support;

/// <summary>A clock the test drives by hand, so sampling behaviour is deterministic.</summary>
internal sealed class FakeClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;
    private long _monotonic = start.UtcTicks;

    public DateTimeOffset UtcNow => _now;

    public long MonotonicTicks => _monotonic;

    /// <summary>Advances both clocks together, as real time does.</summary>
    public void Advance(TimeSpan by)
    {
        _now += by;
        _monotonic += by.Ticks;
    }

    /// <summary>
    /// Moves the wall clock without moving monotonic time, as a time-service correction would.
    /// </summary>
    public void StepWallClock(TimeSpan by) => _now += by;
}

/// <summary>A scripted GPU provider: no hardware, fully deterministic.</summary>
internal sealed class FakeGpuMemoryProvider : IGpuMemoryProvider
{
    private readonly Queue<Func<GpuId, DateTimeOffset, GpuSnapshot>> _script = new();

    public GpuInfo Gpu { get; } = new(
        new GpuId("luid_0x0_0x1"),
        new GpuSelector(0x10DE, 0x2204, 0, "Fake RTX", 0),
        "Fake RTX",
        24L * 1024 * 1024 * 1024,
        false);

    public int SnapshotCalls { get; private set; }

    /// <summary>Default behaviour once the script runs out.</summary>
    public Func<GpuId, DateTimeOffset, GpuSnapshot> Fallback { get; set; } =
        (gpu, at) => new GpuSnapshot(gpu, at, ProbeOutcome.Ok, 8L * 1024 * 1024 * 1024, []);

    public FakeGpuMemoryProvider Then(Func<GpuId, DateTimeOffset, GpuSnapshot> step)
    {
        _script.Enqueue(step);
        return this;
    }

    public FakeGpuMemoryProvider ThenMeasuring(params (uint Pid, double Mb)[] processes) =>
        Then((gpu, at) => new GpuSnapshot(gpu, at, ProbeOutcome.Ok, 8L * 1024 * 1024 * 1024,
            [.. processes.Select(p => new RawProcessMeasurement(p.Pid, Build.Mb(p.Mb), null))]));

    public FakeGpuMemoryProvider ThenFailing(string reason) =>
        Then((gpu, at) => GpuSnapshot.Failed(gpu, at, reason));

    public FakeGpuMemoryProvider ThenThrowing(string message) =>
        Then((_, _) => throw new InvalidOperationException(message));

    public Task<IReadOnlyList<GpuInfo>> GetGpusAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GpuInfo>>([Gpu]);

    public Task<GpuSnapshot> GetSnapshotAsync(GpuId gpuId, CancellationToken cancellationToken)
    {
        SnapshotCalls++;
        Func<GpuId, DateTimeOffset, GpuSnapshot> step = _script.Count > 0 ? _script.Dequeue() : Fallback;
        return Task.FromResult(step(gpuId, DateTimeOffset.UtcNow));
    }
}
