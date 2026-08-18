# Session 9 — continuous route and bounded capture publication

**Date:** `2026-08-17`
**Branch/commit:** `codex/main-thread-performance` on `8dfdac973171fd8850b87aec90d0406985ab800f` plus working-tree changes
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

`STATUS.md` identified the corrected four-leg continuous movement/rotation route as the
next evidence task. The route had only completed five-second smoke runs after its camera
pitch was corrected; capture-result publication had reached 11-14 ms in the earlier
teleport route but was not yet established under continuous movement.

The documented route was run with one warm-up lap and two measured laps, 30 seconds per
400-block leg. Hardware, graphics settings, mod configuration, route inputs, and small
CSV artifacts were recorded under `bench/results/2026-08-17-moving-rotation`.

## Work narrative

### 1. Long corrected baseline

The baseline completed all twelve legs with zero settle timeouts. Across the 28 measured
telemetry intervals, total game tick worst p95/p99/max was 1.75/4.00/12.062 ms and capture
publication was 1.50/3.75/12.038 ms. Capture publication therefore reproduced the plan's
threshold and accounted for essentially the whole worst game tick. There were zero ticks
at or above 25 ms, five projection resets, and no worker, mip, storage, or unsaved-state
errors at the final report.

The four endpoint screenshots were terrain-facing and showed no obvious near-camera
projection cutoff. Static images cannot establish transient clipping or turn-around
smoothness, so a human-watched route remains open.

### 2. Result-boundary capture budget

Capture publication now uses the existing 2 ms / 512 KiB `LodDrainBudget` while retaining
the eight-result item ceiling. One oldest item always progresses even when it exceeds a
ceiling; work after it waits. Raw-run bytes are estimated on the capture worker, and
telemetry reports published items/bytes plus pending results/bytes/oldest age.

Scheduling backpressure now covers queued/in-progress jobs, worker results, and
reload-deferred results. The ordinary scheduler enqueues only its remaining capacity instead of checking
the 24-item threshold once and overshooting it by a full batch.

Capture jobs and results also carry the world epoch. A capture still executing during
world teardown can publish after the queue clear, so the owning thread now rejects that
cross-world result instead of relying on queue clearing alone.

### 3. Follow-up measurement

A five-second-per-leg smoke first established live telemetry, bounded queue growth, and
graceful isolated shutdown. The full route was then repeated with the same warm-up and
measured-lap policy.

Across the 28 measured follow-up intervals, total game tick worst p95/p99/max was
1.50/2.50/5.749 ms and capture publication was 1.50/2.25/5.732 ms. The capture maximum
fell 52.4%. Backlog peaked at 9 results / 0.70 MiB / 93 ms old and repeatedly returned to
zero. Average FPS stayed within 0.2% at every waypoint; 1% lows improved 0.7-6.6%. The
sandbox cache evolved between runs, so aggregate FPS changes are supporting rather than
clean causal evidence. The phase maximum, low queue age, and source-level bound are the
stronger evidence.

The follow-up again had zero ticks at or above 25 ms, five projection resets, zero
capture/mesh/mip errors, zero mip backlog or in-flight jobs, and zero unsaved sections or
storage errors/backlog at the final report. One fresh capture result remained 31 ms old
in the last telemetry sample shortly before route completion; the result-boundary policy
is intentionally not a shutdown durability mechanism.

---

## Delivered

- Preserved the first full corrected movement/rotation baseline and same-route
  capture-budget follow-up with machine/settings context and two CSVs.
- Time/byte-bounded capture publication with oldest-item progress and the existing item
  ceiling.
- Combined queued/in-progress capture job/result/deferred-result backpressure with exact
  ordinary-scheduler capacity.
- Capture result byte/age/item telemetry and cross-world epoch rejection.
- Debug build with zero warnings/errors, 900 passing game-backed fast assertions, and 190
  documentation checks under both Windows PowerShell 5.1 and PowerShell 7.

## Decisions

- Reuse the shared 2 ms / 512 KiB drain policy so capture, background load, and foreign
  publication have one understandable owning-thread budget model.
- Budget only at result boundaries. Live block/palette publication cannot safely be
  preempted halfway through one result, so the oldest oversized result must complete.
- Count retained results as capture backpressure; limiting only jobs would allow the new
  publication budget to move unbounded memory from one queue to another.
- Treat aggregate FPS deltas cautiously because the sandbox world/cache continued to
  evolve; accept the change primarily from phase and queue evidence.

## Traps

- A queue clear is not a cross-world guarantee when a worker may still publish its
  in-progress job afterward. Carry a world epoch and reject on publication.
- Checking a backlog threshold once before an eight-item loop can exceed the stated cap by
  seven. Compute remaining capacity and cap the loop itself.
- A 2 ms drain is a boundary policy, not a promise that every call is below 2 ms; one
  admitted result remains non-preemptible.

## Flagged and unverified

- A human still needs to watch the corrected route for transient clipping and turn-around
  stalls; endpoint screenshots are insufficient.
- The remaining single-result capture tail reached 5.732 ms. Splitting publication within
  a chunk column would require a separate correctness design and is not yet justified.
- The route is client-only. Integrated sibling-cache, sweep, server-assist, and
  interrupted-restart scenarios remain open.
- Enabled-versus-disabled allocation telemetry overhead remains unmeasured.
