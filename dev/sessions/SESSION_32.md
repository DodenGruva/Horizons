# Session 32 — Opaque terrain stops paying for faces and fragments nobody can see

**Date:** `2026-08-20`
**Branch/commit:** `codex/gpu-overdraw-culling` / `681abfbbbfb7284caa82034adb296fe0068ea150`
**Mod version:** `0.3.27`
**Assist protocol / blob / schema:** `1 / 4 / 6` (unchanged)

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The owner demoted the remaining extreme-flight issues after extensive ordinary play: the
reported coarseness and brief handoff overlap were not practical problems, and the speeds
that exposed them are unavailable in normal gameplay. They remain recorded, but ceased to
be the renderer priority.

The new observation was much larger and ordinary: frame rate rose from roughly 300 FPS to
750 FPS when looking down instead of across cached terrain, then measured about 150 FPS
while facing roughly 4,000 blocks of mountainous cached terrain and more than 300 FPS while
facing away. The owner asked whether the mod employed occlusion culling.

Source tracing answered precisely: the quadtree rejects off-frustum nodes, distance policy
rejects far sections, the handoff skips wholly vanilla-owned sections, and normal depth
testing rejects fragments after submission. Vintage Horizons had no own section occlusion,
hierarchical depth test, query result or conservative terrain horizon. The renderer was also
submitting opaque cached meshes two-sided and without a near-first guarantee.

## Work narrative

### 1. Back faces

The installed 1.22.7 client was decompiled read-only to verify its render state. The client
platform exposes ordinary GL culling and retains OpenGL's default counter-clockwise front
face, so the existing opaque render stage could reject back faces without a native call.

That could not be enabled safely at first. The mesher's top/bottom faces already had a
coherent convention, but the two east/west and two north/south directions shared winding
instead of pointing outward. All six solid directions were made outward counter-clockwise,
with regression checks that derive each triangle normal. Water and thin/cutout geometry
remain two-sided.

`.vhbackface on|off` and `VINTAGEHORIZONS_BACKFACE_CULLING` separated the change. The owner
measured the same view at 218 FPS off and 260 on: +19.3%, about 4.59 to 3.85 ms per frame,
or 0.74 ms saved. They checked cliffs, caves, overhangs, high and low views and reported no
visual difference at all. Back-face culling became the default in 0.3.27; `off` remains the
session-only fallback and environment value `0` pins it off.

### 2. Near depth first

The next independent experiment changed only opaque submission order. Selected opaque
sections enter one reusable list with their squared camera distance computed once, sort
nearest-first through a singleton comparer, and draw in that order. The list retains
capacity, so steady frames allocate nothing for the ordering. Water keeps its original
traversal order because sorting translucent geometry this way is not generally correct.

`.vhfront on|off` and `VINTAGEHORIZONS_FRONT_TO_BACK` isolated the path. The owner measured
149 FPS off and 173 on in the same view: +16.1%, about 6.71 to 5.78 ms, or 0.93 ms saved,
again with no visual change. Front-to-back opaque submission became the default in 0.3.27;
`off` and environment value `0` restore the old traversal order.

### 3. What remains

Both experiments establish avoidable GPU overdraw on this machine, but neither is true
occlusion. Hidden mountainous sections are still submitted; back-face culling removes only
oppositely oriented triangles, and near-first order helps only after fragments rasterize far
enough to reach the depth test. The roughly 150-to-over-300 direction swing leaves at least
3.33 ms of view-dependent frame time and makes conservative hidden-section rejection the
next renderer investigation.

The essential constraint is already settled: visibility must not own residency. A camera
turn must reveal existing GPU meshes immediately, without reload, upload or remesh churn,
and every uncertain occlusion result must fail toward drawing rather than a hole.

### 4. Verification and test builds

Six-direction mesher checks pin outward winding. Traversal checks pin nearest-first order,
stable ties, list reuse and water's unchanged path. Static checks cover all command wiring
and the three deliberate ownership-skip call sites. The full tier passes 1,464 assertions
with zero failures; Release builds with zero warnings or errors.

Experimental 0.3.24 and 0.3.26 packages were built and installed for the two A/B tests.
Source version 0.3.27 records the accepted defaults; it was not installed as part of the
documentation close because the owner asked only to document, commit and push.

---

## Delivered

**Source (0.3.27):** outward solid-face winding; default opaque back-face culling; reusable
front-to-back opaque draw order; `.vhbackface` and `.vhfront`; environment fallbacks; water
and thin/cutout geometry left two-sided.

**Tests:** six-direction winding coverage, four opaque-order/traversal checks, updated static
wiring guards; 1,464 assertions and a clean Release build.

**Playable tests:** 0.3.24 back-face package and 0.3.26 front-to-back package, both installed
with the prior mod archived in the user's Mods backup folder before replacement.

**Documentation:** CHANGELOG 0.3.27; G53; README commands; architecture and renderer plan;
TODO priority and low-priority demotions; regenerated STATUS; DONE and this session record.

## Decisions

- Back-face culling and front-to-back opaque submission are on by default after separate
  human A/B acceptance, with immediate commands and environment variables retained as
  fallbacks.
- All solid mesh faces use outward counter-clockwise winding. Water and thin/cutout surfaces
  stay two-sided.
- Front-to-back ordering is opaque-only, retains reusable storage and computes distance once
  per selected section. Water keeps traversal order.
- Extreme-flight coarseness and approach overlap stay documented at low priority; they are
  not deleted, but no longer displace ordinary-gameplay GPU work.
- Conservative cached-terrain occlusion is next. No particular implementation is approved
  until a prototype proves no holes, popping or turn-around churn.

## Traps

- **Trigger:** enabling back-face culling on an existing mesher. **Failure:** shared winding
  between opposing vertical directions silently removes half the world. **Safer action:**
  prove outward winding for all six faces before changing GL state. Promoted as G53.
- **Trigger:** treating frustum culling or the depth buffer as terrain occlusion. **Failure:**
  every in-frustum hidden section is still submitted and can shade before nearer depth
  exists. **Safer action:** name the rejection stage precisely and measure it independently.
- **Trigger:** sorting translucent water with opaque terrain. **Failure:** blend results become
  order-dependent and can change visually. **Safer action:** limit near-first order to opaque
  terrain unless a separate translucent design proves otherwise.

## Flagged and unverified

Judgement calls awaiting human review:

- Which conservative occlusion design, if any, preserves valleys, cliffs, cave mouths,
  overhangs and rapid turns well enough to ship.

Claims lacking their evidence level:

- Both FPS results are one-machine, one-view owner comparisons, not alternating controlled
  benchmarks. Their isolated toggles support causation but not portability or exact effect.
- No GPU timer separates shader, raster, bandwidth and driver cost; no second driver or
  machine has been tested.
- The 4,000-block mountainous observation proves a large view-dependent remainder, not that
  all of it is conservatively occludable.
- The current 0.3.27 accepted-default source has full automated verification but has not been
  separately packaged or launched after changing the experimental defaults to on.
