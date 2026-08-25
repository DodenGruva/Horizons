# Session 50 — The flicker was a texel-mapping defect, and it is fixed

**Date:** `2026-08-24`
**Branch/commit:** `render-overhaul`, on top of `f70f598`; closing commit contains this record
**Mod version:** `0.3.101`, packaged, installed, and human-accepted
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

Session 49 ended with 0.3.100 installed as instrumentation and a four-way interpretation guide
for the capture it was meant to produce. That capture was never run. This session was asked for a
diagnosis and a proper fix from source, and found the defect by reading the pipeline instead.

The starting position was unusually well constrained by the previous sessions' negative results.
Three things were already known and all three turned out to be consequences of one cause: the
artifact appeared under `late` but not `cull`, the 4/16/64/256 margin ladder changed nothing, and
the 0.3.99 one-texel sky perimeter did not cure it. Session 49 had also established that the
offenders alternate only between `occluded` and `background`, that background reads the exact
`1.0` clear value, and that the affected meshes sit inside visible terrain rather than along a sky
silhouette.

The investigation was source-only. No game process was launched, and the diagnosis was gated on an
offline reproduction before any fix was written.

## Work narrative

### 1. The pyramid and the test disagreed about what a texel covers

`LodHzbReference.LevelSize` halves each level with `width >> level`, flooring, and `hzbreduce.fsh`
folds the leftover odd row and column into the **last** texel. A level-L texel therefore stands for
exactly `2^L` source pixels, with the final texel absorbing the remainder out to the screen edge.

Both copies of the box test converted the projected rectangle to texel indices by multiplying the
normalized bounds by the level's texel **count** - `floor(minU * levelSize.x)` and
`ceil(maxU * levelSize.x) - 1` - which assumes every texel covers `1/count` of the screen. That is
true only while `levelSize * 2^level == screenSize`.

At 1440 rows the chain runs 1440, 720, 360, 180, 90, 45, 22, 11, 5, 2, 1. From level 6 up, 22
texels times 64 rows is 1408, not 1440, and the two mappings drift. Because `levelSize` is always
less than or equal to `screenSize >> level`, the computed index is never larger than the true one,
so the **top edge of the sampled rectangle can fall one texel short**. The excluded texel is
precisely the one holding whatever lies beyond an occluder's silhouette. A command whose only
visible part is a sliver peeking over a ridge then reads occluder depth in every sample it takes
and is declared `occluded` - the one verdict the plan's invariant 8 forbids the test from reaching.

Worked example at level 6: texel 21 covers rows 1344 to 1439. A box whose top edge is row 1349
computes `ceil(1350/1440 * 22) - 1 = 20`, and texel 20 stops at row 1343. The sky that would have
saved the box is never sampled.

The width was innocent. 2560 is `5 * 2^9`, so the horizontal mapping is exact through every level
the test can select. The error is vertical only, which is why the artifact is sensitive to pitch
and attaches to horizontal silhouettes.

### 2. Why one wrong near verdict flipped whole far clusters

A wrong verdict alone is not flicker; it is a small permanent error. The split is what turned it
into an alternating one.

The near bucket is culled with `backgroundGuardTexels` at zero, so a marginal near command can be
wrongly suppressed. That command's pixels are then missing from the mid-frame depth picture the far
bucket is built against, and what stands there instead is the exact `1.0` clear value. Any far
cluster whose projected rectangle contains that hole reads `1.0` and returns `background`, which
draws. On a frame where the near command survives, the same far cluster reads real terrain depth and
returns `occluded`, which does not. A two-pixel error in a near command therefore switches an entire
far cluster on and off, and sub-texel frame-to-frame noise - the 0.000604 matrix delta session 49
measured on a still camera is enough - decides which way it lands each frame.

This reproduces every recorded observation: transitions exclusively `occluded`/`background`, a
**core** texel alternating between terrain and exact `1.0`, and offenders inside terrain rather
than at the sky line.

It also explains the two failed remedies, which is what raised confidence enough to act. The margin
ladder could not work because the flip is between covered depth and the exact clear value, not a
near-equality comparison; no bias closes a gap of that size, and 4, 16, 64 and 256 steps behaving
alike is the signature of that. The 0.3.99 perimeter guard could not work because the `1.0` hole
left by a wrongly culled near command lies in the **interior** of the far cluster's rectangle, and
the guard only inspects one texel outside it. Clusters multiplied the affected spots because they
create up to sixteen independent verdicts per section, and `.vhcull off` stopped everything because
it removes the verdicts entirely.

### 3. Reproduce first, then fix

The fix was delegated to a subagent under an explicit gate: build the offline fixture and prove the
current code returns the wrong verdict **before** changing it, and report the diagnosis falsified if
it does not.

It did reproduce. A 256x1440 synthetic buffer with an occluder on rows 0-1343 and clear `1.0` on
rows 1344-1439, reduced through `LodHzbReference` level by level, with a box at rows 900-1349 and
depth 0.9 behind the occluder: the pre-fix tier ran 5,089 assertions with exactly one failure, and
that failure was the peeking box being called hidden. The five control cases in the same fixture
passed pre-fix as well, which is what makes the single failure attributable to the mapping rather
than to the fixture.

### 4. The fix, applied identically in both mirrors

The rectangle is now anchored in screen pixels and then divided down by the level's own halving:
`floor`/`ceil` to integer pixel bounds, clamp to the screen, shift right by `level`, then clamp the
far edge to `levelSize - 1`. That final clamp is what honours the odd-dimension fold, because the
leftover rows genuinely live in the last texel rather than being trimmed away.

Both copies changed together - the GLSL `TestBoxDetailed` inside `SharedSource`, which serves both
compute passes, and the C# `LodHzbProjection.IsOccluded`. The GLSL gained one mirrored fail-open for
a zero screen size, since the screen dimensions now anchor an integer index rather than only scaling
a float.

The level-selection rule is unchanged. Anchoring lets a box touch one more texel than the level was
sized for, so the degenerate guard must stay strictly-greater-than: a span of nine texels gives
`x1 - x0 == 8`, which passes at eight per axis. A fixture case pins exactly that boundary so the fix
cannot quietly convert real culls into fail-opens.

The change is one-directional. Anchoring can only add texels on the far edge, and more max-reduced
samples can only push `farthest` farther away, so it strictly reduces hiding. There is no new path
by which visible terrain can be culled; the cost direction is a few extra draw calls.

### 5. Review and acceptance

The delegated work was reviewed rather than accepted on report. Both changed sites were read side by
side and confirmed textually parallel; the old mapping is absent from both files; the sky guard,
level rule, reduction shader, pyramid build, barriers, builder and split ordering are untouched. The
full fast tier was re-run independently at 5,089 assertions and 0 failures, and the installed zip's
SHA-256 was compared against `dist` rather than taken from the report.

**The owner then ran 0.3.101 and confirmed the flicker is gone.** That is the first human evidence
that the shader variant compiles with the change and that the artifact is resolved.

### 6. Forward recommendations

Recorded in `dev/TODO.md` rather than acted on. The short form: the pipeline's remaining problem is
acceptance, not speed. The highest-value next steps are retiring the diagnostics this fix made
obsolete, pinning the assumption the fix now depends on, and closing the cross-driver and
motion gates that stand between a working experiment and a default-on renderer. The one optimisation
worth doing on its own merits is aggregate vertical bounds for quadtree **subtrees**, which the plan
already flags as the largest unattributed per-frame cost and which improves the established renderer
whether or not the fast path ships.

---

## Delivered

- Version 0.3.101: pixel-anchored HZB texel mapping in both the GLSL box test and its C# mirror,
  plus a mirrored zero-screen fail-open in the shader.
- `TexelRectFollowsThePyramidsOwnHalving`, a deterministic fixture that reduces a real 1440-row
  pyramid and pins the peeking box, the folded remainder, two aligned-level controls, the
  nine-texel degenerate boundary, and a box in front of the occluder.
- Warning-free Release build and a 5,089-assertion fast tier, verified twice and re-run
  independently during review.
- Verified `dist/vintagehorizons_0.3.101.zip`, SHA-256
  `597FD878D8AB63EEE92601D4A1D1DA1290D17DCA9A963B4977CCB2BB4162FB4B`, copied to the owner's Mods
  folder with 0.3.99 and 0.3.100 left untouched.
- Human acceptance: the precise-angle flicker is gone.
- G96 and G97, the session index, changelog, renderer plan, TODO, status, and this record.

## Decisions

**Gate the fix on an offline reproduction.** The fix was not written until the unfixed code failed
a deterministic fixture on the exact case the diagnosis predicted. Three previous sessions shipped
plausible remedies that a capture later refuted; this one cost minutes and produced a permanent
regression check as a by-product.

**Change both mirrors in one step.** The GLSL and the C# are deliberately statement-for-statement
copies, and only the GLSL runs in game. Splitting the change would have left the check tier
validating a rule the shader no longer implemented.

**Keep the sky guard for now.** It is probably redundant after this fix, but removing it in the same
change would have confounded the owner's verification. Retirement is its own decision, and it wants
one capture showing the guard never fires.

**Do not add hysteresis.** It was considered while the cause was still open. With a conservative
mapping restored, any remaining knife-edge flip is draw-safe by construction, and cross-frame GPU
state would be complexity spent on something that no longer reaches pixels.

## Traps

- **A mip texel covers `2^level` pixels, not `1/count` of the screen.** They agree only until a
  dimension halves to something odd, and then the test silently samples the wrong rectangle at the
  far edge. Promoted as G96.
- **A reference implementation that mirrors a shader inherits the shader's assumptions.** 5,073
  assertions passed over both copies of the same mistake. A check has to tie the arithmetic to the
  other artifact's real behaviour, not to its twin. Promoted as G97.
- **A conservative test cannot make visible terrain flicker.** Visible flicker is therefore evidence
  of a non-conservative verdict somewhere upstream, not of noise to be damped. Two sessions were
  spent damping.
- **A wrongly suppressed occluder becomes a hole of exact clear depth for the next stage.** In a
  staged depth pipeline, a small error in an early bucket is amplified into a whole-command flip in
  a later one, so an artifact seen in the far bucket can belong entirely to the near bucket.

## Flagged and unverified

**Judgement calls awaiting human review.** Whether to retire the 0.3.99 sky guard and the
`.vhsplitbias` diagnostic, and which of the recorded optimisation candidates to fund.

**Claims lacking their evidence level.** No FPS or suppression figures were re-measured after the
fix; the ridge numbers in this repository all predate it and the fix strictly reduces hiding, so
they are now a small overstatement of what culling removes. The `25.0%` widening figure quoted in
`LodHzbProjection` came from `HzbField`, which runs through the corrected mapping, and was not
re-run. The sky guard has not been observed idle. The fix's new dependency - that the cull's screen
dimensions always equal the pyramid's own - holds by construction today but is asserted nowhere.
Second-driver, turning/streaming, and paired packed-route evidence remain open exactly as before.
