# Session 49 — Phase 8 value proved; flicker attribution handed off

**Date:** `2026-08-24`
**Branch/commit:** `render-overhaul`, on top of `611fffb`; closing commit contains this record
**Mod version:** `0.3.96` through `0.3.100`; 0.3.100 packaged, verified, and installed but not human-run
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

Session 48 returned from the cache-loading detour to Phase 8's remaining correctness question. The
owner had a repeatable but extremely angle-sensitive terrain flicker and correctly retained all
in-game visual testing: a screenshot cannot reliably capture the failing frame, and leaving the
exact camera angle loses the symptom. The goal was to preserve the large cached-on-cached culling
win while identifying the state that incorrectly makes a real draw command appear and disappear.

The session progressed from a controlled stage boundary, through exact live-cull accounting, to an
armed identity-stable capture. It also rejected two attractive but unsupported diagnoses. The
visual artifact is not fixed at this checkpoint; version 0.3.100 is deliberately instrumentation
for the next capture.

## Work narrative

### 1. The split boundary and margin ladder

An adjacent settled comparison measured 386 FPS under both `.vhphase8 late` and
`.vhphase8 cull`. The precise-angle flicker occurred under `late` and disappeared under `cull`,
localizing it to the far cached bucket testing against freshly drawn near cached terrain. This did
not make the split or clusters valueless: the split is what lets near cached terrain occlude far
cached terrain, and clusters avoid whole-section bounds being refused because they overlap sky.

Version 0.3.96 added `.vhsplitbias 4|16|64|256`, a live, unsaved, far-bucket-only depth margin.
Every value behaved alike, and clusters exposed more affected spots. The margin ladder is rejected;
do not repeat it without evidence of a different threshold failure.

### 2. The reported 9.8% was the wrong population

The existing `.vhhzb` percentage described the shadow classifier's whole-section candidates, not
all terrain in memory and not the exact post-cull draw-command stream. It was therefore possible
for a ridge expected to hide much of the far terrain to report only 9.8% without proving that the
live cluster cull was broken.

Version 0.3.97 sampled the exact command buffer edited by the live compute pass, once per 30
dispatches per ordinary/split-near/split-far bucket. Fenced readback never waits. It records enabled
commands tested and zeroed, indices tested and removed, verdict causes, and distance bands. Its
denominator is the enabled command or index workload in sampled frames.

At the owner's end-of-map ridge view, FPS rose from roughly 340 to 460. Split-near removed 53.3% of
commands and 52.9% of indices; split-far removed 80.5% and 77.1%. The 4-8k far band reached
92.4%/94.8%, with no whole-section fallbacks or instrumentation warnings. This proves that the
split/cluster path works, suppresses a material amount of real geometry, and can produce a large
performance win. Phase 8 should be repaired, not discarded based on the old shadow percentage.

### 3. Stable command identities and the failed sky diagnosis

Version 0.3.98 added `.vhflicker on|off`. While explicitly armed, four asynchronous fenced slots
captured each split-far command's stable `(section key, cluster cell)` identity, verdict, nearest box
depth, farthest HZB depth, mip, texel rectangle, and view-projection drift. Ordinary frames remained
dormant. The first useful run collected 8,416 samples and 27,065,856 observations, dropped none,
and showed exactly zero matrix drift. The leading identity changed 4,021 times, exclusively between
`occluded` and `background`; background read the exact clear depth `1.0`.

Version 0.3.99 tested the resulting silhouette hypothesis with a one-texel perimeter refusal only
around the split-far cached-on-cached test. It also moved the full report to the log and removed an
angle-bracket transition label that Vintage Story parsed as chat markup. The owner then supplied
the decisive result: flicker was still bad, and most affected meshes were in the middle of visible
terrain rather than touching sky.

The 0.3.99 report contained 4,968 split-far samples, 16,357,879 observations, no dropped readbacks,
0.000604 maximum matrix delta, 179 commands changed, and 15,746 raw verdict transitions. Of these,
8,666 were real `occluded/background` cull changes and none were `occluded/visible`; 7,080 were
draw-safe changes. The four highest-ranked draw-safe offenders alone contributed 6,592
`visible/background` transitions. Ranking explanation changes rather than draw-state changes had
made the sky story look stronger than it was. The narrow guard remains in source but is a failed
hypothesis and must not be widened without new evidence.

### 4. Version 0.3.100 follows the real draw-state chain

The final diagnostic captures split-near and split-far independently with eight fenced slots. It
separates command-presence changes before compute from GPU culling-verdict changes, ranks only
transitions that switch drawing on/off, and retains harmless verdict changes only as summary
context. Each offender receives a coarse `left/centre/right` and `lower/middle/upper` projected
screen label. Stable identities come from the builder rather than inferred draw order, and the GL
state guard now preserves the additional SSBO bindings.

The capture remains view-wide. A crosshair-targeted prototype was started and then fully reverted:
the owner cannot point the crosshair at the mesh while retaining the precise camera angle that
causes it to flicker. A proposed missing texture barrier was also withdrawn before implementation.
The HZB reducer reads one explicitly clamped mip and writes a disjoint mip, so the same-texel
feedback condition that would justify that barrier has not been demonstrated.

### 5. Exact next run and interpretation

Restart with the highest installed version, apply `.vhphase8 clusters`, settle at any known flicker
angle, run `.vhflicker on`, hold the camera completely still for 3-5 seconds, then run
`.vhflicker off`. The crosshair position is irrelevant. Chat intentionally shows only the far
summary; inspect both complete `split near` and `split far` sections in the newest
`client-main.log`.

Interpret the result as follows:

- Far presence changes point upstream of compute, to command construction, traversal, or builder
  publication.
- Split-near presence or culling changes mean the near occluder input—and therefore the far HZB
  picture—is changing.
- Stable near input with far culling changes localizes the remaining fault to the far HZB source,
  projection, or depth classification.
- Screen-region mismatch means a reported offender is not the observed defect; improve attribution
  while staying view-wide rather than asking for crosshair targeting.

---

## Delivered

- Versions 0.3.96-0.3.100: live split margin ladder, exact command-stream cull telemetry, armed
  identity-stable flicker capture, narrow failed sky guard, and two-bucket draw-state attribution.
- `.vhhzb` semantics and denominator documented; exact ridge suppression and FPS result preserved.
- G82 expanded for exact-angle/view-wide diagnostics; G95 records verdict-state versus draw-state.
- Completed history, changelog, renderer plan, TODO, status, session index, and this handoff record.
- Warning-free Release build, 5,073 fast assertions, and 1,524 documentation checks pass at
  session close.
- Verified `dist/vintagehorizons_0.3.100.zip`, SHA-256
  `559A00D43D14F686E329D31AC796109AD96C72C7BC956E5020E23EDD7FBCB012`, and copied it to the owner's
  Mods folder without removing an older archive.
- Added the standing copy-only installation rule: never delete, move, replace, or back up an older
  Vintage Horizons Mods-folder zip. No game process was launched; no push, tag, or public release
  was made.

## Decisions

**Keep the split and clusters while correctness is localized.** The ridge result proves high real
command/index suppression and a roughly 340-to-460 FPS gain in the terrain-heavy view.

**Rank externally meaningful transitions.** `visible` and `background` both draw. They may remain
diagnostic context, but only command presence and culled/drawn transitions can explain flicker.

**Capture every upstream bucket and stay view-wide.** Split-near produces the depth picture used by
split-far. Both must be observed, and exact-angle defects cannot be made crosshair-dependent.

**Preserve rollback artifacts.** Installing a higher test version is an additive copy. Older mod
zips belong to the owner and are never removed as installation cleanup.

## Traps

- A shadow-classifier percentage is not the live command stream and not total terrain in memory.
- A verdict transition can be draw-safe; rank draw-state transitions and command presence (G95).
- The reported offender must occupy the same screen region as the visible defect (G82).
- A compelling sky/background correlation did not prove a sky-silhouette visual cause.
- Exact-angle capture cannot require crosshair alignment.
- Texture barriers address a specific feedback hazard; do not add one without proving that the same
  texels are read and written.
- Vintage Story chat parses angle brackets as markup (G67); keep full diagnostics in the log.

## Flagged and unverified

**Judgement calls awaiting human review.** Version 0.3.100 has not been run. The owner must judge the
flicker and identify whether the report's projected region matches what was visible.

**Claims lacking their evidence level.** The source of the remaining precise-angle flicker is
unknown. The sky guard is not accepted. Command-presence stability, split-near stability, and the
far HZB source/projection/classification alternatives await the two-bucket capture. No second-driver
cluster/packed result exists, and paired-route packed memory/GPU-cost gates remain open.
