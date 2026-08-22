# Session 40 — Vanilla lighting parity, and a re-mesh symptom that turned out to be warm-up

**Date:** `2026-08-22`
**Branch/commit:** `render-overhaul`, from `5324a97`
**Mod version:** `0.3.51` (human-tested) and `0.3.52` (built, partly untested)
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

Two open TODO items were taken in order. The first was the long-standing report that cached
terrain matches vanilla in good daylight and drifts further away through the day until
sunset and night look substantially different. The second was the recorded re-mesh
amplification: 64 genuinely changed sections against 5,057 mesh replacements.

Both were settled by offline evidence before any code was written, and in both cases the
evidence contradicted the plan that was already written down.

Out of scope: the Phase 3 fast shader, which the lighting work was explicitly gating; the
periodic-stutter validation; and the moving-route micro-hitch run.

## Work narrative

### 1. Lighting: what vanilla actually multiplies by

`ilspycmd` over `VintagestoryLib.dll` plus the game's own shader assets settled four
questions that prose could not.

`DefaultShaderUniforms.LightPosition3D`, whose behaviour the TODO called unverified and
named as the likely dominant term, is set in `SystemRenderSky.OnRenderFrame3D` as the sun
position lerped toward the moon by `clamp(50 * (moonLightStrength - sunLightStrength), 0, 1)`.
It tracks the sun exactly all day. **The leading theory was disproven**: it was never the
daytime term, and matters only at night.

Reducing vanilla's `applyLight` for the case a LOD section is always in - full sky light, no
baked block light, no glow - every scale factor cancels against the `bMax` renormalisation
except the contrast constant, leaving the pixel multiplied by `1.05 * ambientColor`. That
made the real comparison tractable: vanilla uses `Ambient.BlendedAmbientColor`, the mod used
`SunColor * DayLightStrength`.

Decoding the engine's own sunlight ramp (`textures/environment/sunlight.png`, row 0.01,
sampled exactly as `GameCalendar.getSunlightPixelRel` does) and evaluating both paths across
a day showed why that matters. `calcSunColor` builds the ambient from the calendar's
`ReflectColor`, which is explicitly floored at a blue `nightColor` of `(0, 0.063, 0.133)`
once the sun is down. `SunColor` has no such floor and stays on the orange end of the same
ramp. After sundown the two hues invert: vanilla reaches `(0.30, 0.34, 0.39)` blue-grey
where the mod reached `(0.54, 0.29, 0.04)` orange. That is the reported symptom, and the
cause is not saturation, which is what the TODO had guessed.

The same reading found a divergence the plan did not contain at all: vanilla scales every
terrain pixel by `1 + max(0, shadowIntensity * 2 - 1.66) / 1.5`, where `shadowIntensity` is
`DropShadowIntensity`, roughly `clamp(sunElevation * 2.5, 0, 1)`. That is 22.7% of extra
brightness held through the middle of the day and faded out as the sun sets, applied whether
or not the player has shadows enabled.

Four corrections shipped behind `.vhlight` switches, all defaulting on. `lightPosition` and
`shadowIntensity` needed no upload: the shader includes `fogandlight.fsh`, and the engine
pushes both into any program that does. Only the ambient colour needed new wiring.

The owner played 0.3.51 and reported the result substantially better, with nothing else
regressed. That is the acceptance evidence the corrections were shipped to obtain.

### 2. A fifth lighting divergence, deliberately unbundled

Every engine shader calls `getSkyColorAt` with `SkyDaylight`
(`1.25 * max(DayLightStrength - MoonLightStrength / 2, 0.05)`, attenuated above 1,000 blocks
over sea level), while the mod's far dissolve band passed plain `DayLightStrength` - about
20% too dim at dusk, 60% too bright on a moonlit night, identical in full daylight.
`SkyDaylight` is `internal`, so the renderer reconstructs it from public inputs. Held out of
0.3.51 so the owner's sweep stayed attributable to the terrain terms; shipped in 0.3.52.

Only the sky colour takes the corrected value. The glow clamp beside it is the mod's own
night dimming and its `0.05` constant is calibrated against `DayLightStrength`, which reaches
zero; `SkyDaylight` floors at 0.0625 and would leave a glow burning all night.

### 3. Change locality, and the arithmetic that did not survive contact

`MarkChanged` marked the changed section render-dirty and all four neighbours
unconditionally. `ReplaceColumns` already walked column by column and knew exactly which
moved; it simply did not report it. It now returns an edge mask, and `MarkChanged` refreshes
only those neighbours. Callers with no column-level answer keep the conservative behaviour
by saying nothing.

Before running anything, the geometry was checked and the plan's expectation was corrected:
a vanilla chunk is 32 blocks and a level-0 section is 64, so a capture patch fills exactly
one quadrant and a **first-time** capture must always report two edges. "One section instead
of five" is unreachable for streaming terrain; three instead of five is the ceiling there.

Three benchmark runs on the frozen `bodanboys` profile then measured it. The fan-out result
was identical in all three: **38-40 content changes produced 77-80 stale-mesh claims, 2.00 to
2.03 per change, against 5.00 before**, with all four neighbours resident every time, and no
frame-rate cost. Every change touched exactly one edge, which is consistent with a warm
profile producing no first-time captures.

The second run was misconfigured - default 240 MiB arena instead of the original
measurement's 1,792 MiB - so 1,407 sections never entered the mirror and its 251 replacements
meant nothing. The third run matched the original configuration.

**The symptom the TODO section was named for then failed to appear.** Splitting the third
run's 26 reporting intervals: warm-up (intervals 1-9, about 2.5 minutes) carried 1,827 mesh
uploads and 2,131 MiB, about 237 MiB per 15 seconds; steady state (10-26, about 4 minutes)
carried 54 uploads and 35 MiB, about 2.1 MiB per 15 seconds. The warm-up number reproduces
the original "204-289 MiB built, 76-119 MiB uploaded every 15 seconds" almost exactly.

The original measurement was taken during warm-up and recorded as continuous. The warm-up is
781 sections being meshed once as 3,291 cached sections load, which is work the renderer has
to do, not amplification. The `145 x 35 = 5,057` derivation was comparing a whole-run rebuild
total against a steady-state change count and matched by coincidence.

An intermediate claim made during this session - that the figure was an artefact of the
shadow mirror - was also wrong, and came from reading only the quiet tail intervals of a log
rather than the whole series. Both errors have the same shape: a number taken from one
period and described as belonging to another.

---

## Delivered

**0.3.51 (packaged, human-tested and accepted).** Four lighting corrections behind
`.vhlight moondir | ramp | ambient | boost | all`, all default on: shade by the engine's
light vector; vanilla's `max(0.45, 0.5 + 0.5 * dot)` ramp and floor; light by
`Ambient.BlendedAmbientColor * 1.05`; and vanilla's daylight brightening.

**0.3.52 (packaged, not yet human-tested).** `.vhlight sky`, reconstructing the engine's
`SkyDaylight` for the far dissolve band. Change locality: `LodSection.ReplaceColumns` returns
an edge mask, `LodMip.ApplyToParent` carries it, `LodWorld.MarkChanged` takes it with an
all-edges default, and the capture and mip paths supply it.

**Instrumentation.** `MarkChangedCalls`, `MarkChangedRenderDirtied` and
`MarkChangedNeighborsSkipped`, reported per interval as a `change locality:` line. Before
this, nothing counted `MarkChanged` at all, which is why the amplification could only be
inferred.

**Tests.** `ChangeLocalityChecks` pinning interior, one-edge, quadrant and unknown-edge
behaviour; `StaticAssetChecks.VanillaLightingWiring` holding the six lighting switch uniforms
and vanilla's transcribed constants across shader and renderer; column-edge mapping in
`SectionChecks`. 1,892 assertions, 1,453 doc checks.

**Documentation.** TODO rewritten for both items with measured tables; CHANGELOG 0.3.51 and
0.3.52 sections plus a correction of the withdrawn claim; STATUS regenerated.

## Decisions

**One switch per lighting correction, not one for all four.** The point of shipping switches
was that a single dusk sweep could attribute what it saw. `boost` in particular is the only
one that changes broad daylight, which is the case the owner reported as already correct, so
bundling it could have masked an improvement at dusk with a regression at noon.

**One `.vhlight <part> <on|off>` command rather than five chat commands.** The owner is not a
developer; one command that prints its own state is easier than remembering five names.

**`MarkChanged`'s edge parameter defaults to all edges.** A caller with no column-level
answer - a whole-section install, a palette repair - stays conservative by saying nothing.
The failure mode of the alternative is a missing rebuild, which is a visible seam, and
silence must not cause it.

**Kept the mod's own glow clamp on `DayLightStrength`** rather than moving it to the
corrected sky value. It is not a transcription of vanilla and its constant is calibrated
against a value that reaches zero.

**Reused the existing -X, +X, -Z, +Z bit order** from `LodGpuSectionFacts` rather than
inventing a second side convention, and it already matched `MarkChanged`'s neighbour loop
order.

**Held the sky fix out of 0.3.51.** Rejected bundling it: it would have made the owner's
attribution sweep ambiguous for the sake of one version number.

## Traps

**Trigger:** quoting a rate from a benchmark log. **Failure:** "204-289 MiB every 15 seconds,
continuously, at a standstill" was a warm-up measurement; steady state is 2.1 MiB. The same
mistake was then made in the opposite direction inside this session by reading only the tail
intervals and concluding the cost was a measurement artefact. **Safer action:** split the
interval series into warm-up and settled before quoting any per-interval figure, and state
which period a number belongs to. Promoted to G62.

**Trigger:** an arithmetic identity that "explains" a measurement. **Failure:**
`145 x 35 = 5,057` matched the observed replacement count and was recorded as the mechanism.
The actual `MarkChanged` count is 38-40; nothing counted it, so the middle term was invented.
**Safer action:** add the counter before quoting the factor. Promoted to G63.

**Trigger:** a benchmark re-run intended as a comparison. **Failure:** the confirming run used
the default 240 MiB arena instead of the original's 1,792 MiB, so 1,407 sections never entered
the mirror and the replacement count was meaningless. **Safer action:** copy every
configuration switch from the run being compared against, and check a coverage number before
reading a result.

**Trigger:** editing repository text files with a script. **Failure:** several files carry
mixed CRLF and LF, sometimes within one file, so multi-line pattern matching silently fails or
rewrites endings. **Safer action:** match and splice whole lines using the endings already
present; never normalise a whole file. Promoted to G64.

## Flagged and unverified

Judgement calls awaiting human review:

- All five `.vhlight` switches default on. Four are accepted; `sky` is not yet looked at.
- The change-locality narrowing is on unconditionally, with no switch. A missed rebuild would
  show as a stale section or a seam between distant sections.

Claims lacking the required evidence level:

- 0.3.52 is built and harness-tested only. The sky band and the absence of stale sections are
  both unverified in game.
- The one-edge-per-change result comes only from a frozen warm profile. A cold or moving route
  should show two edges and a smaller saving; not run.
- `dev/plans/PLAN_GPU_DRIVEN_TERRAIN_RENDERER.md` still carries the withdrawn claim that each
  mirror replacement "is a full re-upload in the established renderer too". Left uncorrected
  deliberately; that plan is mid-flight.
- The micro-hitch section now names the quadtree walk (95 us average, 393 us max) and the
  readiness shadow (27.8 us average, 710 us max) as the largest unexplained per-frame costs.
  That is a reading of one run's phase histogram, not an attribution.
