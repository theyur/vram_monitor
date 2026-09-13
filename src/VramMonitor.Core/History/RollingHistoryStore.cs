using VramMonitor.Core.Model;

namespace VramMonitor.Core.History;

/// <summary>
/// In-memory rolling history. RAM only -- nothing is ever written to disk (spec section 3.1).
/// </summary>
/// <remarks>
/// <para>
/// The store keeps two deliberately distinct horizons:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Retention horizon</b> = <c>HistoryWindow + DemotionGracePeriod</c>. Everything the store holds.
/// The extra, older tail exists solely so demotion hysteresis can evaluate a trailing
/// <c>HistoryWindow</c> cumulative at instants as old as <c>now - DemotionGracePeriod</c>.
/// </description></item>
/// <item><description>
/// <b>Visible window</b> = <c>HistoryWindow</c>. Governs everything the spec's retention rules talk
/// about: what is listed, charted, exported and inspectable. A consumer whose last observation has
/// fallen out of this window is gone from the UI even though the sample may still sit in the tail.
/// </description></item>
/// </list>
/// <para>
/// Keeping these apart is what lets the hysteresis rule work without extending consumer lifetime
/// beyond what spec section 7 allows.
/// </para>
/// <para>
/// At a 10 second interval over 60 minutes this holds 360 samples, so a plain list is the right
/// structure; spec section 18 explicitly warns against over-engineering it.
/// </para>
/// </remarks>
public sealed class RollingHistoryStore
{
    private readonly List<GpuSample> _samples = [];
    private readonly Dictionary<ProcessSessionId, ProcessSessionInfo> _sessions = [];

    /// <summary>Every retained sample, oldest first. Spans the full retention horizon.</summary>
    public IReadOnlyList<GpuSample> Samples => _samples;

    /// <summary>Known process sessions, including those now visible only in the grace tail.</summary>
    public IReadOnlyDictionary<ProcessSessionId, ProcessSessionInfo> Sessions => _sessions;

    public int Count => _samples.Count;

    public void Append(GpuSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        // Samples must stay ordered; a backwards wall-clock step would otherwise corrupt every
        // window calculation. Scheduling uses a monotonic clock, so this is a safety net only.
        if (_samples.Count > 0 && sample.TimestampUtc < _samples[^1].TimestampUtc)
        {
            throw new ArgumentException(
                $"Sample timestamp {sample.TimestampUtc:O} precedes the previous sample " +
                $"{_samples[^1].TimestampUtc:O}.", nameof(sample));
        }

        _samples.Add(sample);
    }

    public void UpsertSession(ProcessSessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _sessions[session.SessionId] = session;
    }

    public ProcessSessionInfo? TryGetSession(ProcessSessionId id) =>
        _sessions.TryGetValue(id, out ProcessSessionInfo? s) ? s : null;

    /// <summary>
    /// Drops samples older than the retention horizon, then drops session records that no retained
    /// sample refers to any more. Removing the session record is what removes a consumer's aggregate
    /// history, its PID/session history and its UI row together (spec section 7).
    /// </summary>
    /// <returns>The number of samples evicted.</returns>
    public int Trim(DateTimeOffset now, TimeSpan retentionHorizon)
    {
        DateTimeOffset cutoff = now - retentionHorizon;

        int drop = 0;
        while (drop < _samples.Count && _samples[drop].TimestampUtc < cutoff) drop++;
        if (drop > 0) _samples.RemoveRange(0, drop);

        if (_sessions.Count > 0) RemoveOrphanedSessions();
        return drop;
    }

    private void RemoveOrphanedSessions()
    {
        var live = new HashSet<ProcessSessionId>();
        foreach (GpuSample sample in _samples)
        {
            foreach (ProcessObservation observation in sample.Observations) live.Add(observation.SessionId);
        }

        if (live.Count == _sessions.Count) return;

        List<ProcessSessionId>? dead = null;
        foreach (ProcessSessionId id in _sessions.Keys)
        {
            if (!live.Contains(id)) (dead ??= []).Add(id);
        }

        if (dead is null) return;
        foreach (ProcessSessionId id in dead) _sessions.Remove(id);
    }

    /// <summary>
    /// Index of the first sample inside the visible window, i.e. the start of the slice that drives
    /// the UI, the chart and export. Returns <see cref="Count"/> when nothing is visible.
    /// </summary>
    public int FirstVisibleIndex(DateTimeOffset now, TimeSpan historyWindow)
    {
        DateTimeOffset cutoff = now - historyWindow;
        int i = 0;
        while (i < _samples.Count && _samples[i].TimestampUtc < cutoff) i++;
        return i;
    }

    public void Clear()
    {
        _samples.Clear();
        _sessions.Clear();
    }
}
