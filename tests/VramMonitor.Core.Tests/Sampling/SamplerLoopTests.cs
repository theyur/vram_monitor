using VramMonitor.Core.Configuration;
using VramMonitor.Core.Model;
using VramMonitor.Core.Sampling;
using VramMonitor.Core.Tests.Support;
using Xunit;

namespace VramMonitor.Core.Tests.Sampling;

/// <summary>
/// Drives the real sampling loop rather than the single-cycle test seam.
/// </summary>
/// <remarks>
/// These are the only tests that exercise <c>RunAsync</c> itself: the timer, the settings channel and the
/// interaction between them. That matters because <see cref="PeriodicTimer"/> permits only one outstanding
/// wait, so a loop that re-issues it after a settings wake throws at runtime — a fault no single-cycle test
/// could ever see. They use real time, so the interval is the minimum the settings allow.
/// </remarks>
public sealed class SamplerLoopTests
{
    private static readonly TimeSpan Interval = MonitorSettings.MinSampleInterval;   // 2 s

    private static MonitorSettings Settings() => new MonitorSettings
    {
        SampleInterval = Interval,
        HistoryWindow = TimeSpan.FromMinutes(10),
        MonitoringFloorBytes = Build.Mb(100),
        AggressiveDurationThreshold = TimeSpan.FromSeconds(2),
    }.Validated();

    private static SamplerService NewSampler(out FakeGpuMemoryProvider provider)
    {
        provider = new FakeGpuMemoryProvider();
        FakeGpuMemoryProvider captured = provider;
        captured.Fallback = (gpu, at) => new GpuSnapshot(
            gpu, at, ProbeOutcome.Ok, Build.Mb(8000),
            [new RawProcessMeasurement(1, Build.Mb(500), null)]);

        var sampler = new SamplerService(captured, new FakeMetadataResolver(), Settings());
        sampler.SetGpu(captured.Gpu);
        return sampler;
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100, ct);
        }

        return condition();
    }

    [Fact]
    public async Task The_loop_keeps_sampling_across_many_ticks()
    {
        // Exercises the held-timer-task pattern: re-issuing the wait while one is pending would throw.
        await using SamplerService sampler = NewSampler(out FakeGpuMemoryProvider provider);
        sampler.Start();

        bool reached = await WaitUntil(
            () => provider.SnapshotCalls >= 3, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.True(reached, $"only {provider.SnapshotCalls} probes were taken");
        Assert.True(sampler.Current.Samples.Count >= 3);
    }

    [Fact]
    public async Task A_settings_change_wakes_the_loop_and_re_evaluates_without_waiting_for_the_next_tick()
    {
        await using SamplerService sampler = NewSampler(out FakeGpuMemoryProvider provider);
        sampler.Start();

        Assert.True(await WaitUntil(
            () => sampler.Current.Samples.Count >= 2, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));

        int callsBefore = provider.SnapshotCalls;
        Assert.True(sampler.Current.Analysis.AllConsumers.Single().IsAggressive);

        // Raise the threshold out of reach. The published snapshot must change well inside one interval,
        // and without a new probe.
        sampler.UpdateSettings(Settings() with { AggressiveDurationThreshold = TimeSpan.FromHours(2) });

        bool reEvaluated = await WaitUntil(
            () => !sampler.Current.Analysis.AllConsumers.Single().IsAggressive,
            TimeSpan.FromMilliseconds(1500),
            TestContext.Current.CancellationToken);

        Assert.True(reEvaluated, "the settings change did not take effect before the next tick");
        Assert.Equal(callsBefore, provider.SnapshotCalls);
    }

    [Fact]
    public async Task Changing_the_interval_at_runtime_does_not_break_the_loop()
    {
        // The interval is applied by assigning Period; rebuilding the timer would invalidate the pending wait.
        await using SamplerService sampler = NewSampler(out FakeGpuMemoryProvider provider);
        sampler.Start();

        Assert.True(await WaitUntil(
            () => provider.SnapshotCalls >= 1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        sampler.UpdateSettings(Settings() with { SampleInterval = TimeSpan.FromSeconds(3) });

        int callsAfterChange = provider.SnapshotCalls;
        bool keptGoing = await WaitUntil(
            () => provider.SnapshotCalls > callsAfterChange,
            TimeSpan.FromSeconds(12),
            TestContext.Current.CancellationToken);

        Assert.True(keptGoing, "sampling stopped after the interval was changed");
        Assert.Equal(TimeSpan.FromSeconds(3), sampler.Current.Settings.SampleInterval);
    }

    [Fact]
    public async Task A_provider_that_keeps_throwing_does_not_stop_the_loop()
    {
        await using SamplerService sampler = NewSampler(out FakeGpuMemoryProvider provider);
        provider.Fallback = (_, _) => throw new InvalidOperationException("always broken");
        sampler.Start();

        bool kept = await WaitUntil(
            () => sampler.Current.Health.FailedProbeCount >= 3,
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);

        Assert.True(kept, $"only {sampler.Current.Health.FailedProbeCount} failures were recorded");
    }

    [Fact]
    public async Task Disposing_stops_the_loop_promptly()
    {
        SamplerService sampler = NewSampler(out FakeGpuMemoryProvider provider);
        sampler.Start();

        Assert.True(await WaitUntil(
            () => provider.SnapshotCalls >= 1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        await sampler.DisposeAsync();
        int callsAtDispose = provider.SnapshotCalls;

        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(callsAtDispose, provider.SnapshotCalls);
    }
}
