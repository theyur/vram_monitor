using Microsoft.Win32;
using VramMonitor.Core.Model;
using VramMonitor.Windows.Dxgi;
using VramMonitor.Windows.Pdh;
using VramMonitor.Windows.Processes;
using VramMonitor.Windows.Startup;
using Xunit;

namespace VramMonitor.Windows.Tests;

/// <summary>
/// Integration tests against the real machine.
/// </summary>
/// <remarks>
/// Anything needing a GPU is skipped rather than failed when no adapter publishes the counters, so the suite
/// stays green on a machine without one.
/// </remarks>
public sealed class GpuProviderIntegrationTests
{
    private static readonly DxgiAdapterEnumerator Adapters = new();

    private static GpuInfo? PrimaryGpu() =>
        GpuSelectorResolver.Resolve(Adapters.Enumerate(), remembered: null).Gpu;

    [Fact]
    public void Dxgi_enumerates_at_least_one_adapter_with_a_plausible_memory_size()
    {
        IReadOnlyList<GpuInfo> adapters = Adapters.Enumerate();
        Assert.SkipWhen(adapters.Count == 0, "No DXGI adapters on this machine.");

        GpuInfo? discrete = adapters.FirstOrDefault(a => !a.IsSoftwareAdapter && a.DedicatedVideoMemoryBytes > 0);
        Assert.SkipWhen(discrete is null, "No adapter with dedicated video memory.");

        // WMI's AdapterRAM is a 32-bit field and overflows above 4 GB; DXGI must not.
        Assert.True(discrete.DedicatedVideoMemoryBytes > 0);
        Assert.StartsWith("luid_0x", discrete.Id.Luid, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(discrete.Description));
    }

    [Fact]
    public void The_counter_objects_this_application_needs_are_present()
    {
        string? problem = PdhGpuMemoryProvider.CheckAvailability();
        Assert.SkipWhen(problem is not null, problem ?? string.Empty);
        Assert.Null(problem);
    }

    [Fact]
    public async Task A_real_probe_returns_plausible_per_process_measurements()
    {
        GpuInfo? gpu = PrimaryGpu();
        Assert.SkipWhen(gpu is null, "No usable GPU.");

        var provider = new PdhGpuMemoryProvider(Adapters);
        GpuSnapshot snapshot = await provider.GetSnapshotAsync(gpu.Id, TestContext.Current.CancellationToken);

        Assert.SkipWhen(snapshot.Outcome == ProbeOutcome.Failed, $"Probe failed: {snapshot.FailureReason}");

        Assert.NotEmpty(snapshot.Measurements);
        Assert.All(snapshot.Measurements, m => Assert.True(m.DedicatedBytes is null or >= 0));

        // The adapter total should be in the same ballpark as the processes we can see, and below capacity.
        Assert.NotNull(snapshot.TotalDedicatedBytes);
        Assert.InRange(snapshot.TotalDedicatedBytes!.Value, 0, gpu.DedicatedVideoMemoryBytes * 2);
    }

    [Fact]
    public async Task Repeated_probes_stay_cheap()
    {
        GpuInfo? gpu = PrimaryGpu();
        Assert.SkipWhen(gpu is null, "No usable GPU.");

        var provider = new PdhGpuMemoryProvider(Adapters);
        await provider.GetSnapshotAsync(gpu.Id, TestContext.Current.CancellationToken);   // warm up

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            await provider.GetSnapshotAsync(gpu.Id, TestContext.Current.CancellationToken);
        }

        stopwatch.Stop();
        double perProbeMs = stopwatch.Elapsed.TotalMilliseconds / 20;

        // Measured at about 0.08 ms on the development machine; 25 ms is a generous ceiling that still
        // catches an accidental switch to an expensive API.
        Assert.True(perProbeMs < 25, $"Probe averaged {perProbeMs:F2} ms, which is far above expectations.");
    }

    [Fact]
    public async Task A_stale_luid_fails_the_probe_rather_than_reporting_an_empty_success()
    {
        // Reporting Ok with no processes would render as every application exiting at once, with no marker.
        var provider = new PdhGpuMemoryProvider(Adapters);
        GpuSnapshot snapshot = await provider.GetSnapshotAsync(
            new GpuId("luid_0xDEADBEEF_0xDEADBEEF"), TestContext.Current.CancellationToken);

        Assert.Equal(ProbeOutcome.Failed, snapshot.Outcome);
        Assert.NotNull(snapshot.FailureReason);
    }

    [Fact]
    public void Live_counter_instance_names_all_parse()
    {
        GpuInfo? gpu = PrimaryGpu();
        Assert.SkipWhen(gpu is null, "No usable GPU.");

        // The parser is checked directly against the shapes Windows actually produces on this machine.
        Assert.True(GpuCounterInstance.TryParse(
            "pid_41172_luid_0x00000000_0x0001AEA2_phys_0", out GpuCounterInstance process));
        Assert.Equal(41172u, process.Pid);
        Assert.Equal("luid_0x00000000_0x0001AEA2", process.Luid);
        Assert.Equal(0, process.PhysicalIndex);

        Assert.True(GpuCounterInstance.TryParse(
            "luid_0x00000000_0x0001AEA2_phys_0", out GpuCounterInstance adapter));
        Assert.False(adapter.IsProcessInstance);

        // Partitioned adapters append further segments; the physical index must still parse.
        Assert.True(GpuCounterInstance.TryParse(
            "luid_0x00000000_0x0001C270_phys_0_part_0", out GpuCounterInstance partitioned));
        Assert.Equal("luid_0x00000000_0x0001C270", partitioned.Luid);

        Assert.False(GpuCounterInstance.TryParse("not an instance name", out _));
    }
}

public sealed class ProcessMetadataIntegrationTests
{
    [Fact]
    public void The_current_process_resolves_to_a_full_path_and_a_creation_time()
    {
        var resolver = new WindowsProcessMetadataResolver();
        uint pid = (uint)Environment.ProcessId;

        string? path = resolver.ResolvePath(pid);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));

        ProcessTimesAssert(resolver, pid);
    }

    private static void ProcessTimesAssert(WindowsProcessMetadataResolver resolver, uint pid)
    {
        Core.Abstractions.ProcessTimes? times = resolver.GetTimes(pid);
        Assert.NotNull(times);
        Assert.NotEqual(0, times.Value.CreationTicks);
    }

    [Fact]
    public void A_protected_process_denies_its_path_without_throwing()
    {
        // PID 4 is the System process; a normal user cannot open it. The resolver must degrade, not fail.
        var resolver = new WindowsProcessMetadataResolver();

        Assert.Null(resolver.ResolvePath(4));
        Assert.Equal(0, resolver.GetTimes(4)!.Value.CreationTicks);

        resolver.BeginProbe();
        Assert.Equal("System.exe", resolver.ResolveNameOnly(4));
    }

    [Fact]
    public void A_missing_executable_does_not_throw_when_a_display_name_is_requested()
    {
        var resolver = new WindowsProcessMetadataResolver();
        Assert.Null(resolver.ResolveDisplayName(@"C:\does\not\exist\nothing.exe"));
    }
}

public sealed class AutostartIntegrationTests
{
    [Fact]
    public void The_autostart_entry_can_be_written_and_removed_without_elevation()
    {
        // A uniquely named value, removed again immediately, so the user's real startup list is untouched.
        var manager = new AutostartManager($"VramMonitorTest_{Guid.NewGuid():N}");
        try
        {
            Assert.False(manager.IsEnabled());

            Assert.Null(manager.Set(true, @"C:\apps\VramMonitor.exe"));
            Assert.True(manager.IsEnabled());
        }
        finally
        {
            manager.Set(false, @"C:\apps\VramMonitor.exe");
        }

        Assert.False(manager.IsEnabled());
    }

    [Fact]
    public void An_entry_removed_from_outside_the_application_is_reported_as_absent()
    {
        // What the settings checkbox depends on: Task Manager's Startup apps tab deletes the value without
        // telling us, so the state has to be read back from the key rather than remembered.
        string valueName = $"VramMonitorTest_{Guid.NewGuid():N}";
        var manager = new AutostartManager(valueName);

        try
        {
            Assert.Null(manager.Set(true, @"C:\apps\VramMonitor.exe"));
            Assert.True(manager.IsEnabled());

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            {
                key.DeleteValue(valueName, throwOnMissingValue: true);
            }

            Assert.False(manager.IsEnabled());
        }
        finally
        {
            manager.Set(false, @"C:\apps\VramMonitor.exe");
        }
    }
}
