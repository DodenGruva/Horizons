# Session 52 - The fast path becomes the default, and the scaffolding comes down

**Date:** `2026-08-25`
**Branch/commit:** `render-overhaul`, on top of `0a4f5ec`; closing commit contains this record
**Mod version:** `0.3.103`, packaged and installed, not yet human-run
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

The owner ran 0.3.102 and reported the perimeter-guard counter reading twice: once as `0` and once
as `169994`. Reading his `client-main.log` settled both halves of that.

The two readings are correct and are not a contradiction. The guard was only ever wired to the far
bucket, so the near line reporting zero is by design. The far line is the real measurement.

He then decided to make the whole GPU path the default and retire the diagnostics that had been
staging it. That is a product decision and his to make; it is recorded here as deliberate, because
it is taken ahead of the plan's own Phase 9 gates - no second driver has run this path, and turning
and streaming behaviour have not been judged against the current visual standard.

## Work narrative

### 1. What the guard was actually costing

From the 582-sample split-far report: 2,495,527 far commands tested, 660,943 culled (26.5%), and
**169,994 perimeter-guard refusals (6.8%)**. Every refusal is a command the depth test had already
decided was hidden and the guard forced back into the draw. Removing it takes far-command
suppression from 26.5% to **33.3%**, about a quarter more far terrain suppressed. The index-weighted
effect is unknown: refusals were counted per command, not per triangle.

The same log closed two other questions. The shadow classifier reported **0 degenerate** verdicts,
which is the 0.3.102 base-size check passing rather than misfiring - had the uniforms disagreed with
the pyramid it would have rejected everything. And the pyramid completed **34,868 of 34,868** builds.
So 0.3.102's shader changes compiled and ran correctly on the primary driver.

### 2. The prediction that was wrong, and why

Session 51 predicted this counter would read zero, reasoning that the texel the guard reaches for
outside the projected rectangle would fall inside it once the mapping was corrected. **That was
wrong.** It holds only for boxes that were short by exactly one texel at the edge the guard happens
to inspect. With the rectangle corrected, the guard inspects a genuinely different, genuinely
outside ring, and refuses to hide anything with exact clear sky within one texel of its outline.
Near a ridge against sky that describes an enormous number of far pieces, which is what 6.8% of all
far commands is.

So the guard was doing exactly what it was written to do. What it does is refuse to hide correctly
hidden terrain, which makes it a tax rather than a safety net. It could only ever draw more, never
hide something it should not, so removing it cannot create a hole - the risk is only that the
flicker returns if the mapping fix were incomplete, and that fix is human-confirmed.

### 3. Resolution facts the earlier write-up got wrong

The log gives the pyramid as **1920x1080**, not the 2560x1440 assumed in sessions 50 and 51. The
diagnosis and the fix are resolution-general, but two specifics were wrong for this machine: the
misalignment starts at level **4** vertically rather than 6, and width is **not** exact at every
selectable level - 1920 halves to 15 and then 7, so level 8 and above misalign horizontally too.
The defect was therefore somewhat more widespread than recorded. G96 and the session records are
corrected to state the rule and give the owner's resolution as the worked example.

### 4. Default on

Every stage of the path now defaults on: regional arenas, batched indirect drawing, the depth
pyramid, GPU culling, the same-frame near/far split, packed quads and clusters. The saved config
fields flip with them, so a fresh install with no config file gets the fast path.

The environment variables flipped from opt-in to opt-out - `== "1"` became `!= "0"` - which keeps
the benchmark harness able to pin either side of a controlled comparison for a whole run. That
requirement did not change with the default; only which way an unset variable falls.

The fallbacks are untouched and are what make this safe: a failed capability probe, a refused
shader, a refused allocation or any draw failure still selects the established renderer in the same
frame.

### 5. The scaffolding comes down

Eight commands are gone: `.vhphase8`, `.vhindirect`, `.vhpacked`, `.vhclusters`, `.vhcull`,
`.vhlate`, `.vhheight`, `.vhflicker`. So are the phase-8 preset helpers behind them and the entire
armed flicker-capture machinery - the shader's result buffer and uniform, the eight-slot fenced
readback ring, the CPU transition tracker and its checks. The artifact it was built for is fixed and
nothing could arm it any more; unreachable code that describes a refuted theory is worse than no
code, and git holds it.

`.vhgpu` stays as the single in-game off switch, saved between sessions, and its wording no longer
describes a measurement shadow. `.vhhzb` stays. Commands belonging to other subsystems were not
touched.

---

## Delivered

- Version 0.3.103: the GPU terrain path defaults on with environment overrides now opt-out; the
  0.3.99 sky guard and its 0.3.102 counter removed; eight staging commands, the preset helpers and
  the flicker capture machinery deleted; `.vhgpu` reworded as the one off switch.
- Checks inverted rather than dropped: the fresh-install defaults now pin ON with the reasoning that
  changed, the retired commands are pinned absent, and the surviving off switch and `.vhhzb` are
  pinned present.
- Warning-free build and 5,067 fast assertions.
- Verified `dist/vintagehorizons_0.3.103.zip`, SHA-256
  `63DDA07CA510E1F546301F3EC57FFF7BE01F766485C61FCD6169AB2358D606E9`, copied to the owner's Mods
  folder with 0.3.99 through 0.3.102 left untouched.

## Decisions

**Default on ahead of the plan's Phase 9 gates, deliberately.** The owner's call. Recorded as a
product decision with the open gates named, not as a claim that they closed.

**Delete the guard rather than make it switchable.** The owner asked what command turned it off;
the honest answer was that none existed. Given a measured 6.8% cost, a human-confirmed fix for the
real cause, and a failure mode that can only draw too much, deleting it is the answer to that
question. A toggle would have added a diagnostic in the same breath as retiring diagnostics.

**Keep exactly one off switch.** `.vhgpu off` is nested above every other stage, so it returns the
whole client to the established renderer, and it is saved. Precision levers like `.vhcull` would
diagnose faster but each can select a configuration that is not the product.

**Delete the flicker machinery with its command.** Keeping an unreachable capture for an artifact
that is fixed preserves a refuted theory in source.

## Traps

- **A guard that only ever refuses to hide is a performance tax, not a safety net.** Reason about
  which direction a conservative rule can fail before assuming it is protecting something.
- **A prediction about a diagnostic's output is still a claim.** "It will read zero" was reasoning
  presented as an expectation, and the counter existed precisely because reasoning had been wrong
  three times already. That is why it was built rather than the guard simply deleted.
- **Do not assume the owner's display.** Two sessions of write-ups described 2560x1440; the log says
  1920x1080, which changes which pyramid levels the defect touched.

## Flagged and unverified

**Judgement calls awaiting human review.** Whether default-on survives ordinary play, and whether
the subtree-bounds optimisation is funded next.

**Claims lacking their evidence level.** Version 0.3.103 is built and harness-tested only. It
changes the culling shader and the default path for every frame, so the first evidence is a frame on
screen. The 26.5%-to-33.3% figure is arithmetic over one 582-sample report at one location, not a
re-measurement, and no FPS effect has been observed. The guard's index-weighted cost was never
counted. Phase 9's gates - a second driver, turning and streaming against the current visual
standard, and the paired packed route - are all still open, and the default was moved ahead of them.
