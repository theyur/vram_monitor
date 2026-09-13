# VRAM Monitor — Implementation Plan (final, post round 3)

Spec: `docs/vram-monitor-design-spec.md`
References use `§n` for spec sections and `AC-n` for the 26 acceptance criteria in §22.
Critic reports: `.claude/plans/vram-monitor.review-1.md`, `.review-2.md`, `.review-3.md`.
Dispositions: §17 (round 1), §18 (round 2), §19 (round 3).

---

## 0. Evidence gathered (facts observed, not assumptions)

Measured on the target machine during planning with throwaway prototypes in the session scratchpad
(`probe/` … `probe5/`, `uismoke/`, `xu/`, `cuda_alloc.py`). These are **not** part of the repo.

### 0.1 Repository

Greenfield: only `.gitignore` and `docs/vram-monitor-design-spec.md`. No code, no CLAUDE.md, no solution,
no CI. Branch `claude/vram-monitor-plan-9c945f`, base `master`.

### 0.2 Toolchain

- .NET SDK **10.0.401**, runtime **10.0.12**, `Microsoft.WindowsDesktop.App 10.0.12` → WPF on
  `net10.0-windows` available (§3.1).
- Windows 11 Pro 10.0.26200 x64. Session **not elevated**; user not in *Performance Monitor Users*.
  Everything below works anyway (§4).
- **Machine culture `uk-UA`** (UI culture `en-US`) → decimal separator is `,`. A live hazard for CSV export.

### 0.3 GPU hardware

- `NVIDIA GeForce RTX 3090`, driver 595.79, 24576 MiB, WDDM.
- Also present: `Intel(R) Graphics` (iGPU), `Microsoft Basic Render Driver`.
- DXGI `EnumAdapters1`: RTX 3090 LUID `0x00000000_0x0001AEA2`, `DedicatedVideoMemory` 24322 MiB.
- `Win32_VideoController.AdapterRAM` reports 4293918720 (32-bit overflow). **Unusable**; DXGI is correct.

### 0.4 Provider validation — the decisive finding

**NVML / `nvidia-smi` cannot provide per-process VRAM here.** `nvidia-smi` reports
`GPU Memory Usage = N/A` for every process — the documented WDDM limitation
(`nvmlDeviceGetComputeRunningProcesses` returns "not supported" outside TCC mode, which GeForce cannot enter).
Observed, not assumed.

**Windows performance counters are the viable source** (what Task Manager itself uses):

| Counter | Instance shape | Purpose |
|---|---|---|
| `GPU Process Memory(*)\Dedicated Usage` | `pid_41172_luid_0x00000000_0x0001AEA2_phys_0` | primary metric (§5.1) |
| `GPU Process Memory(*)\Shared Usage` | same | secondary diagnostic (§5.1) |
| `GPU Adapter Memory(*)\Dedicated Usage` | `luid_0x00000000_0x0001AEA2_phys_0` | total-VRAM context (§12.2) |

**Soak measurement — 9000 probes (one full day at a 10 s interval), single long-lived process:**

| Metric | Result |
|---|---|
| Latency | min 0.077 / **median 0.082** / p95 0.184 / p99 0.346 / max 2.376 ms |
| CPU | **0.092 ms per probe** → **0.0009 % of one core** at 10 s (§18, AC-25) |
| Handles | 244 → 250 (+6, flat from iteration 3000) — **no leak** |
| Working set | 26.0 → 29.5 MB (flat from iteration 3000) — **no leak** |
| Managed allocations | **0 bytes per probe, 0 GCs of any generation** |

> Round 1 of this plan quoted 0.23 ms / ~1.0 ms CPU / 0.010 % of a core from a 30-iteration run whose CPU
> delta was dominated by timer granularity. Those figures were an order of magnitude pessimistic and are
> superseded by the table above.

Other observed behaviour:

- Counters are gauges (`PERF_COUNTER_LARGE_RAWCOUNT`) → **one** `PdhCollectQueryData` suffices.
- **Wildcard instance lists refresh when the query is rebuilt, within one long-lived process** — the case
  that actually matters. Verified by launching 3 WPF processes from a persistent probe host: instance count
  went 81 → 84 → 81 after exit, matching `PerformanceCounterCategory.GetInstanceNames()` at every step.
- **`PdhEnumObjectsW(bRefresh: true)` costs ~200 ms and is unnecessary** — a 1000× pessimisation to avoid.
- `PdhAddEnglishCounterW` accepts English names on any locale → immune to `uk-UA`.
- **A missing counter object returns `PDH_CSTATUS_NO_OBJECT` (0xC0000BB8)** → the detection mechanism for
  §16.3 blocking startup errors.

### 0.5 Per-process sum vs adapter total — a real measurement caveat

Measured twice: Σ(per-process `Dedicated Usage`) = **9502 MiB** vs `GPU Adapter Memory\Dedicated Usage`
= **7601 MiB** (`nvidia-smi` independently 7615 MiB). Per-process dedicated **over-counts** because
cross-process shared surfaces are attributed to every referencing process.

The total series must come from the adapter counter, **never** from summing applications. Reconciling the
difference would fabricate data, forbidden by §5.2.

### 0.6 Process metadata

- `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageNameW`: **54 of 59** GPU PIDs
  resolved to a full path in **1.2 ms total**. This per-probe cost is affordable, which is what makes
  per-probe PID-reuse detection viable (§5.4).
- The 5 failures are protected/system processes: `System` (pid 4), `csrss`, `dwm`,
  `CefSharp.BrowserSubprocess`, `L-Connect-Service`. Path **and** creation time both denied.
- `Process.GetProcesses()` names all of them in 23 ms for 511 processes → tier-2 fallback for §5.3.

### 0.7 Instance-name parsing and LUID filtering

- Regex `^pid_(\d+)_luid_(0x[0-9A-Fa-f]+)_(0x[0-9A-Fa-f]+)_phys_(\d+)$` → **0 failures across all 81
  live instances**. All `phys` values are 0 on this machine; no trailing suffixes observed.
- **17 of ~59 GPU PIDs have instances on more than one adapter** — `explorer.exe` on 3 LUIDs, `System` on
  all 4. LUID filtering is therefore a **correctness requirement**, not a multi-GPU nicety: without it a
  process's Intel-iGPU allocation would be added to its RTX 3090 figure.
- No PID had more than one instance on the same LUID here, but partitioned/LDA setups can expose `phys_1+`,
  so the provider sums across all `phys_N` for the selected LUID. Untestable on this hardware.

### 0.8 CUDA workload validation — the metric tracks real ML allocations

The idle-desktop sample of §0.6 says nothing about the workload this tool exists for, so a real CUDA
allocation was measured (PyTorch 2.6.0+cu124, `torch.cuda.is_available() == True`):

| Source | Value |
|---|---|
| `torch` allocated / reserved | 3072.0 MiB |
| PDH `Dedicated Usage` for that PID | **3295.5 MiB** (3072 tensor + ~223 MiB CUDA context/kernels) |
| `nvidia-smi` total | 7658 → 10959 MiB = **+3301 MiB** |
| `GPU Adapter Memory\Dedicated Usage` | 10971 MiB (vs `nvidia-smi` 10959) |

CUDA allocations are fully visible in `Dedicated Usage`, and per-process and adapter figures agree with
`nvidia-smi` within ~12 MiB. This validates the primary metric for the intended use case.

Still unmeasured: an anti-cheat-protected (PPL) game, which denies the process handle. Handled defensively
by §5.4's session rules and the §5.3 fallback chain; flagged as a known limitation.

### 0.9 Library and test-platform validation

All compiled **and executed** on `net10.0-windows`:

| Package | Version | Evidence |
|---|---|---|
| `OxyPlot.Wpf` | 2.2.0 | rendered the proof chart below; MIT; ships `net8.0-windows7.0`, consumed cleanly |
| `H.NotifyIcon.Wpf` | 2.4.1 | `TaskbarIcon.ForceCreate()` succeeded; MIT; first-class `net10.0-windows7.0` |
| `CommunityToolkit.Mvvm` | 8.4.2 | resolves |
| `xunit.v3` | 4.0.1 | **test executed and passed** — see below |

**Test platform — measured, and it contradicts the obvious setup.** On .NET 10 SDK, `dotnet test` against a
`xunit.v3` project fails with:

> `error: Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later.`

The cause is specific: `xunit.v3` 4.x enables Microsoft.Testing.Platform **v2 by default**, and MTP v2
refuses to run through the VSTest target on .NET 10 SDK. VSTest is still `dotnet test`'s default *mode* —
it is the MTP-enabled project driven through that mode that is rejected — so adding
`xunit.runner.visualstudio` does not help either, because the MTP targets are already in the build.

The working configuration, verified by an actually-executed passing test, is **Microsoft.Testing.Platform**.
The exact project shape that passed, which is the contract for Step 1:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <OutputType>Exe</OutputType>          <!-- v3 test projects are executables -->
  <IsTestProject>true</IsTestProject>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="xunit.v3" />   <!-- version from Directory.Packages.props -->
</ItemGroup>
```

with a repo-root `global.json` containing `{"test":{"runner":"Microsoft.Testing.Platform"}}`, **no**
`Microsoft.NET.Test.Sdk`, **no** `xunit.runner.visualstudio`, and **no**
`UseMicrosoftTestingPlatformRunner` property (it was not needed).

**Chart proof.** The OxyPlot smoke test rendered, in one image, every chart requirement of §12–§13:
`double.NaN` breaks a line into a genuine discontinuity with no connecting segment (§13.1); a series that
simply stops renders as a normal line end with no marker (§13.5); a dimmed series works (§11.2); the total
series sits on an independent right axis in faint dashed grey without suppressing per-app scale (§12.2);
vertical `LineAnnotation` markers with hover text render on the time axis (§13.2).

`TaskbarIcon` exposes `TrayToolTip : UIElement` (rich WPF tooltip), `IconSource : ImageSource`,
`ContextMenu`, `TrayLeftMouseUp`, `TrayToolTipOpen`. Construction was validated; **hover rendering on Win11
was not** — see the `szTip` fallback in §8.

---

## 1. Material assumptions

| # | Assumption | Confidence | Consequence if wrong | Validation |
|---|---|---|---|---|
| 1 | PDH `GPU Process Memory` is the only viable per-process source on GeForce/WDDM | 0.97 | Provider replaced wholesale | **Validated** §0.4 |
| 2 | Rebuilding the PDH query each probe discovers new/exited processes within one long-lived host | 0.95 | New apps never appear | **Validated** §0.4 (81→84→81 vs ground truth) |
| 3 | Probe cost is negligible for §18/AC-25 | 0.99 | Would need a cheaper API | **Validated** §0.4 — 0.082 ms median, 0.0009 % of a core over 9000 probes |
| 4 | `Dedicated Usage` is the correct reading of "dedicated VRAM" (§5.1), including CUDA | 0.97 | Wrong metric everywhere | **Validated** §0.8 — tracks a 3 GB torch allocation, agrees with `nvidia-smi` |
| 5 | Counter LUID string matches DXGI `AdapterLuid` | 0.96 | GPU selection breaks | **Validated** §0.3/§0.7 |
| 6 | Σ(per-app) legitimately exceeds the adapter total and must be shown raw | 0.92 | Read as a bug | **Validated** §0.5; surfaced in UI + handover |
| 7 | OxyPlot 2.2.0 satisfies §12–§13 without zoom/pan | 0.95 | Library swap (ScottPlot/LiveCharts2) | **Validated** §0.9 |
| 8 | The optional enhanced-accuracy provider (§4) has no viable candidate | 0.90 | A spec-permitted, not required, feature is absent | §0.4 — NVML is strictly *worse*. §4 is permissive ("is acceptable if") |
| 9 | `HKCU\...\Run` is writable unelevated | 0.99 | Autostart needs Task Scheduler | **Validated** — key opened for write as a normal user; nothing written |
| 10 | Framework-dependent deployment is fine | 0.95 | Self-contained publish | **Validated** §0.2 |
| 11 | PID reuse is handled for **all** processes, including those denying a handle | 0.85 | Two process lifetimes merge into one session | Round 1 wrongly limited this to 5 boot-lifetime OS processes. PPL/anti-cheat games also deny handles and *do* restart → §5.4 adds an absence-based rule. Unit-tested; not testable against a real PPL game here |
| 12 | LUIDs are **not** stable across reboot or driver reset | 0.97 | Persisted GPU selection silently matches nothing | Documented Win32 behaviour (`AllocateLocallyUniqueId`: unique only until restart) → §4/§9 persist a stable selector instead |

No assumption is both low-confidence and high-consequence.

---

## 2. Technical risks and mitigations

| Risk | Mitigation |
|---|---|
| `uk-UA` decimal comma corrupts CSV | `CultureInfo.InvariantCulture` for all export formatting; RFC 4180 quoting; unit test forces `uk-UA` on the test thread |
| Localized counter names elsewhere | `PdhAddEnglishCounterW` only, never `PdhAddCounterW` |
| `PdhEnumObjects(refresh)` 200 ms trap | Forbidden in code with a comment citing the measurement |
| Absent vs missing vs failed conflation (§5.2, §13.5) | Three distinct states, never `0`; §5.3 table; poisoning rule in §7.1 |
| Sleep/resume drawing an interpolated line across hours | Explicit `NaN` break injection in §7.8; gap detected from the monotonic clock, not from `PeriodicTimer` behaviour |
| GPU LUID reallocated by reboot/TDR | Stable `GpuSelector` persisted; stale LUID makes the probe `Failed`, never `Ok`-with-no-instances (§5.1, §9) |
| PDH handle leak over ~8640 probes/day | **Measured**: +6 handles total over 9000 probes, flat after 3000 (§0.4). `SafeHandle` wrappers + `finally` anyway |
| UI thread blocked by analysis | Background sampler loop; UI receives an immutable prepared snapshot; enforced by project layering (§3) |
| Chart unreadable with ~40 retained apps | Configurable top-N by peak plus selected/expanded (D2) |

---

## 3. Solution structure

Layering is enforced **by the compiler**: `VramMonitor.Core` targets plain `net10.0`, so it physically cannot
reference WPF, PDH or DXGI. This is how §3.2 ("the rest of the application must not depend directly on
Windows/NVIDIA-specific structures"; "the UI must not know how VRAM is measured") is guaranteed.

```
VramMonitor.sln
global.json                        { "test": { "runner": "Microsoft.Testing.Platform" } }   (§0.9)
Directory.Build.props              nullable, warnings-as-errors, LangVersion
Directory.Packages.props           central package management, pinned versions
src/VramMonitor.Core/              net10.0           domain, store, analysis, export, settings model, abstractions
src/VramMonitor.Windows/           net10.0-windows   PDH provider, DXGI enumerator, metadata, autostart
src/VramMonitor.App/               net10.0-windows   WPF shell, tray, MVVM, OxyPlot, composition root
tests/VramMonitor.Core.Tests/      net10.0           bulk of §19.1; OutputType=Exe (MTP)
tests/VramMonitor.Windows.Tests/   net10.0-windows   §19.2 integration, GPU-gated; OutputType=Exe (MTP)
```

Dependencies: `App → Windows → Core`, `App → Core`. Core depends on nothing.

---

## 4. Domain model (`VramMonitor.Core.Model`)

Immutable records. **A missing measurement is `null`, never `0`** (§5.2).

```csharp
// RUNTIME-ONLY GPU key. Never persisted — LUIDs are reallocated on reboot / driver reset.
readonly record struct GpuId(string Luid);
// PERSISTED, stable across reboots.
sealed record GpuSelector(uint VendorId, uint DeviceId, uint SubSysId, string Description, int Ordinal);
sealed record GpuInfo(GpuId Id, GpuSelector Selector, string Description, long DedicatedVideoMemoryBytes);

// Opaque, monotonically increasing, assigned at first sight. NEVER changes -> no observation is ever re-keyed.
readonly record struct ProcessSessionId(long Value);

enum ProcessIdentityKind { ExecutablePath, ExecutableName, PidFallback }   // §5.3 fallback order
sealed record ProcessIdentity(string Key, string DisplayName, string? ExecutablePath,
                              string? ExecutableName, ProcessIdentityKind Kind);

// Identity fields are mutable across probes; SessionId is not.
sealed record ProcessSessionInfo(ProcessSessionId SessionId, uint Pid, long CreationTicks, GpuId Gpu,
                                 ProcessIdentity Identity, DateTimeOffset FirstSeenUtc,
                                 DateTimeOffset LastSeenUtc, DateTimeOffset? ProcessStartUtc);

// Present in the instance list but unreadable -> null -> gap (§13.4).
// A process with no instance at all produces NO ProcessObservation (absent, §13.5).
sealed record ProcessObservation(ProcessSessionId SessionId, long? DedicatedBytes, long? SharedBytes);

enum ProbeOutcome { Ok, Partial, Failed }                                  // §13.2–§13.4

sealed record GpuSample(DateTimeOffset TimestampUtc, TimeSpan NominalInterval, ProbeOutcome Outcome,
                        long? TotalDedicatedBytes, IReadOnlyList<ProcessObservation> Observations,
                        string? FailureReason);

sealed record GpuSnapshot(GpuId Gpu, DateTimeOffset TimestampUtc, ProbeOutcome Outcome,
                          long? TotalDedicatedBytes,
                          IReadOnlyList<RawProcessMeasurement> Measurements, string? FailureReason);
readonly record struct RawProcessMeasurement(uint Pid, long? DedicatedBytes, long? SharedBytes);
```

### Abstractions (§3.2)

```csharp
interface IGpuMemoryProvider {
    Task<IReadOnlyList<GpuInfo>> GetGpusAsync(CancellationToken ct);
    Task<GpuSnapshot> GetSnapshotAsync(GpuId gpuId, CancellationToken ct);   // spec's named shape
}
interface IProcessMetadataResolver {
    ProcessTimes? GetTimes(uint pid);        // cheap, every probe -> PID-reuse detection
    string? ResolvePath(uint pid);           // once per session, retried while unresolved
    string? ResolveNameOnly(uint pid);       // tier-2 bulk fallback
}
interface IClock { DateTimeOffset UtcNow { get; } long MonotonicTicks { get; } }
```

---

## 5. Sampling pipeline

### 5.1 Provider (`VramMonitor.Windows.Pdh.PdhGpuMemoryProvider`)

Per probe (measured median 0.082 ms):

1. `PdhOpenQueryW(null)`
2. `PdhAddEnglishCounterW` × 3 (the three counters in §0.4)
3. `PdhCollectQueryData` once
4. `PdhGetFormattedCounterArrayW(PDH_FMT_LARGE)` × 3, growing the buffer on `PDH_MORE_DATA`
5. `PdhCloseQuery` in `finally` (`SafeHandle`-derived wrapper)

Instance filtering: parse `pid_<pid>_luid_<hi>_<lo>_phys_<n>`; keep items whose LUID equals the selected
GPU's; **sum across all `phys_N`** for that LUID (§0.7).

**Per-item status mapping** — this is what keeps normal exits visually distinct from probe failures (AC-10):

| Condition | Meaning | Result |
|---|---|---|
| item status `ERROR_SUCCESS` | measured | observation with a value |
| item status `PDH_CSTATUS_NO_INSTANCE` | process exited between add and collect | **absent** — no observation, no marker (§13.5) |
| any other non-success **item** status | unreadable | **missing** — `null` observation, snapshot becomes `Partial` (§13.4) |
| **`Dedicated Usage` array** returns a non-success status (e.g. `PDH_NO_DATA`, `PDH_CSTATUS_NO_OBJECT` mid-run) | the primary metric is unusable | **`Failed`** with `FailureReason` |
| **`Shared Usage` array** returns a non-success status | only secondary diagnostic data is lost | every `SharedBytes = null`; **outcome unchanged** |
| **`GPU Adapter Memory` array** returns a non-success status | total-VRAM context lost | `TotalDedicatedBytes = null`; outcome **`Partial`**; LUID-presence check skipped this probe |
| **zero process instances returned while the selected adapter instance is present** | implausible; treat as a lost probe, never as "everything exited" | **`Failed`** with `FailureReason` |
| selected LUID absent from the `GPU Adapter Memory` array | adapter gone (TDR / driver reset) | **`Failed`** + trigger DXGI re-resolve (§9) |
| exception anywhere | whole probe lost | **`Failed`** with `FailureReason` (§13.3) |

The three array rows are deliberately **not** symmetric. Failing the whole probe because the *secondary*
`Shared Usage` array failed would discard perfectly good dedicated measurements, against §5.2 ("do not discard
the whole snapshot unless the entire probe failed") and §5.1 (shared memory is "secondary diagnostic
information only"). Only the primary array can fail the probe.

The zero-instance row exists because a non-success return that does **not** throw would otherwise yield an
`Ok` sample carrying no observations — which reads as *every application exiting normally at once*, with no
disruption marker, fabricating exactly the picture §5.2/AC-8 forbid. It is safe only because `System` (pid 4)
holds an instance on every WDDM adapter (§0.7); if that ever ceased to hold for the selected adapter, the app
would show a perpetual `Probe failed` marker. The assumption is recorded here so the rule stays explicable.

The exact numeric values of `PDH_CSTATUS_NO_INSTANCE` and siblings are asserted by an integration test
rather than trusted from memory.

GPU enumeration (`DxgiAdapterEnumerator`): `CreateDXGIFactory1` → `EnumAdapters1` → `GetDesc1` via raw vtable
calls with `delegate* unmanaged[Stdcall]` (validated in the prototype). Adapters present in the counters but
absent from DXGI (one such LUID exists here) are tolerated and left unnamed. No NuGet dependency for interop.

### 5.2 Metadata resolver

Per probe, for **every** PID in the instance list: `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` +
`GetProcessTimes` → creation time. Measured 1.2 ms for 59 PIDs; doing this every probe (not once) is what
makes PID-reuse detection possible (§5.4).

Identity resolution runs **once per session**, with the §5.3 fallback chain:

1. `QueryFullProcessImageNameW` → full path → `ExecutablePath`
2. else a `Process.GetProcesses()` snapshot → `ExecutableName`
3. else `"pid:<n>"` → `PidFallback`

**Resolution policy — bounded, and tiered separately.** Each session carries a retry budget of **3 probes**:

- **Tier 1 is retried while `Kind != ExecutablePath`** and the budget lasts. It costs microseconds, so
  retrying it is free — and it must be retried, because a *single* transient `OpenProcess` race on an
  otherwise-accessible process would otherwise freeze that session at `name:python.exe` while its siblings
  are keyed by full path, splitting one program into two application rows in §7.1.
- **Tier 2 runs only while `Kind == PidFallback`** and the budget lasts. This is the expensive one.

This bound matters. `System`, `csrss` and `dwm` are permanently path-denied *and* permanently present, so a
naive "retry while the path is unresolved" rule would leave at least one PID unresolved forever and fire the
23 ms, ~511-object `Process.GetProcesses()` scan on **every** probe — roughly 300× the 0.082 ms probe itself,
destroying the "0 bytes per probe, 0 GCs" result in §0.4. Under the rule above those three resolve to
`ExecutableName` on their first probe and are never rescanned. PPL denial is likewise permanent, so retrying
it indefinitely buys nothing.

**Friendly display name is resolved here, not in the analyzer.** `FileVersionInfo.GetVersionInfo(path)` reads
the executable from disk; doing that inside `Analyze` would make a "pure" function perform I/O and would force
Core tests to touch the file system. The resolver populates `ProcessIdentity.DisplayName` once per session
(`FileDescription` when non-empty, else the file name without extension) and the analyzer only reads it.

The §15 probe-duration counter covers **snapshot acquisition plus metadata resolution**, not the PDH call
alone, so the reported figure is the real per-cycle cost.

**Measurement is never discarded because metadata failed** (§5.3): identity lives on the session record, not
on the observation.

### 5.3 Absent vs missing vs failed

| Situation | Model | Chart | Marker |
|---|---|---|---|
| No counter instance for the PID (exited / stopped using the GPU) | **no observation** | `NaN` → line ends / breaks | **none** (§13.5) |
| Instance exists, value unreadable | `ProcessObservation(..., null)`, sample `Partial` | `NaN` → gap | `Partial sample` (§13.4) |
| Whole probe threw / adapter gone | `GpuSample(Failed, [])` | every series `NaN` | one `Probe failed` (§13.3) |
| Elapsed since previous sample > 2 × interval (sleep/resume) | recorded as skipped cycles | `NaN` break in every series (§7.8) | `Sampling paused` (neutral, distinct) |

Never `0`, never carry-forward (AC-8).

### 5.4 Session identity and PID reuse

`ProcessSessionId` is an **opaque monotonically increasing `long`** assigned at first sight. It never changes,
so **no observation is ever re-keyed** — a late upgrade from `PidFallback` to `ExecutablePath` updates only
the mutable session record.

Tracker keeps `(Pid, CreationTicks) → ProcessSessionId` plus, per session, whether it was absent from any
intervening sample.

| Case | Rule |
|---|---|
| Creation time known, same `(pid, creationTicks)` | same session |
| Creation time known, different creation time for the same pid | **new session** (exact PID-reuse detection) |
| Session has `CreationTicks == 0`, pid continuously present, creation time **now readable** | re-run tier 1: **same session + adopt the ticks** if the resolved file name matches the session's `ExecutableName` (case-insensitive), otherwise **new session** |
| Creation time denied (`0`), pid continuously present | same session |
| Creation time denied (`0`), pid absent in ≥1 intervening **`Ok` or `Partial`** sample, then reappears | **new session** |

Two constraints on the last two rows are load-bearing:

- **Absence is evaluated over `Ok` and `Partial` samples only.** A `Failed` sample carries no observations at
  all, so *every* PID is trivially "absent" in it. Counting that as absence would mean a single failed probe
  splits the session of every handle-denying process — `System`, `csrss`, `dwm` and the PPL game this rule
  exists for — fragmenting PID drill-down and manufacturing the same fake-exit defect the rule was meant to
  prevent. A failed probe is evidence of nothing.
- **A denied→known creation time is an upgrade, not a new process — but only if the name still matches.**
  `OpenProcess` can fail transiently while a process is still starting, yielding `0` on one probe and a real
  value on the next; without adoption, "different creation time → new session" would split a perfectly
  continuous process. Adoption without the name check would be worse, though: a handle-denying process can
  exit and have its PID reused *within a single interval* by an accessible one, so no `Ok`/`Partial` sample
  ever sees it absent. The new process would then inherit the old session's frozen identity — a game's name,
  say — for its whole lifetime, and would never be re-resolved because its kind is no longer `PidFallback`.
  Its VRAM would be grouped under the wrong application permanently. The name check keeps the transient-denial
  case working while making that mis-attribution impossible.

The absence rule exists because handle-denying processes are not only boot-lifetime OS processes: PPL /
anti-cheat-protected games also deny `PROCESS_QUERY_LIMITED_INFORMATION`, are among the heaviest VRAM
consumers, and do restart. This is what makes §6.2 work (app exits and restarts → one application history
with a gap, two PID sessions).

### 5.5 Sampler service

`PeriodicTimer` on a dedicated background task. Each tick: snapshot → resolve metadata → detect a skipped
stretch → append `GpuSample` → trim → analyse → publish an immutable `MonitorSnapshot`.

**Timestamps are monotonic by construction.** `GpuSample.TimestampUtc` is *not* read from the wall clock per
tick; it is a wall-clock anchor taken at start plus the elapsed monotonic delta. Two things follow: sample
timestamps can never move backwards (a manual clock correction or a w32time step larger than the interval
would otherwise make `RollingHistoryStore.Append` throw and kill the sampler), and §7.2, §7.8 and the
skipped-cycle counter below all measure the *same* quantity, so they cannot disagree. The anchor is re-based
whenever the wall clock drifts from anchor + delta by more than one interval, recorded as a skipped stretch.

**Every tick is wrapped.** Any exception escaping snapshot, metadata resolution, append, trim or analysis is
caught, recorded as a `Failed` sample with `FailureReason`, counted, and the loop continues. An unhandled
exception on the background task would otherwise kill monitoring silently while the tray went on showing
stale values with no marker and no rising error count — precisely the failure mode §16.2 forbids.

**Timer mechanics.** `PeriodicTimer` permits only one outstanding `WaitForNextTickAsync`, so the pending
timer task is held in a local across loop iterations and re-created only once it has actually completed;
re-issuing it after a channel wake would throw `InvalidOperationException`. An interval change assigns
`timer.Period` rather than constructing a new timer.

Health (§15): probe duration (last/avg/max), last successful sample, **`Failed` count** (the user-facing
"probe error count"), **`Partial` count shown separately**, late cycles (delta > 1.5 × interval), skipped
cycles (delta > 2 × interval).

Sampling never touches WPF objects (§18). Settings changes arriving from the UI are posted to this loop
through a `Channel<SettingsChange>` — never applied concurrently from the UI thread.

The loop awaits `Task.WhenAny(timer.WaitForNextTickAsync(), channel.Reader.WaitToReadAsync())`, so a settings
change wakes it immediately and runs trim + analyse + publish **without probing**. Waiting for the next tick
would leave a threshold change invisible for up to a full interval (a minute at the maximum interval), which
§14's "take effect immediately" does not allow.

**A "GPU changed" is keyed on `GpuSelector` equality, never on the runtime LUID.** Only a genuine change of selected
adapter clears the history window. A driver reset or TDR gives the *same physical GPU* a *new LUID*; keying on
`GpuId` would treat that as a GPU change and wipe the hour of history the user most wants to inspect at
exactly the moment something went wrong — directly against §16.2's "preserve previous retained history". On a
same-selector LUID change the runtime key is updated in place and history is kept, so the `Gpu` recorded on
`ProcessSessionInfo` may legitimately differ across a reset; nothing filters samples by the current `GpuId`.
(`GpuSample` itself carries no GPU field — the monitored adapter is a property of the session, not of the
sample.)

---

## 6. Rolling history store (§3.3, §7)

`Queue<GpuSample>` plus `Dictionary<ProcessSessionId, ProcessSessionInfo>`. At 10 s over 60 min that is 360
samples — §18 warns against over-engineering this.

**The store retains `HistoryWindow + DemotionGrace`**, because the hysteresis rule in §7.5 must evaluate a
trailing `HistoryWindow` sum at instants as old as `now − DemotionGrace`. Retaining exactly that much is
necessary and sufficient.

Two horizons, deliberately distinct:

- **Visibility / retention / export / analysis input**: `[now − HistoryWindow, now]`. §7 semantics are
  governed by this horizon alone, so the extra tail never keeps a consumer inspectable longer than the spec
  allows (AC-7).
- **Hysteresis evaluation only**: the full `HistoryWindow + DemotionGrace`.

`Trim(now)` dequeues below the **outer** horizon, then drops session records with no remaining observation.
Session records must survive across the whole retention horizon because the hysteresis predicate needs
application grouping for tail samples too.

### 6.1 The consumer universe — exactly one definition

Because session records outlive the visible window, "which consumers exist" must be pinned to the visible
window alone, or the grace tail would leak consumers into the UI:

- **Universe** = `{ applications with ≥ 1 observation in [now − HistoryWindow, now] }`.
- **`Aggressive` ⊆ Universe**, **`Other` = Universe \ Aggressive**. Nothing outside the universe is ever
  listed, charted or exported.
- **Only the hysteresis predicate (§7.5) may read the grace tail**, and it may only *keep* a member of the
  universe in `Aggressive`; it can never add a consumer to the universe.

Without the subset constraint an application could sit in `Aggressive` with **zero** samples inside the
visible window — reachable whenever `DemotionGracePeriod ≥ AggressiveDurationThreshold`, since both are
user-configurable — while §7 requires it to have been removed entirely. Retention stays sample-driven and
never classification-driven (§7, AC-7).

Corollary for ranking (§7.6): an application held in `Aggressive` purely by grace may have no qualifying
sample inside the visible window; its "average dedicated VRAM during qualifying samples" is then **0**, which
sorts it below genuinely-qualifying peers. This is intended.

### 6.2 Window and grace changes at runtime

Shrinking either re-trims immediately. **Growing** either leaves a transient: the store still holds only the
old `W + G`, so `cum` at the oldest evaluation instants is understated until enough new samples accumulate,
and an application can be demoted slightly early during that window. The store retargets to the new
`W + G` on the change; the transient is bounded by the size of the increase. As before, growing the window
cannot resurrect already-evicted data.

---

## 7. Analysis layer (§9)

```csharp
static AnalysisResult Analyzer.Analyze(IReadOnlyList<GpuSample> retained,     // window + grace
                                       IReadOnlyDictionary<ProcessSessionId, ProcessSessionInfo> sessions,
                                       MonitorSettings settings,
                                       IReadOnlyList<string> previousTrayTop5,
                                       DateTimeOffset now);
```

Pure: no mutable state, no GPU, no ambient clock. This delivers §14 (settings changes re-evaluate retained
history), §18 (off the UI thread) and §19.1 (testable without a GPU). ~360 samples × ~40 apps ≈ 15 k
operations per pass.

`MonitorHealth` is **not** produced here — its counters are cumulative since process start and are not
derivable from a window. The sampler owns it and merges it into `MonitorSnapshot`.

### 7.1 Application aggregation (§6.1) — missing poisons the sum

Application key = normalized full executable path (`ToLowerInvariant`, trimmed), with §5.3 fallbacks.
Different `python.exe` installs stay separate because their paths differ.

For application `A` at sample `i`, with `present` = A's sessions having an observation at `i`:

| Condition | A's value at `i` |
|---|---|
| `present` is empty | **absent** — no point (line break, no marker) |
| any session in `present` has `DedicatedBytes == null` | **missing** — `null` (gap); A is **excluded from `P`** |
| otherwise | `Σ DedicatedBytes` over `present` |

Summing only the measured sessions of a partially-measured app would be "missing interpreted as zero" at the
aggregate level — forbidden by §5.2 — and would leave an affected series (§13.4) with no gap while feeding an
understated value into the quartile population, the average and the peak.

### 7.2 Sample weight

`w_i = clamp(t_i − t_{i−1}, 0, 2 × max(NominalInterval_{i−1}, NominalInterval_i))`; the first retained sample uses
`NominalInterval`. The `max` matters when the interval is changed at runtime: comparing a fresh 30 s delta
against twice the *old* 10 s interval would clamp a legitimate sample and, in §7.8, also fire a spurious
`Sampling paused` marker on the first sample after the change.
Storing the nominal interval per sample keeps this correct across runtime interval changes (§9.4); the 2×
clamp stops a sleep/resume gap inflating durations.

Check against the spec's own example (§9.2) at 10 s: 6 qualifying + 2 non + 15 qualifying = 21 × 10 s =
**3.5 minutes**. ✔

### 7.3 Per-sample top quartile (§9.1)

Per sample, independently:

1. `P` = applications with a **measured** value at that sample ≥ the monitoring floor.
2. Rank `P` by dedicated descending; ties broken by application key for determinism.
3. `k = max(1, ceil(|P| / 4))`; the first `k` qualify.
4. `|P| = 0` → nobody qualifies.

The floor applies at analysis time only; sub-floor observations are still retained so the threshold can be
changed and re-evaluated (§8). The `max(1, …)` clamp is an interpretation, recorded in §16.

### 7.4 Cumulative aggressive duration (§9.2)

`cum(A, t) = Σ { w_i : t − HistoryWindow ≤ t_i ≤ t, A qualified at i }` — cumulative, not streak-based.

**Computed by prefix sum, not by re-summation.** Per pass, each application's qualifying weights are
prefix-summed once over the retained samples, so every `cum(A, t)` is an O(1) subtraction. Evaluating it
naively at each of the `G / interval` instants would be `O((G/interval) × (W/interval))` per application —
harmless at defaults (8 × 360 × ~40) but roughly 130 M operations every 2 s at settings `Validated()` still
permits. The prefix sum makes the pass `O(samples + apps × evaluationInstants)` regardless.
It decreases naturally as qualifying samples age out (AC-11) because it is recomputed each pass.

### 7.5 Demotion hysteresis (§9.4, AC-13) — rewritten

> Round 1 defined this as "the last `t` at which cumulative time **as of** `t` reached the threshold". That is
> a **prefix** sum, monotone non-decreasing in `t`, so if the app fails the threshold now it failed it at every
> earlier `t` too — the rule could never fire and demotion was instant. The Critic was right; this is the fix.

The key is that `cum(A, t)` above uses a **sliding** window ending at `t`, not a prefix sum. It therefore
*falls* as qualifying samples age out — which is precisely the demotion case, and the one a prefix sum cannot
see.

**A is Aggressive at `now`** iff `A ∈ Universe` (§6.1) **and** there exists an instant
`t ∈ {t_i : now − DemotionGrace ≤ t_i ≤ now} ∪ {now − DemotionGrace, now}`
with `cum(A, t) ≥ AggressiveThreshold`.

That is: it meets the threshold now, or met it at any sampled instant within the last `DemotionGrace` of real
time, each instant evaluated against its own trailing `HistoryWindow`.

**The left endpoint `now − DemotionGrace` is in the evaluation set deliberately.** `cum` *falls* at
non-sampled instants — precisely at `t_j + HistoryWindow`, when sample `j` leaves the trailing window — so the
supremum over the interval is `max(cum(now − G), cum at sampled instants in (now − G, now])`. Omitting the
endpoint would demote up to one sample interval early, making the grace period quietly interval-dependent and
breaking the "stays aggressive for exactly `DemotionGrace`" assertion unless `G` happened to be an exact
multiple of the interval.

Properties: expressed in real time and independent of the sample interval (§9.4); a pure function of retained
history and settings, so threshold/grace/window changes re-evaluate immediately (§14); needs exactly
`HistoryWindow + DemotionGrace` of retained samples (§6).

**Test** (replacing the round-1 test, which would have passed trivially): drive an app above the threshold,
stop it qualifying, then advance a `FakeClock` so its qualifying samples age out; assert it remains in
Aggressive for exactly `DemotionGrace` after `cum` drops below the threshold, then leaves. Repeat with the
sample interval changed mid-run to prove the grace is real-time, not sample-count.

### 7.6 Ranking (§9.3, AC-12)

Explainable, no opaque score: (1) cumulative aggressive time desc, (2) average dedicated VRAM **over
qualifying samples only** desc, (3) peak dedicated VRAM desc, (4) application key asc for determinism.

### 7.7 Tray top-5 (§10.1, AC-14) — rewritten as ordered rules

Let `latest` = the most recent sample with outcome `Ok` or `Partial`.

- **R1 Eligibility.** Eligible iff the app has a **measured** aggregate value at `latest`. Apps absent or
  missing at `latest` are ineligible. (Round 1 allowed an EMA to keep an exited app in a *"current"* top-5
  with a stale number — a carry-forward violation of §5.2/§10.1.)
- **R2 Smoothing.** EMA over retained samples, τ = 30 s:
  `ema ← ema + (1 − exp(−Δt/τ)) · (v − ema)`. The EMA **restarts** (`ema := v`) at the first measured sample
  after any absence or missing value — it never carries across a discontinuity.
- **R3 Membership.** Start from `previousTop5 ∩ eligible`, order preserved; fill empty slots from remaining
  eligible apps by `ema` desc, **ties broken by application key** so the result is deterministic. A challenger
  displaces the incumbent **with the lowest `ema`** (not the one in the lowest position — under R4 these can
  differ), **taking that incumbent's slot**, only if
  `ema(challenger) > ema(lowest) + max(32 MB, 5 % of ema(lowest))`. Displacement is applied **greedily and
  repeatedly** until no remaining challenger clears the margin.
- **R4 Ordering.** Within the five, an app moves ahead of the one above it only if it exceeds it by the same
  margin. One stable bubble pass, so at most one position changes per app per sample.

The margin is deliberately asymmetric — it is charged against the incumbent in both R3 and R4 — so an
A→B→A ping-pong cannot occur, while a genuinely large newcomer still enters on its first eligible sample.
- **R5 Fewer than five eligible.** Show what exists; never pad. If every visible sample is `Failed` there is
  no `latest`, the eligible set is empty, and the tooltip shows no entries rather than stale ones.
- **R6 Display.** Each row shows the **raw current dedicated value at `latest`**, never the EMA. Smoothing
  governs ordering only.

Pure in `(previousTop5, retained, settings)`, so it is unit-testable with scripted sequences.

### 7.8 Chart series construction and sleep gaps

For each plotted application, walk the visible window in sample order emitting a point per sample
(value, or `NaN` for absent/missing/`Failed`). Additionally, **between consecutive samples `i−1` and `i` where
`t_i − t_{i−1} > 2 × max(NominalInterval_{i−1}, NominalInterval_i)`, emit a `NaN` break point in every
series** just after `t_{i−1}` — the same threshold used for the weight clamp in §7.2 and for the
skipped-cycle counter in §5.5, so the three can never disagree.

Without this, OxyPlot connects consecutive points regardless of their time distance, so an app present before
and after a two-hour sleep would be drawn with a straight line across the gap — interpolation forbidden by
§13.1/AC-8. The 2× weight clamp in §7.2 fixes duration accounting only, not rendering.

Such a stretch gets a **`Sampling paused`** marker, visually and textually distinct from `Probe failed` and
`Partial sample`, so §13.2's marker vocabulary stays unambiguous.

### 7.9 Output

```csharp
sealed record AnalysisResult(IReadOnlyList<ApplicationView> Aggressive,   // §11.1
                             IReadOnlyList<ApplicationView> Other,        // every other retained consumer
                             IReadOnlyList<TrayEntry> TrayTop5,
                             IReadOnlyList<ChartSeries> Series,
                             IReadOnlyList<DisruptionMarker> Markers);
```

`Aggressive` and `Other` partition the **consumer universe** of §6.1 — applications with at least one
observation inside the *visible* window. `Other` therefore means every universe member not currently
aggressive: previously aggressive, quiet, spike-only, exited-but-still-retained (§11.1, AC-15/AC-16).
Nothing reachable only through the grace tail ever appears in either list.

### 7.10 Displayed metric definitions

Each of these is a place an implementer could silently fabricate a zero, so each is fixed here:

| Metric | Definition |
|---|---|
| Current | value at `latest`; renders **`—`** when absent or missing, never `0` (AC-8). When `latest` is older than the newest sample, the tray and list show its age, so a stale value is never presented as "current" |
| Average | mean over **visible-window** samples where the app had a **measured** value; absence/missing are excluded, not counted as 0 |
| Peak | max measured value **inside the visible window** -- a grace-tail spike must never inflate a peak the user cannot see on the chart |
| Cumulative aggressive time | §7.4 |
| Probe error count | count of `Failed` samples since start; `Partial` count displayed separately |
| Friendly name | resolved **in `IProcessMetadataResolver`** (§5.2), not in the analyzer: `FileDescription` when non-empty, else file name without extension, cached per path. Reading it is disk I/O and must not happen inside the pure analysis pass |

---

## 8. WPF shell (§10–§13)

- **Tray** — `H.NotifyIcon.TaskbarIcon`; `TrayToolTip` is a WPF `UserControl` with 5 rows of
  `name · current dedicated VRAM`, refreshed on `TrayToolTipOpen`. Left click opens/activates the main window
  (§10.2). Context menu: Open, Export…, Settings…, Exit. Closing the window hides to tray; Exit quits.
  **Fallback**: rich-tooltip hover was not validated on Win11 (§0.9); if it misbehaves, fall back to plain
  `ToolTipText` — 5 rows of `name  1234 MB` ≈ 90 chars, inside the 127-char `szTip` limit.
- **Main window** — left: consumer list split *Aggressive* / *Other*, columns name, current, average, peak,
  cumulative aggressive time (§11.1), with an off-by-default "hide below X MB" display filter on the Other
  list (D1). Expander reveals PID count, full path, shared GPU memory, per-session timing and overlays
  PID/session chart lines (§11.2). Right: OxyPlot chart limited to the configurable top-N by peak plus any
  selected/expanded app (D2). Bottom: health status bar — selected GPU, interval, last successful sample,
  probe error count (§15).
- **Chart** — `DateTimeAxis` bottom; left `LinearAxis` (per-app dedicated); right `LinearAxis` keyed `total`,
  grey, `LineStyle.Dash`, low alpha, independent scale (§12.2, AC-17/AC-18). Zoom/pan disabled on every axis
  (§12.3). Selection highlights and dims others via colour alpha. Markers per §7.8/§13.2. A short note
  explains that per-app values can sum above the total line (§0.5).
- **Settings window** — interval, history window, monitoring floor, aggressive threshold, demotion grace,
  chart top-N (D2), display filter (D1), Start with Windows, selected GPU (§14). Runtime-safe changes are
  posted to the sampler loop and re-evaluate retained history without restart (§14, AC-20).
- MVVM via `CommunityToolkit.Mvvm`; views thin so §19.3 stays honest.

---

## 9. Settings, GPU selection and CLI (§14, AC-20/AC-21)

- `%APPDATA%\VramMonitor\settings.json`, `System.Text.Json` with a source-generated context.
- Atomic write (temp + `File.Move(overwrite: true)`). A corrupt file falls back to defaults and is reported
  through monitor health, not a dialog.
- All values validated and clamped on load.
- Defaults: interval **10 s**, window **60 min**, aggressive threshold **3 min**, demotion grace **60 s**,
  monitoring floor **100 MB**, chart top-N **10**.
  The floor comes from the measured distribution (§0.6): at 100 MB ~17 of 59 GPU processes participate,
  cleanly separating real consumers (601/475/411/385 MB) from noise (99/68/64 MB) — inside §8's suggested
  50–100 MB band, finalized during provider validation exactly as §8 asks.

**GPU selection persists a `GpuSelector`, never a LUID.** LUIDs are unique only until the next restart and are
reallocated on driver update or TDR recovery, so a persisted LUID would silently match nothing after a reboot.
At startup DXGI enumeration resolves a **persisted** selector through three tiers only:
`(Vendor, Device, SubSys, Description)` → `(Vendor, Device)` → `Description`. If several adapters match a
tier (two identical cards), they are disambiguated by `Ordinal`. **If no tier matches, that is a §16.3
blocking startup error** and the user is asked to pick a GPU — monitoring does not start.

Falling back to "ordinal, then first non-software adapter" is reserved for the **first run**, when no selector
is persisted at all. Applying that fallback to a persisted selector would make the error case unreachable and
silently monitor the wrong adapter: a saved RTX 3090 selector on a machine where the card is absent or
disabled would resolve to the Intel iGPU, which also publishes `GPU Process Memory` counters, and the user
would see a plausible-looking but entirely wrong picture.

At runtime, if the `GPU Adapter Memory` array lacks the selected LUID the probe is **`Failed`** (never
`Ok`-with-no-instances, which would fake a mass "normal exit" of every application) and DXGI is re-enumerated
to re-resolve the selector.

**First run, when nothing is persisted**, defaults to the non-software adapter with the largest
`DedicatedVideoMemoryBytes` — not simply the first enumerated one, since DXGI order does not guarantee the
discrete card comes first and a display attached to the iGPU could otherwise make the iGPU the permanent
choice. The selection is persisted only after a first probe against it succeeds.

- CLI overrides, current launch only, never persisted: `--interval-seconds`, `--history-minutes`, `--floor-mb`,
  `--aggressive-minutes`, `--grace-seconds`, `--gpu <ordinal|description-substring>`, `--chart-top-n`,
  `--start-hidden`. Hand-rolled parser, no dependency.
- Start with Windows: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (verified writable unelevated).

---

## 10. Export (§17)

Triggered from tray and main window.

**No lock is required — but only because the sampler publishes copies.** `RollingHistoryStore.Samples` and
`.Sessions` expose the *live* `List<>` and `Dictionary<>`, which the sampler mutates on the very next tick
(`RemoveRange`, `Add`). Publishing those references would leave export on the UI thread racing with trimming,
reading torn state or throwing. So at publish time the sampler builds:

```csharp
sealed record MonitorSnapshot(
    GpuInfo Gpu,
    MonitorSettings Settings,
    DateTimeOffset TakenUtc,
    IReadOnlyList<GpuSample> Samples,                                  // copied VISIBLE slice, not W+G
    IReadOnlyDictionary<ProcessSessionId, ProcessSessionInfo> Sessions,// copied, universe members only
    AnalysisResult Analysis,
    MonitorHealth Health);
```

`Samples` is `store.Samples` from `FirstVisibleIndex(now, HistoryWindow)` onwards, copied into a fresh array —
the store already exposes that index for exactly this purpose. **Export therefore covers the visible window**,
matching §6.1; the `W + G` tail is an analysis implementation detail and is never exported. Copying ~360
references once per tick is negligible against the 0.082 ms probe.

- **JSON** (canonical, lossless, §17.1): schema version, GPU metadata, effective settings, every sample with
  timestamp and outcome, per-session observations with `null` for missing, session identity and lifetimes,
  application identity, derived metrics, aggressive-duration data. Round-trips (§19.1).
- **CSV** (§17.2): two files, never mixed row types — `<base>-observations.csv` (one row per sample × session)
  and `<base>-applications.csv` (derived summaries). Missing values are empty fields, never `0`.
- **`CultureInfo.InvariantCulture` everywhere**, RFC 4180 quoting — non-negotiable on this `uk-UA` machine.

---

## 11. Testing (§19)

`xunit.v3` 4.0.1 on **Microsoft.Testing.Platform** (`OutputType=Exe`, repo-root `global.json`; no
`Microsoft.NET.Test.Sdk`, no `xunit.runner.visualstudio` — VSTest is unsupported on SDK 10, §0.9).
Assertions via xunit's own `Assert`. A deterministic `FakeClock` and scripted `FakeGpuMemoryProvider` mean
nearly everything runs without a GPU.

`Core.Tests` — at least one test per §19.1 bullet: rolling-window cleanup; consumer lifetime; executable-path
grouping (two different `python.exe` paths stay separate); multiple PIDs per application; PID/session restart
producing one app history with a gap and two sessions; **PID reuse with a changed creation time**; **PID reuse
with a denied creation time across an absence**; metadata fallback chain; missing-sample semantics (never 0,
never carry-forward); **a partially-measured app gapping rather than summing measured sessions only**;
monitoring-floor filtering; per-sample top quartile at `|P|` = 0/1/3/4 and ties; cumulative aggressive duration
including the spec's 3.5-minute example; window aging reducing duration; **demotion grace driven by aging, and
stable across an interval change**; ranking tie-breaks at each level; **tray top-5 eligibility, EMA restart
after a gap, margin-gated membership and ordering, raw-value display**; **sleep-gap NaN break injection**;
retained non-aggressive consumers; retention after process exit; cleanup when the final sample ages out;
instance-name parsing incl. multi-`phys` and multi-LUID inputs; JSON round-trip; CSV correctness **under a
forced `uk-UA` culture**.

Round-2 additions, one test each: a **`Failed` sample does not count as absence** and so does not split a
denied-handle session; a **denied→known creation time is adopted**, not treated as a new process; the
**consumer universe excludes grace-tail-only applications** even when `DemotionGrace ≥ AggressiveThreshold`;
an application **held by grace with no qualifying sample in the visible window ranks with average 0**; the
**hysteresis left endpoint `now − grace` is evaluated**, asserted with a grace that is deliberately *not* a
multiple of the interval; an **interval change does not fire a spurious `Sampling paused` marker**;
**tier-2 metadata stops running** once every session is resolved to at least `ExecutableName`; **tray
displacement is greedy, lowest-EMA-based and key-deterministic**; an **array-level PDH failure yields
`Failed`, not an empty `Ok`**; a **persisted selector that matches nothing is a blocking error, not a
silent fallback**; a **same-selector LUID change preserves history**.

`Windows.Tests` — real PDH provider returns a plausible snapshot; **asserted numeric values of
`PDH_CSTATUS_NO_INSTANCE` / `PDH_CSTATUS_NO_OBJECT`**; LUID↔DXGI mapping; `GpuSelector` resolution; live
instance-name parsing; metadata resolution including an access-denied PID; settings round-trip and
corrupt-file recovery; autostart registry write/read/remove. GPU-dependent tests **skip** (not fail) when no
adapter exposes the counters.

Per §19.3, no large UI automation suite; view-model logic is covered above, the UI by §13's manual checks.

---

## 12. Implementation steps

Each step ends with a build; test steps end with a green run.

| # | Step | Spec |
|---|---|---|
| 1 | Solution, 5 projects, `Directory.Build.props`, `Directory.Packages.props`, `global.json` (MTP) | §3.1, §0.9 |
| 2 | Core domain model and abstractions (§4) | §3.2, §5 |
| 3 | `RollingHistoryStore`, dual-horizon trimming (`window + grace`), session cleanup | §3.3, §7 |
| 4 | Tests: rolling window, consumer lifetime, cleanup on last-sample expiry, dual horizon | §19.1 |
| 5 | Session tracker (opaque ids, PID-reuse rules), application aggregation, identity fallback | §5.3, §5.4, §6.1 |
| 6 | Tests: grouping, multi-PID, restart, both PID-reuse rules, metadata fallback, missing-poisons-sum | §19.1 |
| 7 | Analyzer: floor, per-sample quartile, sample weighting, cumulative aggressive duration | §8, §9.1, §9.2 |
| 8 | Tests incl. the 3.5-minute example, quartile edges, window aging | §19.1 |
| 9 | Analyzer: demotion hysteresis (§7.5), ranking, tray top-5 (§7.7), series builder + sleep gaps (§7.8) | §9.3, §9.4, §10.1, §13 |
| 10 | Tests: aging-driven demotion, interval change, tie-breaks, top-5 rules, NaN break injection | §19.1 |
| 11 | Settings model, `GpuSelector`, JSON persistence, validation, CLI overrides | §14 |
| 12 | JSON + CSV exporters (InvariantCulture, RFC 4180) | §17 |
| 13 | Tests: JSON round-trip, CSV under `uk-UA` | §19.1 |
| 14 | `VramMonitor.Windows`: PDH interop + provider + status mapping; DXGI enumerator + selector resolution; metadata resolver; autostart | §3.2, §4, §16.3 |
| 15 | Integration tests: real provider, PDH status constants, LUID mapping, selector resolution, settings, autostart | §19.2 |
| 16 | `SamplerService`, health counters, skipped/late detection, settings channel, snapshot publication | §3.2, §15, §18 |
| 17 | WPF shell: bootstrap, composition root, tray icon + rich tooltip (+ `szTip` fallback), single-instance mutex | §10 |
| 18 | Main window: consumer list, aggressive/other split, expansion, details, display filter | §11 |
| 19 | Chart: dual axes, gaps, three marker kinds, selection dim/highlight, legend toggles, top-N | §12, §13 |
| 20 | Settings window; live re-evaluation; GPU change clears window | §14 |
| 21 | Export UI wiring; blocking-startup-error surface | §16.3, §17 |
| 22 | Validation pass (§13 below) | §18, §22 |
| 23 | `README.md` + handover report | §21.7 |

---

## 13. Validation plan (AC-24, AC-25, §21.7)

1. `dotnet build` clean, warnings-as-errors.
2. Full unit suite green; report counts only.
3. Integration suite green against the real RTX 3090.
4. **Runtime** — tray icon and top-5 tooltip render on Win11 (confirms or triggers the `szTip` fallback);
   window opens on click; values match Task Manager's *Dedicated GPU memory* for 3 named processes; total
   series matches `nvidia-smi` within a few hundred MiB.
5. **CUDA** — run the 3 GB torch allocation (§0.8) while the app is live; confirm the app appears, ranks
   correctly, and is classified aggressive after the threshold.
6. **Gap** — start and kill a GPU process; confirm the line ends with **no** error marker (§13.5); restart it
   and confirm a new segment under one application history with two PID sessions (§6.2).
7. **Failure** — inject a provider fault via a debug switch; confirm a `Probe failed` marker, gaps on all
   series, preserved prior history, incremented error count, no dialog spam (§16.2).
8. **Overhead (AC-25)** — record probe duration over ≥30 min of real running; report min/median/p95 plus the
   app's own CPU and working set. Provider baseline already measured (§0.4).
9. **Soak** — ≥60 min so the window fills and eviction runs; confirm flat memory and handle count for the
   **whole application** (the provider alone is already proven flat over 9000 probes).
10. **Sleep/resume** — suspend the machine or simulate a clock jump; confirm a `Sampling paused` marker, a
    genuine break in every series, and no line drawn across the gap.
11. **Export** — JSON round-trips; CSV opens correctly with `.` decimals despite `uk-UA`.
12. **Settings** — change window/threshold/grace/top-N at runtime; confirm retained history is re-evaluated
    without restart (AC-20).

---

## 14. Known limitations to carry into handover

- Per-app dedicated values can sum above the total line (§0.5); this is how Windows attributes shared
  surfaces, and normalising would fabricate data.
- Anti-cheat-protected (PPL) games were not available to test; their path resolution will fall back to
  executable name, and their session identity relies on the absence rule in §5.4.
- Multi-`phys` (partitioned/LDA) GPUs cannot be tested on this hardware; the sum-across-`phys` rule is
  unit-tested only.
- Growing the history window at runtime cannot resurrect already-evicted samples.
- `PeriodicTimer` behaviour immediately after resume is unmeasured; gap detection deliberately relies on the
  monotonic clock instead.

---

## 15. Deliberate deviations and interpretations

1. **No enhanced-accuracy provider (§4).** NVML per-process VRAM is unavailable on GeForce/WDDM (§0.4), so
   the zero-friction PDH provider is simultaneously the most accurate option available. §4 phrases the feature
   permissively. `IGpuMemoryProvider` keeps the door open.
2. **Total VRAM read from `GPU Adapter Memory`, not summed** (§0.5). Per-app values reported raw, matching
   Task Manager.
3. **Quartile floor of one.** `k = max(1, ceil(|P|/4))` means that with 1–4 applications above the floor,
   exactly one is always marked. On a lightly loaded desktop the single biggest app therefore becomes
   aggressive after 3 minutes. Defensible under §9.1 but not stated there; recorded here and in the handover.
   Ties at the k-th position are cut by application key.
4. **A third marker kind, `Sampling paused`** (§7.8), beyond §13.2's `Probe failed` / `Partial sample`. A
   sleep gap is neither a failure nor a partial probe, and labelling it as one would misreport it.

---

## 16. Human decisions (settled before Critic)

- **D1 — "Other consumers" list.** Show **all** retained consumers (faithful to §7) plus a *hide below X MB*
  display filter, **off by default**. Presentation-only: never affects retention, analysis, the aggressive
  population, or export.
- **D2 — Chart series cardinality.** Plot the top **N** applications by peak dedicated VRAM, always plus any
  selected or expanded application. **N is user-configurable**, default **10**, persisted as
  `chartTopApplications`, editable in Settings, overridable via `--chart-top-n`, applied live, clamped 1–50.
- **D3 — Self-monitoring.** VRAM Monitor **shows its own process** like any other consumer.

---

## 17. Review dispositions — round 1

| Finding | Disposition |
|---|---|
| **BLOCKER-1** hysteresis is a no-op | **ACCEPTED.** Confirmed the maths: a prefix sum is monotone, so the rule could never fire. Rewrote §7.5 using a *sliding* trailing-window cumulative evaluated at each sampled instant in `[now − grace, now]`; store now retains `window + grace` (§6) with a separate visibility horizon so AC-7 is unaffected. Chose the Critic's option (b) over (a) because it keeps `Analyze` pure and therefore still satisfies §14 when the threshold itself changes. Replaced the trivially-passing test. |
| **MAJOR-2** aggregation treats missing as zero | **ACCEPTED.** §7.1 rewritten: *absent* sessions contribute nothing, any *missing* session poisons the app's value to `null` for that sample and excludes it from `P`. |
| **MAJOR-3** LUID persisted as GPU identity | **ACCEPTED.** Added `GpuSelector` (Vendor/Device/SubSys/Description/ordinal) as the persisted identity, `GpuId(Luid)` demoted to a runtime-only key (§4, §9). Stale LUID now yields `Failed` + DXGI re-resolve instead of a fake mass exit. `--gpu-luid` replaced by `--gpu`. Added assumption 12. |
| **MAJOR-4** sleep/resume interpolation | **ACCEPTED.** §7.8 injects a `NaN` break in every series when the inter-sample delta exceeds 2 × interval, with a distinct `Sampling paused` marker (§15 deviation 4). Gap detection uses the monotonic clock, sidestepping the unmeasured `PeriodicTimer` resume behaviour (§14). |
| **MAJOR-5** tray top-5 underspecified / carries forward | **ACCEPTED.** §7.7 rewritten as six ordered rules R1–R6: eligibility from the latest sample, EMA restart across gaps, margin-gated membership *and* ordering, no padding, raw current value displayed. |
| **MAJOR-6** session identity gaps (a)(b)(c) | **ACCEPTED, all three.** (a) creation time is now queried **every probe** (§5.2, affordable at the measured 1.2 ms). (b) absence + unknown creation time starts a new session, explicitly motivated by PPL/anti-cheat games; assumption 11 rewritten. (c) `ProcessSessionId` is now an **opaque monotonic id** frozen at first sight, so no observation is ever re-keyed. |
| **MINOR-7** `PDH_CSTATUS_NO_INSTANCE` mislabelled | **ACCEPTED.** §5.1 adds a full status-mapping table: `NO_INSTANCE` → absent, other non-success → missing, adapter-instance-absent → `Failed`. Constants asserted by an integration test rather than trusted. |
| **MINOR-8** test packaging will not run | **ACCEPTED, with a correction.** The suggested fix (`xunit.runner.visualstudio`) would **not** work: on .NET 10 SDK, `dotnet test` rejects the VSTest target outright. Verified empirically (§0.9) — the working setup is Microsoft.Testing.Platform via `global.json`, with neither `Microsoft.NET.Test.Sdk` nor the VSTest runner. A test was executed and passed under that configuration. |
| **MINOR-9** displayed metrics undefined | **ACCEPTED.** §7.10 defines current (`—`, never 0), average (measured samples only), peak, error count (`Failed` only, `Partial` shown separately) and friendly name (`FileDescription` → filename). |
| **MINOR-10** quartile edge is a product decision | **ACCEPTED.** Recorded as interpretation 3 in §15 and carried into the handover. Tie cut by key retained as the simpler deterministic rule. |
| **MINOR-11** thread ownership unclear | **ACCEPTED.** §5.5 routes settings changes through a `Channel` applied at a tick boundary; §10 removes the lock entirely by exporting from the immutable published snapshot; `MonitorHealth` moved out of `Analyze` (§7) since its counters are cumulative; GPU change clears the window (§5.5, §9). |
| **MINOR-12** evidence gaps | **ACCEPTED.** Added §0.8: a real 3 GB PyTorch CUDA allocation is fully visible in `Dedicated Usage` and agrees with `nvidia-smi` within ~12 MiB. Named the `szTip` fallback (§8). Stated that the 81→84→81 refresh was observed within one long-lived process (§0.4). PPL games remain untested and are listed in §14. |

No finding was declined.

---

## 18. Review dispositions — round 2

Round 2 verdict was REVISE with **no blocker**; the round-1 blocker fix was confirmed correct
("the maths holds… demotion by aging, at the right time, in real time"). All 14 findings accepted.

| Finding | Disposition |
|---|---|
| **F-1** consumer universe vs the `W+G` buffer is contradictory | **ACCEPTED.** Real: with `DemotionGrace ≥ AggressiveThreshold` an app could sit in `Aggressive` with zero samples in the visible window, violating §7/AC-7. Added §6.1 defining the universe as applications with ≥1 observation in `[now−W, now]`, with `Aggressive ⊆ Universe` and `Other = Universe \ Aggressive`; only the hysteresis predicate may read the tail, and it may only *keep* a member, never add one. §7.5 and §7.9 updated to match; ranking key 2 defined as 0 for a grace-held app with no qualifying visible sample. |
| **F-2** absence rule fires on every `Failed` probe | **ACCEPTED.** A `Failed` sample has no observations, so every PID is trivially "absent" — one failed probe would have split the session of every denied-handle process, reproducing the fake-exit defect MAJOR-3 was raised for. §5.4 now evaluates absence over `Ok`/`Partial` samples only. |
| **F-3** no row for the array call itself failing | **ACCEPTED.** A non-success return that does not throw would have produced an `Ok` sample with zero observations — a fabricated mass exit. §5.1 adds two rows: any non-success array return, and zero process instances while the adapter instance is present, both → `Failed`. |
| **F-4** tier-2 metadata runs every probe forever | **ACCEPTED.** `System`/`csrss`/`dwm` are permanently path-denied *and* always present, so "retry while unresolved" would fire the 23 ms scan every probe — ~300× the probe itself, destroying the 0-allocation result. §5.2 redefines *unresolved* as `Kind == PidFallback`, runs tiers 1–2 only for newly-seen sessions, caps retries at 3 probes, and states that the §15 duration counter covers snapshot + metadata. |
| **F-5** selector fallback can never fail, silently picks the wrong GPU | **ACCEPTED.** The ordinal/first-adapter tail made the §16.3 error unreachable and would resolve an absent RTX 3090 to the Intel iGPU, which also publishes the counters. §9 restricts persisted-selector resolution to three tiers, disambiguates multiple matches by `Ordinal`, raises a blocking error on no match, and reserves the fallback for first run only. |
| **F-6** TDR re-resolve collides with clear-window | **ACCEPTED.** Keying "GPU changed" on `GpuId` would wipe an hour of history on a driver reset — exactly when the user wants it — against §16.2. §5.5 keys it on `GpuSelector` equality; a same-selector LUID change updates the runtime key in place and keeps history. |
| **F-7** evaluation set omits `now − grace` | **ACCEPTED.** `cum` falls at non-sampled instants, so the left endpoint carries the supremum; omitting it demoted up to one interval early and made the grace interval-dependent. Added to the evaluation set in §7.5, with a test using a grace that is not a multiple of the interval. |
| **F-8** growing grace/window has an undocumented transient | **ACCEPTED.** Documented in §6.2; the store retargets to the new `W + G` on change. |
| **F-9** sleep-gap and weight clamp use different intervals | **ACCEPTED.** Both now use `2 × max(NominalInterval_{i−1}, NominalInterval_i)`, shared with the skipped-cycle counter so the three cannot disagree (§7.2, §7.8, §5.5). |
| **F-10** settings changes wait for the next tick | **ACCEPTED.** §5.5 awaits `Task.WhenAny(timer, channel)` and runs trim+analyse+publish without probing when a settings change wakes it. |
| **F-11** denied→known creation time splits a session | **ACCEPTED.** §5.4 adds an adoption row: an existing session with `CreationTicks == 0` whose pid stayed present adopts a newly readable creation time instead of forking. |
| **F-12** tray R3 leaves three choices open | **ACCEPTED.** §7.7 R3 now specifies lowest-by-EMA (not by position), greedy repeated displacement, and application-key tie-breaks; the asymmetric-margin anti-oscillation argument is recorded. |
| **F-13** friendly name does disk I/O inside the pure analyzer | **ACCEPTED.** `FileVersionInfo` resolution moved to `IProcessMetadataResolver`, populating `ProcessIdentity.DisplayName` once per session (§5.2, §7.10), so `Analyze` stays pure and Core tests need no files. |
| **F-14** §0.9 overstates the cause of the test-platform failure | **ACCEPTED — this corrects my own round-1 correction.** "VSTest itself is the unsupported path" was too strong: VSTest remains `dotnet test`'s default mode on SDK 10; what is rejected is an **MTP-v2-enabled** project driven through the VSTest target, and `xunit.v3` 4.x enables MTP v2 by default. The chosen configuration is unchanged and still verified by an executed passing test; §0.9 now states the real cause and records the exact csproj shape, including that `UseMicrosoftTestingPlatformRunner` was **not** required. |

No finding was declined in either round.

---

## 19. Review dispositions — round 3 (final)

Round 3 verdict was REVISE with **no blocker**: "The architecture, provider choice, store, hysteresis maths,
universe rule, and step order are sound, and the code already on disk matches the plan." All 14 round-2
findings were confirmed resolved. 14 new findings, all accepted.

| Finding | Disposition |
|---|---|
| **N-1** "either array fails → `Failed`" discards valid primary data | **ACCEPTED.** There are three arrays, not two, and my wording would have thrown away every good dedicated measurement because the *secondary* `Shared Usage` array failed — against §5.2 and §5.1. §5.1 now gives each array its own row: dedicated → `Failed`; shared → `SharedBytes` null, outcome unchanged; adapter → total null, `Partial`. |
| **N-2** no per-tick exception policy; store throws on a backwards wall clock | **ACCEPTED.** `RollingHistoryStore.Append` already rejects a backwards timestamp, so a w32time step larger than the interval would have killed the sampler task silently while the tray showed stale data — the §16.2 failure mode. §5.5 now (a) wraps every tick, recording escapes as a `Failed` sample, and (b) derives `TimestampUtc` from a wall-clock anchor plus monotonic delta, monotonic by construction. This also closes the F-9 residual, since §7.2, §7.8 and the skipped-cycle counter now measure one quantity. |
| **N-3** adoption + frozen identity can permanently mis-attribute a reused PID | **ACCEPTED.** A handle-denying process can exit and have its PID reused within one interval, so no sample sees it absent; the new process would inherit the old frozen identity forever and never be re-resolved. §5.4 now re-runs tier 1 on adoption and requires a case-insensitive `ExecutableName` match, else starts a new session. |
| **N-4** `MonitorSnapshot` undefined; §10's "immutable" premise not provided by the code | **ACCEPTED.** `Samples`/`Sessions` expose the live collections the sampler mutates, so export would have raced with trimming. §10 now defines `MonitorSnapshot` explicitly and has the sampler publish a **copied visible slice**, which also settles that export covers the visible window, not `W + G`. |
| **N-5** `AggressiveDurationThreshold` may be zero | **ACCEPTED.** `Validated()` will clamp the minimum to 1 s, and §7.5 additionally requires at least one qualifying sample in the trailing window, so a zero threshold cannot mark every consumer aggressive against §8. |
| **N-6** hysteresis cost unstated, blows up at permitted extremes | **ACCEPTED.** §7.4 now specifies a once-per-pass prefix sum making each `cum` O(1); `Validated()` tightens to interval ≥ 2 s and window ≤ 12 h. |
| **N-7** Average/Peak horizon unnamed | **ACCEPTED.** §7.10 pins both to the visible window; §7.7 R5 states the tray is empty when every visible sample is `Failed`. |
| **N-8** freeze at `ExecutableName` can split one application into two rows | **ACCEPTED.** §5.2 now retries **tier 1** while `Kind != ExecutablePath` (microseconds, so free) and restricts the expensive **tier 2** to `PidFallback`. The F-4 cost fix is preserved. |
| **N-9** first-run "first non-software adapter" can pick the iGPU | **ACCEPTED.** §9 first-run default is now the non-software adapter with the largest `DedicatedVideoMemoryBytes`, persisted only after a successful first probe. |
| **N-10** `PeriodicTimer` allows one outstanding wait | **ACCEPTED.** §5.5 holds the pending timer task in a local across iterations and re-creates it only once completed; an interval change assigns `Period`. |
| **N-11** §5.5 referenced a non-existent `GpuSample.Gpu` | **ACCEPTED.** Corrected to `ProcessSessionInfo.Gpu`, with a note that `GpuSample` carries no GPU field. |
| **N-12** zero-instance rule rests on an unstated assumption | **ACCEPTED.** The `System` (pid 4) assumption is now recorded beside the rule in §5.1. |
| **N-13** no test step for `SamplerService` | **ACCEPTED.** Step 16 gains a test step covering skipped/late counting, the settings-channel wake, and per-tick failure handling, using the existing `FakeClock` + `FakeGpuMemoryProvider`. |
| **N-14** small contract gaps | **ACCEPTED, all five.** (a) the challenger takes the displaced incumbent's slot (§7.7 R3); (b) a stale `latest` is shown with its age (§7.10); (c) `FileVersionInfo` on a deleted executable falls back to the file name (§5.2); (d) §0.1 and §12 record that steps 1–4 already exist on disk; (e) the retracted "VSTest is unsupported" claim is corrected in `Directory.Packages.props`. |

**Noted risks, not findings:** the per-item `Partial` rate was not reported from the 9000-probe soak, so
validation step 9 will record it; `SystemClock.MonotonicTicks` uses `Environment.TickCount64`
(`GetTickCount64`), which includes sleep time and so satisfies the gap-detection requirement, recorded in §14;
PPL games remain untested, as §14 already states.

Across three rounds: **40 findings, 40 accepted, 0 declined.** Implementation proceeds per §12.
