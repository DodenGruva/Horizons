# Session 63 — Horizon policy corrected before implementation

**Date:** `2026-08-27`
**Branch/commit:** `codex/fog-removal` at `c88154f`; documentation handoff uncommitted at record time
**Mod version:** `0.3.125`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

After 0.3.125 was visually accepted, the owner learned that it removes Vintage Story's default
clear-air haze in addition to the terrain-threshold radius. The owner clarified the intended
product boundary: ordinary vanilla atmosphere is intentional and must remain unchanged. Only the
pale circular wall at the vanilla terrain render threshold should be removed.

A third-party implementation had been inspected during a comparison, but the owner then imposed a
strict clean-room boundary on all follow-up work. No third-party code or technical implementation
details are retained in the implementation handoff. The next session must work only from the
owner's requirement, this repository, and a fresh independent examination of official Vintage
Story assets and assemblies.

## Work narrative

No source implementation changed. The correction was deliberately stopped at documentation so a
new session can begin from a product-level clean-room handoff rather than the comparison context.

`dev/plans/PLAN_TERRAIN_HORIZON_WALL_ONLY.md` records the complete allowed input: remove the ambient
hook and density arithmetic entirely, independently re-establish the terrain-only shader scope from
official engine evidence, preserve the existing renderer lifecycle, add independently designed
fail-open isolation, add no toggle, and require separate human checks for the missing terrain radius
and unchanged atmospheric haze.

---

## Delivered

- Reopened horizon-wall work as a corrective TODO for 0.3.126.
- Added a clean-room implementation and acceptance handoff containing no third-party technical
  details.
- Corrected current status: 0.3.125's pleasing clear-horizon result does not constitute acceptance
  of its ambient-haze change.
- Added G118 so future work does not conflate atmospheric fog with a terrain-edge fade.
- Changed no production source, playable artifact, protocol, blob format, or database schema.

## Decisions

- Preserve all vanilla atmospheric haze exactly; do not subtract, zero, tune, or replace any part
  of the ambient fog result.
- Remove only the visible terrain-threshold radius.
- Add no runtime command, configuration, environment variable, or toggle.
- Begin implementation in a new session under the clean-room boundary in
  `dev/plans/PLAN_TERRAIN_HORIZON_WALL_ONLY.md`.
- Preserve 0.3.125 history as an accurate description of that artifact rather than rewriting it.

## Traps

- "Fog wall" is ambiguous. A render-edge geometry fade and ordinary atmospheric haze can both look
  pale at distance, but they are different product behaviors. Removing the latter because the task
  used the word "fog" violates the intended atmosphere. Promoted as G118.
- A pleasing overall screenshot does not establish acceptance of an undisclosed component change.
  Once the ambient change became known, it required a separate product decision.

## Flagged and unverified

- No corrected source or 0.3.126 artifact exists yet.
- The exact official terrain-pass scope and safe replacement distance must be re-derived in the new
  session without third-party implementation input.
- Clear daytime haze, weather, underwater, lava, local fog, shader reload, deferral, and unload have
  not been human-tested under the corrected policy.
- No commit, push, tag, game launch, package change, or Mods-folder change occurred in this session.
