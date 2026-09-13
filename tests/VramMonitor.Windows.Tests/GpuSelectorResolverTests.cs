using VramMonitor.Core.Model;
using VramMonitor.Windows.Dxgi;
using Xunit;

namespace VramMonitor.Windows.Tests;

/// <summary>
/// Covers how a remembered GPU is matched to one currently present. Pure logic; no hardware involved.
/// </summary>
public sealed class GpuSelectorResolverTests
{
    private static GpuInfo Adapter(
        string luid, uint vendor, uint device, uint subsys, string description, int ordinal,
        long memory = 24L * 1024 * 1024 * 1024, bool software = false) =>
        new(new GpuId(luid), new GpuSelector(vendor, device, subsys, description, ordinal),
            description, memory, software);

    private static GpuInfo Nvidia(int ordinal = 0, string luid = "luid_0x0_0x1") =>
        Adapter(luid, 0x10DE, 0x2204, 0x87AF1043, "NVIDIA GeForce RTX 3090", ordinal);

    private static GpuInfo Intel(int ordinal = 1) =>
        Adapter("luid_0x0_0x2", 0x8086, 0x7D67, 0, "Intel(R) Graphics", ordinal, 128 * 1024 * 1024);

    private static GpuInfo Software(int ordinal = 2) =>
        Adapter("luid_0x0_0x3", 0x1414, 0x008C, 0, "Microsoft Basic Render Driver", ordinal, 0, software: true);

    [Fact]
    public void An_exact_match_wins()
    {
        GpuInfo nvidia = Nvidia();
        GpuResolution r = GpuSelectorResolver.Resolve([Intel(), nvidia, Software()], nvidia.Selector);

        Assert.Same(nvidia, r.Gpu);
        Assert.Null(r.Problem);
    }

    [Fact]
    public void A_changed_luid_still_matches_the_same_card()
    {
        // The whole reason a selector exists: LUIDs are reallocated on reboot and after a driver reset.
        GpuInfo before = Nvidia(luid: "luid_0x0_0xAAAA");
        GpuInfo after = Nvidia(luid: "luid_0x0_0xBBBB");

        GpuResolution r = GpuSelectorResolver.Resolve([after], before.Selector);

        Assert.Same(after, r.Gpu);
    }

    [Fact]
    public void A_changed_subsystem_id_still_matches_on_vendor_and_device()
    {
        GpuInfo present = Adapter("luid_0x0_0x1", 0x10DE, 0x2204, 0x99999999, "NVIDIA GeForce RTX 3090", 0);
        var remembered = new GpuSelector(0x10DE, 0x2204, 0x87AF1043, "NVIDIA GeForce RTX 3090", 0);

        Assert.Same(present, GpuSelectorResolver.Resolve([present], remembered).Gpu);
    }

    [Fact]
    public void A_remembered_gpu_that_is_absent_is_a_blocking_error_not_a_silent_substitution()
    {
        // Falling back to another adapter would monitor the wrong hardware: an integrated GPU publishes the
        // same counters, so the result would look plausible and be entirely wrong.
        var remembered = new GpuSelector(0x10DE, 0x2204, 0x87AF1043, "NVIDIA GeForce RTX 3090", 0);

        GpuResolution r = GpuSelectorResolver.Resolve([Intel(), Software()], remembered);

        Assert.Null(r.Gpu);
        Assert.NotNull(r.Problem);
        Assert.Contains("RTX 3090", r.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_identical_cards_are_disambiguated_by_ordinal()
    {
        GpuInfo first = Nvidia(ordinal: 0, luid: "luid_0x0_0xA");
        GpuInfo second = Nvidia(ordinal: 1, luid: "luid_0x0_0xB");

        GpuResolution r = GpuSelectorResolver.Resolve([first, second], second.Selector);

        Assert.Same(second, r.Gpu);
    }

    [Fact]
    public void The_first_run_default_is_the_largest_adapter_not_the_first_enumerated()
    {
        // DXGI does not guarantee the discrete card comes first, and on a machine whose display hangs off
        // the integrated GPU, taking adapter zero would monitor the wrong one.
        GpuResolution r = GpuSelectorResolver.Resolve([Intel(ordinal: 0), Nvidia(ordinal: 1)], remembered: null);

        Assert.Equal("NVIDIA GeForce RTX 3090", r.Gpu!.Description);
        Assert.Null(r.Problem);
    }

    [Fact]
    public void Software_adapters_are_never_selected()
    {
        GpuResolution r = GpuSelectorResolver.Resolve([Software()], remembered: null);

        Assert.Null(r.Gpu);
        Assert.NotNull(r.Problem);
    }

    [Fact]
    public void No_adapters_at_all_is_a_blocking_error()
    {
        GpuResolution r = GpuSelectorResolver.Resolve([], remembered: null);

        Assert.Null(r.Gpu);
        Assert.NotNull(r.Problem);
    }

    [Fact]
    public void A_command_line_selector_accepts_an_ordinal_or_a_description_fragment()
    {
        GpuInfo[] adapters = [Intel(ordinal: 0), Nvidia(ordinal: 1)];

        Assert.Equal("NVIDIA GeForce RTX 3090", GpuSelectorResolver.FromCommandLine(adapters, "1")!.Description);
        Assert.Equal("NVIDIA GeForce RTX 3090", GpuSelectorResolver.FromCommandLine(adapters, "3090")!.Description);
        Assert.Equal("Intel(R) Graphics", GpuSelectorResolver.FromCommandLine(adapters, "intel")!.Description);
        Assert.Null(GpuSelectorResolver.FromCommandLine(adapters, "nonexistent"));
        Assert.Null(GpuSelectorResolver.FromCommandLine(adapters, null));
    }
}
