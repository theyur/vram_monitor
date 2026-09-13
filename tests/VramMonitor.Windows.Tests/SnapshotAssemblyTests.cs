using VramMonitor.Core.Model;
using VramMonitor.Windows.Pdh;
using Xunit;

namespace VramMonitor.Windows.Tests;

/// <summary>
/// Covers how the three counter arrays combine into a snapshot, including every failure combination.
/// No GPU required: the arrays are supplied directly.
/// </summary>
public sealed class SnapshotAssemblyTests
{
    private static readonly GpuId Gpu = new("luid_0x00000000_0x0001AEA2");
    private static readonly DateTimeOffset At = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private const uint Valid = 0x00000000;      // PDH_CSTATUS_VALID_DATA
    private const uint NoInstance = 0x800007D1; // PDH_CSTATUS_NO_INSTANCE
    private const uint Invalid = 0xC0000BC6;    // PDH_INVALID_DATA

    private static CounterItem Process(uint pid, long value, uint status = Valid) =>
        new($"pid_{pid}_luid_0x00000000_0x0001AEA2_phys_0", status, value);

    private static CounterArray Processes(params CounterItem[] items) => CounterArray.Of(items);

    private static CounterArray AdapterPresent(long total = 8_000_000_000) =>
        CounterArray.Of([new CounterItem("luid_0x00000000_0x0001AEA2_phys_0", Valid, total)]);

    private static CounterArray AdapterOther() =>
        CounterArray.Of([new CounterItem("luid_0x00000000_0x0009FFFF_phys_0", Valid, 123)]);

    // ---------------------------------------------------------------- the primary array rules

    [Fact]
    public void An_unreadable_dedicated_array_loses_the_probe()
    {
        GpuSnapshot s = PdhGpuMemoryProvider.Assemble(
            Gpu, At, CounterArray.Unreadable, CounterArray.Unreadable, AdapterPresent());

        Assert.Equal(ProbeOutcome.Failed, s.Outcome);
        Assert.NotNull(s.FailureReason);
    }

    // ---------------------------------------------------------------- the secondary arrays must NOT

    [Fact]
    public void An_unreadable_shared_array_costs_only_the_shared_values()
    {
        // Shared memory is secondary diagnostic data. Failing the whole probe over it would discard the
        // dedicated measurements, which are the entire point.
        GpuSnapshot s = PdhGpuMemoryProvider.Assemble(
            Gpu, At, Processes(Process(10, 500)), CounterArray.Unreadable, AdapterPresent());

        Assert.Equal(ProbeOutcome.Ok, s.Outcome);
        Assert.Equal(500, s.Measurements.Single().DedicatedBytes);
        Assert.Null(s.Measurements.Single().SharedBytes);
        Assert.Equal(8_000_000_000, s.TotalDedicatedBytes);
    }

    [Fact]
    public void An_unreadable_adapter_array_degrades_to_partial_and_keeps_the_measurements()
    {
        GpuSnapshot s = PdhGpuMemoryProvider.Assemble(
            Gpu, At, Processes(Process(10, 500)), CounterArray.Missing, CounterArray.Unreadable);

        Assert.Equal(ProbeOutcome.Partial, s.Outcome);
        Assert.Null(s.TotalDedicatedBytes);
        Assert.Equal(500, s.Measurements.Single().DedicatedBytes);
    }

    [Fact]
    public void An_unreadable_adapter_array_is_not_treated_as_the_gpu_disappearing()
    {
        // This is the trap: "our LUID is not in the array" and "the array could not be read" are different
        // facts. Only the first means the adapter went away. Conflating them turns a transient glitch in
        // secondary data into a failed probe claiming the GPU vanished.
        GpuSnapshot s = PdhGpuMemoryProvider.Assemble(
            Gpu, At, Processes(Process(10, 500)), CounterArray.Missing, CounterArray.Unreadable);

        Assert.NotEqual(ProbeOutcome.Failed, s.Outcome);
        Assert.Null(s.FailureReason);
    }

    [Fact]
    public void An_adapter_array_that_reads_but_lacks_our_luid_does_mean_the_gpu_went_away()
    {
        GpuSnapshot s = PdhGpuMemoryProvider.Assemble(
            Gpu, At, Processes(Process(10, 500)), CounterArray.Missing, AdapterOther());

        Assert.Equal(ProbeOutcome.Failed, s.Outcome);
        Assert.Contains("no longer present", s.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- per-item rules

    [Fact]
    public void A_process_that_exited_between_add_and_collect_is_absent_not_partial()
    {
        // Spec section 13.5: a normal exit must not raise a disruption marker.
        GpuSnapshot s = PdhGpuMemoryProvider.Assemble(
            Gpu, At,
            Processes(Process(10, 500), Process(11, 0, NoInstance)),
            CounterArray.Missing, AdapterPresent());

        // Still Ok: an exited process is not a disruption, and a missing shared array does not degrade
        // the sample either.
        Assert.Equal(ProbeOutcome.Ok, s.Outcome);
        Assert.Single(s.Measurements);                       // the exited process contributes nothing at all
        Assert.Equal(10u, s.Measurements[0].Pid);
    }

    [Fact]
    public void An_unreadable_item_becomes_a_missing_measurement_and_marks_the_sample_partial()
    {
        GpuSnapshot s = PdhGpuMemoryProvider.Assemble(
            Gpu, At,
            Processes(Process(10, 500), Process(11, 999, Invalid)),
            CounterArray.Missing, AdapterPresent());

        Assert.Equal(ProbeOutcome.Partial, s.Outcome);
        RawProcessMeasurement missing = s.Measurements.Single(m => m.Pid == 11);
        Assert.Null(missing.DedicatedBytes);                 // never 0
    }

    [Fact]
    public void Instances_belonging_to_another_adapter_are_ignored()
    {
        // Many processes hold instances on more than one adapter; without this filter an integrated GPU's
        // allocation would be added to the discrete card's figure.
        var items = new[]
        {
            Process(10, 500),
            new CounterItem("pid_10_luid_0x00000000_0x0009FFFF_phys_0", Valid, 4242),
        };

        GpuSnapshot s = PdhGpuMemoryProvider.Assemble(
            Gpu, At, Processes(items), CounterArray.Missing, AdapterPresent());

        Assert.Equal(500, s.Measurements.Single().DedicatedBytes);
    }

    [Fact]
    public void Partitions_of_one_adapter_are_summed_per_process()
    {
        var items = new[]
        {
            new CounterItem("pid_10_luid_0x00000000_0x0001AEA2_phys_0", Valid, 300),
            new CounterItem("pid_10_luid_0x00000000_0x0001AEA2_phys_1", Valid, 200),
        };

        GpuSnapshot s = PdhGpuMemoryProvider.Assemble(
            Gpu, At, Processes(items), CounterArray.Missing, AdapterPresent());

        Assert.Equal(500, s.Measurements.Single().DedicatedBytes);
    }

    [Fact]
    public void An_unreadable_partition_keeps_the_whole_process_unknown()
    {
        // Summing only the readable partition would silently report the other one as zero.
        var items = new[]
        {
            new CounterItem("pid_10_luid_0x00000000_0x0001AEA2_phys_0", Valid, 300),
            new CounterItem("pid_10_luid_0x00000000_0x0001AEA2_phys_1", Invalid, 0),
        };

        GpuSnapshot s = PdhGpuMemoryProvider.Assemble(
            Gpu, At, Processes(items), CounterArray.Missing, AdapterPresent());

        Assert.Null(s.Measurements.Single().DedicatedBytes);
    }

    [Fact]
    public void An_empty_process_list_on_a_live_adapter_is_a_failure_not_a_mass_exit()
    {
        GpuSnapshot s = PdhGpuMemoryProvider.Assemble(
            Gpu, At, Processes(), CounterArray.Missing, AdapterPresent());

        Assert.Equal(ProbeOutcome.Failed, s.Outcome);
    }

    [Fact]
    public void An_empty_process_list_is_a_failure_even_when_the_adapter_array_is_unreadable()
    {
        // Otherwise this publishes as Partial with no observations, which renders as every application
        // exiting at once. An unreadable adapter array is no licence to publish an empty picture.
        GpuSnapshot s = PdhGpuMemoryProvider.Assemble(
            Gpu, At, Processes(), CounterArray.Missing, CounterArray.Unreadable);

        Assert.Equal(ProbeOutcome.Failed, s.Outcome);
    }

    [Fact]
    public void An_unreadable_adapter_item_makes_the_total_unknown_rather_than_zero()
    {
        // If the only instance for our adapter is unreadable, summing the valid ones yields 0 -- a total
        // that was never measured, on the series the chart plots as context.
        CounterArray adapter = CounterArray.Of(
            [new CounterItem("luid_0x00000000_0x0001AEA2_phys_0", Invalid, 0)]);

        GpuSnapshot s = PdhGpuMemoryProvider.Assemble(
            Gpu, At, Processes(Process(10, 500)), CounterArray.Missing, adapter);

        Assert.Null(s.TotalDedicatedBytes);
        Assert.Equal(ProbeOutcome.Partial, s.Outcome);
        Assert.NotEqual(ProbeOutcome.Failed, s.Outcome);      // the adapter is present, just unreadable
    }

    [Fact]
    public void One_unreadable_adapter_partition_does_not_understate_the_total()
    {
        CounterArray adapter = CounterArray.Of(
        [
            new CounterItem("luid_0x00000000_0x0001AEA2_phys_0", Valid, 4_000_000_000),
            new CounterItem("luid_0x00000000_0x0001AEA2_phys_1", Invalid, 0),
        ]);

        GpuSnapshot s = PdhGpuMemoryProvider.Assemble(
            Gpu, At, Processes(Process(10, 500)), CounterArray.Missing, adapter);

        Assert.Null(s.TotalDedicatedBytes);
        Assert.Equal(ProbeOutcome.Partial, s.Outcome);
    }

    [Fact]
    public void An_unreadable_adapter_item_still_proves_the_adapter_is_present()
    {
        // Presence comes from the instance existing, not from its value being readable -- otherwise this
        // would be misreported as the GPU having disappeared.
        CounterArray adapter = CounterArray.Of(
            [new CounterItem("luid_0x00000000_0x0001AEA2_phys_0", Invalid, 0)]);

        GpuSnapshot s = PdhGpuMemoryProvider.Assemble(
            Gpu, At, Processes(Process(10, 500)), CounterArray.Missing, adapter);

        Assert.DoesNotContain("no longer present", s.FailureReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Shared_values_are_matched_to_their_process()
    {
        CounterArray shared = CounterArray.Of(
            [new CounterItem("pid_10_luid_0x00000000_0x0001AEA2_phys_0", Valid, 64)]);

        GpuSnapshot s = PdhGpuMemoryProvider.Assemble(
            Gpu, At, Processes(Process(10, 500)), shared, AdapterPresent());

        Assert.Equal(64, s.Measurements.Single().SharedBytes);
    }
}
