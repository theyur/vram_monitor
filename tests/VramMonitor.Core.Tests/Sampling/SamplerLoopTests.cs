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
/// could ever see. They use real time, so the interval is the minimum the settings allow, except where a
/// test needs room between ticks to observe something that must happen without one.
/// </remarks>
public sealed class SamplerLoopTests
{
    private static readonly TimeSpan Interval = MonitorSettings.MinSampleInterval;   // 1 s

    private static MonitorSettings Settings(TimeSpan? interval = null) => new MonitorSettings
    {
        SampleInterval = interval ?? Interval,
        HistoryWindow = TimeSpan.FromMinutes(10),
        MonitoringFloorBytes = Build.Mb(100),
        AggressiveDurationThreshold = TimeSpan.FromSeconds(2),
    }.Validated();

    private static SamplerService NewSampler(out FakeGpuMemoryProvider provider, TimeSpan? interval = null)
    {
        provider = new FakeGpuMemoryProvider();
        FakeGpuMemoryProvider captured = provider;
        captured.Fallback = (gpu, at) => new GpuSnapshot(
            gpu, at, ProbeOutcome.Ok, Build.Mb(8000),
            [new RawProcessMeasurement(1, Build.Mb(500), null)]);

        var sampler = new SamplerService(captured, new FakeMetadataResolver(), Settings(interval));
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
            () => sampler.Current.Samples.Count >= 4, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.True(reached, $"only {provider.SnapshotCalls} probes were taken");

        // Ties the loop to the one-second floor rather than to "something ticked": the old two-second floor
        // could produce neither the published interval nor the cadence. The timestamps come from the
        // sampler's monotonic anchor, so they do not depend on how promptly this test polled.
        IReadOnlyList<GpuSample> samples = sampler.Current.Samples;
        Assert.Equal(TimeSpan.FromSeconds(1), sampler.Current.Settings.SampleInterval);
        Assert.All(samples, s => Assert.Equal(TimeSpan.FromSeconds(1), s.NominalInterval));

        double[] gaps = [.. samples.Skip(1).Select((s, i) => (s.TimestampUtc - samples[i].TimestampUtc).TotalSeconds).Order()];
        Assert.InRange(gaps[gaps.Length / 2], 0.5, 1.5);
    }

    [Fact]
    public async Task A_settings_change_wakes_the_loop_and_re_evaluates_without_waiting_for_the_next_tick()
    {
        // Four seconds rather than the one-second floor: the re-evaluation is given 1.5 s below, and at a
        // one-second cadence the next tick could land inside that wait and take the probe the assertion says
        // must not happen. Both the initial settings and the update use this interval, so the update cannot
        // shorten the timer's period either.
        TimeSpan interval = TimeSpan.FromSeconds(4);
        await using SamplerService sampler = NewSampler(out FakeGpuMemoryProvider provider, interval);
        sampler.Start();

        Assert.True(await WaitUntil(
            () => sampler.Current.Samples.Count >= 2, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));

        int callsBefore = provider.SnapshotCalls;
        Assert.True(sampler.Current.Analysis.AllConsumers.Single().IsAggressive);

        // Raise the threshold out of reach. The published snapshot must change well inside one interval,
        // and without a new probe.
        sampler.UpdateSettings(Settings(interval) with { AggressiveDurationThreshold = TimeSpan.FromHours(2) });

        bool reEvaluated = await WaitUntil(
            () => !sampler.Current.Analysis.AllConsumers.Single().IsAggressive,
            TimeSpan.FromMilliseconds(1500),
            TestContext.Current.CancellationToken);

        Assert.True(reEvaluated, "the settings change did not take effect before the next tick");
        Assert.Equal(callsBefore, provider.SnapshotCalls);
    }

    [Fact]
    public async Task Changing_the_interval_at_runtime_in_both_directions_keeps_sampling_and_history()
    {
        // The interval is applied by assigning Period; rebuilding the timer would invalidate the pending wait.
        await using SamplerService sampler = NewSampler(out _);
        sampler.Start();
        CancellationToken ct = TestContext.Current.CancellationToken;

        Assert.True(await WaitUntil(() => sampler.Current.Samples.Count >= 2, TimeSpan.FromSeconds(10), ct));
        DateTimeOffset firstSample = sampler.Current.Samples[0].TimestampUtc;

        foreach (TimeSpan interval in new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1) })
        {
            sampler.UpdateSettings(Settings(interval));
            Assert.True(
                await WaitUntil(() => sampler.Current.Settings.SampleInterval == interval, TimeSpan.FromSeconds(2), ct),
                $"the change to {interval} was not applied");

            // Two probes after the change, so at least one was scheduled by the new period rather than
            // already pending under the old one.
            int samplesAtChange = sampler.Current.Samples.Count;
            Assert.True(
                await WaitUntil(() => sampler.Current.Samples.Count >= samplesAtChange + 2, TimeSpan.FromSeconds(12), ct),
                $"sampling stopped after the interval was changed to {interval}");

            IReadOnlyList<GpuSample> samples = sampler.Current.Samples;
            Assert.Equal(interval, samples[^1].NominalInterval);
            Assert.Equal(firstSample, samples[0].TimestampUtc);   // history retained across the change
        }

        // Each sample still records the interval it was taken under, so durations stay right after a change.
        IReadOnlyList<GpuSample> all = sampler.Current.Samples;
        Assert.Equal(TimeSpan.FromSeconds(1), all[0].NominalInterval);
        Assert.Contains(all, s => s.NominalInterval == TimeSpan.FromSeconds(3));
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
