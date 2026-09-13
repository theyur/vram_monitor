# VRAM Monitor — handover

Built autonomously from `docs/vram-monitor-design-spec.md` after the autonomy boundary defined in §21.2.

**Status: ready for direct human testing.** Everything builds clean, 131 automated tests pass, and the
application has been run against the real RTX 3090 with live data, injected failures and a sustained soak.
The gaps that remain are listed in "Not validated" and are all things that need a human at the keyboard or
hardware I do not have.

---

## 1. What was built

A single WPF desktop process on .NET 10 that samples per-application dedicated VRAM, keeps a rolling
in-memory history, and presents it from the tray and a main window.

```
src/VramMonitor.Core/      domain, rolling store, analysis, export, settings   (plain net10.0)
src/VramMonitor.Windows/   PDH provider, DXGI enumeration, process metadata    (net10.0-windows)
src/VramMonitor.App/       WPF shell, tray, charting                           (net10.0-windows)
tests/VramMonitor.Core.Tests/      96 tests, no GPU required
tests/VramMonitor.Windows.Tests/   35 tests, hardware-dependent ones against the real machine
```

No service, no database, no web server, no telemetry, no automatic persistence. History is RAM-only.

---

## 2. Architectural decisions that shaped everything else

**The provider had to change before anything else could be planned.** NVML and `nvidia-smi` cannot report
per-process VRAM on a GeForce card: under WDDM, which a consumer card cannot leave, `nvidia-smi` prints
`N/A` for every process. This was measured on the target machine, not assumed. The working source is the
Windows performance counters `GPU Process Memory` and `GPU Adapter Memory` — what Task Manager itself uses —
which also need no elevation. Those are read through **PDH (Performance Data Helper)**, the Windows
counter-consumption API in `pdh.dll`; "PDH" below always means that path.

**`VramMonitor.Core` targets plain `net10.0` on purpose.** It physically cannot reference WPF, PDH or DXGI,
so the spec's rule that the UI must not know how VRAM is measured is enforced by the compiler rather than by
convention.

**The analysis layer is a pure function** of `(history, sessions, settings, previousTray, now)`. That is what
makes a settings change re-evaluate history already in memory instead of needing a restart, and what lets
nearly the whole specification be tested without a GPU.

**The store keeps two horizons.** It retains `HistoryWindow + DemotionGracePeriod`, but only the visible
window defines which consumers exist and every figure displayed. The older tail is read solely by the
demotion-hysteresis predicate, which can keep a consumer in the aggressive list but can never introduce one.
Without the extra tail, demotion by aging is undetectable; without the subset rule, a consumer could appear
in the aggressive list after the spec says it must be gone.

**`ProcessSessionId` is an opaque monotonic id**, frozen at first sight. Observations reference only the id,
so a late identity upgrade never rewrites recorded history.

**Sample timestamps come from a wall-clock anchor plus elapsed monotonic time**, with the anchor only ever
moved forward. That is what makes them non-decreasing: a backward time-service correction is ignored rather
than followed, and the sleep-gap detector, the duration weighting and the skipped-cycle counter all measure
the same quantity. Re-anchoring backwards was the round-4 blocker; see §11.

---

## 3. Deviations and interpretations

1. **No optional enhanced-accuracy provider (spec §4).** There is no viable candidate: NVML is strictly
   worse here, not better. §4 phrases the feature permissively. `IGpuMemoryProvider` keeps the door open.
2. **Total VRAM is read from the adapter counter, never summed from applications.** Measured on an idle
   desktop: Σ per-process = 9502 MB against an adapter total of 7601 MB, because Windows attributes a shared
   surface to every process referencing it. Per-application values are reported raw, matching Task Manager;
   reconciling the difference would mean inventing data. This is explained in the window's header.
3. **Quartile floor of one.** With 1–4 applications above the monitoring floor, exactly one is always marked,
   so on a lightly loaded desktop the single biggest application becomes aggressive after the threshold.
   Defensible under §9.1 but not stated there.
4. **A third marker kind, `Sampling paused`,** beyond §13.2's `Probe failed` and `Partial sample`. A sleep
   gap is neither a failure nor a partial probe, and labelling it as one would misreport it.
5. **Monitoring floor defaults to 100 MB**, chosen from the measured distribution as §8 asks: it separates
   real consumers (601/475/411/385 MB) from noise (99/68/64 MB).

---

## 4. Build and test status

| Check | Result |
|---|---|
| `dotnet build -c Debug` | clean |
| `dotnet build -c Release` | clean, **0 warnings** (warnings are errors repo-wide) |
| `dotnet test` (Debug) | **131 passed, 0 failed, 0 skipped** |
| `dotnet test -c Release` | **131 passed, 0 failed, 0 skipped** |

96 unit tests cover every bullet of spec §19.1, including the spec's own worked example from §9.2
(3.5 minutes of cumulative aggressive time) and CSV correctness under a forced `uk-UA` culture.

35 integration tests ran with **zero skips**, those needing hardware against the real RTX 3090, so the GPU path genuinely executed:
DXGI enumeration, counter availability, a real probe, probe cost, the stale-LUID failure path, live instance
parsing, process metadata including an access-denied process, and the autostart registry round-trip.

---

## 5. Runtime and provider validation performed

**The application was run.** Screenshots confirmed the tray icon, the main window, the Aggressive/Other
split (5 aggressive, 44 other — the top quartile of those above the floor), the five columns, the chart with
per-application lines on the left axis and a faint dashed total on an independent right axis, the legend, and
the health status bar. Values matched Task Manager and `nvidia-smi` for named processes.

**CUDA workload.** A 3072 MiB PyTorch allocation appeared as 3295.5 MiB of dedicated VRAM (tensor plus CUDA
context), and `nvidia-smi`'s total moved by +3301 MiB — agreement within about 12 MiB. This matters because
the idle-desktop sample says nothing about the workload the tool exists for.

**Probe cost (spec §18, AC-25), measured through the real `SamplerService`:**

| | |
|---|---|
| First cycle (one-time PDH initialisation) | 215 ms |
| Warm cycle: probe + metadata + store + analysis + publish | median **0.94 ms**, p95 1.46 ms, max 2.29 ms |
| At the default 10 s interval | **0.0094 % of one core** |
| PDH call alone, 9000 consecutive probes | median 0.082 ms, handles +6 total, working set flat, **zero allocations per probe** |

**Six-minute soak of the whole pipeline, 181 cycles at a 2 s interval with a deliberately short 60 s window
so eviction ran constantly:**

| Cycle | 30 | 61 | 91 | 121 | 151 | 181 |
|---|---|---|---|---|---|---|
| Handles | 309 | 306 | 306 | 300 | 303 | 306 |
| Working set (MB) | 46.8 | 60.4 | 58.1 | 57.6 | 57.8 | 57.7 |
| Retained samples | 30 | 30 | 30 | 31 | 30 | 30 |

Handles and memory are flat, garbage collection over the whole run was 3 gen-0 and 2 gen-1 collections with
no gen-2, and cadence was perfect: **0 failed, 0 partial, 0 late and 0 skipped cycles**. The retained-sample
count held at 30 while 181 samples were taken, which is the rolling window actually evicting rather than
growing.

**Failure handling, injected into the real pipeline against live data:** 10 failed probes and 4 partial
samples produced exactly 10 `Probe failed` and 4 `Partial sample` markers and matching health counters. A
provider that *threw* was caught, recorded as a failed sample, and sampling continued. All 49 consumers
remained listed and the largest application's peak was preserved. Its series showed 21 measured and 14
missing points with **zero fabricated zeros**.

**Export, against real GPU data:** JSON with schema version, GPU metadata, settings, health, sessions and
every sample; `-observations.csv` (one row per sample and process) and `-applications.csv` (derived
summaries) as separate files. Missing measurements are `null` in JSON and empty in CSV. Cumulative seconds
exported as `90.006` — an invariant decimal point on a machine whose locale uses a comma.

**Identity fallback in production:** of 58 live sessions, 54 resolved to a full executable path and 4 to an
executable name only (`dwm`, `csrss` and other protected processes). Those 4 carry a `name:` key and an empty
path in the export, exactly as designed.

**Two defects were found by running it and fixed:**
- The health display showed `probe 0,0 ms` because the monotonic clock used `Environment.TickCount64`, whose
  ~15 ms granularity is coarser than a whole sampling cycle. Switched to the high-resolution performance
  counter, which also keeps advancing across standby.
- The chart's X axis repeated labels on a short history window. It now switches to `HH:mm:ss` below ten
  minutes.
- A usability gap was also closed: if the aggressive threshold is not shorter than the history window, no
  application can ever accumulate enough qualifying time. Both values are legal individually, so the window
  now warns rather than silently showing an empty Aggressive list.

---

## 6. Running it

```bash
dotnet run --project src/VramMonitor.App
```

Or after `dotnet build -c Release`, launch
`src/VramMonitor.App/bin/Release/net10.0-windows/VramMonitor.exe`.

No elevation, no installation. It starts in the tray with the window open; closing the window hides it to the
tray, and **Exit** on the tray menu quits.

Useful for a quick look, since the defaults take an hour to fill the window:

```bash
dotnet run --project src/VramMonitor.App -- --interval-seconds 2 --history-minutes 5 --aggressive-minutes 1
```

Full argument list is in `README.md`. Settings live at `%APPDATA%\VramMonitor\settings.json`.

### Setup required from you

None. .NET 10 is already installed, the performance counters are present, and nothing needs admin rights.

---

## 7. Suggested manual acceptance checks

These are the things I could not verify without a human at the keyboard:

1. ~~**Tray tooltip on hover.**~~ **Done, and it was broken** — see section 13. Offscreen rendering said the
   control was correct, which it was; what was wrong was that the shell never displayed it. The fallback I
   was relying on, plain `ToolTipText`, was the very thing that hid the defect: it rendered instead, so the
   tooltip looked like it worked.
2. **Tray left-click** opens the window; **Exit** quits.
3. **Settings dialog**: change the history window, the threshold and the chart size, click Apply, and confirm
   the lists and chart re-evaluate immediately without a restart.
4. **Start with Windows**: toggle it on, confirm the entry under
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, then toggle it off. The registry manager is
   integration-tested, but the checkbox path is not.
5. **Export…**: the file dialog path. The exporters themselves are tested against real data.
6. **Selection and expansion**: click a consumer (its line highlights, others dim), expand it (per-process
   detail appears and PID lines overlay). Selection is **done** — and was broken; see section 13. Expansion
   is still unverified.
7. **A real workload**: start a training run or a game, confirm it climbs the list and becomes aggressive,
   then close it and confirm its line ends without an error marker and it stays inspectable until its last
   sample ages out.
8. **Sleep/resume**: suspend the machine and resume. Expect a `Sampling paused` marker and a genuine break in
   every series, with no line drawn across the gap. I could not induce this safely.

---

## 8. Known limitations

- Per-application values can legitimately sum above the total line; this is how Windows attributes shared
  surfaces, and normalising would fabricate data.
- **Anti-cheat protected (PPL) games were not available to test.** They deny a process handle, so their path
  falls back to executable name and their session identity relies on the absence rule. This is the least
  validated real-world path, and it matters because such games are heavy VRAM consumers.
- Multi-partition (`phys_1+`) and linked-display-adapter GPUs cannot be tested on this hardware; the
  sum-across-partitions rule is unit-tested only.
- Growing the history window at runtime cannot resurrect already-evicted samples. Growing the window or the
  grace period leaves a bounded transient during which an application can be demoted slightly early.
- The first sampling cycle costs about 215 ms of one-time PDH initialisation.
- Sub-floor observations are retained but the monitoring floor is applied at analysis time, so changing it
  re-evaluates existing history — as intended, but it means the Aggressive list can change retroactively.

## 9. Unresolved risks

- **Long-run stability beyond a few hours is projection, not measurement.** The PDH layer is proven flat over
  9000 probes and the full pipeline over a 181-cycle soak with handles and memory flat, but nothing has run
  for a day.
- Per-item `Partial` statuses never occurred naturally in any run, so their real-world frequency is unknown.
  If they turn out to be common, the chart could accumulate `Partial sample` markers.
- The chart is rebuilt from scratch each cycle. At the default 10 s interval with ten series this is
  comfortable; at a 2 s interval with a 12-hour window it has not been profiled.

---

## 10. Post-implementation audit

Round 3 was the last Critic pass, so its four MAJOR findings went into the plan and the code without another
review. Those fixes were re-audited afterwards against the plan text. **One of the four was implemented
incorrectly.**

**N-1 was wrong in the code.** The plan says an unreadable `GPU Adapter Memory` array should degrade the
sample to `Partial` with no total *and skip the adapter-presence check*. The implementation set `Partial` and
then fell straight into the presence check anyway — which, finding no instance for the selected LUID because
nothing had been read, returned a **failed probe claiming the GPU had disappeared** and triggered a DXGI
re-resolve. So a transient glitch in the *context* series would have failed the whole probe and reported the
card as gone. This is the same over-correction N-1 itself warned about, reintroduced one layer down.

The fix distinguishes three states per array — never added, read failed, read succeeded — because "our LUID
is not in the array" and "the array could not be read" are different facts and only the first is evidence the
adapter went away. The snapshot-assembly logic was extracted into a pure method so every failure combination
is testable without a GPU, and 12 tests now cover them.

The audit also found coverage gaps rather than defects:

- **`TrayRanking` had no tests at all** — about 100 lines implementing spec §19.1's "tray top-5 hysteresis"
  bullet, which the plan claimed was covered. 12 tests added; all passed first run, so the logic was right,
  merely unverified.
- **The real sampling loop was never exercised by tests.** Every sampler test used a single-cycle seam that
  bypasses `RunAsync`, so the timer, the settings channel and their interaction were covered only by the
  soak — which never changed a setting. 5 tests added that drive the real loop, including a runtime interval
  change and a settings change taking effect inside one interval.
- **No test pinned peak and average to the visible window**, so a grace-tail spike inflating a displayed peak
  would not have been caught. One test added.

Test count went from 81 to **111** as a result. The remaining round-3 items (N-2, N-3, N-4 and the minors)
were verified as correctly implemented and already had tests.

---

## 11. Round 4 — the code reviewed, not the plan

Rounds 1–3 reviewed the plan. Round 4 was run specifically against the **implemented code** for the round-3
findings, because those had been written and shipped with nothing checking them, and because the self-audit in
§10 was performed by the same author who wrote the bug it found.

It returned **one BLOCKER and six MAJOR findings.** The self-audit had been insufficient.

**The blocker was in N-2 — the finding §10 claimed was closed.** `NextTimestamp` re-anchored on a *backward*
wall-clock step, leaving the anchor behind the newest stored sample. The one-tick clamp patched only that
single return; every later cycle then derived a timestamp hours before the stored history, the store rejected
the append, and the loop counted a failure and republished stale analysis — **on every tick until real time
caught up with the size of the correction.** A three-hour NTP correction would have stopped monitoring for
three hours while the UI showed a frozen picture.

It survived because my test pumped exactly one cycle after the step, which is the only cycle that worked.
The test now runs several, asserts monotonic spacing and zero failures, and I verified by mutation that
removing the fix makes it fail.

The other majors, all fixed:

| Finding | Defect |
|---|---|
| Adapter recovery | `Failed` was raised on a lost adapter but nothing ever re-resolved it — after a driver reset the app would show a probe error every tick until restarted. Also, `GpuSelector` equality includes the enumeration ordinal, so an order shift would have been read as a different GPU and wiped history |
| `Assemble` residual 1 | An unreadable adapter *item* produced `TotalDedicatedBytes = 0` — a fabricated zero on the series the chart plots |
| `Assemble` residual 2 | Adapter unreadable + no matching processes published `Partial` with no observations, rendering as every application exiting at once |
| Threading | `SetGpu` ran on the UI thread, clearing the store and tracker while the loop appended to and enumerated them — exactly what plan §5.5 forbids. GPU changes now go through the same command channel as settings |
| Complexity | `cum(A, t)` was O(n) despite the plan claiming O(1): prefix sums existed, but the index lookups were linear scans. Now binary search |

Writing the missing tests also uncovered a bug of its own: **`--gpu 3090` did not work.** It parses as an
integer, so it searched for adapter *ordinal* 3090 and silently found nothing — and that exact invocation is
the example in `README.md`. An ordinal match is now tried first but no longer final.

The Critic was also asked directly whether any *other* shipped code contradicted its own stated contract, as
N-1 had. It found four, all now fixed: the "non-decreasing by construction" remark above the timestamp bug;
a comment saying a missing part must keep the whole value missing, sitting beside code that summed only the
readable adapter partitions; a comment saying an empty process list means a failed read, beside code that
published that empty list; and the O(1) claim.

Test count went **81 → 111 → 131.** The honest summary of the arc: my confidence tracked test coverage rather
than correctness, and every defect found in rounds 3–4 lived in code that had no test pinning it.

---

## 12. Process note

Three Critic rounds produced **40 findings; all 40 were accepted, none declined.** Round 1 found a genuine
blocker: the demotion-hysteresis rule as first written was a monotone prefix sum and could never fire, so
demotion was instant. Rounds 2 and 3 caught four classes of defect worth recording, because each was a rule
that looked reasonable in isolation:

- aggregation that summed only the *readable* processes of a partly-unreadable application, silently treating
  a missing measurement as zero;
- an absence rule that counted a *failed probe* as evidence every process had vanished;
- a persisted GPU LUID, which Windows reallocates on every boot;
- a metadata retry policy that would have run a 23 ms process scan on every probe forever, because three
  system processes are permanently unreadable and permanently present.

The full reports and dispositions are in `.claude/plans/`, including the four Critic reports.

---

## 13. What the manual checks found

Sections 1–12 were written before any human had used the application interactively. Two of the manual checks
in section 7 were then run — the tooltip and selection — and **both found a defect**. A third defect, the
mouse wheel, turned up in ordinary use and was not on the list at all. All three are fixed and verified
against the running application.

**The tray tooltip never appeared.** Hovering showed only the fallback string. H.NotifyIcon 2.4.1 does not
clear `UseStandardTooltip` when a custom `TrayToolTip` is resolved, so the icon keeps `NIF_SHOWTIP`; the
shell then draws `ToolTipText` itself and stops sending `NIN_POPUPOPEN`, the message the WPF popup opens on.
Upstream fixed this after 2.4.1. The flag is now cleared before the icon is created.

**A selected application could not be deselected.** Nothing ever assigned `SelectedKey` null, so the chart
stayed dimmed for the rest of the session. Behind it sat a second defect: each snapshot replaces both row
collections, so the list-box selection was dropped every interval and the highlighted row vanished while the
chart stayed dimmed — a selection whose effect was visible but whose cause was not. Clicking the selected row
again, Escape, and a **Show all lines** button now clear it, and the highlight is restored after each rebuild.

**The mouse wheel did not scroll the consumer lists.** Both lists sit in the `StackPanel` of one outer
`ScrollViewer`, so they are laid out at full height and never scroll themselves, yet their own `ScrollViewer`
still marks the wheel event handled. The wheel is now taken in the tunnelling phase and forwarded to the
enclosing viewer.

The tooltip also gained the adapter total as a last row, replacing a "click to open" hint.

### What this says about the validation in sections 4 and 5

Every one of these is a UI-layer defect, and the UI layer is the one surface with no automated tests — a
deliberate choice recorded in the plan, on the grounds that it holds no logic. That reasoning was sound and
the outcome still poor: three of the first three checks run by a human failed. The tooltip case is the
sharpest, because section 7 recorded the control as rendering correctly offscreen. It did. The rendering was
never the part that was broken, and offscreen rendering could not have told me so.

Two of the three were also invisible to my own attempts at verification: synthetic mouse input could not
reach the Win11 tray flyout, and z-order silently swallowed injected wheel events, so an automated check
would have reported "no change" whether the fix worked or not. Both were settled by a human moving the mouse
while the application logged what it received.

The list was also incomplete: wheel scrolling was not on it, and nothing on it would have caught that.
Checks 2, 3, 4, 5, 7 and 8, and the expansion half of check 6, have not been run as part of this work.
