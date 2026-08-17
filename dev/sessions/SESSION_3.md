# Session 3 — measured mip-thread remediation

**Date:** 2026-08-17
**Branch/commit:** `codex/main-thread-performance`, phase based on `f8d4b03`
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## 1. Context and investigation

The approved plan began with source-traced candidates but no current in-game attribution. A complete local game installation made the full SQLite fixture and an isolated rendered benchmark available. Client instrumentation was extended before choosing the first runtime fix.

Two short active-exploration runs on the 0.2.1 baseline showed total pipeline time nearly equal to total game-tick time. The synchronous mip phase reached 20–22.5 ms p95, 32.5–35 ms p99, and 103.1 ms maximum. Total game ticks reached 107.9 ms. Renderer subphases remained below the 25 ms hitch threshold, despite high upload volume and projection resets. The evidence promoted versioned asynchronous mip work ahead of the original phase order.

## 2. Instrumentation and harness work

- Added a compact piecewise latency histogram with p95, p99, exact maximum, and 25/50/100 ms counters.
- Timed client tick, assist/local-offer/pipeline/resident-eviction phases and individual pipeline/render phases.
- Counted projection resets and mesh upload bytes.
- Added a Windows-native benchmark runner with an isolated data path, exact pidfile/command-line validation, hidden server, rendered client, safe authentication-field seeding, and no force-kill path.
- Corrected a benchmark shutdown race: the client now sends `/stop` before publishing the marker that causes the runner to close it.
- Disabled SQLite connection pooling in the stale-version fixture's direct schema-edit connection so file replacement cannot inherit an old pooled handle.

## 3. Runtime implementation

Mip jobs now snapshot immutable run arrays plus copied capture-mask/palette data and carry a world epoch, child key, quadrant, and content revision. A dedicated below-normal worker performs boundary collection, sorting, majority occupancy, and merged-run construction.

The owning thread applies at most three completed results per tick and allows at most twelve in flight. It rejects failed, stale-revision, and cross-world results; these leave `MipDirty` set for retry. Parents are pinned against RAM eviction while child work is in flight. A valid result remaps child palette ids, publishes the quadrant, marks a changed parent, and only then clears the child's propagation obligation. World close advances the epoch and clears queued/result work; a job already executing is harmless when it returns under the old epoch.

Mesh-worker selection now accounts for the dedicated capture and mip threads, reserving two logical processors for game work on machines with six or more.

## 4. Measured result

Two same-route after runs ended with zero mip errors, zero pending/in-flight mip jobs, zero unsaved sections, and zero game ticks at or above 25 ms. Mip apply/schedule maxima were 1.93/0.43 ms. Capture application is now the largest measured pipeline tail, reaching 11.3–14.1 ms maximum.

Mean worst-1%-frame time from two before and two after runs:

| Waypoint | Before | After | Reduction |
|---|---:|---:|---:|
| spawn-horizon | 17.86 ms | 3.62 ms | 79.8% |
| spawn-look-south | 2.48 ms | 2.23 ms | 10.1% |
| ridge-east | 24.08 ms | 2.18 ms | 90.9% |
| valley-north | 10.99 ms | 1.60 ms | 85.5% |
| high-overlook | 11.79 ms | 2.38 ms | 79.8% |

The route is short and teleport-driven. It establishes the reproduced mip spike and the local fix's effect, but it does not replace a long human movement/rotation playtest.

---

## Delivered

- Client tick/pipeline/render percentile and hitch telemetry, projection-reset counts, and mesh-upload bytes.
- Safe Windows-native isolated benchmark workflow.
- Full game-backed fast-check portability; all 18 suites now complete.
- Bounded dedicated mip worker with content revisions, world epochs, stale/failure retry, parent pins, and owning-thread publication.
- Regression checks for worker isolation and mutation-during-flight rejection/rescheduling.
- Two before and two after route measurements.
- Updated architecture, plan, current status, TODO/DONE, and session history.
- Documentation checks pass 168 assertions under both Windows PowerShell 5.1 and PowerShell 7.

## Decisions

- Reorder implementation by measured main-thread cost: asynchronous mip work precedes incremental key discovery and renderer scaling.
- Keep palette registration/remapping and live parent publication on the owning thread; move only registry-independent structural work.
- Preserve `MipDirty` as the durable obligation until a revision-valid commit, rather than clearing it when work is queued.
- Use a separate mip worker so propagation cannot drain the capture queue or block all visible mesh jobs.
- Treat the route result as controlled evidence for one spike class, not as a universal FPS claim.

## Traps

- A benchmark completion marker can race a delayed graceful-stop callback. Once the runner sees the marker it closes the client, so send required network commands before writing the marker.
- A SQLite file-replacement fixture can still see an old schema through a pooled connection after disposal. Disable pooling for deliberate stale-file edits.
- Item-count budgeting did not bound mip latency: three sections produced a repeatable 20–35 ms tick tail and one 103 ms maximum.

## Flagged and unverified

- Human play quality, continuous movement/rotation, long soak, restart interruption, integrated sweep, and server-assist load remain unverified.
- Capture application is the next measured client tick cost, but its long-route distribution is not established.
- Projection-reset counts are live evidence; the proposed bounds/hysteresis design is not implemented.
- No changelog entry or release package was produced; this is unreleased development work.
