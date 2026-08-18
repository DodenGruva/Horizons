# Session 18 — visibility-aware traversal and controlled runtime proof

**Date:** `2026-08-18`
**Branch/commit:** `codex/main-thread-performance`, working tree based on `6550370`
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> This session changes client renderer traversal and mesh-residency policy, adds focused
> checks and telemetry, and preserves controlled benchmark evidence. It does not change
> assist protocol, stored blob format, database schema compatibility, or server behavior.

## Context and investigation

After the larger owning-thread pipeline spikes were reduced, the remaining renderer walk
still descended the resident quadtree before draw-time frustum rejection. That meant
off-screen terrain could still participate in draw selection, child coverage checks, and
mesh demand. Reusing draw selection as the eviction signal was unsafe because a camera
turn could evict the hidden hierarchy and cause a remesh storm when the player looked
back.

The work therefore had two distinct requirements: reject conservative off-screen parent
bounds before traversal, and keep residency independent of visibility. Runtime acceptance
also required the same cache on both sides; the persistent benchmark sandbox had evolved
enough that unrelated historical frame results were not causal evidence.

## Work narrative

1. Added a pure traversal policy for conservative world-height frustum bounds and a
   distance-based residency band with a one-detail-level grace margin.
2. Moved the renderer frustum update ahead of quadtree collection, rejected invisible
   parent nodes before descent, and made coverage refinement wait only on visible child
   slots. Invisible subtrees no longer select draws or request meshes.
3. Replaced visibility-derived eviction stamps with independent distance/age residency.
   Fresh uploads receive age grace, while nearby meshes stay warm regardless of camera
   direction.
4. Added a separate traversal-rejection counter to renderer telemetry and seven focused
   frustum/residency checks.
5. Ran a production-only warm route with one warm-up and two measured laps against a
   601-section client cache. Full turns at four waypoints retained 543 meshes with zero
   evictions, no reported 25 ms Vintage Horizons tick, and clean final convergence.
6. Archived that exact prelaunch cache, temporarily disabled only early subtree rejection
   and visible-child filtering, and ran one controlled `off` lap. Production source and
   the same byte-identical cache were then restored before the controlled `on` lap.
7. Across 14 matched telemetry intervals per side, mean selected nodes fell from 360.1 to
   128.9 (-64.2%), weighted mean traversal time fell from 66.0 to 52.9 microseconds
   (-19.8%), and weighted mean draw-submission time fell from 107.8 to 97.8 microseconds
   (-9.3%). Both sides retained 543 meshes with zero evictions and zero reported 25 ms
   ticks.
8. Mean waypoint average FPS changed from 463.4 to 459.4, while mean 1% low changed from
   292.4 to 292.3. The one ordered pair at roughly 400–480 uncapped FPS therefore supports
   the phase-work reduction but no aggregate FPS improvement claim.
9. Preserved frame summaries, semantic scenario proofs, matched renderer telemetry, the
   method, and limitations under `bench/results/2026-08-18-visibility-traversal`.

---

## Delivered

- Early conservative subtree rejection and visible-child-only coverage refinement.
- Camera-independent distance/age mesh residency that stays stable through full turns.
- Traversal telemetry plus seven focused policy checks; the full game-backed fast tier
  passes 975 assertions across 23 suites.
- A byte-identical-cache controlled comparison establishing lower selection, traversal,
  and draw-submission work without a turn-around eviction storm.
- 258 documentation checks under both PowerShell 7 and Windows PowerShell 5.1 after
  final session harvesting.

## Decisions

- Treat frustum visibility as a traversal/draw/request signal only. Residency remains a
  distance-and-age decision, preserving Gotcha G8.
- Frustum-test conservative node bounds using the same projection and camera matrices
  handed to the shader after the effective far plane is applied.
- Use the controlled phase metrics as the causal result. Do not promote a noisy single
  high-FPS pair into an aggregate FPS claim.
- Continue to dirty-set scheduling and time/byte-bounded uploads while retaining a later
  thousands-section traversal run and human motion review as verification debt.

## Traps

- A persistent cache can evolve between benchmark runs. Without restoring the exact
  prelaunch database, selected-node and frame differences cannot be assigned to traversal.
- Disabling the new residency policy in the baseline would mix traversal savings with
  eviction/remeshing behavior. The controlled baseline removed only the two visibility
  gates.
- Static screenshots can reveal obvious endpoint holes but cannot establish clipping or
  turn-around behavior during motion.

## Flagged and unverified

- The controlled cache held 601 sections, not the eventual thousands-section scale
  target, and used one machine with separate dedicated client/server processes.
- No person was available to watch this route in motion. The screenshots showed no
  obvious new camera-edge holes, but subjective clipping and turn-around quality remain
  human verification debt.
- Dirty-set scheduling still scans whole collections, GPU upload remains item-count-only,
  and shader/fill cost remains separate later work.
