using VramMonitor.Core.Abstractions;

namespace VramMonitor.Core.Tests.Support;

internal sealed class FakeMetadataResolver : IProcessMetadataResolver
{
    public Dictionary<uint, long> CreationTicks { get; } = [];
    public Dictionary<uint, string> Paths { get; } = [];
    public Dictionary<uint, string> Names { get; } = [];
    public Dictionary<string, string> DisplayNames { get; } = [];

    public int GetTimesCalls { get; private set; }
    public int ResolvePathCalls { get; private set; }
    public int ResolveNameOnlyCalls { get; private set; }
    public int BeginProbeCalls { get; private set; }

    public ProcessTimes? GetTimes(uint pid)
    {
        GetTimesCalls++;
        return CreationTicks.TryGetValue(pid, out long ticks) ? new ProcessTimes(ticks) : null;
    }

    public string? ResolvePath(uint pid)
    {
        ResolvePathCalls++;
        return Paths.GetValueOrDefault(pid);
    }

    public string? ResolveNameOnly(uint pid)
    {
        ResolveNameOnlyCalls++;
        return Names.GetValueOrDefault(pid);
    }

    public string? ResolveDisplayName(string executablePath) => DisplayNames.GetValueOrDefault(executablePath);

    public void BeginProbe() => BeginProbeCalls++;
}
