# Plan - Cache startup, panoramic coverage, and refinement

**Status:** Complete and human-accepted through 0.3.95. The owner accepted the under-player L0
foundation, outward radial sharpening, proper-colour first reveal, and the final dominant priority
for the closest band. Reopen only for a new observed regression; Phase 8 resumes from its existing
controlled preset boundary.
**Created:** 2026-08-24
**Scope:** Client demand-loading and meshing of already-persisted Vintage Horizons terrain after
joining a world. Capture, storage format, networking, and GPU Phase 8 remain unchanged unless
measurement proves one is directly involved.

## 1. Problem statement

The owner spends most test time waiting for cached terrain rather than testing it:

1. A join can show no cached terrain for roughly 30-60 seconds.
2. Once terrain begins appearing, refinement has no legible coarse-to-fine progression.
3. Terrain behind the camera remains unloaded or at its coarsest level until the owner turns to
   look at it.

The current 5,317-section world reproduced the first problem more severely. Version 0.3.90 logged:

- 10.0 seconds: 38 resident sections, **0 render-dirty, 0 loads in flight, 0 mesh jobs**, and 5,101
  render frames skipped because no mesh existed;
- 30 seconds: 0 meshes, 0 selected nodes, empty worker queues, and still 0 render-dirty;
- first mesh at 75.1 seconds, 100 at 79.0, 300 at 89.3, 600 at 189.3, and 1,200 at 217.8.

This is not a disk-throughput backlog. During the dead period, no subsystem is asking the storage
thread to read cached terrain.

## 2. Source-traced mechanisms

### 2.1 Bootstrap has a circular dependency

`LodWorld.InstallStoredKey` registers only the quadtree skeleton. It deliberately does not load
section data or mark it render-dirty. `LodTerrainRenderer.RenderFrame` schedules existing dirty
work, then returns before the selection walk while both mesh dictionaries are empty. The selection
walk is the ordinary caller of `RequestMesh`, which creates dirty work and eventually invokes
`TryGetForRender` to start an asynchronous load.

Therefore:

```text
no mesh -> no selection walk -> no mesh request -> no dirty key -> no load -> no mesh
```

Newly captured L0 sections eventually break the cycle by marking themselves changed. That explains
why the first cached mesh can arrive tens of seconds after the cache keys themselves were known.

### 2.2 Camera visibility currently gates demand, not just drawing

`CollectDrawNodes` calls `NodeInView` before it requests a mesh or descends into children. A subtree
behind the camera returns immediately. This makes turning the camera a load/refinement trigger by
construction. It is not an eviction bug: residency and visibility are already separate, but demand
eligibility is still frustum-only.

### 2.3 Refinement is local but not panoramic

For an in-view node, the walk requests only the level wanted at that distance. A parent remains as
coverage until all **visible** children have meshes. The dirty scheduler then chooses nearest-first
from only the obligations the view created. Each rule is locally conservative, but together they
cannot produce a camera-independent, comprehensible 360-degree refinement sequence.

## 3. Required behavior

The replacement policy should be explicitly two-dimensional:

- **Coverage stage:** establish coarse, hole-free cached terrain in every direction around the
  player, independent of camera orientation.
- **Refinement stage:** replace that coverage in stable distance/LOD rings, near-to-far, while
  parents remain until their required children are ready.

Visibility may prioritize work within an equal coverage/refinement band, but it must not be the
only way work becomes eligible. Turning the camera should reveal work already loaded or already
queued, not create an untouched half-world demand wave.

## 4. Implementation phases

### Phase 0 - Pin the scheduler policy offline

- Add deterministic fixtures for a persisted quadtree with zero resident sections and zero dirty
  keys.
- Model front and rear hemispheres at a fixed camera position.
- Record time/order proxies: first load request, first mesh request, coarse coverage by radial band,
  requested level by band, and obligations created only after a 180-degree turn.
- Pin existing safety invariants: no mesh for absent data, no synchronous decompression, no-hole
  parent coverage, bounded queue ownership, and visibility-independent residency.

Gate: the fixture reproduces both the zero-work bootstrap and the rear-hemisphere starvation before
the implementation changes.

### Phase 1 - Break the empty-mesh bootstrap cycle

- Add an explicit startup demand planner that runs after cache-key discovery and does not require an
  existing mesh.
- Seed only data-bearing keys appropriate for coarse coverage around the player; do not mark all
  5,317 rows dirty or load the entire historical cache.
- Reuse `TryGetForRender`, the storage thread, exact dirty ownership, and existing snapshot/upload
  budgets. Do not add an inline load shortcut.

Gate on the current world: a load and mesh obligation begin immediately after cache keys are known;
the ten-second `Join:` report must never again show the zero-dirty/zero-load/zero-mesh circular
stall. Provisional human target: first cached terrain within five seconds.

### Phase 2 - Establish panoramic coarse coverage

- Generate orientation-independent coarse obligations in stable distance bands.
- Keep the existing frustum walk authoritative for what draws this frame.
- Let visible work win ties where that improves first view, but reserve progress for rear and side
  bands so a fixed camera cannot starve them.
- Add cheap telemetry for coarse-ready versus coarse-required sections by band and hemisphere.

Gate: after coarse fill reports complete, a 180-degree turn exposes already-ready terrain without a
new storage/mesh burst and without a coarsest-only rear hemisphere. Provisional target: useful
360-degree coarse coverage within fifteen seconds on the current cache.

### Phase 3 - Make refinement legible and convergent

- Refine by stable radial LOD bands rather than only by current frustum discovery order.
- Preserve parent coverage until every required replacement child is resident, meshed, and ready.
- Prevent a busy/in-flight key from losing its exact obligation; reuse the scheduler rules pinned by
  G25.
- Define and report a terminal settled state for the current camera position: required/ready by LOD
  and band, pending loads, pending meshes, and oldest refinement obligation.

Gate: with the camera stationary, terrain continuously sharpens near-to-far and reaches the same
LOD distribution in front and behind. No rotation is required for convergence.

### Phase 4 - Runtime and memory gate

- Compare the same warm-cache join before/after with identical cache, position, and configuration.
- Measure first mesh, coarse-coverage time, refinement convergence, load/decode queues, snapshot and
  upload bytes, frame timeline, and resident/arena memory.
- Exercise a large cache, a small cache, movement during fill, world leave/rejoin, server-only
  offers, and a cache with missing/corrupt rows.

Gate: no unbounded cache-wide load, no new 25 ms Vintage Horizons tick, no lost dirty obligation,
no turning-triggered remesh storm, and no visual holes while refinement replaces parents.

## 5. Explicit non-solutions

- Raising per-frame mesh or upload budgets cannot fix a period with zero requested work.
- Marking every stored key dirty at join would remove the deadlock by loading historical terrain the
  player may never approach; it violates the cache-size/RAM independence that key-only discovery
  was built to preserve.
- Running the current frustum walk once before the first mesh would fix only the initial view. It
  would leave the rear-hemisphere starvation and incoherent refinement policy intact.
- Visibility-driven eviction must not return. G8 remains authoritative: visibility controls drawing;
  residency and planned coverage have independent policies.
- Phase 8 culling/cluster work remained paused during implementation so scheduler changes could not
  contaminate its comparisons. With this plan human-accepted, Phase 8 resumes independently.

## 6. Closure evidence

1. Deterministic fixtures reproduce the zero-resident bootstrap, exclude structural ancestors,
   preserve eight-direction fairness, pin the diagonal wave, reach under-player L0, and hold the
   combined 32-row/eight-request ceiling.
2. Versions 0.3.92-0.3.95 were built, packaged, verified, and installed in sequence; the complete
   fast tier passes 5,013 assertions.
3. The owner accepted immediate startup, camera-independent radial sharpening, under-player and
   closest-band detail, proper-colour first reveal, and continued all-direction progress.
4. Assist protocol 1, blob format 4, and database schema 6 did not change.
