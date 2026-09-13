using VramMonitor.Core.Configuration;
using VramMonitor.Core.Model;

namespace VramMonitor.Core.Analysis;

/// <summary>
/// Builds the tray tooltip's top consumers (spec section 10.1) with enough hysteresis that small
/// fluctuations do not constantly reorder it.
/// </summary>
/// <remarks>
/// Smoothing decides <em>ordering only</em>. Each row still shows the raw current measurement, because the
/// tooltip claims to show what is happening now.
/// </remarks>
internal static class TrayRanking
{
    private readonly record struct Candidate(string Key, string DisplayName, long Current, double Ema);

    public static IReadOnlyList<TrayEntry> Build(
        List<AppTrack> universe,
        IReadOnlyList<GpuSample> samples,
        MonitorSettings settings,
        int visibleStart,
        int latest,
        IReadOnlyList<string> previousTop)
    {
        // R1: only applications with a real measurement in the newest observed sample are eligible. Without
        // this an exited application could linger in a "current" view showing a stale smoothed number.
        if (latest < visibleStart) return [];

        var eligible = new List<Candidate>(universe.Count);
        foreach (AppTrack track in universe)
        {
            if (track.MeasuredValue(latest) is not { } current) continue;
            eligible.Add(new Candidate(
                track.Key,
                track.Latest?.Identity.DisplayName ?? track.Key,
                current,
                Smooth(track, samples, visibleStart, latest, settings.TraySmoothingTimeConstant)));
        }

        if (eligible.Count == 0) return [];

        var byKey = eligible.ToDictionary(c => c.Key, StringComparer.Ordinal);
        int max = settings.TrayEntryCount;

        // R3: incumbents that are still eligible keep their places, in order.
        var chosen = new List<Candidate>(max);
        foreach (string key in previousTop)
        {
            if (chosen.Count >= max) break;
            if (byKey.TryGetValue(key, out Candidate incumbent) && !chosen.Contains(incumbent))
            {
                chosen.Add(incumbent);
            }
        }

        var rest = eligible
            .Where(c => !chosen.Any(x => x.Key == c.Key))
            .OrderByDescending(c => c.Ema)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToList();

        // Fill any free slots outright -- there is no incumbent to protect.
        while (chosen.Count < max && rest.Count > 0)
        {
            chosen.Add(rest[0]);
            rest.RemoveAt(0);
        }

        // R3 continued: a challenger must clear the margin to take the weakest incumbent's slot. The margin
        // is charged against the incumbent, so a demoted entry cannot immediately win its place back.
        while (rest.Count > 0)
        {
            Candidate weakest = chosen.OrderBy(c => c.Ema).ThenBy(c => c.Key, StringComparer.Ordinal).First();
            Candidate challenger = rest[0];
            if (!Beats(challenger.Ema, weakest.Ema, settings)) break;

            chosen[chosen.IndexOf(weakest)] = challenger;   // challenger takes the displaced slot
            rest.RemoveAt(0);                                // the demoted incumbent does not re-enter
        }

        // R4: one stable pass, so at most one position changes per entry per sample.
        for (int i = chosen.Count - 1; i > 0; i--)
        {
            if (Beats(chosen[i].Ema, chosen[i - 1].Ema, settings))
            {
                (chosen[i - 1], chosen[i]) = (chosen[i], chosen[i - 1]);
            }
        }

        return [.. chosen.Select(c => new TrayEntry(c.Key, c.DisplayName, c.Current))];
    }

    private static bool Beats(double challenger, double incumbent, MonitorSettings settings)
    {
        double margin = Math.Max(
            settings.TrayHysteresisMarginBytes,
            incumbent * settings.TrayHysteresisMarginFraction);
        return challenger > incumbent + margin;
    }

    /// <summary>
    /// Exponential moving average over the visible window.
    /// </summary>
    /// <remarks>
    /// The average restarts at the first measurement after any gap. Carrying a value across a discontinuity
    /// would let a number measured before the gap keep influencing the ordering afterwards, which is the
    /// carry-forward that spec section 5.2 rules out.
    /// </remarks>
    private static double Smooth(
        AppTrack track, IReadOnlyList<GpuSample> samples, int visibleStart, int latest, TimeSpan tau)
    {
        double ema = 0;
        bool seeded = false;
        DateTimeOffset previous = default;

        for (int i = visibleStart; i <= latest; i++)
        {
            if (track.MeasuredValue(i) is not { } value)
            {
                seeded = false;      // restart on the next measurement rather than skipping the gap
                continue;
            }

            DateTimeOffset at = samples[i].TimestampUtc;
            if (!seeded)
            {
                ema = value;
                seeded = true;
            }
            else if (tau > TimeSpan.Zero)
            {
                double dt = (at - previous).TotalSeconds;
                double alpha = 1 - Math.Exp(-dt / tau.TotalSeconds);
                ema += alpha * (value - ema);
            }
            else
            {
                ema = value;
            }

            previous = at;
        }

        return ema;
    }
}
