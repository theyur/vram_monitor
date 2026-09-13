# Critic review — round 2

Plan reviewed: `.claude/plans/vram-monitor.plan.md` (round 2)

## Verdict: REVISE — no blocker

Six majors, all local: three are one-sentence rule tightenings in sections 5.1 / 5.4 / 7 (F-1, F-2, F-3),
one is a resolver policy (F-4), two are GPU-selection edge rules (F-5, F-6). None require changing the
architecture, the store, the provider, or the step order.

## Blocker fix verification (sections 7.5, 6) — CONFIRMED

The maths holds. `cum(A,t)` is a sliding sum: between sampled instants it can only fall (samples leave the
trailing window at `t_j + W`); at sampled instants it can rise. Once an app stops qualifying, `cum` is flat
until the first qualifying sample ages out, then steps down. The app leaves when the last instant where
`cum >= T` is older than `G`. That is demotion by aging, at the right time, in real time. The rule is a pure
function of retained history, so threshold changes re-evaluate immediately (spec section 14). Evaluating
`cum` at `t = now - G` needs samples back to `now - G - W`, so retaining `W + G` is necessary; nothing older
is ever read, so it is sufficient.

Microsoft confirms the QPC-through-sleep assumption behind section 5.5: QPC "returns the total number of
ticks that have occurred since the Windows operating system was started, including the time when the machine
was in a sleep state such as standby, hibernate, or connected standby."

## Findings

- **[MAJOR] F-1** — Consumer universe vs the `W+G` buffer is contradictory (sections 6, 7.9; spec 7, AC-7,
  AC-15). Section 6 says analysis input is `[now-W, now]`, but the `Analyze` signature is annotated
  `retained // window + grace` and `Other` is "all retained consumers". With
  `DemotionGrace >= AggressiveThreshold`, `cum(A, now-G) >= T` can hold while A has zero samples in
  `[now-W, now]` — A would appear in Aggressive while spec section 7 requires it removed.

- **[MAJOR] F-2** — The absence-based session rule fires on every `Failed` probe (section 5.4; spec 6.2,
  13.5, AC-5). A `Failed` sample has no observations, so every PID is "absent" in it; one failed probe then
  starts a new session for every creation-time-denied process.

- **[MAJOR] F-3** — The status mapping has no row for the array call itself failing (section 5.1; spec 5.2,
  13.3, AC-8). A non-success return from `PdhGetFormattedCounterArrayW` that does not throw yields an `Ok`
  sample with zero observations: every app "exits normally" with no marker.

- **[MAJOR] F-4** — Tier-2 metadata (`Process.GetProcesses()`) runs every probe forever (section 5.2; spec
  18, AC-25). `System`, `csrss`, `dwm` are permanently path-denied and always present, so at least one PID is
  always unresolved and the 23 ms, ~511-object scan runs every 10 s — roughly 300x the measured 0.082 ms
  probe, destroying the "0 bytes per probe, 0 GCs" result.

- **[MAJOR] F-5** — `GpuSelector` fallback can never be "unresolvable" and silently picks the wrong GPU
  (section 9; spec 14, 16.3). The chain ends at "first non-software adapter", so a persisted RTX 3090
  selector on a machine without that card resolves to the Intel iGPU, which also exposes the counters. The
  plan's own "unresolvable selector raises a blocking error" sentence is unreachable.

- **[MAJOR] F-6** — TDR re-resolve collides with "selected-GPU change clears the history window"
  (sections 5.5, 9; spec 7, 16.2). After a driver reset the same GPU gets a new LUID; clearing on a `GpuId`
  change wipes the hour of history the user most wants.

- **[MINOR] F-7** — Hysteresis evaluation set omits `now - G`; `cum` drops at non-sampled instants, so the
  supremum needs the left endpoint. Demotes up to one interval early.
- **[MINOR] F-8** — Growing grace or window at runtime has an undocumented transient.
- **[MINOR] F-9** — Sleep-gap condition uses `Nominal_{i-1}` while the weight clamp uses `Nominal_i`; an
  interval change fires a spurious `Sampling paused` marker.
- **[MINOR] F-10** — Settings changes wait for the next tick, against spec section 14 "take effect
  immediately".
- **[MINOR] F-11** — Creation time going from denied to known splits a session.
- **[MINOR] F-12** — Tray R3 leaves three choices open: which incumbent is "lowest", whether displacement
  repeats, and EMA tie-breaks. Oscillation and starvation are otherwise sound.
- **[MINOR] F-13** — Friendly-name resolution does disk I/O inside the "pure" analyzer.
- **[MINOR] F-14** — Section 0.9 overstates the cause. The configuration is correct, but "VSTest itself is
  the unsupported path" is wrong: VSTest remains the default mode for `dotnet test` on .NET 10 SDK; what was
  removed is running MTP-enabled projects through the VSTest target with MTP 2.x. Record the exact csproj
  shape that passed.

## Round-1 dispositions — do they genuinely resolve the finding?

- BLOCKER-1: **Yes.** Residuals F-7, F-1, F-8 do not reopen it.
- MAJOR-2: **Yes.** Absent vs missing is right; the poison rule is spec-faithful.
- MAJOR-3: **Partly.** Selector/runtime split and stale-LUID→Failed are right; new gaps F-5, F-6.
- MAJOR-4: **Yes.** QPC-through-sleep confirmed. Residual F-9 is a consistency nit.
- MAJOR-5: **Yes.** R1–R6 fix carry-forward. Residual F-12 is minor.
- MAJOR-6: **(a) and (c) yes.** (b) yes for the intended case but introduced F-2, plus F-11.
- MINOR-7: **Yes** for per-item codes; F-3 covers the array-level return.
- MINOR-8: **Yes in outcome**; reasoning corrected in F-14.
- MINOR-9, MINOR-10, MINOR-12: **Yes, fully.**
- MINOR-11: **Yes**; F-10 is a latency nit, not a thread-safety issue.
