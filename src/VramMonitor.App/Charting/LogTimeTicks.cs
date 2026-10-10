namespace VramMonitor.App.Charting;

/// <summary>
/// Chooses where the logarithmic time axis puts its ticks, from the history window alone.
/// </summary>
/// <remarks>
/// <para>
/// The choice is deliberately a pure function of the window with no reference to any measured size. OxyPlot's
/// own tick selection is derived from the plot area's pixel width, and that width is not constant even when
/// the window is: the left value axis has an automatic maximum, so its widest label grows from "500" to
/// "12288" as data arrives and takes pixels from the plot, and the outside legend re-flows as the charted
/// series change. Under a width-driven policy the label density flipped between successive refreshes at an
/// unchanged window size. A policy that cannot see the width cannot do that.
/// </para>
/// <para>
/// A uniform time step, which is what the linear axis used, would be wrong here: on a logarithmic axis it
/// crowds the old end into an unreadable smear while leaving the recent end bare. Instead ticks sit at round
/// ages chosen for their spacing on screen. Because the positions are ages rather than clock times they stay
/// put as samples arrive; only their clock labels advance with the snapshot.
/// </para>
/// </remarks>
public static class LogTimeTicks
{
    /// <summary>Smallest normalized distance between any two ticks, endpoints included.</summary>
    public const double MinSeparation = 0.14;

    /// <summary>Most ticks allowed between the two endpoints; six labels in all.</summary>
    public const int MaxInteriorTicks = 4;

    /// <summary>Candidate interior ages, ascending. Visited newest first.</summary>
    /// <remarks>
    /// The entries beyond eight hours cannot be reached while <c>MonitorSettings</c> clamps the window to
    /// twelve hours, since a candidate must be strictly younger than the window; twelve hours is there so that
    /// raising that ceiling needs no second edit.
    /// </remarks>
    private static readonly TimeSpan[] Ladder =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(3),
        TimeSpan.FromHours(4),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(8),
        TimeSpan.FromHours(12),
    ];

    /// <summary>The candidates, for tests.</summary>
    internal static IReadOnlyList<TimeSpan> Candidates => Ladder;

    /// <summary>
    /// The tick ages for a window, ordered by ascending X: the window itself first (the left edge), zero last
    /// (the right edge).
    /// </summary>
    public static IReadOnlyList<TimeSpan> For(TimeSpan window)
    {
        var scale = new LogTimeScale(DateTimeOffset.UnixEpoch, window);

        // The endpoints are reserved first, so every interior candidate is also kept clear of both edges.
        var accepted = new List<(TimeSpan Age, double X)>
        {
            (TimeSpan.Zero, scale.XForAge(0)),
            (window, scale.XForAge(window.TotalSeconds)),
        };

        int interior = 0;
        foreach (TimeSpan candidate in Ladder)
        {
            if (interior == MaxInteriorTicks) break;
            if (candidate <= TimeSpan.Zero || candidate >= window) continue;

            double x = scale.XForAge(candidate.TotalSeconds);
            if (accepted.Any(t => Math.Abs(t.X - x) < MinSeparation)) continue;

            accepted.Add((candidate, x));
            interior++;
        }

        return [.. accepted.OrderBy(t => t.X).Select(t => t.Age)];
    }

    /// <summary>
    /// Whether the labels need seconds: only when some interior tick is younger than a minute. Otherwise every
    /// label would end in a ":00" that costs width and says nothing.
    /// </summary>
    public static bool NeedsSeconds(IReadOnlyList<TimeSpan> ages, TimeSpan window) =>
        ages.Any(a => a > TimeSpan.Zero && a < window && a < TimeSpan.FromMinutes(1));
}
