# VramMonitor

## C# code analysis

Two CLI tools are installed globally and pre-approved in `.claude/settings.json`. They are
**complements, not alternatives** — their findings barely overlap, and in this repo each is
blind to something the other sees.

**Division of labour here:**

- **`jb inspectcode`** (ReSharper CLI 2026.2.1) — the tool to trust for *anything touching the
  MVVM view models*. It resolves CommunityToolkit.Mvvm generated members correctly, so it is
  the only one that can answer "does this member exist / is it used" in `MainViewModel.cs`,
  `TrayTooltipView.xaml.cs`, `App.xaml.cs` and `MainWindow.xaml.cs`. Also finds redundant
  usings, naming-rule and redundancy issues — a finding class Roslynator does not report at all.
- **`roslynator`** (0.13.0, Roslyn 4.14) — the tool for *Roslyn analyzer findings*
  (`CA*`, `SYSLIB*`, `xUnit*`), plus `loc` and the real public API surface via `list-symbols`.
  It cannot see MVVM-generated members, so its output in the four files above needs filtering
  (see below).

Both beat grep whenever the question is semantic — who calls this, what is actually public,
is this member real — because grep cannot distinguish a declaration from a string, a comment
or a stale doc reference.

**Never run these:**

- **Never `jb cleanupcode`.** It rewrites and reformats source files in place across the whole
  solution. It is not pre-approved, and a permission prompt is not an invitation.
- **Never pass `--remove` to `roslynator find-symbol`.** It deletes the declarations it finds.
  `find-symbol` is deliberately not pre-approved for exactly this reason.

### Restore first — Roslynator only

```bash
dotnet restore VramMonitor.slnx
```

Without it every NuGet type is unresolved: **766 diagnostics unrestored vs 32 restored** on this
solution (326 of them phantom `CS0246`). Fresh clones and git worktrees never inherit `obj/`, so
this bites there every time — and also after adding a new package.

**`jb` needs no restore.** Its `--build` defaults to true, so it builds the solution itself.
Verified on a tree with `obj/` and `bin/` wiped and the cache cleared: identical findings to the
warm run, no phantom resolve errors. A second run is not needed here.

### Commands

```bash
roslynator analyze VramMonitor.slnx                                    # analyzer findings
roslynator loc VramMonitor.slnx                                        # 5,105 LOC / 7,549 total
roslynator list-symbols VramMonitor.slnx --visibility public --depth member
jb inspectcode VramMonitor.slnx --stdout -f=Text -e=WARNING --caches-home=r:/Temp/jb-caches --no-updates
```

- `-e=WARNING` is the working floor for `jb`. The default `SUGGESTION` adds ~50 more low-value
  findings. **`-e=ERROR` reports nothing on this solution** — use it as a quiet check.
- Always point `--caches-home` outside the repo; the cache is tens of megabytes.
- `.slnx` loads fine in both tools. Neither leaves anything in the working tree.
- If you wipe `obj/` but keep the cache, `jb` prints one
  `Warning: DocumentInfoCache contains outdated data ... GeneratedInternalTypeHelper.g.cs` line.
  That is cache staleness, not a finding, and it self-heals on the next run.

Baseline as of 2026-09-17, with every actionable finding fixed: `roslynator analyze` **32**
diagnostics in ~4 s; `jb inspectcode -e=WARNING` **13** findings in ~28 s. Every one of those 45
is a documented false positive below — so **anything new is real**. Treat a higher count as a
regression, not as noise.

### Known false positives — Roslynator

Checked 2026-09-17. **Do not "fix" the code for any of these.**

- **CommunityToolkit.Mvvm generated members are invisible.** Roslynator's MSBuild workspace does
  not run Roslyn source generators, so every `[ObservableProperty]` property does not exist as a
  symbol. This accounts for **31 of the 32 remaining diagnostics**, all in `MainViewModel.cs`,
  `TrayTooltipView.xaml.cs`, `App.xaml.cs` and `MainWindow.xaml.cs`:
  - `CS0103` / `CS1061` on `SelectedKey`, `Plot`, `GpuName`, `StatusLine`, `BlockingError`,
    `HasHiddenOtherRows`, `OtherDisplayFloorMegabytes`, `TotalValue`, `Footer`;
  - `CS0169` / `CS0414` "field never used" on their `_camelCase` backing fields;
  - `CS0759` "no defining declaration for partial method" on the `OnXChanged` hooks.
- **Downstream of the above:** `CA1822: Member 'HasSelection' can be marked static`
  (`MainViewModel.cs:59`) is wrong — `HasSelection => SelectedKey is not null` does read
  instance state; Roslynator just cannot see `SelectedKey`. This is the 32nd diagnostic.
- Consequently `find-symbol --unused` is unreliable for anything the MVVM generator touches.
  Confirm every hit with grep before believing it.
- **WPF XAML codegen, by contrast, *is* visible** — the markup compiler runs as an MSBuild
  target, not a source generator. `list-symbols` therefore includes `InitializeComponent` and
  `GeneratedInternalTypeHelper`; that is generated plumbing, not public API.
- The Roslyn 4.14 C# 14 `field`-keyword gap does not apply: this repo uses no `field` contextual
  keyword.

`SYSLIB1054` on `QueryFullProcessImageName` is suppressed by a scoped `#pragma` in
`WindowsProcessMetadataResolver.cs`. The `DllImport` there is deliberate — the source generator
cannot marshal its `char[]` buffer without disabling runtime marshalling assembly-wide. Leave
both the pragma and the `DllImport` alone.

### Known false positives — `jb inspectcode`

Checked 2026-09-17. All 13 remaining findings are listed here.
**Do not "fix" the code for any of these.**

- **"Base type 'X' is already specified in other parts"** ×4 — one per WPF code-behind
  (`App.xaml.cs:27`, `TrayTooltipView.xaml.cs:10`, `MainWindow.xaml.cs:11`,
  `SettingsWindow.xaml.cs:11`). Restating the base type on the hand-written half of a XAML
  partial class is the idiomatic pattern. Leave it.
- **P/Invoke names must match the native ones** ×2. `CreateDXGIFactory1`
  (`DxgiAdapterEnumerator.cs:26`) and `PDH_FMT_LARGE` (`PdhInterop.cs:37`) are flagged against
  the `Methods` and `Constant fields` naming rules. Never rename an interop declaration or
  constant to satisfy a naming rule.
- **`MainViewModel.cs:81 Parameter 'value' is never used`** — that is the MVVM Toolkit
  `OnOtherDisplayFloorMegabytesChanged(double value)` hook; the generator fixes the signature.
- **"Positional property … is never accessed"** ×6 — `SessionView.SessionId`,
  `TotalSeries.AdapterCapacityBytes`, `MonitorHealth.MaxProbeDuration`,
  `ProcessSessionInfo.Gpu`, `GpuSnapshot.Gpu`, `GpuSnapshot.TimestampUtc`. These are carried
  model state on immutable domain records, written but not yet read, and **deleting them would
  break the spec**: section 15 (spec line 547) lists probe duration, skipped cycles and late
  cycles as things to track *internally* — recorded without being displayed. `GpuSnapshot`'s own
  timestamp is unread by design too, because the sampler timestamps samples from its anchored
  monotonic clock instead. jb cannot see intent, only readers.
- jb reported nothing inside `.xaml` markup here, even at `-e=SUGGESTION` — all findings are in
  `.cs`. Do not assume it is checking binding paths or resource keys.

### Where grep still wins

Both tools only see C# compiled into the solution. Use grep for `.xaml` markup (binding paths,
`x:Name`, resource keys, styles), `Directory.Build.props` / `Directory.Packages.props` /
`VramMonitor.slnx` / `global.json`, `docs/`, `README.md` and `Assets/`. In particular, a
property that XAML binds to by string name is invisible to both tools' usage analysis — check
the markup before deleting anything a view could bind.

## Tests

`dotnet test` reports "Zero tests ran" (exit 5) in this repo — the Microsoft.Testing.Platform
runner configured in `global.json` does not discover through it. Run the test projects' own
executables instead; they self-host and are the reliable signal:

```bash
./tests/VramMonitor.Core.Tests/bin/Debug/net10.0/VramMonitor.Core.Tests.exe
./tests/VramMonitor.Windows.Tests/bin/Debug/net10.0-windows/VramMonitor.Windows.Tests.exe
```

96 + 36 = 132 tests as of 2026-09-17. GPU-dependent integration tests skip rather than fail on a
machine without an adapter.
