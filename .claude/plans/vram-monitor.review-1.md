# Critic review — round 1

Spec: `docs/vram-monitor-design-spec.md`
Plan reviewed: `.claude/plans/vram-monitor.plan.md` (round 1)

## Verdict: REVISE

One blocker (hysteresis rule does nothing) and five majors, three of which are
"never fabricate / never carry forward" violations hiding inside otherwise reasonable rules
(app aggregation, sleep gaps, tray EMA). All are fixable within the existing architecture
without changing the project layout or the provider choice.

## Findings

### BLOCKER-1 — Demotion hysteresis (plan §7.5) is mathematically a no-op
The rule records "the last timestamp `t` at which cumulative aggressive time *as of t* reached the
threshold", computed over retained samples. That prefix sum is monotone non-decreasing in `t`. If the app
does not meet the threshold at `now`, it never met it at any earlier `t` within the retained window either.
So `lastMet` is never found and demotion is instant. In a cumulative (non-streak) model the *only*
demotion trigger is aging, which is exactly the case the rule cannot see, because the samples that made the
app aggressive a minute ago are the ones that just left the window. Touches §9.4, AC-13. The planned test
would pass trivially. Fix: (a) pass explicit prior state `LastMetThresholdUtc` into `Analyze`, or
(b) retain `window + grace` of samples and compute the true trailing-window cumulative at each
`t ∈ [now − grace, now]`.

### MAJOR-2 — App-level aggregation treats a missing session as zero (plan §7.1)
Summing only *measured* session values means a two-session app with one `null` observation reports the
other session's value alone. That is "interpret missing as zero" at the aggregate level, forbidden by §5.2;
the aggregate is an "affected series" under §13.4 yet shows no gap. It also understates the quartile
population and avg/peak. Fix: distinguish *absent* (no observation → contributes nothing) from *missing*
(`null` → poisons the sum); if any session is `null` at that sample, the app value is `null` and the app is
excluded from `P`.

### MAJOR-3 — Selected GPU is persisted and addressed by LUID
A LUID is unique only until the system restarts, and is reallocated on driver update / TDR recovery.
After a reboot the persisted selection matches nothing; at runtime after a driver reset every probe returns
`Ok` with zero instances for the stale LUID, so every app appears to exit "normally" with no marker — a
fabricated picture (AC-8, AC-3, AC-20, §14). Fix: persist a stable identity (VendorId, DeviceId,
Description, ordinal) and resolve to a LUID at startup; if the `GPU Adapter Memory` instance for the
selected LUID is absent, treat the probe as `Failed` and re-enumerate DXGI. Keep `GpuId` as a runtime-only key.

### MAJOR-4 — Sleep/resume "rendered as a gap" has no mechanism
Series are built from samples; during sleep there are no samples; OxyPlot connects consecutive points
regardless of time distance, so an app present before and after a two-hour sleep gets a straight line across
it — interpolation forbidden by §13.1/AC-8. The 2× weight clamp fixes duration accounting only, not
rendering. Fix: emit a `NaN` break in every series when `t_i − t_{i−1} > 2 × NominalInterval`. Decide
explicitly whether a skipped stretch gets a marker. `PeriodicTimer` behaviour after resume was not measured.

### MAJOR-5 — Tray top-5 (plan §7.7) is underspecified and can carry forward
An EMA over the retained window yields a non-zero smoothed value for an app absent in the latest sample, so
an exited app can sit in a *current* top-5 with a stale number (§10.1, §5.2). Undefined: eligibility, EMA
behaviour across `null`/absence, whether the margin governs ordering within the five, what "exceeds it"
compares against, behaviour with fewer than five eligible apps, and which value is displayed. Fix: write the
algorithm as ordered rules; display raw current, not the EMA.

### MAJOR-6 — Session identity rules have three gaps
(a) It is not stated when creation time is (re)queried; "resolved once" implies a PID reused within one
interval merges two processes. (b) For PIDs whose creation time is denied, a PID that disappears and
reappears has no defined outcome — and the "5 boot-lifetime OS processes" argument does not generalize:
anti-cheat-protected games run as PPL, deny `PROCESS_QUERY_LIMITED_INFORMATION`, are the heaviest VRAM
consumers, and do restart. (c) "Re-keys the session without rewriting history" is only true for the path;
`ProcessSessionId` embeds `CreationTicks`, so a late-obtained creation time either changes the id (requiring
a history rewrite) or stays `0` forever. Touches §6.2, AC-5.

### MINOR-7 — PDH per-item status mapping mislabels exit races as partial probes
A process exiting between `PdhAddEnglishCounterW` and `PdhCollectQueryData` yields
`PDH_CSTATUS_NO_INSTANCE`; that is a normal exit (§13.5) yet would produce a "Partial sample" marker
(AC-10). Fix: map `PDH_CSTATUS_NO_INSTANCE` to *absent*, other non-success codes to *missing*, and
adapter-instance-absent to `Failed`.

### MINOR-8 — Test project packaging will not run as listed
`xunit.v3` 4.0.1 in default VSTest mode also requires `xunit.runner.visualstudio`; the plan lists only
`Microsoft.NET.Test.Sdk`, so `dotnet test` would find no tests. Fix: add the runner, or switch to
Microsoft.Testing.Platform via `global.json`. v3 test projects are `OutputType=Exe`.

### MINOR-9 — Displayed metrics are not defined
"Average dedicated VRAM" over measured samples only or over the window with absence as zero? "Current" for
an app absent in the latest sample must render as a dash, not `0`. "Probe error count" — `Failed` only or
`Failed + Partial`? "Friendly application name" — file name or `FileVersionInfo.FileDescription`?

### MINOR-10 — Quartile edge is an unstated product decision
`k = max(1, ceil(|P|/4))` means with 1–4 apps above the floor exactly one always qualifies. Defensible under
§9.1, but record it as an interpretation. Ties at the k-th position broken by key are arbitrary.

### MINOR-11 — Thread ownership of mutable state is not spelled out
Settings changes originate on the UI thread but trimming/analysis run on the sampler task; re-analysis must
be marshalled, not run concurrently. Export "copied under lock" does not name the lock. `Analyze(window,
sessions, settings, now)` cannot produce `MonitorHealth` (error count is cumulative since start). A
selected-GPU change at runtime should clear the window.

### MINOR-12 — Evidence gaps in §0
The 59-process sample is an idle desktop; nothing was measured with a heavy CUDA/game consumer. The rich
`TrayToolTip` was validated only via `ForceCreate()`, not by hover on Win11; the `szTip` fallback fits the
127-char limit and should be named. The 81→84→81 refresh observation should state it was within one
long-lived process.

## Confirmed sound (not repeated above)
Package existence/targets/licences (`H.NotifyIcon.Wpf` 2.4.1 ships `net10.0-windows7.0`, MIT;
`OxyPlot.Wpf` 2.2.0 MIT); `PdhAddEnglishCounterW` for locale immunity; adapter total from
`GPU Adapter Memory` rather than a sum; invariant-culture export under `uk-UA`; sample-driven retention;
sample weighting with per-sample nominal interval (the 3.5-min check is correct); compiler-enforced
layering; NaN-based gap rendering for exit vs failed/partial with the marker as the sole distinguishing cue.
