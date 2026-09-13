# VRAM Monitor — Design Specification

## 1. Purpose

Build a lightweight Windows 11 desktop application for personal use that monitors per-application VRAM consumption on an NVIDIA RTX 3090 and preserves a rolling in-memory history.

The application should answer one focused question:

> Which applications used VRAM, how much did they use, and for how long?

The application is not intended for public distribution and does not need generic cross-platform or multi-vendor GPU support in v1.

---

## 2. Primary goals

- Track per-application dedicated VRAM consumption over a rolling history window.
- Default history window: 60 minutes.
- Default sampling interval: 10 seconds.
- Keep all runtime history in RAM only.
- Minimize CPU/GPU/probing overhead.
- Show current top consumers from the Windows system tray.
- Provide a native desktop window for historical inspection.
- Distinguish application-level history from individual PID/session detail.
- Preserve missing observations as visible gaps rather than fabricating data.
- Support manual export for later inspection.
- Remain usable as a normal Windows user by default.

---

## 3. Platform and application shape

### 3.1 Technology direction

Use:

- C#
- .NET 10
- WPF
- Windows 11

The application should be a single desktop process.

Do not introduce:

- a Windows service,
- a database,
- a local web server,
- cloud dependencies,
- telemetry,
- automatic persistence of monitoring history.

### 3.2 Architectural components

Keep the following responsibilities isolated:

1. **GPU memory provider**
   - Retrieves per-process GPU memory measurements for a selected GPU.
   - Hides provider-specific implementation details.
   - Should expose a provider abstraction, conceptually similar to:

     `IGpuMemoryProvider.GetSnapshotAsync(gpuId)`

   - The rest of the application must not depend directly on Windows/NVIDIA-specific structures.

2. **Sampler**
   - Runs on the configured interval.
   - Requests a GPU snapshot.
   - Resolves process metadata separately from VRAM measurement.
   - Records probe timing, failures, skipped cycles, and late cycles.

3. **Rolling history store**
   - Stores observations only in RAM.
   - Retains samples inside the configured history window.
   - Removes expired samples incrementally.

4. **Analysis layer**
   - Aggregates PID/session data into logical applications.
   - Calculates current usage, averages, peaks, top-quartile participation, aggressive duration, ranking, hysteresis, and tray top-5.
   - Operates on ordinary domain objects and must be unit-testable without a real GPU.

5. **WPF shell/UI**
   - System tray integration.
   - Tray tooltip.
   - Main monitoring window.
   - Settings UI.
   - Charting.
   - Application/PID drill-down.
   - Manual export.

The UI must not know how VRAM is measured.

---

## 4. GPU scope

v1 presents a single selected GPU in the UI.

The internal data model must still carry GPU identity so multi-GPU support can be added later without redesigning the core model.

Primary target hardware:

- NVIDIA RTX 3090
- Windows 11

Provider selection should prefer zero-friction operation.

An optional enhanced-accuracy provider/mode is acceptable if it provides materially better measurements but requires extra dependencies, native interop, or privileges.

Normal-user operation remains the default.

---

## 5. Measurement semantics

### 5.1 Primary metric

Dedicated VRAM is the primary metric for:

- ranking,
- graphing,
- aggressive-consumer classification,
- tray top-5.

Shared GPU memory may be collected when available, but it is secondary diagnostic information only.

Shared GPU memory must not affect aggressive ranking.

### 5.2 Snapshot behavior

Each polling cycle creates a timestamped GPU observation.

A snapshot may be complete or partial.

If some measurements fail:

- keep successful measurements,
- preserve failures as missing observations,
- do not discard the whole snapshot unless the entire probe failed.

Never interpret missing data as zero.

Never carry forward the previous value to hide a gap.

### 5.3 Process metadata

Process metadata lookup is separate from VRAM acquisition.

Measurement data remains valid even if metadata resolution fails.

Identity fallback order:

1. full executable path when available,
2. executable name when path is unavailable,
3. PID-based fallback label when neither is available.

Do not discard a valid VRAM observation only because metadata lookup failed.

---

## 6. Application identity and process sessions

### 6.1 Default grouping

The normal UI is application-oriented rather than PID-oriented.

Application identity should primarily use the full executable path.

This avoids incorrectly merging unrelated processes such as different `python.exe` installations or environments.

The UI should display a friendly application name.

Exact path and process details belong in drill-down/details.

### 6.2 PID/session behavior

An application may contain multiple PIDs.

The normal chart shows one aggregated application line.

The application can be expanded to reveal PID/session lines.

If the same application exits and later starts again:

- application-level history remains one logical history with a gap,
- PID/session drill-down preserves the separate process lifetimes.

Normal application grouping and PID/session identity are therefore distinct concepts.

---

## 7. Retention model

History lifetime is controlled only by the rolling history window.

Default:

- 60 minutes

Configurable:

- yes

Every consumer remains available in the UI as long as at least one of its retained samples still exists inside the active history window.

This applies whether the consumer:

- stayed quiet for the entire window,
- had only one spike,
- was previously aggressive,
- stopped being aggressive several minutes ago,
- exited completely.

Aggressive classification must never control whether a consumer remains inspectable.

Once the last retained sample for a consumer ages out of the history window:

- remove its aggregate history,
- remove its PID/session history,
- remove it from the UI.

Retention is sample-driven, not classification-driven.

---

## 8. Monitoring floor

Only consumers above a configurable dedicated-VRAM floor participate in aggressive-consumer analysis.

The exact default may be selected during provider validation; an initial range around 50–100 MB is reasonable.

Important:

- the floor applies at analysis time,
- raw observations should still be retained when collecting them is cheap.

This allows threshold changes to re-evaluate existing in-memory history.

---

## 9. Aggressive-consumer classification

### 9.1 Comparison population

For every sample:

1. take applications whose dedicated VRAM is above the configured monitoring floor,
2. rank them by current dedicated VRAM,
3. mark the top quartile as qualifying for that sample.

The top quartile is relative to the current qualifying application population.

### 9.2 Aggressive duration

Aggressive duration is cumulative across the active history window.

It is not streak-based.

Example:

- qualifying for 1 minute,
- not qualifying for 20 seconds,
- qualifying again for 2.5 minutes,

counts as 3.5 minutes of aggressive time.

Default aggressive-duration threshold:

- 3 minutes

Configurable:

- yes

As old qualifying samples age out of the rolling history window, aggressive duration must decrease naturally.

### 9.3 Ranking aggressive consumers

Keep ranking explainable rather than using an opaque score.

Ordering:

1. cumulative aggressive time,
2. average dedicated VRAM during qualifying samples,
3. peak dedicated VRAM as final tie-breaker.

### 9.4 Demotion hysteresis

An application should not move out of the aggressive section immediately when it stops qualifying.

Use a configurable demotion grace period expressed in real time, not sample count.

The grace period must remain stable even if the sample interval changes.

---

## 10. Tray behavior

The application normally lives in the Windows system tray.

### 10.1 Tray tooltip

Hovering the tray icon shows the current top-5 VRAM consumers.

Each entry contains:

- application name,
- current dedicated VRAM.

The ranking is based primarily on current dedicated VRAM.

Apply simple hysteresis/smoothing so tiny fluctuations do not constantly reorder the top-5.

The tooltip should remain compact.

### 10.2 Tray interaction

Clicking the tray icon opens the main application window.

The application should support:

- manual launch by default,
- optional Start with Windows behavior.

No background Windows service is required.

---

## 11. Main window

The main window contains two primary areas:

1. consumer list,
2. VRAM history chart.

### 11.1 Consumer list

Split the list into:

- **Aggressive consumers**
- **Other consumers**

`Other consumers` means every retained consumer that is not currently classified as aggressive.

It includes:

- previously aggressive consumers,
- quiet consumers,
- consumers that only spiked,
- exited consumers whose samples are still inside the history window.

Each default application row shows:

- application name,
- current dedicated VRAM,
- average dedicated VRAM,
- peak dedicated VRAM,
- cumulative aggressive time.

Additional details such as:

- PID count,
- exact executable path,
- shared GPU memory,
- process/session timing,

belong behind expansion/details.

### 11.2 Selection and drill-down

Clicking any retained consumer must be possible regardless of current aggressive status.

Selecting an application:

- highlights its aggregate graph line,
- dims other application lines.

Expanding it:

- reveals its PID/session details,
- overlays the relevant PID/session graph lines.

There should be no separate diagnostic window unless a future need justifies one.

---

## 12. Chart behavior

### 12.1 Primary series

X-axis:

- time

Left Y-axis:

- per-application dedicated VRAM

Application lines use the left Y-axis.

### 12.2 Overall VRAM context

Also show total GPU VRAM usage as contextual information.

Use:

- a separate right Y-axis,
- its own scale,
- a faint visual style,
- a dotted or dashed line.

The total-VRAM series must not visually suppress lower per-application values.

It is contextual, not a competing consumer series.

### 12.3 Interaction

v1 chart interaction should stay simple:

- hover values,
- legend,
- show/hide series,
- application selection/highlighting,
- PID/session expansion.

Out of scope for v1:

- zoom,
- pan,
- time-range brushing,
- event annotation systems,
- rich GPU dashboards.

---

## 13. Observation gaps and disruptions

Observation gaps are first-class information.

### 13.1 Missing data

If a measurement is missing:

- break the affected line series,
- do not interpolate across the missing interval,
- do not draw a line connecting the last known value before the gap to the next known value after it.

The graph should visually resemble a split/discontinuity.

### 13.2 Probe-failure markers

At a failed or partial probe timestamp, show a subtle disruption indicator.

Possible representation:

- a small neutral marker on the time axis,
- or a very subtle vertical marker.

Hover text can indicate:

- `Probe failed`
- `Partial sample`

### 13.3 Whole-probe failure

If the entire probe fails:

- all relevant application series have a gap,
- one common disruption marker is enough.

### 13.4 Partial failure

If only some process/application measurements fail:

- only the affected series have gaps,
- successfully measured series continue normally,
- the probe timestamp is marked as partial.

### 13.5 Normal process exit

A normal process exit is not an observation error.

When an application/process stops:

- its line ends normally,
- no error marker is shown.

If the application later starts again:

- a new line segment begins,
- the application aggregate remains the same logical application history,
- PID/session drill-down shows separate process lifetimes.

---

## 14. Settings

Use a small native settings UI backed by a local JSON configuration file.

Store configuration in an appropriate per-user AppData location.

Initial configurable settings:

- sample interval,
- history window,
- VRAM monitoring floor,
- aggressive-duration threshold,
- demotion grace period,
- Start with Windows,
- selected GPU,
- provider/accuracy mode when applicable.

Defaults:

- sample interval: 10 seconds,
- history window: 60 minutes,
- aggressive-duration threshold: 3 minutes,
- monitoring floor: finalize during provider validation.

Settings that can be applied safely at runtime should take effect immediately.

Examples:

- history-window changes,
- threshold changes,
- grace-period changes.

Changing such settings should re-evaluate retained history rather than require a restart.

Command-line arguments may override persisted configuration for the current launch only.

---

## 15. Monitor health

Expose lightweight monitor health information only.

Show:

- selected GPU,
- current sample interval,
- last successful sample time,
- probe error count.

Internally track:

- probe duration,
- skipped cycles,
- late cycles.

Do not build a general self-profiling subsystem.

The purpose is simply to verify that monitoring remains healthy and lightweight.

---

## 16. Error handling

The app should degrade gracefully.

### 16.1 Per-process errors

Examples:

- process exits during lookup,
- permission issue,
- metadata resolution failure,
- provider returns incomplete data.

Behavior:

- retain successful measurements,
- preserve missing measurements as gaps,
- avoid user-facing dialogs for routine races.

### 16.2 Whole-probe errors

If an entire polling cycle fails:

- record the failure,
- increment the error count,
- preserve previous retained history,
- wait for the next scheduled probe.

Do not spam notifications.

### 16.3 Blocking startup errors

A real blocking condition such as:

- no compatible GPU,
- provider initialization failure,
- missing required dependency,

may be surfaced once through the tray/main window with a concise actionable message.

---

## 17. Manual export

Runtime history remains RAM-only.

Provide explicit manual export.

Export captures the entire retained history window, not only aggressive consumers.

Monitoring should continue while export is produced.

### 17.1 JSON

JSON is the canonical lossless format.

It should preserve enough information to reconstruct the session:

- GPU metadata,
- relevant settings,
- timestamps,
- application identity,
- PID/session identity,
- dedicated VRAM,
- shared GPU memory when available,
- missing observations/gaps,
- derived metrics,
- aggressive-duration data.

### 17.2 CSV

CSV is provided for convenient analysis.

Prefer:

- one row per sample/PID observation,
- enough identity columns to regroup by application/session.

If application-level derived summaries are exported as CSV, keep them in a separate file rather than mixing row types.

---

## 18. Performance constraints

Lightweight probing is a core requirement.

The provider implementation must be validated by measuring:

- probe latency,
- CPU overhead,
- sampling stability.

Do not assume a provider is acceptable merely because it returns correct data.

Prefer cheap probing APIs.

Analysis and aggregation must run off the WPF UI thread.

The sampling loop must not manipulate WPF objects directly.

The UI should receive already-prepared state/snapshots.

Do not prematurely optimize the rolling store into a complex data structure. At a 10-second interval, a 60-minute window is only 360 sampling points per hour.

---

## 19. Testing strategy

Most behavior must be testable without a physical GPU.

### 19.1 Unit-test focus

Provide strong unit coverage for:

- rolling-window cleanup,
- consumer lifetime,
- executable-path grouping,
- multiple PIDs per application,
- PID/session restarts,
- metadata fallback,
- missing-sample semantics,
- monitoring-floor filtering,
- per-sample top-quartile selection,
- cumulative aggressive duration,
- window aging,
- demotion grace period,
- aggressive ranking/tie-breaks,
- tray top-5 hysteresis,
- retained non-aggressive consumers,
- app retention after process exit,
- cleanup when final sample ages out,
- JSON export correctness,
- CSV export correctness.

### 19.2 Integration testing

Use a smaller number of integration tests for:

- the actual GPU provider,
- Windows process metadata,
- configuration persistence,
- startup integration where practical.

### 19.3 UI testing

Keep WPF UI logic thin enough that a large UI automation suite is unnecessary for v1.

Use focused validation for:

- tray behavior,
- main-window opening,
- chart rendering,
- selection,
- expansion,
- gap rendering,
- settings application.

---

## 20. Out of scope for v1

Do not add unless implementation uncovers a genuine requirement:

- temperature monitoring,
- power monitoring,
- GPU clocks,
- GPU utilization dashboards,
- alerts,
- notifications for threshold crossings,
- remote access,
- cloud storage,
- telemetry,
- persistent automatic monitoring sessions,
- generic multi-GPU visualization,
- cross-platform support,
- support for non-NVIDIA GPUs,
- a Windows service,
- a database,
- zoom/pan-rich chart exploration,
- complex event annotations.

---

## 21. Autonomous implementation workflow

This project is also an experiment in end-to-end autonomous implementation with Claude Code.

The success criterion is not only that the VRAM monitor works.

It also tests whether Claude Code can take a sufficiently precise specification and carry a medium-sized Windows application from plan to validated handover with no human intervention after the autonomy boundary.

### 21.1 Human interaction allowed before Critic

Before Critic review begins, Claude Code may:

- read the specification,
- inspect the repository/environment,
- ask the human clarifying questions,
- surface assumptions,
- research implementation options,
- prepare the initial implementation plan,
- show the plan to the human.

Normal interaction is allowed during this phase.

### 21.2 Autonomy boundary

The autonomy boundary begins when the human instructs Claude Code to submit/send the initial plan to Critic.

From that moment onward:

> Claude Code must continue autonomously through Critic review, plan revision, implementation, testing, debugging, validation, and final handover.

No routine HITL interaction is expected after Critic begins.

### 21.3 Planner → Critic behavior

After the autonomy boundary:

1. send the plan to Critic,
2. analyze Critic findings,
3. revise the plan when findings are material,
4. resolve disagreements through technical reasoning and available evidence,
5. continue until the plan is sufficiently strong to implement.

Critic findings are not a reason to escalate to the human.

### 21.4 Autonomous implementation

Claude Code should then:

- implement the application,
- build it,
- run it,
- create and run tests,
- diagnose failures,
- fix failures,
- rerun relevant tests,
- validate the real application behavior,
- verify the implementation against this specification.

Implementation uncertainty, failing tests, ordinary technical trade-offs, or code-review findings are not reasons to ask the human.

Claude Code is expected to make reasonable technical decisions itself.

### 21.5 External blockers

Human escalation after Critic begins is reserved for a genuine external blocker that cannot be resolved technically or safely.

Examples may include:

- missing credentials that are strictly required,
- a required permission only the human can grant,
- unavailable hardware,
- an irreversible/destructive action requiring explicit authorization,
- missing information that cannot be inferred, researched, or safely defaulted.

Escalation should not be used for:

- ordinary ambiguity,
- implementation decisions,
- test failures,
- Critic feedback,
- debugging,
- choosing between libraries,
- fixing build issues,
- non-destructive local setup decisions.

### 21.6 Safety boundaries

Autonomy does not permit Claude Code to:

- expose secrets,
- print `.env` contents,
- reveal .NET user secrets,
- commit secrets,
- make unrelated system changes,
- perform destructive or irreversible actions solely to avoid asking for permission.

### 21.7 Final handover

Claude Code should report back only when the application is ready for human handover or when a genuine external blocker prevents completion.

The handover report should include:

- what was built,
- important architectural decisions,
- meaningful deviations from this specification,
- build status,
- unit-test results,
- integration-test results,
- runtime validation performed,
- GPU-provider validation performed,
- known limitations,
- unresolved risks,
- exact instructions for launching the application,
- any setup required from the human,
- suggested manual acceptance checks.

The handover should make clear whether Claude Code considers the application ready for direct human testing.

---

## 22. Acceptance criteria

The implementation is ready for handover when all of the following are true:

1. The application runs as a native WPF Windows application.
2. It can remain in the system tray.
3. It samples per-process dedicated VRAM on the configured interval.
4. It aggregates processes into applications by executable identity.
5. PID/session drill-down is available.
6. The rolling history window is configurable and defaults to 60 minutes.
7. Consumers remain inspectable until their last sample ages out.
8. Missing observations are represented as gaps, never as fabricated zero/carry-forward values.
9. Probe disruptions are visible on the graph.
10. Normal process exits are visually distinct from probe failures.
11. Aggressive-consumer classification follows the top-quartile cumulative-time rules.
12. Aggressive ranking follows the approved explainable ordering.
13. Aggressive demotion uses configurable time-based hysteresis.
14. The tray shows a stable top-5 current-consumer view.
15. The main window separates Aggressive consumers from Other consumers.
16. Any retained consumer can be selected and inspected.
17. The graph shows application VRAM on the left Y-axis.
18. Total VRAM is shown contextually on a separate right Y-axis with a faint dotted/dashed style.
19. Manual JSON and CSV export works for the retained history.
20. Configuration persists locally.
21. Command-line overrides work for the current launch where implemented.
22. Probe health and basic monitor status are visible.
23. Core analysis behavior is covered by automated tests.
24. The real provider path has been validated on Windows where environment access permits.
25. Probe overhead has been measured and judged acceptable.
26. Claude Code has completed Critic review, implementation, testing, debugging, validation, and handover autonomously after the defined autonomy boundary.
