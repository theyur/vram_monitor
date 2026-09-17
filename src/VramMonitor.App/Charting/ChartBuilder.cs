using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
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
/// </remarks>
public static class ChartBuilder
{
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

    public static PlotModel Build(
        MonitorSnapshot snapshot,
        string? selectedKey,
        IReadOnlyCollection<string> expandedKeys)
    {
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

        AddAxes(model, snapshot);

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
                    $"{app.DisplayName} · pid {session.Pid}",
                    session.Series,
                    OxyColor.FromAColor(150, baseColour),
                    1.0,
                    ApplicationAxisKey,
                    LineStyle.Dot,
                    inLegend: false));
            }
        }

        AddTotalSeries(model, analysis.Total);
        AddDisruptionMarkers(model, analysis.Markers);

        return model;
    }

    private static void AddAxes(PlotModel model, MonitorSnapshot snapshot)
    {
        DateTimeOffset end = snapshot.TakenUtc.ToLocalTime();
        TimeSpan window = snapshot.Settings.HistoryWindow;
        DateTimeOffset start = end - window;

        // The tick step is pinned rather than left to OxyPlot, because OxyPlot derives it from the plot
        // area's pixel width -- Axis.UpdateIntervals calls DateTimeAxis.CalculateActualInterval, which
        // budgets IntervalLength (60px) per label -- and that width is not constant even when the window
        // is. The left value axis has an automatic Maximum, so its widest label grows from "500" to "12288"
        // as data arrives and takes about nine more pixels from the plot, and the outside legend re-flows as
        // the charted series set changes. For a ten-minute window OxyPlot's own boundary sits at exactly
        // 660px of plot area, so a few pixels of drift flipped the labels between a two-minute and a
        // one-minute step on successive refreshes at an unchanged window size. A step chosen from the
        // history window alone cannot do that.
        //
        // What this gives up is OxyPlot's automatic overlap protection, which is why TimeAxisStep targets at
        // most six labels and why the format below drops the seconds as soon as they are constant.
        TimeSpan step = TimeAxisStep.For(window);

        // Seconds earn their width only while the step is finer than a minute. At coarser steps every label
        // would end in the same ":00" -- about 15px per label spent on nothing, and enough to make the
        // labels of a six-to-ten-minute window collide at the chart column's MinWidth.
        string timeFormat = step < TimeSpan.FromMinutes(1) ? "HH:mm:ss" : "HH:mm";

        // Zoom and pan are deliberately off: spec section 12.3 puts them out of scope for v1.
        model.Axes.Add(new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            StringFormat = timeFormat,
            Minimum = DateTimeAxis.ToDouble(start.DateTime),
            Maximum = DateTimeAxis.ToDouble(end.DateTime),

            // A DateTimeAxis carries its values as days since an epoch, so a step is a number of DAYS.
            // Setting it makes Axis.UpdateIntervals skip CalculateActualInterval entirely, which is the
            // whole point. IntervalType is therefore never consulted -- DateTimeAxis only reads it inside
            // the method that no longer runs -- so setting it would be inert; StringFormat above is still
            // honoured, because ActualStringFormat is assigned independently of the step.
            MajorStep = step.TotalDays,

            // Matching the major step is what the automatic path already did for every interval type this
            // window range can reach, and minor ticks coinciding with major ones are dropped, so the axis
            // line stays free of intermediate marks. Leave this unset and OxyPlot falls back to a fifth of
            // the major step, putting twenty tick marks on an axis that has none today.
            MinorStep = step.TotalDays,

            IsZoomEnabled = false,
            IsPanEnabled = false,
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromRgb(0xEC, 0xEF, 0xF3),
        });

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

    private static LineSeries CreateSeries(
        string title,
        IReadOnlyList<SeriesPoint> points,
        OxyColor colour,
        double thickness,
        string axisKey,
        LineStyle style = LineStyle.Solid,
        bool inLegend = true)
    {
        var series = new LineSeries
        {
            Title = title,
            Color = colour,
            StrokeThickness = thickness,
            LineStyle = style,
            YAxisKey = axisKey,

            // The title is still set when the series stays out of the legend: it is what the hover tracker
            // names the line by.
            RenderInLegend = inLegend,
            TrackerFormatString = "{0}\n{2:HH:mm:ss}\n{4:0} MB",
            CanTrackerInterpolatePoints = false,
        };

        foreach (SeriesPoint point in points)
        {
            double x = DateTimeAxis.ToDouble(point.TimestampUtc.ToLocalTime().DateTime);

            // NaN is what produces a real discontinuity. Substituting zero here would invent a measurement.
            series.Points.Add(new DataPoint(x, point.HasValue ? Format.Megabytes(point.Value) : double.NaN));
        }

        return series;
    }

    private static void AddTotalSeries(PlotModel model, TotalSeries total)
    {
        if (total.Points.Count == 0) return;

        LineSeries series = CreateSeries(
            "Total GPU VRAM",
            total.Points,
            OxyColor.FromAColor(120, OxyColors.DimGray),
            1.5,
            TotalAxisKey,
            LineStyle.Dash);

        model.Series.Add(series);
    }

    private static void AddDisruptionMarkers(PlotModel model, IReadOnlyList<DisruptionMarker> markers)
    {
        foreach (DisruptionMarker marker in markers)
        {
            OxyColor colour = marker.Kind switch
            {
                DisruptionKind.ProbeFailed => OxyColor.FromAColor(120, OxyColors.OrangeRed),
                DisruptionKind.PartialSample => OxyColor.FromAColor(100, OxyColors.Goldenrod),
                _ => OxyColor.FromAColor(90, OxyColors.SteelBlue),
            };

            model.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Vertical,
                X = DateTimeAxis.ToDouble(marker.TimestampUtc.ToLocalTime().DateTime),
                Color = colour,
                LineStyle = LineStyle.Dot,
                StrokeThickness = 1,
                ToolTip = marker.Text,
                Text = string.Empty,
            });
        }
    }
}
