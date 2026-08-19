# Plan - chunk-aware cached-to-vanilla terrain handoff

**Status:** In progress. The installed-engine lifecycle is source-traced. The pixel-neutral
Phase 1 readiness model and renderer shadow integration are source- and harness-complete;
runtime convergence/timing evidence and all pixel-changing phases remain open. The runtime
gate itself is now automated: `scripts/bench-windows.ps1 -RequireReadinessConvergence`
asserts the Phase 1 exit conditions from client telemetry, and a static check matches the
runner's pattern against the renderer's own log format so drift fails the fast tier rather
than a game run.
**Date:** 2026-08-18.
**Scope:** Client rendering and transient client-side readiness tracking only. No cache,
database, network, or wire-format change.
**Baseline:** The current playtest branch uses a radial fragment-shader discard as a
temporary near-field handoff. That prevents long-lived overlap near the player but is not
aware of which vanilla chunks have actually reached the renderer. Cached meshes remain
resident, selected, submitted, vertex-processed, rasterized, and then discarded early in
the fragment shader.

## 1. Outcome

Replace the radial approximation with an exact, chunk-aware ownership handoff:

- Cached terrain remains visible wherever the corresponding vanilla terrain is not ready.
- Vanilla terrain exclusively owns an area once its renderable chunk is ready.
- Cached and vanilla terrain do not shade the same world-space area in steady state, so
  their surfaces cannot sustain z-fighting.
- If vanilla terrain unloads, cached terrain becomes visible again without leaving the
  render-radius-shaped hole that motivated this work.
- Fully replaced cached meshes are rejected on the CPU before draw submission.
- Fully unreplaced cached meshes retain the existing unmasked draw path.
- Only mixed meshes at the streaming frontier pay for a readiness-mask lookup.
- Readiness tracking is bounded, incremental, allocation-free in steady state, and does
  not scan every loaded game chunk every frame.

The target is no measurable performance regression in a cache-only or moving-frontier
scenario and a measurable reduction in CPU draw submission and GPU work once vanilla has
replaced cached sections. A performance claim is not complete until a reproducible paired
benchmark establishes it.

## 2. Non-goals

- Do not delete persistent LOD data when vanilla terrain takes ownership.
- Do not tie visibility directly to CPU/GPU mesh eviction.
- Do not split every LOD mesh into one GPU mesh per vanilla chunk.
- Do not rebuild a cached mesh whenever a vanilla chunk loads or unloads.
- Do not change vanilla chunk loading, tessellation, draw order, or shaders.
- Do not use depth bias, downward sinking, alpha blending, or a broad spatial cross-fade
  to hide overlap.
- Do not promise a perfectly invisible geometry transition. A single clean shape change
  may remain when approximate cached geometry is replaced by exact vanilla geometry.
- Do not persist readiness. It is renderer/session state, not terrain truth.

## 3. Governing invariants

1. **Exclusive ownership:** for an ownership cell committed as vanilla-ready, cached
   opaque and cached water fragments in that cell produce no color or depth output.
2. **Coverage before refinement:** an unready or uncertain cell remains cache-owned.
3. **Fast loss, cautious gain:** a confirmed loss of vanilla readiness restores cached
   coverage immediately; gaining vanilla ownership requires a render-safe confirmation.
4. **One mask for both passes:** opaque terrain and water use identical committed
   readiness state in the same frame.
5. **Atomic publication:** CPU whole-mesh classification must never get ahead of the GPU
   mask. A readiness transition becomes drawable state only after its texture update is
   available to that draw.
6. **Visibility is not residency:** suppressing a cached mesh does not itself dispose its
   GPU buffers or evict its `LodSection`. Existing distance/age residency remains the
   anti-thrash policy.
7. **Bounded render work:** readiness probes, event ingestion, texture updates, and any
   maintenance sweep have explicit item and elapsed-time limits.
8. **No full loaded-chunk enumeration:** do not use `LoadedChunkIndices` as a frame loop;
   the installed client implementation materializes the complete key set as an array.
9. **Render-thread GPU ownership:** mask texture creation, update, binding, and disposal
   remain with the renderer. No worker calls OpenGL.
10. **ASCII shaders:** all edited shader source and comments remain pure ASCII.
11. **World isolation:** no readiness state, pending candidate, texture update, or engine
    chunk reference may survive a world change.
12. **Fail toward coverage:** an API error, stale candidate, texture failure, or unknown
    state keeps or restores cached terrain rather than opening a hole.

## 4. Source-traced engine facts and remaining proof

The supported client API provides `ICoreClientAPI.IsChunkRendered(EntityPos)`. The exact
installed 1.22.7 implementation resolves the 32x32x32 `ClientChunk` and returns whether
its `quantityDrawn` is greater than zero.

That signal is useful but not a perfect post-draw notification:

- `quantityDrawn` advances during chunk tessellation.
- The completed mesh is subsequently consumed and uploaded by the client renderer.
- Empty chunks also advance the counter, which is useful if an entire vertical column is
  used as the readiness requirement.
- `ChunkDirty` can identify newly loaded or changed candidates, but it occurs before final
  render readiness and therefore cannot directly transfer ownership.
- A public, client-side unload or post-upload event has not yet been proven available.

Before implementation, source-trace and record:

1. The exact thread and frame order of `ChunkDirty`, `IsChunkRendered`, tessellated-result
   upload, vanilla terrain drawing, and Vintage Horizons' opaque render callback.
2. Whether a public post-upload/re-tessellated client event can be subscribed to without
   patching engine internals.
3. Whether a public client chunk-unload event is available and reliable for all unload
   paths, including view-distance reductions and world teardown.
4. Whether every vertical chunk in a loaded column reaches `quantityDrawn > 0`, including
   completely empty chunks.
5. Whether `IsChunkRendered` returns false in the first render frame in which an unloaded
   vanilla chunk no longer draws.
6. The supported texture formats and update APIs in the game's OpenGL wrapper, including
   integer point-sampled 3D textures and subregion updates.

Prefer a proven post-upload/unload notification. If none exists, use the bounded polling
state machine in this plan. Do not use Harmony or private-field reflection merely to avoid
a small, measurable readiness tracker.

### 4.1 Installed 1.22.7 lifecycle trace - 2026-08-18

The current installed `VintagestoryLib.dll` reports file/product version 1.22.7 and SHA-256
`E08F22B493B92FEAF0AAEB79D22437EA0F7EFC38AA7F72A04A47F98BC0E40DF0`; the installed
`VintagestoryAPI.dll` SHA-256 is
`034283E7E9D98EAE45EE63005576FD89BADC3C995B531CC4C3FE46F3EB2D3296`. The checked test
references match those hashes. ILSpyCmd 11.0 was used against that exact library copy.

The relevant source path is:

1. `ClientWorldMap.loadChunkMT` installs the `ClientChunk`, sets `loadedFromServer`, calls
   `MarkChunkDirty`, dirties neighbours, and only then triggers the public
   `ChunkDirty(NewlyLoaded)` event. The event is therefore a candidate signal, not a ready
   signal.
2. `ChunkTesselatorManager.TesselateChunk` runs on the separate tessellation thread. It
   increments `ClientChunk.quantityDrawn` before `ChunkTesselator.NowProcessChunk` and
   before queuing the result for upload. Empty chunks also increment the counter and
   return without a GPU upload.
3. `ChunkTesselatorManager.OnBeforeFrame` is registered at `EnumRenderStage.Before`, order
   0.99. It calls `ChunkRenderer.AddTesselatedChunk` and then the internal
   `ClientEventManager.TriggerChunkRetesselated` callback. The callback is post-upload but
   is not exposed by `IClientEventAPI`/`IEventAPI` as a global event.
4. `LodTerrainRenderer` runs at opaque order 0.36. Vanilla `SystemRenderTerrain` runs at
   opaque order 0.37, so a mask committed in the earlier `Before` stage is available to
   both ownership decisions in that frame and cached terrain remains immediately before
   vanilla terrain.
5. `SystemUnloadChunks.HandleChunkUnload` removes the chunk's renderer pool locations,
   disposes it, and removes it from `ClientWorldMap.chunks`. The public client event APIs
   expose no chunk-unload event. A subsequent `IsChunkRendered` is false because
   `ClientWorldMap.IsChunkRendered` no longer finds the chunk, but prompt loss detection
   therefore requires bounded boundary-first revalidation.
6. The public shader/render wrappers bind 2D and cube textures only. The client itself
   uses OpenTK 3D/array textures internally, but there is no public 3D bind/update wrapper.
   Phase 2 should prototype the plan's 2D Y-slice atlas first unless a direct OpenTK path
   is proven portable against the project's supported game/GL range.

**Supported-version policy.** This trace is exact for installed 1.22.7 only, while the mod
declares 1.22.5 and the check tiers run against 1.22.5/1.22.6. The tracker is never allowed
to be pixel-authoritative without a positive probe result on the running client, and every
failure path restores cached coverage, so an older or newer lifecycle can cost coverage
accuracy but cannot open a hole. Do not raise the declared minimum for this feature alone;
if a supported version is ever found where `IsChunkRendered` never returns true, the
tracker must report that condition and stay on the radial fallback for that session.

This proves there is no supported notification-only implementation. Use the planned
event-fed candidate queue plus two-render-frame true stabilization and prompt false
revalidation. Do not bind to `ClientEventManager` or other `NoObf` internals for the
handoff lifecycle.

## 5. Ownership model

### 5.1 Cell granularity

The authoritative cell is one vanilla 32x32x32 render chunk, not an entire horizontal
column. A three-dimensional cell avoids declaring cached caves, cliffs, water, or tall
terrain replaced merely because one surface-height chunk rendered.

Each cached fragment maps to:

```text
chunkX = floor(worldX / 32)
chunkY = floor(worldY / 32)
chunkZ = floor(worldZ / 32)
```

The world-space convention is half-open on every axis:
`[chunk * 32, chunk * 32 + 32)`. Ancestor addressing divides chunk coordinates by the
section edge, which truncates rather than floors; that is correct only because Vintage
Story world coordinates are non-negative and the active window refuses a negative origin.
Keep that guard: removing it as a redundant bounds check would silently mis-key sections
rather than fail. Coordinate conversion must be isolated in pure helpers
and tested at both sides of every boundary. Use section-local X/Z plus an integer per-draw
section-chunk origin so large camera or world coordinates do not destabilize `floor()`.

### 5.2 Cell states

| State | Meaning | Cached ownership |
|---|---|---|
| Unknown | Not probed, outside the active window, or invalidated | Visible |
| Pending | Engine activity suggests the cell may become rendered | Visible |
| Observed rendered | `IsChunkRendered` returned true, but publication is not yet render-safe | Visible |
| Vanilla ready | Readiness is committed to both CPU aggregates and the GPU mask | Suppressed |
| Invalidated | Unload, false recheck, resize, teardown, or failed publication | Restored |

If source tracing proves a true post-upload notification, it may move a cell directly
from Pending to Vanilla ready. Otherwise:

1. First true observation records the render-frame number.
2. The cell is checked again after at least one complete client render boundary.
3. A second true result admits a GPU-mask update.
4. The cell becomes Vanilla ready only after that update is committed.

This delay is intended to bridge the known tessellation-to-upload gap. The exact number
of frame boundaries must be established against engine ordering, not guessed from wall
clock time.

### 5.3 Interaction through time

```text
vanilla absent
    -> cache draws normally

vanilla tessellation begins
    -> cell enters Pending/Observed rendered
    -> cache still covers it

vanilla upload is render-safe
    -> GPU readiness bit is committed
    -> cache is discarded in exactly that 32x32x32 cell
    -> vanilla exclusively owns the cell

vanilla unloads or readiness becomes false
    -> readiness bit clears before cached drawing
    -> cache resumes coverage
```

There is no multi-second blend and no vertical deformation. With polling, a transition
may contain a very short overlap while readiness is confirmed, but it must not sustain
z-fighting. A missing vanilla frame must not be exposed as a hole.

## 6. Bounded readiness tracker

Add a renderer-owned `VanillaRenderReadiness` component with no dependency on persistence
or `LodWorld` content mutation.

### 6.1 Active window

- Center the window on the camera's vanilla chunk coordinate.
- Horizontal radius derives from the smaller of desired and server-approved vanilla view
  distance, rounded outward, plus a measured guard band.
- Vertical extent derives from `MapSizeY`, rounded to vanilla chunk height.
- Use a power-of-two horizontal ring size when it materially simplifies address wrapping.
- Resize only when the required dimensions exceed capacity or remain substantially below
  a lower capacity for a cooldown. Do not reallocate while an old texture is still the
  committed ownership source.
- Cells outside the valid world-space window always decode as cache-owned, even if their
  wrapped texture address contains a stale ready byte.

Representative storage at a 512-block vanilla radius is modest: approximately
`(2 * 16 + guards)^2 * (MapSizeY / 32)` one-byte GPU cells. Record the actual CPU and GPU
bytes in telemetry instead of relying on this estimate.

### 6.2 Event-fed candidates

- Subscribe to the narrowest available client chunk-dirty/load signal.
- Pack candidate X/Y/Z into a value key; do not allocate `EntityPos` per permanent cell.
- Coalesce duplicates before probing.
- If event and render callbacks are on the same proven thread, mutate the tracker
  directly. Otherwise, use a bounded, minimal inbox and drain it on the render owner.
- An event means "probe soon," never "vanilla owns this cell."
- Prioritize cells intersecting selected cached meshes and the current streaming frontier.

### 6.3 Initial discovery and revalidation

Events do not cover chunks that were already rendered when the tracker was created. Seed
the current window incrementally and probe it under both item and elapsed-time limits.

If there is no reliable unload notification:

- Revalidate ready cells near the moving vanilla boundary first.
- Revalidate cells affected by a view-distance change immediately.
- Revalidate newly exposed ring rows/columns as the camera crosses a chunk boundary.
- Sweep interior ready cells at a low rolling cadence under the remaining budget.
- Clear a false cell before cached drawing in that frame.

Do not scan every 3D cell every frame. Start with conservative limits and expose the
oldest pending/revalidation age so a limit that is too small is visible rather than
silently causing prolonged overlap or holes.

**State the stale-ownership bound explicitly.** The interior sweep rate divided into the
active cell count is the worst-case time an unloaded interior chunk can keep vanilla
ownership, and that interval is exactly how long a hole can persist. At the current
32 cells per frame over an approximately 11,000-cell window, that is roughly six seconds
at 60 FPS, with the boundary shell covering the frontier far sooner. Any change to either
number must restate this bound, and the runtime run must confirm it against measured
oldest-work age rather than assuming the sweep keeps up.

### 6.4 State storage

Use compact arrays for the camera-centered window and sparse sets/queues only for pending
work. A cell needs, at most:

- Current committed readiness bit.
- Candidate/stabilization state.
- First-observed frame or a small generation counter.
- Ring generation/tag when needed to reject aliased stale cells.

Avoid one managed object per chunk. Avoid per-frame LINQ, tuple allocation, array copies,
and rebuilding hash sets from the complete window.

### 6.5 Aggregate counts for CPU fast paths

Maintain a ready-cell count for every aligned LOD section touched by a point transition.
One ready/unready cell updates its L0 ancestor and each parent through L6: seven fixed
counter changes, not a section-area scan.

For a selected cached section:

```text
ready count == 0
    -> unmasked normal draw

ready count == total 32x32x32 ownership cells covered by the section
    -> skip opaque and water draws entirely

otherwise
    -> mixed draw using the GPU readiness mask
```

The expected total includes the section's horizontal footprint and valid vertical world
chunks. If source tracing proves some empty vertical chunks never report rendered, add a
geometry-coverage aggregate rather than weakening readiness to one arbitrary surface Y.

**This is a Phase 1 exit question, not a Phase 3 detail.** A complete classification needs
every vertical chunk of every covered column, so if the client never holds or tessellates
the empty chunks above terrain and below the caves, no section ever classifies as fully
ready, the CPU whole-mesh skip never fires, and every near section pays mixed-mode masking
instead. The tracker therefore reports a vertical distribution - columns tracked, complete,
and partial, the deepest column, and ready cells per Y band - in its periodic diagnostics
and in the benchmark scenario record. Read that distribution before building Phase 2 or
Phase 3. If complete columns never appear, the covered-cell total must come from cached
geometry coverage (only the cells a section's mesh actually occupies) and the plan's
expected CPU saving must be re-estimated before the mask work is justified.
Any geometry-derived coverage must be produced once with mesh work and stored compactly;
it must not rescan runs on the render thread.

Aggregate updates and texture publication use the same committed transition. A CPU count
must not classify a section as fully ready while the texture still contains its old value.

## 7. GPU readiness mask

### 7.1 Resource

Preferred representation: a one-byte, point-sampled 3D texture containing committed
vanilla readiness for the bounded X/Y/Z window.

- No mipmaps, filtering, compression, or normalized interpolation.
- One texel represents one 32x32x32 vanilla render chunk.
- Pass the world-space valid bounds and ring origin as uniforms.
- A sample outside valid bounds returns cache-owned without reading a wrapped stale cell.
- Resize by creating and initializing the replacement first, then atomically switching
  and disposing the old texture on the render thread.
- Batch changed texels. Compare partial updates with a full small-volume upload when many
  cells change; choose by measured call time and bytes, not intuition.

If the game wrapper cannot support an efficient 3D integer texture, evaluate in order:

1. A 2D R8 atlas of Y slices with point `texelFetch`.
2. A compact buffer/texture-buffer representation supported by the existing GL version.
3. A small uniform bitmask only for L0 plus an atlas for rare coarse fallback draws.

Do not fall back to one texture or uniform upload per cached mesh.

### 7.2 Shader path

Use one coherent per-draw mode:

| Mode | Meaning | Shader cost |
|---|---|---|
| Cache only | Section aggregate contains zero ready cells | No readiness sample |
| Mixed | Section contains both ownership states | One point sample and early discard |
| Vanilla only | Section aggregate is fully ready | No draw call |

The branch is uniform for the draw and therefore coherent. In mixed mode, perform the
ownership lookup before normal calculation, noise, tint, lighting, fog, sky color, water
blending, and framebuffer output. Apply exactly the same lookup to opaque and water.

Keep the current radial handoff available as an internal rollout fallback until the
hybrid path is complete and verified. When hybrid state is healthy, radial distance must
not also suppress cache-owned cells.

### 7.3 Boundary policy

- Test the half-open chunk mapping at local coordinates 0, 31.999, 32, 63.999, and 64,
  including sections at large world coordinates.
- Verify triangles greedily merged across a 32-block ownership boundary are clipped by
  fragment ownership even though the GPU mesh is not split.
- Source-check face winding and vanilla boundary-face behavior before adding any epsilon
  to mask lookup coordinates.
- Do not introduce a general coordinate bias that can move a horizontal surface into the
  wrong vertical ownership chunk.
- If testing finds z-fighting restricted to vertical faces exactly on a chunk plane,
  establish deterministic face ownership from mesher face direction. Do not solve it with
  a broad overlap or depth bias.

## 8. Visual transition and seam strategy

### 8.1 Expected transition

The approximate cached surface may not exactly match current vanilla terrain. Ownership
transfer can therefore produce a one-time shape or color pop at a chunk boundary. The
desired priority order is:

1. No sustained z-fighting.
2. No open hole.
3. No repeated flicker or ownership flapping.
4. Minimize the remaining one-time pop.

Do not reintroduce overlapping surfaces merely to soften the pop.

### 8.2 Crack detection

A height mismatch can expose a seam between a cache-owned cell and a vanilla-owned cell.
Before adding geometry, determine whether existing cached side faces and vanilla chunk
boundary faces already close it.

Human test cases must include:

- Flat plains and shallow slopes.
- Tall cliffs aligned with and crossing chunk boundaries.
- Mountains with large cached-versus-current height differences.
- Water shorelines, underwater viewing, and transparent water over cached seabed.
- Caves, overhangs, and tall structures crossing vertical chunk boundaries.
- Rapid flight toward and away from the streaming frontier.
- Rotation at the frontier and a live view-distance decrease/increase.

If a reproducible crack remains, add the smallest frontier-only remedy:

- Prefer deterministic boundary-face ownership or an existing side-face correction.
- If a skirt is necessary, generate it only for the cache-owned side of a mixed frontier,
  keep it vertical, narrow, and mask-controlled, and measure added vertices and fragments.
- Never add a horizontal skirt or fade band that overlaps vanilla terrain.

Skirts are a contingent phase, not part of the first implementation.

### 8.3 Flicker control

- Setting ready requires the render-safe confirmation described above.
- Clearing ready happens on the first reliable false/unload observation.
- Duplicate dirty events do not reset a committed ready cell unless a recheck fails.
- A re-tessellation keeps vanilla ownership while the old vanilla mesh remains active;
  do not expose cache merely because a replacement is queued.
- Log state oscillation only under diagnostics and rate-limit it.

## 9. Residency, eviction, and cache interaction

The persistent LOD row is unchanged. Suppression affects draw ownership only.

- A suppressed mesh may stay in GPU memory while it remains in the existing residency
  band, allowing immediate fallback without remeshing if vanilla unloads.
- Turning the camera must not evict and rebuild cached terrain.
- Existing distance/age eviction may eventually dispose a mesh that remains unnecessary.
- Existing persistence allows an evicted section to reload later.
- Readiness does not clear `RenderDirty`, cancel a mesh job, discard a completed result,
  or alter mip propagation.
- A newly completed cached mesh can be uploaded under the existing bounded policy even if
  currently vanilla-owned; measure whether avoiding such uploads is worthwhile only after
  correctness. Do not complicate the first implementation with cancellation races.

A later optimization may defer a never-visible upload when every covered cell is stably
vanilla-owned, but only if it preserves the dirty obligation and can recover without a
blocking load. It is explicitly outside the first hybrid implementation.

## 10. Failure behavior and teardown

| Failure | Required behavior |
|---|---|
| Readiness API throws or returns an invalid result | Treat affected cell as cache-owned; throttle diagnostics |
| Candidate queue over budget | Leave remaining cells cache-owned and continue next frame |
| Texture update fails | Do not commit CPU ready counts; retain cached coverage |
| Mask texture cannot be created | Use the existing radial fallback and report once |
| Shader compile fails | Preserve existing renderer failure reporting; do not leak the mask resource |
| View distance changes | Recompute valid bounds incrementally; prioritize cells losing validity |
| Camera teleports | Invalidate/reseed the window without interpreting wrapped texels as current |
| World changes or renderer disposes | Unsubscribe events, clear pending state, and dispose GPU resources on their owner |
| Vanilla readiness flaps | Restore cache on loss; require confirmation again before suppression |

Do not retain strong references to every engine chunk solely to observe `Disposed`. If a
weak-reference approach is considered, measure retained objects and prove that collection
does not create false ownership changes.

## 11. Instrumentation

Add cheap counters available in existing renderer diagnostics and detailed timing only
when performance telemetry is enabled.

### Readiness

- Active X/Z/Y dimensions and CPU/GPU bytes.
- Pending, observed, committed-ready, and invalidated cells.
- Candidate events accepted/coalesced/dropped.
- Probes per frame, true/false results, and oldest pending/revalidation age.
- Ready/unready transitions and oscillations.
- Window shifts, resizes, full clears, and view-distance changes.
- Vertical distribution: columns tracked, complete, and partial; the deepest ready column;
  and ready cells per Y band. This is what proves or disproves reachable whole-section
  ownership, so it belongs in ordinary diagnostics rather than a one-off investigation.

### GPU publication

- Texture updates, texels/bytes uploaded, full versus partial uploads.
- Upload average, p95, p99, and maximum time under diagnostics.
- Failed or deferred publications.

### Rendering

- Cache-only sections drawn.
- Mixed sections drawn with the mask.
- Vanilla-only opaque and water draws skipped.
- Estimated draw calls avoided.
- Existing traversal, draw-submission, GL upload, and disposal timing retained.
- Optional non-blocking/delayed GPU timer queries for shader/fill comparison if they can
  be implemented without a synchronization stall. Never call a blocking GPU query in the
  production frame path.

Telemetry must allocate nothing per steady-state frame when disabled. Counters used in
normal status output remain O(1).

## 12. Implementation phases

### Phase 0 - prove the engine lifecycle and baseline

**Implementation status:** Installed 1.22.7 lifecycle and public-API limits are
source-traced in section 4.1. Paired radial baseline capture and benchmark-noise work
remain open and require an isolated game run.

1. Record the exact engine source/decompilation path for readiness, upload, dirty, and
   unload behavior.
2. Capture current radial-handoff draw counts, renderer phase timings, FPS/frame times,
   mesh residency, and visual examples on fixed scenarios.
3. Establish current benchmark noise with at least three repeated runs per side before
   choosing regression thresholds.
4. Add a development-only comparison switch: current radial behavior versus hybrid.

**Exit:** readiness publication and invalidation can be placed in the frame lifecycle
without guessing, and the performance baseline is reproducible.

### Phase 1 - pure readiness model

**Implementation status:** Source-, harness-, and runtime-validated on 2026-08-18 against
installed 1.22.7; see `bench/results/2026-08-18-readiness-shadow/`. The first gated route
exposed a real defect - interior maintenance requeued only committed cells, so the sweep
could lose ownership but never gain it, and one 15-second stationary interval spent
432,744 probes on already-ready cells and none on 1,801 pending ones. Maintenance is now
state-agnostic, engine-announced candidates are counted separately from the tracker's own
sweeps, and the repeat route converged to 232 of 441 complete columns with 0 errors,
0 dropped events, 18.4 us average and 50 us p99 readiness cost, no 25 ms renderer phase,
and zero steady-state allocation. The
renderer owns a fixed-array/ring shadow tracker, receives value-only `ChunkDirty`
candidates, incrementally seeds existing cells, defers the second true observation across
a render boundary, revalidates the expected streaming shell before a slower interior
sweep, and probes under 256-item/0.25 ms ceilings. Accepted publications currently update
shadow aggregates and telemetry only; draw selection and shaders do not read them, so the
radial handoff remains the sole pixel owner. Focused checks cover boundaries, ring alias
rejection, movement/teardown invalidation, stale publication tokens, L0-L6 aggregates,
boundary-first revalidation, and a zero-allocation converged scheduling/probe loop.

1. Implement coordinate packing, active-window/ring addressing, states, candidate
   coalescing, probe budgeting, stabilization, invalidation, and teardown without changing
   rendering.
2. Add fixed ancestor ready counts and O(1) section classification.
3. Compare tracker state with visible vanilla streaming under diagnostics.
4. Prove zero steady-state allocations and bounded work in isolated checks.

**Exit:** met for loading, movement, and steady state on 2026-08-18. Teleport and live
view-distance change are still only covered by harness checks, and no person has watched
this build. Complete vertical columns are common - 232 of 441, with a flat per-Y
histogram - so empty sky and deep chunks do report rendered and the Phase 3 whole-mesh
skip is reachable without a geometry-derived aggregate.

**Open runtime tuning, deliberately not guessed before the run.** The two-render-frame
stabilization is expressed in frames, but the gap between `quantityDrawn` advancing and
the completed mesh being uploaded depends on the tessellation queue depth, not on frame
count, so a teleport or view-distance increase can stretch it. Premature suppression is a
hole and late suppression is brief overlap, so if the run shows ready/lost oscillation or
visible early suppression, add a short elapsed-time hold alongside the frame boundary and
set its duration from that measurement.

### Phase 2a - readiness-driven radial distance

Approved and implemented 2026-08-18. This is the first phase in which measured readiness
owns pixels. Before any GPU mask exists, the tracker can
already improve the shipped behavior through the uniform the shader takes today. Replace
the constant near-handoff fraction with the measured Chebyshev distance to the nearest
non-ready cell, minus one cell, in blocks.

- No new GPU resource, shader edit, atlas, or publication protocol; only the value of
  `cacheHandoffDistance` changes.
- It removes the render-radius-shaped hole, because a missing or unloaded chunk shrinks
  the radius and cached terrain re-covers that area automatically.
- Uncertainty shrinks the radius, so it fails toward coverage by construction.
- It is deliberately conservative: one unloaded pocket near the camera shrinks coverage
  globally and restores overlap elsewhere. How often that happens in real play is itself
  the evidence for whether the full per-cell mask earns its complexity.
- It gives the mask a measured baseline to beat instead of a guessed constant, and it
  gives the player a testable improvement before Phase 2 completes.

This is an addition to the rollout order, not a replacement for the mask.

**As implemented.** The radius is the distance to the nearest tracked column that is not
wholly owned, minus one vanilla chunk, clamped to the vanilla view distance and quantized
down to a whole chunk. A column counts only when every one of its vertical chunks is
committed ready, and the active window edge bounds the result, so an untracked frontier
cannot be mistaken for owned ground. `LodNearHandoffState` applies shrinkage in the same
frame and holds growth for 500 ms, applying the smallest radius seen during that hold;
continuous movement therefore cannot stall growth or let a transient peak through. If the
tracker is disabled, absent, or the player leaves the default dimension, the established
`LodNearHandoff.InnerDiscardRadius` constant returns immediately.

**Measured before building it.** A stationary route reported the nearest not-wholly-owned
column at 234 blocks with 201 complete columns and zero partial ones, against a radial
constant of 64 blocks at a 256-block vanilla view distance. Incompleteness begins at the
frontier rather than as pockets near the player, which is the condition a single global
radius needs. The implemented handoff then held 192 blocks with no fallback sample.

**Known limitations.** One unowned pocket near the camera still shrinks coverage globally;
that is the case the per-cell mask exists to fix, and how often it happens in real play is
the evidence for whether Phase 2 earns its complexity. Nothing here reduces CPU draw
submission or vertex work: cached fragments inside the radius are still rasterized before
being discarded. Both remain Phase 2 and Phase 3 work.

### Phase 2 - GPU mask behind a disabled feature gate

**Implementation status:** Implemented 2026-08-19 behind `VINTAGEHORIZONS_CHUNK_MASK=1`,
off by default. Runtime evidence is partial: the mask becomes active, owns exactly the
tracker's committed cells, and uploads in 3 microseconds, but no controlled performance
pair and no human visual check exist yet.

**As implemented.** One BGRA texel per 32x32x32 cell in a 2D atlas of Y slices stacked
down the texture, 32 KiB for a 256-block window, addressed by the same wrapped ring the
tracker uses. Section 5.1's warning about `floor()` at large world coordinates is not
optional and was proven by a numeric check: the first implementation derived the cell from
the summed world position, and at a 512,000-block coordinate a float32 rounds a fragment
0.03 blocks below a chunk edge onto the next chunk, which then owns it. The shader now
receives the section origin as an integer in chunks and adds the floor of the exact local
offset. The decompiled client update path creates on a size mismatch and otherwise
issues `TexSubImage2D` for the whole extent; mipmaps are built only on creation and
`texelFetch` ignores filtering, so steady-state upload is one sub-image call. There is no
public subregion update, which is why changes are coalesced to at most one upload per
frame. Window movement rebuilds the mask from committed state rather than patching it,
because a reused ring slot would otherwise carry another place's ownership. Any upload
failure disables the mask and restores the measured radius, and leaving the default
dimension clears the texels and drops the active flag together, so ownership measured in
one world can never suppress terrain in another. When the mask is healthy the
radial handoff is driven to zero so it cannot also suppress cells the mask assigns to the
cache. The fragment shader decides ownership before normals, tint, lighting, fog, sky and
water work; an address outside the window, above or below the world, or reading a
cache-owned texel falls through to normal cached drawing.

1. Add the bounded readiness texture/atlas and atomic publication protocol.
2. Add world-to-mask shader addressing and mixed-mode early discard.
3. Apply the same committed mask to opaque and water.
4. Keep radial fallback active unless the hybrid feature gate is explicitly enabled.
5. Verify resource recreation, shader reload, world teardown, and allocation failure.

**Exit:** a controlled synthetic mask can select cache/vanilla ownership at exact chunk
boundaries without changing mesh topology.

### Phase 3 - CPU whole-mesh fast paths

**Implementation status:** Implemented 2026-08-19 behind the same gate. A section whose
every ownership cell is committed vanilla ready is skipped in both the opaque and water
submission loops, and only while the mask is live, so a skip can never run ahead of what
the GPU would have drawn. Residency is untouched by design: the mesh stays resident and
warm so vanilla unloading restores coverage without a reload or remesh.

A controlled stationary pair in `bench/results/2026-08-19-chunk-mask/` measured 467.9 FPS
against a 436.2 baseline (+7.3% average, +6.2% at the 1% low) while skipping about 42 of
149 submissions per frame, with mesh residency and selected-node counts unchanged and zero
evictions. The mask alone was neutral; the gain is entirely this phase. No person has seen
the result, and moving, teleport, and view-distance scenarios remain unmeasured.

1. Use aggregate count zero for the current unmasked path.
2. Skip both opaque and water draw submission when the section is wholly vanilla-owned.
3. Use mixed masking only for partial sections, including temporarily coarse parent
   fallback meshes.
4. Confirm draw skipping does not modify residency timestamps or dirty obligations.

**Exit:** the common steady states are cheaper than unconditional shader masking.

### Phase 4 - replace the radial handoff

1. Enable hybrid ownership for normal play while retaining a guarded internal fallback.
2. Disable radial near discard whenever committed hybrid state is healthy.
3. Exercise load, unload, teleport, dimension/world changes, shader reload, and competing
   LOD-mod deferral.
4. Fix sustained overlap or holes before attempting cosmetic transition work.

**Exit:** chunk-aware ownership is the sole normal near handoff and the original three
reported artifacts do not return.

### Phase 5 - seam work only if evidence requires it

1. Reproduce and classify any remaining seam as horizontal top, vertical boundary face,
   water, or readiness timing.
2. Correct coordinate/face ownership first.
3. Add a masked frontier skirt only when the geometry evidence proves it necessary.
4. Re-measure vertices, draw cost, and GPU time after any seam geometry.

**Exit:** accepted visual behavior without broad overlap, sinking, or fade bands.

### Phase 6 - performance acceptance and packaging

1. Run paired radial-versus-hybrid benchmarks in cache-only, vanilla-settled, and moving
   frontier scenarios.
2. Compare CPU submission, traversal, readiness maintenance, mask upload, GPU frame time
   where available, FPS, worst-1% frame time, and hitch counts.
3. Human-test the visual matrix above. Automated evidence cannot accept z-fighting,
   popping, or cracks on behalf of the player.
4. Remove diagnostic-only instrumentation that materially affects disabled performance.
5. Produce a test zip only after automated/source acceptance; the user performs the
   requested in-game evaluation.

**Exit:** no established regression, expected draw reduction is observed, and a packaged
playtest artifact is ready for human testing.

## 13. Automated checks

### Pure state/model checks

- Coordinate mapping on every 32-block boundary and at large section origins.
- Ring wrap, stale-generation rejection, window movement, and teleport reset.
- Candidate coalescing and budget continuation.
- False-to-true stabilization and immediate true-to-false invalidation.
- View-distance growth/shrink and world-height changes.
- Seven-level aggregate count increment/decrement without underflow or double count.
- Section classification: zero, mixed, and complete for L0 through L6.
- CPU readiness cannot publish before its GPU update is accepted.
- Texture failure leaves ownership cache-visible.
- Teardown rejects late events/results from the old world.
- Vertical distribution separates an unprobed column from a partly ready one, attributes
  each ready cell to its own Y band, and refuses a histogram shorter than the world.
- The benchmark runner's readiness pattern matches the renderer's own emitted line, so a
  format change fails the fast tier instead of a game run.

### Shader/static checks

- Shader files remain ASCII.
- Ownership discard precedes lighting, noise, fog, sky, and output work.
- Cache-only mode does not execute a readiness texture sample.
- Opaque and water share the same readiness lookup.
- Radial discard is inactive in healthy hybrid mode.
- Integer/ring uniforms are uploaded by the renderer.

### Renderer integration checks

- Zero-ready section submits its existing draw.
- Fully ready section submits neither opaque nor water.
- Mixed L0 and coarse fallback sections submit masked draws.
- Frustum rejection still prevents traversal/draw work independently.
- Suppression does not cause mesh disposal or a remesh request.
- An unloading cell restores the draw path without waiting for persistence reload when the
  mesh is resident.
- Shader reload and renderer disposal release the mask exactly once.

## 14. Performance acceptance

Use the same world, cache, camera route, graphics settings, vanilla view distance, far cap,
detail distance, resolution, and frame cap for both sides. Preserve raw logs and route
artifacts. Alternate run order where practical to reduce warm-up bias.

### Scenarios

1. **Cache-only frontier:** most selected sections have zero ready cells. This isolates
   tracker overhead and proves the unmasked fast path remains cheap.
2. **Vanilla-settled center:** vanilla has replaced the near cached region. This measures
   skipped draws and expected CPU/GPU gain.
3. **Continuous flight:** chunks enter and leave readiness while the camera moves and
   rotates. This stresses probes, texture changes, and mixed draws.
4. **Large cache:** thousands of persistent sections prove readiness cost depends on the
   bounded vanilla window, not explored cache history.
5. **View-distance change and teleport:** burst paths prove budgets and fallback coverage.

### Required results

- No full loaded-chunk or resident-mesh scan is introduced in an ordinary frame.
- Cache-only steady-state tracker work approaches zero after initial convergence.
- Disabled diagnostics add no steady-state managed allocation.
- Readiness maintenance and mask upload remain below their measured frame budget; no
  admitted batch creates a 25 ms renderer phase.
- Cache-only and moving-frontier aggregate frame time are not consistently worse outside
  the established run-to-run noise band.
- Vanilla-settled draw submissions decrease by the counted fully ready opaque/water
  meshes, with CPU draw time equal or lower.
- GPU time or worst-1% frame time is equal or lower in the vanilla-settled case. If GPU
  timing is unavailable, label the GPU claim unestablished rather than inferring it from
  CPU submission time.
- Mesh count/residency does not oscillate when turning around.
- Pending and revalidation age remain bounded during sustained flight.

If a repeatable regression exceeds benchmark noise, do not hide it in an average. Identify
whether it comes from probes, texture publication, mixed sampling, state aggregation, or
extra instrumentation, then optimize or retain the radial fallback.

## 15. Player acceptance

The implementation is not visually accepted until human testing establishes:

- No render-radius-shaped gap while vanilla chunks stream.
- No sustained cached/vanilla z-fighting near the player.
- No downward shrink or sinking transition.
- No world-anchored color alternation regression.
- No repeated ownership flicker while hovering at a boundary.
- Cached terrain returns promptly when backing away or reducing view distance.
- Chunk-shaped handoff popping is acceptable in motion.
- No objectionable cliff, water, cave, or structure seams.
- No noticeable new stutter during flight and camera rotation.

Record visual concerns separately from measured performance. A visually correct design
may still fail the performance gate, and a faster renderer may still fail the ownership
contract.

## 16. Documentation and rollout

After implementation and evidence:

- Update `dev/ARCHITECTURE.md` with the settled ownership tracker, GPU mask, and separation
  from residency.
- Add a gotcha if the engine's rendered signal, upload ordering, or unload lifecycle proves
  surprising and repeatable.
- Update `STATUS.md` with source, harness, integration, human, and performance evidence at
  their actual levels.
- Update `dev/TODO.md` with only remaining visual or benchmark debt.
- Add the change under `Unreleased` in `CHANGELOG.md` once behavior is established.
- Record the implementation session and run `dev/DocCheck.ps1` during finalization.

The first testing zip should remain explicitly labeled as a playtest. Do not publish a
release or remove the internal radial fallback until the user's in-game acceptance and the
paired performance evidence are both complete.
