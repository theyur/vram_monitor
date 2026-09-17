namespace VramMonitor.App.Charting;

/// <summary>
/// Chooses the time axis' tick step from the history window alone.
/// </summary>
/// <remarks>
/// <para>
/// This exists because OxyPlot's own choice is derived from the plot area's pixel width, and that width is
/// not constant even when the window is -- see <see cref="ChartBuilder"/> for the measurement. Picking the
/// step here is deliberately a pure function of the window with no reference to any measured size: a step
/// that cannot see the plot's width cannot change when only the plot's width changes.
/// </para>
/// </remarks>
public static class TimeAxisStep
{
    /// <summary>
    /// The most labels any window may ask for.
    /// </summary>
    /// <remarks>
    /// Six is what stays legible at the narrowest plot the layout permits. Dragging the splitter to the
    /// chart column's <c>MinWidth</c> of 360 leaves roughly 210px of plot area once both value axes and
    /// their titles are paid for, which is a ~35px pitch at six labels: enough for "14:48" and not for
    /// "14:48:00". That is also why <see cref="ChartBuilder"/> shows seconds only while the step is finer
    /// than a minute.
    /// </remarks>
    public const int MaxLabels = 6;

    /// <summary>
    /// Candidate steps, ascending.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every entry must divide 24 hours exactly</b>, and that is not cosmetic: OxyPlot places ticks at
    /// whole multiples of the step measured from midnight, so a step of seven minutes labels 14:31, 14:38,
    /// 14:45 rather than round times. A new entry that does not divide 24 hours will still render -- it
    /// will merely look subtly wrong -- which is why a test asserts the property rather than a comment
    /// asking for it.
    /// </para>
    /// <para>
    /// The entries above two hours cannot be reached while <see cref="Core.Configuration.MonitorSettings"/>
    /// clamps the window to 12 hours; they are here so that raising that ceiling needs no second edit.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan[] Ladder =
    [
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
        TimeSpan.FromHours(12),
    ];

    /// <summary>The candidates, for the test that guards the divides-24-hours invariant.</summary>
    internal static IReadOnlyList<TimeSpan> Candidates => Ladder;

    /// <summary>
    /// The finest candidate step that the window needs no more than <see cref="MaxLabels"/> of.
    /// </summary>
    /// <remarks>
    /// The window is not necessarily a whole multiple of the step -- a user may ask for seven minutes -- in
    /// which case the number of ticks falling inside the range alternates by one as the window slides and a
    /// single label winks at the left edge. The <em>pitch</em> stays constant, which is the property that
    /// was broken. Removing that last label would mean snapping the axis range, which costs either the
    /// newest samples or a step's worth of blank at the leading edge, and would make the span -- and so the
    /// horizontal scale of every series -- vary between refreshes.
    /// </remarks>
    public static TimeSpan For(TimeSpan historyWindow)
    {
        // MonitorSettings.Validated() clamps the window into [1 minute, 12 hours] long before it reaches a
        // snapshot, but the chart is not the place to rely on that having happened: a zero or negative
        // window simply matches the first candidate rather than falling through to anything surprising.
        foreach (TimeSpan candidate in Ladder)
        {
            if (historyWindow <= candidate * MaxLabels) return candidate;
        }

        return Ladder[^1];
    }
}
