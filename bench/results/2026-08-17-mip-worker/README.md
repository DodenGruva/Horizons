# Asynchronous mip worker — preserved short-route evidence

These four CSVs are the small, reviewable outputs from the two before and two after runs
summarized in `dev/sessions/SESSION_3.md`. They were promoted from the ignored
`.testdata/bench` sandbox when the branch was prepared for review.

## Scenario

- Route: `bench/routes/vhsurvival.txt`
- Isolated rendered client and dedicated server
- Fixed settle: 3 seconds
- Measure: 3 seconds per waypoint
- Warm-up laps: 0
- Measured laps: 1
- Before labels: `phase1-smoke`, `phase1-smoke-repeat`
- After labels: `phase1-mip-worker-smoke`, `phase1-mip-worker-repeat`

The accompanying telemetry established that synchronous mip propagation reached
20–22.5 ms p95, 32.5–35 ms p99, and 103.1 ms maximum before the worker change. Both after
runs ended with no game ticks at or above 25 ms, no mip errors, and no pending/in-flight
mip work. See Session 3 and `STATUS.md` for the summarized phase evidence.

## Camera limitation discovered later

The old harness treated route pitch as zero-centred, while Vintage Story centres camera
pitch at PI radians. These runs therefore rendered mostly sky. Their teleports, chunk
capture, pipeline work, and mip propagation still occurred, so the same-route owning-thread
mip comparison remains useful. The CSV frame rates, screenshots, and renderer-phase load
must not be treated as representative terrain-rendering or visual-quality evidence.

The harness now maps route pitch correctly and has a separate deterministic movement and
rotation route. Corrected smoke artifacts remain local because they are integration checks,
not controlled performance results.
