using VramMonitor.App.Charting;
using Xunit;

namespace VramMonitor.App.Tests.Charting;

public sealed class LogTimeTicksTests
{
    [Fact]
    public void A_sixty_minute_window_gets_the_spec_reference_ticks()
    {
        IReadOnlyList<TimeSpan> ages = LogTimeTicks.For(TimeSpan.FromMinutes(60));

        Assert.Equal(Seconds(3600, 1800, 600, 300, 60, 0), ages);
    }

    [Fact]
    public void The_narrowest_and_widest_windows_get_known_ticks()
    {
        Assert.Equal(Seconds(60, 30, 10, 0), LogTimeTicks.For(TimeSpan.FromMinutes(1)));
        Assert.Equal(Seconds(43200, 7200, 1800, 600, 120, 0), LogTimeTicks.For(TimeSpan.FromHours(12)));
    }

    [Fact]
    public void Every_window_the_settings_allow_gets_well_spaced_ticks_including_both_endpoints()
    {
        foreach (TimeSpan window in ClampedWindows())
        {
            IReadOnlyList<TimeSpan> ages = LogTimeTicks.For(window);
            var scale = new LogTimeScale(DateTimeOffset.UnixEpoch, window);
            double[] xs = [.. ages.Select(a => scale.XForAge(a.TotalSeconds))];

            Assert.InRange(ages.Count, 2, 2 + LogTimeTicks.MaxInteriorTicks);
            Assert.Equal(window, ages[0]);
            Assert.Equal(TimeSpan.Zero, ages[^1]);

            for (int i = 1; i < xs.Length; i++)
            {
                Assert.True(xs[i] - xs[i - 1] >= LogTimeTicks.MinSeparation, $"{window}: ticks {ages[i - 1]} and {ages[i]} are too close");
            }

            foreach (TimeSpan interior in ages.Skip(1).SkipLast(1))
            {
                Assert.Contains(interior, LogTimeTicks.Candidates);
            }
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(60, false)]
    [InlineData(12 * 60, false)]
    public void Seconds_are_shown_only_while_some_interior_tick_is_younger_than_a_minute(int windowMinutes, bool seconds)
    {
        TimeSpan window = TimeSpan.FromMinutes(windowMinutes);

        Assert.Equal(seconds, LogTimeTicks.NeedsSeconds(LogTimeTicks.For(window), window));
    }

    private static TimeSpan[] Seconds(params int[] seconds) => [.. seconds.Select(s => TimeSpan.FromSeconds(s))];

    /// <summary>Every whole minute in the range <c>MonitorSettings.Validated</c> clamps the window to.</summary>
    private static IEnumerable<TimeSpan> ClampedWindows()
    {
        for (int minutes = 1; minutes <= 12 * 60; minutes++) yield return TimeSpan.FromMinutes(minutes);
    }
}
