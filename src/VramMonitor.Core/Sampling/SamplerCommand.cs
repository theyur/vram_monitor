using VramMonitor.Core.Configuration;
using VramMonitor.Core.Model;

namespace VramMonitor.Core.Sampling;

/// <summary>
/// A change requested from outside the sampling loop, applied on the loop's own thread.
/// </summary>
/// <remarks>
/// Settings and GPU changes both arrive this way. Applying either directly from the UI thread would mutate
/// the rolling store and the session tracker while the loop is appending to and enumerating them.
/// </remarks>
/// <param name="Settings">New settings, or null when this command does not change them.</param>
/// <param name="Gpu">The GPU to monitor. Meaningful only when <paramref name="HasGpu"/> is true.</param>
/// <param name="BlockingError">A startup-level problem to surface, or null to clear it.</param>
/// <param name="HasGpu">
/// Distinguishes "do not touch the GPU" from "set the GPU to null", which are different instructions.
/// </param>
internal readonly record struct SamplerCommand(
    MonitorSettings? Settings,
    GpuInfo? Gpu,
    string? BlockingError,
    bool HasGpu);
