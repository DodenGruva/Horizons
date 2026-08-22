# Session 37 - GPU-driven terrain renderer planning

**Date:** `2026-08-21`
**Branch/commit:** `master` at base commit `0c1f28a1600bce3ac16fecefdc1782e2d74ae032` (working tree changes uncommitted)
**Mod version:** `0.3.40` source metadata; the next playable artifact must be `0.3.41`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

The owner asked which occlusion techniques the project currently uses, which absent
techniques fit it, and which long-term combination would yield the best performance. Source
tracing established that the current renderer already combines quadtree/frustum rejection,
distance LOD, whole-vanilla ownership skip, opaque back-face culling, front-to-back depth,
post-vanilla ordinary depth rejection, and delayed exact-geometry queries. The unresolved
long-term scaling boundary is the per-section resource/submission model rather than the
absence of one more isolated culling toggle.

The investigation read the canonical architecture, renderer sections of the main-thread
performance plan, relevant G2-G10 constraints, current TODO/status evidence, the expanded
mesh-result and upload path, the GLSL 330 shaders, and the original optional GL 4.3 fast-path
direction. No game process was launched, and no runtime capability or performance claim was
made.

## Work narrative

### 1. The target became one coordinated optional fast path

The proposed destination is a regional GPU renderer rather than a stack of competing
visibility systems. CPU-approved, hole-free candidates enter regional buffers; a copied
vanilla depth buffer becomes a conservative HZB; compute culling writes indirect opaque draw
commands; and one or a few multi-draw calls submit the survivors. The current GL 3.3 path
remains complete and keeps its delayed exact queries as the fallback, rather than running
queries and HZB simultaneously.

### 2. Risk was split into independently measurable phases

`dev/plans/PLAN_GPU_DRIVEN_TERRAIN_RENDERER.md` records the proposed architecture, data and
resource identity, compatibility tiers, HZB depth-source questions, cached-on-cached
alternatives, packed-quad study, water boundary, failure behavior, telemetry, verification
matrix, risk register, and ten implementation phases.

The order proves expanded regional buffers and indirect drawing before changing geometry;
proves HZB classification in shadow mode before it can hide terrain; and leaves CPU
parent/child coverage authoritative until measurements justify GPU LOD. Packed quads and
cluster subdivision are later optimizations rather than prerequisites.

### 3. Canonical routing now points to the proposal

The TODO renderer-scaling section remains the open-work authority and now links the detailed
proposal while stating that no implementation phase is approved. Status, the earlier
main-thread plan, the original design's optional fast-path note, and the Tier 0 working
agreement route future GPU-renderer work to the same document. Architecture was not changed:
the proposal is not yet a settled invariant.

---

## Delivered

- A detailed proposed GPU-driven cached-terrain renderer plan with phase-specific gates.
- Explicit preservation of the current GL 3.3 renderer and no-holes CPU coverage authority.
- A staged regional-buffer, indirect-draw, HZB, cached-on-cached, and packed-quad sequence.
- Performance decision rules, GPU/CPU telemetry requirements, correctness matrix, fallback
  behavior, risks, and proposed code boundaries.
- Cross-references from the open-work and routing documents most likely to be read before
  future renderer implementation.
- Documentation-only verification through `dev/DocCheck.ps1`.

## Decisions

- The plan is proposed, not approved implementation. Its first possible milestone is
  measurement and capability probing with no visual change.
- The current renderer remains the correctness and compatibility baseline.
- Regional multi-draw and HZB are independently gated; either may be rejected without
  blocking the other.
- The first fast path keeps CPU LOD/no-holes candidate authority and regionalizes opaque
  terrain only. GPU LOD, packed quads, clusters, and water batching come later if measured.
- HZB replaces per-section temporal-query suppression only while the fast path is active;
  the two are alternatives, not additive defaults.

## Traps

- A detailed future plan is not current architecture or shipped behavior. References must
  preserve its proposed status.
- HZB alone does not remove per-section CPU/driver submission. Regional buffers and indirect
  commands are the foundational scaling change.
- Rewriting buffer ownership, geometry format, visibility, and LOD selection in one patch
  would make both correctness and performance regressions unattributable.

No new proven implementation trap was added to `dev/GOTCHAS.md`; these remain planning risks
until a prototype or runtime test establishes them.

## Flagged and unverified

- The active framebuffer depth-copy path, MSAA behavior, depth convention, GL limits, and
  required entry points have not been runtime-probed.
- Remaining CPU submission, GPU raster, shader, bandwidth, and query costs are not yet
  separated by GPU timers.
- Regional allocation, indirect draw, HZB, packed geometry, GPU LOD, and cluster benefits are
  hypotheses with explicit go/no-go gates, not performance claims.
- No visual behavior changed and no human playtest was required or performed.
