# Session 26 — runtime-validated readiness and a measured near handoff

**Date:** `2026-08-19`
**Branch/commit:** `codex/main-thread-performance` at base `8cb11ad0fe52e9e05a484d43d470dc12a465a582` (Session 25 and this session committed together as one checkpoint)
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

Session 25 delivered the vanilla-readiness tracker as pixel-neutral shadow state and
explicitly refused to tune its provisional 256-item / 0.25 ms probe ceilings before the
tracker had ever run in a game process. This session supplied that runtime evidence,
fixed what it exposed, and then implemented the first phase in which measured readiness
owns pixels.

The benchmark runner had no readiness assertions, so a run would have produced telemetry
nobody checked. Building the gate came first. PowerShell 7 was absent from the machine at
the start of the session; the user installed it, after which five isolated runs were
performed through `scripts/bench-windows.ps1`.

## Work narrative

1. **Gate before run.** `Get-ClientReadinessRecord` parses every readiness telemetry line
   plus the surrounding phase-cost and allocation lines. `-RequireReadinessConvergence`
   asserts the Phase 1 exit conditions. A static check renders the renderer's own
   `DescribeReadiness` literal with sample values and requires the runner's regex to match
   it, so a format rename fails the fast tier instead of a ten-minute game run. The check
   was mutation-tested by renaming one word in the log line.
2. **Vertical reachability, measured first.** Whole-section vanilla ownership needs every
   vertical chunk of every covered column. If the client never holds the empty chunks above
   terrain, no section could ever classify as fully ready and the planned CPU whole-mesh
   skip would be unreachable. A per-column distribution was added to the tracker before any
   mask work was contemplated. It answered favourably: 232 of 441 columns reach 8/8 with a
   flat per-Y histogram.
3. **Run 1 exposed a scheduling defect no harness check had reached.** Interior maintenance
   requeued only cells already committed `VanillaReady`, so the sweep could lose ownership
   but never gain it. Because `ChunkDirty` necessarily precedes tessellation, a cell's first
   probe usually returns false, leaving it `Pending` with nothing to revisit it; only camera
   movement resetting the discovery cursor could rescue it. One 15-second interval with the
   camera turning in place spent 432,744 probes re-confirming 1,727 already-ready cells and
   none on the 1,801 pending ones.
4. **Fix and confirmation.** Interior maintenance became state-agnostic. Run 2 on the same
   route rose to 1,856 ready cells and 232 complete columns with cost falling from 20.5 to
   18.4 µs average despite 45% more probes. Run 3 isolated the stationary case that run 1
   could not progress in: roughly 117,000 probes per interval now land on non-ready cells.
5. **Telemetry that meant nothing was separated.** `CandidateEvents*` counted every enqueue
   including the tracker's own sweeps, reading 16.5 million and hiding whether engine events
   arrived at all. Engine-announced candidates and scheduled sweep candidates now have
   distinct counters; a settled world announces 3–9 events per interval.
6. **Two gate corrections, both mine.** A 5,000 µs maximum failed run 1 on a single 7,021 µs
   frame that the tracker cannot preempt; the maximum moved to the plan's own 25 ms hitch
   rule and a p99 gate at 500 µs was added, which is the threshold that actually bites. A
   `readyTransitions > 0` gate then failed a settled stationary run holding 1,608 committed
   cells, because transitions are an interval rate and committed cells are state. The
   corrected gate would still have passed run 1, so the invariant that run 1 violated was
   encoded: any interval that probes heavily, returns nothing but true, and still holds
   pending cells now fails the run. Verified by replay.
7. **Phase 2a measured before it was built.** A single global radius is only worth building
   if incomplete columns sit at the frontier rather than beside the player. A shadow-only
   diagnostic reported the nearest not-wholly-owned column at 234 blocks against a 64-block
   constant, with zero partial columns. Only then was the shader uniform rewired.
8. **Phase 2a implemented.** The radius is the nearest not-wholly-owned column minus one
   chunk of margin, clamped to view distance and quantized down. `LodNearHandoffState`
   shrinks in the same frame and holds growth for 500 ms, applying the smallest radius seen
   during the hold. Re-measuring only when committed ownership changes or the camera moves
   four blocks reduced the per-frame cost from 8.4 µs to 1.6 µs over baseline.

---

## Delivered

- Runner readiness gate with convergence, budget, fallback, and confirmation-only
  assertions, plus full readiness telemetry in every scenario record.
- Cross-file contract check matching the runner's pattern against the renderer's own log
  format, mutation-tested.
- State-agnostic interior maintenance, fixing a defect that made readiness gain depend on
  camera movement.
- Separate engine-event and scheduled-sweep candidate counters.
- Vertical column distribution: columns tracked, complete, partial, deepest column, and
  ready cells per Y band.
- `NearestIncompleteColumnBlocks` with incremental per-column ready counts.
- `LodNearHandoffState` and the readiness-driven `cacheHandoffDistance`, with an explicit
  fallback to the established constant.
- Five isolated runs preserved under `bench/results/2026-08-18-readiness-shadow/` and
  `bench/results/2026-08-18-readiness-handoff/`, each with its own README.
- The Release check tier rose from 1,176 to 1,225 assertions, all passing.
- A playtest package the user evaluated in game and found acceptable.

## Decisions

- Measure before building, twice: vertical reachability before contemplating the mask, and
  nearest-incomplete distance before rewiring a pixel. Both measurements were cheap, both
  were decisive, and one of them would have invalidated the phase had it come back badly.
- Gate on committed state rather than interval rates, and encode the specific invariant a
  discovered defect violated rather than only fixing the code.
- Fail toward coverage at join. The derived radius starts small before convergence, showing
  more cached terrain near the camera than the old constant did for the first seconds. The
  rejected alternative — flooring the derived radius at the old constant — would reintroduce
  holes wherever vanilla genuinely is not present.
- Package under a distinct playtest name rather than let `scripts/package.sh` overwrite the
  released `vintagehorizons_0.2.1.zip`.
- Commit Session 25 and Session 26 together. Their edits are interleaved in the same files,
  and hunk-level separation of intertwined work risks a commit that does not build.

## Traps

- **Trigger:** writing a bounded revalidation sweep for state that can be both gained and
  lost. **Failure:** filtering the sweep to committed cells only lets it lose ownership and
  never gain it, and the wasted budget looks like healthy activity in telemetry.
  **Safer:** sweep without a state filter, and assert that probes reach non-committed cells.
- **Trigger:** writing an acceptance gate against counters that reset every interval.
  **Failure:** gating on a rate fails a legitimately settled system and passes a broken one.
  **Safer:** gate on retained state; report rates.
- **Trigger:** adding a value to a log line that a parser reads. **Failure:** an
  interpolation hole containing a quoted string is not machine-readable, and a strict
  alternation cannot match a substituted sample value. **Safer:** compute the value into a
  local first, and keep parser patterns structural with semantics checked in the gate.

## Flagged and unverified

Judgement calls awaiting human review:

- The join-window behavior described above, whose duration and visibility are unmeasured.
- Gate thresholds chosen after seeing data: 25 ms maximum, 500 µs p99, four-block recheck
  distance, 32-block margin, 500 ms growth hold.

Claims lacking their evidence level:

- Frame rate is neutral within noise, not proven neutral: per-waypoint averages moved
  +1.3%, −2.6%, +0.1%, −1.4% with intra-run lap spread up to 1.9%, one run per side.
- No CPU or GPU saving exists yet. Cached fragments inside the radius are still submitted,
  vertex-processed, and rasterized before being discarded.
- The single-unowned-pocket weakness, where one bad column near the camera collapses the
  global radius, was never deliberately reproduced.
- Teleport and live view-distance change remain harness-only.
- The recurring multi-millisecond readiness outlier is attributed to a blocking
  `IsChunkRendered` call by inference, not by proof.
- Human acceptance covers the packaged playtest on one machine, one world, and one view
  distance; seams, flicker at a boundary, and popping were not separately reported.
