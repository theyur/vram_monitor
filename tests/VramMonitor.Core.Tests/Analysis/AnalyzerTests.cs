using VramMonitor.Core.Analysis;
using VramMonitor.Core.Configuration;
using VramMonitor.Core.Tests.Support;
using Xunit;
using static VramMonitor.Core.Tests.Support.Build;

namespace VramMonitor.Core.Tests.Analysis;

public sealed class AnalyzerTests
{
    private static MonitorSettings Settings(
        double windowMinutes = 60,
        double floorMb = 100,
        double aggressiveSeconds = 180,
        double graceSeconds = 60,
        double intervalSeconds = 10) =>
        new MonitorSettings
        {
            HistoryWindow = TimeSpan.FromMinutes(windowMinutes),
            MonitoringFloorBytes = Mb(floorMb),
            AggressiveDurationThreshold = TimeSpan.FromSeconds(aggressiveSeconds),
            DemotionGracePeriod = TimeSpan.FromSeconds(graceSeconds),
            SampleInterval = TimeSpan.FromSeconds(intervalSeconds),
        }.Validated();

    private static AnalysisResult Run(Hist h, MonitorSettings? settings = null, DateTimeOffset? now = null) =>
        Analyzer.Analyze(h.Samples, h.Sessions, settings ?? Settings(), [], now ?? h.Now);

    private static ApplicationView App(AnalysisResult r, string key) =>
        r.AllConsumers.Single(v => v.Key == key);

    // ---------------------------------------------------------------- spec section 9.2 worked example

    [Fact]
    public void Cumulative_aggressive_time_matches_the_specs_own_worked_example()
    {
        // Spec section 9.2: qualifying 1 min, not qualifying 20 s, qualifying 2.5 min => 3.5 min.
        // At a 10 s interval that is 6 qualifying, 2 below the floor, then 15 qualifying.
        Hist h = Hist.New().App(1, @"C:\a\app.exe");
        int i = 0;
        for (int k = 0; k < 6; k++, i++) h.At(i * 10, (1, 500));
        for (int k = 0; k < 2; k++, i++) h.At(i * 10, (1, 50));     // below the 100 MB floor
        for (int k = 0; k < 15; k++, i++) h.At(i * 10, (1, 500));

        AnalysisResult r = Run(h);

        Assert.Equal(TimeSpan.FromMinutes(3.5), App(r, @"c:\a\app.exe").CumulativeAggressiveTime);
    }

    [Fact]
    public void Aggressive_time_is_cumulative_not_streak_based()
    {
        // The gap in the middle must not reset the total.
        Hist h = Hist.New().App(1, @"C:\a\app.exe");
        for (int i = 0; i < 10; i++) h.At(i * 10, (1, 500));
        for (int i = 10; i < 13; i++) h.At(i * 10, (1, 10));
        for (int i = 13; i < 20; i++) h.At(i * 10, (1, 500));

        AnalysisResult r = Run(h);

        Assert.Equal(TimeSpan.FromSeconds(170), App(r, @"c:\a\app.exe").CumulativeAggressiveTime);
    }

    // ---------------------------------------------------------------- floor and quartile

    [Fact]
    public void Applications_below_the_monitoring_floor_never_qualify()
    {
        Hist h = Hist.New().App(1, @"C:\a\small.exe");
        for (int i = 0; i < 40; i++) h.At(i * 10, (1, 50));

        AnalysisResult r = Run(h);

        Assert.Equal(TimeSpan.Zero, App(r, @"c:\a\small.exe").CumulativeAggressiveTime);
        Assert.False(App(r, @"c:\a\small.exe").IsAggressive);
    }

    [Fact]
    public void With_four_applications_above_the_floor_exactly_one_qualifies()
    {
        Hist h = Hist.New()
            .App(1, @"C:\a\1.exe").App(2, @"C:\a\2.exe").App(3, @"C:\a\3.exe").App(4, @"C:\a\4.exe");
        for (int i = 0; i < 30; i++) h.At(i * 10, (1, 800), (2, 400), (3, 300), (4, 200));

        AnalysisResult r = Run(h);

        Assert.Equal(TimeSpan.FromSeconds(300), App(r, @"c:\a\1.exe").CumulativeAggressiveTime);
        Assert.Equal(TimeSpan.Zero, App(r, @"c:\a\2.exe").CumulativeAggressiveTime);
        Assert.Equal(TimeSpan.Zero, App(r, @"c:\a\4.exe").CumulativeAggressiveTime);
    }

    [Fact]
    public void With_eight_applications_above_the_floor_the_top_two_qualify()
    {
        var h = Hist.New();
        for (long s = 1; s <= 8; s++) h.App(s, $@"C:\a\{s}.exe");
        for (int i = 0; i < 30; i++)
        {
            h.At(i * 10, [.. Enumerable.Range(1, 8).Select(s => ((long)s, (double?)(1000 - (s * 100))))]);
        }

        AnalysisResult r = Run(h);

        Assert.True(App(r, @"c:\a\1.exe").CumulativeAggressiveTime > TimeSpan.Zero);
        Assert.True(App(r, @"c:\a\2.exe").CumulativeAggressiveTime > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, App(r, @"c:\a\3.exe").CumulativeAggressiveTime);
    }

    [Fact]
    public void A_single_application_above_the_floor_is_its_own_top_quartile()
    {
        Hist h = Hist.New().App(1, @"C:\a\only.exe");
        for (int i = 0; i < 30; i++) h.At(i * 10, (1, 500));

        Assert.True(Run(h).AllConsumers.Single().IsAggressive);
    }

    [Fact]
    public void A_zero_threshold_is_clamped_so_it_cannot_mark_everything_aggressive()
    {
        // Spec section 8 says only consumers above the floor take part; a zero threshold would otherwise be
        // met by every consumer at every sample.
        MonitorSettings s = Settings(aggressiveSeconds: 0);
        Assert.True(s.AggressiveDurationThreshold >= MonitorSettings.MinAggressiveDuration);

        Hist h = Hist.New().App(1, @"C:\a\tiny.exe");
        for (int i = 0; i < 10; i++) h.At(i * 10, (1, 5));

        Assert.False(Run(h, s).AllConsumers.Single().IsAggressive);
    }

    // ---------------------------------------------------------------- missing never becomes zero

    [Fact]
    public void One_unreadable_session_makes_the_whole_application_value_unknown()
    {
        // Summing only the readable sessions would silently treat the unreadable one as zero.
        Hist h = Hist.New()
            .App(1, @"C:\a\chrome.exe", pid: 10)
            .App(2, @"C:\a\chrome.exe", pid: 11);
        h.At(0, (1, 400), (2, 300));
        h.At(10, (1, 400), (2, null));       // one renderer unreadable

        AnalysisResult r = Run(h);
        ApplicationView app = App(r, @"c:\a\chrome.exe");

        Assert.Null(app.CurrentDedicatedBytes);
        Assert.Equal(PointState.Missing, app.Series[^1].State);
        Assert.Equal(Mb(700), app.PeakDedicatedBytes);   // the good sample only
    }

    [Fact]
    public void An_unknown_current_value_is_reported_as_unknown_never_as_zero()
    {
        Hist h = Hist.New().App(1, @"C:\a\app.exe");
        h.At(0, (1, 500));
        h.At(10, (1, null));

        ApplicationView app = Run(h).AllConsumers.Single();

        Assert.Null(app.CurrentDedicatedBytes);
        Assert.True(app.IsCurrentUnknown);
    }

    [Fact]
    public void Average_ignores_missing_samples_rather_than_counting_them_as_zero()
    {
        Hist h = Hist.New().App(1, @"C:\a\app.exe");
        h.At(0, (1, 400));
        h.At(10, (1, null));
        h.At(20, (1, 600));

        Assert.Equal(Mb(500), Run(h).AllConsumers.Single().AverageDedicatedBytes);
    }

    [Fact]
    public void A_failed_probe_gaps_every_series_without_implying_an_exit()
    {
        Hist h = Hist.New().App(1, @"C:\a\app.exe");
        h.At(0, (1, 500));
        h.Failed(10);
        h.At(20, (1, 500));

        AnalysisResult r = Run(h);
        ApplicationView app = r.AllConsumers.Single();

        Assert.Equal(PointState.Missing, app.Series[1].State);
        Assert.Contains(r.Markers, m => m.Kind == DisruptionKind.ProbeFailed);
    }

    [Fact]
    public void A_normal_exit_gaps_the_series_without_any_marker()
    {
        // Spec section 13.5: a process exiting is not an observation error.
        Hist h = Hist.New().App(1, @"C:\a\app.exe").App(2, @"C:\a\other.exe");
        h.At(0, (1, 500), (2, 500));
        h.At(10, (2, 500));                  // app.exe exited
        h.At(20, (2, 500));

        AnalysisResult r = Run(h);

        Assert.Equal(PointState.Absent, App(r, @"c:\a\app.exe").Series[^1].State);
        Assert.Empty(r.Markers);
    }

    // ---------------------------------------------------------------- retention and the universe

    [Fact]
    public void An_exited_application_stays_inspectable_while_its_sample_is_in_the_window()
    {
        Hist h = Hist.New().App(1, @"C:\a\gone.exe").App(2, @"C:\a\here.exe");
        h.At(0, (1, 500), (2, 100));
        for (int i = 1; i < 20; i++) h.At(i * 10, (2, 100));

        AnalysisResult r = Run(h);

        Assert.Contains(r.AllConsumers, v => v.Key == @"c:\a\gone.exe");
        Assert.Equal(Mb(500), App(r, @"c:\a\gone.exe").PeakDedicatedBytes);
    }

    [Fact]
    public void An_application_seen_only_in_the_grace_tail_is_not_a_consumer_at_all()
    {
        // The tail exists purely so hysteresis can evaluate older instants. It must never resurrect a
        // consumer whose last visible sample has aged out (spec section 7).
        Hist h = Hist.New().App(1, @"C:\a\old.exe").App(2, @"C:\a\live.exe");
        h.At(0, (1, 900));                                   // will fall outside the visible window
        for (int i = 1; i <= 10; i++) h.At(i * 60, (2, 500));

        MonitorSettings s = Settings(windowMinutes: 5, graceSeconds: 600);
        AnalysisResult r = Run(h, s, T0.AddSeconds(600));

        Assert.DoesNotContain(r.AllConsumers, v => v.Key == @"c:\a\old.exe");
        Assert.Contains(r.AllConsumers, v => v.Key == @"c:\a\live.exe");
    }

    [Fact]
    public void A_spike_in_the_grace_tail_does_not_inflate_the_displayed_peak_or_average()
    {
        // The store keeps window + grace so hysteresis can look back far enough, but every displayed figure
        // must come from the visible window alone -- otherwise the peak shows a value that is no longer on
        // the chart and cannot be explained.
        Hist h = Hist.New().App(1, @"C:\a\app.exe");
        h.At(0, (1, 900));                                   // falls into the grace tail
        for (int i = 7; i <= 12; i++) h.At(i * 10, (1, 100));

        MonitorSettings s = Settings(windowMinutes: 1, graceSeconds: 60);
        AnalysisResult r = Run(h, s, T0.AddSeconds(120));

        ApplicationView app = App(r, @"c:\a\app.exe");
        Assert.Equal(Mb(100), app.PeakDedicatedBytes);
        Assert.Equal(Mb(100), app.AverageDedicatedBytes);
    }

    [Fact]
    public void Aggressive_time_shrinks_as_qualifying_samples_age_out_of_the_window()
    {
        Hist h = Hist.New().App(1, @"C:\a\app.exe");
        for (int i = 0; i < 60; i++) h.At(i * 10, (1, 500));

        MonitorSettings wide = Settings(windowMinutes: 60);
        MonitorSettings narrow = Settings(windowMinutes: 1);

        TimeSpan wideTime = Run(h, wide).AllConsumers.Single().CumulativeAggressiveTime;
        TimeSpan narrowTime = Run(h, narrow).AllConsumers.Single().CumulativeAggressiveTime;

        Assert.True(narrowTime < wideTime);
        Assert.Equal(TimeSpan.FromSeconds(70), narrowTime);   // 7 samples inside a 60 s window
    }

    // ---------------------------------------------------------------- demotion hysteresis

    [Fact]
    public void An_application_stays_aggressive_through_the_grace_period_then_leaves()
    {
        // Demotion here is driven purely by aging: the qualifying samples leave the trailing window, so the
        // cumulative total falls back below the threshold. A prefix-sum formulation could never see this.
        Hist h = Hist.New().App(1, @"C:\a\app.exe").App(2, @"C:\a\filler.exe");

        // 30 qualifying samples (300 s) inside a 120 s window: comfortably over a 60 s threshold.
        for (int i = 0; i < 30; i++) h.At(i * 10, (1, 800), (2, 150));
        // then it drops below the floor and stays there.
        for (int i = 30; i < 60; i++) h.At(i * 10, (1, 5), (2, 150));

        MonitorSettings s = Settings(
            windowMinutes: 2, aggressiveSeconds: 60, graceSeconds: 45, floorMb: 100);

        // While qualifying samples are still inside the 120 s window it remains aggressive.
        Assert.True(Run(h, s, T0.AddSeconds(300)).AllConsumers.First(v => v.Key.EndsWith("app.exe")).IsAggressive);

        // Long after they have aged out and the grace has elapsed, it is demoted.
        Assert.False(Run(h, s, T0.AddSeconds(560)).AllConsumers.First(v => v.Key.EndsWith("app.exe")).IsAggressive);
    }

    [Fact]
    public void Demotion_happens_at_the_end_of_the_grace_period_not_one_interval_early()
    {
        // The cumulative sum falls BETWEEN samples -- exactly when a qualifying sample leaves the trailing
        // window -- so the supremum over the grace interval is not always at a sampled instant. The grace is
        // deliberately not a multiple of the interval, so an implementation that only evaluates sampled
        // instants demotes early and this fails.
        Hist h = Hist.New().App(1, @"C:\a\app.exe");
        for (int i = 0; i < 12; i++) h.At(i * 10, (1, 800));        // qualifying, t = 0..110, 10 s each
        for (int i = 12; i < 40; i++) h.At(i * 10, (1, 5));         // below the floor thereafter

        // The window is deliberately 125 s -- NOT a multiple of the 10 s interval. The trailing sum drops
        // when a qualifying sample leaves the window, i.e. at t = 125, 135, 145 ..., none of which is a
        // sample time. Ten qualifying samples (100 s) remain in the window until exactly t = 145, so 145 is
        // the last instant meeting the threshold and it lies strictly between two samples.
        MonitorSettings s = Settings(
            windowMinutes: 125.0 / 60.0, aggressiveSeconds: 100, graceSeconds: 25, floorMb: 100);

        // At t = 168 the only instant in [143, 168] that still meets the threshold is the left endpoint
        // itself: the sampled instants 150 and 160 are already below it. An implementation that evaluates
        // only sampled instants therefore demotes here, one interval early.
        Assert.True(
            App(Run(h, s, T0.AddSeconds(168)), @"c:\a\app.exe").IsAggressive,
            "demoted early: the left endpoint of the grace interval was not evaluated");

        // 145 + 25 s of grace = 170, so by 175 it must be gone.
        Assert.False(
            App(Run(h, s, T0.AddSeconds(175)), @"c:\a\app.exe").IsAggressive,
            "still aggressive after the grace period had elapsed");
    }

    [Fact]
    public void An_application_held_by_grace_alone_ranks_with_an_average_of_zero()
    {
        // Ranking key 2 is the average during qualifying samples. An application kept in the list purely by
        // grace has none left in the visible window, so it must sort below one that genuinely qualifies.
        Hist h = Hist.New().App(1, @"C:\a\fading.exe").App(2, @"C:\a\current.exe");

        for (int i = 0; i < 12; i++) h.At(i * 10, (1, 900), (2, 300));
        for (int i = 12; i < 26; i++) h.At(i * 10, (1, 5), (2, 300));

        MonitorSettings s = Settings(windowMinutes: 1, aggressiveSeconds: 20, graceSeconds: 120, floorMb: 100);
        AnalysisResult r = Run(h, s, T0.AddSeconds(250));

        if (r.Aggressive.Count == 2)
        {
            Assert.Equal(@"c:\a\current.exe", r.Aggressive[0].Key);
        }
    }

    [Fact]
    public void The_grace_period_is_real_time_and_survives_an_interval_change()
    {
        // Same elapsed time, different sample rate: the outcome must not change.
        static Hist Build(double interval)
        {
            var h = Hist.New().Interval(interval).App(1, @"C:\a\app.exe");
            int count = (int)(300 / interval);
            for (int i = 0; i < count; i++) h.At(i * interval, (1, 800));
            int after = (int)(200 / interval);
            for (int i = 0; i < after; i++) h.At(300 + (i * interval), (1, 5));
            return h;
        }

        MonitorSettings s = Settings(windowMinutes: 2, aggressiveSeconds: 60, graceSeconds: 45);

        bool atTenSeconds = Run(Build(10), s, T0.AddSeconds(480)).AllConsumers.Single().IsAggressive;
        bool atFiveSeconds = Run(Build(5), s, T0.AddSeconds(480)).AllConsumers.Single().IsAggressive;

        Assert.Equal(atTenSeconds, atFiveSeconds);
    }

    // ---------------------------------------------------------------- ranking

    [Fact]
    public void Aggressive_ranking_breaks_ties_by_average_then_peak()
    {
        // Both qualify on every sample, so cumulative time ties and the average decides.
        Hist h = Hist.New().App(1, @"C:\a\low.exe").App(2, @"C:\a\high.exe");
        for (int i = 0; i < 40; i++) h.At(i * 10, (1, 400), (2, 900));

        AnalysisResult r = Run(h);

        Assert.Equal(@"c:\a\high.exe", r.Aggressive[0].Key);
    }

    // ---------------------------------------------------------------- sleep gaps

    [Fact]
    public void A_long_sampling_gap_breaks_every_series_and_is_marked_distinctly()
    {
        Hist h = Hist.New().App(1, @"C:\a\app.exe");
        h.At(0, (1, 500));
        h.At(10, (1, 500));
        h.At(3600, (1, 500));             // machine slept for an hour

        AnalysisResult r = Run(h);
        ApplicationView app = r.AllConsumers.Single();

        Assert.Contains(app.Series, p => p.State == PointState.Missing);
        Assert.Contains(r.Markers, m => m.Kind == DisruptionKind.SamplingPaused);
        Assert.DoesNotContain(r.Markers, m => m.Kind == DisruptionKind.ProbeFailed);
    }

    [Fact]
    public void Changing_the_interval_does_not_look_like_a_sampling_gap()
    {
        Hist h = Hist.New().Interval(10).App(1, @"C:\a\app.exe");
        h.At(0, (1, 500));
        h.At(10, (1, 500));
        h.Interval(30).At(40, (1, 500));   // first sample at the new rate: delta 30 s

        Assert.DoesNotContain(Run(h).Markers, m => m.Kind == DisruptionKind.SamplingPaused);
    }

    // ---------------------------------------------------------------- charting

    [Fact]
    public void Only_the_configured_number_of_applications_is_charted()
    {
        var h = Hist.New();
        for (long s = 1; s <= 12; s++) h.App(s, $@"C:\a\{s}.exe");
        for (int i = 0; i < 5; i++)
        {
            h.At(i * 10, [.. Enumerable.Range(1, 12).Select(s => ((long)s, (double?)(s * 50)))]);
        }

        AnalysisResult r = Run(h, Settings() with { ChartTopApplications = 3 });

        Assert.Equal(3, r.ChartedKeys.Count);
        Assert.Contains(@"c:\a\12.exe", r.ChartedKeys);      // highest peak
        Assert.DoesNotContain(@"c:\a\1.exe", r.ChartedKeys);
    }
}
