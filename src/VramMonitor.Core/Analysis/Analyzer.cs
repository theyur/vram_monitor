using VramMonitor.Core.Configuration;
using VramMonitor.Core.Model;

namespace VramMonitor.Core.Analysis;

/// <summary>
/// Turns retained history into everything the UI shows. A pure function: no mutable state, no GPU, no
/// ambient clock.
/// </summary>
/// <remarks>
/// <para>
/// Purity is what makes spec section 14 work -- changing a threshold, the window or the grace period
/// re-evaluates the history already in memory instead of requiring a restart -- and what lets spec section
/// 19.1 be covered without a GPU.
/// </para>
/// <para>
/// <b>Two horizons.</b> The <c>retained</c> parameter of <see cref="Analyze"/> spans
/// <c>HistoryWindow + DemotionGracePeriod</c>. Only the <em>visible</em> window defines which consumers exist
/// and every figure displayed; the older grace tail is read solely by the demotion-hysteresis predicate,
/// which can keep a consumer in the aggressive list but can never introduce one.
/// </para>
/// </remarks>
public static class Analyzer
{
    public static AnalysisResult Analyze(
        IReadOnlyList<GpuSample> retained,
        IReadOnlyDictionary<ProcessSessionId, ProcessSessionInfo> sessions,
        MonitorSettings settings,
        IReadOnlyList<string> previousTrayTop5,
        DateTimeOffset now,
        long? adapterCapacityBytes = null)
    {
        ArgumentNullException.ThrowIfNull(retained);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(previousTrayTop5);

        int n = retained.Count;
        if (n == 0) return AnalysisResult.Empty;

        int visibleStart = FirstIndexAtOrAfter(retained, now - settings.HistoryWindow);
        if (visibleStart >= n) return AnalysisResult.Empty;

        long[] weightTicks = ComputeWeights(retained);
        Dictionary<string, AppTrack> tracks = BuildTracks(retained, sessions);
        MarkQualifyingSamples(retained, tracks, settings);
        BuildPrefixSums(tracks, weightTicks, n);

        // The universe: applications observed at least once inside the VISIBLE window (spec section 7).
        List<AppTrack> universe = [.. tracks.Values.Where(t => t.HasObservationFrom(visibleStart))];

        int latest = LatestObservedIndex(retained);
        DateTimeOffset? latestUtc = latest >= 0 ? retained[latest].TimestampUtc : null;

        var views = new List<ApplicationView>(universe.Count);
        foreach (AppTrack track in universe)
        {
            views.Add(BuildView(track, retained, sessions, settings, visibleStart, latest, now));
        }

        List<ApplicationView> aggressive = [.. views.Where(v => v.IsAggressive)];
        List<ApplicationView> other = [.. views.Where(v => !v.IsAggressive)];

        aggressive.Sort((a, b) => CompareAggressive(a, b, universe, visibleStart));
        other.Sort(CompareOther);

        IReadOnlyList<TrayEntry> tray = TrayRanking.Build(
            universe, retained, settings, visibleStart, latest, previousTrayTop5);

        List<string> charted = [.. views
            .Where(v => v.PeakDedicatedBytes is not null)
            .OrderByDescending(v => v.PeakDedicatedBytes)
            .ThenBy(v => v.Key, StringComparer.Ordinal)
            .Take(settings.ChartTopApplications)
            .Select(v => v.Key)];

        return new AnalysisResult(
            aggressive,
            other,
            tray,
            charted,
            BuildTotalSeries(retained, visibleStart, adapterCapacityBytes),
            BuildMarkers(retained, visibleStart),
            latestUtc);
    }

    // ---------------------------------------------------------------- weights

    /// <summary>
    /// How much real time each sample represents for aggressive-duration accounting.
    /// </summary>
    /// <remarks>
    /// The cap uses the larger of the two adjacent nominal intervals so that changing the interval at runtime
    /// does not clamp the first sample taken at the new rate -- the same threshold the sleep-gap detector and
    /// the skipped-cycle counter use, so the three can never disagree.
    /// </remarks>
    private static long[] ComputeWeights(IReadOnlyList<GpuSample> samples)
    {
        var weights = new long[samples.Count];
        weights[0] = samples[0].NominalInterval.Ticks;

        for (int i = 1; i < samples.Count; i++)
        {
            long delta = (samples[i].TimestampUtc - samples[i - 1].TimestampUtc).Ticks;
            long cap = 2 * Math.Max(samples[i - 1].NominalInterval.Ticks, samples[i].NominalInterval.Ticks);
            weights[i] = Math.Clamp(delta, 0, cap);
        }

        return weights;
    }

    // ---------------------------------------------------------------- per-application series

    private static Dictionary<string, AppTrack> BuildTracks(
        IReadOnlyList<GpuSample> samples,
        IReadOnlyDictionary<ProcessSessionId, ProcessSessionInfo> sessions)
    {
        var tracks = new Dictionary<string, AppTrack>(StringComparer.Ordinal);
        int n = samples.Count;

        for (int i = 0; i < n; i++)
        {
            GpuSample sample = samples[i];

            // A failed probe measured nothing. Every application gets a gap, but it is Missing rather than
            // Absent: the processes did not go away, we simply failed to look.
            if (sample.Outcome is ProbeOutcome.Failed)
            {
                foreach (AppTrack track in tracks.Values) track.ProbeFailed[i] = true;
                continue;
            }

            foreach (ProcessObservation observation in sample.Observations)
            {
                if (!sessions.TryGetValue(observation.SessionId, out ProcessSessionInfo? session)) continue;

                string key = session.Identity.Key;
                if (!tracks.TryGetValue(key, out AppTrack? track))
                {
                    track = new AppTrack(key, n);
                    // Backfill: a track created at sample i must still show gaps for earlier failed probes.
                    for (int j = 0; j < i; j++)
                    {
                        if (samples[j].Outcome is ProbeOutcome.Failed) track.ProbeFailed[j] = true;
                    }

                    tracks.Add(key, track);
                }

                track.Latest = session;
                track.Present[i] = true;
                track.Sessions.Add(observation.SessionId);

                if (observation.DedicatedBytes is { } dedicated) track.Sum[i] += dedicated;
                else track.AnyMissing[i] = true;   // a missing part poisons the whole aggregate

                if (observation.SharedBytes is { } shared) track.SharedSum[i] += shared;
            }
        }

        return tracks;
    }

    /// <summary>
    /// Marks, per sample independently, which applications fall in the top quartile of those above the
    /// monitoring floor (spec section 9.1).
    /// </summary>
    private static void MarkQualifyingSamples(
        IReadOnlyList<GpuSample> samples, Dictionary<string, AppTrack> tracks, MonitorSettings settings)
    {
        var candidates = new List<(long Value, AppTrack Track)>(tracks.Count);

        for (int i = 0; i < samples.Count; i++)
        {
            candidates.Clear();
            foreach (AppTrack track in tracks.Values)
            {
                if (track.MeasuredValue(i) is { } value && value >= settings.MonitoringFloorBytes)
                {
                    candidates.Add((value, track));
                }
            }

            if (candidates.Count == 0) continue;

            candidates.Sort(static (a, b) =>
            {
                int byValue = b.Value.CompareTo(a.Value);
                return byValue != 0 ? byValue : string.CompareOrdinal(a.Track.Key, b.Track.Key);
            });

            int k = Math.Max(1, (int)Math.Ceiling(candidates.Count / 4.0));
            for (int c = 0; c < k; c++) candidates[c].Track.Qualified[i] = true;
        }
    }

    /// <summary>
    /// Prefix-sums each application's qualifying weights once, so every later cumulative-time query is an
    /// O(1) subtraction rather than a re-summation over the window.
    /// </summary>
    private static void BuildPrefixSums(Dictionary<string, AppTrack> tracks, long[] weightTicks, int n)
    {
        foreach (AppTrack track in tracks.Values)
        {
            long running = 0;
            for (int i = 0; i < n; i++)
            {
                if (track.Qualified[i]) running += weightTicks[i];
                track.PrefixQualifyingTicks[i] = running;
            }
        }
    }

    // ---------------------------------------------------------------- hysteresis

    /// <summary>
    /// Cumulative qualifying time over the trailing history window ending at <paramref name="at"/>.
    /// </summary>
    /// <remarks>
    /// This is a <em>sliding</em> sum, not a prefix sum: it falls as qualifying samples leave the trailing
    /// window. That is precisely what makes demotion detectable -- a prefix sum is monotone, so it could
    /// never fall below the threshold once passed, and an application would never be demoted at all.
    /// </remarks>
    private static long CumulativeTicks(
        AppTrack track, IReadOnlyList<GpuSample> samples, DateTimeOffset at, TimeSpan window)
    {
        int hi = LastIndexAtOrBefore(samples, at);
        if (hi < 0) return 0;

        int lo = FirstIndexAtOrAfter(samples, at - window);
        if (lo > hi) return 0;

        return track.PrefixQualifyingTicks[hi] - (lo > 0 ? track.PrefixQualifyingTicks[lo - 1] : 0);
    }

    /// <summary>
    /// Whether an application is currently aggressive, including the real-time demotion grace of spec
    /// section 9.4.
    /// </summary>
    /// <remarks>
    /// The evaluation set includes the left endpoint <c>now - grace</c> deliberately. The cumulative sum drops
    /// at instants <em>between</em> samples -- exactly when a sample leaves the trailing window -- so the
    /// supremum over the grace interval is not necessarily attained at a sampled instant. Omitting the
    /// endpoint would demote up to one interval early and make the grace quietly interval-dependent.
    /// </remarks>
    private static bool IsAggressiveNow(
        AppTrack track, IReadOnlyList<GpuSample> samples, MonitorSettings settings, DateTimeOffset now)
    {
        long threshold = settings.AggressiveDurationThreshold.Ticks;
        TimeSpan window = settings.HistoryWindow;

        if (CumulativeTicks(track, samples, now, window) >= threshold) return true;
        if (settings.DemotionGracePeriod <= TimeSpan.Zero) return false;

        DateTimeOffset graceStart = now - settings.DemotionGracePeriod;
        if (CumulativeTicks(track, samples, graceStart, window) >= threshold) return true;

        for (int i = samples.Count - 1; i >= 0; i--)
        {
            DateTimeOffset t = samples[i].TimestampUtc;
            if (t > now) continue;
            if (t <= graceStart) break;
            if (CumulativeTicks(track, samples, t, window) >= threshold) return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- views

    private static ApplicationView BuildView(
        AppTrack track,
        IReadOnlyList<GpuSample> samples,
        IReadOnlyDictionary<ProcessSessionId, ProcessSessionInfo> sessions,
        MonitorSettings settings,
        int visibleStart,
        int latest,
        DateTimeOffset now)
    {
        long sum = 0;
        int measured = 0;
        long peak = long.MinValue;

        for (int i = visibleStart; i < samples.Count; i++)
        {
            if (track.MeasuredValue(i) is not { } value) continue;
            sum += value;
            measured++;
            if (value > peak) peak = value;
        }

        long? current = latest >= visibleStart ? track.MeasuredValue(latest) : null;
        long? shared = latest >= visibleStart && track.Present[latest] && !track.AnyMissing[latest]
            ? track.SharedSum[latest]
            : null;

        ProcessSessionInfo? info = track.Latest;
        List<ProcessSessionId> sessionIds = [.. track.Sessions.Distinct()];

        return new ApplicationView(
            Key: track.Key,
            DisplayName: info?.Identity.DisplayName ?? track.Key,
            ExecutablePath: info?.Identity.ExecutablePath,
            IdentityKind: info?.Identity.Kind ?? ProcessIdentityKind.PidFallback,
            CurrentDedicatedBytes: current,
            CurrentAsOfUtc: latest >= 0 ? samples[latest].TimestampUtc : null,
            AverageDedicatedBytes: measured > 0 ? sum / measured : null,
            PeakDedicatedBytes: measured > 0 ? peak : null,
            SharedBytes: shared,
            CumulativeAggressiveTime: TimeSpan.FromTicks(
                CumulativeTicks(track, samples, now, settings.HistoryWindow)),
            IsAggressive: IsAggressiveNow(track, samples, settings, now),
            SessionCount: sessionIds.Count,
            Sessions: BuildSessionViews(sessionIds, sessions, samples, visibleStart, latest),
            Series: BuildSeries(track, samples, visibleStart));
    }

    private static List<SessionView> BuildSessionViews(
        List<ProcessSessionId> sessionIds,
        IReadOnlyDictionary<ProcessSessionId, ProcessSessionInfo> sessions,
        IReadOnlyList<GpuSample> samples,
        int visibleStart,
        int latest)
    {
        var views = new List<SessionView>(sessionIds.Count);

        foreach (ProcessSessionId id in sessionIds)
        {
            if (!sessions.TryGetValue(id, out ProcessSessionInfo? info)) continue;

            var points = new List<SeriesPoint>(samples.Count - visibleStart);
            long? current = null;
            long? shared = null;

            for (int i = visibleStart; i < samples.Count; i++)
            {
                GpuSample sample = samples[i];
                ProcessObservation? observation = null;
                foreach (ProcessObservation candidate in sample.Observations)
                {
                    if (candidate.SessionId == id) { observation = candidate; break; }
                }

                PointState state;
                long value = 0;
                if (sample.Outcome is ProbeOutcome.Failed) state = PointState.Missing;
                else if (observation is null) state = PointState.Absent;
                else if (observation.DedicatedBytes is { } dedicated) { state = PointState.Measured; value = dedicated; }
                else state = PointState.Missing;

                points.Add(new SeriesPoint(sample.TimestampUtc, value, state));

                if (i == latest && state is PointState.Measured)
                {
                    current = value;
                    shared = observation?.SharedBytes;
                }
            }

            views.Add(new SessionView(
                id, info.Pid, info.ProcessStartUtc, info.FirstSeenUtc, info.LastSeenUtc,
                current, shared, InjectSamplingGaps(points, samples, visibleStart)));
        }

        views.Sort(static (a, b) => a.Pid.CompareTo(b.Pid));
        return views;
    }

    private static List<SeriesPoint> BuildSeries(
        AppTrack track, IReadOnlyList<GpuSample> samples, int visibleStart)
    {
        var points = new List<SeriesPoint>(samples.Count - visibleStart);

        for (int i = visibleStart; i < samples.Count; i++)
        {
            PointState state = track.StateAt(i);
            long value = state is PointState.Measured ? track.Sum[i] : 0;
            points.Add(new SeriesPoint(samples[i].TimestampUtc, value, state));
        }

        return InjectSamplingGaps(points, samples, visibleStart);
    }

    /// <summary>
    /// Inserts an explicit break wherever no probe ran for much longer than the interval.
    /// </summary>
    /// <remarks>
    /// Chart libraries join consecutive points regardless of how far apart in time they are, so an
    /// application present both before and immediately after a two-hour sleep would otherwise be drawn with a
    /// straight line straight across the gap -- interpolation that spec section 13.1 forbids.
    /// </remarks>
    private static List<SeriesPoint> InjectSamplingGaps(
        List<SeriesPoint> points, IReadOnlyList<GpuSample> samples, int visibleStart)
    {
        List<SeriesPoint>? result = null;

        for (int i = visibleStart + 1; i < samples.Count; i++)
        {
            if (!IsSamplingGap(samples, i)) continue;

            result ??= [.. points];
            int insertAt = result.FindIndex(p => p.TimestampUtc >= samples[i].TimestampUtc);
            var breakPoint = new SeriesPoint(
                samples[i - 1].TimestampUtc.AddTicks(1), 0, PointState.Missing);

            if (insertAt < 0) result.Add(breakPoint); else result.Insert(insertAt, breakPoint);
        }

        return result ?? points;
    }

    private static bool IsSamplingGap(IReadOnlyList<GpuSample> samples, int i)
    {
        long delta = (samples[i].TimestampUtc - samples[i - 1].TimestampUtc).Ticks;
        long cap = 2 * Math.Max(samples[i - 1].NominalInterval.Ticks, samples[i].NominalInterval.Ticks);
        return delta > cap;
    }

    private static TotalSeries BuildTotalSeries(
        IReadOnlyList<GpuSample> samples, int visibleStart, long? adapterCapacityBytes)
    {
        var points = new List<SeriesPoint>(samples.Count - visibleStart);

        for (int i = visibleStart; i < samples.Count; i++)
        {
            GpuSample sample = samples[i];
            points.Add(sample.TotalDedicatedBytes is { } total
                ? new SeriesPoint(sample.TimestampUtc, total, PointState.Measured)
                : new SeriesPoint(sample.TimestampUtc, 0, PointState.Missing));
        }

        return new TotalSeries(InjectSamplingGaps(points, samples, visibleStart), adapterCapacityBytes);
    }

    private static List<DisruptionMarker> BuildMarkers(IReadOnlyList<GpuSample> samples, int visibleStart)
    {
        var markers = new List<DisruptionMarker>();

        for (int i = visibleStart; i < samples.Count; i++)
        {
            GpuSample sample = samples[i];

            if (sample.Outcome is ProbeOutcome.Failed)
            {
                markers.Add(new DisruptionMarker(sample.TimestampUtc, DisruptionKind.ProbeFailed));
            }
            else if (sample.Outcome is ProbeOutcome.Partial)
            {
                markers.Add(new DisruptionMarker(sample.TimestampUtc, DisruptionKind.PartialSample));
            }

            if (i > visibleStart && IsSamplingGap(samples, i))
            {
                markers.Add(new DisruptionMarker(samples[i - 1].TimestampUtc, DisruptionKind.SamplingPaused));
            }
        }

        return markers;
    }

    // ---------------------------------------------------------------- ordering

    /// <summary>
    /// Spec section 9.3: cumulative aggressive time, then average while qualifying, then peak. Explainable
    /// by construction -- no opaque score.
    /// </summary>
    private static int CompareAggressive(
        ApplicationView a, ApplicationView b, List<AppTrack> universe, int visibleStart)
    {
        int byTime = b.CumulativeAggressiveTime.CompareTo(a.CumulativeAggressiveTime);
        if (byTime != 0) return byTime;

        long avgA = universe.First(t => t.Key == a.Key).AverageWhileQualifying(visibleStart);
        long avgB = universe.First(t => t.Key == b.Key).AverageWhileQualifying(visibleStart);
        int byAvg = avgB.CompareTo(avgA);
        if (byAvg != 0) return byAvg;

        int byPeak = (b.PeakDedicatedBytes ?? 0).CompareTo(a.PeakDedicatedBytes ?? 0);
        return byPeak != 0 ? byPeak : string.CompareOrdinal(a.Key, b.Key);
    }

    private static int CompareOther(ApplicationView a, ApplicationView b)
    {
        int byCurrent = (b.CurrentDedicatedBytes ?? -1).CompareTo(a.CurrentDedicatedBytes ?? -1);
        if (byCurrent != 0) return byCurrent;

        int byPeak = (b.PeakDedicatedBytes ?? -1).CompareTo(a.PeakDedicatedBytes ?? -1);
        return byPeak != 0 ? byPeak : string.CompareOrdinal(a.Key, b.Key);
    }

    // ---------------------------------------------------------------- index helpers

    /// <remarks>
    /// Binary search, not a scan. These run once per application per evaluated instant inside the demotion
    /// grace window, so at the extremes the settings permit -- a 2 second interval, a 12 hour window and an
    /// hour of grace -- a linear scan would be hundreds of millions of comparisons per pass. Samples are
    /// ordered by construction (the store rejects an out-of-order append), so the search is sound.
    /// </remarks>
    internal static int FirstIndexAtOrAfter(IReadOnlyList<GpuSample> samples, DateTimeOffset at)
    {
        int low = 0;
        int high = samples.Count;

        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (samples[mid].TimestampUtc < at) low = mid + 1;
            else high = mid;
        }

        return low;
    }

    internal static int LastIndexAtOrBefore(IReadOnlyList<GpuSample> samples, DateTimeOffset at)
    {
        int low = 0;
        int high = samples.Count;

        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (samples[mid].TimestampUtc <= at) low = mid + 1;
            else high = mid;
        }

        return low - 1;
    }

    /// <summary>The newest sample that actually observed the GPU. Spec section 10.1's "current".</summary>
    private static int LatestObservedIndex(IReadOnlyList<GpuSample> samples)
    {
        for (int i = samples.Count - 1; i >= 0; i--)
        {
            if (samples[i].Outcome is not ProbeOutcome.Failed) return i;
        }

        return -1;
    }
}
