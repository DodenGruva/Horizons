# Plan - chunk-aware cached-to-vanilla terrain handoff

**Status:** Proposed. No implementation work in this document is complete.
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

The installed Vintage Story 1.22.5 client provides
`ICoreClientAPI.IsChunkRendered(EntityPos)`. The implementation resolves the 32x32x32
`ClientChunk` and returns whether its `quantityDrawn` is greater than zero.

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
`[chunk * 32, chunk * 32 + 32)`. Coordinate conversion must be isolated in pure helpers
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

1. Implement coordinate packing, active-window/ring addressing, states, candidate
   coalescing, probe budgeting, stabilization, invalidation, and teardown without changing
   rendering.
2. Add fixed ancestor ready counts and O(1) section classification.
3. Compare tracker state with visible vanilla streaming under diagnostics.
4. Prove zero steady-state allocations and bounded work in isolated checks.

**Exit:** the tracker follows loading, unloading, movement, teleport, and view-distance
changes without influencing pixels.

### Phase 2 - GPU mask behind a disabled feature gate

1. Add the bounded readiness texture/atlas and atomic publication protocol.
2. Add world-to-mask shader addressing and mixed-mode early discard.
3. Apply the same committed mask to opaque and water.
4. Keep radial fallback active unless the hybrid feature gate is explicitly enabled.
5. Verify resource recreation, shader reload, world teardown, and allocation failure.

**Exit:** a controlled synthetic mask can select cache/vanilla ownership at exact chunk
boundaries without changing mesh topology.

### Phase 3 - CPU whole-mesh fast paths

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
