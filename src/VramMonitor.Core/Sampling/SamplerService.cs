using System.Threading.Channels;
using VramMonitor.Core.Abstractions;
using VramMonitor.Core.Analysis;
using VramMonitor.Core.Configuration;
using VramMonitor.Core.History;
using VramMonitor.Core.Model;

namespace VramMonitor.Core.Sampling;

/// <summary>
/// Drives the sampling loop: probe, record, analyse, publish.
/// </summary>
/// <remarks>
/// <para>
/// Runs entirely off the UI thread and never touches a UI object. The UI receives finished, immutable
/// snapshots (spec section 18).
/// </para>
/// <para>
/// Sample timestamps come from a wall-clock anchor plus elapsed monotonic time rather than from the wall
/// clock directly. This makes them non-decreasing by construction, so a clock correction or a time-service
/// step cannot produce an out-of-order sample, and it means the sleep-gap detector, the aggressive-duration
/// weighting and the skipped-cycle counter all measure the same quantity.
/// </para>
/// </remarks>
public sealed class SamplerService : IAsyncDisposable
{
    private readonly IGpuMemoryProvider _provider;
    private readonly IProcessMetadataResolver _metadata;
    private readonly IClock _clock;
    private readonly RollingHistoryStore _store = new();
    private readonly ProcessSessionTracker _tracker;
    private readonly Channel<SamplerCommand> _commands =
        Channel.CreateUnbounded<SamplerCommand>(new UnboundedChannelOptions { SingleReader = true });

    private readonly List<string> _previousTrayKeys = [];

    private MonitorSettings _settings;
    private GpuInfo? _gpu;

    private DateTimeOffset _anchorWall;
    private long _anchorMonotonic;
    private DateTimeOffset? _previousTimestamp;
    private TimeSpan? _previousInterval;

    private int _failedProbes;
    private int _partialProbes;
    private int _skippedCycles;
    private int _lateCycles;
    private long _probeDurationSumTicks;
    private long _probeCount;
    private TimeSpan _lastProbeDuration;
    private TimeSpan _maxProbeDuration;
    private DateTimeOffset? _lastSuccessUtc;
    private string? _blockingError;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SamplerService(
        IGpuMemoryProvider provider,
        IProcessMetadataResolver metadata,
        MonitorSettings settings,
        IClock? clock = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? SystemClock.Instance;
        _tracker = new ProcessSessionTracker(_metadata);

        _anchorWall = _clock.UtcNow;
        _anchorMonotonic = _clock.MonotonicTicks;
        Current = MonitorSnapshot.Empty(_settings);
    }

    /// <summary>The most recently published snapshot. Always safe to read from any thread.</summary>
    public MonitorSnapshot Current { get; private set; }

    public event Action<MonitorSnapshot>? SnapshotPublished;

    /// <summary>
    /// Sets the monitored GPU before the loop starts. Once running, use <see cref="RequestGpuChange"/>.
    /// </summary>
    public void SetGpu(GpuInfo? gpu, string? blockingError = null)
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException(
                $"The sampler is running; use {nameof(RequestGpuChange)} so the change is applied on the loop thread.");
        }

        ApplyGpu(gpu, blockingError);
    }

    /// <summary>
    /// Queues a GPU change to be applied on the sampling thread.
    /// </summary>
    /// <remarks>
    /// Applying it directly would mutate the store and the session tracker while the loop is appending,
    /// trimming and enumerating them. Those are ordinary collections, so a concurrent <c>Clear</c> is a torn
    /// read or an exception, not merely a stale value.
    /// </remarks>
    public void RequestGpuChange(GpuInfo? gpu, string? blockingError = null) =>
        _commands.Writer.TryWrite(new SamplerCommand(null, gpu, blockingError, HasGpu: true));

    public void UpdateSettings(MonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _commands.Writer.TryWrite(new SamplerCommand(settings.Validated(), null, null, HasGpu: false));
    }

    private void ApplyGpu(GpuInfo? gpu, string? blockingError)
    {
        // Only a genuine change of adapter clears history. A driver reset gives the same physical GPU a new
        // LUID, and wiping history then would destroy exactly what the user wants to look at. Enumeration
        // order can also shift across a reset, so the ordinal is excluded from the comparison.
        bool differentAdapter = _gpu is not null && gpu is not null && !SameAdapter(_gpu.Selector, gpu.Selector);

        _gpu = gpu;
        _blockingError = blockingError;

        if (!differentAdapter) return;

        _store.Clear();
        _tracker.Reset();
        _previousTrayKeys.Clear();
        _previousTimestamp = null;
        _previousInterval = null;
    }

    private static bool SameAdapter(GpuSelector a, GpuSelector b) =>
        a.VendorId == b.VendorId && a.DeviceId == b.DeviceId && a.SubSysId == b.SubSysId
        && string.Equals(a.Description, b.Description, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs exactly one sampling cycle. A test seam: it lets cycles be driven deterministically against a
    /// fake clock instead of waiting on the real timer, whose minimum interval is measured in seconds.
    /// </summary>
    internal Task PumpOnceAsync(CancellationToken cancellationToken = default) =>
        SampleOnceAsync(cancellationToken);

    /// <summary>Applies any queued settings change at once. Test seam; see <see cref="PumpOnceAsync"/>.</summary>
    internal bool PumpSettings()
    {
        bool changed = false;
        while (_commands.Reader.TryRead(out SamplerCommand command))
        {
            if (command.Settings is { } updated) _settings = updated;
            if (command.HasGpu) ApplyGpu(command.Gpu, command.BlockingError);
            changed = true;
        }

        if (changed) PublishFromStore();
        return changed;
    }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var timer = new PeriodicTimer(_settings.SampleInterval);

        // PeriodicTimer allows only one outstanding wait, so the pending task is kept across iterations and
        // replaced only once it has actually completed.
        ValueTask<bool> pendingTick = timer.WaitForNextTickAsync(cancellationToken);
        Task<bool> tickTask = pendingTick.AsTask();

        try
        {
            await SampleOnceAsync(cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                Task commandReady = _commands.Reader.WaitToReadAsync(cancellationToken).AsTask();
                Task completed = await Task.WhenAny(tickTask, commandReady).ConfigureAwait(false);

                if (completed == tickTask)
                {
                    if (!await tickTask.ConfigureAwait(false)) break;
                    tickTask = timer.WaitForNextTickAsync(cancellationToken).AsTask();
                    await SampleOnceAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // A command re-analyses the history already in memory immediately, without probing, so a
                // threshold edit is visible at once rather than at the next tick.
                try
                {
                    if (DrainCommands(timer)) PublishFromStore();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Without this the task would fault silently and monitoring would stop dead.
                    RecordPipelineFailure(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            timer.Dispose();
        }
    }

    /// <summary>Applies every queued command on the sampling thread. Returns true if anything changed.</summary>
    private bool DrainCommands(PeriodicTimer timer)
    {
        bool changed = false;

        while (_commands.Reader.TryRead(out SamplerCommand command))
        {
            if (command.Settings is { } updated)
            {
                bool intervalChanged = updated.SampleInterval != _settings.SampleInterval;
                _settings = updated;

                // Assigning Period rather than rebuilding the timer keeps the outstanding wait valid.
                if (intervalChanged) timer.Period = updated.SampleInterval;
            }

            if (command.HasGpu) ApplyGpu(command.Gpu, command.BlockingError);

            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Runs one cycle. Nothing is allowed to escape: an unhandled exception here would silently kill the
    /// background task, leaving the tray showing stale values with no marker and no rising error count.
    /// </summary>
    private async Task SampleOnceAsync(CancellationToken cancellationToken)
    {
        long startTicks = _clock.MonotonicTicks;
        DateTimeOffset timestamp = NextTimestamp();

        GpuSample sample;
        try
        {
            sample = await ProbeAsync(timestamp, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sample = new GpuSample(timestamp, _settings.SampleInterval, ProbeOutcome.Failed, null, [], ex.Message);
        }

        try
        {
            RecordCadence(timestamp, sample.NominalInterval);
            RecordOutcome(sample);

            _store.Append(sample);
            _store.Trim(timestamp, _settings.RetentionHorizon);

            RecordDuration(_clock.MonotonicTicks - startTicks);
            PublishFromStore();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordPipelineFailure(ex);
        }
    }

    /// <summary>
    /// Records a failure from anywhere in the cycle other than the probe itself.
    /// </summary>
    /// <remarks>
    /// The failure is written into the history as a <see cref="ProbeOutcome.Failed"/> sample where possible,
    /// not merely counted, so the chart shows a gap and a disruption marker rather than an unexplained flat
    /// line. If recording it also fails, the counter and the health message are the fallback: the loop must
    /// survive either way.
    /// </remarks>
    private void RecordPipelineFailure(Exception ex)
    {
        _failedProbes++;

        try
        {
            DateTimeOffset at = NextTimestamp();
            _store.Append(new GpuSample(
                at, _settings.SampleInterval, ProbeOutcome.Failed, null, [], ex.Message));
            _store.Trim(at, _settings.RetentionHorizon);
            _previousTimestamp = at;
            PublishFromStore();
            return;
        }
        catch
        {
            // Fall through to the counter-only path below.
        }

        Current = Current with { Health = BuildHealth() with { BlockingError = $"Sampling error: {ex.Message}" } };
        SnapshotPublished?.Invoke(Current);
    }

    private async Task<GpuSample> ProbeAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        if (_gpu is null)
        {
            return new GpuSample(
                timestamp, _settings.SampleInterval, ProbeOutcome.Failed, null, [], "No GPU selected.");
        }

        GpuSnapshot snapshot = await _provider.GetSnapshotAsync(_gpu.Id, cancellationToken).ConfigureAwait(false);

        _tracker.BeginProbe(snapshot.Outcome);

        if (snapshot.Outcome is ProbeOutcome.Failed)
        {
            return new GpuSample(
                timestamp, _settings.SampleInterval, ProbeOutcome.Failed, null, [], snapshot.FailureReason);
        }

        var observations = new List<ProcessObservation>(snapshot.Measurements.Count);
        foreach (RawProcessMeasurement measurement in snapshot.Measurements)
        {
            ProcessSessionInfo session = _tracker.Observe(measurement.Pid, _gpu.Id, timestamp);
            _store.UpsertSession(session);
            observations.Add(new ProcessObservation(
                session.SessionId, measurement.DedicatedBytes, measurement.SharedBytes));
        }

        return new GpuSample(
            timestamp,
            _settings.SampleInterval,
            snapshot.Outcome,
            snapshot.TotalDedicatedBytes,
            observations,
            snapshot.FailureReason);
    }

    private void PublishFromStore()
    {
        DateTimeOffset now = NowFromAnchor();
        _store.Trim(now, _settings.RetentionHorizon);

        AnalysisResult analysis = Analyzer.Analyze(
            _store.Samples, _store.Sessions, _settings, _previousTrayKeys, now, _gpu?.DedicatedVideoMemoryBytes);

        _previousTrayKeys.Clear();
        _previousTrayKeys.AddRange(analysis.TrayTop5.Select(t => t.Key));

        // Copies, not the store's live collections: the sampler mutates those on the next tick, and export
        // reads this snapshot from another thread without any lock.
        int visibleStart = _store.FirstVisibleIndex(now, _settings.HistoryWindow);
        var samples = new GpuSample[_store.Count - visibleStart];
        for (int i = 0; i < samples.Length; i++) samples[i] = _store.Samples[visibleStart + i];

        Current = new MonitorSnapshot(
            _gpu,
            _settings,
            now,
            samples,
            VisibleSessions(samples),
            analysis,
            BuildHealth());

        SnapshotPublished?.Invoke(Current);
    }

    // ---------------------------------------------------------------- time

    private DateTimeOffset NowFromAnchor() =>
        _anchorWall + TimeSpan.FromTicks(_clock.MonotonicTicks - _anchorMonotonic);

    /// <summary>
    /// The session records referenced by the visible slice, copied.
    /// </summary>
    /// <remarks>
    /// Scoped to the visible window rather than the whole retention horizon, so a session that survives only
    /// in the grace tail -- retained purely so hysteresis can look back -- is never listed or exported.
    /// </remarks>
    private Dictionary<ProcessSessionId, ProcessSessionInfo> VisibleSessions(IReadOnlyList<GpuSample> samples)
    {
        var sessions = new Dictionary<ProcessSessionId, ProcessSessionInfo>();

        foreach (GpuSample sample in samples)
        {
            foreach (ProcessObservation observation in sample.Observations)
            {
                if (sessions.ContainsKey(observation.SessionId)) continue;
                if (_store.TryGetSession(observation.SessionId) is { } info) sessions[observation.SessionId] = info;
            }
        }

        return sessions;
    }

    /// <summary>
    /// The timestamp for the cycle now starting: a wall-clock anchor plus elapsed monotonic time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The anchor is only ever moved <em>forward</em>. That is what makes the result non-decreasing: monotonic
    /// ticks never go back, so if the anchor never goes back either, neither can the timestamp.
    /// </para>
    /// <para>
    /// Re-anchoring on a backward correction would be actively harmful. It would leave the anchor behind the
    /// newest stored sample, and every later cycle would then derive a timestamp older than the history it is
    /// being appended to -- which the store rejects outright. Sampling would fail on every tick until real
    /// time caught up with the size of the correction. A backward wall-clock step is therefore ignored here;
    /// the monitor keeps its own consistent timeline.
    /// </para>
    /// </remarks>
    private DateTimeOffset NextTimestamp()
    {
        DateTimeOffset derived = NowFromAnchor();
        DateTimeOffset wall = _clock.UtcNow;

        // Forward only. A large forward discrepancy means the wall clock was corrected ahead of our
        // timeline; following it keeps timestamps meaningful, and the jump shows up as a sampling gap.
        if (wall - derived > _settings.SampleInterval + TimeSpan.FromSeconds(1))
        {
            _anchorWall = wall;
            _anchorMonotonic = _clock.MonotonicTicks;
            derived = wall;
        }

        return derived;
    }

    private void RecordCadence(DateTimeOffset timestamp, TimeSpan nominalInterval)
    {
        if (_previousInterval is { } previousInterval && _previousTimestamp is { } previous)
        {
            // The same threshold the analyzer uses for sleep gaps, so the counter here and the marker on the
            // chart can never disagree about what counts as a skipped stretch.
            TimeSpan delta = timestamp - previous;
            TimeSpan reference = previousInterval > nominalInterval ? previousInterval : nominalInterval;

            if (delta > reference * 2) _skippedCycles++;
            else if (delta > reference * 1.5) _lateCycles++;
        }

        _previousTimestamp = timestamp;
        _previousInterval = nominalInterval;
    }

    private void RecordOutcome(GpuSample sample)
    {
        switch (sample.Outcome)
        {
            case ProbeOutcome.Failed: _failedProbes++; break;
            case ProbeOutcome.Partial: _partialProbes++; _lastSuccessUtc = sample.TimestampUtc; break;
            default: _lastSuccessUtc = sample.TimestampUtc; break;
        }
    }

    private void RecordDuration(long elapsedTicks)
    {
        _lastProbeDuration = TimeSpan.FromTicks(Math.Max(0, elapsedTicks));
        _probeDurationSumTicks += _lastProbeDuration.Ticks;
        _probeCount++;
        if (_lastProbeDuration > _maxProbeDuration) _maxProbeDuration = _lastProbeDuration;
    }

    private MonitorHealth BuildHealth() => new(
        _lastSuccessUtc,
        _failedProbes,
        _partialProbes,
        _skippedCycles,
        _lateCycles,
        _lastProbeDuration,
        _probeCount > 0 ? TimeSpan.FromTicks(_probeDurationSumTicks / _probeCount) : TimeSpan.Zero,
        _maxProbeDuration,
        _blockingError);

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
    }
}
