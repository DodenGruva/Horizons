# Session 5 — cached bounds and stable projection

**Date:** 2026-08-17
**Branch/commit:** `codex/main-thread-performance`, phase based on `d2b1eca5f980`
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## 1. Context and investigation

`STATUS.md` identified cached mesh bounds and stable far-plane projection as the next
implementation phase. Source tracing confirmed that every rendered frame enumerated all
opaque section meshes, measured every section centre relative to the moving camera, and
fed the exact resulting float to `Reset3DProjection`. The existing telemetry had counted
2–19 projection resets per active interval, but the teleport-driven route was not suitable
evidence for ordinary continuous movement.

The change stayed inside the render-thread ownership boundary. It did not alter quadtree
selection, visibility, mesh residency policy, shaders, `.vhfar` configuration, protocol,
blob format, database schema, or player-facing defaults.

## 2. Cached world-space mesh bounds

`LodMeshBounds` now maintains the horizontal world-space rectangle containing every
section key with an opaque or water mesh. A newly present key expands the rectangle in
constant time. Replacing one mesh with another for the same key does not perturb the
bounds. Every real removal path is centralized through `RemoveMeshes`; removing a key on
an outer edge marks the rectangle dirty, and the next far-distance query rebuilds it once
from the remaining opaque and water keys. Interior removals require no scan.

The steady frame now computes the farthest horizontal distance from the camera to the
cached rectangle's farthest corner. This is constant-time and conservatively covers the
full footprint of coarse LOD sections. The prior centre-distance plus fixed 96-block
allowance could understate a large coarse section's corner.

## 3. Quantized projection with hysteresis

The effective LOD edge remains a live camera-relative value and retains the existing
vanilla-view margin and `.vhfar` cap. Only the expensive camera projection is stabilized.
Its required distance rounds upward to a 512-block boundary. Growth applies immediately
so newly installed or newly distant geometry cannot clip. A lower boundary must remain the
candidate for five seconds before the projection shrinks. Returning to the current band or
moving into a different lower band cancels or restarts that cooldown.

World teardown clears the bounds and projection policy. `ApplyZFar` also continues to
repair the camera if another engine path lowers it below the already selected safe value.

## 4. Verification and playtest artifact

- The focused far-distance suite passed 30 assertions covering mixed LOD footprints,
  interior and extreme removal, water-only meshes, rebuild/clear, unlimited and capped
  `.vhfar` arithmetic, upward quantization, immediate growth, delayed shrink, cancellation,
  and cross-world reset.
- The complete Release fast tier passed 758 assertions across all 19 suites.
- Explicit Debug and Release builds succeeded against the installed Vintage Story 1.22.5
  assemblies with zero warnings and errors.
- `dev/DocCheck.ps1` passed 174 checks under both Windows PowerShell 5.1 and PowerShell 7.
- A local Release playtest package was built as
  `dist/vintagehorizons_0.2.1-playtest-far-plane.zip`. Its archive root contains the DLL,
  manifest, license, icon, and two shaders; it contains no PDB or game DLL. `dist/` remains
  ignored and the package is not a public release artifact.

No game process or moving-camera route was run. The O(1) steady-state calculation and
state transitions are source-traced and harness-tested; projection-reset frequency and
visual non-clipping are not yet established in game.

---

## Delivered

- Constant-time steady-frame far-distance calculation from cached world-space mesh bounds.
- Rare exact bounds rebuild after an extreme opaque or water mesh disappears.
- Safe 512-block projection bands with immediate growth and five-second shrink hysteresis.
- Preserved vanilla-distance margin, explicit `.vhfar` behavior, and world teardown reset.
- Thirty focused assertions and a 758-assertion full fast-tier pass.
- A verified, ignored local ZIP for human playtesting.
- Reconciled plan, TODO/DONE, architecture, gotcha, current status, and session history.

## Decisions

- Use one conservative horizontal rectangle rather than a more complex spatial index. It
  makes ordinary work independent of mesh count and cannot clip a section footprint; sparse
  diagonal coverage may temporarily request more far plane than its individual meshes need.
- Include water-only keys in bounds even though opaque terrain normally accompanies them.
  Resource dictionaries, not that ordinary-case assumption, define what can render.
- Quantize the applied projection but keep the shader's effective far edge continuous. The
  projection reset is the churn being removed; changing the fog/fade edge was not required.
- Use monotonic elapsed milliseconds for shrink cooldown rather than frame count, so a slow
  or paused renderer does not change the intended five-second policy.
- Leave the default far-distance cap unchanged pending benchmark and human evidence.

## Traps

- Treating mesh replacement as remove-then-add at the bounds layer marks an unchanged
  extreme dirty and creates unnecessary whole-set rebuilds. Compare key presence before and
  after replacement instead.
- Opaque and water GPU resources have separate dictionaries. A bounds tracker driven by only
  the opaque dictionary can forget a water-only key and understate the required far plane.
- Quantizing the raw effective edge would also move fog/fade behavior. Quantize only the
  camera projection unless a visual change is separately intended and tested.

## Flagged and unverified

- A continuous movement and camera-rotation run must compare projection-reset counts and
  inspect the horizon for clipping during movement, mesh arrival, eviction, and `.vhfar`
  changes.
- The 512-block step and five-second shrink cooldown are conservative initial policy values;
  playtesting may justify tuning them without changing the design.
- A conservative rectangle can overestimate required distance for sparse diagonal coverage;
  the runtime cost and depth-precision effect have not been measured on a very large cache.
- No changelog entry, compatibility bump, public release, push, or human playtest was made.
