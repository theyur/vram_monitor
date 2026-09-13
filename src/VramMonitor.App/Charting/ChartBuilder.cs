using System.Collections.Generic;
using System.Linq;
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

        model.Legends.Add(new Legend
        {
            LegendPosition = LegendPosition.RightTop,
            LegendPlacement = LegendPlacement.Outside,
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
                model.Series.Add(CreateSeries(
                    $"{app.DisplayName} · pid {session.Pid}",
                    session.Series,
                    OxyColor.FromAColor(150, baseColour),
                    1.0,
                    ApplicationAxisKey,
                    LineStyle.Dot));
            }
        }

        AddTotalSeries(model, analysis.Total);
        AddDisruptionMarkers(model, analysis.Markers);

        return model;
    }

    private static void AddAxes(PlotModel model, MonitorSnapshot snapshot)
    {
        DateTimeOffset end = snapshot.TakenUtc.ToLocalTime();
        DateTimeOffset start = end - snapshot.Settings.HistoryWindow;

        // Minutes alone repeat themselves on a short window, so seconds are shown when the whole window is
        // only a few minutes wide.
        string timeFormat = snapshot.Settings.HistoryWindow <= TimeSpan.FromMinutes(10) ? "HH:mm:ss" : "HH:mm";

        // Zoom and pan are deliberately off: spec section 12.3 puts them out of scope for v1.
        model.Axes.Add(new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            StringFormat = timeFormat,
            Minimum = DateTimeAxis.ToDouble(start.DateTime),
            Maximum = DateTimeAxis.ToDouble(end.DateTime),
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
        LineStyle style = LineStyle.Solid)
    {
        var series = new LineSeries
        {
            Title = title,
            Color = colour,
            StrokeThickness = thickness,
            LineStyle = style,
            YAxisKey = axisKey,
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
