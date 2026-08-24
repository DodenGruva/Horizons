# Session 45 — Phase 6, and the four instruments that failed before the feature did

**Date:** `2026-08-24`
**Branch/commit:** `render-overhaul`, on top of `c136c08`
**Mod version:** `0.3.83` packaged, installed and human-played
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

Phase 6 of `dev/plans/PLAN_GPU_DRIVEN_TERRAIN_RENDERER.md` asks whether cached terrain should be
able to hide other cached terrain. Phase 5 had shipped a working depth cull that could not pay for
itself, and the plan's own diagnosis was that the depth picture is taken after vanilla terrain and
before any cached section, so the only occluder is whatever vanilla draws inside its own view
distance — a bubble of a couple of hundred blocks, with every large cached hill beyond it
invisible to the test.

The plan offers four approaches and ranks them. Option 2, a near/far split with a second picture
taken mid-frame, is the one it says to prototype. Option 3, reusing the previous frame's finished
depth, is ranked third with the note "use only if the extra temporal complexity beats option 2".

## Work narrative

### 1. The number the phase was justified by was not a comparison

`dev/TODO.md` argued for Phase 6 with "extending the occluder from 64 to 512 blocks takes the
hidden share from 38.2% to 61.1%". Those two percentages are counted over different populations:
raising the occluder radius removes every section inside it from the tested set, and those are the
near, easy-to-hide ones. Most of the jump is the population changing, not the occluder working
harder.

Comparing the same distance bands across radii — same sections, different occluder — is the honest
version. Over the owner's real cache at his 192-block view distance, 64 views, the gain is 4 to 6
percentage points, not 23. Composed into whole-frame terms, a near/far split removes about an
eighth of what is still drawn, and the split radius barely matters between 384 and 768 blocks.

### 2. Two harness modes, and a fixture that failed usefully

`HzbField` gained `--occlude-all`, which rasterises every drawn section and tests every drawn
section, modelling a pyramid built from the previous frame's finished depth. On the three bands
whose populations are identical it beat the best split (+8.8 / +8.7 / +15.2 points against
+5.9 / +5.4 / +8.8), and it also improves the 0–1k band, which no split can reach by construction.

The mode rests on one claim: a section tested against a picture containing itself cannot hide
itself, because the pyramid reduces with `max` and the comparison is strictly greater, so the
coplanar case is a tie and a tie draws. Written into a fixture, that claim **failed** at sixteen
texels per axis — the two sides are computed differently (a box corner in doubles against a
rasterised depth) and at a fine enough level no background is pooled into the texel to separate
them, so the tie breaks the wrong way. A meter added to the harness put the real frequency at 0
hides at a 1e-7 margin and 4 of 1,183 at 1e-5, with both radius modes reading zero as a control.

### 3. The world-size correction

The owner pointed out that his cache is not what a normal world becomes: the far bands are starved,
and a grown world holds roughly as many sections in the 8–16 km ring as in the 1–2 km one.
Re-weighting the measured per-band rates onto a fully-grown mix moved the prize from about 8% fewer
sections drawn to 23% at a 16 km draw distance and 36% at 64 km. His correction changed the size of
the answer by a factor of three, and invalidated the structural argument — "the near bucket is
three quarters of the work and a split cannot touch it" — that had been the session's main
objection to the split.

### 4. The choice, and the confidence that was withdrawn twice

Option 3 was recommended on the numbers. Asked directly whether it was really better, the
recommendation was withdrawn: the offline figure had been taken with a **still camera**, which is
the exact assumption the plan's ranking was about. A motion mode was added, along with a
stale-versus-fresh verdict comparison whose floor is exactly zero by construction. It read 0 at
rest, 1.1% of hides at walking pace, and 2.6% under a hard turn. Coverage, not correctness, was
what motion appeared to cost: the per-band hiding rates barely moved, and only the count of
sections that were off screen last frame grew. The owner chose option 3 on that basis.

The first version of that safety figure was itself quoted over the wrong population — only the
sections the narrow test had failed to hide ever reached the comparison, which is the same
population-mismatch mistake this session opened by correcting. It was re-measured over everything
tested.

### 5. Built

`.vhlate` moves the depth picture from before the cached draw to between the opaque and water
passes, and the next frame culls against it. The picture must exclude water: water is drawn blended
but still writes depth, and a picture taken after it would judge terrain hidden behind water you
can see straight through. Boxes are built against the current camera and the old view-projection is
re-based by the camera delta, so neither the boxes nor the shader need to know any of this
happened. Guards refuse the whole picture — drawing everything — when there is no picture yet, the
projection changed, the camera jumped more than two blocks, terrain was rebuilt or evicted under
it, or the view is turning.

GPU timing was made available in an ordinary session, because the phase's gate is GPU time and the
timers previously armed only under the benchmark harness, so a normal run printed `0.0us` — which
reads as "free" rather than "unmeasured".

### 6. Four instruments failed before the feature did

Every one was found by an owner playtest, and every one failed safe.

**The picture never built.** Inserted between two draw passes, the build asked the engine to
activate a second shader while `lodterrain` was in use, and the engine refuses: 0 of 4,047 builds
completed across an entire session. The refusal counter said so plainly rather than leaving it to
be inferred from behaviour.

**The classify pass was throwing away 99.9% of what it paid for.** It checked its fence once, one
frame after dispatch, and *deleted* the fence when the answer was "not yet" — 2 reads out of 2,212
dispatches, at 149us a frame against 24.5us for the pyramid it reports on. It now keeps the fence
and never dispatches while a result is outstanding; the cost fell to about 1us.

**The cull shader was created behind the picture check**, so a session with no picture never
attempted it, and the status line said "the cull shader has not been created" — which reads like a
driver refusing it. That line was printed once, one second before the first picture existed, and
never again, leaving no evidence either way for a whole run.

**The classifier ran after the rebuild that overwrites the picture it describes**, so it correctly
refused a picture stamped with the current frame and reported nothing across 3,298 frames in which
culling was demonstrably working.

**And the GPU timer counted frames where the build did not run**, diluting a genuine 24us to 4.8us
over 256,368 "timed builds" against 50,733 real ones. That is the number the phase is gated on.

### 7. The ceiling was the bottleneck, not the feature

The arena reported `322 failures (322 ceiling)` and 35% coverage: only 41 of 116 drawn sections
were in the batched path, so batching and culling could not affect two thirds of the screen, and no
measurement taken through it meant much. The ceiling is now derived from the player's own
cached-terrain draw distance through the level scheme's own ring maths, times a measured
per-section cost, times a residency margin the owner set at two. Coverage went to 100% with zero
failures, and the owner reported **300 FPS to 480 FPS** at the same spot.

The first version of that model was "validated" against 580 sections resident in his world and
declared close. It could not have been: that world is a partly-explored corridor, so agreeing with
a *sparse* world's residency means the model already falls short of a populated one. The owner
caught it and set the margin.

### 8. The measurement that decided the phase

Three viewpoints. A saturated valley view could not discriminate, because the old arrangement
already hid 98–100% past 1 km. A bird's-eye view from extreme altitude returned 0% both ways, which
is the correct answer — from above nothing is behind anything — and is the best negative control of
the session: 27,000 sections, no legitimate occlusion available, and not one suppressed.

The third was an ordinary hilltop with an open view:

| | 0–1k | 1–2k | 2–4k |
|---|---|---|---|
| picture taken early (today) | 0% | 0% | 0% |
| picture taken at end of frame | 8% | 20% | 35% |

Pyramid cost 22.1us against 25.4us — the same. The old arrangement finds no occluders at all from a
normal long view, because vanilla's terrain is behind and below the camera.

### 9. And then the owner looked at it

Standing still and swinging the camera, a few pieces of distant terrain flickered, and at some
angles disappeared and stayed gone. A turning guard was shipped on the theory that the error is
concentrated at the screen edges, where the projection clamps a box's rectangle to the visible
sliver and applies that verdict to the whole section.

The owner had observed the failures **in the middle of the screen**, and said so once the
assumption was stated out loud. It was never asked. That disposes of the edge theory, and with it
the case for keeping a one-frame-old picture: if staleness is wrong mid-screen, the errors are not
a boundary artefact and no edge band contains them.

---

## Delivered

**Source — 0.3.72 through 0.3.83, each packaged and installed; 0.3.83 human-played.**

- `.vhlate`: the depth picture taken between the opaque and water passes and read by the next
  frame, so cached terrain can hide cached terrain. Off by default, saved per install.
- `LodHzbProjection.RebaseForCameraDelta`: folds the camera delta into the old view-projection so
  current-frame boxes can be tested against an earlier picture without moving the boxes.
- `LodStaleDepthPolicy`: the pure accept/refuse decision, with five distinct refusal reasons and a
  deliberate reporting order.
- `LodRenderPathCoordinator.GeometryRevision`: bumped when drawable geometry is replaced, removed
  or cleared, and deliberately *not* when it arrives.
- GPU timing armed on demand rather than only under the benchmark harness, and discarded for a pass
  that did not run.
- Classify pass: fence retained until signalled, no dispatch while a result is outstanding, and a
  30-frame floor between dispatches — 149us to about 1us.
- Cull shader created ahead of the picture checks; its status line now distinguishes a driver
  refusal from "no frame has been drawn yet".
- Arena ceiling derived from the configured draw distance instead of a fixed 256 MiB.

**Tests.** 3,696 assertions pass, from 3,034 at session start. New coverage: the matrix re-base
identity, the stale-depth policy including the ordering of its refusal reasons, the geometry
revision's arrivals-versus-losses distinction, discarded GPU timing, the distance-derived ceiling,
and the self-occlusion property in both directions.

**Harness.** `HzbField` gained `--occlude-all`, `--motion b,d`, a self-occlusion meter, and a
stale-versus-fresh safety comparison with a zero floor.

## Decisions

**Phase 6 will use the near/far split (option 2), not previous-frame depth (option 3).** Reversed
at the end of the session on the owner's observation. Option 3 was chosen on measurement and its
cost claim held up — the same pyramid time, no second picture, no mid-frame stall — but its
verdicts are a frame old, and mid-screen flicker means that staleness is not a boundary artefact a
guard can contain. The split's verdicts are same-frame by construction, which is a structural
advantage that was under-weighted throughout this session. The pyramid, cull shader, classifier,
guards and telemetry are all shared, so most of the work built here carries over.

**The arena ceiling follows the player's setting, with the player as the release valve.** Someone
who asks for terrain to the horizon gets a pool that can hold it; someone whose card cannot afford
that turns the distance down, which is a control they already have and understand. Safe to be
generous because the ceiling is a cap and pages are committed only as sections arrive — 2,588 MiB
configured, about 692 MiB actually committed.

**The residency margin is two, and it is a judgement call rather than a measurement.** Recorded as
such in the code so nobody later reads it as derived.

## Traps

Promoted to `dev/GOTCHAS.md` as G78–G83.

## Flagged and unverified

**Judgement calls awaiting human review.**

- The turning guard shipped in 0.3.83 is built on the edge theory the owner has since disproved. It
  should be removed with the switch to the split rather than tuned.
- The 30-frame floor between classify dispatches is chosen, not measured.

**Claims lacking their evidence level.**

- The mid-screen flicker has one cause proposed and none established. The split removes staleness
  entirely, so it may never need diagnosing — but it is not explained, and that should be said
  rather than assumed away.
- The 300-to-480 FPS gain is attributed mainly to arena coverage. Several changes landed together
  and no isolating run was made.
- Every offline percentage carries the harness's own approximations: vanilla's near terrain is
  modelled by cached L0 sections, and every section that exists is loaded, so none is walled to
  bedrock by a missing neighbour.
- The re-weighting onto a fully-grown world is a model. No cache on this machine reaches those far
  bands; the most developed one covers 16,384 x 12,288 blocks at L6.
