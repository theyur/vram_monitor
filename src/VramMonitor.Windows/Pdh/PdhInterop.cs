using System.Runtime.InteropServices;

namespace VramMonitor.Windows.Pdh;

/// <summary>Performance Data Helper status codes, from <c>pdhmsg.h</c>.</summary>
internal static class PdhStatus
{
    public const uint ValidData = 0x00000000;
    public const uint NewData = 0x00000001;

    public const uint NoMachine = 0x800007D0;

    /// <summary>
    /// The instance no longer exists. For a per-process counter this is a process that exited between the
    /// counter being added and the data being collected -- a normal exit, not an error.
    /// </summary>
    public const uint NoInstance = 0x800007D1;

    public const uint MoreData = 0x800007D2;
    public const uint NoData = 0x800007D5;

    /// <summary>The counter object is absent entirely, e.g. no GPU counters on this machine.</summary>
    public const uint NoObject = 0xC0000BB8;

    public const uint NoCounter = 0xC0000BB9;
    public const uint InvalidHandle = 0xC0000BBC;
    public const uint InvalidData = 0xC0000BC6;

    /// <summary>PDH reports freshly collected data as either of two success codes, not just zero.</summary>
    public static bool IsItemValid(uint status) => status is ValidData or NewData;
}

internal static partial class PdhInterop
{
    private const string Pdh = "pdh.dll";

    public const uint PDH_FMT_LARGE = 0x00000400;

    [LibraryImport(Pdh, EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    /// <summary>
    /// Adds a counter using its <em>English</em> path.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>PdhAddCounterW</c>: performance-counter names are localised, so the non-English
    /// variant would fail on any machine whose display language is not English.
    /// </remarks>
    [LibraryImport(Pdh, EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);

    [LibraryImport(Pdh, EntryPoint = "PdhCollectQueryData")]
    public static partial uint PdhCollectQueryData(nint query);

    [LibraryImport(Pdh, EntryPoint = "PdhCloseQuery")]
    public static partial uint PdhCloseQuery(nint query);

    [LibraryImport(Pdh, EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static unsafe partial uint PdhGetFormattedCounterArray(
        nint counter, uint format, ref uint bufferSize, out uint itemCount, byte* buffer);
}

/// <summary>
/// One entry of <c>PDH_FMT_COUNTERVALUE_ITEM_W</c>: a name pointer, a status, and the value.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PdhFormattedItem
{
    [FieldOffset(0)] public nint NamePointer;
    [FieldOffset(8)] public uint Status;
    [FieldOffset(16)] public long LargeValue;
}
