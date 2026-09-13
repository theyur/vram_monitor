namespace VramMonitor.Windows.Pdh;

/// <summary>One entry returned by a PDH counter array.</summary>
internal readonly record struct CounterItem(string Name, uint Status, long Value);

/// <summary>
/// The outcome of reading one PDH counter array.
/// </summary>
/// <remarks>
/// <see cref="Available"/> and <see cref="Readable"/> are deliberately distinct. A counter that could not be
/// added at all and one whose array read failed are both "no data", but neither is the same as a successful
/// read that happened to contain nothing -- and only the last of those can be used to conclude that an
/// adapter has gone away.
/// </remarks>
internal readonly record struct CounterArray(bool Available, bool Readable, IReadOnlyList<CounterItem> Items)
{
    /// <summary>The counter could not be added; the object or counter does not exist.</summary>
    public static CounterArray Missing { get; } = new(false, false, []);

    /// <summary>The counter exists but the array read returned a failure status.</summary>
    public static CounterArray Unreadable { get; } = new(true, false, []);

    public static CounterArray Of(IReadOnlyList<CounterItem> items) => new(true, true, items);
}
