using OxyPlot;
using OxyPlot.Axes;

namespace VramMonitor.App.Charting;

/// <summary>
/// The horizontal axis of the chart: normalized positions from a <see cref="LogTimeScale"/>, labelled with
/// clock times.
/// </summary>
/// <remarks>
/// <para>
/// Series carry normalized X values in [0, 1] rather than timestamps, so this is a <see cref="LinearAxis"/>
/// whose range is pinned to exactly that, with no padding: the left edge is the oldest instant of the history
/// window and the right edge is the snapshot's time, whether or not any data is there yet.
/// </para>
/// <para>
/// Tick positions come from <see cref="LogTimeTicks"/> and are handed to OxyPlot through
/// <see cref="GetTickValues"/>, which is what the renderer reads for labels, tick marks and gridlines alike.
/// Leaving OxyPlot to choose would make the density depend on the plot's pixel width again -- see
/// <see cref="LogTimeTicks"/> for why that was a bug. There are no minor ticks.
/// </para>
/// </remarks>
public sealed class LogTimeAxis : LinearAxis
{
    private readonly LogTimeScale _scale;
    private readonly TimeZoneInfo _timeZone;
    private readonly string _labelFormat;
    private readonly double[] _tickPositions;

    public LogTimeAxis(LogTimeScale scale, TimeZoneInfo timeZone)
    {
        _scale = scale ?? throw new ArgumentNullException(nameof(scale));
        _timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));

        TickAges = LogTimeTicks.For(scale.Window);
        _tickPositions = [.. TickAges.Select(a => scale.XForAge(a.TotalSeconds))];

        // Seconds earn their width only while a tick is finer than a minute; the hover text always has them.
        _labelFormat = LogTimeTicks.NeedsSeconds(TickAges, scale.Window) ? "HH:mm:ss" : "HH:mm";

        Position = AxisPosition.Bottom;
        Title = "Time (logarithmic age)";
        Minimum = 0;
        Maximum = 1;
        MinimumPadding = 0;
        MaximumPadding = 0;

        // Zoom and pan are deliberately off: spec section 12.3 puts them out of scope for v1.
        IsZoomEnabled = false;
        IsPanEnabled = false;

        MajorGridlineStyle = LineStyle.Dot;
        MajorGridlineColor = OxyColor.FromRgb(0xEC, 0xEF, 0xF3);
        MinorGridlineStyle = LineStyle.None;
    }

    /// <summary>The ages the ticks stand for, ordered by ascending X (oldest first).</summary>
    public IReadOnlyList<TimeSpan> TickAges { get; }

    /// <summary>The normalized positions of <see cref="TickAges"/>.</summary>
    public IReadOnlyList<double> TickPositions => _tickPositions;

    /// <summary>The clock-time format of the tick labels.</summary>
    public string LabelFormat => _labelFormat;

    public override void GetTickValues(
        out IList<double> majorLabelValues, out IList<double> majorTickValues, out IList<double> minorTickValues)
    {
        // Fresh lists every call: the renderer is entitled to treat them as its own.
        majorLabelValues = [.. _tickPositions];
        majorTickValues = [.. _tickPositions];
        minorTickValues = [];
    }

    protected override string FormatValueOverride(double x)
    {
        // A tick is labelled from its exact age, not from the inverse of its position: a round trip through
        // exp/log can land a hair before the minute -- 11:58:59.9999999 for a tick at 11:59 -- and a label that
        // truncates to minutes would then read 11:58. The renderer passes back the very doubles that
        // GetTickValues handed out, so exact comparison is the right test. The inverse is the fallback for
        // any other X the layout might probe, which keeps this total.
        int tick = Array.IndexOf(_tickPositions, x);
        DateTimeOffset at = tick >= 0 ? _scale.EndUtc - TickAges[tick] : _scale.TimestampAt(x);

        return TimeZoneInfo.ConvertTime(at, _timeZone).ToString(_labelFormat, ActualCulture);
    }
}
