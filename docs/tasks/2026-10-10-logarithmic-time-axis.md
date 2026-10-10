# Logarithmic time axis and 1-second sampling minimum

Implementation hand-off for Claude Code. Prepared from the repository at
`a4c251c` on 2026-10-10. This document specifies future changes; the application
has not been modified as part of preparing it.

## Goal

Give fresh VRAM measurements more horizontal space on the right of the chart,
while progressively condensing older measurements toward the left. Lower the
minimum supported sampling interval from 2 seconds to 1 second.

Success means recent spikes are easier to inspect without losing the context of
the configured rolling history window, accurate hover values, or observation gaps.
Both VRAM value axes remain linear.

## Scope and design assumptions

The user explicitly requested a logarithmic horizontal scale, recent data on the
right, older data condensed on the left, and a 1-second minimum sampling interval.

The user also confirmed that the configured rolling history limit should remain.
The other choices below are proposed defaults to make the hand-off actionable:

- Preserve the configured history limit. Data older than `HistoryWindow` still
  leaves the visible chart, consumer list, and export. Condensing older data means
  compressing its horizontal position while it remains inside that window.
- Use logarithmic age as the sole chart mode; no linear/log toggle in this task.
- Use a fixed 60-second offset to control the curve; no new user setting.
- Keep the default sampling interval at 10 seconds. A minimum of 1 second means
  users can choose 1 second; it does not change their existing interval.
- Preserve clock-time labels and use a short axis title to explain the new scale.

Keeping data beyond that limit is outside this task. The existing internal grace
tail remains available solely for demotion analysis.

## Current implementation and relevant files

| File | Current responsibility / required attention |
| --- | --- |
| `src/VramMonitor.App/Charting/ChartBuilder.cs` | Builds the plot, linear `DateTimeAxis`, application/PID/total series, and disruption markers. All X coordinates currently use local `DateTimeAxis.ToDouble(...)`. |
| `src/VramMonitor.App/Charting/TimeAxisStep.cs` | Picks a uniform time step from the window alone, preventing label density from oscillating as value-axis labels or the legend change width. The nonlinear axis needs a replacement tick policy. |
| `src/VramMonitor.App/ViewModels/MainViewModel.cs` | Rebuilds the entire plot on each snapshot and on selection/expansion changes. Status line shows the actual sample interval and history window. |
| `src/VramMonitor.App/App.xaml.cs` | Dispatches published snapshots to WPF. Relevant if profiling reveals queued UI work at 1-second cadence. |
| `src/VramMonitor.Core/Configuration/MonitorSettings.cs` | `MinSampleInterval` is 2 seconds; `Validated()` clamps to that minimum and a 5-minute maximum. History ranges from 1 minute to 12 hours. |
| `src/VramMonitor.Core/Configuration/SettingsDocument.cs` | Loads persisted seconds through `Validated()`; default remains 10 seconds. |
| `src/VramMonitor.Core/Configuration/CommandLineOverrides.cs` | `--interval-seconds` also flows through `Validated()`. |
| `src/VramMonitor.App/Views/SettingsWindow.xaml` and `.xaml.cs` | Interval hint currently says minimum 2 seconds. Apply already validates settings centrally. |
| `src/VramMonitor.Core/Sampling/SamplerService.cs` | Background `PeriodicTimer`; runtime interval changes assign `timer.Period`. Publishes immutable snapshots with `TakenUtc`, visible samples, and analysis. |
| `src/VramMonitor.Core/History/RollingHistoryStore.cs` | Retains `HistoryWindow + DemotionGracePeriod`; the extra grace tail is internal only. |
| `src/VramMonitor.Core/Analysis/Analyzer.cs` | Produces timestamped series and explicit gap points; calculates durations using real timestamps and each sample's nominal interval. |
| `tests/VramMonitor.App.Tests/Charting/` | Existing axis tests assume `DateTimeAxis` and uniform steps; replace those assumptions while preserving the regression protection. |
| `tests/VramMonitor.Core.Tests/Configuration/SettingsTests.cs` | Settings validation, persistence, and CLI tests. |
| `tests/VramMonitor.Core.Tests/Sampling/SamplerLoopTests.cs` | Real-loop tests derive their interval from the minimum; changing it can invalidate timing assumptions. |
| `README.md` | CLI table currently documents a minimum of 2 seconds. |

Stack: C# / .NET 10, WPF, OxyPlot.Wpf 2.2.0, xUnit v3 with
Microsoft.Testing.Platform. Read `CLAUDE.md` before implementation, especially its
test-runner and analysis-tool guidance. This task changes the chart presentation
and sampling floor; all other contracts in `docs/vram-monitor-design-spec.md` apply.

## Horizontal mapping contract

Create a small, independently testable mapping helper in the App charting layer.
For each plot build, capture exactly one immutable context:

```text
endUtc = snapshot.TakenUtc
W = snapshot.Settings.HistoryWindow.TotalSeconds
tau = 60 seconds
a = (endUtc - timestampUtc).TotalSeconds

x(a) = 1 - ln(1 + a / tau) / ln(1 + W / tau)
```

`x` is a normalized horizontal coordinate: 0 is the left edge and 1 the right.
Use actual elapsed UTC time, not sample indices or local wall-clock subtraction.
Changing the sampling interval must not change where an existing timestamp appears
in a plot with the same `endUtc` and window.

Properties:

- `timestampUtc = endUtc - W` maps to 0; `timestampUtc = endUtc` maps to 1.
- X increases strictly with timestamp inside the window.
- Equal elapsed-time intervals occupy more width when they are recent.
- Age zero is finite because of the offset. A logarithm of an absolute timestamp,
  or a plain `log(age)` without an offset, does not satisfy this contract.
- The window is always the configured window, even at startup with little data.
  Do not stretch a partially filled history across the entire plot.
- On each new snapshot, older points move left and their spacing contracts.
  Rebuilding selection/expansion against the same snapshot uses the same mapping.

For a 60-minute window, the newest 1 minute occupies about 16.9% of the plot width,
the newest 5 minutes about 43.6%, and the newest 15 minutes about 67.4%.
These are useful acceptance references for the chosen curve.

The inverse, for axis formatting or hit testing, is:

```text
a(x) = tau * (exp((1 - x) * ln(1 + W / tau)) - 1)
timestampUtc(x) = endUtc - a(x) seconds
```

Use double precision and explicit positive-window and nonnegative-age preconditions.
Unexpected timestamps after `endUtc` must be excluded before mapping; never evaluate
the logarithm for a future point. Keep negative X results for timestamps older than
the visible window; clip them at the plot boundary rather than clamping all older
samples onto the left edge. In
particular, retain any synthetic gap point supplied by analysis, even if it lies
just outside the visible window. Do not change sample timestamps or stored history.

## Chart integration

Recommended approach: map source timestamps into normalized X coordinates and use
a dedicated axis derived from `LinearAxis` with explicit tick positions and clock
label formatting. Keep the helper independent of OxyPlot/WPF rendering so the math
can be tested directly.

This keeps the transformation in presentation code. A custom nonlinear time axis
that retains timestamp-valued series coordinates is also possible, but requires
consistent forward/inverse transforms throughout OxyPlot. Prefer the normalized
approach for the bounded change; whichever implementation is used must meet the
same mapping, tracker, and alignment tests. No dependency upgrade is required.

1. Build the mapping context once in `ChartBuilder.Build` and share it between the
   time axis, every application series, every expanded PID series, the total series,
   and all disruption annotations. Use an explicit shared X-axis key.
2. Fix normalized axis bounds at `[0, 1]` with no extra horizontal padding. Keep
   zoom and pan disabled.
3. Preserve existing colours, highlighting, dimming, legend placement, subordinate
   PID styling, and the separate left/right VRAM axes.
4. Preserve each measured Y value and all `double.NaN` discontinuities. No
   smoothing, averaging, interpolation across missing points, or data decimation
   is introduced by this task. Historical compression is spatial only.
5. Transform probe-failed, partial-sample, and sampling-paused marker timestamps
   with the same helper as series points. Their colours and tooltips stay intact.
6. Hover must show the original measured sample's local clock time to seconds,
   series title/PID, and VRAM value. Keep tracker interpolation disabled. The
   existing `TrackerFormatString` formats `{2}` as a timestamp, so simply replacing
   X with a normalized double would break it. Preserve original timestamps in
   chart-only point metadata or implement tracker text from the source point;
   never display a normalized coordinate as a time.
7. Use the original UTC timestamp for identity/order and local time only for
   display. During a daylight-saving transition, chronology remains correct even
   if clock labels repeat; include date and UTC offset in tracker text so samples
   can be distinguished.

### Tick and label policy

Retain the protection against tick density changing when only plot width, legend
contents, or Y-axis label widths change. Uniform time steps from `TimeAxisStep.For`
are unsuitable because they would crowd the old end of the logarithmic chart.

Use this deterministic policy, based only on `W` and the fixed curve:

- Reserve endpoint ticks for ages 0 and `W`.
- Interior candidate ages, ascending: 1, 2, 5, 10, 15, 30 seconds; 1, 2, 5, 10,
  15, 30 minutes; 1, 2, 3, 4, 6, 8, 12 hours.
- Visit candidates from newest to oldest. Accept a candidate only when `0 < a < W`
  and its normalized X distance from every already reserved/accepted tick is at
  least 0.14. Stop accepting after four interior ticks. Sort the final ticks by X.
- Draw major ticks and dotted gridlines at those coordinates. Disable intermediate
  minor ticks/gridlines.
- Format tick timestamps as local `HH:mm:ss` when any chosen interior age is less
  than 60 seconds; otherwise use `HH:mm`. Always retain seconds in hover text.
- Set the axis title to `Time (logarithmic age)`.

For `W = 3600 seconds`, selected ages are 0, 60, 300, 600, 1800, 3600 seconds.
They stay unchanged as new samples arrive; their clock labels advance with the
snapshot. For narrow layouts, verify legibility visually. If labels collide,
reduce the fixed maximum or increase the fixed separation and update the tests
and this policy together; do not restore per-refresh width-based tick selection.

## Sampling interval contract

- Set `MonitorSettings.MinSampleInterval` to `TimeSpan.FromSeconds(1)`.
- Continue clamping intervals below the floor to the floor. Preserve the current
  5-minute maximum and support for fractional intervals above the floor.
- Verify direct validation, Settings Apply, persisted settings, and
  `--interval-seconds 1` all produce an effective interval of 1 second.
- Existing persisted intervals of 2 seconds or greater remain unchanged. No
  settings schema migration or default interval change is needed.
- Change the Settings hint to `How often the GPU is probed. Minimum 1 second.`
  Update the README minimum and stale comments affected by this change.
- Runtime switching between 1 second and a slower interval must keep sampling
  running, retain history, and preserve per-sample `NominalInterval`. Continue
  changing `PeriodicTimer.Period` rather than creating a second outstanding wait.
- Aggressive-duration weighting, grace periods, gap detection, and exported
  timestamps remain based on real elapsed time, independent of the chart curve.

## Performance and timing considerations

At 1 second, a 60-minute window has about 3,600 samples; a 12-hour window has about
43,200, plus an endpoint when both boundaries are sampled. The maximum internal
grace tail adds another hour. Spatial compression does not reduce storage or the
cost of analysis or rebuilding chart points.

Measure Release-build analysis/snapshot publication and chart build/render times
with deterministic synthetic 1-second histories for 60 minutes and 12 hours,
at chart top-N 10 and 50. Include several expanded PID series and a separate
gap/marker fixture. Report fixture size, machine, timings, and allocation/memory
observations. Target a sustained 1-second cadence without a growing UI dispatch
backlog; report any configuration that cannot meet it. Keep fixes targeted to
measured bottlenecks. Lossy downsampling, changed retention, and a broad pipeline
rewrite require a follow-up design rather than being silently added here.

`SamplerLoopTests` currently assumes a 2-second minimum. In particular,
`A_settings_change_wakes_the_loop_and_re_evaluates_without_waiting_for_the_next_tick`
allows a 1,500 ms wait and asserts no extra probe occurred. At a 1-second cadence,
that assertion can race the next tick. Give that test an explicit slower interval
or use synchronization that isolates command publication; retain separate coverage
for the real 1-second loop. Do not weaken the assertion to hide a timing race.

## Verification required from implementation

### Automated coverage

- Mapping endpoints, finite age zero, strict ordering, and forward/inverse
  round trips for 1-minute, 60-minute, and 12-hour windows.
- More screen spacing for equal 1-second steps near the right edge than near the
  left; numeric checks against the 60-minute examples above.
- Identical timestamp positions regardless of sample cadence, and the same
  physical instant represented with different UTC offsets mapping identically.
- Fixed startup extent with zero/one sample; older timestamps are not collapsed
  onto an endpoint, and unexpected future points are excluded before mapping.
- Identical X for an application, PID, total, and marker sharing a timestamp.
- Nonzero-age hover returns the original time/value and never interpolates; gaps
  remain `NaN`, including an analysis-generated pause break near a boundary.
- Endpoint ticks, selected 60-minute candidate ages, at most six major ticks,
  correct label formatting, no minor ticks, and unchanged tick ages after changes
  to plotted values, legend/selection/expansion, or chart width.
- Existing selection, dimming, PID legend behaviour, and separate VRAM scales.
- Validation clamps 0, 0.5 seconds, and negative intervals to exactly 1 second;
  preserves 1, 1.5, 2, and 10 seconds; preserves the upper clamp and 10-second default.
- Persist/load a 1-second interval and a legacy 2-second interval; CLI accepts
  1 second and clamps a subsecond override through the same validation.
- The real loop samples across multiple 1-second ticks and survives runtime
  changes in both directions. Existing gap/duration and export tests still pass.

Replace uniform-step assertions in `ChartBuilderAxisTests`; preserve their purpose
(stable density and exact history extent). Replace/remove `TimeAxisStep` and its
tests if unused rather than keeping obsolete production logic.

Build and run all three test executables using PowerShell from the repo root:

```powershell
dotnet build VramMonitor.slnx -c Debug
& .\tests\VramMonitor.App.Tests\bin\Debug\net10.0-windows\VramMonitor.App.Tests.exe
& .\tests\VramMonitor.Core.Tests\bin\Debug\net10.0\VramMonitor.Core.Tests.exe
& .\tests\VramMonitor.Windows.Tests\bin\Debug\net10.0-windows\VramMonitor.Windows.Tests.exe
dotnet build VramMonitor.slnx -c Release
```

Check each exit code. `CLAUDE.md` documents that `dotnet test` can report zero
tests for this runner setup; the project executables are the reliable check.
GPU-dependent tests may skip when no suitable adapter is available.

### Manual acceptance

- With an hour of synthetic or retained history, recent spikes are visibly wider
  than equally long old spikes, newest at right, oldest at left.
- At Settings interval 1 second, the status says `every 1s`, new samples continue
  arriving, and the effective interval persists after reopening the app.
- Change the history window between 1, 60, and 720 minutes. Extent/mapping update
  immediately without changing retained measurement values or inventing history.
- Hover old and new measured points and expanded PID lines. Times/values agree
  with source samples/export; failure/pause markers remain aligned with gaps.
- Test a narrow chart, splitter resize, changing Y-axis magnitude, and changing
  legend size over multiple refreshes: labels remain legible and density stable.
- Select, deselect, and expand consumers; chart geometry for a given snapshot
  remains identical and the total stays on its right-hand scale.

## Completion deliverables

Provide implementation, focused regression tests, updated minimum-interval docs,
test/build results, performance measurements, and before/after chart screenshots.
State any manual checks or target configurations that remain unverified.
