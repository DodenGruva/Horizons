# Session 25 - pixel-neutral vanilla readiness shadow tracker

**Date:** `2026-08-18`
**Branch/commit:** `codex/main-thread-performance` at base `8cb11ad0fe52e9e05a484d43d470dc12a465a582` (session work uncommitted)
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

Session 24 corrected camera-relative color noise and near-transition sinking, retained a
conservative radial handoff as a playtest stopgap, and approved a chunk-aware hybrid
ownership plan. This session began that plan with its first pixel-neutral increment:
source-trace the installed client lifecycle precisely, implement a bounded readiness model,
and exercise it from the renderer without allowing the new state to affect any draw.

The installed Vintage Story client had advanced from the project's 1.22.5 minimum to
1.22.7. The trace was therefore performed against the exact installed and checked
`VintagestoryLib.dll`, whose SHA-256 is
`E08F22B493B92FEAF0AAEB79D22437EA0F7EFC38AA7F72A04A47F98BC0E40DF0`, using ILSpyCmd
11.0. The installed and checked `VintagestoryAPI.dll` SHA-256 is
`034283E7E9D98EAE45EE63005576FD89BADC3C995B531CC4C3FE46F3EB2D3296`.

The investigation covered client chunk installation, the public `ChunkDirty` event,
tessellation, `quantityDrawn`, tessellated-result upload, opaque render ordering, chunk
unload, and public texture wrappers. No Vintage Story process was launched; runtime and
visual evaluation remained with the user.

## Work narrative

1. `ClientWorldMap.loadChunkMT` installs a chunk and raises `ChunkDirty(NewlyLoaded)`
   before tessellation completes. `ChunkTesselatorManager.TesselateChunk` increments
   `quantityDrawn` before processing and before the completed result is queued for upload.
   Consequently, neither the dirty event nor the first true `IsChunkRendered` result can
   safely transfer pixel ownership.
2. Tessellated results are uploaded during render stage `Before` at order 0.99, followed
   by an internal retessellated callback. That callback is not exposed through the public
   client event APIs. Vintage Horizons renders opaque terrain at order 0.36 and vanilla
   terrain follows at 0.37, so a future mask committed in `Before` can be shared by both
   decisions in the same frame.
3. Client chunk unload removes the chunk from the world map, making later
   `IsChunkRendered` queries false, but the public API exposes no reliable unload event.
   The design therefore requires dirty-event candidate discovery plus bounded polling,
   including boundary-first revalidation for prompt loss detection.
4. A new renderer-owned `VanillaRenderReadiness` model tracks 32x32x32 ownership cells in
   a power-of-two horizontal tagged ring. Unknown, pending, observed, and committed-ready
   state; coalesced candidates; deferred observations; publication tokens; and L0-L6
   ancestor counts all use fixed arrays and value keys.
5. Readiness gain requires true observations separated by a complete render-frame
   boundary. Readiness loss proposes cache restoration on the first false observation.
   CPU aggregates change only through the same publication handshake intended for the
   future GPU mask, and generation tokens reject late publications after window or world
   invalidation.
6. The renderer now subscribes to `ChunkDirty`, incrementally seeds unknown cells,
   revalidates a guard shell before interior ready cells, promotes deferred observations,
   and probes through the public `IsChunkRendered(EntityPos)` API. Each frame is capped at
   256 probes and 0.25 ms, with separate seeding and revalidation item ceilings. The hot
   path reuses one `EntityPos` and the converged scheduling/probe model is allocation-free
   in the harness.
7. The tracker is shadow state only. The renderer never calls readiness classification in
   its draw path, no shader or texture-mask code was added, and the radial handoff remains
   the sole pixel owner. Any tracker exception disables the shadow tracker and preserves
   that established fallback.
8. Periodic renderer diagnostics and `.vhinfo` now report readiness phase cost, active
   window and capacity, state counts, queue depths and ages, tracked bytes, probes,
   transitions, window changes, dirty events, and errors. These diagnostics are intended
   to establish runtime convergence and budget suitability before Phase 2 changes pixels.

---

## Delivered

- Exact installed-1.22.7 lifecycle and public-API source trace recorded in the chunk-aware
  handoff plan.
- `VanillaRenderReadiness`, including coordinate/window helpers, tagged ring storage,
  duplicate-coalesced candidate and deferred-observation queues, two-frame readiness
  stabilization, publication tokens, immediate loss proposals, teardown invalidation,
  L0-L6 aggregates, and bounded scheduling helpers.
- Pixel-neutral renderer integration consuming `ChunkDirty` and performing time- and
  item-bounded initial discovery, boundary-first loss revalidation, interior maintenance,
  and `IsChunkRendered` probing.
- Readiness phase, state, queue-age, transition, error, event, capacity, and byte telemetry
  in periodic logs and `.vhinfo`.
- Eighty-nine focused readiness-model assertions plus static wiring guards that ensure the
  renderer subscribes and unsubscribes correctly, retains both probe budgets, and does not
  use readiness classification in its draw path.
- The full Release check tier passes 1,176 assertions. `git diff --check` passes; only Git's
  existing LF-to-CRLF working-copy warnings remain.
- No shader, assist protocol, section blob, database schema, persistent cache meaning,
  package, or playable release change.

## Decisions

- Use supported public APIs and bounded polling rather than bind to internal
  `ClientEventManager` callbacks or private client fields.
- Treat `ChunkDirty` only as candidate discovery and require two render-frame-separated
  true observations before proposing vanilla ownership.
- Detect likely unloads through a small boundary-first ready-cell shell, with a slower
  interior safety sweep; fail toward cached coverage on uncertainty or error.
- Keep readiness and residency independent. Window movement can clear draw ownership
  state but does not delete cached sections or dispose their meshes.
- Preserve a publication handshake even in the shadow phase so Phase 2 cannot later make
  CPU whole-section classification run ahead of GPU mask visibility.
- Prototype a public-wrapper-compatible 2D Y-slice atlas in Phase 2. The public shader
  wrappers expose 2D and cube binding, not a supported integer 3D texture update path.
- Do not tune or claim the provisional 256-item/0.25-ms probe budget until runtime
  diagnostics establish discovery latency, loss latency, and frame cost.

## Traps

- `IsChunkRendered` means that `quantityDrawn` advanced, not that a non-empty replacement
  mesh is necessarily uploaded and visible in the same frame. A first true result must not
  directly hide cached terrain.
- `ChunkDirty(NewlyLoaded)` fires before render readiness. Treating it as an ownership
  event would recreate the streaming hole the handoff is meant to eliminate.
- There is no public client chunk-unload event covering the observed unload path. Without
  bounded ready-cell revalidation, stale ownership bits could leave holes.
- Requeueing an observed cell directly into the active candidate queue can consume the
  same frame's probe budget repeatedly. A separate fixed deferred-observation queue keeps
  the second probe behind a render-frame boundary.
- A horizontal ring needs world-column tags. Wrapped slots without tag validation can
  leak stale readiness from a different coordinate after camera movement.

## Flagged and unverified

- The shadow tracker has not run in a Vintage Story client. Its active-window convergence,
  candidate age, unload detection latency, probe cost, exception fallback, and zero-allocation
  behavior remain unverified in the integrated renderer.
- The 256-probe and 0.25-ms ceilings are conservative starting values, not measured final
  budgets. Continuous high-speed movement and view-distance changes still need stress
  coverage.
- The source trace is exact for the installed 1.22.7 binaries. Compatibility of the same
  lifecycle assumptions with the project's 1.22.5 minimum remains to be confirmed or
  bounded by a supported-version policy.
- Phase 2 mask allocation/publication, Phase 3 shader sampling, Phase 4 CPU whole-section
  rejection, paired benchmarks, seam inspection, and human visual acceptance remain open.
- The conservative radial handoff package from Session 24 still lacks recorded human
  in-game results.
