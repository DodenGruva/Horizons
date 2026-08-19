# Vanilla-readiness shadow tracker — runtime validation, 2026-08-18

Runtime evidence for Phase 1 of `dev/plans/PLAN_CHUNK_AWARE_VANILLA_HANDOFF.md`. The
tracker is pixel-neutral in every run here: the radial handoff remained the sole pixel
owner, no shader or draw decision read readiness state, and no mask exists yet.

Client Vintage Story 1.22.7, dedicated sandbox server on port 42425, warm client cache
reporting 360 Vintage Horizons sections, uncapped frame rate at roughly 450–510 FPS. Runs 1
and 2 used the terrain-facing `bench/routes/moving-rotation.txt`; run 3 used
`bench/routes/steady-stationary.txt`. Stats telemetry was enabled throughout, so allocation
counters were active.

## What the runs were for

The tracker had never executed in a game process. Session 25 shipped it as shadow state
with provisional 256-item / 0.25 ms probe ceilings and explicitly refused to tune them
before runtime evidence existed. These runs supply that evidence, and the first one found
a defect that no harness check had reached.

## Run 1 — `readiness-shadow-2026-08-18` (ready-only maintenance)

Gate result: **failed**, on a 7,021 µs readiness frame against the 5,000 µs ceiling in
force at the time. Everything else passed: 0 probe errors, 0 dropped candidate events,
0 unknown cells, queues drained every interval, oldest queued work never exceeded 2
frames, no renderer phase or Vintage Horizons tick reached 25 ms, and all four waypoints
settled with no timeout.

The important finding is not the gate failure. Interior maintenance requeued only cells
already committed `VanillaReady`, so the sweep could **lose** ownership but never **gain**
it. A cell whose chunk had not finished tessellating when its `ChunkDirty` event was
probed fell to `Pending`, and nothing revisited it — the event necessarily precedes
tessellation, so this is the normal case, not an edge case. The only escape was camera
movement resetting the discovery cursor.

The 21:26:01 interval measures it directly, with the camera turning in place inside one
chunk cell:

```
0 window changes, interval 432744 probes (432744 true/0 false), 0 ready/0 lost transitions
1727 ready / 1801 pending
```

432,744 probes in fifteen seconds, every one of them re-confirming an already-ready cell,
none on the 1,801 pending ones, and no state change. The probe budget was being spent
almost exactly backwards.

## Run 2 — `readiness-maintenance-2026-08-18` (state-agnostic maintenance)

Gate result: **passed**, with the same route, cache, and settings.

| Metric | Run 1 (ready-only) | Run 2 (maintenance) |
|---|---|---|
| Committed ready cells | 1,723 | 1,856 |
| Pending cells | 1,805 | 1,672 |
| Complete columns (of 441) | 215 | 232 |
| Partially ready columns | 3 | 0 |
| Ready cells per Y band | 215/215/215/218/215/215/215/215 | 232/232/232/232/232/232/232/232 |
| Probes | 16,491,337 | 23,912,461 |
| False results | 295,730 | 5,068,333 |
| Engine events accepted | 16,491,337 (all enqueues) | 23,563 (events only) |
| Sweep candidates | not separated | 23,888,898 |
| Probe errors / dropped events | 0 / 0 | 0 / 0 |
| Readiness cost avg / p95 / p99 / max | 20.5 / 50 / 75 / 7,021 µs | 18.4 / 50 / 50 / 988 µs |
| Renderer phase hitches ≥25 ms | 0 in all 44 intervals | 0 in all 44 intervals |
| Steady-state allocation | 0.00 MiB after construction | 0.00 MiB in 42 of 44 intervals |

The rise in false results is the fix working: pending cells are now probed, and most of
them are legitimately unrendered because the tracked window is a square while the vanilla
load region is a circle. Stationary intervals that previously recorded zero false results
now record 99,480–168,096, and one stationary interval gained eight cells with no camera
movement at all — impossible in run 1 by construction.

Cost fell despite 45% more probes. The 7,021 µs outlier did not recur; it is most likely a
single blocking `IsChunkRendered` call that the 0.25 ms deadline cannot preempt, since the
deadline is only checked between probes. That remains unproven, so it is not claimed as
fixed.

## Run 3 — `readiness-stationary-2026-08-18` (stand-still isolation)

Gate result: **passed**, on `bench/routes/steady-stationary.txt` with a 30 s settle and a
60 s measurement at `512020,190,512010`. One waypoint, no movement, no window change in
any interval — exactly the condition run 1 could not make progress in.

```
0 window changes, interval 432485 probes (314645 true/117840 false)
1608 ready / 1920 pending, 201/441 complete columns, 1608 ready transitions
events 5 accepted, sweeps 432480 accepted
```

Run 1's equivalent stationary interval recorded 432,744 probes with **zero** false results
and no attention to its 1,801 pending cells. Run 3 spends roughly 117,000 probes per
interval on non-ready cells while stationary, with 0 errors, 0 dropped events, oldest work
at 0 frames, 19.6 µs average and 100 µs p99, and no renderer phase hitch in any of its
seven intervals. Frame rate held 450.6 average with 0.0% lap spread.

The split event counters also come into their own here: a settled world announces 3–9
`ChunkDirty` events per interval, against ~430,000 scheduled sweep candidates. Before the
split, this line reported "millions of events" and said nothing at all.

### A gate correction this run forced

The first attempt at run 3 failed on `readyTransitions -le 0` while holding 1,608 committed
cells. Committed cells are state; transitions are an interval rate, and a settled
stationary world legitimately produces none. The gate now requires committed state and
merely reports the rate.

The opposite problem also needed fixing: the corrected gate would have **passed** run 1,
whose defect it exists to catch. The runner now counts intervals that probed heavily,
returned nothing but true, and still held pending cells — the signature of a budget spent
entirely on re-confirmation — and fails on any. Verified by replay: run 2's 44 samples
score 0, and a replay of run 1's 21:26:01 interval scores 1.

## What this establishes

- **Whole-column ownership is reachable.** 232 of 441 columns reach 8/8 ready and the
  per-Y histogram is flat, so empty sky chunks and deep chunks report rendered like
  surface ones. Phase 3's CPU whole-mesh skip does not need a geometry-derived aggregate.
- **The budget is not the constraint.** 18.4 µs average and 50 µs p99 with queues drained
  and oldest work at 3 frames. The provisional ceilings were never the limiting factor;
  the scheduling policy was.
- **Steady-state allocation is genuinely zero.** 377 KiB is allocated once when the
  tracker is constructed; 42 of 44 intervals then report 0.00 MiB with a 0.0 KiB worst
  call.

## What this does not establish

- No person has watched this build in motion. Every visual criterion in the plan's player
  acceptance section is open.
- Teleport and live view-distance change are covered by harness checks only; neither route
  exercises them.
- FPS differed between runs by 0.6–1.1% in the direction of run 2 being slower, which is
  inside the intra-run lap spread and is not a controlled comparison. Establishing whether
  state-agnostic maintenance costs anything measurable needs repeated alternating runs.
- One machine, one world, one vanilla view distance, one player.
- Run 3's multi-millisecond outlier (3,151 µs) recurred without a tracker construction in
  that interval, which weakens the construction-cost explanation and strengthens the
  blocking-engine-call one. Still inferred.
- The 7 ms outlier's cause is inferred, not proven.

## Files

- `readiness-shadow-2026-08-18-scenario.json` / `.csv` — run 1 scenario proof and route
  measurements.
- `readiness-shadow-2026-08-18-samples.txt` — all 44 readiness telemetry lines from run 1.
- `readiness-shadow-2026-08-18-phase-us.txt` — per-interval readiness p95/p99/max.
- `readiness-maintenance-2026-08-18-scenario.json` / `.csv` / `-samples.txt` — the same for
  run 2.
- `readiness-stationary-2026-08-18-scenario.json` / `.csv` / `-samples.txt` — the same for
  run 3.
