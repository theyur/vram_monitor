using System.Globalization;
using System.Runtime.InteropServices;
using VramMonitor.Core.Model;

namespace VramMonitor.Windows.Dxgi;

/// <summary>Enumerates GPU adapters and their identities.</summary>
public interface IGpuAdapterEnumerator
{
    IReadOnlyList<GpuInfo> Enumerate();
}

/// <summary>
/// Enumerates adapters through DXGI.
/// </summary>
/// <remarks>
/// DXGI is used rather than WMI because <c>Win32_VideoController.AdapterRAM</c> is a 32-bit field and
/// overflows on any card with more than 4 GB -- an RTX 3090 reports about 4 GB there. DXGI also supplies the
/// adapter LUID, which is what ties an adapter to its performance-counter instances.
/// </remarks>
public sealed unsafe partial class DxgiAdapterEnumerator : IGpuAdapterEnumerator
{
    private const uint DxgiAdapterFlagSoftware = 2;

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(in Guid riid, out nint factory);

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDescription1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Flags;
    }

    public IReadOnlyList<GpuInfo> Enumerate()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");   // IID_IDXGIFactory1
        if (CreateDXGIFactory1(in iid, out nint factory) != 0 || factory == 0) return [];

        var results = new List<GpuInfo>();

        try
        {
            void** factoryVtable = *(void***)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<nint, uint, out nint, int>)factoryVtable[12];

            for (uint index = 0; ; index++)
            {
                if (enumAdapters1(factory, index, out nint adapter) != 0 || adapter == 0) break;

                try
                {
                    void** adapterVtable = *(void***)adapter;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<nint, AdapterDescription1*, int>)adapterVtable[10];

                    AdapterDescription1 description;
                    if (getDesc1(adapter, &description) != 0) continue;

                    string name = new(&description.Description[0]);
                    results.Add(new GpuInfo(
                        new GpuId(FormatLuid(description.AdapterLuidHigh, description.AdapterLuidLow)),
                        new GpuSelector(
                            description.VendorId, description.DeviceId, description.SubSysId, name, (int)index),
                        name,
                        (long)description.DedicatedVideoMemory,
                        (description.Flags & DxgiAdapterFlagSoftware) != 0));
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }

        return results;
    }

    /// <summary>Renders a LUID exactly as the performance-counter instance names spell it.</summary>
    internal static string FormatLuid(int high, uint low) =>
        string.Create(CultureInfo.InvariantCulture, $"luid_0x{high:X8}_0x{low:X8}");

    private static void Release(nint comObject)
    {
        void** vtable = *(void***)comObject;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
        release(comObject);
    }
}
