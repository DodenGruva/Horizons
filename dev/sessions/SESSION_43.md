# Session 43 — The sky problem gets a number, and a cheaper candidate fix

**Date:** `2026-08-23`
**Branch/commit:** `render-overhaul`
**Mod version:** `0.3.68` packaged; `0.3.66` is the last build the owner ran
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

Continued Phase 4 with the owner asleep and then awake but unwilling to keep playtesting. The
session's turning point was his question — "Are you seriously gated by the measurements and
unable to proceed without them?" — which was correct and which this record should preserve
as a correction rather than an aside.

## Work narrative

### 1. Reading the 0.3.66 run, and being wrong twice

The owner ran 0.3.66 and the log carried four things worth having.

**The epoch guard was disproved.** Session 42 attributed the unsafe disagreements — sections
the pyramid called hidden that an occlusion query had seen — to comparing across a camera
turn, and added a guard refusing comparisons across a view epoch change. That guard refused
**zero** comparisons, and seven unsafe disagreements still stood. The epoch only advances on
a global invalidation, not on ordinary mouse movement, so it was never measuring the thing it
claimed to. A frame-age bound was added on top (four frames, about 10 ms at these frame
rates).

**The headroom figure was measured over the wrong population.** It read 6.4%, which looked
like evidence against cluster subdivision. But **53% of sections came back undecided** in
that run, against under 1% previously, and the owner then explained the setting: new terrain,
nothing close, only distant ground. The near ring of cached-but-masked sections around the
camera has corners behind the camera plane and can never be judged; with nothing in the
middle distance it dominated the population. Subdividing a section that straddles the near
plane subdivides an unanswerable question. The 6.4% is not evidence either way, and would
have been reported as though it were.

The fix was an instrument, not an argument: fail-open now carries its cause — near plane, off
screen, or degenerate — so a run can say which it was.

**Two defects.** A 160,690 us maximum in the phase cost turned out to be the compute program
compiling on the first frame after `.vhhzb on`, inside the timed path; it now compiles before
the clock starts. And `.vhhzb why` still landed on an unjudgeable adjacent section, so it now
walks past those to the first section the test can actually judge, keeping the near one only
as a fallback.

**The good number.** 3,439 of 4,570 hides were sections no occlusion query had measured —
**75% of what the pyramid finds is genuinely new** rather than duplicating the delayed
occlusion already running.

### 2. The correction: a cross-check is not a gate

Asked whether the work was really blocked on measurements, the honest answer was no.

The comparison against occlusion queries **can never read zero**. The pyramid judges last
frame's depth buffer; the query reports an actual draw some frames earlier. A moving camera
makes them disagree while both are correct. Seven in 2,922 is 0.24%, about what occasional
mouse movement should produce. Treating that as a gate meant asking the owner to keep playing
to chase a number with a floor above zero by construction.

The question underneath — can the test hide a box that is partly in front of the scene — is
deterministic and belongs offline. `NeverHidesABoxThatPokesOut` builds a synthetic ridge (a
near-depth occluder with background left, right and above), reduces it through the real
pyramid code, and throws boxes at it: inside, past each edge, straddling a corner, spanning
the screen, and one swept a percent at a time from inside to outside. Every box touching
background must never hide, and once a widening box escapes the occluder it may never hide
again.

### 3. What the failing fixture taught

The fixture failed on its first run, and the reason is the session's main finding.

A box **fully inside the occluder in pixels** refused to hide, because at its size the level
rule chose level 8, where one texel pools 256 screen pixels — so a texel overlapping the box
also reached outside the occluder and pulled in background. Stated precisely:

> **A box can only be hidden if it clears the occluder edge by at least one texel of the
> level it is tested at, and the level is chosen from the box's own size — so larger boxes
> are tested at coarser levels and need proportionally larger clearance.**

That is the sky problem in exact terms, and it points at a fix that is not cluster
subdivision. The level is picked so a box spans at most N texels; raising N picks a finer
level and shrinks the clearance needed. Measured offline over 1,085 boxes behind the
synthetic ridge:

| texels per axis | hidden |
|---:|---:|
| 2 (current) | 326 |
| 4 | 368 |
| 8 | 404 |
| 16 | 421 |

Eight finds about a quarter more than two, and the curve flattens after it. The cost is
samples per box, growing as the square; it changes nothing about what is drawn, how terrain
is stored, or how sections are built. Cluster subdivision changes all three.

### 4. Making the two levers comparable on real terrain

Rather than choose from a synthetic fixture, the shadow pass now measures both over exactly
the same population — the sections the current test could not hide. For each it reports how
many a 4x4 split would recover, and how many a wider sampling footprint would hide whole. One
run distinguishes them, and if wider sampling wins the answer is a constant rather than a
phase.

---

## Delivered

- **0.3.67** — fail-open split into near-plane, off-screen and degenerate causes; comparison
  bounded by query age as well as epoch; compute program compiled outside the timed path;
  `.vhhzb why` walks past sections it cannot judge.
- **0.3.68** — sampling width made a parameter; the shadow pass compares a wider footprint
  against 4x4 subdivision over the same population.
- `NeverHidesABoxThatPokesOut` and `WiderSamplingHidesMore`: a synthetic-ridge fixture that
  answers the correctness question offline and measures the sampling-width curve.
- Result packing extended and pinned across every verdict, cell count and flag combination.
- The shader is held to its C# twin's constants by a check, including the two sampling widths.
- 2,899 assertions pass.

## Decisions

- **The occlusion-query comparison is a cross-check, not a gate.** It has a noise floor above
  zero by construction. Correctness is established by the offline synthetic-scene fixture; the
  comparison remains useful for spotting a gross divergence and for the new-saving count.
- **Wider sampling is now a candidate fix alongside cluster subdivision**, and is measured
  beside it rather than argued against it. It is strictly cheaper if it works.
- **Sampling width stays at two by default.** Nothing changes behaviour until the comparison
  has run on real terrain.

## Traps

- A cross-check between two asynchronous measurements has a noise floor. Gating on it costs
  playtests and never converges. Promoted as G73.
- A synthetic occlusion fixture must place its test boxes clear of the occluder edge by more
  than one texel of the level they will be tested at, or it fails for the same reason real
  terrain does. Cost one debugging cycle, and was worth more than the test.

## Flagged and unverified

Judgement calls awaiting human review:

- Whether to raise the default sampling width, once the on-terrain comparison exists.
- Whether cluster subdivision is still worth promoting if wider sampling closes most of the gap.

Claims lacking the evidence level to be treated as established:

- **The sampling-width curve is from a synthetic ridge, not real terrain.** The 24% gain is
  indicative of a lever existing, not predictive of its size in a real scene.
- **Nothing in 0.3.67 or 0.3.68 has run.** The undecided breakdown, the frame-age bound, the
  fixed `why` command and the two-lever comparison are all unexercised on hardware.
- The seven unsafe disagreements are attributed to instrument noise by reasoning, not proven
  so. The frame-age bound should shrink them; if it does not, that reasoning is wrong too.
- The 75% new-saving figure is one run, in a sparse setting, over 4,570 hides.
