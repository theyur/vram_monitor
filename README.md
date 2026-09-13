# VRAM Monitor

A lightweight Windows 11 desktop application that tracks **per-application dedicated VRAM** on an NVIDIA GPU
and keeps a rolling in-memory history, so you can answer one question:

> Which applications used VRAM, how much did they use, and for how long?

Built to `docs/vram-monitor-design-spec.md`.

---

## Running it

```bash
dotnet run --project src/VramMonitor.App
```

Or build once and launch the executable:

```bash
dotnet build -c Release
```

The binary lands at `src/VramMonitor.App/bin/Release/net10.0-windows/VramMonitor.exe`.

No elevation is required, and nothing is installed: it is a single desktop process with no service, no
database and no background agent. Monitoring history lives **only in RAM** and is discarded when the
application exits, unless you export it first.

The application lives in the system tray. Hovering the tray icon shows the current top five consumers and,
on the last row, the adapter total; clicking it opens the main window. Closing the main window hides it back
to the tray — use **Exit** on the tray menu to quit.

### Command-line overrides

These apply to the current launch only and are never written back to the settings file:

| Argument | Meaning |
|---|---|
| `--interval-seconds <n>` | Sampling interval (minimum 2) |
| `--history-minutes <n>` | Rolling history window (maximum 720) |
| `--floor-mb <n>` | Monitoring floor for aggressive analysis |
| `--aggressive-minutes <n>` | Cumulative qualifying time before an application counts as aggressive |
| `--grace-seconds <n>` | How long an application stays listed as aggressive after it stops qualifying |
| `--chart-top-n <n>` | How many applications are plotted (1–50) |
| `--gpu <ordinal\|text>` | Adapter index, or part of its description, e.g. `--gpu 3090` |
| `--start-hidden` | Start in the tray without opening the window |

Example:

```bash
dotnet run --project src/VramMonitor.App -- --interval-seconds 5 --history-minutes 30 --gpu 3090
```

### Settings

Stored at `%APPDATA%\VramMonitor\settings.json`. Interval, window, floor, thresholds, chart size, the
display filter, autostart and the selected GPU are all editable in **Settings…**, and the runtime-safe ones
take effect immediately by re-evaluating the history already in memory — no restart.

The file deliberately records the GPU by vendor/device/description rather than by LUID, because Windows
reallocates adapter LUIDs on every boot and again after any driver reset.

---

## How it measures

Per-process GPU memory comes from the Windows performance counters `GPU Process Memory` and
`GPU Adapter Memory` — the same source Task Manager uses.
They are read through **PDH (Performance Data Helper)**, the Windows counter-consumption API in `pdh.dll`.
"PDH" means that path throughout the code and the rest of these docs.

**NVML and `nvidia-smi` cannot do this on a GeForce card.** Under the WDDM driver model, which a consumer
card cannot leave, `nvidia-smi` reports per-process GPU memory as `N/A`. The performance counters are the
only per-process source available, and they need no elevation.

A probe costs about **0.08 ms** and allocates nothing measurable. Over 9000 consecutive probes — a full day
at the default interval — handle count and working set both stayed flat.

### Two things worth knowing about the numbers

- **Per-application values can sum to more than the total.** Windows attributes a shared surface to every
  process that references it, so the sum over applications legitimately exceeds the adapter total (measured:
  9502 MB against 7601 MB on an idle desktop). The total line is read from the adapter counter and is never
  derived by summing. Reconciling the difference would mean inventing data.
- **A missing measurement is never shown as zero.** An unreadable value renders as `—` and breaks the line
  on the chart. A process that simply exits also breaks its line, but gets no disruption marker — the marker
  is what distinguishes a probe failure from a normal exit.

---

## Reading the window

The consumer list is split into **Aggressive consumers** and **Other consumers**. An application is
aggressive when it has spent enough cumulative time in the top quartile of consumers above the monitoring
floor. "Other" is everything else still retained — quiet applications, ones that only spiked, ones that were
aggressive a while ago, and ones that have already exited but whose samples are still inside the window.

Any retained consumer can be selected, which highlights its line and dims the rest, and expanded, which
reveals its individual processes and overlays their lines. Clicking the selected row again, pressing
<kbd>Esc</kbd>, or the **Show all lines** button that appears while something is selected brings every line
back.

Chart markers:

| Marker | Meaning |
|---|---|
| Red dotted | Probe failed — no measurement at all for that moment |
| Amber dotted | Partial sample — some measurements were unreadable |
| Blue dotted | Sampling paused — no probe ran for much longer than the interval, typically machine sleep |

A line that simply stops, with no marker, is a process that exited normally.

---

## Export

**Export…** writes the whole retained window as three files, and monitoring continues while they are written:

- `<name>.json` — canonical and lossless; missing measurements are `null`, never `0`
- `<name>-observations.csv` — one row per sample and process
- `<name>-applications.csv` — one row per application with the derived figures

All exported numbers use an invariant decimal point regardless of the machine's locale, so a comma-decimal
system cannot corrupt the CSV.

---

## Layout

```
src/VramMonitor.Core/      domain, rolling store, analysis, export, settings   (plain net10.0)
src/VramMonitor.Windows/   PDH provider, DXGI enumeration, process metadata    (net10.0-windows)
src/VramMonitor.App/       WPF shell, tray, charting                           (net10.0-windows)
tests/                     unit tests (no GPU needed) and integration tests
```

`VramMonitor.Core` targets plain `net10.0` on purpose: it physically cannot reference WPF, PDH or DXGI, so
the rule that the UI must not know how VRAM is measured is enforced by the compiler rather than by
convention. The analysis layer is a pure function of `(history, settings, now)`, which is what lets a
settings change re-evaluate existing history and lets nearly everything be tested without a GPU.

## Tests

```bash
dotnet test
```

Tests run on Microsoft.Testing.Platform (see `global.json`). GPU-dependent integration tests skip rather
than fail on a machine with no suitable adapter.
