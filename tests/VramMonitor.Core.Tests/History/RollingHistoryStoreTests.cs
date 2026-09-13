using VramMonitor.Core.History;
using VramMonitor.Core.Model;
using VramMonitor.Core.Tests.Support;
using Xunit;
using static VramMonitor.Core.Tests.Support.Build;

namespace VramMonitor.Core.Tests.History;

public sealed class RollingHistoryStoreTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);
    private static TimeSpan Horizon => Window + Grace;

    [Fact]
    public void Append_rejects_a_backwards_timestamp()
    {
        var store = new RollingHistoryStore();
        store.Append(Sample(20));

        Assert.Throws<ArgumentException>(() => store.Append(Sample(10)));
    }

    [Fact]
    public void Trim_evicts_only_samples_older_than_the_retention_horizon()
    {
        var store = new RollingHistoryStore();
        store.Append(Sample(0));
        store.Append(Sample(120));
        store.Append(Sample(3660));

        // Horizon is 3660s, so "now" at T0+3700 puts the cutoff at T0+40: only the first sample falls out.
        DateTimeOffset now = T0.AddSeconds(3700);
        int evicted = store.Trim(now, Horizon);

        Assert.Equal(1, evicted);
        Assert.Equal(2, store.Count);
        Assert.Equal(T0.AddSeconds(120), store.Samples[0].TimestampUtc);
    }

    [Fact]
    public void A_sample_sitting_exactly_on_the_cutoff_is_retained()
    {
        var store = new RollingHistoryStore();
        store.Append(Sample(0));

        int evicted = store.Trim(T0.Add(Horizon), Horizon);

        Assert.Equal(0, evicted);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Trim_removes_a_session_once_no_retained_sample_refers_to_it()
    {
        var store = new RollingHistoryStore();
        store.UpsertSession(SessionInfo(1));
        store.UpsertSession(SessionInfo(2));
        store.Append(Sample(0, ProbeOutcome.Ok, null, Obs(1, 500)));
        store.Append(Sample(3660, ProbeOutcome.Ok, null, Obs(2, 500)));

        store.Trim(T0.AddSeconds(3700), Horizon);

        Assert.Null(store.TryGetSession(Session(1)));
        Assert.NotNull(store.TryGetSession(Session(2)));
    }

    [Fact]
    public void A_missing_observation_still_keeps_its_session_alive()
    {
        // Spec section 7: a consumer stays inspectable while any retained sample mentions it, even
        // when that sample's value was unreadable. Absence of a value is not absence of the consumer.
        var store = new RollingHistoryStore();
        store.UpsertSession(SessionInfo(1));
        store.Append(Sample(0, ProbeOutcome.Partial, null, Missing(1)));

        store.Trim(T0, Horizon);

        Assert.NotNull(store.TryGetSession(Session(1)));
    }

    [Fact]
    public void The_grace_tail_is_retained_but_is_not_visible()
    {
        // The samples that hysteresis needs must not extend how long a consumer appears in the UI.
        var store = new RollingHistoryStore();
        store.Append(Sample(0));        // now - 3630s: inside horizon (3660s), outside window (3600s)
        store.Append(Sample(30));       // now - 3600s: exactly on the window edge
        store.Append(Sample(3630));     // now

        DateTimeOffset now = T0.AddSeconds(3630);
        store.Trim(now, Horizon);

        Assert.Equal(3, store.Count);                                  // all retained
        Assert.Equal(1, store.FirstVisibleIndex(now, Window));         // first one is not visible
    }

    [Fact]
    public void FirstVisibleIndex_returns_Count_when_nothing_is_visible()
    {
        var store = new RollingHistoryStore();
        store.Append(Sample(0));

        Assert.Equal(store.Count, store.FirstVisibleIndex(T0.AddHours(5), Window));
    }

    [Fact]
    public void Clear_drops_samples_and_sessions()
    {
        var store = new RollingHistoryStore();
        store.UpsertSession(SessionInfo(1));
        store.Append(Sample(0, ProbeOutcome.Ok, null, Obs(1, 100)));

        store.Clear();

        Assert.Equal(0, store.Count);
        Assert.Empty(store.Sessions);
    }
}
