# Critic review — round 4 (implementation, not plan)

Scope: the IMPLEMENTED CODE for round-3 findings N-1 .. N-14, plus the self-audit that followed.

## Verdict: REVISE — one BLOCKER, six MAJOR, several MINOR

"The N-1 fix itself is correct for the case it targeted and is genuinely tested. The surrounding audit work,
however, left one blocking regression in the very finding (N-2) it claimed to close, plus two residual
fabricate-a-picture paths in `Assemble`, a missing runtime re-resolve, a UI-thread mutation of loop-owned
state, and an unfulfilled complexity claim. All fixes are local; none requires redesign."

## Findings

- **[BLOCKER] N-2 not closed: a backward wall-clock step kills sampling for the length of the step.**
  `NextTimestamp` re-anchored on a backward jump (it used `.Duration()`), leaving the anchor behind the newest
  stored sample. The single clamp fixed only that one return value; every later cycle derived a timestamp
  hours before the stored history, `RollingHistoryStore.Append` threw, and the catch merely counted a failure
  and republished stale analysis — repeating every tick until real time caught up. The existing test pumped
  exactly one cycle after the step, which is the only cycle that worked.

- **[MAJOR] "adapter gone → Failed + DXGI re-resolve" was half implemented.** Nothing consumed the failure;
  `Enumerate()` ran only at startup and in the Settings dialog, so after a TDR the app showed `Probe failed`
  on every tick until restart. Also: `GpuSelector` record equality includes `Ordinal`, so an enumeration-order
  shift after a reset would have been read as a different adapter and wiped history.

- **[MAJOR] N-1 residual: an unreadable adapter *item* fabricated `TotalDedicatedBytes = 0`.**
  `adapterInstanceSeen` was set before the status check and `total = sum` assigned unconditionally, so an
  invalid-status instance produced a measured-looking zero on the series §12.2 plots — the exact
  "missing read as zero" the per-process merge guards against.

- **[MAJOR] N-1 residual: adapter unreadable + zero matching process instances published `Partial` with no
  observations.** That renders as every application exiting at once. The zero-instance rule was gated on
  `adapterInstanceSeen`, but its justification (a system process always holds an instance on a live adapter)
  holds whether or not the adapter array could be read.

- **[MAJOR] `SetGpu` was applied from the UI thread while the loop ran.** It calls `_store.Clear()`,
  `_tracker.Reset()` and clears tray state while the loop appends, trims and enumerates those same ordinary
  collections. Plan §5.5 forbids exactly this.

- **[MAJOR] N-6 not delivered: `cum(A, t)` was O(n), not O(1).** Prefix sums existed, but the index lookups
  were linear scans, so at the permitted extremes (2 s interval, 12 h window, 1 h grace, ~40 apps) a pass was
  roughly 260 M comparisons every 2 s.

- **[MINOR]** Post-probe exceptions counted but never recorded as a `Failed` sample; the settings-wake
  `PublishFromStore` was outside any try. Skipped/late used the current interval rather than
  `2 × max(adjacent nominals)`. Published `Sessions` was the whole W+G dictionary, so export included
  grace-tail-only sessions. `AverageWhileQualifying` spanned W+G, contradicting the §6.1 corollary. The
  first-run GPU was never persisted. `startupProblem` was set even when `--gpu` had succeeded. Dead code:
  `CounterArray.Available` never read, `PdhQueryHandle` unused.

- **[MINOR] Test gaps.** Tray tests supplied incumbents already in EMA order, so a by-position implementation
  would pass; no assertion on the challenger's landing slot; the demotion test asserted far from the
  demotion instant, so omitting the grace-interval endpoint would still pass; `GpuSelectorResolver` had no
  test at all; no `Assemble` case for an invalid adapter item or adapter-unreadable + empty process list.

## Other defects of the N-1 class (code contradicting its own stated contract)

Four, all confirmed: the "non-decreasing by construction" remark on timestamps; the "a missing part keeps the
whole process missing" comment sitting beside code that summed readable adapter partitions only; the "an
empty list means the read went wrong" comment beside code that published that empty list; and the plan's
"O(1) subtraction" claim.
