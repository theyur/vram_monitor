using VramMonitor.App.Charting;
using Xunit;

namespace VramMonitor.App.Tests.Charting;

public sealed class TimeAxisStepTests
{
    [Fact]
    public void Every_candidate_step_divides_twenty_four_hours_exactly()
    {
        // OxyPlot places ticks at whole multiples of the step measured from midnight, so a step that does
        // not divide a day labels 14:31, 14:38, 14:45 instead of round times. That renders perfectly well
        // and merely looks subtly wrong, which is exactly why it needs an assertion rather than a comment.
        foreach (TimeSpan step in TimeAxisStep.Candidates)
        {
            Assert.Equal(0, TimeSpan.FromHours(24).Ticks % step.Ticks);
        }
    }

    [Fact]
    public void No_window_needs_more_labels_than_the_ceiling()
    {
        foreach (TimeSpan window in ClampedWindows())
        {
            TimeSpan step = TimeAxisStep.For(window);
            Assert.True(
                window <= step * TimeAxisStep.MaxLabels,
                $"a {window} window would need more than {TimeAxisStep.MaxLabels} labels of {step}");
        }
    }

    [Fact]
    public void The_step_never_shrinks_as_the_window_grows()
    {
        TimeSpan previous = TimeSpan.Zero;

        foreach (TimeSpan window in ClampedWindows())
        {
            TimeSpan step = TimeAxisStep.For(window);
            Assert.True(step >= previous, $"a {window} window stepped back to {step} from {previous}");
            previous = step;
        }
    }

    [Fact]
    public void The_step_is_always_one_of_the_candidates()
    {
        foreach (TimeSpan window in ClampedWindows())
        {
            Assert.Contains(TimeAxisStep.For(window), TimeAxisStep.Candidates);
        }
    }

    [Theory]
    [InlineData(1, 15)]        // the narrowest window the settings allow
    [InlineData(2, 30)]
    [InlineData(3, 30)]
    [InlineData(10, 120)]      // the window the density flipped on
    [InlineData(60, 600)]      // the shipped default
    [InlineData(12 * 60, 7200)]// the widest window the settings allow
    public void Known_windows_map_to_known_steps(int windowMinutes, int expectedStepSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedStepSeconds),
            TimeAxisStep.For(TimeSpan.FromMinutes(windowMinutes)));
    }

    [Fact]
    public void A_degenerate_window_falls_back_to_the_finest_step()
    {
        // MonitorSettings.Validated() makes these unreachable in practice; the chart still should not
        // depend on that having happened.
        Assert.Equal(TimeAxisStep.Candidates[0], TimeAxisStep.For(TimeSpan.Zero));
        Assert.Equal(TimeAxisStep.Candidates[0], TimeAxisStep.For(TimeSpan.FromMinutes(-5)));
    }

    /// <summary>Every whole minute in the range <c>MonitorSettings.Validated</c> clamps the window to.</summary>
    private static IEnumerable<TimeSpan> ClampedWindows()
    {
        for (int minutes = 1; minutes <= 12 * 60; minutes++) yield return TimeSpan.FromMinutes(minutes);
    }
}
