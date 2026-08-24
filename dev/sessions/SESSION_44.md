# Session 44 — The measurement moved offline, and Phase 5 drew its first culled frame

**Date:** `2026-08-23`
**Branch/commit:** `render-overhaul`
**Mod version:** `0.3.69` through `0.3.71`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The session opened on the assumption that Phase 5 was next. It was not: Phase 4's correctness
gate was open and one comparison was owed - wider sampling against cluster subdivision - with
0.3.68 packaged and unrun.

The owner then asked why so much time was going into these tests. That question, not the
plan, set the session. The honest answer was that Phase 4 produces no visible change, so its
only output is a number, and every number so far had arrived through a playtest whose
instrument was broken: two runs on diagnostics that could not be read (G72), one where the
figures were read wrong twice, and a cross-check that had a noise floor above zero by
construction (G73). Three sessions of the owner's evenings had bought nothing.

He chose to move the comparison offline rather than run another build.

## Work narrative

### 1. The offline field measurement

Everything the question needs already ran on the CPU: the cache decoder, the mesher, the
pyramid reduction (`LodHzbReference`), the projection and the box test. `HzbField` assembles
them into the same measurement the game takes - decode real sections from the owner's cache,
mesh them with the renderer's own mesher, rasterise an occluder set into a depth image from a
chosen camera, reduce it through the real reduction, and run both levers over the real
population.

The model is faithful in the way that matters: the depth buffer the pyramid reads is captured
after vanilla terrain and before any cached section, so the occluder is whatever vanilla draws
inside its view distance and the population is the cached sections beyond it. Making that
radius a parameter also makes Phase 6's question answerable with the same code, since a
section is never in both sets.

**The first run was wrong twice, and that is the point of the exercise.** It reported 7.5%
hidden against 46-88% in game. Two faults, both in the harness: the renderer's frustum cull
was not applied, so 70% of the "results" were sections behind the camera being refused as
near-plane crossings; and each camera was placed at its section's highest surface, which
stands the viewer on a hilltop where nothing can hide behind anything. Both produced a clean,
believable table. Offline they cost ten minutes; in game they would have cost two evenings and
might never have been caught.

### 2. The lever comparison, and a correction that mattered

Corrected, at 128 views over two independent seeds, the answer was stable: of the sections the
current test could not hide, wider sampling hides about twice what 4x4 subdivision does, and
it changes only the test rather than how terrain is stored, meshed and drawn.

The first figures were computed at 64 blocks, read from `clientsettings.json`. The owner said
he had only just changed that; every in-game figure on record came from about 350. Re-run at
350 the ordering held but the margin narrowed - and, more usefully, **the offline model
reproduced the game's own number**: 47.5% hidden in the 0-1k band against the 46-88% the
0.3.65 run reported at the same view distance. That is the first time an offline figure and an
in-game figure were compared on equal terms.

The measurement was also extended to answer the question the two-column comparison hides: what
subdivision still adds *after* widening is adopted. It is not an alternative, it is a smaller
follow-on, and that is the number that should decide whether the storage rework ever starts.

### 3. Widening, shipped

`DefaultTexelsPerAxis` went from two to eight, in the C# and in the shader, with the verdict
and the sub-cells both moving so a cell is never judged more loosely than its section. Three
existing checks failed on the way through and correctly: they had been written against the old
width as an unstated assumption, so a settings change read as a defect. They now state the
width they test.

A separate GPU timer was added for the classification dispatch. The pyramid build had one; the
part just multiplied ninefold in texture fetches did not, so a playtest would have reported the
cost of everything except the thing that changed.

### 4. Phase 5

The plan assumed the verdict would be read back and applied a frame later. Reading the code
showed that was avoidable: the command list is fully built before anything draws, so the card
can classify and write the skip decisions into the command list in the same frame. That
removes the staleness that would have produced edge flashes on rotation - the hardest item in
the phase's gate - so the design is strictly safer than the planned one.

Implemented in slices, each independently checkable:

- **Cull boxes per command, in command order.** The walk hands sections over front to back but
  the builder regroups them into page buckets, so slot N is usually not the Nth section added.
  Anything that culls by zeroing slot N reads box N. If those come apart the renderer hides
  terrain based on a different section's position.
- **The command buffer as a compute target,** with `COMMAND_BARRIER_BIT` and nothing else.
- **The cull shader,** sharing one copy of the box test with the measurement pass so the two
  cannot disagree about what hidden means.
- **The ordering:** upload, cull, barrier, draw. Every failure path leaves the commands as the
  CPU uploaded them, which draws everything.
- **The old queries stood down,** including releasing the query objects the batched path had
  been retaining for a whole session without issuing one.

### 5. What the playtests actually established

0.3.69 was run and looked correct, but the log could not say whether culling had run - the
periodic report fires once at thirty seconds, before anyone types anything, and `.vhcull`
printed to chat rather than the log. So the result was unattributable.

0.3.70 fixed both, and the log then answered it plainly: `cull: on: 8019 dispatches over
336798 commands. last frame's commands were culled on the card.` The earlier run's mystery was
in the same log - `on, but idle: there is no depth pyramid to test against` - so on 0.3.69
culling had indeed never run.

0.3.70 also exposed a real bug. The log read *"widening is already hiding 0 sections"*. The
shader sets that flag only for sections it HID; the counter incremented it only for sections
that were NOT hidden. Mutually exclusive, so the figure was structurally zero whatever the
terrain did. Fixed by removing the gate entirely - the flag carries its own precondition.

Corrected, the number is the session's strongest result: **404 sections hidden, 248 of which
the old narrow width would have drawn.** Widening is responsible for 61% of all hides, and it
lands inside the band the offline harness predicted for the owner's 192-block view distance.

---

## Delivered

**Offline (no version):**

- `HzbField`, a cache-backed measurement of both sky-problem levers, plus `HzbFieldChecks`
  over the rasteriser, near-plane clipper and the occluder/box join.

**0.3.69:** widening from two to eight texels; the classify GPU timer; Phase 5 complete in
source - per-command cull boxes, the command buffer as a compute target with its barrier, the
cull shader, same-frame ordering, query stand-down, `.vhcull` off by default. Also a latent
fix: the GL state guard now restores indexed SSBO slot 1, which both compute passes bind and
neither used to put back.

**0.3.70:** the widening counter fix, and `.vhcull` writing to the log.

**0.3.71:** the GPU path switches are saved per install; `.vhcull on` switches on the pyramid
it depends on.

**Human-tested:** 0.3.69 and 0.3.70 both played. No missing terrain, no edge flashes, at a
193-block view distance with culling confirmed active on 0.3.70. AMD RX 9070 XT, GL 4.3.

## Decisions

**Move the measurement offline rather than run another build.** The question never needed a
GPU. What genuinely needs the owner is whether terrain visibly disappears, which is a different
question from which lever is better.

**Wider sampling first, subdivision kept.** Measured roughly two to one, and it changes only
the test. Subdivision is not discarded: it carries a measured 13-15% follow-on and the in-game
counter now reports it every run.

**Same-frame culling, not frame-late readback.** Eliminates stale suppression entirely rather
than guarding against it.

**The cull shader may only ever write zero.** Every other verdict leaves the slot as the CPU
wrote it, so the pass can lose a saving but can never resurrect geometry suppressed for a
reason the GPU knows nothing about - ownership, seams, the distance cap.

**Saved, not defaulted on.** Rejected turning the GPU path on by default: batching suspends
delayed occlusion, which was worth 170 to 500 FPS in session 34, and culling does not yet pay
for itself while cached terrain cannot occlude cached terrain. Saving the setting gives the
owner what he asked for without shipping that trade to everyone.

## Traps

**A flag whose precondition lives in the shader must not be re-gated by its reader.** Trigger:
inverting what a packed result bit means. Failure: the shader set it only for hidden sections
while the counter incremented it only for not-hidden ones, so the figure was structurally zero
and read as "the change bought nothing" for a whole playtest. Safer: count such a flag
unconditionally - it already carries its own precondition - and pin that precondition in a
check beside the code that reads it.

**A status line in a report that fires once cannot observe a switch flipped later.** Trigger:
adding telemetry so a playtest reports itself. Failure: the client's periodic report runs once
at thirty seconds unless allocation telemetry is on, so the cull line could only ever capture
the state before anyone typed a command. Safer: for anything a person switches on mid-session,
log from the command itself, as `.vhhzb` already did.

**A camera placed at the maximum surface of its own section cannot measure occlusion.**
Trigger: choosing viewpoints for an offline occlusion measurement. Failure: taking the section
maximum stands the camera on a hilltop every time; the run reported 7.5% hidden against 46-88%
in game. Safer: take the centre column's surface, which puts the camera in valleys and on
slopes in the proportion the terrain actually has them.

**An offline harness must apply the same CPU culls the renderer does.** Trigger: reproducing an
in-game measurement offline. Failure: without the frustum test the population is the whole
world including everything behind the camera, which the projection then refuses as near-plane
crossings - 70% of the first run. Safer: reproduce the approval chain, not just the test.

## Flagged and unverified

**Judgement calls awaiting human review:**

- Saving the GPU switches rather than defaulting them on. The owner asked for defaults; this
  is the smaller change that meets the stated need.
- `.vhcull on` implying the pyramid, while `.vhcull off` leaves it alone.

**Lacking the evidence level to treat as established:**

- **Culling has never been shown to pay for itself.** 0.3.70 measured roughly 115us per frame
  for pyramid plus classify plus cull, against no measurable frame-rate gain. Both known
  reasons apply and neither is a defect: the scene was CPU-bound at ~300 FPS, and the occluder
  is a 192-block bubble. Not a cost gate failure and not a pass.
- **The 115us is not attributed.** GPU timers arm only under the benchmark harness, so a normal
  session reports 0.0us and the split between depth copy, mip reduction, classify and cull is
  unknown.
- **One machine, one world, one vendor.** Phase 5's gate lists stationary, motion, rotation,
  teleport, vertical look, streaming, cave/structure and threshold-crossing cases; roughly
  "stood on a hill and toggled" has been done. No non-AMD driver has run this.
- **Widening's in-game figure is only trustworthy from 0.3.70.** Every earlier build reported
  it through the broken counter.
- **The offline harness approximates its occluder.** It rasterises cached L0 terrain where the
  game has vanilla chunks, and a section is rasterised whole once any part of it is inside the
  radius. Absolute percentages are approximate; the comparison between levers is not.
