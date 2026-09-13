using VramMonitor.Core.Model;
using VramMonitor.Core.Sampling;
using VramMonitor.Core.Tests.Support;
using Xunit;
using static VramMonitor.Core.Tests.Support.Build;

namespace VramMonitor.Core.Tests.Sampling;

public sealed class ProcessSessionTrackerTests
{
    private static readonly GpuId Gpu = new("luid_0x0_0x1");
    private const uint Pid = 4242;

    private static (ProcessSessionTracker Tracker, FakeMetadataResolver Resolver) NewTracker()
    {
        var resolver = new FakeMetadataResolver();
        return (new ProcessSessionTracker(resolver), resolver);
    }

    /// <summary>Runs one probe with the given outcome, reporting the given PIDs as present.</summary>
    private static ProcessSessionInfo[] Probe(
        ProcessSessionTracker tracker, double atSeconds, ProbeOutcome outcome, params uint[] pids)
    {
        tracker.BeginProbe(outcome);
        return [.. pids.Select(p => tracker.Observe(p, Gpu, T0.AddSeconds(atSeconds)))];
    }

    [Fact]
    public void Same_pid_and_creation_time_is_one_session()
    {
        (ProcessSessionTracker tracker, FakeMetadataResolver resolver) = NewTracker();
        resolver.CreationTicks[Pid] = 1000;

        ProcessSessionInfo a = Probe(tracker, 0, ProbeOutcome.Ok, Pid)[0];
        ProcessSessionInfo b = Probe(tracker, 10, ProbeOutcome.Ok, Pid)[0];

        Assert.Equal(a.SessionId, b.SessionId);
    }

    [Fact]
    public void A_reused_pid_with_a_new_creation_time_starts_a_new_session()
    {
        (ProcessSessionTracker tracker, FakeMetadataResolver resolver) = NewTracker();
        resolver.CreationTicks[Pid] = 1000;
        ProcessSessionInfo first = Probe(tracker, 0, ProbeOutcome.Ok, Pid)[0];

        resolver.CreationTicks[Pid] = 2000;      // same PID, different process
        ProcessSessionInfo second = Probe(tracker, 10, ProbeOutcome.Ok, Pid)[0];

        Assert.NotEqual(first.SessionId, second.SessionId);
    }

    [Fact]
    public void A_handle_denying_process_that_stays_present_keeps_one_session()
    {
        // No creation time available: continuity is inferred from uninterrupted presence.
        (ProcessSessionTracker tracker, _) = NewTracker();

        ProcessSessionInfo a = Probe(tracker, 0, ProbeOutcome.Ok, Pid)[0];
        ProcessSessionInfo b = Probe(tracker, 10, ProbeOutcome.Ok, Pid)[0];
        ProcessSessionInfo c = Probe(tracker, 20, ProbeOutcome.Ok, Pid)[0];

        Assert.Equal(a.SessionId, b.SessionId);
        Assert.Equal(a.SessionId, c.SessionId);
    }

    [Fact]
    public void A_handle_denying_process_that_disappears_and_returns_starts_a_new_session()
    {
        (ProcessSessionTracker tracker, _) = NewTracker();

        ProcessSessionInfo before = Probe(tracker, 0, ProbeOutcome.Ok, Pid)[0];
        Probe(tracker, 10, ProbeOutcome.Ok);                       // observed; PID genuinely absent
        ProcessSessionInfo after = Probe(tracker, 20, ProbeOutcome.Ok, Pid)[0];

        Assert.NotEqual(before.SessionId, after.SessionId);
    }

    [Fact]
    public void A_failed_probe_is_not_evidence_of_absence()
    {
        // A failed probe observed nothing at all. Reading its empty result as "every process vanished"
        // would split the session of every handle-denying process on any transient probe failure.
        (ProcessSessionTracker tracker, _) = NewTracker();

        ProcessSessionInfo before = Probe(tracker, 0, ProbeOutcome.Ok, Pid)[0];
        Probe(tracker, 10, ProbeOutcome.Failed);
        ProcessSessionInfo after = Probe(tracker, 20, ProbeOutcome.Ok, Pid)[0];

        Assert.Equal(before.SessionId, after.SessionId);
    }

    [Fact]
    public void A_partial_probe_does_count_as_an_observation()
    {
        (ProcessSessionTracker tracker, _) = NewTracker();

        ProcessSessionInfo before = Probe(tracker, 0, ProbeOutcome.Ok, Pid)[0];
        Probe(tracker, 10, ProbeOutcome.Partial);
        ProcessSessionInfo after = Probe(tracker, 20, ProbeOutcome.Ok, Pid)[0];

        Assert.NotEqual(before.SessionId, after.SessionId);
    }

    [Fact]
    public void A_creation_time_that_becomes_readable_is_adopted_not_treated_as_a_restart()
    {
        // OpenProcess can fail transiently while a process is still starting.
        (ProcessSessionTracker tracker, FakeMetadataResolver resolver) = NewTracker();

        ProcessSessionInfo denied = Probe(tracker, 0, ProbeOutcome.Ok, Pid)[0];
        Assert.True(denied.HasUnknownCreationTime);

        resolver.CreationTicks[Pid] = 5000;
        ProcessSessionInfo known = Probe(tracker, 10, ProbeOutcome.Ok, Pid)[0];

        Assert.Equal(denied.SessionId, known.SessionId);
        Assert.Equal(5000, known.CreationTicks);
        Assert.False(known.HasUnknownCreationTime);
    }

    [Fact]
    public void A_pid_reused_within_one_interval_is_not_adopted_into_the_old_identity()
    {
        // The nastiest case: a handle-denying process exits and its PID is reused inside a single interval,
        // so no probe ever sees it absent. Adopting the newly readable creation time would hand the newcomer
        // the old session's identity permanently -- and it would never be re-resolved, because its kind is
        // no longer PidFallback. The executable name is the evidence that they are different processes.
        (ProcessSessionTracker tracker, FakeMetadataResolver resolver) = NewTracker();
        resolver.Names[Pid] = "TheGame.exe";

        ProcessSessionInfo game = Probe(tracker, 0, ProbeOutcome.Ok, Pid)[0];
        Assert.Equal("name:thegame.exe", game.Identity.Key);

        // PID reused by an ordinary, openable process.
        resolver.CreationTicks[Pid] = 9000;
        resolver.Paths[Pid] = @"C:\apps\notepad.exe";
        ProcessSessionInfo newcomer = Probe(tracker, 10, ProbeOutcome.Ok, Pid)[0];

        Assert.NotEqual(game.SessionId, newcomer.SessionId);
        Assert.Equal(@"c:\apps\notepad.exe", newcomer.Identity.Key);
    }

    [Fact]
    public void Adoption_still_works_when_the_executable_name_matches()
    {
        // The legitimate transient-denial case must keep working: same program, handle briefly unavailable.
        (ProcessSessionTracker tracker, FakeMetadataResolver resolver) = NewTracker();
        resolver.Names[Pid] = "python.exe";

        ProcessSessionInfo before = Probe(tracker, 0, ProbeOutcome.Ok, Pid)[0];

        resolver.CreationTicks[Pid] = 9000;
        resolver.Paths[Pid] = @"D:\envs\Corpus\python.exe";
        ProcessSessionInfo after = Probe(tracker, 10, ProbeOutcome.Ok, Pid)[0];

        Assert.Equal(before.SessionId, after.SessionId);
        Assert.Equal(9000, after.CreationTicks);
        // The now-readable path also upgrades the grouping key.
        Assert.Equal(@"d:\envs\corpus\python.exe", after.Identity.Key);
    }

    [Fact]
    public void A_transient_path_failure_does_not_split_one_program_into_two_applications()
    {
        // Tier 1 raced and lost on the first probe, so this session is keyed by name while its siblings are
        // keyed by full path. Retrying tier 1 -- which costs microseconds -- reunites them.
        (ProcessSessionTracker tracker, FakeMetadataResolver resolver) = NewTracker();
        resolver.CreationTicks[Pid] = 1000;
        resolver.Names[Pid] = "python.exe";

        ProcessSessionInfo first = Probe(tracker, 0, ProbeOutcome.Ok, Pid)[0];
        Assert.Equal(ProcessIdentityKind.ExecutableName, first.Identity.Kind);

        resolver.Paths[Pid] = @"D:\envs\Corpus\python.exe";
        ProcessSessionInfo second = Probe(tracker, 10, ProbeOutcome.Ok, Pid)[0];

        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(ProcessIdentityKind.ExecutablePath, second.Identity.Kind);
        Assert.Equal(@"d:\envs\corpus\python.exe", second.Identity.Key);
    }

    [Fact]
    public void Identity_falls_back_from_path_to_name_to_pid()
    {
        (ProcessSessionTracker tracker, FakeMetadataResolver resolver) = NewTracker();
        resolver.Paths[1] = @"C:\apps\Thing.exe";
        resolver.Names[2] = "dwm.exe";
        // pid 3 has neither

        ProcessSessionInfo[] infos = Probe(tracker, 0, ProbeOutcome.Ok, 1, 2, 3);

        Assert.Equal(ProcessIdentityKind.ExecutablePath, infos[0].Identity.Kind);
        Assert.Equal(@"c:\apps\thing.exe", infos[0].Identity.Key);
        Assert.Equal(ProcessIdentityKind.ExecutableName, infos[1].Identity.Kind);
        Assert.Equal("name:dwm.exe", infos[1].Identity.Key);
        Assert.Equal(ProcessIdentityKind.PidFallback, infos[2].Identity.Kind);
        Assert.Equal("pid:3", infos[2].Identity.Key);
    }

    [Fact]
    public void Two_pythons_from_different_environments_stay_separate_applications()
    {
        (ProcessSessionTracker tracker, FakeMetadataResolver resolver) = NewTracker();
        resolver.Paths[1] = @"D:\envs\Corpus\python.exe";
        resolver.Paths[2] = @"C:\Python312\python.exe";

        ProcessSessionInfo[] infos = Probe(tracker, 0, ProbeOutcome.Ok, 1, 2);

        Assert.NotEqual(infos[0].Identity.Key, infos[1].Identity.Key);
    }

    [Fact]
    public void A_version_resource_description_is_preferred_as_the_display_name()
    {
        (ProcessSessionTracker tracker, FakeMetadataResolver resolver) = NewTracker();
        resolver.Paths[1] = @"C:\apps\msedge.exe";
        resolver.DisplayNames[@"C:\apps\msedge.exe"] = "Microsoft Edge";

        ProcessSessionInfo info = Probe(tracker, 0, ProbeOutcome.Ok, 1)[0];

        Assert.Equal("Microsoft Edge", info.Identity.DisplayName);
    }

    [Fact]
    public void An_unidentifiable_session_stops_being_retried_after_the_attempt_cap()
    {
        // Denial by a protected process is permanent, so retrying forever would run the expensive
        // tier-2 bulk scan on every probe for the life of the application.
        (ProcessSessionTracker tracker, FakeMetadataResolver resolver) = NewTracker();

        for (int i = 0; i < 10; i++) Probe(tracker, i * 10, ProbeOutcome.Ok, Pid);

        Assert.Equal(ProcessSessionTracker.MaxIdentityAttempts, resolver.ResolveNameOnlyCalls);
    }

    [Fact]
    public void A_session_resolved_to_a_name_is_never_re_resolved()
    {
        // dwm/csrss/System resolve to a name on the first probe and must never trigger the bulk scan again.
        (ProcessSessionTracker tracker, FakeMetadataResolver resolver) = NewTracker();
        resolver.Names[Pid] = "dwm.exe";

        for (int i = 0; i < 10; i++) Probe(tracker, i * 10, ProbeOutcome.Ok, Pid);

        Assert.Equal(1, resolver.ResolveNameOnlyCalls);
    }
}
