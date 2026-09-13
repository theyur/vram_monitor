using VramMonitor.Core.Model;

namespace VramMonitor.Windows.Dxgi;

/// <summary>Outcome of resolving a remembered GPU to one currently present.</summary>
/// <param name="Gpu">The adapter to monitor, or null when a remembered selection matched nothing.</param>
/// <param name="Problem">A message to surface once, per spec section 16.3.</param>
public sealed record GpuResolution(GpuInfo? Gpu, string? Problem);

/// <summary>
/// Turns a remembered <see cref="GpuSelector"/> into the adapter that currently holds that identity.
/// </summary>
/// <remarks>
/// This indirection exists because an adapter's LUID is not stable: it is reallocated on every boot, and
/// again whenever the adapter is re-created by a driver update or TDR recovery. Persisting a LUID would mean
/// a saved selection silently matching nothing after a reboot.
/// </remarks>
public static class GpuSelectorResolver
{
    public static GpuResolution Resolve(IReadOnlyList<GpuInfo> adapters, GpuSelector? remembered)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        List<GpuInfo> usable = [.. adapters.Where(a => !a.IsSoftwareAdapter)];
        if (usable.Count == 0)
        {
            return new GpuResolution(null, "No compatible GPU was found on this system.");
        }

        if (remembered is null) return new GpuResolution(PickDefault(usable), null);

        // Three tiers, most specific first. Several adapters can match a tier when two identical cards are
        // installed, so the remembered ordinal breaks the tie.
        GpuInfo? match =
            Best(usable, remembered, static (a, s) =>
                a.Selector.VendorId == s.VendorId && a.Selector.DeviceId == s.DeviceId
                && a.Selector.SubSysId == s.SubSysId && Same(a.Description, s.Description))
            ?? Best(usable, remembered, static (a, s) =>
                a.Selector.VendorId == s.VendorId && a.Selector.DeviceId == s.DeviceId)
            ?? Best(usable, remembered, static (a, s) => Same(a.Description, s.Description));

        if (match is not null) return new GpuResolution(match, null);

        // Deliberately no fall back to "some other adapter". Quietly monitoring a different GPU would show a
        // plausible but entirely wrong picture -- an integrated GPU publishes the same counters as a
        // discrete one.
        return new GpuResolution(
            null,
            $"The GPU this profile monitors ('{remembered.Description}') is not present. " +
            "Choose a GPU in Settings to continue.");
    }

    /// <summary>
    /// The adapter chosen on a first run, when nothing has been remembered yet.
    /// </summary>
    /// <remarks>
    /// Largest dedicated video memory rather than first enumerated: DXGI does not guarantee the discrete
    /// card comes first, and on a machine whose display is driven by the integrated GPU, taking the first
    /// adapter would monitor the wrong one.
    /// </remarks>
    private static GpuInfo PickDefault(List<GpuInfo> usable) =>
        usable.OrderByDescending(a => a.DedicatedVideoMemoryBytes)
              .ThenBy(a => a.Selector.Ordinal)
              .First();

    private static GpuInfo? Best(
        List<GpuInfo> adapters, GpuSelector selector, Func<GpuInfo, GpuSelector, bool> predicate)
    {
        List<GpuInfo> matches = [.. adapters.Where(a => predicate(a, selector))];
        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => matches.FirstOrDefault(a => a.Selector.Ordinal == selector.Ordinal) ?? matches[0],
        };
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves a <c>--gpu</c> argument: an adapter ordinal, or part of its description.</summary>
    /// <remarks>
    /// An ordinal match is tried first but is not allowed to be the final word, because the most natural
    /// thing to type is a model number: <c>--gpu 3090</c> parses perfectly well as an integer and would
    /// otherwise look for adapter index 3090 and silently find nothing.
    /// </remarks>
    public static GpuInfo? FromCommandLine(IReadOnlyList<GpuInfo> adapters, string? argument)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        if (string.IsNullOrWhiteSpace(argument)) return null;

        if (int.TryParse(argument, out int ordinal))
        {
            GpuInfo? byOrdinal = adapters.FirstOrDefault(a => a.Selector.Ordinal == ordinal);
            if (byOrdinal is not null) return byOrdinal;
        }

        return adapters.FirstOrDefault(
            a => a.Description.Contains(argument, StringComparison.OrdinalIgnoreCase));
    }
}
