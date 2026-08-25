# Session 53 - Default-on accepted, promoted to 0.4.0

**Date:** `2026-08-25`
**Branch/commit:** `render-overhaul`, on top of `0a4f5ec`; closing commit contains this record
**Mod version:** `0.4.0`, packaged, installed, and human-accepted
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

Session 52 made the GPU terrain path the default and removed the scaffolding that had staged it,
ahead of the plan's own Phase 9 gates and on the owner's explicit decision. That build had not been
run. This session is the acceptance and the promotion.

The owner played 0.3.103 and reported that everything looks correct and that performance is a large
improvement. He then directed the promotion to 0.4.0, a documentation close-out, and a clean
starting point for the next work.

Before promoting, the retired diagnostics were checked against the remaining TODO items to confirm
nothing that had been deleted was needed to implement them. It was not: every measurement surface
survived, and only the switches were removed.

## Work narrative

### 1. What the acceptance covers, and what it does not

The owner's report is qualitative: the picture is right and the frame rate is much better on his
machine. That closes the visual and motion question for the primary driver in ordinary play, which
is the gate that mattered most and the one that has failed repeatedly since Phase 6.

It is deliberately **not** recorded as a performance measurement. No figure was taken, so none is
written down. The suppression and frame-rate numbers already in the repository all predate the
0.3.101 mapping fix and remain flagged as historical rather than current.

It also does not close the remaining Phase 9 gates. A second driver has still never run this path,
and the paired packed route has not been timed. Default-on was moved ahead of both, and that remains
recorded as a deliberate product decision rather than as evidence those gates closed.

### 2. Confirming the retired diagnostics were not load-bearing

Checked directly rather than assumed. All thirteen environment overrides survive, including the two
added when the defaults flipped, so any single stage can still be pinned for a scripted run. The
live cull telemetry, the GPU stage timings, the section-height distribution and vertical-cull
counters, the distance-band report and the offline `HzbField` harness are all intact.

Against the open TODO items: the arena page-size experiment, the upload-volume measurement, the
two-tier cluster question, the suppression re-measurement and the packed route all run on surfaces
that were kept. The one item that will want something new is the subtree vertical bounds, and that
is a new toggle for new work rather than a resurrection - the section-level equivalent of
`.vhheight` also survives as `VINTAGEHORIZONS_SECTION_HEIGHT_CULLING`.

What was genuinely lost is diagnosis speed, not capability: isolating culling from batching now
needs an environment variable and a restart rather than one command, and a flicker recurrence would
mean restoring the capture from git. Both are recorded so a future session does not rediscover them
under pressure.

### 3. The promotion

0.4.0 is byte-identical to the accepted 0.3.103 apart from the two version strings. The minor
version marks the milestone: the fast path stopped being an experiment behind switches and became
the renderer.

`CLAUDE.md`'s packaging rule said the patch component advances by exactly one, which would forbid
this. The rule is amended rather than quietly broken: the owner may direct a minor or major
promotion at a milestone, and the patch rule continues to govern ordinary test artifacts.

---

## Delivered

- Version 0.4.0: promoted from the accepted 0.3.103 with no code change.
- Verified `dist/vintagehorizons_0.4.0.zip`, SHA-256
  `695E283E5B2EF99EF49F04DE50471AB2B22243924DF49337B54E84679506D3DE`, copied to the owner's Mods
  folder without removing anything.
- A 0.4.0 release entry consolidating what the milestone means for a player, with the compatibility
  statement that no cache, protocol, blob or schema meaning changed.
- Confirmation, checked against source, that no retired diagnostic is required by any open TODO
  item, and a record of the two things that did get slower to diagnose.
- `CLAUDE.md` versioning rule amended for owner-directed milestone promotions.
- Warning-free build, 5,067 fast assertions, and the documentation checks at session close.

## Decisions

**Record the acceptance qualitatively.** "Looks great and performance is fantastic" is human
acceptance of the picture and of motion on one machine, and it is written down as exactly that. No
percentage or frame-rate figure is attached to it, because none was measured.

**Promote with no code change.** 0.4.0 carries the build that was actually played. Rebuilding
against a changed tree would have promoted something nobody had run.

**Amend the versioning rule rather than break it.** A rule that the project silently violates is
worse than one that names its exception.

**Leave the historical numbers in place, still flagged.** The 80.5%/77.1% suppression and the
340-to-460 FPS ridge result are the record of why this path was worth finishing. They are not
current, they are marked as not current, and deleting them would lose the reasoning.

## Traps

- **Acceptance is not measurement.** A warm qualitative report closes a visual gate and no other.
  Writing a number beside it that nobody took is how a repository acquires figures with no
  provenance.
- **Retiring a switch is not retiring an instrument.** The distinction is what made this cleanup
  safe: the commands went, the counters and offline harness stayed, so the open work is unaffected.
  Check that separation explicitly before deleting, not afterwards.

## Flagged and unverified

**Judgement calls awaiting human review.** Whether to push, tag or publish 0.4.0 - none of which
was done here - and whether the subtree vertical bounds is the next item to fund.

**Claims lacking their evidence level.** Acceptance covers one machine, one driver, one world and
ordinary play. A second driver has never run the fast path. The paired packed route is untimed, so
the 12-byte format's memory and bandwidth benefit remains unseparated from its decode cost, and the
temporary expanded regional mirror is still retained alongside it. No current suppression or
frame-rate figure exists for the corrected mapping. Long sessions, multiplayer, resizes, shader
reloads and competing-LOD-mod deferral under the new default are all unexercised.
