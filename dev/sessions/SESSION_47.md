# Session 47 — Phase 8 clusters measured; loading becomes top priority

**Date:** `2026-08-24`
**Branch/commit:** `render-overhaul`, on top of `b525fcf`
**Mod version:** `0.3.87` through `0.3.91`; 0.3.91 packaged, verified, and installed
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

Session 46 accepted the indexed 12-byte packed representation and directed Phase 8 toward the
measured 4x4 cluster branch. This session implemented that branch, played it repeatedly, discovered
that it amplified an older depth-cull artifact, added a conservative depth margin, and then repaired
the experiment itself when seven independent controls produced invalid comparisons.

The owner then stopped the rendering bisect for a higher-leverage reason: most development time is
spent waiting for cached terrain to appear and refine after every join. The session closed by
source-tracing that loading behavior and making it the next session's top priority. No loading fix
was implemented here.

## Work narrative

### 1. Phase 8 implemented the measured cluster branch

Workers retain Phase 7's whole-section packed stream and produce a second 4x4 spatial stream. Each
populated cell is one contiguous packed range with conservative local geometry bounds. A separate,
demand-committed regional arena publishes those ranges. The indirect builder emits up to sixteen
independently cullable commands per CPU-approved section while the shader record remains
whole-section, preserving ownership-mask addressing, colour noise, edge fade, lighting, fog, and
section transforms.

Missing cluster publication refuses the complete cluster set and returns the section to the
established same-frame fallback. A draw failure repairs the full bucket through legacy rendering.
The focused cluster suite contributes 1,048 assertions covering range partition, geometry area,
bounds, L0/L2/L6 addressing, arena publication, commands, batching, and fail-open fallback.

The first owner scene improved from roughly 360 to 390 FPS, about 0.21 ms, proving the branch can
remove enough raster work to beat its additional command cost in at least one view.

### 2. Clusters exposed a shared depth-cull artifact

Several normally shaped terrain pieces flickered at precise camera angles, including while the
camera was stationary. The new cluster path produced more affected pieces, but did not deform
section geometry. `.vhcull off` stopped every older and newer flicker while clusters remained
enabled. That clears cluster geometry/range publication and identifies the HZB verdict as the
authority making pieces disappear.

Source tracing found a missing requirement from the GPU plan: the HZB comparison used strict
`nearest > farthest` with no margin for projection, rasterization, or 24-bit depth quantization.
Version 0.3.88 added a four-step normalized 24-bit fail-open band to the CPU reference and both
compute uses. A pre-existing offline one-ULP self-occlusion fixture became the regression pin.

### 3. Invalid live state forced one authoritative command

The first 0.3.88 performance follow-up looked as if the safety band had erased the cluster gain, but
its log showed a mixed experiment: late depth was initially off, then hidden share rose from 0.5%
to 4.6% after it was enabled, while clusters were `on, but idle` because packed drawing was off.

Version 0.3.89 added `.vhphase8 on|off` to assign arenas, batching, packing, HZB, GPU culling,
same-frame near/far depth, and clusters together. The first valid run recorded the complete stack
active—5,577 cluster commands in 68 multi-draws—then the complete legacy baseline with every flag
off and arenas released. The owner saw flicker return and measured higher FPS with the complete
stack off. This rejects the stack as currently composed, but does not reverse the large wins from
earlier individually measured renderer stages.

### 4. One command became a chronological preset ladder

All-on/all-off fixed hidden state but collapsed several historical variables. Version 0.3.90 changed
the same command into cumulative `off`, `batch`, `cull`, `late`, `packed`, and `clusters` presets;
`on` remains an alias for `clusters`. Every preset assigns all seven controls, so adjacent stages
cannot inherit stale state.

The first `late` result was visually useful: about 362 FPS, with the single older flickering section
but none of the numerous cluster flickers. The log then caught another experimental confound:
selecting an already-active preset called `RequestGpuShadow("on")` again and queued all 1,678 live
sections for re-mesh. The visual classification counts, but 362 FPS is warm-up-contaminated.

Version 0.3.91 compares arena-request state before invoking the transition. Moving among active
presets now preserves filled buffers; only crossing `off` attaches or releases them. The no-argument
report describes the selected stage's effective path. The next rendering comparison would be
`late` versus `cull` in one settled session, but it is deliberately paused.

### 5. The loading delay is a zero-work bootstrap failure

The latest ordinary join loaded a cache with 5,317 known sections. The log is conclusive:

- after 10.0 seconds: 38 resident, **0 render-dirty, 0 loads in flight, 0 mesh jobs**, and 5,101
  render frames skipped because no mesh existed;
- after 30 seconds: 0 meshes, 0 selected nodes, empty worker queues, 0 render-dirty;
- first mesh at 75.1 seconds, 100 at 79.0, 300 at 89.3, 600 at 189.3, and 1,200 at 217.8.

`InstallStoredKey` intentionally creates only the quadtree skeleton. `RenderFrame` returns before
the selection walk while there are no meshes. The selection walk is the ordinary code that requests
meshes, and mesh scheduling is what starts asynchronous section loads. With no dirty bootstrap key,
the cache path is circular and idle; newly captured L0 data eventually breaks the cycle.

The owner's other two symptoms are also source-traced. `CollectDrawNodes` rejects an out-of-frustum
subtree before requesting its mesh or descending into children, so terrain behind the camera is
ineligible for load/refinement until the player turns. For visible terrain, the walk requests only
the level wanted at that distance and parents remain until visible replacement children are meshed.
Nearest-first scheduling is coherent only inside the obligations the current view happened to
create; it cannot yield camera-independent panoramic sharpening.

A dedicated plan now defines separate bootstrap, 360-degree coarse-coverage, and stable radial
refinement phases. The intended shape is coarse hole-free coverage in every direction, then
near-to-far LOD refinement, with visibility at most a tie-breaker rather than the only eligibility
rule. Existing asynchronous loading, exact dirty ownership, parent coverage, bounded snapshot/GPU
upload, and visibility-independent residency remain required.

---

## Delivered

- 4x4 clustered packed geometry, conservative cluster bounds, a separate bounded arena, clustered
  indirect commands, telemetry, fallback, environment/benchmark control, and 1,048 focused checks.
- 0.3.87, owner-measured at roughly 360 to 390 FPS in one scene, with additional depth-cull flicker.
- 0.3.88's four-step normalized 24-bit fail-open depth margin and shader/reference regression pin.
- 0.3.89's authoritative all-on/all-off Phase 8 command and logged state report.
- 0.3.90's cumulative one-command phase ladder.
- 0.3.91's idempotent arena transition and effective-path reporting; packaged, verified, and copied
  to the normal Mods folder. SHA-256:
  `C981F601614BAFCEC8DE2B0895E349AC945AAF1C8BB1663662EBCFD2EF03662D`.
- Warning-free Release build, **4,980 fast assertions**, and **1,513 documentation checks**.
- Production-log and source diagnosis of the empty-mesh bootstrap cycle and view-gated refinement.
- `PLAN_CACHE_STARTUP_AND_REFINEMENT.md`, with loading established as the next session's top priority.
- No game process was launched by the assistant; no commit, push, tag, or public release was made.

## Decisions

**Pause Phase 8.** Its next adjacent comparison is known, but faster reliable cache fill will reduce
the human cost of every remaining rendering test. Loading therefore takes priority.

**Do not call the complete Phase 8 stack rejected work.** The valid all-on/all-off result rejects
that composition. Earlier batching, arena-sizing, depth, and first-cluster gains remain measured
facts; the chronological ladder exists to locate the regressing addition later.

**Treat cluster and old flicker as two populations.** `late` reproduces the one older section;
`clusters` adds many more. `cull` is the next diagnostic boundary after loading is fixed.

**Separate coverage demand from draw visibility.** Frustum culling should still decide what draws,
but turning the camera must not be required to make persisted terrain eligible for load or
refinement. Coarse 360-degree coverage should precede stable radial sharpening.

**Do not solve zero work by raising throughput.** No queue was backed up during the 75-second
delay. The bootstrap must create bounded obligations; throughput tuning comes only after work
exists and telemetry shows a backlog.

## Traps

- Strict HZB depth inequality is not conservative; reserve an explicit fail-open band (G88).
- A dependent experiment needs authoritative state, but one command still needs cumulative presets
  to isolate historical stages (G89-G90).
- Re-requesting an already-on resource can be an expensive rebuild, not an idempotent assignment;
  compare requested state before transition calls (G91).
- A demand-driven traversal cannot bootstrap if it refuses to run until one of its own products
  exists.
- Visibility-independent residency does not imply visibility-independent demand. A frustum test
  placed before `RequestMesh` still makes camera direction the load authority.

## Flagged and unverified

**Judgement calls awaiting human review.** Provisional loading targets are first cached terrain
within five seconds and useful 360-degree coarse coverage within fifteen seconds on the current
5,317-section cache. The owner may tighten or relax these after the first correct bootstrap run.

**Claims lacking their evidence level.** No loading fix exists yet. Panoramic coarse-first/radial
refinement is a proposed policy, not a human-accepted implementation. The 0.3.91 preset transition
fix is built, harness-tested, packaged, and installed but has not run in game. The `late` preset's
visual classification is human-tested; its 362 FPS observation is contaminated by a 1,678-section
re-mesh and is not performance evidence.
