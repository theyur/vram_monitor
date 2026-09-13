using VramMonitor.Core.Model;

namespace VramMonitor.Core.Analysis;

/// <summary>
/// Scratch state for one application while a single analysis pass runs. Not part of any public contract.
/// </summary>
internal sealed class AppTrack(string key, int sampleCount)
{
    public string Key { get; } = key;

    /// <summary>Most recent session record seen for this application, used for display fields.</summary>
    public ProcessSessionInfo? Latest { get; set; }

    public HashSet<ProcessSessionId> Sessions { get; } = [];

    /// <summary>Whether the application had at least one observation in this sample.</summary>
    public bool[] Present { get; } = new bool[sampleCount];

    /// <summary>
    /// Whether any of the application's sessions was present but unreadable in this sample. One unreadable
    /// part makes the whole aggregate unknown -- summing only the readable sessions would silently treat the
    /// missing one as zero, which spec section 5.2 forbids.
    /// </summary>
    public bool[] AnyMissing { get; } = new bool[sampleCount];

    /// <summary>Whether the whole probe failed at this sample, so nothing was observed about anyone.</summary>
    public bool[] ProbeFailed { get; } = new bool[sampleCount];

    public long[] Sum { get; } = new long[sampleCount];
    public long[] SharedSum { get; } = new long[sampleCount];
    public bool[] Qualified { get; } = new bool[sampleCount];
    public long[] PrefixQualifyingTicks { get; } = new long[sampleCount];

    /// <summary>The application's dedicated total at this sample, or null when it is not a measurement.</summary>
    public long? MeasuredValue(int i) =>
        !ProbeFailed[i] && Present[i] && !AnyMissing[i] ? Sum[i] : null;

    public PointState StateAt(int i)
    {
        if (ProbeFailed[i]) return PointState.Missing;
        if (!Present[i]) return PointState.Absent;
        return AnyMissing[i] ? PointState.Missing : PointState.Measured;
    }

    public bool HasObservationFrom(int start)
    {
        for (int i = start; i < Present.Length; i++)
        {
            if (Present[i]) return true;
        }

        return false;
    }

    /// <summary>
    /// Mean dedicated VRAM across the samples where this application qualified -- the second ranking key of
    /// spec section 9.3. Zero when it is being held in the aggressive list by grace alone, with no qualifying
    /// sample left in the window; such an application then sorts below genuinely qualifying peers.
    /// </summary>
    public long AverageWhileQualifying(int visibleStart)
    {
        long sum = 0;
        int count = 0;

        // Visible window only. An application held in the aggressive list by grace alone, whose qualifying
        // samples have all aged out, therefore averages zero and sorts below genuinely qualifying peers --
        // and no figure is ever derived from samples the user cannot see on the chart.
        for (int i = visibleStart; i < Qualified.Length; i++)
        {
            if (!Qualified[i]) continue;
            if (MeasuredValue(i) is not { } value) continue;
            sum += value;
            count++;
        }

        return count > 0 ? sum / count : 0;
    }
}
