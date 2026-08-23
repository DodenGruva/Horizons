# Session 42 — Phase 3b shipped, Phase 4 built and measured, gate not closed

**Date:** `2026-08-22`
**Branch/commit:** `render-overhaul`
**Mod version:** `0.3.65` packaged; `0.3.58` is the last build the owner played through
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

Opened on the render plan's Phase 3b. Closed with Phase 4 substantially built, its cost gate
passed, its correctness gate open, and its central assumption — that a whole section is a
testable unit — contradicted by measurement.

Evidence used, in order of weight: the owner's own client logs from five playtests, two
controlled benchmark pairs on the frozen BodanBoys profile, 2,358 offline assertions, and
the engine's own behaviour where the mod trusts it.

## Work narrative

### 1. Phase 3b: sections learn how tall they are

The renderer culled every section with a box spanning bedrock to sky, because nothing
recorded the vertical extent of the terrain inside. `LodMesher` already computes every Y it
emits, so the bounds are a running min/max over work it does anyway — free, and describing
what is DRAWN rather than what is stored.

Shipped in 0.3.58, per pass, because opaque and water are submitted separately and a lake
surface sits nowhere near the lakebed. `AddVert` is the single funnel every vertex passes
through, so no emission path can forget to contribute. Unset bounds fall back to the
full-height box, so a missing measurement can only ever draw too much.

The owner played it and reported nothing vanished looking up or down. The established
renderer's share is real but small: about 4 section-draws per frame rejected that the old box
would have kept, worst frame 26, against 272 selected sections.

### 2. The distribution, and a wrong conclusion drawn from it

0.3.58 reports the height distribution, which was the phase's own gate. The owner's first
reading: mean 146 blocks in a 256-block world, and **not one section under 64 blocks tall**.

This session concluded from that that Phase 4 should be held. **The owner vetoed it and was
right.** The error was judging a screen-space question with a world-space statistic: apparent
height is world height over distance, so the same 146-block section is about 258 px tall at
500 blocks and 4 px at 32,000, while the section count scales as area. The value of depth
rejection grows with exactly the draw distance the mod exists to provide, and a histogram of
world-space heights cannot see that. The plan's "Phase 4 requires Phase 3b" claim was
withdrawn for the same reason: at 20 km a full-height box is about 11 px against a real box's
7 px, and both are trivially buried.

The owner's own explanation of the height — ground level near y=100 with the world continuing
to bedrock — led to the mechanism, which was then measured rather than assumed.
`MesherChecks.FrontierWallsSetTheFloor` meshes one section twice, changing nothing but
whether its neighbours are loaded: with all four present it is a single quad spanning y=110
to y=110; with none it is five quads spanning y=0 to y=110. **One open side out of four drops
the whole section's floor to bedrock.** The bound is honest — that curtain is real drawn
geometry — but the section is not tall; it has a tall thing attached to one edge.

### 3. Phase 4 built, in the order the plan sets out

A persistent private depth copy; the full mip chain reduced with `max` by a fragment pass
writing `gl_FragDepth`; conservative AABB projection and sampling; shadow-mode classification
on the GPU; comparison against the delayed occlusion queries; and an explain command.

Two design choices are load-bearing. The reduction uses no compute shader — levels are
disjoint and the base/max clamp makes that explicit — so the pyramid alone runs on hardware
the arenas would refuse. Classification, however, cannot run on the CPU: the sections most
worth culling are the far ones, a section 20 km out is about four pixels tall and therefore
needs the finest levels, and a full-resolution depth level is 14 MB per frame to bring back
on a path that stalls. The owner chose the compute route after both options were put to him.

Everywhere the logic exists twice — `LodHzbReference` against `hzbreduce.fsh`,
`LodHzbProjection` against the classifier's GLSL — the C# half carries the tests and the
shader mirrors it, so a divergence is a shader bug rather than a difference of opinion.

### 4. What running it found that reading it could not

Four defects reached hardware, and every one was found by a run:

- The engine tracks which shader program is in use in its own static state, separate from the
  GL binding. The pyramid called `Use()` and never `Stop()`, so the next terrain draw threw
  and took the client down.
- `sample` is a reserved qualifier in GLSL 4.x. The compute shader would not compile, and the
  mod correctly warned once and carried on rendering — the fail-open path, observed.
- `GetBufferSubData` stalls the render thread even on a frame-old buffer. A fence with a zero
  timeout fixed a 686 µs phase back to about 20 µs.
- A minimised window renders frames with a zero-area viewport, which made two thirds of a
  real session's builds look like failures.

### 5. The measurement, over five owner playtests

Cost, from the sandbox with GPU timers armed: **27.4 µs of GPU time per frame** at 1440p/12
levels, stable across twelve intervals, against **218.6 µs** for cached-terrain draw
submission. The pyramid costs about an eighth of the work it could remove. The frame-time A/B
agrees at +0.023 ms, though that is at the noise floor per view and rests on the sign being
consistent in 5 of 6 views where an off-vs-off pair flips sign three times.

Benefit, from the owner's world at two ground-level locations, 16 million section-tests:

| distance | location 1 | location 2 |
|---|---:|---:|
| 0–1k | 88% | 46–48% |
| 1–2k | 99% | 62–65% |
| 2–4k | 100% | 78–90% |
| 4–8k | 98–100% | 98% |

The absolute share varies with what is in front of the camera; **the shape replicates**. The
aerial BodanBoys route, by contrast, found 0.0% — it puts the camera above the terrain where
nothing occludes anything, and is a draw baseline rather than an occlusion one.

### 6. The finding that decides what comes next

**Every not-hidden verdict is a background refusal.** Across 7.5 million of them, only 25,316
sections were ever genuinely in view. Sky holds the depth clear value, the pyramid takes the
farthest of what it covers, and nothing can be farther — so one sky pixel anywhere in a
section's rectangle makes it unhideable however deeply buried its terrain is.

A section is 64 blocks across at L0 and 1,024 at L4. At that size a box is either fully buried
or it pokes into open sky. That makes cluster subdivision — currently Phase 8 — the thing that
unlocks the remainder, rather than a later optimisation.

---

## Delivered

- **0.3.58** — Phase 3b: per-pass mesh vertical bounds, used by the established renderer's
  frustum box; height distribution and vertical-cull telemetry; `.vhheight` and
  `VINTAGEHORIZONS_SECTION_HEIGHT_CULLING`.
- **0.3.59** — fixed `.vhheight` being refused by the chat parser; silenced the false shader
  errors logged on every start; added floor and ceiling to the height report.
- **0.3.60** — Phase 4 slice 1: persistent depth copy, `max` mip pyramid, GPU timing,
  `.vhhzb`, `LodHzbReference` plus 231 assertions.
- **0.3.61** — projection, sampling and the fail-open gate (42 assertions); GPU compute
  classification in shadow; per-distance reporting; agreement against occlusion queries;
  `KnownVisible` on the query state with 14 assertions; `.vhhzb why`.
- **0.3.62** — `.vhhzb` reports the whole measurement on demand rather than only through the
  30-second log window.
- **0.3.63** — both commands write to the client log, because game chat cannot be copied.
- **0.3.64** — view-matched comparison; finest-first `why` search; counts what the pyramid
  adds over the occlusion already running.
- **0.3.65** — minimised frames skipped rather than counted as failed builds.
- Benchmark runner takes `-Hzb 0|1`; `VINTAGEHORIZONS_DEPTH_PYRAMID` pins it for a run.
- 2,358 assertions pass. Gotchas G67–G72 added.

## Decisions

- **Phase 4 is not gated on Phase 3b.** Withdrawn on the owner's veto; the two serve different
  distance regimes and are complementary. Recorded in the plan and in TODO.
- **Section bounds are not carried into the GPU section record.** That record is a byte layout
  GLSL reads, its only consumer is the untested indirect path, and Phase 5 rewrites it anyway.
  Nothing on the card needs the bounds until it culls there.
- **Classification runs on the GPU**, accepting a compute-shader dependency the pyramid itself
  avoids. The alternative was abandoning far-section culling entirely.
- **The reduction refuses to run under reversed depth** rather than flipping the operator. A
  wrong guess inverts every comparison and the failure is invisible until terrain goes missing.
- **Diagnostics must reach the log.** Two owner sessions were spent producing numbers that
  could not be read: one behind a report that fires once at 30 seconds, one in a chat window
  that cannot be copied from.

## Traps

Promoted to `dev/GOTCHAS.md` as G67 through G72. In short: chat text is parsed as VTML so a
leading `<` swallows the reply; the first shader load always fails and saying so as an error
hides the real one; the engine's shader bookkeeping is separate from the GL binding and
`Use()` needs its `Stop()`; `sample` is reserved in GLSL; an SSBO readback needs a fence or it
stalls the render thread; and a diagnostic that only reaches chat or a one-shot log line
cannot be read by the person running it.

## Flagged and unverified

Judgement calls awaiting human review:

- Whether cluster subdivision should be promoted ahead of the rest of the GPU plan, given that
  whole sections are measurably too coarse to test.
- Whether to keep the pyramid's compute dependency or seek a non-compute classification.

Claims lacking the evidence level to be treated as established:

- **The correctness gate is NOT met.** 1,215 sections were called hidden that a query had
  seen, on 0.3.63. The view-matched comparison that should explain them is written but has
  never run: 0.3.64 and 0.3.65 were not installed before the session ended.
- **The pyramid's gain over the existing delayed occlusion is unmeasured.** The overlap is
  known to be large — millions of verdicts where the query hid what the pyramid did not — so
  the headline share flatters it by an unknown amount. The counter exists in 0.3.65 and has
  never been read.
- **No open-view control exists.** All three benefit samples face terrain; the owner confirmed
  the intended control was still facing a mountain.
- **The cache reaches only about 4 km**, so the 8–16k and 16k+ bands are empty and the
  far-distance case is inferred from the 2–8k trend rather than observed.
- GPU cost is one machine, one resolution, one route. `.vhhzb why` has never produced a useful
  answer, since the run that would test its fix has not happened.
