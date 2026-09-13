using VramMonitor.Core.Abstractions;
using VramMonitor.Core.Model;

namespace VramMonitor.Core.Sampling;

/// <summary>
/// Maps observed PIDs onto process sessions, and owns identity resolution for them.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="ProcessSessionId"/> is opaque and assigned once. Observations reference only the id, so
/// upgrading a session's identity later never rewrites recorded history.
/// </para>
/// <para>
/// PID reuse is detected from process creation time, which is re-read on every probe. Where the creating
/// process denies a handle -- protected system processes, and anti-cheat protected games, which are also
/// among the heaviest VRAM consumers -- creation time is unavailable, and a restart is instead inferred from
/// the process having been absent from an intervening probe.
/// </para>
/// </remarks>
public sealed class ProcessSessionTracker(IProcessMetadataResolver resolver)
{
    /// <summary>
    /// How many probes an unidentified session is retried before its identity is frozen. Denial by a
    /// protected process is permanent, so retrying it forever would run the expensive tier-2 bulk scan on
    /// every probe for the whole life of the application.
    /// </summary>
    public const int MaxIdentityAttempts = 3;

    private readonly IProcessMetadataResolver _resolver = resolver;
    private readonly Dictionary<uint, LiveSession> _byPid = [];

    private long _nextSessionId = 1;

    /// <summary>
    /// Counts only probes that actually observed the GPU. A failed probe does not advance it, which is what
    /// stops a failed probe from being read as every process having vanished.
    /// </summary>
    private long _observedProbeSequence;

    private sealed class LiveSession
    {
        public required ProcessSessionId SessionId { get; init; }
        public required uint Pid { get; init; }
        public long CreationTicks { get; set; }
        public required GpuId Gpu { get; set; }
        public required ProcessIdentity Identity { get; set; }
        public required DateTimeOffset FirstSeenUtc { get; init; }
        public DateTimeOffset LastSeenUtc { get; set; }
        public DateTimeOffset? ProcessStartUtc { get; set; }
        public long LastSeenSequence { get; set; }
        public int IdentityAttempts { get; set; }

        public ProcessSessionInfo ToInfo() => new(
            SessionId, Pid, CreationTicks, Gpu, Identity, FirstSeenUtc, LastSeenUtc, ProcessStartUtc);
    }

    /// <summary>
    /// Announces a probe. Only <see cref="ProbeOutcome.Ok"/> and <see cref="ProbeOutcome.Partial"/> count as
    /// evidence about which processes exist; a failed probe observed nothing and must not imply absence.
    /// </summary>
    public void BeginProbe(ProbeOutcome outcome)
    {
        if (outcome is ProbeOutcome.Failed) return;
        _observedProbeSequence++;
        _resolver.BeginProbe();
    }

    /// <summary>Resolves the session for a PID seen in the current probe, creating one if needed.</summary>
    public ProcessSessionInfo Observe(uint pid, GpuId gpu, DateTimeOffset nowUtc)
    {
        long creationTicks = _resolver.GetTimes(pid)?.CreationTicks ?? 0;

        if (_byPid.TryGetValue(pid, out LiveSession? existing) && IsSameProcess(existing, creationTicks)
            && TryAdoptCreationTime(existing, pid, creationTicks))
        {
            existing.Gpu = gpu;
            existing.LastSeenUtc = nowUtc;
            existing.LastSeenSequence = _observedProbeSequence;
            TryUpgradeIdentity(existing, pid);
            return existing.ToInfo();
        }

        var session = new LiveSession
        {
            SessionId = new ProcessSessionId(_nextSessionId++),
            Pid = pid,
            CreationTicks = creationTicks,
            Gpu = gpu,
            Identity = ResolveIdentity(pid),
            FirstSeenUtc = nowUtc,
            LastSeenUtc = nowUtc,
            ProcessStartUtc = creationTicks == 0 ? null : DateTimeOffset.FromFileTime(creationTicks),
            LastSeenSequence = _observedProbeSequence,
            IdentityAttempts = 1,
        };

        _byPid[pid] = session;
        return session.ToInfo();
    }

    private bool IsSameProcess(LiveSession existing, long creationTicks)
    {
        // Both creation times known: they decide the question outright.
        if (existing.CreationTicks != 0 && creationTicks != 0) return existing.CreationTicks == creationTicks;

        // Creation time unavailable. The process counts as the same one only if it has not been missing from
        // any probe that actually observed the GPU since we last saw it.
        return existing.LastSeenSequence >= _observedProbeSequence - 1;
    }

    /// <summary>
    /// Handles a creation time that was denied before and is readable now.
    /// </summary>
    /// <returns>
    /// True when the session continues; false when the evidence says this is a different process and a new
    /// session must be started.
    /// </returns>
    /// <remarks>
    /// The name check is what stops a permanent mis-attribution. A handle-denying process can exit and have
    /// its PID reused by an accessible one inside a single interval, so no probe ever sees it absent and
    /// <see cref="IsSameProcess"/> cannot tell them apart. Without this check the newcomer would inherit the
    /// old session's frozen identity for its whole lifetime -- and would never be re-resolved, because its
    /// kind is no longer <see cref="ProcessIdentityKind.PidFallback"/>.
    /// </remarks>
    private bool TryAdoptCreationTime(LiveSession session, uint pid, long creationTicks)
    {
        if (session.CreationTicks != 0 || creationTicks == 0) return true;

        // The handle is open now, so tier 1 can answer who this actually is.
        string? path = _resolver.ResolvePath(pid);
        string? fileName = path is null ? null : FileNameOf(path);

        if (fileName is not null && session.Identity.ExecutableName is { } known
            && !string.Equals(fileName, known, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        session.CreationTicks = creationTicks;
        session.ProcessStartUtc = DateTimeOffset.FromFileTime(creationTicks);

        if (path is not null && session.Identity.Kind != ProcessIdentityKind.ExecutablePath)
        {
            session.Identity = ApplicationIdentity.FromPath(path, _resolver.ResolveDisplayName(path));
        }

        return true;
    }

    /// <remarks>
    /// Tier 1 is retried while the session lacks a full path: it costs microseconds, and a single transient
    /// <c>OpenProcess</c> race would otherwise freeze an ordinary process at <c>name:python.exe</c> while its
    /// siblings are keyed by full path, splitting one program into two applications. Tier 2 is the expensive
    /// bulk scan and runs only while the session has no identity at all.
    /// </remarks>
    private void TryUpgradeIdentity(LiveSession session, uint pid)
    {
        if (session.Identity.Kind == ProcessIdentityKind.ExecutablePath) return;
        if (session.IdentityAttempts >= MaxIdentityAttempts) return;

        session.IdentityAttempts++;

        string? path = _resolver.ResolvePath(pid);
        if (!string.IsNullOrEmpty(path))
        {
            session.Identity = ApplicationIdentity.FromPath(path, _resolver.ResolveDisplayName(path));
            return;
        }

        if (session.Identity.Kind != ProcessIdentityKind.PidFallback) return;

        string? name = _resolver.ResolveNameOnly(pid);
        if (!string.IsNullOrEmpty(name)) session.Identity = ApplicationIdentity.FromName(name);
    }

    private ProcessIdentity ResolveIdentity(uint pid)
    {
        string? path = _resolver.ResolvePath(pid);
        if (!string.IsNullOrEmpty(path)) return ApplicationIdentity.FromPath(path, _resolver.ResolveDisplayName(path));

        string? name = _resolver.ResolveNameOnly(pid);
        if (!string.IsNullOrEmpty(name)) return ApplicationIdentity.FromName(name);

        return ApplicationIdentity.FromPid(pid);
    }

    private static string FileNameOf(string path)
    {
        int slash = path.LastIndexOfAny(['\\', '/']);
        return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
    }

    /// <summary>Every session currently mapped to a live PID.</summary>
    public IEnumerable<ProcessSessionInfo> LiveSessions => _byPid.Values.Select(s => s.ToInfo());

    public void Reset()
    {
        _byPid.Clear();
        _observedProbeSequence = 0;
    }
}
