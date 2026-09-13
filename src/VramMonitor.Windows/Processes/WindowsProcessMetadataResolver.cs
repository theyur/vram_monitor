using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VramMonitor.Core.Abstractions;

namespace VramMonitor.Windows.Processes;

/// <summary>
/// Resolves process metadata for observed PIDs.
/// </summary>
/// <remarks>
/// <para>
/// Metadata is looked up separately from VRAM measurement so that a failure here never discards a valid
/// measurement (spec section 5.3). Protected processes deny a handle outright -- on the development machine
/// <c>System</c>, <c>csrss</c> and <c>dwm</c> all do -- so the fallbacks matter in practice.
/// </para>
/// <para>
/// The tier-2 bulk listing is only taken when something actually needs it, and at most once per probe. It
/// costs about 23 ms for 500 processes, roughly 300 times a whole probe, so running it unconditionally would
/// dominate the sampling budget.
/// </para>
/// </remarks>
public sealed partial class WindowsProcessMetadataResolver : IProcessMetadataResolver
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private readonly Dictionary<string, string?> _displayNames = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<uint, string>? _bulkNames;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    // Hand-written rather than source-generated: the generator refuses to marshal a char[] buffer without
    // disabling runtime marshalling assembly-wide, which is not worth doing for one call.
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        nint process, uint flags, char[] buffer, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(
        nint process, out long creation, out long exit, out long kernel, out long user);

    public void BeginProbe() => _bulkNames = null;

    public ProcessTimes? GetTimes(uint pid)
    {
        nint handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == 0) return new ProcessTimes(0);   // denied: unknown, not absent

        try
        {
            return GetProcessTimes(handle, out long creation, out _, out _, out _)
                ? new ProcessTimes(creation)
                : new ProcessTimes(0);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public string? ResolvePath(uint pid)
    {
        nint handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == 0) return null;

        try
        {
            var buffer = new char[1024];
            uint size = (uint)buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public string? ResolveNameOnly(uint pid)
    {
        if (_bulkNames is null)
        {
            _bulkNames = [];
            foreach (Process process in Process.GetProcesses())
            {
                try { _bulkNames[(uint)process.Id] = process.ProcessName + ".exe"; }
                catch (InvalidOperationException) { /* exited while being enumerated */ }
                finally { process.Dispose(); }
            }
        }

        return _bulkNames.GetValueOrDefault(pid);
    }

    /// <summary>
    /// Reads the executable's version resource for a friendly name, cached per path.
    /// </summary>
    /// <remarks>
    /// This is disk I/O, which is exactly why it lives in the resolver rather than in the analysis layer:
    /// the analyzer stays a pure function and its tests need no file system.
    /// </remarks>
    public string? ResolveDisplayName(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        if (_displayNames.TryGetValue(executablePath, out string? cached)) return cached;

        string? description = null;
        try
        {
            string? value = FileVersionInfo.GetVersionInfo(executablePath).FileDescription;
            if (!string.IsNullOrWhiteSpace(value)) description = value.Trim();
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            // The executable can be deleted or replaced while the process is still running. The caller
            // falls back to the file name, which is still perfectly usable.
        }

        _displayNames[executablePath] = description;
        return description;
    }
}
