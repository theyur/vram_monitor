using VramMonitor.Core.Configuration;
using VramMonitor.Core.Model;
using VramMonitor.Core.Sampling;
using VramMonitor.Core.Tests.Support;
using Xunit;
using static VramMonitor.Core.Tests.Support.Build;

namespace VramMonitor.Core.Tests.Sampling;

public sealed class SamplerServiceTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private static MonitorSettings Settings() => new MonitorSettings
    {
        SampleInterval = Interval,
        HistoryWindow = TimeSpan.FromMinutes(60),
        MonitoringFloorBytes = Mb(100),
    }.Validated();

    private static (SamplerService Sampler, FakeGpuMemoryProvider Provider, FakeClock Clock, FakeMetadataResolver Meta)
        NewSampler(Action<FakeGpuMemoryProvider>? script = null)
    {
        var provider = new FakeGpuMemoryProvider();
        script?.Invoke(provider);

        var clock = new FakeClock(T0);
        var meta = new FakeMetadataResolver();
        var sampler = new SamplerService(provider, meta, Settings(), clock);
        sampler.SetGpu(provider.Gpu);

        return (sampler, provider, clock, meta);
    }

    [Fact]
    public async Task A_provider_that_throws_is_recorded_as_a_failed_sample_and_sampling_continues()
    {
        // An escaping exception would kill the background task silently, leaving the tray showing stale
        // values with no marker and no rising error count.
        (SamplerService sampler, FakeGpuMemoryProvider provider, FakeClock clock, _) =
            NewSampler(p => p.ThenMeasuring((1, 500)).ThenThrowing("counter blew up").ThenMeasuring((1, 500)));

        await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);
        clock.Advance(Interval);
        await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);
        clock.Advance(Interval);
        await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);

        MonitorSnapshot snapshot = sampler.Current;

        Assert.Equal(3, provider.SnapshotCalls);
        Assert.Equal(3, snapshot.Samples.Count);
        Assert.Equal(ProbeOutcome.Failed, snapshot.Samples[1].Outcome);
        Assert.Equal("counter blew up", snapshot.Samples[1].FailureReason);
        Assert.Equal(1, snapshot.Health.FailedProbeCount);
        Assert.Equal(ProbeOutcome.Ok, snapshot.Samples[2].Outcome);
    }

    [Fact]
    public async Task A_failed_probe_preserves_the_history_gathered_before_it()
    {
        (SamplerService sampler, _, FakeClock clock, _) =
            NewSampler(p => p.ThenMeasuring((1, 500)).ThenFailing("probe lost"));

        await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);
        clock.Advance(Interval);
        await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sampler.Current.Samples.Count);
        Assert.Equal(ProbeOutcome.Ok, sampler.Current.Samples[0].Outcome);
        Assert.Single(sampler.Current.Samples[0].Observations);
    }

    [Fact]
    public async Task Late_and_skipped_cycles_are_counted_separately()
    {
        (SamplerService sampler, _, FakeClock clock, _) = NewSampler();

        await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);
        clock.Advance(Interval);                                  // on time
        await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);
        clock.Advance(Interval * 1.7);                            // late
        await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);
        clock.Advance(Interval * 5);                              // skipped, e.g. the machine slept
        await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, sampler.Current.Health.LateCycles);
        Assert.Equal(1, sampler.Current.Health.SkippedCycles);
    }

    [Fact]
    public async Task A_backwards_wall_clock_step_does_not_break_sampling()
    {
        // Timestamps derive from a monotonic delta, so a time-service correction cannot produce an
        // out-of-order sample -- which the history store would otherwise reject outright.
        //
        // Several cycles run AFTER the step on purpose. An earlier version of this test pumped only one,
        // and passed: the damage was done to the anchor, so the first cycle still worked and every later
        // one failed. One cycle is not enough to prove monotonicity survives.
        (SamplerService sampler, _, FakeClock clock, _) = NewSampler();

        await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);
        clock.Advance(Interval);
        await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);

        clock.StepWallClock(TimeSpan.FromHours(-3));              // clock corrected backwards

        for (int i = 0; i < 3; i++)
        {
            clock.Advance(Interval);
            await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);
        }

        IReadOnlyList<GpuSample> samples = sampler.Current.Samples;
        Assert.Equal(5, samples.Count);
        Assert.Equal(0, sampler.Current.Health.FailedProbeCount);

        for (int i = 1; i < samples.Count; i++)
        {
            Assert.True(
                samples[i].TimestampUtc > samples[i - 1].TimestampUtc,
                $"sample {i} at {samples[i].TimestampUtc:O} did not advance past {samples[i - 1].TimestampUtc:O}");
        }
    }

    [Fact]
    public async Task Sampling_keeps_its_cadence_after_a_backwards_wall_clock_step()
    {
        // Spacing must stay one interval apart: the monitor keeps its own timeline rather than following
        // the correction, so aggressive-duration weighting is unaffected.
        (SamplerService sampler, _, FakeClock clock, _) = NewSampler();

        await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);
        clock.StepWallClock(TimeSpan.FromMinutes(-90));

        for (int i = 0; i < 3; i++)
        {
            clock.Advance(Interval);
            await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);
        }

        IReadOnlyList<GpuSample> samples = sampler.Current.Samples;
        for (int i = 1; i < samples.Count; i++)
        {
            Assert.Equal(Interval, samples[i].TimestampUtc - samples[i - 1].TimestampUtc);
        }

        Assert.Equal(0, sampler.Current.Health.SkippedCycles);
    }

    [Fact]
    public async Task A_forward_wall_clock_correction_is_followed()
    {
        // Forward is the case where following the clock is right: our timeline is behind reality.
        (SamplerService sampler, _, FakeClock clock, _) = NewSampler();

        await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);
        DateTimeOffset first = sampler.Current.Samples[0].TimestampUtc;

        clock.StepWallClock(TimeSpan.FromHours(2));
        clock.Advance(Interval);
        await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);

        DateTimeOffset second = sampler.Current.Samples[^1].TimestampUtc;
        Assert.True(second - first > TimeSpan.FromMinutes(90));
        Assert.Equal(1, sampler.Current.Health.SkippedCycles);
    }

    [Fact]
    public async Task A_settings_change_re_evaluates_retained_history_without_probing_again()
    {
        (SamplerService sampler, FakeGpuMemoryProvider provider, FakeClock clock, _) = NewSampler();
        provider.Fallback = (gpu, at) => new GpuSnapshot(
            gpu, at, ProbeOutcome.Ok, Mb(8000), [new RawProcessMeasurement(1, Mb(500), null)]);

        for (int i = 0; i < 30; i++)
        {
            await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);
            clock.Advance(Interval);
        }

        int callsBefore = provider.SnapshotCalls;
        Assert.True(sampler.Current.Analysis.AllConsumers.Single().IsAggressive);

        // Raise the threshold far beyond what has accumulated: the same history must now read as not
        // aggressive, with no new probe taken.
        sampler.UpdateSettings(Settings() with { AggressiveDurationThreshold = TimeSpan.FromHours(2) });
        Assert.True(sampler.PumpSettings());

        Assert.Equal(callsBefore, provider.SnapshotCalls);
        Assert.False(sampler.Current.Analysis.AllConsumers.Single().IsAggressive);
    }

    [Fact]
    public async Task Shrinking_the_history_window_evicts_immediately()
    {
        (SamplerService sampler, _, FakeClock clock, _) = NewSampler();

        for (int i = 0; i < 40; i++)
        {
            await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);
            clock.Advance(Interval);
        }

        int before = sampler.Current.Samples.Count;
        sampler.UpdateSettings(Settings() with { HistoryWindow = TimeSpan.FromMinutes(1) });
        sampler.PumpSettings();

        Assert.True(sampler.Current.Samples.Count < before);
    }

    [Fact]
    public async Task The_published_snapshot_is_a_copy_that_later_sampling_cannot_mutate()
    {
        // Export reads this from another thread while sampling continues; handing out the store's live
        // collections would leave it reading torn state mid-trim.
        (SamplerService sampler, _, FakeClock clock, _) = NewSampler();

        await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);
        MonitorSnapshot captured = sampler.Current;
        int capturedCount = captured.Samples.Count;

        for (int i = 0; i < 5; i++)
        {
            clock.Advance(Interval);
            await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(capturedCount, captured.Samples.Count);
        Assert.True(sampler.Current.Samples.Count > capturedCount);
    }

    [Fact]
    public async Task Selecting_a_different_gpu_clears_history_but_a_new_luid_for_the_same_gpu_does_not()
    {
        (SamplerService sampler, FakeGpuMemoryProvider provider, FakeClock clock, _) = NewSampler();

        for (int i = 0; i < 3; i++)
        {
            await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);
            clock.Advance(Interval);
        }

        Assert.Equal(3, sampler.Current.Samples.Count);

        // A driver reset: same adapter, new LUID. History must survive -- this is exactly the moment the
        // user wants to look at it.
        var sameGpuNewLuid = new GpuInfo(
            new GpuId("luid_0x0_0xNEW"), provider.Gpu.Selector, provider.Gpu.Description,
            provider.Gpu.DedicatedVideoMemoryBytes, false);
        sampler.RequestGpuChange(sameGpuNewLuid);
        sampler.PumpSettings();
        Assert.Equal(3, sampler.Current.Samples.Count);

        // A genuinely different adapter does clear it.
        var differentGpu = new GpuInfo(
            new GpuId("luid_0x0_0x2"), new GpuSelector(0x8086, 0x7D67, 0, "Intel Graphics", 1),
            "Intel Graphics", 128 * 1024 * 1024, false);
        sampler.RequestGpuChange(differentGpu);
        sampler.PumpSettings();
        await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);

        Assert.Single(sampler.Current.Samples);
    }

    [Fact]
    public async Task Probe_duration_is_tracked_for_the_health_display()
    {
        (SamplerService sampler, _, _, _) = NewSampler();

        await sampler.PumpOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(sampler.Current.Health.AverageProbeDuration >= TimeSpan.Zero);
        Assert.NotNull(sampler.Current.Health.LastSuccessfulSampleUtc);
    }
}
