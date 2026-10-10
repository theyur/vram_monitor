using System.Globalization;
using OxyPlot;
using OxyPlot.Annotations;
using VramMonitor.App.Charting;
using VramMonitor.Core.Analysis;
using VramMonitor.Core.Configuration;
using VramMonitor.Core.Model;
using Xunit;
using static VramMonitor.App.Tests.Support.SnapshotFixture;

namespace VramMonitor.App.Tests.Charting;

public sealed class ChartBuilderSeriesTests
{
    private static readonly DateTimeOffset Shared = End.AddMinutes(-7);

    [Fact]
    public void An_application_its_process_the_total_and_a_marker_sharing_a_timestamp_share_an_x()
    {
        MonitorSnapshot snapshot = Snapshot(
            apps: [Consumer("a", [Measured(Shared, 500)], Session(42, Measured(Shared, 300)))],
            total: [Measured(Shared, 4000)],
            markers: [new DisruptionMarker(Shared, DisruptionKind.PartialSample)]);

        PlotModel model = ChartBuilder.Build(snapshot, null, ["a"], TimeZoneInfo.Utc);

        double expected = new LogTimeScale(End, TimeSpan.FromMinutes(60)).ToX(Shared);
        List<TimestampedLineSeries> series = SeriesOf(model);
        Assert.Equal(3, series.Count);
        Assert.All(series, s =>
        {
            Assert.Equal(ChartBuilder.TimeAxisKey, s.XAxisKey);
            Assert.Equal(expected, Assert.Single(s.Points).X);
        });

        LineAnnotation marker = Assert.IsType<LineAnnotation>(Assert.Single(model.Annotations));
        Assert.Equal(ChartBuilder.TimeAxisKey, marker.XAxisKey);
        Assert.Equal(expected, marker.X);
    }

    [Fact]
    public void Rebuilding_for_selection_or_expansion_keeps_every_position()
    {
        MonitorSnapshot snapshot = Snapshot(
            apps:
            [
                Consumer("a", [Measured(Shared, 500), Measured(End, 600)], Session(42, Measured(Shared, 300))),
                Consumer("b", [Measured(Shared.AddMinutes(-30), 200), Measured(End, 250)]),
            ],
            total: [Measured(Shared, 4000), Measured(End, 4100)]);

        Dictionary<string, double[]> plain = Positions(ChartBuilder.Build(snapshot, null, [], TimeZoneInfo.Utc));
        Dictionary<string, double[]> selected = Positions(ChartBuilder.Build(snapshot, "b", ["a"], TimeZoneInfo.Utc));

        foreach ((string title, double[] xs) in plain) Assert.Equal(xs, selected[title]);
    }

    [Fact]
    public void An_empty_history_still_shows_the_whole_window()
    {
        PlotModel model = ChartBuilder.Build(Snapshot(), null, [], TimeZoneInfo.Utc);

        LogTimeAxis axis = Assert.Single(model.Axes.OfType<LogTimeAxis>());
        Assert.Equal(0, axis.Minimum);
        Assert.Equal(1, axis.Maximum);
        Assert.Empty(model.Series);
    }

    [Fact]
    public void A_single_sample_sits_at_the_right_edge_without_being_stretched_across_the_plot()
    {
        PlotModel model = ChartBuilder.Build(
            Snapshot(apps: [Consumer("a", [Measured(End, 500)])]), null, [], TimeZoneInfo.Utc);

        DataPoint point = Assert.Single(Assert.Single(SeriesOf(model)).Points);
        Assert.Equal(1, point.X);
        Assert.Equal(500, point.Y);
    }

    [Fact]
    public void A_point_after_the_snapshot_is_excluded_and_an_older_gap_point_is_kept()
    {
        MonitorSnapshot snapshot = Snapshot(
            apps: [Consumer("a", [Missing(End.AddMinutes(-61)), Measured(End.AddMinutes(-59), 500), Measured(End.AddSeconds(5), 999)])],
            total: [Measured(End.AddSeconds(5), 4000)],
            markers: [new DisruptionMarker(End.AddSeconds(5), DisruptionKind.ProbeFailed)]);

        PlotModel model = ChartBuilder.Build(snapshot, null, [], TimeZoneInfo.Utc);

        TimestampedLineSeries app = SeriesOf(model).Single(s => s.Title == "a.exe");
        Assert.Equal(2, app.Points.Count);
        Assert.True(app.Points[0].X < 0, "the older gap point was clamped or dropped");
        Assert.True(double.IsNaN(app.Points[0].Y));
        Assert.Equal(500, app.Points[1].Y);

        // The total had only a future point, so it is drawn empty; the future marker is gone too.
        Assert.Empty(SeriesOf(model).Single(s => s.Title == "Total GPU VRAM").Points);
        Assert.Empty(model.Annotations);
    }

    [Fact]
    public void Missing_and_absent_points_stay_breaks_rather_than_becoming_values()
    {
        MonitorSnapshot snapshot = Snapshot(apps:
        [
            Consumer("a", [Measured(End.AddMinutes(-3), 500), Missing(End.AddMinutes(-2)), Absent(End.AddMinutes(-1)), Measured(End, 510)]),
        ]);

        TimestampedLineSeries app = Assert.Single(SeriesOf(ChartBuilder.Build(snapshot, null, [], TimeZoneInfo.Utc)));

        Assert.Equal([500, double.NaN, double.NaN, 510], app.Points.Select(p => p.Y));
    }

    [Fact]
    public void An_analysis_generated_pause_break_near_the_left_edge_stays_a_break()
    {
        // A lone sample just inside a ten-minute window, a five-minute silence, then regular sampling: the
        // analyzer inserts its own NaN break one tick after the lone sample and a sampling-paused marker.
        MonitorSettings settings = Settings(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10));
        var id = new ProcessSessionId(1);
        var sessions = new Dictionary<ProcessSessionId, ProcessSessionInfo>
        {
            [id] = new(id, 10, 1, new GpuId("luid"), new ProcessIdentity("a", "a.exe", null, "a.exe", ProcessIdentityKind.ExecutableName),
                End.AddMinutes(-10), End, null),
        };

        List<GpuSample> samples = [Sample(End.AddMinutes(-9.8))];
        for (int s = 300; s >= 0; s -= 10) samples.Add(Sample(End.AddSeconds(-s)));

        AnalysisResult analysis = Analyzer.Analyze(samples, sessions, settings, [], End);
        MonitorSnapshot snapshot = Snapshot(settings) with { Analysis = analysis };
        PlotModel model = ChartBuilder.Build(snapshot, null, [], TimeZoneInfo.Utc);
        var scale = new LogTimeScale(End, settings.HistoryWindow);

        double breakX = scale.ToX(End.AddMinutes(-9.8).AddTicks(1));
        foreach (TimestampedLineSeries series in SeriesOf(model))
        {
            DataPoint gap = Assert.Single(series.Points, p => Math.Abs(p.X - breakX) < 1e-12);
            Assert.True(double.IsNaN(gap.Y), $"{series.Title} lost its pause break");
        }

        LineAnnotation paused = Assert.Single(model.Annotations.OfType<LineAnnotation>(), a => a.ToolTip == "Sampling paused");
        Assert.Equal(scale.ToX(End.AddMinutes(-9.8)), paused.X);

        GpuSample Sample(DateTimeOffset at) => new(
            at, settings.SampleInterval, ProbeOutcome.Ok, 4000 * Mb, [new ProcessObservation(id, 500 * Mb, null)]);
    }

    [Fact]
    public void The_sample_interval_does_not_move_a_timestamp()
    {
        SeriesPoint[] series = [Measured(Shared, 500), Measured(End, 510)];

        double fast = Assert.Single(SeriesOf(ChartBuilder.Build(
            Snapshot(Settings(interval: TimeSpan.FromSeconds(1)), apps: [Consumer("a", series)]), null, [], TimeZoneInfo.Utc))).Points[0].X;
        double slow = Assert.Single(SeriesOf(ChartBuilder.Build(
            Snapshot(Settings(interval: TimeSpan.FromSeconds(10)), apps: [Consumer("a", series)]), null, [], TimeZoneInfo.Utc))).Points[0].X;

        Assert.Equal(fast, slow);
    }

    [Fact]
    public void Hovering_an_older_point_names_the_original_sample_and_never_interpolates()
    {
        DateTimeOffset first = End.AddMinutes(-40);
        DateTimeOffset second = End.AddMinutes(-39);
        PlotModel model = ChartBuilder.Build(
            Snapshot(apps: [Consumer("a", [Measured(first, 500), Measured(second, 500), Measured(End, 900)], Session(42, Measured(first, 300)))]),
            null, ["a"], TimeZoneInfo.Utc);
        SvgExporter.ExportToString(model, 1000, 500, false);   // lays out the axes

        TimestampedLineSeries app = SeriesOf(model).Single(s => s.Title == "a.exe");

        TrackerHitResult? hit = app.GetNearestPoint(app.Transform(app.Points[0]), interpolate: false);
        Assert.NotNull(hit);
        Assert.Equal(Measured(first, 500), hit.Item);
        Assert.Equal($"a.exe\n{Local(first)}\n500 MB", hit.Text);

        // Between two samples the hit is one of them, at its own time -- not a time in between.
        ScreenPoint a = app.Transform(app.Points[0]);
        ScreenPoint b = app.Transform(app.Points[1]);
        TrackerHitResult? between = app.GetNearestPoint(new ScreenPoint((a.X + b.X) / 2, a.Y), interpolate: false);
        Assert.NotNull(between);
        SeriesPoint source = Assert.IsType<SeriesPoint>(between.Item);
        Assert.Contains(source.TimestampUtc, new[] { first, second });
        Assert.Contains(Local(source.TimestampUtc), between.Text, StringComparison.Ordinal);

        Assert.Null(app.GetNearestPoint(new ScreenPoint((a.X + b.X) / 2, a.Y), interpolate: true));

        // A process line hovers under its own name.
        TimestampedLineSeries process = SeriesOf(model).Single(s => s.Title.Contains("pid 42", StringComparison.Ordinal));
        TrackerHitResult? processHit = process.GetNearestPoint(process.Transform(process.Points[0]), interpolate: false);
        Assert.NotNull(processHit);
        Assert.StartsWith("a.exe · pid 42\n", processHit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Hover_text_tells_apart_two_samples_whose_local_clock_times_repeat_across_a_dst_change()
    {
        // Central Europe falls back at 01:00 UTC on 25 October 2026: 00:30Z and 01:30Z are both 02:30 local.
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var before = new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero);
        var after = new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero);
        DateTimeOffset end = after.AddMinutes(15);

        PlotModel model = ChartBuilder.Build(
            Snapshot(Settings(TimeSpan.FromHours(2)), end, apps: [Consumer("a", [Measured(before, 500), Measured(after, 600)])]),
            null, [], zone);

        TimestampedLineSeries app = Assert.Single(SeriesOf(model));
        Assert.True(app.Points[0].X < app.Points[1].X, "chronology must follow UTC, not the repeated local clock");
        Assert.Contains("2026-10-25 02:30:00 +02:00", app.TrackerText(app.Sources[0], app.Points[0].Y), StringComparison.Ordinal);
        Assert.Contains("2026-10-25 02:30:00 +01:00", app.TrackerText(app.Sources[1], app.Points[1].Y), StringComparison.Ordinal);
    }

    [Fact]
    public void Selection_dims_the_others_and_process_lines_stay_dotted_and_out_of_the_legend()
    {
        MonitorSnapshot snapshot = Snapshot(
            apps:
            [
                Consumer("a", [Measured(End, 500)], Session(42, Measured(End, 300))),
                Consumer("b", [Measured(End, 200)]),
            ],
            total: [Measured(End, 4000)]);

        List<TimestampedLineSeries> series = SeriesOf(ChartBuilder.Build(snapshot, "a", ["a"], TimeZoneInfo.Utc));

        TimestampedLineSeries a = series.Single(s => s.Title == "a.exe");
        TimestampedLineSeries b = series.Single(s => s.Title == "b.exe");
        TimestampedLineSeries pid = series.Single(s => s.Title == "a.exe · pid 42");
        TimestampedLineSeries total = series.Single(s => s.Title == "Total GPU VRAM");

        Assert.Equal(255, a.Color.A);
        Assert.Equal(2.25, a.StrokeThickness);
        Assert.Equal(55, b.Color.A);
        Assert.Equal(1.5, b.StrokeThickness);

        Assert.Equal(LineStyle.Dot, pid.LineStyle);
        Assert.False(pid.RenderInLegend);
        Assert.True(a.RenderInLegend);

        Assert.Equal(ChartBuilder.ApplicationAxisKey, a.YAxisKey);
        Assert.Equal(ChartBuilder.ApplicationAxisKey, pid.YAxisKey);
        Assert.Equal(ChartBuilder.TotalAxisKey, total.YAxisKey);
        Assert.Equal(LineStyle.Dash, total.LineStyle);
    }

    [Fact]
    public void Disruption_markers_keep_their_colours_and_tooltips()
    {
        MonitorSnapshot snapshot = Snapshot(markers:
        [
            new DisruptionMarker(End.AddMinutes(-3), DisruptionKind.ProbeFailed),
            new DisruptionMarker(End.AddMinutes(-2), DisruptionKind.PartialSample),
            new DisruptionMarker(End.AddMinutes(-1), DisruptionKind.SamplingPaused),
        ]);

        LineAnnotation[] markers = [.. ChartBuilder.Build(snapshot, null, [], TimeZoneInfo.Utc).Annotations.OfType<LineAnnotation>()];

        Assert.Equal(["Probe failed", "Partial sample", "Sampling paused"], markers.Select(m => m.ToolTip));
        Assert.Equal(OxyColor.FromAColor(120, OxyColors.OrangeRed), markers[0].Color);
        Assert.Equal(OxyColor.FromAColor(100, OxyColors.Goldenrod), markers[1].Color);
        Assert.Equal(OxyColor.FromAColor(90, OxyColors.SteelBlue), markers[2].Color);
        Assert.All(markers, m => Assert.Equal(LineAnnotationType.Vertical, m.Type));
    }

    [Fact]
    public void Settings_that_skipped_validation_are_refused_rather_than_drawn()
    {
        // MonitorSettings.Validated() guarantees at least a minute; a zero window has no scale to draw on.
        MonitorSnapshot snapshot = Snapshot(new MonitorSettings { HistoryWindow = TimeSpan.Zero });

        Assert.Throws<ArgumentOutOfRangeException>(() => ChartBuilder.Build(snapshot, null, []));
    }

    private static List<TimestampedLineSeries> SeriesOf(PlotModel model) => [.. model.Series.OfType<TimestampedLineSeries>()];

    private static Dictionary<string, double[]> Positions(PlotModel model) =>
        SeriesOf(model).ToDictionary(s => s.Title, s => s.Points.Select(p => p.X).ToArray());

    private static string Local(DateTimeOffset at) =>
        at.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.CurrentCulture);
}
