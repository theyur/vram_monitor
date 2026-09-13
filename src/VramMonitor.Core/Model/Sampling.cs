namespace VramMonitor.Core.Model;

/// <summary>Outcome of a single polling cycle. See spec sections 13.2 to 13.4.</summary>
public enum ProbeOutcome
{
    /// <summary>Every requested measurement succeeded.</summary>
    Ok,

    /// <summary>The probe ran but at least one process measurement was unreadable.</summary>
    Partial,

    /// <summary>The probe failed entirely; no measurements are available for this timestamp.</summary>
    Failed,
}

/// <summary>
/// One process's measurement within a sample.
/// </summary>
/// <remarks>
/// A null value means <em>missing</em>: the process had a counter instance but its value could not be
/// read, so the series must show a gap. A process that has no counter instance at all is <em>absent</em>
/// and produces no <see cref="ProcessObservation"/> whatsoever -- its line simply ends, with no
/// disruption marker (spec section 13.5). Neither case may ever be recorded as zero (spec section 5.2).
/// </remarks>
public sealed record ProcessObservation(
    ProcessSessionId SessionId,
    long? DedicatedBytes,
    long? SharedBytes)
{
    /// <summary>True when the process was present but its dedicated value could not be read.</summary>
    public bool IsMissing => DedicatedBytes is null;
}

/// <summary>A single timestamped observation of the selected GPU, as stored in the rolling history.</summary>
/// <param name="NominalInterval">
/// The configured sample interval in force when this sample was taken. Stored per sample so that
/// aggressive-duration weighting stays correct when the interval is changed at runtime.
/// </param>
public sealed record GpuSample(
    DateTimeOffset TimestampUtc,
    TimeSpan NominalInterval,
    ProbeOutcome Outcome,
    long? TotalDedicatedBytes,
    IReadOnlyList<ProcessObservation> Observations,
    string? FailureReason = null);

/// <summary>A raw per-process measurement as returned by a provider, before session resolution.</summary>
public readonly record struct RawProcessMeasurement(
    uint Pid,
    long? DedicatedBytes,
    long? SharedBytes);

/// <summary>What a provider returns for one probe, keyed by PID rather than by session.</summary>
public sealed record GpuSnapshot(
    GpuId Gpu,
    DateTimeOffset TimestampUtc,
    ProbeOutcome Outcome,
    long? TotalDedicatedBytes,
    IReadOnlyList<RawProcessMeasurement> Measurements,
    string? FailureReason = null)
{
    public static GpuSnapshot Failed(GpuId gpu, DateTimeOffset timestampUtc, string reason) =>
        new(gpu, timestampUtc, ProbeOutcome.Failed, null, [], reason);
}
