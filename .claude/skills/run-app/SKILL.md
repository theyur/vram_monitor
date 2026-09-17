---
name: run-app
description: Launch and drive VRAM Monitor, this repo's native Windows WPF desktop tray app, to see a change working in the real UI — build it, start it, screenshot the window, click buttons and select rows through UI Automation, then shut it down. Use this whenever the user asks to run, start, launch, open, or screenshot the app, or asks to confirm a change actually works in the running application rather than only in tests. It covers the WPF window, the OxyPlot chart, the settings dialog and the tray icon. This app is native WPF/XAML desktop only, never a web app — do NOT use this skill for Blazor, ASP.NET, MVC, Razor components, anything served over http/localhost, browser or Playwright automation, or Python scripts, and do not use it to run the xUnit suite (CLAUDE.md covers tests).
---

# Running VRAM Monitor

The app is a single WPF desktop process that lives in the system tray. There is no server, no
browser and no dev server — you launch an `.exe`, look at a real window, and drive it through
UI Automation. Nothing here transfers to a web UI.

Two helper scripts live in `scripts/`. Prefer them over writing your own; each exists because
of a specific trap documented below.

## Before you launch: three things that will bite you

**A second instance exits silently.** `App.OnStartup` takes a single-instance mutex
(`Local\VramMonitor.SingleInstance`) and calls `Shutdown()` if it is already held. If the user
already has VRAM Monitor running, your launch produces no window and no error — it just
vanishes, and you will waste time wondering why. Check first:

```powershell
Get-Process VramMonitor -ErrorAction SilentlyContinue
```

If one is running, ask the user before killing it — it may be their real session, holding
monitoring history that only exists in RAM.

**Settings are shared with the user's real installation.** `SettingsStore.Default()` reads
`%APPDATA%\VramMonitor\settings.json`. Launching the app only reads it, which is harmless, but
**clicking Apply in the settings dialog overwrites the user's configuration.** Use Cancel when
you are done inspecting that dialog. Command-line overrides are deliberately never written back
(see `CommandLineOverrides`), so they are the safe way to change behaviour for one run.

**Nothing is visible until sampling has run for a while.** "Aggressive consumers" stays at 0
until processes exceed the aggressive-duration threshold, so a screenshot taken two seconds
after launch shows an empty list and tells you nothing. Pass `--interval-seconds 2` (2 s is the
documented minimum) and give it 30–60 s before judging the chart.

## Build and launch

```powershell
dotnet build VramMonitor.slnx
Start-Process -FilePath "src\VramMonitor.App\bin\Debug\net10.0-windows\VramMonitor.exe" -ArgumentList '--interval-seconds','2'
```

`dotnet run --project src/VramMonitor.App` also works but blocks the shell, which is awkward
when you still need to drive the window. Prefer `Start-Process`.

The main window is shown on startup unless you pass `--start-hidden`, in which case the app goes
straight to the tray and `MainWindowHandle` stays 0 — both scripts below will then refuse to
run, which is the correct behaviour rather than a bug. Other useful one-run overrides:
`--history-minutes`, `--floor-mb`, `--aggressive-minutes`, `--grace-seconds`, `--chart-top-n`,
`--gpu`.

## Screenshot it, then actually look at it

```powershell
.\.claude\skills\run-app\scripts\screenshot.ps1 -Out shot.png -WindowOnly
```

`-WindowOnly` captures just the window at its current size and is the most legible option. Drop
the flag to maximize first and capture the whole screen, which is better when you want the chart
and the full consumer list in one frame. Either way the image is downscaled to 1920 wide, because
reading a raw 4K grab costs a lot of context for no extra detail.

The script sets per-monitor DPI awareness before measuring, and does it **on the thread**
(`SetThreadDpiAwarenessContext`) rather than the process. This is the non-obvious part, and it has
two halves. Without any DPI awareness, `GetWindowRect` returns logically-scaled coordinates while
`CopyFromScreen` works in physical pixels, so the capture lands up and to the left of the window
and shows whatever was behind it. And `SetProcessDPIAware()` is not enough, because `pwsh.exe`
already declares `SYSTEM_AWARE` in its manifest, which makes the process-wide call a silent no-op:
a system-aware process is told a single DPI for the whole desktop — the primary monitor's — and
Windows then virtualizes every other monitor's geometry by `systemDPI / monitorDPI`. On a desktop
with a 4K primary at 150% next to a 1920x1080 display at 100%, the second monitor is reported as
2880x1620 and the full-screen capture region comes out half again too large.

Both failures are silent, which is why the script asserts the mode rather than hoping for it.

**Read the screenshot rather than assuming a successful launch.** The status bar along the bottom
is the health summary and the fastest way to tell real success from a window that merely opened:

```
every 2s · last sample 14:49:53 · no probe errors · probe 0,9 ms · window 10 min · 16 samples
```

A populated GPU name in the header plus a rising sample count means the PDH counters, the DXGI
enumerator and the sampling loop are all genuinely working. A blank window, an empty consumer
list after a minute, a missing GPU name, or a startup error banner all mean something failed —
investigate rather than reporting success.

## Drive it

WPF publishes its visual tree to UI Automation with no instrumentation needed, so a script can
click real buttons. There is no CDP or Playwright equivalent for a native window.

```powershell
$d = ".\.claude\skills\run-app\scripts\drive.ps1"
& $d buttons                                          # list clickable buttons
& $d click 'Settings…'                                # open the settings dialog
& $d dialogs                                          # confirm it opened
& $d dialog-click 'VRAM Monitor settings' 'Cancel'    # close WITHOUT saving
& $d rows                                             # count consumer rows
& $d select 1                                         # isolate one app on the chart
```

Three things the script already handles, worth knowing so you can debug it:

- **Button names contain `…` (U+2026), not three periods.** `click 'Settings...'` finds nothing.
  Run `buttons` and copy the name exactly.
- **A modal WPF dialog is not a child of the desktop root.** Enumerating the desktop's children
  filtered by process id finds only the main window, so the settings dialog looks absent. It has
  to be found under the owning window's subtree.
- **Consumer rows have no useful `Name`** — UIA reports the type name
  (`VramMonitor.App.ViewModels.ConsumerRow`) for all of them, so select by index. Index 0 is the
  first aggressive consumer; the other-consumers list follows.

Selecting a row is the single most informative interaction: it isolates that application's
series on the chart, fades the rest, and makes a **Show all lines** button appear. That button's
visibility is bound to `MainViewModel.HasSelection`, so seeing it confirms the
CommunityToolkit.Mvvm generated `SelectedKey` property and the chart rebuild both work.

## Shut it down

`ShutdownMode` is `OnExplicitShutdown` and the window only hides to the tray when closed, so
closing the window does not end the process. Stopping it is safe — monitoring history is
in-memory by design and discarded on exit (spec sections 3.1 and 10):

```powershell
Stop-Process -Name VramMonitor -Confirm:$false
```

Afterwards, confirm you left nothing behind: `git status` should be clean of build noise, and
`%APPDATA%\VramMonitor\settings.json` should have its original timestamp if you avoided Apply.

## Not this skill

Running the test suite is a different job with its own quirk — `dotnet test` reports "Zero tests
ran" here and the test executables must be invoked directly. See the Tests section of the root
`CLAUDE.md`. And to restate the scope: this repo has no web front end, so a request about
Blazor, Razor, ASP.NET, a localhost URL or browser automation is not about this app.
