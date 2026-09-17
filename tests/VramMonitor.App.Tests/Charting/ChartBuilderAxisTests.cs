using OxyPlot;
using OxyPlot.Axes;
using VramMonitor.App.Charting;
using VramMonitor.Core.Configuration;
using VramMonitor.Core.Model;
using Xunit;

namespace VramMonitor.App.Tests.Charting;

public sealed class ChartBuilderAxisTests
{
    [Fact]
    public void The_time_axis_step_is_pinned_rather_than_left_to_the_plot_width()
    {
        DateTimeAxis axis = TimeAxisFor(TimeSpan.FromMinutes(10));

        // The regression this guards: with MajorStep unset, OxyPlot derives the step from the plot area's
        // pixel width. That width is not constant at a constant window size -- the left axis' automatic
        // Maximum widens its labels as data arrives and the outside legend re-flows -- so a ten-minute
        // window flipped between a two-minute and a one-minute step on successive refreshes.
        Assert.False(double.IsNaN(axis.MajorStep));
        Assert.Equal(TimeSpan.FromMinutes(2).TotalDays, axis.MajorStep);

        // Matching the major step keeps the axis line free of intermediate marks, because coincident minor
        // ticks are dropped. Left unset, OxyPlot uses a fifth of the major step and draws twenty of them.
        Assert.Equal(axis.MajorStep, axis.MinorStep);

        // Seconds would be constant at a two-minute step, so they are dropped to buy label width.
        Assert.Equal("HH:mm", axis.StringFormat);
    }

    [Fact]
    public void A_window_stepped_finer_than_a_minute_keeps_the_seconds_in_its_labels()
    {
        DateTimeAxis axis = TimeAxisFor(TimeSpan.FromMinutes(2));

        Assert.Equal(TimeSpan.FromSeconds(30).TotalDays, axis.MajorStep);
        Assert.Equal("HH:mm:ss", axis.StringFormat);
    }

    [Fact]
    public void The_axis_still_spans_exactly_the_history_window()
    {
        // Pinning the step must not have snapped the range: the right edge means "now", and the span has to
        // keep agreeing with the "window N min" the status line reports.
        TimeSpan window = TimeSpan.FromMinutes(10);
        DateTimeAxis axis = TimeAxisFor(window);

        double spanInDays = axis.Maximum - axis.Minimum;

        Assert.Equal(window.TotalDays, spanInDays, precision: 9);
    }

    private static DateTimeAxis TimeAxisFor(TimeSpan historyWindow)
    {
        MonitorSettings settings = new MonitorSettings { HistoryWindow = historyWindow }.Validated();
        PlotModel model = ChartBuilder.Build(MonitorSnapshot.Empty(settings), selectedKey: null, expandedKeys: []);

        return Assert.Single(model.Axes.OfType<DateTimeAxis>());
    }
}
