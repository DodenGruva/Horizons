# Session 51 - Post-fix cleanup, and a measurement that corrected me

**Date:** `2026-08-24`
**Branch/commit:** `render-overhaul`, on top of `0a4f5ec`; closing commit contains this record
**Mod version:** `0.3.102`, packaged and installed, not yet human-run
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

Session 50 fixed the Phase 8 precise-angle flicker, the owner accepted it, and the recommendations
that came out of that work were recorded in `dev/TODO.md` as an ordered list. This session was
asked to begin working that list, and did so in the order it was written: retire what the fix made
obsolete, pin the assumption the fix introduced, refresh the figures it invalidated.

No game process was launched. The one measurement this session produced came from `HzbField`,
which reads a copy of the owner's cache database and needs no client, and it is the reason the
session ended somewhere other than where it started.

## Work narrative

### 1. The rejected margin diagnostic is gone

`.vhsplitbias` searched a larger safety margin for the far cached bucket at 4, 16, 64 and 256
steps. Every value behaved alike, which is now explained: the flicker it was aimed at flipped
between real terrain depth and the exact `1.0` clear value, and no bias closes a gap of that size.
The command, the far-bucket-only bias plumbing, `DepthBiasForSteps` and
`MaximumDiagnosticDepthBiasSteps` are removed, and both buckets take the one established four-step
margin. The default was already four steps, so nothing about the shipped verdict changes.

### 2. The sky guard is kept, and made to report on itself

The 0.3.99 one-texel perimeter refusal is very probably redundant after the mapping fix: the texel
it reached for outside the projected rectangle now falls inside it. That is an argument, not
evidence, and deleting a guard on an argument is how the previous three attempts at this artifact
went wrong.

Instead the guard now counts. A word appended after the distance bands in the live cull telemetry
records how often a perimeter refusal turned an otherwise hidden far cluster into a drawn one, and
`.vhhzb` prints it even when it is zero, because zero is the number the line exists to establish.
One ordinary session under `.vhphase8 clusters` now settles whether the guard can go. A non-zero
count would be worth more than the deletion: it would mean the mapping fix did not cover every
case.

### 3. The anchoring assumption is pinned where it is used

Pixel anchoring reproduces the pyramid's own mapping only while the box test's screen size is the
pyramid's base size. Both callers pass `depthPyramid.Width/.Height`, so it holds by construction -
which is exactly the kind of quiet dependency that G96 was. A C# assertion at the call site would
have been tautological, so the check went into the shader, where the assumption is actually used:
the test fails open when `textureSize(hzb, 0)` is not `ivec2(screenWidth, screenHeight)`. A future
reduced-resolution pyramid now loses culling instead of hiding terrain, and a check pins the shader
text so the guard cannot be dropped silently.

The cull program's ten uniform locations moved to link time in the same pass, which was the
trivial item at the bottom of the optimisation list.

### 4. The re-measurement, and a claim withdrawn

The list's third item asked for the `HzbField` widening figure to be re-run or marked stale. It was
first marked, in a source comment stating the old `25.0%` was "now a slight overstatement" because
the harness ran through the defective mapping. **That sentence asserted a direction that had not
been measured, and running the harness disproved it.**

Four runs on that day's cache at 2560x1440, 64 views each. At the 350-block modelled vanilla
distance the original figure quotes: 18.4% and 25.6% on two camera seeds. At the 192 blocks
`clientsettings.json` actually holds today: 16.2% and 23.8%.

The seed alone moves the figure about seven points. That is larger than the mapping correction and
larger than the vanilla-distance difference, and the old `25.0%` sits inside the range rather than
above it. So the correction did not materially change what widening buys, and the number was never
precise to a decimal place. The comment now records a range with its conditions, and the reasoning
rests on the property that was stable in all four runs - wide-4 below wide-8 below wide-16 - rather
than on a midpoint. The same runs put sixteen at 3.9 to 5.4 points above eight rather than the 2.5
recorded earlier, so the curve flattens less sharply than claimed but in the same direction.

Three inputs had changed since the original measurement - the mapping, the cache contents, and the
vanilla view distance - so no single-number comparison to `25.0%` was ever available. Reporting the
spread is what the measurement can actually support.

---

## Delivered

- Version 0.3.102: `.vhsplitbias` and its bias plumbing removed; a perimeter-guard refusal counter
  in the live cull telemetry and on the `.vhhzb` line; a shader-side fail-open when the test's
  screen size is not the pyramid's base size; cull uniform locations resolved at link time.
- Four `HzbField` runs and a corrected `DefaultTexelsPerAxis` comment quoting a range and its
  conditions instead of a single decimal.
- G98, and checks pinning the new telemetry word, the shader constant, the refusal report and the
  base-size guard.
- Warning-free build, 5,091 fast assertions, 1,526 documentation checks.
- Verified `dist/vintagehorizons_0.3.102.zip`, SHA-256
  `7330D7FF8177CE84EA2BFA35BB55207B43E93AF68E7B77E86582B8B2A711607C`, copied to the owner's Mods
  folder with 0.3.99 through 0.3.101 left untouched.

## Decisions

**Count the guard rather than delete it.** Its redundancy follows from the mapping fix by
reasoning, and this artifact has already defeated three plausible arguments. A counter that reads
zero over a real session costs one line of a report and converts the deletion into evidence.

**Put the dimension check in the shader, not at the C# call site.** The call site passes the
pyramid's own fields, so an assertion there compares a value with itself. The shader can compare
the uniform against the sampler it will actually read, which catches any route by which the two
diverge.

**Report a range for a seed-sampled figure.** Two seeds disagreeing by seven points means the
single-value form of this statistic was never meaningful. The decision it supports - eight texels
over two - rests on an ordering that held in every run, and that is what the comment now says.

**Keep the subtree-bounds optimisation out of this build.** It carries real visual risk and needs
its own look-up/look-down check; sharing a build with a shader change would make a missing-terrain
report ambiguous.

## Traps

- **A seed-sampled percentage carries seed noise.** Quoting one to a decimal place invites a later
  session to read a different seed as a regression. Promoted as G98.
- **Marking a figure stale is still a claim.** Writing "now a slight overstatement" into a comment
  asserted a measured direction from an unmeasured argument. The harness was available, offline,
  and took two minutes; the annotation should have waited for it.
- **An assertion at a call site that passes the field it checks proves nothing.** Put the check
  where the two values can actually disagree.

## Flagged and unverified

**Judgement calls awaiting human review.** Whether to delete the sky guard once its counter is
read, and whether to fund the subtree-bounds optimisation next.

**Claims lacking their evidence level.** Version 0.3.102 is built and harness-tested only. It
changes the culling shader, so the first evidence that it still compiles on the primary driver is a
frame on screen; every failure path disables culling rather than hiding terrain, so the exposure is
a lost optimisation. The guard has not been observed idle. The split's suppression and FPS figures
still predate the 0.3.101 fix and remain un-re-measured. The seven-point seed spread is estimated
from two seeds on one cache snapshot on one day: it establishes that the figure is noisy, not how
noisy, and a variance claim would need more seeds.
