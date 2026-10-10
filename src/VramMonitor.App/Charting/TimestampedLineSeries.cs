using OxyPlot;
using OxyPlot.Series;
using VramMonitor.App.Formatting;
using VramMonitor.Core.Analysis;

namespace VramMonitor.App.Charting;

/// <summary>
/// A line series whose points sit at normalized X positions but whose hover text names the original
/// measurement's time.
/// </summary>
/// <remarks>
/// <para>
/// OxyPlot's tracker formats <c>{2}</c> from the X axis' value at the hit, which on the logarithmic axis is a
/// normalized number between 0 and 1. Showing that as a time -- or even converting it back -- would be wrong:
/// the inverse of a rounded position is not the instant the sample was taken. So every plotted point keeps
/// the <see cref="SeriesPoint"/> it came from, and the tracker text is written from that source instead.
/// </para>
/// <para>
/// The hit is always a real point. <see cref="LineSeries.CanTrackerInterpolatePoints"/> is off, so OxyPlot
/// returns no result for an interpolating request and the nearest measured point otherwise; points whose Y is
/// <c>NaN</c> -- the gaps -- are never hit.
/// </para>
/// <para>
/// The time is shown with its date and UTC offset. Around a daylight-saving change the local clock repeats an
/// hour, and two samples an hour apart would otherwise hover as the same "02:30:00".
/// </para>
/// </remarks>
public sealed class TimestampedLineSeries : LineSeries
{
    private readonly List<SeriesPoint> _sources = [];
    private readonly TimeZoneInfo _timeZone;

    public TimestampedLineSeries(TimeZoneInfo timeZone)
    {
        _timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        CanTrackerInterpolatePoints = false;
    }

    /// <summary>The source of each entry in <see cref="DataPointSeries.Points"/>, index for index.</summary>
    public IReadOnlyList<SeriesPoint> Sources => _sources;

    /// <summary>Reserves room for <paramref name="count"/> points in both the plotted and the source lists.</summary>
    /// <remarks>
    /// Measured, not speculative: a twelve-hour window at one-second sampling charts some 2.7 million points at
    /// chart-top-N 50, and letting both lists grow by doubling tripled the allocation of a chart build.
    /// </remarks>
    public void EnsureCapacity(int count)
    {
        Points.EnsureCapacity(count);
        _sources.EnsureCapacity(count);
    }

    /// <summary>Plots <paramref name="source"/> at <paramref name="x"/>; NaN for anything not measured.</summary>
    public void Add(double x, SeriesPoint source)
    {
        // NaN is what produces a real discontinuity. Substituting zero here would invent a measurement.
        Points.Add(new DataPoint(x, source.HasValue ? Format.Megabytes(source.Value) : double.NaN));
        _sources.Add(source);
    }

    public override TrackerHitResult? GetNearestPoint(ScreenPoint point, bool interpolate)
    {
        TrackerHitResult? result = base.GetNearestPoint(point, interpolate);
        if (result is null) return null;

        // Base returns the index of a real point when it does not interpolate, and it cannot interpolate here.
        // Anything else would have no single source to describe, so it is not shown at all.
        if (!double.IsInteger(result.Index)) return null;
        int index = (int)result.Index;
        if (index < 0 || index >= _sources.Count) return null;

        SeriesPoint source = _sources[index];
        result.Item = source;
        result.Text = TrackerText(source, result.DataPoint.Y);
        return result;
    }

    /// <summary>Title, local time with date and offset, and value: what the hover shows for one point.</summary>
    internal string TrackerText(SeriesPoint source, double megabytes)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(source.TimestampUtc, _timeZone);
        return string.Format(
            ActualCulture, "{0}\n{1:yyyy-MM-dd HH:mm:ss zzz}\n{2:0} MB", Title, local, megabytes);
    }
}
