# Session 54 - Restore Phase 9 as the next renderer work

**Date:** `2026-08-25`
**Branch/commit:** `render-overhaul`, on top of `f13185a`; documentation changes uncommitted
**Mod version:** `0.4.0`, unchanged
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

After 0.4.0 was accepted, the next-work summary led with a corrected-mapping performance
re-measurement and a second-GPU/driver run. The owner retired those two items and asked whether the
GPU-driven terrain-renderer plan had actually been finished, specifically recalling Phase 9.

The plan, current status, TODO, and Session 53 were checked against one another. They agreed on the
underlying fact: 0.4.0 became the default on the owner's product decision before Phase 9's own
hardening gates were complete. The top-level TODO obscured that by presenting two evidence gaps as
the next work instead of presenting Phase 9 itself.

## Work narrative

### 1. Separate retirement from completion

The corrected-mapping suppression/FPS measurement was never run, and a second driver never ran the
fast path. Retiring those tasks removes them from scope; it does not manufacture their evidence.
Historical ridge figures therefore remain historical, and acceptance remains specific to the
primary AMD driver.

### 2. Reconstruct the remaining Phase 9 boundary

Phase 8's implemented cluster branch and correctness gate are closed. Its old sky guard and bias
diagnostic were already removed. The remaining renderer-plan boundary is Phase 9:

- paired packed/expanded route timing and the final regional-memory policy;
- primary-machine settings and lifecycle coverage;
- allocation, shader, buffer, and depth-copy failure fallback;
- representative forced-legacy coverage so the fallback remains viable.

The longer-tail settings matrix includes MSAA/SSAO changes, resize/fullscreen, shader reload,
dimension/world changes, long sessions, large caches, multiplayer, and competing-LOD-mod deferral.

---

## Delivered

- Retired the corrected-mapping suppression/FPS re-measurement without relabelling historical
  figures as current.
- Retired the second-GPU/driver requirement without making a portability claim.
- Restored Phase 9 hardening as the renderer plan's top priority in `dev/TODO.md` and `STATUS.md`.
- Updated Phases 7-9 of the renderer plan to distinguish passed, retired, and still-open gates.
- Recorded G99: shipping ahead of a validation gate does not complete that gate.
- No source, binary, package, version, protocol, blob, or schema change.

## Decisions

**Retire two evidence tasks, not Phase 9.** The two tasks are no longer project obligations. Their
absence remains a stated evidence limit.

**Do not fund another renderer optimisation before Phase 9 closes.** Aggregate subtree bounds
remains the highest-ranked candidate, but it comes after the existing renderer is hardened and its
packed-memory policy is settled.

## Traps

- **Shipping ahead of a gate does not erase the gate.** A product decision can accept risk and move
  a feature to default without turning unrun validation into completed validation. Record each
  missing item as passed, retired, or open.

## Flagged and unverified

**Judgement calls awaiting human review.** None for the two retired tasks. The exact practical
sequence for the remaining Phase 9 matrix is still to be chosen.

**Claims lacking their evidence level.** No current suppression/FPS effect size exists, and no
cross-driver evidence exists. The paired packed route and final process-memory reduction are still
unmeasured. The settings/lifecycle, injected-failure, and forced-legacy portions of Phase 9 remain
unexercised.
