# Plan - GPU-driven cached-terrain renderer

**Status:** Phase 0 capability feasibility is complete on the primary machine; controlled
FPS baselines and noise-floor ownership moved to the owner on 2026-08-21. Phase 1 is
approved and source/harness-complete. Legacy remains the only visible draw path; no regional
arena, indirect, HZB, or other visible fast path is approved yet.
**Created:** 2026-08-21
**Scope:** Client rendering of Vintage Horizons cached terrain. Storage, capture, mip
generation, networking, and the persisted section format remain unchanged unless a later
phase proves that a renderer-only representation cannot meet its acceptance gate.
**Relationship to existing work:** This plan begins after the accepted visibility and
overdraw work recorded in `PLAN_MAIN_THREAD_PERFORMANCE.md`. `dev/TODO.md` remains the
authority for open work, `STATUS.md` for current evidence, and `dev/ARCHITECTURE.md` for
settled invariants.

## 1. Decision summary

The long-term target is an optional OpenGL 4.3+ renderer that keeps cached terrain in
regional GPU arenas, culls an already-safe candidate set with a hierarchical depth buffer
(HZB), writes indirect draw commands on the GPU, and submits the surviving opaque terrain
with one or a small number of multi-draw calls.

The existing renderer remains the complete compatibility and correctness path. The fast
path must not replace it until the new path has independently passed correctness,
performance, visual, motion, and cross-driver gates. Unsupported hardware, shader failure,
resource allocation failure, or a disabled setting must select the existing path without
changing cache contents or requiring a restart.

The recommended implementation order is deliberately conservative:

1. Establish GPU-stage timing and capability facts.
2. Introduce a renderer boundary and shadow mode without changing pixels.
3. Prove regional allocation using the existing expanded vertex format.
4. Prove indirect multi-draw with CPU-approved candidates and no new occlusion.
5. Build and validate HZB classification in shadow mode.
6. Let GPU culling write the indirect opaque command list.
7. Add cached-on-cached occlusion only if it beats its extra HZB work.
8. Replace expanded vertices with packed quads only after the submission path is stable.
9. Move LOD selection or subdivide sections only if measurements still justify it.

This ordering separates three different possible bottlenecks:

- CPU traversal and per-section submission.
- Hidden raster and fragment shading.
- Geometry memory, upload bandwidth, and vertex processing.

No phase may claim improvement in one merely because total FPS moved. Each phase measures
the work it intends to remove.

## 2. Current baseline

The current renderer already has the following layers, all of which are preserved unless a
fast-path phase explicitly replaces their mechanism:

- Distance-based quadtree LOD selection with parent coverage until required children are
  ready.
- Quadtree and final-section frustum rejection.
- Independent distance/age mesh residency; visibility does not evict resources.
- Whole-section skip where vanilla owns every cell, plus a fragment ownership mask for
  mixed sections.
- Outward counter-clockwise opaque geometry with back-face culling.
- Front-to-back opaque submission.
- Post-vanilla rendering so ordinary depth testing sees nearby vanilla terrain.
- Delayed exact-geometry `AnySamplesPassed` queries with fail-open epochs, local mesh
  invalidation, periodic probes, mixed-seam protection, and turning-edge protection.
- Separate two-sided blended water/thin-cover submission.
- Render-thread-only OpenGL ownership and boundary-budgeted mesh uploads.

The current CPU mesh representation expands each greedy quad to four 16-byte vertices and
six 32-bit indices. Each opaque or water section is uploaded through the engine as its own
`MeshRef`, and rendering performs per-section lookups, uniform updates, and `RenderMesh`
submission. That is the scaling boundary this plan addresses.

Existing evidence says hidden cached terrain can dominate some views and that delayed exact
queries can recover several milliseconds on the owner's machine. It does not yet separate
remaining shader, raster, memory-bandwidth, query, CPU submission, and driver costs. The
first phase therefore measures those costs instead of assuming which later phase will win.

## 3. Outcomes

The fast path should produce the following player-facing result:

- Large cached worlds should not become progressively more expensive merely because they
  contain more resident section meshes.
- Mountain, valley, downward-looking, and enclosed views should avoid drawing distant
  cached terrain that contributes no visible samples.
- Moving and turning should retain the current no-hole behavior and should not introduce
  edge flashes, delayed terrain, coarse replacement storms, or periodic stalls.
- Visible open-horizon views should benefit from lower submission and geometry overhead
  even when little terrain is occluded.
- Unsupported or problematic systems should continue to render through the current path.

The engineering outcomes are:

- Reduce ordinary opaque cached-terrain submission from one call per visible section to a
  bounded number of regional indirect calls.
- Make hidden-section decisions on the GPU without one query object per section and without
  CPU result polling.
- Keep the CPU as the initial authority for section availability and hole-free parent/child
  coverage.
- Reduce live geometry bytes and upload bandwidth after the regional path is proven.
- Preserve explicit diagnostics and a live comparison path until the new default has been
  accepted on real hardware.

## 4. Non-goals

The first complete fast path will not:

- Replace the vanilla near-terrain renderer.
- Change capture, mip generation, database rows, assist protocol, or section blob format.
- Require a server installation.
- Occlusion-cull blended water or thin cover.
- Depend on ray tracing, bindless textures, sparse buffers, mesh shaders, or Vulkan.
- Make visibility control CPU/GPU residency or persistence.
- Start with GPU-owned quadtree traversal or LOD selection.
- Add a full cached-terrain depth prepass.
- Delete the legacy renderer.
- Promise a default-on rollout from source or harness evidence alone.

## 5. Governing invariants

These are acceptance constraints, not implementation preferences.

1. The GL 3.3 renderer remains complete and visually correct.
2. OpenGL object creation, upload, dispatch, draw, fence, and disposal remain on the render
   thread.
3. Workers may produce immutable CPU arrays or packed records but never touch OpenGL or
   live `LodWorld` state.
4. A parent remains drawable until the required child coverage is resident and published.
5. Visibility, residency, persistence, and vanilla ownership remain distinct policies.
6. Every asynchronous or deferred result carries world identity plus section generation or
   an equivalent stale-result guard.
7. Resource and publication queues are bounded before retaining large data; render-thread
   work is bounded by elapsed time and bytes, not only item count.
8. A culling uncertainty draws. It never creates a hole.
9. Mixed vanilla/cache seam sections remain protected until a finer-grained proof safely
   replaces that rule.
10. Water and thin cover retain their established two-sided blending behavior and ordering.
11. Camera-relative precision, stable world-space color noise, ownership-mask addressing,
    open-edge fade, fog, seasonal tint, snow, and curvature remain visually equivalent.
12. Shader sources remain pure ASCII and shader/CPU buffer layouts receive static checks.
13. The fast path does not synchronously read GPU visibility results on the CPU.
14. No optimization may introduce `glFinish`, a same-frame query wait, or an unbounded fence
    wait in an ordinary frame.
15. A failed optional feature disables itself and reports why; it does not damage cache or
    renderer state.

## 6. Target architecture

```text
Owning thread and mesh workers
    section data -> immutable mesh/quad result + identity
                         |
                         v
Render thread publication
    regional opaque arena + section table + allocation generations
                         |
CPU-safe candidate selection (initial fast path)
    parent/child coverage + residency + vanilla whole-section ownership
                         |
                         v
Candidate buffer for frame N
    section record ids in approved order
                         |
                         v
Current vanilla depth copy -> HZB reduction
                         |
                         v
Compute culling
    frustum + distance cap + ownership guard + HZB test
                         |
                         v
Indirect command buffer
    zero-count commands or compact visible commands
                         |
                         v
One/few regional opaque multi-draw calls
                         |
                         v
Legacy water/thin-cover pass
```

The initial GPU path receives candidates that the current CPU traversal has already proven
safe. This deliberately leaves some CPU traversal work in place. The first objective is to
remove per-section GL submission and hidden GPU work without moving the no-holes LOD policy
into a second implementation at the same time.

Only after that path is measured may the GPU take over candidate hierarchy traversal or LOD
choice.

## 7. Compatibility tiers

Capability selection must be explicit and logged once. Version strings alone are not
sufficient; required entry points, limits, shader compilation, and a minimal resource test
must all succeed.

### Tier 0 - current renderer

- OpenGL 3.3-compatible shader path.
- Per-section `MeshRef` resources.
- CPU traversal and submission.
- Delayed exact-geometry query occlusion.
- Always available when current rendering is available.

### Tier 1 - baseline GPU-driven path

Expected minimum: OpenGL 4.3 or equivalent extensions providing:

- Compute shaders.
- Shader storage buffers.
- Multi-draw indirect.
- Image load/store or another proven HZB reduction mechanism.
- Required memory barriers and buffer-copy operations.
- Sufficient texture dimensions, work-group limits, and SSBO sizes.

The base Tier 1 implementation should not require an indirect-count extension. It may
produce one command per CPU-approved candidate and set `count = 0` for culled entries, then
call multi-draw with the CPU-known candidate count. This avoids a readback of the compacted
visible count.

### Tier 2 - optional enhancements

Use only when individually detected and measured:

- Persistent mapped buffers for lower candidate/upload overhead.
- Count-driven indirect drawing for compact command lists.
- Shader draw-parameter extensions where they simplify metadata indexing.
- More efficient texture views or depth-copy paths.

Tier 2 features are refinements, not correctness dependencies.

## 8. Proposed render data

Exact names are provisional. The important part is the ownership and identity model.

### CPU section record

One record per live renderable section should contain:

- Packed section key and LOD level.
- World-space horizontal origin and footprint.
- Conservative vertical bounds; use actual mesh bounds when cheaply available, otherwise
  the full world height and accept weaker occlusion.
- Opaque allocation handle and generation.
- Water allocation handle and generation, if later regionalized.
- Mesh/content generation used to reject stale publication.
- Opaque element or quad range.
- Open-neighbor edge flags.
- Vanilla ownership class: cache-only, mixed, or vanilla-only.
- Flags for temporal/disocclusion protection and diagnostic forcing.
- Region/page identity.

### GPU section table

The GPU-visible table should contain only fixed-width render facts:

- Camera-relative or region-relative bounds.
- Section origin/footprint and LOD scale.
- Geometry range.
- Ownership and draw flags.
- Allocation generation or validity token.
- Region-relative transform data needed by the shader.

The C# and GLSL layouts must use explicit sizes and offsets. A test should verify every
field offset and total stride rather than trusting default structure packing.

### Geometry arenas

Start with the existing expanded vertex/index representation in a regional arena. This
isolates buffer management and multi-draw correctness from a geometry-format rewrite.

Recommended allocator properties:

- Separate opaque and translucent arenas.
- Fixed-size pages grouped by broad world region, with a free-range structure within each
  page.
- Allocate-new, publish-new, retire-old replacement semantics.
- No in-place overwrite of a range potentially referenced by an in-flight frame.
- Deferred retirement behind a GPU fence; never block the frame waiting for the fence.
- Bounded reclamation checks per frame.
- Fragmentation, largest-free-range, committed bytes, live bytes, pending-retire bytes, and
  allocation-failure telemetry.
- Compaction as a later measured maintenance operation, not part of initial correctness.

### Per-frame buffers

Use a small ring, normally three or more slots, for:

- Candidate section ids.
- Indirect commands.
- Optional visible-command counter.
- Per-frame camera/projection/culling constants.
- Delayed GPU timer results.

A slot is reused only after its fence is signaled. If no slot is available, the fast path
must skip optional work or use the legacy path; it must not wait without a strict measured
ceiling.

## 9. Indirect draw design

The first indirect path should preserve the current CPU candidate order and use a fixed
command slot for every candidate:

- CPU traversal emits a safe candidate list.
- CPU fills the immutable parts of each command or identifies the matching section record.
- Compute copies or retains the command for visible sections.
- Compute sets the geometry count to zero for rejected sections.
- `MultiDraw*Indirect` uses the CPU-known candidate count.

This design avoids three early risks:

- No visible-count readback.
- No atomic compaction contention.
- No ordering ambiguity for front-to-back opaque commands.

If command processing itself later matters and indirect-count support is available, a
second path may compact visible commands. The compacting shader must retain deterministic
front-to-back buckets or demonstrate that losing that ordering is cheaper than preserving
it.

Per-section shader state cannot remain ordinary uniforms in a multi-draw. Prefer a
section-record index supplied through an instanced integer attribute selected by the
indirect command's base instance. If capability probing proves shader draw parameters are
portable on the supported tier, they may replace that mechanism. Do not make an optional
extension a hidden baseline dependency.

## 10. HZB design

### 10.1 Depth-source feasibility

Before implementation, source-trace and runtime-probe the active opaque framebuffer:

- Is depth texture-backed or renderbuffer-backed?
- Is multisampling enabled, and can depth be resolved or blitted into a private texture?
- What is the depth internal format?
- Is the game using conventional or reversed depth?
- What is the clear value and comparison function?
- Can the copy occur at render order 0.38 without disturbing later engine passes?
- Which GL framebuffer, viewport, read buffer, draw buffer, texture, program, VAO, and
  enable states must be restored?

The preferred approach is to copy or blit current depth into a private base texture, then
build the mip chain in compute. Sampling an attachment while the same image is actively
being rendered must not be assumed safe.

If current vanilla depth cannot be copied reliably for a tested configuration, HZB is
disabled for that configuration and the delayed-query path remains active.

### 10.2 Conservative pyramid

For conventional depth, every coarser texel must retain the farthest depth represented by
its children; for reversed depth, it must retain the corresponding conservative opposite.
The reduction operation must be selected from detected depth convention, not hard-coded.

Each section test should:

1. Transform the conservative AABB using the exact view/projection pair for the source
   depth.
2. Treat near-plane crossings, invalid homogeneous coordinates, NaN/infinity, and
   degenerate projected bounds as visible.
3. Clamp the projected rectangle to the viewport.
4. Choose a mip that covers the rectangle with a small bounded sample count.
5. Compare the section's nearest possible depth against the pyramid's conservative depth.
6. Apply a measured bias for quantization, raster rules, and camera motion.
7. Draw on every uncertain or borderline result.

Mixed ownership sections, diagnostic paint mode, and explicit safety profiles bypass HZB
suppression initially.

### 10.3 Cached terrain occluding cached terrain

Current vanilla depth alone cannot represent cached opaque geometry that has not yet drawn
this frame. Evaluate these approaches in order:

1. **Vanilla-only current HZB.** Cheapest. It addresses the already-proven nearby vanilla
   hill case and may be sufficient after batching reduces submission cost.
2. **Two-stage cached draw.** Cull/draw a near opaque bucket, rebuild or refresh HZB from
   the depth now containing vanilla plus near cached terrain, then cull/draw the far bucket.
   This uses real color draws as occluders and avoids a separate depth prepass.
3. **Previous-frame full-depth HZB.** Test against the previous view/projection with the
   same fail-open motion, projection, mesh-generation, seam, and screen-edge principles as
   current temporal queries. Use only if the extra temporal complexity beats option 2.
4. **Dedicated depth prepass.** Defer unless measurements prove the other choices cannot
   recover a material cached-on-cached remainder. It duplicates geometry work and is not
   the default design.

Do not stack per-section temporal queries on top of an accepted HZB fast path. Queries remain
the Tier 0 alternative. During shadow validation they may run only to compare classifications
and must not both make suppression decisions.

## 11. Packed-quad fast geometry

Packed quads are intentionally later than regional submission and HZB. The expanded format
is wasteful, but changing geometry representation, shader inputs, and batching at once would
make failures difficult to attribute.

### Format study

Prototype at least two formats and record exact error bounds:

- A compact region-relative quad record targeting 8 bytes where field ranges permit it.
- A 12- or 16-byte format that retains simpler decoding or wider world-height/color fields.

The format must represent:

- Quad origin and extents.
- Face direction and winding.
- Opaque/water/thin material band.
- Color and tint slot information.
- LOD column scale.
- Any section or region identity required for mask addressing, noise, open-edge fade, and
  transforms.

Do not force an 8-byte target by losing established visual semantics. A 12- or 16-byte quad
still removes the current four vertices plus six indices per greedy rectangle and may be the
better total-performance format.

### Rendering model

The preferred model is instanced six-vertex quad expansion or vertex pulling from a packed
quad buffer. Avoid a geometry-shader expansion path unless measurement on target hardware
proves it competitive.

Group draws by regional origin so the shader can reconstruct camera-relative positions with
small coordinates. Preserve stable world-space noise and exact integer ownership-mask
addressing; never reconstruct mask cells from a large summed float world coordinate.

### Migration

- Teach the mesher to produce packed quad records beside, not instead of, existing arrays.
- Validate decoded packed geometry against the existing mesher output in deterministic
  checks.
- Upload both forms only in bounded shadow experiments; do not permanently double memory.
- Switch opaque geometry first.
- Keep water/thin cover on the old representation until opaque parity and performance are
  established.

## 12. LOD and cluster strategy

### Initial authority

The CPU remains authoritative for:

- Whether section data and mesh geometry exist.
- Parent/child coverage and no-hole transitions.
- Mesh requests and background scheduling.
- Residency and eviction.
- Whole-section vanilla ownership.

The GPU initially receives only sections the CPU would otherwise consider for drawing.

### Later GPU LOD selection

Move distance/LOD selection only if telemetry shows CPU traversal remains material after
multi-draw. A GPU hierarchy would need immutable parent/child indices, resident-generation
bits, and a rule identical to "parent covers until all required children are ready."

Before GPU LOD selection can suppress the CPU walk, prove in a shadow comparison that both
systems select equivalent geographic coverage over:

- Every LOD threshold.
- Partial child residency.
- Child replacement in flight.
- Camera movement across thresholds.
- Missing/corrupt sections and explored frontiers.
- World clear and renderer restart.

Any coverage disagreement draws the CPU-approved set during validation.

### Cluster subdivision

Whole-section AABBs are the first HZB unit. Add smaller clusters only when measurements show
a substantial population of large sections classified visible despite most of their
geometry being hidden.

If needed, prefer mesher-produced spatial clusters with conservative local bounds and
contiguous geometry ranges. Set a metadata and dispatch budget before selecting cluster
size. Tiny meshlet counts are not automatically faster; they can trade raster savings for
SSBO traffic, command processing, and allocation fragmentation.

## 13. Water and thin cover

Water and thin cover stay out of HZB suppression because their blending and two-sided rules
differ from opaque terrain.

Initial fast-path behavior:

- Opaque terrain uses regional indirect rendering.
- Water/thin cover continues through the established CPU traversal and draw order.
- Opaque depth remains available before the water pass.

Later, water may use a regional arena and CPU-ordered indirect commands solely to reduce
submission overhead. GPU compaction must not reorder blended commands. It is accepted only
if water submission is measured as material.

## 14. Failure and fallback model

### Startup selection

Proposed controls during development:

- `auto`: use the fast path only after capability/resource/shader checks pass.
- `off`: force the existing renderer.
- `on`: request the fast path but still fail safely to the existing renderer.
- Independent development switches for regional drawing, HZB, GPU culling, packed quads,
  and cached-on-cached strategy.

Exact command and environment-variable names should be chosen when the first path exists.
They remain session-only until product acceptance decides otherwise.

### Resource publication

- Build new regional resources completely before publishing them.
- Keep the old section resource or legacy mesh usable until publication succeeds.
- Tag section table entries and retire records with a generation.
- A stale queued update cannot revive a freed allocation.
- World clear advances identity before any resource queue is drained.

### Frame failure

- Failure before any fast draw submits: use the legacy opaque draw that frame.
- Driver/resource failure after submission: record it once, disable the fast path for
  subsequent frames, and avoid an unbounded recovery operation inside the failing frame.
- HZB copy/reduction failure: draw the indirect candidate set without HZB or fall back to
  legacy, depending on which stage has already been proven safe.
- Shader reload: build and validate the replacement program before swapping; retain the old
  working path on compile failure.
- Never persist a failed capability decision across game or driver updates without
  re-probing.

## 15. Telemetry

Telemetry must be cheap when disabled and delayed when reading GPU results.

### CPU measurements

- Candidate traversal time and candidate count.
- Candidate-buffer preparation/upload time and bytes.
- Regional allocation, upload, retirement, and reclamation time.
- Indirect submission CPU time and call count.
- Legacy draw submission time when used as comparison.
- Fast-path fallback count and reason.
- Arena live/committed/free/pending-retire bytes and fragmentation.

### GPU timer stages

Use timestamp or elapsed queries in a delayed ring; never wait in the current frame.

- Depth copy/resolve.
- HZB reduction.
- GPU culling and command generation.
- Near opaque draw, if using two-stage cached occlusion.
- Far opaque draw.
- Total cached opaque pass.
- Water pass where feasible.

### Visibility counters

- CPU-approved candidates.
- GPU frustum rejects.
- Distance-cap rejects.
- Vanilla-only/mixed-protected sections.
- HZB rejects.
- Borderline/bias-protected sections.
- Commands drawn and zeroed.
- Optional cluster candidates/rejects.
- Legacy temporal query results when that path is active.

### Correctness diagnostics

- Shadow-mode disagreement counts between legacy and GPU classification.
- Stale section/generation rejects.
- Buffer fence age and unavailable-ring-slot events.
- GL errors attributed to a named fast-path phase.
- A view-ray explanation command should identify CPU candidate, HZB decision, mip/sample,
  compared depths, bias, section generation, and fallback reason for the first relevant
  section.

## 16. Phase plan

### Phase 0 - rebaseline and feasibility spike

**Implementation status:** Capability feasibility complete; controlled performance evidence
is owner-run. The first source slice adds pure capability policy,
a one-time advertised-versus-validated GL/depth report, opt-in delayed opaque/water GPU
timers, and live draw/geometry counters. The isolated 0.3.41 `ring-overlook` probe validated
the instrumentation on the owner's Radeon RX 9070 XT: GL 4.3, advertised regional MDI/HZB,
conventional 32-bit single-sample texture depth, 2,520 delayed samples, zero ring-full skips
or timer-target conflicts, about 1.25 ms opaque p95/p99 and 0.05 ms water p95/p99. The owner
confirmed the generic landscape camera angle. This is one instrumentation/feasibility run,
not a controlled performance comparison. The isolated 0.3.44 follow-up validated the
required loaded entry points, minimal compute dispatch and expected SSBO readback, including
exact restoration of the incoming program and SSBO bindings. The legacy renderer remained
selected. The isolated 0.3.47 follow-up then validated a disposable 2,560x1,440
`DEPTH_COMPONENT32` blit, all 12 private mip allocations and exact framebuffer/texture
binding restoration with no GL errors. This satisfies the primary-machine capability gate,
not conservative HZB classification. The aerial Bodanboys route added an open-horizon GPU
cost sample, but did not place cached terrain behind enough vanilla foreground to compare
temporal occlusion. The owner has taken responsibility for FPS baselines and noise-floor
measurement; those results remain external acceptance evidence rather than an assistant-run
gate.

**Purpose:** Decide whether the engine/driver surface supports the proposed path before
building architecture on assumptions.

Work:

- Record the exact implementation baseline and preserve current uncommitted user work.
- Add nonblocking GPU timers around the existing cached opaque and water passes.
- Add CPU counters for draw calls, selected sections, vertices/indices, query issue/result
  work, and current GPU mesh bytes.
- Probe GL version, extensions, limits, function availability, depth framebuffer form,
  multisample behavior, depth convention, and copy/resolve feasibility.
- Compile a minimal compute shader and issue a harmless SSBO write/read validation outside
  the measured frame path.
- Prove that a private depth copy and mip chain can be created and destroyed without GL
  errors or engine-state leakage.
- Establish run-to-run noise using alternating current-renderer controls in the settled,
  streaming, open-horizon, valley, downward, and rotation scenarios.

Gate:

- Proceed to Tier 1 only if compute, SSBO, MDI, and a reliable current-depth source are all
  proven on the primary machine.
- If MDI works but depth copying does not, regional multi-draw may proceed independently;
  HZB remains disabled.
- If the remaining current opaque GPU or CPU time is below the measured noise floor, stop
  and document that no fast-path implementation is currently justified.

### Phase 1 - renderer boundary and shadow infrastructure

**Implementation status:** Source- and harness-complete on 2026-08-21; owner runtime
equivalence remains open. Publication/removal, frame preparation, opaque/water drawing,
clear, and disposal now cross one coordinator. Its visible target is structurally fixed to
legacy. An explicit `VINTAGEHORIZONS_GPU_RENDERER=shadow|auto` may activate only a CPU-only
identity/count mirror after Tier 1 validation; the coordinator never invokes its draw
methods, and `off` is the default. World, section-render, and opaque/water resource
generations are tracked. The compute and depth probes share exact state capture/restoration
for programs, generic/indexed SSBO bindings, draw/read framebuffers, active texture, and
texture-unit-zero binding. Fast checks cover fail-closed selection, stale identities,
world/clear lifecycle, shadow-fault isolation, zero shadow draw calls/GL ownership, and
ordered exact state restoration.

**Purpose:** Create a safe dual-path seam without changing rendered output.

Work:

- Split current rendering responsibilities behind a narrow interface: resource
  publication/removal, frame preparation, opaque draw, water draw, clear, and dispose.
- Keep the current implementation behavior byte-for-byte where practical.
- Add capability/path selection and one-time reason logging.
- Add a shadow fast-path owner that may mirror metadata but issues no visible draw.
- Centralize GL state capture/restoration needed by direct OpenGL work.
- Add world epoch, section render generation, and resource generation identities.

Gate:

- Current renderer checks and controlled measurements remain unchanged within baseline
  noise.
- Shader reload, world change, mod defer, and disposal leave no fast-path GL resources.
- Forcing `off` is indistinguishable from the pre-phase renderer.

### Phase 2 - regional arenas with expanded geometry

**Purpose:** Prove allocation, replacement, and lifetime without changing vertex semantics.

Work:

- Create opaque regional vertex and index arenas using the existing 16-byte vertex and
  32-bit index data.
- Mirror a bounded subset first, then all live opaque sections in shadow mode.
- Implement allocate-new/publish-new/fence-retire-old replacement.
- Keep existing `MeshRef` resources authoritative while comparing geometry ranges and
  counts.
- Add allocator and memory telemetry plus deterministic allocator tests.
- Add an explicit memory ceiling during dual residency so shadow validation cannot double
  an unbounded cache.

Gate:

- Repeated replacement/eviction/world-clear stress produces no stale range reuse.
- Arena live geometry matches legacy vertex/index content for every mirrored section.
- No frame performs an unbounded retire or compaction sweep.
- Dual-path shadow memory stays within its configured ceiling and converges after activity.

### Phase 3 - CPU-approved indirect opaque drawing

**Purpose:** Measure batching independently of HZB and compute visibility.

Work:

- Keep current CPU traversal, ownership skip, frustum test, distance cap, and front-to-back
  candidate order.
- Emit one fixed indirect command per candidate.
- Supply per-section metadata through a proven multi-draw-compatible indexing mechanism.
- Render opaque terrain from regional buffers with no HZB suppression.
- Keep legacy water rendering.
- Add a live legacy/indirect comparison switch and screenshot/checkpoint route.

Gate:

- Geometry, winding, fog, mask, noise, tint, snow, open-edge fade, extreme-coordinate
  precision, and vanilla handoff match the legacy path under human review.
- Draw-call count falls by at least 90% in the large-cache scenario, unless region boundaries
  establish a documented lower theoretical limit.
- CPU opaque submission time improves beyond both two times the measured noise and a
  pre-agreed material frame-time threshold.
- Open-horizon GPU time does not regress materially.

### Phase 4 - HZB construction and shadow classification

**Purpose:** Prove conservative depth logic before it can suppress terrain.

Work:

- Copy/resolve current vanilla depth into a private texture after vanilla terrain.
- Build every HZB mip with delayed GPU timing.
- Implement conservative section-AABB projection and sampling.
- Run classification in shadow mode while legacy drawing remains authoritative.
- Compare HZB decisions with exact sample-query outcomes only as diagnostics; do not combine
  their suppression.
- Add deterministic CPU reference tests for pyramid reduction and projected-bound edge cases.
- Add a diagnostic overlay or explanation command for rejected/protected bounds.

Gate:

- No known-visible section is classified hidden across the visual matrix.
- Near-plane, off-screen, mixed-seam, projection-change, and invalid-value cases fail open.
- Depth-copy and HZB-build time is below the savings opportunity established in Phase 0.
- No GL state leakage affects vanilla shadows, SSAO, fog, water, UI, or later render stages.

### Phase 5 - GPU culling drives indirect opaque draws

**Purpose:** Replace per-section temporal queries in the fast path.

Work:

- Dispatch frustum/distance/HZB tests over CPU-approved candidates.
- Produce fixed-slot zero-count indirect commands.
- Insert only the required memory barriers before multi-draw.
- Disable temporal query allocation, issue, and polling while the fast path owns
  suppression.
- Retain mixed-seam, diagnostic, projection, and fail-open guards.
- Preserve candidate order for commands that survive.
- Allow an HZB-off indirect mode to isolate batching from occlusion.

Gate:

- No visibility holes or edge flashes in stationary, motion, rotation, teleport, vertical
  look, streaming, cave/structure, and threshold-crossing tests.
- Target occluded views improve total cached opaque GPU time beyond HZB construction cost
  and baseline noise.
- Open views do not regress materially.
- CPU query-object work and per-section opaque submission disappear from the fast path.
- Fast-path disable restores the accepted legacy behavior immediately or at the documented
  safe renderer boundary.

### Phase 6 - cached-on-cached occlusion experiment

**Purpose:** Determine whether a second depth opportunity pays for itself.

Work:

- Measure the vanilla-only HZB remainder in cached mountain views.
- Prototype one near/far split with a second HZB refresh after the near opaque color draw.
- Compare split strategies based on distance buckets or a bounded near-command budget.
- If warranted, separately prototype previous-frame full-depth reuse with old-view
  projection and strict fail-open motion/generation guards.
- Do not add a full depth prepass in this phase.

Gate:

- Adopt at most one cached-on-cached strategy.
- The added depth copy/reduction and synchronization must save more total GPU time than they
  cost in repeated alternating comparisons.
- Motion behavior must meet the same visual standard as the current aggressive temporal
  profile.
- If neither strategy wins, retain vanilla-only HZB and record the result.

### Phase 7 - packed opaque quads

**Purpose:** Reduce geometry memory, upload bandwidth, and vertex/index processing.

Work:

- Select the packed format through encode/decode and GPU-format experiments.
- Produce packed records directly from greedy meshing without first expanding arrays.
- Add a packed regional arena and fast vertex shader.
- Preserve all current shader semantics and exact ownership addressing.
- Compare 8-, 12-, and 16-byte candidates on decode cost, memory, upload time, vertex time,
  and visual correctness.
- Keep old geometry until the packed replacement is completely published.

Gate:

- Deterministic geometry checks cover all face directions, coordinate limits, material
  bands, tint slots, LOD scales, and degenerate rectangles.
- Human visual parity passes the full matrix.
- Live opaque geometry bytes and upload bytes fall materially; target at least 50% unless a
  smaller reduction produces a better measured total frame time.
- Shader decode cost does not erase the memory/vertex benefit on the primary or second
  tested driver.

### Phase 8 - optional GPU LOD selection and clusters

**Purpose:** Remove only a measured remaining bottleneck.

Work:

- First measure CPU traversal after all previous phases.
- If material, mirror the ready section hierarchy and compare GPU/CPU coverage selections in
  shadow mode.
- Preserve CPU mesh demand and residency ownership.
- If HZB precision, rather than traversal, is the remainder, add moderate mesher-produced
  clusters instead of GPU hierarchy traversal.
- Never introduce both in one measurement patch.

Gate:

- Implement only the measured branch: GPU LOD or clusters.
- Coverage is equivalent and hole-free under partial residency and replacement.
- Metadata, dispatch, and synchronization cost is lower than the work removed.
- Turning does not trigger remesh/reload storms.

### Phase 9 - hardening and default decision

**Purpose:** Decide whether the fast path is ready for ordinary users.

Work:

- Run repeated alternating comparisons on at least two GPU vendors or drivers where
  practical.
- Cover MSAA on/off, SSAO settings, window resize, fullscreen changes, shader reload,
  dimension/world changes, long sessions, large caches, multiplayer, and competing-LOD-mod
  deferral.
- Exercise allocation pressure and injected shader/buffer/depth-copy failures.
- Verify that the fallback path remains current rather than becoming an untested museum
  path.
- Decide `auto` policy and whether any control persists in configuration.
- Update architecture, gotchas, TODO, status, history/session notes, README commands, and
  changelog only when implementation evidence settles those facts.

Gate:

- Human acceptance of visual behavior and motion.
- Controlled performance win above the measured noise floor in target scenarios, with no
  material open-view or frame-time-tail regression.
- No unresolved correctness defect can expose a hole or corrupt renderer/cache state.
- Unsupported and injected-failure cases demonstrably return to legacy rendering.
- Full checks, packaging rules, and documentation ritual pass.

## 17. Verification strategy

### Pure and harness checks

Add focused checks for:

- Capability-policy selection from synthetic version/extension/limit inputs.
- Exact C#/GLSL structure sizes, offsets, strides, and indirect-command layout.
- Arena allocate/free/coalesce behavior, generation reuse, fragmentation, and fence-retire
  policy.
- Region assignment and extreme positive/negative world coordinates.
- Packed-quad encode/decode boundaries and all six face windings.
- HZB reduction for conventional and reversed depth.
- Conservative AABB projection: near-plane crossing, behind-camera, off-screen,
  sub-pixel, giant bounds, NaN/infinity, and depth bias.
- Fixed-slot command generation and zero-count rejection.
- Parent/child coverage equivalence if GPU LOD is attempted.
- World epoch and section/resource generation rejection.
- Fast-path policy failure returning `legacy`, never `nothing`.
- Shader ASCII and required static tokens/layout constants.

### Game-backed correctness matrix

Every game launch requires fresh user approval and must use the repository isolation
scripts. Observe small, specific cases:

- Flat open terrain with little occlusion.
- Valley and mountain views with vanilla occluders.
- Cached hill hiding farther cached terrain.
- Looking sharply down and up.
- Caves, arches, overhangs, cliffs, and structures.
- Fast horizontal yaw and sustained slow rotation.
- Translation, flight, teleport, and LOD-threshold crossings.
- Active streaming and continuous mesh replacement.
- Mixed vanilla/cache seam, mask debug paint, and outer known-data edges.
- Water from above and below, shorelines, thin plants, and section water seams.
- Day/night, seasonal tint, snow line, fog, underwater, and globe curvature.
- Large positive and negative coordinates.
- Resize, graphics-setting change, shader reload, pause/unpause, world exit/rejoin, and
  competing LOD-mod deferral.

### Performance scenarios

Use preserved configurations and alternate A/B order to reduce thermal and route-order
bias:

1. Settled stationary hill/valley view.
2. Settled open-horizon view.
3. Looking down at vanilla foreground.
4. Continuous movement and rotation through a warm cache.
5. Sustained streaming into a new area.
6. Large-cache stationary and moving routes.
7. Cached-on-cached occlusion view specifically constructed from existing world terrain.
8. Water-heavy view if water batching is considered.

Record:

- Average, median, 1% low, p95, p99, maximum frame time, and hitch counts.
- CPU phase times and allocation deltas.
- Delayed GPU stage times.
- Draw/dispatch/barrier counts.
- Candidate, rejection, protection, and command counts.
- Geometry and metadata memory.
- Upload bytes/time, arena fragmentation, pending retirement, and oldest fence age.
- Exact game, mod, graphics, view-distance, world, route, hardware, driver, path, and commit
  identity.

### Performance decision rule

For each phase, first calculate the scenario's run-to-run noise. A phase proceeds only when
its intended metric improves by more than both:

- Twice the observed noise for that metric; and
- A predeclared material threshold appropriate to the phase.

Initial thresholds to confirm after Phase 0:

- At least 90% fewer opaque draw submissions for regional multi-draw.
- At least 0.2 ms or 10% lower targeted CPU/GPU phase time where baseline cost is large
  enough to measure.
- No more than 0.1 ms or 2% material regression in the open-horizon control, accounting for
  measured noise.
- No new Vintage Horizons render phase at or above 25 ms in guarded routes.
- Zero accepted visibility holes.

These numbers are gates, not promised outcomes. Phase 0 may revise them once the actual
noise floor is recorded; any revision must be written before the affected A/B result.

## 18. Risk register

| Risk | Consequence | Mitigation |
|---|---|---|
| Vanilla depth is not reliably copyable, especially with MSAA | HZB cannot see the best occluder | Capability-test the active configuration; allow regional MDI without HZB; retain temporal queries |
| Wrong depth convention or reduction operator | Visible terrain is falsely hidden | Detect convention, reference-test both paths, fail open on ambiguity |
| Direct GL work leaks engine state | Unrelated rendering breaks later in the frame | Centralize state ownership/restoration; integration-test later stages and settings |
| CPU/GLSL layout mismatch | Wrong transforms, ranges, or commands | Explicit offsets/strides and static agreement checks |
| Buffer reuse races in-flight GPU reads | Flicker, corruption, or driver fault | Generation-tagged allocate-new publication and fence-delayed retirement |
| Arena fragmentation | Allocation failure despite free bytes | Region pages, telemetry, bounded new-page growth; measured compaction later |
| Dual-path validation doubles memory | OOM during large-cache test | Hard shadow-memory ceiling and bounded subset mirroring |
| HZB build costs more than it saves | Lower FPS in open or lightly occluded views | Separate HZB switch/timers and quantitative gate |
| Two-stage cached occlusion adds synchronization | Cached-on-cached path loses overall | One measured near/far experiment; reject if total GPU time rises |
| GPU culling breaks mixed ownership seam | Visible ring or holes near vanilla handoff | Initial unconditional mixed-section bypass and mask diagnostics |
| Camera motion exposes stale depth | Edge flashes or delayed terrain | Current-view vanilla HZB first; fail-open bias/edge guards for any temporal reuse |
| GPU LOD disagrees with no-holes CPU policy | Missing terrain or coarse oscillation | Defer GPU LOD; shadow coverage comparison before authority transfer |
| Packed decode adds too much shader work | Memory drops but frame time rises | Compare several formats and retain expanded regional path |
| Water order changes | Incorrect transparency and shore artifacts | Keep legacy water until separately measured; never unordered compact it |
| Driver feature claims are incomplete | Compile, entry-point, or runtime failure | Probe functions/limits/resources, not version string; retain Tier 0 |
| Fast path becomes the only tested path | Compatibility regressions go unnoticed | Include forced-legacy checks and periodic legacy A/B in release validation |

## 19. Rejected or deferred alternatives

- **Same-frame proxy occlusion queries:** already measured and rejected. They added proxy
  raster/query work, retained real-mesh submission, and ran before vanilla depth.
- **Running HZB and per-section queries together:** duplicate visibility mechanisms. Queries
  are fallback or diagnostic comparison, not an additional default layer.
- **Full cached depth prepass:** duplicates geometry work. Consider only after real color
  near/far staging and temporal HZB options fail a measured cached-on-cached need.
- **Software raster occlusion:** possible GL 3.3 experiment, but it cannot easily include
  vanilla foreground and duplicates a now-proven GPU visibility path. Reconsider only if
  Tier 0 CPU submission remains a material supported-hardware problem.
- **PVS, portals, and authored occluders:** poor fit for arbitrary open voxel worlds.
- **Immediate GPU-owned quadtree:** couples the most delicate no-holes rule to the buffer and
  draw rewrite. It is a later measured optimization.
- **Immediate meshlet-scale subdivision:** metadata and command overhead may exceed saved
  raster. Begin at section granularity.
- **Mandatory GL 4.3 renderer:** violates the complete compatibility-path requirement.

## 20. Proposed code boundaries

Names are suggestions; keep types small enough to test independently.

Potential C# files:

- `Render/Gpu/LodGpuCapabilities.cs` - feature/limit probing and path policy.
- `Render/Gpu/LodGpuRenderer.cs` - fast-path lifecycle and frame orchestration.
- `Render/Gpu/LodGpuArena.cs` - regional range allocation and deferred retirement.
- `Render/Gpu/LodGpuSectionTable.cs` - CPU/GPU records and generations.
- `Render/Gpu/LodGpuFrameRing.cs` - candidate/command/timer slots and fences.
- `Render/Gpu/LodGpuDepthPyramid.cs` - depth copy and HZB resources.
- `Render/Gpu/LodGpuTelemetry.cs` - counters and delayed timers.
- `Render/LodRenderPathPolicy.cs` - legacy/auto/fast selection independent of GL calls.

Potential shader assets:

- `lodterrainfast.vsh` and `lodterrainfast.fsh`.
- `lodgpucull.comp`.
- `lodhiz.comp`.

Potential checks:

- `GpuCapabilityChecks.cs`.
- `GpuArenaChecks.cs`.
- `GpuLayoutChecks.cs`.
- `GpuOcclusionChecks.cs`.
- `PackedQuadChecks.cs`.
- `RenderPathPolicyChecks.cs`.

Do not let `LodTerrainRenderer` become the owner of every allocator, shader, fence, depth,
and command detail. It should coordinate the selected path and preserve shared traversal,
readiness, scheduling, and telemetry integration.

## 21. Implementation and review boundaries

Keep the following as separate reviewable changes and separate measurements:

1. GPU timers and capability/depth feasibility.
2. Renderer interface/path selection with legacy-only behavior.
3. Expanded regional arena shadow resources.
4. Expanded regional indirect opaque draw.
5. HZB copy/reduction and shadow classification.
6. HZB-driven indirect suppression.
7. Cached-on-cached near/far or temporal experiment.
8. Packed opaque quad format and rendering.
9. GPU LOD or cluster experiment, never both together.
10. Hardening, accepted defaults, and documentation.

Do not combine a geometry-format change with the first direct-GL arena implementation. Do
not combine HZB construction with the first visible indirect draw. Do not combine GPU LOD
authority with cluster subdivision. These boundaries are what make regressions attributable
and rollback practical.

Commits, pushes, playable packages, releases, and game launches retain the approvals in
`CLAUDE.md`. Before any playable artifact, increment the patch version exactly once in both
version authorities.

## 22. Completion criteria

The vision is complete when all of the following are true:

- A supported fast path stores opaque cached terrain in bounded regional GPU arenas.
- Ordinary opaque submission uses a bounded number of indirect multi-draw calls.
- Conservative HZB culling sees current vanilla depth and suppresses hidden cached opaque
  commands without CPU visibility readback.
- The accepted cached-on-cached strategy is either implemented with a measured net win or
  explicitly rejected with preserved evidence.
- Per-section temporal queries are absent from the active fast path and remain functional in
  the legacy path.
- Geometry packing has produced a measured worthwhile reduction or has been explicitly
  rejected without blocking the regional renderer.
- Parent/child coverage, ownership seams, motion, water, shaders, and extreme coordinates
  meet current visual behavior.
- Visibility remains independent from residency and persistence.
- Resource publication is generation-safe; retirement never stalls an ordinary frame.
- Unsupported and failure cases fall back to the complete legacy renderer.
- Performance gains exceed the recorded noise floor in reproducible target scenarios and do
  not create material control-scenario or tail-latency regressions.
- Human visual acceptance, full automated checks, cross-driver coverage, and documentation
  updates are complete before the fast path becomes the default.

## 23. First actionable milestone

The first implementation milestone is deliberately small and carries no visual behavior
change:

1. Add delayed GPU timers around the current opaque and water passes.
2. Add a pure capability-policy type and one-time diagnostic report.
3. Source-trace and probe the active depth attachment, including MSAA and depth convention.
4. Prove a private depth copy plus mip-chain allocation in a disabled/shadow path.
5. Record alternating baselines and the noise floor.
6. Decide independently whether regional MDI and HZB each have enough measured opportunity
   to proceed.

That milestone converts the largest architectural unknowns into evidence while leaving the
accepted renderer untouched.
