# Plan — logarithmic time axis and 1-second sampling minimum

Spec: `docs/tasks/2026-10-10-logarithmic-time-axis.md` (copied verbatim into this worktree from the
untracked file in the main checkout; the spec itself is not edited).
Base: `a4c251c` (master), branch `claude/logarithmic-time-axis-b7f644`.

## 0. Requirement IDs

The spec has no IDs, so they are assigned here. Each quotes or paraphrases its spec anchor.

| ID | Spec section | Requirement |
| --- | --- | --- |
| R-MAP-1 | Horizontal mapping | One immutable mapping context per plot build: `endUtc = snapshot.TakenUtc`, `W = HistoryWindow`, `tau = 60 s`, `x(a) = 1 - ln(1+a/tau)/ln(1+W/tau)`. |
| R-MAP-2 | Horizontal mapping | Age from real UTC elapsed time; independent of sample interval/index; same instant in different offsets maps identically. |
| R-MAP-3 | Properties | `endUtc-W → 0`, `endUtc → 1`, strictly increasing, recent intervals wider, age 0 finite. |
| R-MAP-4 | Properties | Window is always the configured window, even with 0/1 samples; no stretching. |
| R-MAP-5 | Properties | Rebuilds against the same snapshot use the same mapping (selection/expansion). |
| R-MAP-6 | Inverse | `a(x)` / `timestampUtc(x)` inverse available. |
| R-MAP-7 | Preconditions | Double precision; explicit positive-window and non-negative-age preconditions; future timestamps excluded before mapping (log never evaluated for them). |
| R-MAP-8 | Preconditions | Older-than-window timestamps keep negative X (clipped by plot, not clamped); synthetic analysis gap points retained even just outside the window; samples/history untouched. |
| R-MAP-9 | Acceptance refs | 60-min window: newest 1 min ≈ 16.9 %, 5 min ≈ 43.6 %, 15 min ≈ 67.4 %. |
| R-CH-1 | Chart integration 1 | Mapping built once in `ChartBuilder.Build`, shared by axis, app, PID, total series and markers; explicit shared X-axis key. |
| R-CH-2 | Chart integration 2 | Normalized axis bounds `[0,1]`, no padding; zoom/pan off. |
| R-CH-3 | Chart integration 3 | Preserve colours, highlight/dim, legend placement, PID styling, separate left/right VRAM axes. |
| R-CH-4 | Chart integration 4 | Preserve Y values and all `NaN` discontinuities; no smoothing/interpolation/decimation. |
| R-CH-5 | Chart integration 5 | Disruption markers mapped with the same helper; colours/tooltips intact. |
| R-CH-6 | Chart integration 6 | Hover: original sample local time to seconds, title/PID, value; interpolation off; never a normalized coordinate shown as time. |
| R-CH-7 | Chart integration 7 | UTC for identity/order, local for display; tracker includes date + UTC offset (DST). |
| R-TK-1 | Tick policy | Endpoint ticks at ages 0 and W. |
| R-TK-2 | Tick policy | Candidate ladder 1,2,5,10,15,30 s; 1,2,5,10,15,30 min; 1,2,3,4,6,8,12 h; newest→oldest; accept if `0<a<W` and normalized distance ≥ 0.14 from every reserved/accepted tick; max 4 interior; sort by X. |
| R-TK-3 | Tick policy | Major ticks + dotted gridlines at those X; no minor ticks/gridlines. |
| R-TK-4 | Tick policy | Labels `HH:mm:ss` if any interior age < 60 s, else `HH:mm`; hover always keeps seconds. |
| R-TK-5 | Tick policy | Axis title `Time (logarithmic age)`. |
| R-TK-6 | Tick policy | W=3600 → ages 0,60,300,600,1800,3600; ages fixed across refreshes, labels advance; width-independent; narrow layout verified visually. |
| R-SA-1 | Sampling | `MinSampleInterval = 1 s`; clamp below; keep 5-min max and fractional intervals. |
| R-SA-2 | Sampling | Direct validation, Settings Apply, persisted settings and `--interval-seconds 1` all yield 1 s. |
| R-SA-3 | Sampling | Persisted ≥ 2 s unchanged; no schema migration; default stays 10 s. |
| R-SA-4 | Sampling | Settings hint `How often the GPU is probed. Minimum 1 second.`; README minimum and stale comments updated. |
| R-SA-5 | Sampling | Runtime 1 s ↔ slower keeps sampling, retains history, preserves per-sample `NominalInterval`; keep assigning `PeriodicTimer.Period`. |
| R-SA-6 | Sampling | Weighting, grace, gap detection, export remain real-time based, independent of the chart. |
| R-PF-1 | Performance | Release measurements: analysis/snapshot publication and chart build/render, synthetic 1-s histories, 60 min and 12 h, top-N 10 and 50, several expanded PIDs, separate gap/marker fixture; report fixture size, machine, timings, allocation/memory; target sustained 1-s cadence without UI backlog; report failures; no lossy downsampling/retention change/rewrite. |
| R-PF-2 | Performance | Fix `SamplerLoopTests` settings-wake race with an explicit slower interval; keep real 1-s loop coverage; do not weaken the assertion. |
| R-VE-1 | Automated coverage | All bullets of "Automated coverage" (mapped per test in step 9–12). |
| R-VE-2 | Automated coverage | Replace uniform-step assertions in `ChartBuilderAxisTests`, preserving purpose; remove `TimeAxisStep` + its tests if unused. |
| R-VE-3 | Build/run | Debug build, three test exes, Release build; check exit codes. |
| R-VE-4 | Manual acceptance | All bullets of "Manual acceptance". |
| R-DL-1 | Deliverables | Implementation, tests, docs, test/build results, perf numbers, before/after chart screenshots, list of unverified checks. |

## 1. Evidence (observed in the repository)

- `ChartBuilder.Build(snapshot, selectedKey, expandedKeys)` (`src/VramMonitor.App/Charting/ChartBuilder.cs:40`)
  is the only plot construction site. Callers: `MainViewModel.OnSelectedKeyChanged` (line 78) and
  `MainViewModel.Rebuild` (line 121); tests in `ChartBuilderAxisTests`. Every X coordinate is
  `DateTimeAxis.ToDouble(ts.ToLocalTime().DateTime)` (lines 145–146, 225, 263).
- Series use `TrackerFormatString = "{0}\n{2:HH:mm:ss}\n{4:0} MB"` and `CanTrackerInterpolatePoints = false`
  (lines 219–220). `{2}` is `xAxis.GetValue(x)`, which would become a double on a `LinearAxis`.
- `TimeAxisStep` is used only by `ChartBuilder.AddAxes` (line 133) and `TimeAxisStepTests`
  (grep: no other references in `src/`, `tests/`, `.xaml`).
- OxyPlot 2.2.0 (reflected from `bin/Debug/.../OxyPlot.dll`): `Axis.GetTickValues(out …)` is public virtual,
  `Axis.FormatValueOverride(double)` is protected virtual, `LineSeries.GetNearestPoint(ScreenPoint, bool)` is
  public virtual, every `TrackerHitResult` property used here (`Text`, `Item`, `Index`, `DataPoint`) is
  settable, `LineAnnotation` has `XAxisKey`/`ClipByXAxis`, `SvgExporter.ExportToString(model, w, h, isDocument,
  textMeasurer, …)` is public static (headless layout for tests).
- Analyzer: series points carry `DateTimeOffset TimestampUtc`; sampling-gap break points are inserted at
  `samples[i-1].TimestampUtc + 1 tick` with `PointState.Missing` (`Analyzer.cs:392–410`); markers at sample
  timestamps (`Analyzer.cs:435–458`). Everything visible satisfies `ts ≥ now − W` where `now == TakenUtc`
  (`SamplerService.PublishFromStore` uses one `now` for both), so negative X arises only from hand-built
  results; the chart must still handle it (R-MAP-8).
- `MonitorSettings.MinSampleInterval = 2 s`, `Validated()` clamps `[Min, 5 min]` (`MonitorSettings.cs:17, 95`).
  `SettingsDocument.ToSettings()` and `CommandLineOverrides.ApplyTo()` both end in `.Validated()`;
  `SettingsWindow.OnApply` ends in `.Validated()` (`SettingsWindow.xaml.cs:50–76`).
- Tests that touch the interval floor: `SettingsTests.Out_of_range_values_are_clamped_rather_than_rejected`
  (uses the constant, keeps passing) and `SamplerLoopTests` (`Interval = MinSampleInterval`). All Analyzer /
  SamplerService / Export tests use explicit ≥ 10 s or 12.5 s intervals — unaffected by the floor change.
- Stale "2 s" text: `SettingsWindow.xaml:42`, `README.md:40`, `Analyzer.cs:495` (comment),
  `SamplerLoopTests.cs:20` (comment), `.claude/skills/run-app/SKILL.md:37`, `docs/HANDOVER.md:234` (risk item).
- Status line formats the interval as `{TotalSeconds:0}` (`MainViewModel.cs:148`), so 1 s prints `every 1s`,
  but 1.5 s would print `every 2s`.
- UI dispatch is `Dispatcher.InvokeAsync` per snapshot (`App.xaml.cs:145`) — no coalescing, so UI work
  > interval would queue (relevant for R-PF-1).
- **Baseline measured on `a4c251c`** (scratchpad harness, Release, synthetic 1-s histories, 60 apps / 69
  sessions, 3 apps × 4 PIDs expanded; machine COLL1, 24 logical CPUs, Windows 10.0.26200, .NET 10.0.12):
  60 min top-10: publish 89 ms, build 12 ms, PNG render 386 ms; 60 min top-50: 59 / 12 / 564 ms;
  12 h top-10: 1447 / 56 / 2211 ms; 12 h top-50: 1272 / 133 / 4666 ms; 60-min gap fixture: 42 / 3 / 153 ms.
  So at 12 h × 1 s the existing pipeline already cannot hold a 1-s cadence, and UI work per snapshot
  (build + render ≥ 2.2 s) exceeds the publication period — with uncoalesced `InvokeAsync` the dispatcher
  queue grows without bound. This is the measured bottleneck addressed by step 6a.

## 2. Assumptions

| # | Assumption | Confidence | Consequence if wrong | Validation |
| --- | --- | --- | --- | --- |
| 1 | Subclassing `LineSeries` and overriding `GetNearestPoint` (post-processing `Text`/`Item`) is honoured by OxyPlot.Wpf's tracker manipulator. | 0.85 | Hover would show OxyPlot's default text with a normalized X. | Reflection confirmed the override point; headless test calls `GetNearestPoint` after `SvgExporter` layout; manual hover in the running app (step 15). |
| 2 | Overriding `GetTickValues` alone controls labels, tick marks and major gridlines, and empty minor list removes minor ticks. | 0.85 | Extra/missing gridlines or minor ticks. | Reflection shows it is the virtual feeding the renderer; verified in rendered PNG (step 14). |
| 3 | Injecting a `TimeZoneInfo` (optional parameter, default `TimeZoneInfo.Local`) into `ChartBuilder.Build` is acceptable as an App-internal API change. | 0.9 | None outside this repo; App is the only consumer. | grep callers (two in `MainViewModel`). |
| 4 | Settings Apply can be verified by the existing central `Validated()` unit coverage plus one manual run, with `%APPDATA%\VramMonitor\settings.json` backed up first and restored afterwards. | 0.75 | Apply path unverified, or the user's real config altered. | Backup/restore around the manual check; reported. |
| 5 | Performance harness lives in the session scratchpad (not committed); "render" is measured with OxyPlot.Wpf `PngExporter` on an STA thread as a proxy for `PlotView` rendering. | 0.8 | Render numbers differ from on-screen cost. | State the proxy explicitly; corroborate with the live app at 1 s for a few minutes (step 15). |
| 6 | "Before/after chart screenshots" can be produced by rendering the real `ChartBuilder` (master build vs this branch) over the same synthetic one-hour history, plus a live-app screenshot after. | 0.8 | User wanted live-app before shots only. | Also take a live "after" screenshot; reported. |
| 7 | ~~Status-line `0.##` format~~ — dropped after review round 1 (out of spec); reported as a known gap instead. | — | — | — |
| 10 | Coalescing UI dispatch to "latest snapshot wins" loses no data: every snapshot is the complete visible state, so skipping a superseded one changes only how often the UI refreshes. | 0.9 | A consumer relying on every intermediate snapshot (none found: `MainViewModel.Apply` and `TrayTooltipModel.Apply` (`src/VramMonitor.App/Tray/TrayTooltipView.xaml.cs:34`) both replace state wholesale). | Read both `Apply` methods before step 6a; unit test of the coalescer. |
| 8 | Tracker text uses `CurrentCulture` for date/number formatting, matching OxyPlot's axis label culture. | 0.85 | Locale-specific separators. | Test pins culture. |
| 9 | Copying the spec into `docs/tasks/` in this worktree is wanted (the user referenced it by that relative path). | 0.75 | Extra file in the branch. | Mentioned in the final report; trivially removable. |

No assumption is both < 0.7 and high-consequence, so none blocks.

## 3. Design

New App charting types (namespace `VramMonitor.App.Charting`), all independent of WPF; only the axis and
series types depend on OxyPlot:

- **`LogTimeScale`** (sealed class, immutable). Ctor `(DateTimeOffset endUtc, TimeSpan window)`; throws
  `ArgumentOutOfRangeException` unless `window > 0`. `const double TauSeconds = 60`. Members:
  `EndUtc`, `Window`, `AgeSeconds(ts)`, `IsFuture(ts)` (`ts > EndUtc`), `XForAge(double ageSeconds)`
  (throws for negative/NaN age), `ToX(DateTimeOffset ts)` (throws for future), `AgeSecondsAt(double x)`,
  `TimestampAt(double x)`. The inverse is **total for every finite x** (x > 1 yields a small negative age,
  i.e. a time just after `EndUtc`; x < 0 an age beyond W) because OxyPlot may evaluate labels marginally
  outside `[0, 1]` during layout, and a throw there would fault the UI thread; it throws only for NaN/∞. `L = ln(1 + W/tau)` precomputed; `Math.Log(1 + a/tau)` evaluated as
  `Math.Log1P`-equivalent via `Math.Log(1 + a / tau)` (no `Log1P` in BCL; precision is ample at a ≥ 0).
  Subtraction is on `DateTimeOffset` (UTC instant), so offsets do not matter (R-MAP-2).
- **`LogTimeTicks`** (static). `MinSeparation = 0.14`, `MaxInteriorTicks = 4`, `Candidates` ladder (R-TK-2).
  `For(TimeSpan window)` → `IReadOnlyList<TimeSpan>` of ages ordered by ascending X (oldest first,
  W … 0), and `UsesSeconds(IReadOnlyList<TimeSpan>)` → any interior age < 60 s. Pure function of W.
- **`LogTimeAxis : LinearAxis`**. Ctor takes the `LogTimeScale` and `TimeZoneInfo`. Sets `Key = TimeAxisKey`,
  `Position = Bottom`, `Minimum = 0`, `Maximum = 1`, `MinimumPadding = MaximumPadding = 0`,
  zoom/pan off, `Title = "Time (logarithmic age)"`, dotted major gridlines with today's colour,
  `MinorGridlineStyle = None`. Precomputes tick X values and labels once. Overrides
  `GetTickValues` (labels = ticks = precomputed X, minor = empty) and `FormatValueOverride` (label of the
  tick whose X equals x within 1e-9, else the inverse-mapped local time with the same format).
  Exposes `TickAges`/`TickPositions` for tests.
- **`TimestampedLineSeries : LineSeries`**. Holds the `SeriesPoint` sources parallel to `Points` and the
  `TimeZoneInfo`. Overrides `GetNearestPoint`: calls base; if non-null, resolves `(int)Math.Round(result.Index)`
  to its source, and rewrites `Text` = `"{Title}\n{local:yyyy-MM-dd HH:mm:ss zzz}\n{Y:0} MB"`, `Item` = source
  point. If `interpolate` is true, returns base result only when it is a non-interpolated hit; with
  `CanTrackerInterpolatePoints = false` base already returns null/nearest — verified in test.
- **`ChartBuilder`**: adds `public const string TimeAxisKey = "time"`; `Build(snapshot, selectedKey,
  expandedKeys, TimeZoneInfo? timeZone = null)` creates one `LogTimeScale(snapshot.TakenUtc,
  snapshot.Settings.HistoryWindow)` and passes it everywhere. `CreateSeries` skips future points
  (`scale.IsFuture`) and keeps everything else, including NaN and negative-X points; every series sets
  `XAxisKey = TimeAxisKey`; annotations set `XAxisKey = TimeAxisKey`, future markers skipped.
  `TrackerFormatString` is no longer relied on (left unset; the override supplies `Text`).
- `TimeAxisStep.cs` and `TimeAxisStepTests.cs` deleted (R-VE-2).

## 4. Steps

1. **Copy spec** into `docs/tasks/` of the worktree (done before planning; no content change). — R-DL-1
2. **`LogTimeScale`** in `src/VramMonitor.App/Charting/LogTimeScale.cs` as designed in §3. XML doc explains
   the formula, the offset's purpose, the preconditions and why negative X is kept. — R-MAP-1, R-MAP-2,
   R-MAP-3, R-MAP-6, R-MAP-7, R-MAP-8
3. **`LogTimeTicks`** in `src/VramMonitor.App/Charting/LogTimeTicks.cs` (§3). Comments carry the width-
   independence rationale migrated from `TimeAxisStep`/`ChartBuilder.AddAxes`. — R-TK-1, R-TK-2, R-TK-4, R-TK-6
4. **`LogTimeAxis`** in `src/VramMonitor.App/Charting/LogTimeAxis.cs` (§3). — R-CH-2, R-TK-3, R-TK-4, R-TK-5
5. **`TimestampedLineSeries`** in `src/VramMonitor.App/Charting/TimestampedLineSeries.cs` (§3). — R-CH-6, R-CH-7
6. **`ChartBuilder`** rewrite of `AddAxes`, `CreateSeries`, `AddTotalSeries`, `AddDisruptionMarkers` to use
   one scale + `TimeAxisKey`; keep palette, alpha/thickness, legend, PID dotted/`inLegend:false`, both value
   axes exactly as today. Update the class remarks, including that `snapshot.Settings` must already be
   `Validated()` (a zero/negative window now throws `ArgumentOutOfRangeException` from `LogTimeScale`
   instead of rendering — the contract `TimeAxisStepTests.A_degenerate_window_falls_back_to_the_finest_step`
   used to guard is deliberately replaced). In the same step, delete `TimeAxisStep.cs` **and**
   `TimeAxisStepTests.cs`, and rewrite `ChartBuilderAxisTests.cs` (step 10) so App.Tests compiles at every
   step boundary; steps 6 and 10 land together. — R-CH-1…R-CH-5, R-MAP-4, R-MAP-5, R-MAP-8, R-VE-2
6a. **Coalesced UI dispatch** (measured bottleneck, §1 baseline): new
   `src/VramMonitor.App/LatestSnapshotDispatcher.cs` — a small internal class holding the newest pending
   snapshot (`Interlocked.Exchange`) and an `int` "scheduled" flag. `Post(snapshot)` stores the snapshot
   first, then schedules only if `Interlocked.CompareExchange(ref scheduled, 1, 0) == 0`, so two posters
   can never both schedule. The dispatched callback first clears the flag (`Volatile.Write(ref scheduled,
   0)`), then takes the newest snapshot with `Interlocked.Exchange(ref latest, null)`, and **treats a null
   take as a no-op** — this is the expected outcome of the interleaving "callback clears flag → a post
   stores S2 and schedules again → this callback takes S2 → the extra callback finds nothing", and it must
   never reach `MainViewModel.Apply`/`TrayTooltipModel.Apply`, which both `ThrowIfNull`. Clearing before
   taking is what guarantees a post is never stranded. In the app the scheduling delegate is
   `Dispatcher.InvokeAsync(callback, DispatcherPriority.Background)`, so input (hover/tracker) and the
   `Render` pass are never queued behind a chart rebuild; today's default `Normal` outranks both. The
   scheduling delegate and the apply delegate are injected, so it is unit-testable without a dispatcher. `App.OnSnapshotPublished` keeps `TryRecoverLostAdapter` and
   `PersistFirstRunGpu` on the sampler thread exactly as today and replaces only the `InvokeAsync` block.
   Result: at most one queued UI operation at any time; a snapshot superseded before the UI ran is
   skipped (assumption 10). No change to sampling, analysis, retention or chart data. Tests in
   `tests/VramMonitor.App.Tests/LatestSnapshotDispatcherTests.cs`: N posts before the scheduled callback
   runs → exactly one schedule and one apply of the last snapshot; a post after the callback ran schedules
   again; a post arriving during apply is applied by a fresh schedule (not lost); a post arriving between
   the callback clearing the flag and taking the snapshot is applied exactly once, and the resulting extra
   callback finds nothing, applies nothing and does not throw; concurrent posts from
   several threads → never more than one pending schedule and the final applied snapshot is the last one
   published. — R-PF-1
7. **Sampling floor**: `MonitorSettings.MinSampleInterval = TimeSpan.FromSeconds(1)` (doc comment kept
   accurate); `SettingsWindow.xaml:42` hint → `How often the GPU is probed. Minimum 1 second.`;
   `README.md:40` → `minimum 1`; `Analyzer.cs:495` comment → "a 1 second interval" with the magnitude
   updated (≈ 46,800 retained samples for 12 h + 1 h grace × up to 3,600 grace instants ≈ 1.7 × 10⁸ comparisons per application per pass
   for a linear scan, i.e. billions across applications); `SamplerLoopTests.cs:20` comment;
   `.claude/skills/run-app/SKILL.md` minimum mention → 1 s. — R-SA-1, R-SA-3, R-SA-4
8. ~~Status line format~~ — dropped after review round 1 (current `{TotalSeconds:0}` already yields
   `every 1s`; fractional display is reported as a known gap).
9. **App tests — mapping** `tests/VramMonitor.App.Tests/Charting/LogTimeScaleTests.cs`:
   endpoints 0/1 for W ∈ {1 min, 60 min, 12 h}; age 0 finite; strict monotonicity over a dense sweep;
   forward/inverse round trips (timestamp and age) for the three windows; equal 1-s steps wider near the
   right than the left; 60-min references 0.169/0.436/0.674 (±0.001); same instant with `+00:00` and
   `+05:30` offsets → identical X; non-positive window throws; future timestamp throws from `ToX`
   (and `IsFuture` true); older-than-window → negative X, not clamped; inverse is total for finite x:
   `AgeSecondsAt(-0.01)` > W and `AgeSecondsAt(1.01)` < 0 without throwing, round-tripping through
   `XForAge`/`ToX` where the age is non-negative; NaN x throws. — R-MAP-1…R-MAP-9, R-VE-1
10. **App tests — ticks/axis** rewrite `ChartBuilderAxisTests.cs` + new `LogTimeTicksTests.cs`:
    - `LogTimeTicks.For(60 min)` = 0, 60, 300, 600, 1800, 3600 s; 1-min and 12-h windows produce known lists;
      every window 1…720 min yields ≤ 6 ticks, includes both endpoints, all pairwise X gaps ≥ 0.14, ascending X;
      interior ages are from the ladder.
    - Axis from `ChartBuilder.Build`: single `LogTimeAxis` with `Key == TimeAxisKey`, `Minimum 0`, `Maximum 1`,
      zero padding, zoom/pan off, title text; `GetTickValues` majors == labels == expected X, minors empty;
      label format `HH:mm` for 60 min, `HH:mm:ss` for 1 min; labels equal `TZ.Convert(end − age)` formatted;
      right-edge label is the snapshot's time.
    - Stability: tick X/labels identical across (a) two snapshots with the same `TakenUtc` but different
      plotted values, (b) selection/expansion changes, (c) `SvgExporter` layouts at 300 px and 1600 px wide;
      tick ages unchanged across two snapshots 1 s apart while labels advance.
    - Exact extent kept: `TimestampAt(0) == end − W` and `TimestampAt(1) == end` (replaces the old span test).
    — R-TK-1…R-TK-6, R-CH-2, R-VE-1, R-VE-2
11. **App tests — series/hover** new `ChartBuilderSeriesTests.cs` with a small fixture builder
    (`tests/VramMonitor.App.Tests/Support/SnapshotFixture.cs`) that hand-builds `ApplicationView`,
    `SessionView`, `TotalSeries`, `DisruptionMarker`:
    - app, PID, total and marker sharing a timestamp → identical X; all series `XAxisKey == TimeAxisKey`.
    - zero samples / one sample: axis still [0,1]; single point at X = 1.
    - a point after `TakenUtc` is absent from every series and markers; a NaN gap point older than
      `end − W` is kept with negative X and NaN Y.
    - NaN preserved for `Missing`/`Absent`; analysis-generated sampling-pause break near the left boundary,
      produced by `Analyzer.Analyze` over a real `GpuSample` history, stays `NaN` in the chart.
    - same timestamp at two cadences (`SampleInterval` 1 s vs 10 s, same `TakenUtc`/window) → same X.
    - hover: after `SvgExporter` layout, `GetNearestPoint(Transform(point), false)` on an older (non-zero age)
      point returns `Item` == source point, text with title, `yyyy-MM-dd HH:mm:ss zzz` local time and value;
      a screen point midway between two samples returns one of the two samples (no interpolated time);
      `interpolate: true` never yields an interpolated time; PID series tracker title includes `pid N`.
    - DST: `W. Europe Standard Time`, samples at 2026-10-25 00:30Z and 01:30Z render tracker text
      `02:30:00 +02:00` vs `02:30:00 +01:00`, X ordered by UTC.
    - preserved behaviour: selection dims others to alpha 55 / thickness 1.5 and keeps the selected at 2.25;
      PID series dotted, `RenderInLegend == false`; app series on `ApplicationAxisKey`, total on
      `TotalAxisKey` (right); marker colours/tooltips per kind.
    - selection/expansion rebuild against the same snapshot → identical X for every shared series.
    - `ChartBuilder.Build` with an unvalidated zero-window snapshot throws `ArgumentOutOfRangeException`
      (replaced degenerate-window contract, see step 6).
    — R-CH-1…R-CH-7, R-MAP-4, R-MAP-5, R-MAP-8, R-VE-1
12. **Core tests**:
    - `SettingsTests`: theory clamps `0`, `0.5 s`, `−3 s` → exactly 1 s; theory preserves 1, 1.5, 2, 10 s;
      10 min → 5 min; default 10 s; save/load 1 s; hand-written legacy JSON with `sampleIntervalSeconds: 2`
      loads as 2 s.
    - `CommandLineTests`: `--interval-seconds 1` → 1 s; `--interval-seconds 0.25` → 1 s.
    - `SamplerLoopTests`: `Settings(TimeSpan interval)` parameter. The settings-wake test uses an explicit
      4 s interval (the 1.5 s wait then sits ≥ 2.5 s before the next tick) and keeps its no-extra-probe
      assertion unchanged. `The_loop_keeps_sampling_across_many_ticks` keeps the 1 s floor and asserts
      ≥ 4 probes, the published `Settings.SampleInterval == 1 s`, every sample's `NominalInterval == 1 s`,
      and the median gap between consecutive sample timestamps lies in [0.5 s, 1.5 s] — a cadence the old
      2 s floor cannot produce. Sample timestamps come from the sampler's monotonic anchor, so the bound
      does not depend on how promptly the test's polling loop observes them. Replace the
      runtime-change test with one that goes 1 s → 3 s → 1 s, asserting sampling continues after each
      change, the earliest sample is still retained (history kept), and the newest sample's
      `NominalInterval` matches the active interval while older samples keep theirs.
    - Existing Analyzer gap/duration and Export tests unchanged and must pass. — R-SA-1, R-SA-2, R-SA-3,
      R-SA-5, R-SA-6, R-PF-2, R-VE-1
13. **Build + tests + analysis**: `dotnet build VramMonitor.slnx -c Debug`; three test exes; `-c Release`
    build; `roslynator analyze` (after `dotnet restore`) and `jb inspectcode -e=WARNING` — counts must stay at
    the CLAUDE.md baseline (32 / 13) or every new finding is fixed or justified. Update CLAUDE.md test counts.
    — R-VE-3
14. **Performance + screenshots harness** (scratchpad, Release, not committed): a console project
    referencing `VramMonitor.App` and `VramMonitor.Core`, building deterministic synthetic 1-s histories
    (60 min + 1 min grace; 12 h + 1 min grace), 60 applications with 3 apps × 4 PIDs, `ChartTopApplications`
    10 and 50, 3 expanded apps; a separate gap/marker fixture (failed probes every 5 min, partial samples,
    one 10-min sleep gap). Measures (warm-up + median of N): `Analyzer.Analyze` + visible copy (the
    publication path), `ChartBuilder.Build`, and `PngExporter` render at 1280×700 on STA; reports
    allocated bytes per op (`GC.GetAllocatedBytesForCurrentThread`) and working set. Also renders
    before/after chart PNGs from the same synthetic hour (before = master `a4c251c` build output in the
    scratchpad, after = this branch) and narrow-layout PNGs (≈ 210 px and 360 px plot widths) at 1, 60,
    720 min to check label collisions. **Dispatch backlog** is measured too: on an STA thread running a
    real WPF `Dispatcher`, a background timer publishes a snapshot every 1 s for 30 s. The UI side mirrors
    the app: a shown `Window` hosting an OxyPlot `PlotView`, whose `Model` is replaced with
    `ChartBuilder.Build(...)` on each apply, so rendering happens in WPF's own `Render` pass as in the app.
    The harness samples posted-but-not-yet-run operations each second **and** counts actual chart
    refreshes (`PlotModel.Updated` events raised by the `PlotView` render), once with the old
    per-snapshot `InvokeAsync` (Normal) and once through `LatestSnapshotDispatcher` (Background), for
    60 min top-10 and 12 h top-50; the report states the achieved refresh cadence per configuration.
    (A prototype of the backlog half on `a4c251c`, with build + PNG render inside the callback, already
    showed per-snapshot dispatch at 23 queued operations after 30 s and growing ≈ 0.77/s for 12 h top-50,
    versus ≤ 1 coalesced; 60 min top-10 never queued.) Targeted fixes are allowed anywhere in the publish → dispatch →
    build path when a measurement shows the bottleneck (step 6a is the one already justified); decimation,
    retention changes and pipeline rewrites stay excluded — configurations still over budget (expected:
    12 h analysis ≈ 1.3–1.4 s on the sampler thread) are reported, not hidden. `docs/HANDOVER.md` §9 risk
    item updated with the measured result. — R-PF-1, R-TK-6, R-DL-1
15. **Manual acceptance in the real app** (run-app skill): confirm no running instance first; back up
    `%APPDATA%\VramMonitor\settings.json`; launch with `--interval-seconds 1 --history-minutes 15`; after
    a few minutes screenshot (status `every 1s`, log chart, newest right); hover old/new points and an
    expanded PID line via UIA/mouse where possible; change window 1/60/720 min via `--history-minutes`
    relaunches or Settings; Settings Apply with interval 1, relaunch without override and confirm
    `every 1s` persisted; drag splitter to minimum; restore the backup. — R-SA-2, R-VE-4, R-TK-6
16. **Report**: results, perf table, screenshots, and 2–3 unverified gaps. — R-DL-1

## 5. Out of scope

Linear/log toggle, configurable tau, data beyond the history window, decimation, retention change,
zoom/pan, dependency upgrades.

## Review dispositions

### Round 1 (`logarithmic-time-axis.review-1.md`, verdict REVISE)

| # | Severity | Finding | Disposition |
| --- | --- | --- | --- |
| 1 | MAJOR | Step 14 restricted fixes to new code; spec allows targeted fixes incl. `App.xaml.cs` dispatch; measure backlog. | ACCEPTED — baseline added to §1 (12 h UI work ≥ 2.2 s per snapshot); new step 6a coalesces dispatch; step 14 now measures queue depth old vs new and widens the fix rule to the publish → dispatch → build path. |
| 2 | MINOR | Wrong `OnApply` citation. | ACCEPTED — §1 now cites `SettingsWindow.xaml.cs:50–76`. |
| 3 | MINOR | Inverse preconditions unspecified. | ACCEPTED — §3 states inverse is total for finite x; step 9 tests x = −0.01 / 1.01 and NaN. |
| 4 | MINOR | Degenerate-window contract silently changed. | ACCEPTED — step 6 remarks + step 11 test that `Build` throws for an unvalidated zero window. |
| 5 | MINOR | Deletion ordering breaks App.Tests between steps. | ACCEPTED — step 6 deletes both `TimeAxisStep` files and lands with step 10's rewrite. |
| 6 | MINOR | Status-line `0.##` is off-spec. | ACCEPTED — step 8 dropped; reported as a known gap. |
| 7 | MINOR | `Analyzer.cs:495` magnitude stale. | ACCEPTED — step 7 updates the magnitude. |

### Round 2 (`logarithmic-time-axis.review-2.md`, verdict REVISE)

| # | Severity | Finding | Disposition |
| --- | --- | --- | --- |
| 1 | MAJOR | Coalescer clear-then-take window yields a callback with a null take; both appliers `ThrowIfNull`; flag must be CAS. | ACCEPTED — step 6a specifies CAS on an `int` flag, clear-before-take ordering, null take = no-op, and a test for exactly that interleaving. |
| 2 | MINOR | Dispatcher priority unstated; harness should count real refreshes. | ACCEPTED — step 6a posts at `DispatcherPriority.Background`; step 14 harness hosts a real `PlotView` in a shown window and counts `PlotModel.Updated` refreshes per 30 s. |
| 3 | MINOR | Loop-cadence assertion also passes at 2 s. | ACCEPTED — step 12 asserts published interval and `NominalInterval` = 1 s and median timestamp gap in [0.5 s, 1.5 s]. |
| — | nit | Assumption 10 path. | ACCEPTED — full path below. |

### Round 3 (`logarithmic-time-axis.review-3.md`, verdict APPROVE)

| # | Severity | Finding | Disposition |
| --- | --- | --- | --- |
| 1 | MINOR | Settings-wake test must build both `Settings(...)` calls from the 4-s interval. | ACCEPTED — step 12: the `UpdateSettings(...)` in that test is `Settings(4 s) with { … }`. |
| 2 | MINOR | Clear the scheduled flag with `Interlocked.Exchange`. | ACCEPTED — step 6a uses `Interlocked.Exchange(ref scheduled, 0)`. |

### Post-approval amendment (evidence, not scope growth)

The step-14 backlog harness was upgraded (per round-2 finding 2) to host a real `PlotView` in a shown
window on `a4c251c`. Result over 30 s at 1 publication/s: 60 min top-10 and **12 h top-50 both refresh
30/30 with at most 1 queued op**; apply → `LayoutUpdated` median 16 ms / max 112 ms (60 min top-10) and
344 ms / 391 ms (12 h top-50). The earlier 23-op backlog came from the `PngExporter` proxy (bitmap
encoding inside the callback), which overstates on-screen render cost. The spec allows fixes only for
*measured* bottlenecks, so **step 6a becomes conditional**: it is implemented only if the same harness on
this branch shows queued ops growing or apply → layout ≥ 1 s for any measured configuration; otherwise it
is not implemented and the margin (and the remaining risk on slower hardware) is reported. Its approved
protocol stands unchanged if it is needed.

## Implementation notes (2026-10-10)

- Steps 1–7, 9–16 implemented. Step 8 dropped (round 1). **Step 6a not implemented**: on this branch the
  real-`PlotView` harness showed 30/30 refreshes, ≤ 1 queued op and apply → layout 315–325 ms for 12 h
  top-50 in both dispatch modes, so its measured-bottleneck condition was not met.
- One measured, targeted fix added in task code: `TimestampedLineSeries.EnsureCapacity` pre-sizes both
  point lists (12 h top-50 build: 100.5 ms / 378 MB → 37.5 ms / 125 MB).
- Results: Debug and Release builds 0 warnings; tests 49 + 111 + 36 = 196 pass (Core suite repeated 3×);
  `roslynator analyze` 32, `jb inspectcode -e=WARNING` 13 — both at the documented baseline.
- Found in manual testing, pre-existing on `a4c251c`, out of scope: the GridSplitter lets the list column
  push the chart column past its `MinWidth` / off-window. Flagged as a separate task.
