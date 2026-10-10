# Plan review — round 1 (plan-critic)

Verdict: **REVISE**

## Requirement coverage

All 19 independently-read spec requirements mapped to plan steps; requirement 14 (performance) covered
but self-restricted narrower than the spec (MAJOR below). Verified against repo and OxyPlot v2.2.0
source: `Axis.GetTickValues` public virtual and used by `AxisRendererBase.Render`; `FormatValueOverride`
protected virtual; `DataPointSeries.GetNearestPoint` returns null when `interpolate &&
!CanTrackerInterpolatePoints`, builds `Text` from `TrackerFormatString`; `TrackerManipulator` shows
`result.Text` unmodified. Assumptions 1 and 2 hold. No CLAUDE.md conflicts.

## Findings

- [MAJOR] Step 14 narrows the spec's performance rule ("only a targeted fix to a measured bottleneck in
  code this task adds") whereas the spec names `App.xaml.cs` as relevant if profiling reveals queued UI
  work. `Dispatcher.InvokeAsync` has no coalescing; at 12 h × 1 s × top-N 50 a growing dispatch backlog is
  plausible. Fix: allow a targeted fix anywhere in the publish/dispatch/build path (excluding decimation,
  retention changes, rewrites) and measure dispatch queue depth.
- [MINOR] Wrong citation: `SettingsWindow.OnApply` is at `SettingsWindow.xaml.cs:50-76`, not 226–237.
- [MINOR] Inverse-mapping preconditions unspecified; OxyPlot may pass x marginally outside [0,1]; a throw
  there would happen inside layout. State `AgeSecondsAt(x)` is total for finite x; test x = −0.01, 1.01.
- [MINOR] `TimeAxisStepTests.A_degenerate_window_falls_back_to_the_finest_step` guarded tolerance of a
  zero/negative window; `LogTimeScale` throws, so `Build` now throws. Add a test and a remark that
  settings must be validated.
- [MINOR] Deletion ordering: `TimeAxisStepTests.cs` deletion not in a step; App.Tests won't compile
  between steps 6 and 10. Move deletion into step 6 or merge.
- [MINOR] Step 8 (status line `0.##`) is outside the spec; current format already shows `every 1s`.
  Recommend dropping.
- [MINOR] `Analyzer.cs:495` magnitude ("hundreds of millions") is tied to the 2 s floor; update it.
