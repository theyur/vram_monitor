namespace VramMonitor.Core.Model;

/// <summary>
/// Runtime-only identity of a GPU adapter, as it appears in performance-counter instance names
/// ("luid_0x00000000_0x0001AEA2").
/// </summary>
/// <remarks>
/// A LUID is unique only until the machine restarts, and is reallocated on driver update or TDR
/// recovery. It must therefore NEVER be persisted -- use <see cref="GpuSelector"/> for that.
/// </remarks>
public readonly record struct GpuId(string Luid)
{
    public override string ToString() => Luid;
}

/// <summary>
/// Stable, persistable identity of a GPU adapter. Survives reboots and driver resets, unlike a LUID.
/// </summary>
public sealed record GpuSelector(
    uint VendorId,
    uint DeviceId,
    uint SubSysId,
    string Description,
    int Ordinal);

/// <summary>A GPU adapter discovered at runtime, carrying both its runtime and stable identities.</summary>
public sealed record GpuInfo(
    GpuId Id,
    GpuSelector Selector,
    string Description,
    long DedicatedVideoMemoryBytes,
    bool IsSoftwareAdapter);
