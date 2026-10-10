using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using VramMonitor.App.Formatting;
using VramMonitor.Core.Analysis;
using VramMonitor.Core.Model;

namespace VramMonitor.App.Charting;

/// <summary>
/// Turns an analysis result into an OxyPlot model.
/// </summary>
/// <remarks>
/// <para>
/// Gaps are the point of this class. A point that is not a measurement becomes <c>double.NaN</c>, which
/// OxyPlot renders as a genuine break rather than joining across it. Nothing is ever interpolated and no
/// value is ever carried forward (spec section 13.1).
/// </para>
/// <para>
/// A normal process exit and a probe failure both break the line; what tells them apart is the disruption
/// marker on the time axis, which only a failure or a partial sample gets (spec sections 13.2 and 13.5).
/// </para>
/// <para>
/// Time runs on a logarithmic-age axis. One <see cref="LogTimeScale"/> is built per call from the snapshot
/// alone and every series, every marker and the axis itself are positioned through it, so a point, its
/// process line, the total and a marker sharing a timestamp share an X exactly. The scale throws for a
/// non-positive window: the snapshot's settings are expected to have been through
/// <c>MonitorSettings.Validated()</c>, which every path into a snapshot does.
/// </para>
/// </remarks>
public static class ChartBuilder
{
    public const string TimeAxisKey = "time";
    public const string ApplicationAxisKey = "apps";
    public const string TotalAxisKey = "total";

    private static readonly OxyColor[] Palette =
    [
        OxyColor.FromRgb(0x4A, 0x9E, 0xDE), OxyColor.FromRgb(0x4A, 0xDE, 0x80),
        OxyColor.FromRgb(0xFB, 0xBF, 0x24), OxyColor.FromRgb(0xF8, 0x71, 0x71),
        OxyColor.FromRgb(0xA7, 0x8B, 0xFA), OxyColor.FromRgb(0x2D, 0xD4, 0xBF),
        OxyColor.FromRgb(0xF4, 0x72, 0xB6), OxyColor.FromRgb(0xFB, 0x92, 0x3C),
        OxyColor.FromRgb(0x94, 0xA3, 0xB8), OxyColor.FromRgb(0x84, 0xCC, 0x16),
    ];

    /// <param name="snapshot">The state to draw. Its settings must already be validated.</param>
    /// <param name="selectedKey">The application to highlight, dimming every other line; null for none.</param>
    /// <param name="expandedKeys">Applications whose individual process lines are overlaid.</param>
    /// <param name="timeZone">
    /// Where clock times are displayed. Null means the machine's local zone; tests pass a fixed one.
    /// </param>
    public static PlotModel Build(
        MonitorSnapshot snapshot,
        string? selectedKey,
        IReadOnlyCollection<string> expandedKeys,
        TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(expandedKeys);

        var context = new Context(
            new LogTimeScale(snapshot.TakenUtc, snapshot.Settings.HistoryWindow), timeZone ?? TimeZoneInfo.Local);

        var model = new PlotModel
        {
            PlotAreaBorderColor = OxyColor.FromRgb(0xD8, 0xDE, 0xE6),
            TextColor = OxyColor.FromRgb(0x33, 0x3A, 0x45),
        };

        // Under the plot rather than beside it: a column of legend entries down the right cost the graph
        // roughly a third of the window's width at a raised chart-top-N, because OxyPlot wraps a vertical
        // legend into further columns rather than clipping it, and the graph is the thing being read.
        // Horizontal, so the entries flow across and wrap instead of stacking into a tall column again.
        //
        // Deliberately unbounded. A LegendMaxHeight would protect the graph's height, but past the bound
        // OxyPlot drops the overflowing entries and cuts the last visible line mid-glyph, leaving series
        // plotted that nothing identifies. Letting the legend take the height it needs costs about a fifth
        // of the plot at chart-top-N 50 and nothing at all at the default of 10.
        model.Legends.Add(new Legend
        {
            LegendPosition = LegendPosition.BottomLeft,
            LegendPlacement = LegendPlacement.Outside,
            LegendOrientation = LegendOrientation.Horizontal,
            LegendFontSize = 11,
        });

        AddAxes(model, snapshot, context);

        AnalysisResult analysis = snapshot.Analysis;
        var charted = new HashSet<string>(analysis.ChartedKeys);
        if (selectedKey is not null) charted.Add(selectedKey);
        foreach (string key in expandedKeys) charted.Add(key);

        int colour = 0;
        foreach (ApplicationView app in analysis.AllConsumers.Where(a => charted.Contains(a.Key)))
        {
            bool dimmed = selectedKey is not null && app.Key != selectedKey;
            OxyColor baseColour = Palette[colour++ % Palette.Length];

            model.Series.Add(CreateSeries(
                context,
                app.DisplayName,
                app.Series,
                dimmed ? OxyColor.FromAColor(55, baseColour) : baseColour,
                dimmed ? 1.5 : 2.25,
                ApplicationAxisKey));

            // Expanding an application overlays its individual process lifetimes (spec section 11.2).
            if (!expandedKeys.Contains(app.Key)) continue;

            foreach (SessionView session in app.Sessions)
            {
                // Out of the legend on purpose. A browser can hold dozens of GPU-touching processes, so
                // legending them would let expanding one application grow the legend without bound and eat
                // the plot it sits under. These lines are subordinate anyway -- dotted, thin, and drawn in a
                // faded shade of an application that the legend already names -- and they are identified
                // where the user expanded them: the row in the consumer list, and the hover tracker.
                model.Series.Add(CreateSeries(
                    context,
                    $"{app.DisplayName} · pid {session.Pid}",
                    session.Series,
                    OxyColor.FromAColor(150, baseColour),
                    1.0,
                    ApplicationAxisKey,
                    LineStyle.Dot,
                    inLegend: false));
            }
        }

        AddTotalSeries(model, context, analysis.Total);
        AddDisruptionMarkers(model, context, analysis.Markers);

        return model;
    }

    /// <summary>What every element of one plot is positioned and labelled by.</summary>
    private sealed record Context(LogTimeScale Scale, TimeZoneInfo TimeZone);

    private static void AddAxes(PlotModel model, MonitorSnapshot snapshot, Context context)
    {
        // The time axis takes its tick positions from the history window alone; LogTimeTicks explains why
        // letting OxyPlot derive them from the plot's pixel width made the label density flicker.
        model.Axes.Add(new LogTimeAxis(context.Scale, context.TimeZone) { Key = TimeAxisKey });

        model.Axes.Add(new LinearAxis
        {
            Key = ApplicationAxisKey,
            Position = AxisPosition.Left,
            Title = "Per-application dedicated VRAM (MB)",
            Minimum = 0,
            MinimumPadding = 0,
            IsZoomEnabled = false,
            IsPanEnabled = false,
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromRgb(0xEC, 0xEF, 0xF3),
        });

        // The total sits on its own scale so that it cannot flatten the per-application series into the
        // bottom of the plot (spec section 12.2).
        model.Axes.Add(new LinearAxis
        {
            Key = TotalAxisKey,
            Position = AxisPosition.Right,
            Title = "Total GPU VRAM (MB)",
            Minimum = 0,
            Maximum = snapshot.Gpu is { DedicatedVideoMemoryBytes: > 0 } gpu
                ? Format.Megabytes(gpu.DedicatedVideoMemoryBytes)
                : double.NaN,
            IsZoomEnabled = false,
            IsPanEnabled = false,
            TextColor = OxyColors.Gray,
            TitleColor = OxyColors.Gray,
            TicklineColor = OxyColors.LightGray,
        });
    }

    private static TimestampedLineSeries CreateSeries(
        Context context,
        string title,
        IReadOnlyList<SeriesPoint> points,
        OxyColor colour,
        double thickness,
        string axisKey,
        LineStyle style = LineStyle.Solid,
        bool inLegend = true)
    {
        var series = new TimestampedLineSeries(context.TimeZone)
        {
            Title = title,
            Color = colour,
            StrokeThickness = thickness,
            LineStyle = style,
            XAxisKey = TimeAxisKey,
            YAxisKey = axisKey,

            // The title is still set when the series stays out of the legend: it is what the hover tracker
            // names the line by.
            RenderInLegend = inLegend,
        };

        series.EnsureCapacity(points.Count);
        foreach (SeriesPoint point in points)
        {
            // A point after the snapshot's own time has no age to place it by. Analysis never produces one,
            // but the logarithm must not be the thing that finds out. Anything older than the window is kept,
            // at a negative X, for the plot area to clip -- including a gap break analysis put just outside it.
            if (context.Scale.IsFuture(point.TimestampUtc)) continue;

            series.Add(context.Scale.ToX(point.TimestampUtc), point);
        }

        return series;
    }

    private static void AddTotalSeries(PlotModel model, Context context, TotalSeries total)
    {
        if (total.Points.Count == 0) return;

        TimestampedLineSeries series = CreateSeries(
            context,
            "Total GPU VRAM",
            total.Points,
            OxyColor.FromAColor(120, OxyColors.DimGray),
            1.5,
            TotalAxisKey,
            LineStyle.Dash);

        model.Series.Add(series);
    }

    private static void AddDisruptionMarkers(
        PlotModel model, Context context, IReadOnlyList<DisruptionMarker> markers)
    {
        foreach (DisruptionMarker marker in markers)
        {
            if (context.Scale.IsFuture(marker.TimestampUtc)) continue;

            OxyColor colour = marker.Kind switch
            {
                DisruptionKind.ProbeFailed => OxyColor.FromAColor(120, OxyColors.OrangeRed),
                DisruptionKind.PartialSample => OxyColor.FromAColor(100, OxyColors.Goldenrod),
                _ => OxyColor.FromAColor(90, OxyColors.SteelBlue),
            };

            model.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Vertical,
                // Through the same scale as the series, so a marker sits exactly on the gap it explains.
                X = context.Scale.ToX(marker.TimestampUtc),
                XAxisKey = TimeAxisKey,
                Color = colour,
                LineStyle = LineStyle.Dot,
                StrokeThickness = 1,
                ToolTip = marker.Text,
                Text = string.Empty,
            });
        }
    }
}
