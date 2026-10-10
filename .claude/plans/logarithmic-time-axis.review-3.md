# Plan review — round 3 (plan-critic)

Verdict: **APPROVE**

All round-2 dispositions verified as applied. The coalescing protocol was walked through all three
orderings of `Post(S2)` relative to the callback's clear and take: S2 is applied exactly once and the only
null take lands in a callback that does nothing; the fences are sound. `DispatcherPriority.Background` is
sound for this app (OxyPlot render and DataBind run at higher priority; input stays ahead). Cadence
assertion now cannot pass at the old 2 s floor. Spec coverage complete; all citations verified.

## Findings

- [MINOR] Settings-wake test: the `UpdateSettings(Settings() with { AggressiveDurationThreshold = … })` call
  (`SamplerLoopTests.cs:83`) must also use the 4-s interval; built from the 1-s floor, `DrainCommands` would
  set `timer.Period = 1 s` and a tick could fire inside the 1.5-s wait.
- [MINOR] Step 6a: use `Interlocked.Exchange(ref scheduled, 0)` for the clear to make the ordering obvious
  instead of relying on the following `Interlocked.Exchange` being a full fence. Taste.
