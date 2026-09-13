namespace VramMonitor.Core.Abstractions;

/// <summary>Creation time of a process, used to distinguish process lifetimes that reuse a PID.</summary>
/// <param name="CreationTicks">Zero when the process denied a handle and its creation time is unknown.</param>
public readonly record struct ProcessTimes(long CreationTicks);

/// <summary>
/// Resolves process metadata independently of VRAM measurement, so that a metadata failure never
/// discards a valid measurement (spec section 5.3).
/// </summary>
public interface IProcessMetadataResolver
{
    /// <summary>
    /// Cheap per-probe lookup of a process's creation time. Called for every observed PID on every
    /// probe, which is what makes PID-reuse detection possible.
    /// </summary>
    ProcessTimes? GetTimes(uint pid);

    /// <summary>Full executable path, or null when it cannot be read.</summary>
    string? ResolvePath(uint pid);

    /// <summary>Executable name only, from a bulk process listing. Tier-2 fallback.</summary>
    string? ResolveNameOnly(uint pid);

    /// <summary>
    /// Friendly display name for an executable, read from its version resource. Lives here rather than in
    /// the analysis layer because it is disk I/O: the analyzer must stay a pure function so it can be
    /// unit-tested without a file system. Returns null when no description is available.
    /// </summary>
    string? ResolveDisplayName(string executablePath);

    /// <summary>Hint that a new probe has begun, allowing bulk caches to be invalidated at most once per probe.</summary>
    void BeginProbe();
}
