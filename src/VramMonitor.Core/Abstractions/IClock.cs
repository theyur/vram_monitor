namespace VramMonitor.Core.Abstractions;

/// <summary>
/// Time source. Wall-clock time is used for sample timestamps; monotonic time for interval
/// scheduling and gap detection, so that clock adjustments and sleep/resume cannot corrupt either.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Monotonically increasing tick count, unaffected by wall-clock adjustments.</summary>
    long MonotonicTicks { get; }
}

/// <summary>The real system clock.</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <remarks>
    /// Backed by <see cref="System.Diagnostics.Stopwatch"/> (the high-resolution performance counter) rather
    /// than <c>Environment.TickCount64</c>. Tick count has roughly 15 ms granularity, which is coarser than a
    /// whole sampling cycle and would report every probe as taking zero time. The performance counter also
    /// keeps advancing across standby and hibernation, so sleep still shows up as a gap.
    /// </remarks>
    public long MonotonicTicks =>
        (long)(System.Diagnostics.Stopwatch.GetTimestamp()
               * (double)TimeSpan.TicksPerSecond / System.Diagnostics.Stopwatch.Frequency);
}
