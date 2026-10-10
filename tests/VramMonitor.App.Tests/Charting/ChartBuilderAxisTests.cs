using System.Globalization;
using System.Text.RegularExpressions;
using OxyPlot;
using OxyPlot.Axes;
using VramMonitor.App.Charting;
using VramMonitor.Core.Model;
using Xunit;
using static VramMonitor.App.Tests.Support.SnapshotFixture;

namespace VramMonitor.App.Tests.Charting;

public sealed partial class ChartBuilderAxisTests
{
    /// <summary>The tick ages the spec lists for a 60-minute window, by ascending X.</summary>
    private static readonly int[] ReferenceAgesSeconds = [3600, 1800, 600, 300, 60, 0];

    [Fact]
    public void The_time_axis_is_a_normalized_log_age_axis_pinned_to_the_unit_range()
    {
        LogTimeAxis axis = TimeAxisOf(Build(Snapshot()));

        Assert.Equal(ChartBuilder.TimeAxisKey, axis.Key);
        Assert.Equal(AxisPosition.Bottom, axis.Position);
        Assert.Equal(0, axis.Minimum);
        Assert.Equal(1, axis.Maximum);
        Assert.Equal(0, axis.MinimumPadding);
        Assert.Equal(0, axis.MaximumPadding);
        Assert.False(axis.IsZoomEnabled);
        Assert.False(axis.IsPanEnabled);
        Assert.Equal("Time (logarithmic age)", axis.Title);
        Assert.Equal(LineStyle.Dot, axis.MajorGridlineStyle);
        Assert.Equal(LineStyle.None, axis.MinorGridlineStyle);
    }

    [Fact]
    public void Ticks_sit_at_the_policy_positions_with_no_minor_ticks()
    {
        LogTimeAxis axis = TimeAxisOf(Build(Snapshot()));
        var scale = new LogTimeScale(End, TimeSpan.FromMinutes(60));

        axis.GetTickValues(out IList<double> labels, out IList<double> majors, out IList<double> minors);

        double[] expected = [.. ReferenceAgesSeconds.Select(s => scale.XForAge(s))];
        Assert.Equal(expected, majors);
        Assert.Equal(expected, labels);
        Assert.Empty(minors);
        Assert.True(majors.Count <= 6);
    }

    [Theory]
    [InlineData(60, "HH:mm")]
    [InlineData(12 * 60, "HH:mm")]
    [InlineData(1, "HH:mm:ss")]
    public void Labels_are_the_clock_times_of_the_tick_ages(int windowMinutes, string format)
    {
        TimeSpan window = TimeSpan.FromMinutes(windowMinutes);
        LogTimeAxis axis = TimeAxisOf(Build(Snapshot(Settings(window))));

        Assert.Equal(format, axis.LabelFormat);
        for (int i = 0; i < axis.TickAges.Count; i++)
        {
            string expected = (End - axis.TickAges[i]).ToString(format, CultureInfo.CurrentCulture);
            Assert.Equal(expected, axis.FormatValue(axis.TickPositions[i]));
        }

        // The right edge is the snapshot's own time.
        Assert.Equal(End.ToString(format, CultureInfo.CurrentCulture), axis.FormatValue(axis.TickPositions[^1]));
    }

    [Fact]
    public void A_tick_label_is_never_a_minute_early_through_round_off()
    {
        // The inverse of a tick's position can land a hair before the minute; a minutes-only label of that
        // would read one minute early. Labels come from the exact age instead. Checked across many end times,
        // since whether the round-off goes up or down depends on the operands.
        for (int offsetMs = 0; offsetMs < 60_000; offsetMs += 997)
        {
            DateTimeOffset end = End.AddMilliseconds(offsetMs);
            LogTimeAxis axis = TimeAxisOf(Build(Snapshot(takenUtc: end)));

            for (int i = 0; i < axis.TickAges.Count; i++)
            {
                Assert.Equal(
                    (end - axis.TickAges[i]).ToString("HH:mm", CultureInfo.CurrentCulture),
                    axis.FormatValue(axis.TickPositions[i]));
            }
        }
    }

    [Fact]
    public void Tick_positions_ignore_plotted_values_selection_expansion_and_chart_width()
    {
        // The regression this guards: a tick policy that can see the plot's pixel width flipped label density
        // on successive refreshes, because that width moves with the value axis' label width and the legend.
        MonitorSnapshot quiet = Snapshot(apps: [Consumer("a", [Measured(End.AddMinutes(-3), 200)])]);
        MonitorSnapshot loud = Snapshot(apps:
        [
            Consumer("a", [Measured(End.AddMinutes(-3), 12288)], Session(7, Measured(End.AddMinutes(-3), 12288))),
            Consumer("b", [Measured(End.AddMinutes(-20), 9000)]),
        ]);

        IList<double> reference = Ticks(Build(quiet));

        Assert.Equal(reference, Ticks(Build(loud)));
        Assert.Equal(reference, Ticks(ChartBuilder.Build(loud, "a", ["a"], TimeZoneInfo.Utc)));

        foreach (double width in new[] { 300.0, 1600.0 })
        {
            PlotModel model = Build(loud);
            string svg = SvgExporter.ExportToString(model, width, 500, false);
            Assert.Equal(reference, Ticks(model));

            LogTimeAxis axis = TimeAxisOf(model);
            foreach (double x in reference)
            {
                Assert.Contains(axis.FormatValue(x), TextElements(svg));
            }
        }
    }

    [Fact]
    public void Tick_ages_stay_put_while_their_labels_advance_with_the_snapshot()
    {
        LogTimeAxis first = TimeAxisOf(Build(Snapshot(takenUtc: End)));
        LogTimeAxis later = TimeAxisOf(Build(Snapshot(takenUtc: End.AddMinutes(1))));

        Assert.Equal(first.TickAges, later.TickAges);
        Assert.Equal(first.TickPositions, later.TickPositions);
        Assert.NotEqual(first.FormatValue(first.TickPositions[^1]), later.FormatValue(later.TickPositions[^1]));
    }

    [Fact]
    public void The_axis_still_spans_exactly_the_history_window()
    {
        // The right edge means "now", and the span has to keep agreeing with the "window N min" the status
        // line reports -- even with no data at all yet.
        var scale = new LogTimeScale(End, TimeSpan.FromMinutes(10));

        Assert.True((scale.TimestampAt(0) - (End - TimeSpan.FromMinutes(10))).Duration() < TimeSpan.FromMilliseconds(1));
        Assert.Equal(End, scale.TimestampAt(1));
    }

    private static PlotModel Build(MonitorSnapshot snapshot) =>
        ChartBuilder.Build(snapshot, selectedKey: null, expandedKeys: [], TimeZoneInfo.Utc);

    private static LogTimeAxis TimeAxisOf(PlotModel model) => Assert.Single(model.Axes.OfType<LogTimeAxis>());

    private static IList<double> Ticks(PlotModel model)
    {
        TimeAxisOf(model).GetTickValues(out _, out IList<double> majors, out _);
        return majors;
    }

    private static List<string> TextElements(string svg) =>
        [.. SvgText().Matches(svg).Select(m => m.Groups[1].Value)];

    [GeneratedRegex("<text[^>]*>([^<]*)</text>")]
    private static partial Regex SvgText();
}
