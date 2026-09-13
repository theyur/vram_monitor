# Critic review — round 3 (final)

Plan reviewed: `.claude/plans/vram-monitor.plan.md` (round 3 input)

## Verdict: REVISE — no blocker

"The architecture, provider choice, store, hysteresis maths, universe rule, and step order are sound, and the
code already on disk matches the plan. The four MAJOR items are each a one- or two-sentence contract change:
N-1 (per-array failure semantics), N-2 (tick exception policy and monotonic-derived timestamps), N-3
(adoption requires a name match), N-4 (publish immutable visible-slice copies). They should be folded into
sections 5.1, 5.4, 5.5 and 10 before step 14 and step 16 are written; nothing built so far needs to change."

## Round-2 findings F-1 .. F-14 — all confirmed resolved

F-1 resolved (section 6.1 universe + `A in Universe` in 7.5 + 7.9 partition), residual export horizon -> N-4.
F-2 resolved (absence over Ok/Partial only); composes correctly with the new zero-instance rule.
F-3 resolved but over-corrected -> N-1; zero-instance row rests on an unstated assumption -> N-12.
F-4 resolved for cost; transient tier-1 failure freezes identity -> N-8.
F-5 resolved; first-run fallback reintroduces the hazard -> N-9.
F-6 resolved; text references a non-existent field -> N-11.
F-7 resolved; the supremum argument is correct (`cum` falls only at `t_j + W`, rises only at sampled instants).
F-8 resolved. F-9 resolved, residual clock mismatch -> N-2. F-10 resolved, mechanics -> N-10.
F-11 resolved for the stated case; PID reuse within one interval -> N-3.
F-12 resolved; challenger landing slot unstated -> N-14. F-13 resolved; deleted executable -> N-14.
F-14 resolved and verified against the files on disk.

## New findings

- **[MAJOR] N-1** — "either `PdhGetFormattedCounterArrayW` call returns non-success -> `Failed`" discards
  valid primary data. There are three arrays. If only `Shared Usage` fails, every good `Dedicated Usage`
  value is thrown away, against spec 5.2 and 5.1 (shared is "secondary diagnostic information only").
- **[MAJOR] N-2** — No per-tick exception policy, and `RollingHistoryStore.Append` throws on a backwards wall
  clock. An unhandled exception on the background task kills monitoring silently while the tray shows stale
  data with no marker and no error-count change — the failure mode spec 16.2 forbids.
- **[MAJOR] N-3** — Adoption row plus frozen identity can permanently attribute a new process to the wrong
  application: a handle-denying process exits, its PID is reused within one interval by an accessible
  process, no sample sees it absent, creation time becomes readable, and the new process inherits the old
  frozen identity for its whole lifetime.
- **[MAJOR] N-4** — `MonitorSnapshot` content undefined and section 10's "immutable" premise is not provided
  by the code: `Samples`/`Sessions` return the live collections the sampler mutates. Export on the UI thread
  would race with trimming. Also "retained" means `W + G` in the `Analyze` signature while export must cover
  the visible window.
- **[MINOR] N-5** — `AggressiveDurationThreshold` clamps to `[Zero, 24h]`; `T = 0` marks every universe
  member aggressive, contradicting spec 8.
- **[MINOR] N-6** — Hysteresis evaluation cost unstated; naive evaluation is
  `O((G/interval) x (W/interval))` per app, ~130M operations every 2 s at permitted extremes.
- **[MINOR] N-7** — Average and Peak do not name their horizon; must be visible-window only.
- **[MINOR] N-8** — Freeze at `ExecutableName` after one transient tier-1 failure splits one application into
  two rows (one keyed by name, siblings keyed by path).
- **[MINOR] N-9** — First-run "first non-software adapter" can pick the iGPU; DXGI order is not guaranteed.
- **[MINOR] N-10** — `PeriodicTimer` permits one outstanding `WaitForNextTickAsync`; the naive
  `Task.WhenAny` loop throws `InvalidOperationException`.
- **[MINOR] N-11** — Section 5.5 references `GpuSample.Gpu`, which does not exist.
- **[MINOR] N-12** — The zero-instance rule is safe only because `System` (pid 4) holds an instance on every
  WDDM adapter; record the assumption.
- **[MINOR] N-13** — Step 16 (`SamplerService`) has no test step.
- **[MINOR] N-14** — Contract gaps: challenger landing slot; stale `latest` presented as current;
  `FileVersionInfo` on a deleted executable; sections 0.1/12 should record that steps 1-4 exist on disk;
  `Directory.Packages.props` still carries the retracted "VSTest is unsupported" claim.

## Remaining risks (not findings)

Per-item `Partial` frequency was not reported from the 9000-probe soak. `SystemClock.MonotonicTicks` uses
`Environment.TickCount64`, which includes sleep time so gap detection holds. PPL games remain untested.
