using System.Globalization;

namespace VramMonitor.Windows.Pdh;

/// <summary>
/// A parsed <c>GPU Process Memory</c> or <c>GPU Adapter Memory</c> counter instance name.
/// </summary>
/// <remarks>
/// Shapes are <c>pid_41172_luid_0x00000000_0x0001AEA2_phys_0</c> and
/// <c>luid_0x00000000_0x0001AEA2_phys_0</c>. The LUID part is what ties an instance to a specific adapter.
/// </remarks>
internal readonly record struct GpuCounterInstance(uint Pid, string Luid, int PhysicalIndex)
{
    public bool IsProcessInstance => Pid != 0;

    /// <summary>
    /// Parses an instance name. Returns false rather than throwing: instance names come from the OS and a
    /// shape we do not recognise should be skipped, never allowed to fail a probe.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> name, out GpuCounterInstance instance)
    {
        instance = default;

        uint pid = 0;
        if (name.StartsWith("pid_", StringComparison.Ordinal))
        {
            ReadOnlySpan<char> rest = name[4..];
            int underscore = rest.IndexOf('_');
            if (underscore <= 0) return false;
            if (!uint.TryParse(rest[..underscore], NumberStyles.None, CultureInfo.InvariantCulture, out pid)) return false;
            name = rest[(underscore + 1)..];
        }

        if (!name.StartsWith("luid_", StringComparison.Ordinal)) return false;

        int physical = name.IndexOf("_phys_", StringComparison.Ordinal);
        if (physical < 0) return false;

        string luid = new(name[..physical]);

        ReadOnlySpan<char> tail = name[(physical + "_phys_".Length)..];
        // Some instance names carry further suffixes, for example "_part_0" on partitioned adapters.
        int nextUnderscore = tail.IndexOf('_');
        ReadOnlySpan<char> physicalText = nextUnderscore < 0 ? tail : tail[..nextUnderscore];

        if (!int.TryParse(physicalText, NumberStyles.None, CultureInfo.InvariantCulture, out int physicalIndex))
        {
            return false;
        }

        instance = new GpuCounterInstance(pid, luid, physicalIndex);
        return true;
    }
}
