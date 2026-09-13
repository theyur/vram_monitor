using System.Runtime.InteropServices;
using VramMonitor.Core.Abstractions;
using VramMonitor.Core.Model;
using VramMonitor.Windows.Dxgi;

namespace VramMonitor.Windows.Pdh;

/// <summary>
/// Reads per-process GPU memory from the Windows performance counters that Task Manager itself uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not NVML.</b> On a GeForce card under the WDDM driver model -- which a consumer card cannot leave --
/// NVML reports per-process GPU memory as "not supported"; <c>nvidia-smi</c> prints N/A for every process.
/// The performance counters are the only per-process source available, and they need no elevation.
/// </para>
/// <para>
/// <b>Why the query is rebuilt every probe.</b> A PDH wildcard counter expands its instance list when the
/// counter is added, so a long-lived query would never see a process that started afterwards. Rebuilding
/// costs about 0.08 ms and allocates nothing measurable, so it is simply done every time. Note that
/// <c>PdhEnumObjects</c> with a refresh flag is <em>not</em> the fix: it costs around 200 ms.
/// </para>
/// </remarks>
public sealed class PdhGpuMemoryProvider(IGpuAdapterEnumerator adapters) : IGpuMemoryProvider
{
    private const string ProcessDedicatedPath = @"\GPU Process Memory(*)\Dedicated Usage";
    private const string ProcessSharedPath = @"\GPU Process Memory(*)\Shared Usage";
    private const string AdapterDedicatedPath = @"\GPU Adapter Memory(*)\Dedicated Usage";

    private readonly IGpuAdapterEnumerator _adapters = adapters;

    private byte[] _buffer = new byte[128 * 1024];

    public Task<IReadOnlyList<GpuInfo>> GetGpusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_adapters.Enumerate());

    /// <summary>Checks that the counter objects exist at all, for a clear startup error (spec section 16.3).</summary>
    public string? CheckAvailability()
    {
        nint query = 0;
        try
        {
            if (PdhInterop.PdhOpenQuery(null, 0, out query) != 0) return "Could not open a performance-counter query.";

            uint status = PdhInterop.PdhAddEnglishCounter(query, ProcessDedicatedPath, 0, out _);
            return status switch
            {
                0 => null,
                PdhStatus.NoObject =>
                    "This system does not publish the 'GPU Process Memory' performance counters, " +
                    "which VRAM Monitor needs to measure per-process GPU memory.",
                _ => $"The 'GPU Process Memory' counters could not be opened (PDH status 0x{status:X8}).",
            };
        }
        finally
        {
            if (query != 0) PdhInterop.PdhCloseQuery(query);
        }
    }

    public Task<GpuSnapshot> GetSnapshotAsync(GpuId gpuId, CancellationToken cancellationToken) =>
        Task.FromResult(Probe(gpuId));

    private GpuSnapshot Probe(GpuId gpuId)
    {
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        nint query = 0;

        try
        {
            uint open = PdhInterop.PdhOpenQuery(null, 0, out query);
            if (open != 0) return GpuSnapshot.Failed(gpuId, timestamp, $"PdhOpenQuery failed (0x{open:X8}).");

            if (PdhInterop.PdhAddEnglishCounter(query, ProcessDedicatedPath, 0, out nint dedicated) is var d && d != 0)
            {
                return GpuSnapshot.Failed(gpuId, timestamp, $"Dedicated Usage counter unavailable (0x{d:X8}).");
            }

            bool haveShared = PdhInterop.PdhAddEnglishCounter(query, ProcessSharedPath, 0, out nint shared) == 0;
            bool haveAdapter = PdhInterop.PdhAddEnglishCounter(query, AdapterDedicatedPath, 0, out nint adapter) == 0;

            uint collect = PdhInterop.PdhCollectQueryData(query);
            if (collect != 0) return GpuSnapshot.Failed(gpuId, timestamp, $"PdhCollectQueryData failed (0x{collect:X8}).");

            return Assemble(
                gpuId,
                timestamp,
                ReadArray(dedicated, available: true),
                ReadArray(shared, haveShared),
                ReadArray(adapter, haveAdapter));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return GpuSnapshot.Failed(gpuId, timestamp, ex.Message);
        }
        finally
        {
            if (query != 0) PdhInterop.PdhCloseQuery(query);
        }
    }

    /// <summary>
    /// Turns three counter arrays into a snapshot. Pure, so every failure combination can be tested without
    /// a GPU.
    /// </summary>
    /// <remarks>
    /// The three arrays are deliberately <b>not</b> treated alike. Only the dedicated array can lose the
    /// probe; shared memory is secondary diagnostic data and the adapter total is context, so discarding good
    /// per-process measurements because one of those failed would throw away the very thing being asked for.
    /// </remarks>
    internal static GpuSnapshot Assemble(
        GpuId gpuId,
        DateTimeOffset timestamp,
        CounterArray dedicated,
        CounterArray shared,
        CounterArray adapter)
    {
        if (!dedicated.Readable)
        {
            return GpuSnapshot.Failed(gpuId, timestamp, "The Dedicated Usage counter array could not be read.");
        }

        var outcome = ProbeOutcome.Ok;

        Dictionary<string, long>? sharedByInstance = null;
        if (shared.Readable)
        {
            sharedByInstance = new Dictionary<string, long>(shared.Items.Count, StringComparer.Ordinal);
            foreach (CounterItem item in shared.Items)
            {
                if (PdhStatus.IsItemValid(item.Status)) sharedByInstance[item.Name] = item.Value;
            }
        }

        long? total = null;
        bool adapterInstanceSeen = false;

        if (adapter.Readable)
        {
            long sum = 0;
            bool anyPartUnreadable = false;

            foreach (CounterItem item in adapter.Items)
            {
                if (!GpuCounterInstance.TryParse(item.Name, out GpuCounterInstance parsed)) continue;
                if (!string.Equals(parsed.Luid, gpuId.Luid, StringComparison.OrdinalIgnoreCase)) continue;

                // Presence is established by the instance existing, whatever its status: an unreadable
                // instance still proves the adapter is there.
                adapterInstanceSeen = true;

                if (PdhStatus.IsItemValid(item.Status)) sum += item.Value;
                else anyPartUnreadable = true;
            }

            if (adapterInstanceSeen && !anyPartUnreadable)
            {
                total = sum;
            }
            else if (anyPartUnreadable)
            {
                // Reporting the readable partitions alone would publish a total that is silently too low --
                // or zero, if the only instance was unreadable. Unknown is unknown.
                outcome = ProbeOutcome.Partial;
            }
        }
        else if (adapter.Available)
        {
            // The counter exists but this read of it failed: a transient disruption, worth marking. Losing
            // the context series degrades the sample; it does not invalidate it.
            //
            // A counter that was never available at all is a standing condition rather than a per-sample
            // disruption, so it deliberately does NOT mark every sample Partial -- that would fill the chart
            // with markers telling the user nothing new.
            //
            // Either way the adapter-presence check below is skipped: not having read the array is no
            // evidence about whether the GPU is still there, and treating it as evidence would turn a glitch
            // in secondary data into "your GPU disappeared".
            outcome = ProbeOutcome.Partial;
        }

        if (adapter.Readable && !adapterInstanceSeen)
        {
            // The adapter really is gone: the array was read and our LUID was not in it. A driver reset or
            // TDR reallocated it. Reporting Ok would show every application exiting normally at once.
            return GpuSnapshot.Failed(
                gpuId, timestamp, "The monitored GPU is no longer present; its adapter was re-created.");
        }

        var measurements = new List<RawProcessMeasurement>(dedicated.Items.Count);
        foreach (CounterItem item in dedicated.Items)
        {
            if (!GpuCounterInstance.TryParse(item.Name, out GpuCounterInstance parsed)) continue;
            if (!parsed.IsProcessInstance) continue;
            if (!string.Equals(parsed.Luid, gpuId.Luid, StringComparison.OrdinalIgnoreCase)) continue;

            // A process that exited between adding the counter and collecting is a normal exit, not a
            // partial probe: it must not raise a disruption marker.
            if (item.Status == PdhStatus.NoInstance) continue;

            if (!PdhStatus.IsItemValid(item.Status))
            {
                measurements.Add(new RawProcessMeasurement(parsed.Pid, null, null));
                outcome = ProbeOutcome.Partial;
                continue;
            }

            long? sharedValue = null;
            if (sharedByInstance is not null && sharedByInstance.TryGetValue(item.Name, out long s)) sharedValue = s;

            measurements.Add(new RawProcessMeasurement(parsed.Pid, item.Value, sharedValue));
        }

        if (measurements.Count == 0)
        {
            // At least one system process holds an instance on every live adapter -- pid 4 does so on all
            // four adapters of the development machine -- so an empty list means the read went wrong, not
            // that nothing is using the GPU. Publishing it would render as every application exiting at
            // once. This holds whether or not the adapter array could be read, so it is deliberately not
            // conditioned on that: an unreadable adapter array is no licence to publish an empty picture.
            return GpuSnapshot.Failed(
                gpuId, timestamp, "No process instances were returned for the monitored GPU.");
        }

        return new GpuSnapshot(gpuId, timestamp, outcome, total, MergeByPid(measurements));
    }

    /// <summary>
    /// Folds together the physical partitions of one adapter, so a partitioned or linked-display-adapter
    /// GPU reports one figure per process rather than one per partition.
    /// </summary>
    private static List<RawProcessMeasurement> MergeByPid(List<RawProcessMeasurement> measurements)
    {
        var byPid = new Dictionary<uint, RawProcessMeasurement>(measurements.Count);

        foreach (RawProcessMeasurement m in measurements)
        {
            if (!byPid.TryGetValue(m.Pid, out RawProcessMeasurement existing))
            {
                byPid[m.Pid] = m;
                continue;
            }

            // A missing part keeps the whole process missing: summing the readable partitions only would
            // quietly treat the unreadable one as zero.
            long? dedicated = existing.DedicatedBytes is { } a && m.DedicatedBytes is { } b ? a + b : null;
            long? shared = existing.SharedBytes is { } c && m.SharedBytes is { } d ? c + d : existing.SharedBytes ?? m.SharedBytes;
            byPid[m.Pid] = new RawProcessMeasurement(m.Pid, dedicated, shared);
        }

        return [.. byPid.Values];
    }

    /// <summary>Reads one counter array, distinguishing "never added" from "read failed" from "read OK".</summary>
    private CounterArray ReadArray(nint counter, bool available)
    {
        if (!available) return CounterArray.Missing;
        return TryReadArray(counter, out List<CounterItem> items, out _) ? CounterArray.Of(items) : CounterArray.Unreadable;
    }

    private unsafe bool TryReadArray(nint counter, out List<CounterItem> items, out uint error)
    {
        items = [];
        error = 0;

        while (true)
        {
            uint size = (uint)_buffer.Length;
            uint status;

            fixed (byte* pointer = _buffer)
            {
                status = PdhInterop.PdhGetFormattedCounterArray(
                    counter, PdhInterop.PDH_FMT_LARGE, ref size, out uint count, pointer);

                if (status == PdhStatus.MoreData)
                {
                    _buffer = new byte[Math.Max(size, (uint)_buffer.Length * 2)];
                    continue;
                }

                if (status != 0) { error = status; return false; }

                var result = new List<CounterItem>((int)count);
                var entries = (PdhFormattedItem*)pointer;
                for (uint i = 0; i < count; i++)
                {
                    string name = Marshal.PtrToStringUni(entries[i].NamePointer) ?? string.Empty;
                    result.Add(new CounterItem(name, entries[i].Status, entries[i].LargeValue));
                }

                items = result;
                return true;
            }
        }
    }
}
