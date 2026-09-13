using VramMonitor.Core.Model;

namespace VramMonitor.Core.Abstractions;

/// <summary>
/// Retrieves per-process GPU memory measurements, hiding all provider-specific detail.
/// Nothing outside the provider implementation may depend on Windows or NVIDIA structures
/// (spec section 3.2).
/// </summary>
public interface IGpuMemoryProvider
{
    /// <summary>Enumerates the GPU adapters this provider can measure.</summary>
    Task<IReadOnlyList<GpuInfo>> GetGpusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Takes one timestamped measurement of the given GPU. Implementations must not throw for
    /// routine failures: a failed probe is reported as <see cref="ProbeOutcome.Failed"/>.
    /// </summary>
    Task<GpuSnapshot> GetSnapshotAsync(GpuId gpuId, CancellationToken cancellationToken);
}
