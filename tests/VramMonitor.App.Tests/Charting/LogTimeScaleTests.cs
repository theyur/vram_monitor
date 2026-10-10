using VramMonitor.App.Charting;
using Xunit;

namespace VramMonitor.App.Tests.Charting;

public sealed class LogTimeScaleTests
{
    private static readonly DateTimeOffset End = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<int> Windows => [1, 60, 12 * 60];

    [Theory]
    [MemberData(nameof(Windows))]
    public void The_window_start_maps_to_the_left_edge_and_the_snapshot_time_to_the_right(int windowMinutes)
    {
        TimeSpan window = TimeSpan.FromMinutes(windowMinutes);
        var scale = new LogTimeScale(End, window);

        Assert.Equal(0, scale.ToX(End - window), precision: 12);
        Assert.Equal(1, scale.ToX(End), precision: 12);
    }

    [Fact]
    public void Age_zero_is_finite()
    {
        // The offset is what makes this hold. A plain log(age) would be minus infinity at the newest sample.
        double x = new LogTimeScale(End, TimeSpan.FromMinutes(60)).XForAge(0);

        Assert.True(double.IsFinite(x));
        Assert.Equal(1, x);
    }

    [Theory]
    [MemberData(nameof(Windows))]
    public void X_increases_strictly_with_the_timestamp(int windowMinutes)
    {
        TimeSpan window = TimeSpan.FromMinutes(windowMinutes);
        var scale = new LogTimeScale(End, window);

        double previous = double.NegativeInfinity;
        for (int s = (int)window.TotalSeconds; s >= 0; s--)
        {
            double x = scale.ToX(End - TimeSpan.FromSeconds(s));
            Assert.True(x > previous, $"age {s}s mapped to {x}, not right of {previous}");
            previous = x;
        }
    }

    [Theory]
    [MemberData(nameof(Windows))]
    public void Forward_and_inverse_mappings_round_trip(int windowMinutes)
    {
        TimeSpan window = TimeSpan.FromMinutes(windowMinutes);
        var scale = new LogTimeScale(End, window);

        foreach (double fraction in new[] { 0, 0.001, 0.1, 0.25, 0.5, 0.75, 0.999, 1 })
        {
            double age = window.TotalSeconds * fraction;
            Assert.Equal(age, scale.AgeSecondsAt(scale.XForAge(age)), precision: 6);

            DateTimeOffset at = End - TimeSpan.FromSeconds(age);
            Assert.True(
                (scale.TimestampAt(scale.ToX(at)) - at).Duration() < TimeSpan.FromMilliseconds(1),
                $"{at:O} came back as {scale.TimestampAt(scale.ToX(at)):O}");
        }

        foreach (double x in new[] { 0, 0.2, 0.5, 0.8, 1 })
        {
            Assert.Equal(x, scale.XForAge(scale.AgeSecondsAt(x)), precision: 12);
        }
    }

    [Fact]
    public void Equal_steps_take_more_width_when_they_are_recent()
    {
        var scale = new LogTimeScale(End, TimeSpan.FromMinutes(60));

        double newest = scale.ToX(End) - scale.ToX(End.AddSeconds(-1));
        double oldest = scale.ToX(End.AddSeconds(-3599)) - scale.ToX(End.AddSeconds(-3600));

        Assert.True(newest > 10 * oldest, $"newest second {newest}, oldest second {oldest}");
    }

    [Theory]
    [InlineData(1, 0.169)]
    [InlineData(5, 0.436)]
    [InlineData(15, 0.674)]
    public void The_spec_reference_shares_of_a_sixty_minute_window_hold(int newestMinutes, double share)
    {
        var scale = new LogTimeScale(End, TimeSpan.FromMinutes(60));

        double width = 1 - scale.ToX(End - TimeSpan.FromMinutes(newestMinutes));

        Assert.Equal(share, width, precision: 3);
    }

    [Fact]
    public void The_same_instant_in_different_offsets_maps_identically()
    {
        var scale = new LogTimeScale(End, TimeSpan.FromMinutes(60));
        var utc = new DateTimeOffset(2026, 10, 10, 11, 37, 12, TimeSpan.Zero);
        DateTimeOffset kolkata = utc.ToOffset(TimeSpan.FromHours(5.5));

        Assert.Equal(scale.ToX(utc), scale.ToX(kolkata));
    }

    [Fact]
    public void A_timestamp_older_than_the_window_keeps_a_negative_x()
    {
        // Clamping it to zero would stack every older point on the left edge; the plot clips it instead.
        var scale = new LogTimeScale(End, TimeSpan.FromMinutes(60));

        double x = scale.ToX(End - TimeSpan.FromMinutes(61));

        Assert.True(x < 0);
        Assert.True(x < scale.ToX(End - TimeSpan.FromMinutes(60.5)));
    }

    [Fact]
    public void A_future_timestamp_is_refused_rather_than_mapped()
    {
        var scale = new LogTimeScale(End, TimeSpan.FromMinutes(60));

        Assert.True(scale.IsFuture(End.AddTicks(1)));
        Assert.False(scale.IsFuture(End));
        Assert.Throws<ArgumentOutOfRangeException>(() => scale.ToX(End.AddSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => scale.XForAge(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => scale.XForAge(double.NaN));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public void A_non_positive_window_is_refused(int windowSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogTimeScale(End, TimeSpan.FromSeconds(windowSeconds)));
    }

    [Fact]
    public void The_inverse_is_total_for_any_finite_x()
    {
        // OxyPlot probes labels a hair outside [0, 1] during layout; throwing there would fault a render.
        var scale = new LogTimeScale(End, TimeSpan.FromMinutes(60));

        Assert.True(scale.AgeSecondsAt(-0.01) > 3600);
        Assert.True(scale.AgeSecondsAt(1.01) < 0);
        Assert.True(scale.TimestampAt(1.01) > End);
        Assert.Equal(-0.01, scale.XForAge(scale.AgeSecondsAt(-0.01)), precision: 12);
        Assert.Throws<ArgumentOutOfRangeException>(() => scale.AgeSecondsAt(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => scale.AgeSecondsAt(double.PositiveInfinity));
    }
}
