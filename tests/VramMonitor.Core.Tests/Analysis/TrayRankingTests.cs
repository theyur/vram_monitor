using VramMonitor.Core.Analysis;
using VramMonitor.Core.Configuration;
using VramMonitor.Core.Tests.Support;
using Xunit;
using static VramMonitor.Core.Tests.Support.Build;

namespace VramMonitor.Core.Tests.Analysis;

/// <summary>Covers the tray top-5 rules of spec section 10.1: current values, stable ordering, no carry-forward.</summary>
public sealed class TrayRankingTests
{
    private static MonitorSettings Settings(
        double marginMb = 32, double marginFraction = 0.05, int entries = 5, double tauSeconds = 30) =>
        new MonitorSettings
        {
            MonitoringFloorBytes = Mb(1),
            TrayHysteresisMarginBytes = Mb(marginMb),
            TrayHysteresisMarginFraction = marginFraction,
            TrayEntryCount = entries,
            TraySmoothingTimeConstant = TimeSpan.FromSeconds(tauSeconds),
            HistoryWindow = TimeSpan.FromMinutes(60),
        }.Validated();

    private static AnalysisResult Run(Hist h, IReadOnlyList<string> previousTop, MonitorSettings? s = null) =>
        Analyzer.Analyze(h.Samples, h.Sessions, s ?? Settings(), previousTop, h.Now);

    private static string[] Keys(AnalysisResult r) => [.. r.TrayTop5.Select(t => t.Key)];

    [Fact]
    public void The_tray_lists_the_largest_current_consumers()
    {
        var h = Hist.New();
        for (long s = 1; s <= 7; s++) h.App(s, $@"C:\a\{s}.exe");
        for (int i = 0; i < 5; i++)
        {
            h.At(i * 10, [.. Enumerable.Range(1, 7).Select(s => ((long)s, (double?)(s * 100)))]);
        }

        AnalysisResult r = Run(h, []);

        Assert.Equal(5, r.TrayTop5.Count);
        Assert.Equal(@"c:\a\7.exe", r.TrayTop5[0].Key);
        Assert.Equal(Mb(700), r.TrayTop5[0].CurrentDedicatedBytes);
    }

    [Fact]
    public void Entries_show_the_raw_current_value_not_the_smoothed_one()
    {
        // Smoothing exists to stabilise ordering. Displaying it would show a number that was never measured.
        Hist h = Hist.New().App(1, @"C:\a\one.exe");
        for (int i = 0; i < 10; i++) h.At(i * 10, (1, 100));
        h.At(100, (1, 900));                       // a sharp jump the EMA will lag behind

        AnalysisResult r = Run(h, []);

        Assert.Equal(Mb(900), r.TrayTop5.Single().CurrentDedicatedBytes);
    }

    [Fact]
    public void An_application_absent_from_the_latest_sample_is_not_listed()
    {
        // The tooltip claims to show what is happening now; an exited application must not linger with a
        // stale smoothed value.
        Hist h = Hist.New().App(1, @"C:\a\gone.exe").App(2, @"C:\a\here.exe");
        for (int i = 0; i < 5; i++) h.At(i * 10, (1, 900), (2, 100));
        h.At(50, (2, 100));                        // gone.exe exited

        AnalysisResult r = Run(h, [@"c:\a\gone.exe"]);

        Assert.DoesNotContain(@"c:\a\gone.exe", Keys(r));
        Assert.Contains(@"c:\a\here.exe", Keys(r));
    }

    [Fact]
    public void An_application_whose_latest_value_is_unreadable_is_not_listed()
    {
        Hist h = Hist.New().App(1, @"C:\a\one.exe").App(2, @"C:\a\two.exe");
        for (int i = 0; i < 5; i++) h.At(i * 10, (1, 900), (2, 100));
        h.At(50, (1, null), (2, 100));             // present but unreadable

        Assert.DoesNotContain(@"c:\a\one.exe", Keys(Run(h, [@"c:\a\one.exe"])));
    }

    [Fact]
    public void A_marginal_challenger_cannot_displace_an_incumbent()
    {
        // The whole point of the hysteresis: a few megabytes of drift must not reshuffle the list.
        var h = Hist.New();
        for (long s = 1; s <= 6; s++) h.App(s, $@"C:\a\{s}.exe");
        for (int i = 0; i < 10; i++)
        {
            h.At(i * 10, (1, 500), (2, 400), (3, 300), (4, 200), (5, 100), (6, 105));
        }

        // 6.exe leads 5.exe by only 5 MB, well inside the 32 MB margin.
        string[] incumbents = [@"c:\a\1.exe", @"c:\a\2.exe", @"c:\a\3.exe", @"c:\a\4.exe", @"c:\a\5.exe"];
        AnalysisResult r = Run(h, incumbents);

        Assert.Contains(@"c:\a\5.exe", Keys(r));
        Assert.DoesNotContain(@"c:\a\6.exe", Keys(r));
    }

    [Fact]
    public void A_decisive_challenger_does_displace_the_weakest_incumbent()
    {
        var h = Hist.New();
        for (long s = 1; s <= 6; s++) h.App(s, $@"C:\a\{s}.exe");
        for (int i = 0; i < 10; i++)
        {
            h.At(i * 10, (1, 500), (2, 400), (3, 300), (4, 200), (5, 100), (6, 900));
        }

        string[] incumbents = [@"c:\a\1.exe", @"c:\a\2.exe", @"c:\a\3.exe", @"c:\a\4.exe", @"c:\a\5.exe"];
        AnalysisResult r = Run(h, incumbents);

        Assert.Contains(@"c:\a\6.exe", Keys(r));
        Assert.DoesNotContain(@"c:\a\5.exe", Keys(r));
        Assert.Equal(5, r.TrayTop5.Count);
    }

    [Fact]
    public void Ordering_does_not_oscillate_when_two_applications_stay_close()
    {
        // Feed the previous result back in repeatedly, as the running application does. A margin charged
        // against the incumbent in both directions means neither can keep stealing the other's place.
        var h = Hist.New().App(1, @"C:\a\a.exe").App(2, @"C:\a\b.exe");
        for (int i = 0; i < 10; i++) h.At(i * 10, (1, 300), (2, 305));

        IReadOnlyList<string> previous = [];
        var seen = new List<string>();
        for (int round = 0; round < 6; round++)
        {
            AnalysisResult r = Run(h, previous);
            previous = Keys(r);
            seen.Add(string.Join(",", previous));
        }

        Assert.Single(seen.Skip(1).Distinct());     // settles immediately and never flips again
    }

    [Fact]
    public void The_displaced_incumbent_is_the_weakest_by_value_not_the_one_in_the_last_position()
    {
        // Incumbents are supplied in an order that does NOT match their sizes, so an implementation that
        // simply drops the last entry would evict the wrong one and this would fail.
        var h = Hist.New();
        for (long s = 1; s <= 6; s++) h.App(s, $@"C:\a\{s}.exe");
        for (int i = 0; i < 10; i++)
        {
            h.At(i * 10, (1, 100), (2, 900), (3, 800), (4, 700), (5, 600), (6, 500));
        }

        // 1.exe is the smallest but sits first; 5.exe is the largest incumbent but sits last.
        string[] incumbents = [@"c:\a\1.exe", @"c:\a\2.exe", @"c:\a\3.exe", @"c:\a\4.exe", @"c:\a\5.exe"];
        AnalysisResult r = Run(h, incumbents);

        Assert.DoesNotContain(@"c:\a\1.exe", Keys(r));   // weakest by value, evicted
        Assert.Contains(@"c:\a\5.exe", Keys(r));         // last by position, kept
        Assert.Contains(@"c:\a\6.exe", Keys(r));         // the challenger got in
    }

    [Fact]
    public void The_challenger_takes_the_displaced_slot_rather_than_going_to_the_end()
    {
        var h = Hist.New();
        for (long s = 1; s <= 6; s++) h.App(s, $@"C:\a\{s}.exe");
        for (int i = 0; i < 10; i++)
        {
            h.At(i * 10, (1, 100), (2, 900), (3, 800), (4, 700), (5, 600), (6, 500));
        }

        string[] incumbents = [@"c:\a\1.exe", @"c:\a\2.exe", @"c:\a\3.exe", @"c:\a\4.exe", @"c:\a\5.exe"];
        string[] keys = Keys(Run(h, incumbents));

        // 1.exe held slot 0, so its replacement inherits that slot rather than being appended at the end.
        // The single ordering pass then moves it down at most one place, because 2.exe beats it by far more
        // than the margin. Landing at 0 or 1 proves it took the displaced slot; landing last would mean it
        // had been appended.
        Assert.InRange(Array.IndexOf(keys, @"c:\a\6.exe"), 0, 1);
    }

    [Fact]
    public void Several_qualifying_challengers_all_get_in()
    {
        // Displacement repeats rather than admitting one newcomer per sample.
        var h = Hist.New();
        for (long s = 1; s <= 8; s++) h.App(s, $@"C:\a\{s}.exe");
        for (int i = 0; i < 10; i++)
        {
            h.At(i * 10, (1, 10), (2, 20), (3, 30), (4, 40), (5, 50), (6, 900), (7, 800), (8, 700));
        }

        string[] incumbents = [@"c:\a\1.exe", @"c:\a\2.exe", @"c:\a\3.exe", @"c:\a\4.exe", @"c:\a\5.exe"];
        string[] keys = Keys(Run(h, incumbents));

        Assert.Contains(@"c:\a\6.exe", keys);
        Assert.Contains(@"c:\a\7.exe", keys);
        Assert.Contains(@"c:\a\8.exe", keys);
    }

    [Fact]
    public void Fewer_eligible_applications_than_slots_are_listed_without_padding()
    {
        Hist h = Hist.New().App(1, @"C:\a\one.exe").App(2, @"C:\a\two.exe");
        h.At(0, (1, 500), (2, 400));

        Assert.Equal(2, Run(h, []).TrayTop5.Count);
    }

    [Fact]
    public void The_tray_is_empty_when_every_visible_sample_failed()
    {
        Hist h = Hist.New().App(1, @"C:\a\one.exe");
        h.Failed(0);
        h.Failed(10);

        Assert.Empty(Run(h, []).TrayTop5);
    }

    [Fact]
    public void Smoothing_restarts_after_a_gap_rather_than_carrying_a_value_across_it()
    {
        // An application that was huge, disappeared, and came back small must be ranked on what it is now.
        Hist h = Hist.New().App(1, @"C:\a\spiky.exe").App(2, @"C:\a\steady.exe");
        for (int i = 0; i < 10; i++) h.At(i * 10, (1, 2000), (2, 300));   // spiky.exe dominates
        for (int i = 10; i < 20; i++) h.At(i * 10, (2, 300));             // spiky.exe exits
        for (int i = 20; i < 23; i++) h.At(i * 10, (1, 50), (2, 300));    // returns much smaller

        AnalysisResult r = Run(h, []);

        Assert.Equal(@"c:\a\steady.exe", r.TrayTop5[0].Key);
        Assert.Equal(Mb(50), r.TrayTop5.Single(t => t.Key == @"c:\a\spiky.exe").CurrentDedicatedBytes);
    }

    [Fact]
    public void The_result_is_deterministic_when_values_tie()
    {
        var h = Hist.New();
        for (long s = 1; s <= 8; s++) h.App(s, $@"C:\a\{s}.exe");
        for (int i = 0; i < 3; i++)
        {
            h.At(i * 10, [.. Enumerable.Range(1, 8).Select(s => ((long)s, (double?)500))]);
        }

        string[] first = Keys(Run(h, []));
        string[] second = Keys(Run(h, []));

        Assert.Equal(first, second);
        Assert.Equal(5, first.Length);
    }

    [Fact]
    public void The_entry_count_is_configurable()
    {
        var h = Hist.New();
        for (long s = 1; s <= 8; s++) h.App(s, $@"C:\a\{s}.exe");
        h.At(0, [.. Enumerable.Range(1, 8).Select(s => ((long)s, (double?)(s * 100)))]);

        Assert.Equal(3, Run(h, [], Settings(entries: 3)).TrayTop5.Count);
    }
}
