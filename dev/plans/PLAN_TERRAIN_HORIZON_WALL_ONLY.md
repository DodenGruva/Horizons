# Plan — Remove only the vanilla terrain horizon wall

**Status:** Completed in 0.3.127 and human-accepted
**Date:** 2026-08-27
**Starting version:** 0.3.125
**Planned next playable version:** 0.3.126
**Final accepted version:** 0.3.127 (0.3.126 was an unplayed intermediate package)

## Clean-room boundary

This document is the complete handoff for the next implementation session.

- Do not inspect, reopen, quote, copy, translate, adapt, or otherwise use source code or technical
  implementation details from any third-party fog-removal mod.
- Do not use the prior comparison conversation as implementation input.
- Work only from this product requirement, the Vintage Horizons repository, and an independent
  examination of the installed official Vintage Story assemblies and shader assets.
- Any implementation must be newly reasoned and written for Vintage Horizons. General awareness
  that a terrain-edge fade can be suppressed is allowed; third-party mechanisms are not.

## Product requirement

Vintage Story's ordinary atmospheric haze is intentional and must remain exactly vanilla. This
includes clear-air haze and every weather, altitude, underwater, lava, cloud, local, server, and
other environmental fog contribution.

Remove only the visible pale circular wall at the vanilla terrain render threshold. The purpose is
to stop that terrain-edge transition from drawing a conspicuous radius over Vintage Horizons'
extended terrain. Do not broaden the feature into general fog removal.

There will be no runtime command, configuration option, or toggle. The correction is active only
while the Vintage Horizons terrain renderer is active and must restore vanilla behavior when the
renderer is deferred, unavailable, or disposed.

## Known defect in 0.3.125

Version 0.3.125 changes the engine's ambient fog result by subtracting its default clear-air
contribution. The owner explicitly rejects that behavior. The next implementation must remove the
ambient hook and all ambient-density arithmetic entirely, not retune or replace it.

Version 0.3.125 may also affect distance fades outside terrain rendering. The next session must
independently enumerate the official shader passes that create the visible terrain-threshold wall
and restrict the policy to those terrain passes. General objects, instanced content, and entities
must retain vanilla distance behavior unless independent official-engine evidence proves that a
specific pass is part of the terrain wall.

## Implementation constraints

1. Do not write to ambient fog density, base fog, blended fog, fog modifiers, or environmental fog
   uniforms.
2. Do not change the saved view-distance setting, chunk loading, terrain culling, entity culling,
   `viewDistanceLod0`, or Vintage Horizons' far-terrain selection.
3. Identify the terrain-edge fade solely from official Vintage Story 1.22.x assets and assemblies.
4. Keep the patch surface narrow and off general high-frequency dispatch paths where an official,
   lower-frequency seam is available.
5. Every missing target, invalid value, shader variant, reload, or callback failure must preserve
   vanilla behavior and must not destabilize rendering. Diagnose a failure once rather than
   repeatedly disrupting frames.
6. Install only after Vintage Horizons passes competing-LOD deferral. Make behavior inert before
   structural teardown and remove only Vintage Horizons' own hooks.
7. Add no player command, saved setting, environment variable, or runtime toggle.

## Required source changes

- Remove the ambient-manager patch from `VanillaHorizonEffects`.
- Remove the ambient-density policy and its tests from `VanillaHorizonPolicy` and
  `HorizonEffectsChecks`.
- Independently re-audit official terrain shaders and replace the current broad pass allowlist with
  a terrain-wall-only allowlist justified in the new session record.
- Independently choose the smallest safe replacement distance that places the terrain fade outside
  the engine's actual drawable terrain boundary. Do not inherit a multiplier from any third-party
  implementation.
- Preserve the existing Vintage Horizons renderer ownership, deferral, reload, and disposal
  lifecycle where it remains applicable.
- Add independently designed per-pass fail-open handling so one incompatible shader cannot affect
  other passes or propagate an exception through rendering.

## Automated acceptance

- No production horizon-effects source references `AmbientManager`, `AmbientModifier`,
  `BlendedFogDensity`, or fog-density mutation.
- Tests prove the hook set contains no ambient update patch.
- Tests prove the allowlist contains only independently established terrain-wall passes and rejects
  general, instanced, entity, Vintage Horizons, sky, and unknown passes.
- Tests prove invalid distance inputs preserve the original value and that a failing or missing
  shader pass cannot affect another pass.
- Tests install and remove the actual hooks against the installed official game assembly without an
  OpenGL context.
- Lifecycle/static checks pin install after competing-LOD deferral, inert-before-unpatch teardown,
  exact hook ownership, and the absence of a general uniform-dispatch patch.
- Warning-free Release and Debug builds, the full fast tier, `dev/DocCheck.ps1`, packaging checks,
  and source/installed archive hash equality pass.

## Human acceptance

Use a new 0.3.126 artifact and verify both parts separately:

1. At the vanilla terrain threshold, the pale circular wall is absent and Vintage Horizons terrain
   remains visible through the boundary.
2. Away from that boundary, vanilla atmospheric haze is visually unchanged. Check clear daytime
   air first, then at least one visibly hazy or weather-affected condition. Underwater/lava/local
   fog may remain source-and-harness coverage unless the owner elects to test them, but the source
   must have no path that modifies their shared ambient result.

The 0.3.125 report that the scene "looked great" establishes only that its clear-horizon picture was
pleasing. It does not authorize the ambient-haze change, which the owner rejected after learning it
was present.

**Completion:** 0.3.127 removes the ambient hook and arithmetic, limits the policy to the five
official terrain programs, and was accepted by the owner for both the missing terrain radius and
visually unchanged vanilla atmosphere. Session 64 records the implementation and evidence.

## Documentation closeout for the implementation session

- Record the clean-room official-engine investigation without mentioning third-party mechanisms.
- Correct current-state documentation so the feature is described as terrain-wall suppression, not
  fog or clear-air-haze removal.
- Preserve 0.3.125 history as what that version actually did, while recording 0.3.126 as the product
  correction.
- Move this reopened TODO item back to completion history only after the owner accepts both the
  missing terrain radius and unchanged atmospheric haze.
