# Plan review — round 2 (plan-critic)

Verdict: **REVISE**

All seven round-1 dispositions verified as applied. Spec coverage complete; tick ladder recomputed by hand
for W = 3600 and W = 60 and matches the spec. Stale-text sites verified exhaustive by grep.
`InternalsVisibleTo("VramMonitor.App.Tests")` exists; `SvgExporter` null measurer falls back to
`PdfRenderContext`. Minor path nit: assumption 10 should cite `src/VramMonitor.App/Tray/TrayTooltipView.xaml.cs:34`.

## Findings

- [MAJOR] Step 6a interleaving: callback clears flag → `Post(S2)` stores and schedules a second callback →
  first callback takes S2 → second callback takes `null`. `MainViewModel.Apply` and
  `TrayTooltipModel.Apply` call `ThrowIfNull`, so the second callback throws on the dispatcher thread.
  Fix: null take is a no-op; use `Interlocked.CompareExchange` for the scheduled flag; test "a scheduled
  callback that finds nothing pending applies nothing and does not throw".
- [MINOR] Dispatcher priority unstated. Default `Normal` outranks `Render` and `Input`. Post the coalesced
  apply at `DispatcherPriority.Background`. The step-14 harness should count actual chart refreshes per
  30 s (not only queued ops), so the 12 h rows report a real refresh cadence.
- [MINOR] Step 12 `The_loop_keeps_sampling_across_many_ticks`: "≥ 0.5 s apart" also passes at the old 2 s.
  Tie it to the new floor (published `SampleInterval == 1 s` and a cadence 2 s could not meet).
