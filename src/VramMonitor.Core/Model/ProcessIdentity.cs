namespace VramMonitor.Core.Model;

/// <summary>
/// Opaque, monotonically increasing identifier for one observed process lifetime.
/// </summary>
/// <remarks>
/// Assigned once, at first sight, and never changed. Because observations reference only this id,
/// a later upgrade of the process's identity (for example when a previously denied executable path
/// finally resolves) updates the session record alone and never rewrites recorded history.
/// </remarks>
public readonly record struct ProcessSessionId(long Value)
{
    public override string ToString() => $"s{Value}";
}

/// <summary>How confidently a process was identified. Mirrors the fallback order in spec section 5.3.</summary>
public enum ProcessIdentityKind
{
    /// <summary>Full executable path was resolved.</summary>
    ExecutablePath,

    /// <summary>Only the executable name was available.</summary>
    ExecutableName,

    /// <summary>Neither path nor name could be resolved; identified by PID alone.</summary>
    PidFallback,
}

/// <summary>
/// The identity an application is grouped by. <see cref="Key"/> is the grouping key: the normalized
/// full executable path when available, so that different installations of the same executable
/// (for example two <c>python.exe</c> environments) never merge.
/// </summary>
public sealed record ProcessIdentity(
    string Key,
    string DisplayName,
    string? ExecutablePath,
    string? ExecutableName,
    ProcessIdentityKind Kind);

/// <summary>One observed process lifetime. Identity fields are mutable across probes; the id is not.</summary>
public sealed record ProcessSessionInfo(
    ProcessSessionId SessionId,
    uint Pid,
    long CreationTicks,
    GpuId Gpu,
    ProcessIdentity Identity,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    DateTimeOffset? ProcessStartUtc)
{
    /// <summary>True when the creating process denied a handle, so its creation time is unknown.</summary>
    public bool HasUnknownCreationTime => CreationTicks == 0;
}
