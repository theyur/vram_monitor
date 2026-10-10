namespace VramMonitor.App.Charting;

/// <summary>
/// Maps a timestamp to a normalized horizontal position by its logarithmic age: 0 is the left edge, the
/// oldest instant the history window shows, and 1 is the right edge, the snapshot's own time.
/// </summary>
/// <remarks>
/// <para>
/// <c>x(a) = 1 - ln(1 + a/τ) / ln(1 + W/τ)</c>, where <c>a</c> is the age in seconds measured back from
/// <see cref="EndUtc"/>, <c>W</c> is the history window and <c>τ</c> is <see cref="TauSeconds"/>. Equal
/// stretches of elapsed time therefore take more width the more recent they are, which is the whole point:
/// a spike from the last minute gets room to be read while the rest of the window stays in view.
/// </para>
/// <para>
/// The offset τ is what keeps age zero finite. A plain <c>log(age)</c> runs off to minus infinity at the
/// newest sample, and a logarithm of the absolute timestamp is nearly linear across any window this
/// application can show. τ also sets the curve: a 60-second offset gives a 60-minute window's newest minute
/// about a sixth of the width.
/// </para>
/// <para>
/// Age is real elapsed UTC time, never a sample index, so neither the sample interval nor the number of
/// samples moves an existing timestamp, and the same instant expressed in two UTC offsets maps identically.
/// One instance is built per plot from the snapshot alone; rebuilding the plot for a selection or an
/// expansion against the same snapshot reproduces every position exactly.
/// </para>
/// <para>
/// A timestamp older than the window maps to a negative X and is left there for the plot area to clip.
/// Clamping it to zero would pile every older point onto the left edge and draw a line along it, which is
/// a measurement nobody took. A timestamp after <see cref="EndUtc"/> has no age at all and must be excluded
/// before mapping: <see cref="ToX"/> refuses it rather than evaluate the logarithm of a negative age.
/// </para>
/// </remarks>
public sealed class LogTimeScale
{
    /// <summary>The offset τ, in seconds, that keeps the newest sample finite and shapes the curve.</summary>
    public const double TauSeconds = 60;

    private readonly double _logOfWindow;

    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="window"/> is not positive. <c>MonitorSettings.Validated()</c> guarantees at least a
    /// minute, so this only fires for settings that skipped validation.
    /// </exception>
    public LogTimeScale(DateTimeOffset endUtc, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        EndUtc = endUtc;
        Window = window;
        _logOfWindow = Math.Log(1 + (window.TotalSeconds / TauSeconds));
    }

    /// <summary>The instant at the right edge.</summary>
    public DateTimeOffset EndUtc { get; }

    /// <summary>The span from the left edge to the right edge.</summary>
    public TimeSpan Window { get; }

    /// <summary>True when <paramref name="timestamp"/> lies after <see cref="EndUtc"/> and so cannot be mapped.</summary>
    public bool IsFuture(DateTimeOffset timestamp) => timestamp > EndUtc;

    /// <summary>Seconds from <paramref name="timestamp"/> to <see cref="EndUtc"/>; negative for a future point.</summary>
    public double AgeSeconds(DateTimeOffset timestamp) => (EndUtc - timestamp).TotalSeconds;

    /// <exception cref="ArgumentOutOfRangeException">The timestamp is after <see cref="EndUtc"/>.</exception>
    public double ToX(DateTimeOffset timestamp)
    {
        if (IsFuture(timestamp))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestamp), timestamp, $"Timestamps after {EndUtc:O} have no age and must be excluded before mapping.");
        }

        return XForAge(AgeSeconds(timestamp));
    }

    /// <exception cref="ArgumentOutOfRangeException">The age is negative or not a number.</exception>
    public double XForAge(double ageSeconds)
    {
        if (!(ageSeconds >= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(ageSeconds), ageSeconds, "Age must be a non-negative number.");
        }

        return 1 - (Math.Log(1 + (ageSeconds / TauSeconds)) / _logOfWindow);
    }

    /// <summary>The inverse of <see cref="XForAge"/>.</summary>
    /// <remarks>
    /// Total for every finite X, unlike the forward direction. OxyPlot evaluates labels at positions a hair
    /// outside the axis range during layout, and an exception there would be thrown on the UI thread in the
    /// middle of a render. An X past the right edge simply yields a small negative age: a moment after
    /// <see cref="EndUtc"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">X is NaN or infinite.</exception>
    public double AgeSecondsAt(double x)
    {
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x), x, "X must be finite.");

        return TauSeconds * (Math.Exp((1 - x) * _logOfWindow) - 1);
    }

    /// <summary>The instant at normalized position <paramref name="x"/>. See <see cref="AgeSecondsAt"/>.</summary>
    public DateTimeOffset TimestampAt(double x) => EndUtc - TimeSpan.FromSeconds(AgeSecondsAt(x));
}
