# Session 41 — Phase 3 draws, and two instruments for symptoms nothing could measure

**Date:** `2026-08-22`
**Branch/commit:** `render-overhaul`, from `7857be5`
**Mod version:** `0.3.57` (packaged, never run)
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

The owner asked what was next, then chose the render plan, then approved starting the
drawing half of `PLAN_GPU_DRIVEN_TERRAIN_RENDERER.md` Phase 3 with the ordering left to
this session. Phase 3's non-drawing half and its measured draw-call gate were already in
place from session 39: 87-182 opaque submissions per frame would collapse to 8-17
multi-draw batches.

Out of scope, and it took an owner correction to keep it there: the join/loading behaviour
investigated late in the session. The owner has plans for that work and asked to be
consulted before the focus moves. Phase 4 was explicitly held at session end.

No game process was launched. Nothing in this session has drawn a frame.

## Work narrative

### 1. One shader body, two programs

The plan said "a fast variant of the terrain shader". A multi-draw has no per-draw
uniforms, so the six per-section values the established shader receives as uniforms have to
arrive another way - but a copied shader would make the phase's acceptance gate ("draws
pixels identical to the established path") meaningless the first time someone edited one
copy.

`RegisterFileShaderProgram` loads by program name, so a second program needs a second file
pair. The engine's `ShaderRegistry` was decompiled to find the alternative: `HandleIncludes`
splices `#include` by bare filename from a table built from every loaded `shaders` and
`shaderincludes` asset. The shader body therefore moved to
`assets/vintagehorizons/shaderincludes/lodterrainbody.vsh|fsh`, and the four files under
`shaders/` are three-line wrappers; the indirect pair adds `#define VH_INDIRECT 1`.

Inside the body one `#ifdef` chooses uniforms or instanced attributes and `#define`s them to
the names the rest of the file already uses, so **every line below that block is identical
in both variants**. `IShader.PrefixCode` was also found and rejected as the mechanism: it
would have required registering both programs under one name, which shares a registry slot
and leaks a program object per shader reload.

**The timing risk this introduced was settled from the owner's own log, not by reasoning.**
The include table is built inside `loadRegisteredShaderPrograms`, and the client's startup
sequence builds it once at `DoGameInitStage2` from base assets only. The log shows
`Loading shaders...` running a second time on every world load, after the mod list is
printed - so mod-supplied includes are in the table by the time our program is registered.
`LoadShader` additionally names the failure explicitly if the body is ever missing, rather
than leaving an empty shader to fail for no stated reason.

### 2. The pass, and what it does not do

The walk is unchanged - ownership skip, distance cap, frustum, front-to-back. In an indirect
frame `SetupSectionTransform` writes no per-section uniforms and `SubmitOpaqueMesh` writes a
command instead of issuing a draw; after the walk the pass switches program, uploads that
frame's frame-uniforms to it, and issues one `MultiDrawElementsIndirect` per page set.
Geometry is re-pointed per batch through `glBindVertexBuffer`; the records are bound once
with divisor one, so each command reads element `baseInstance`. Water and the established
program are untouched.

Two fallbacks keep partial coverage and driver failure off the screen. Sections the arenas
do not hold are drawn the established way in a second sub-pass with their own uniforms, and
a pass where **any** batch failed redraws its whole list that way - all or nothing, because
three batches of four leaves a quarter of the horizon missing while the same opaque geometry
submitted twice writes no second pixel.

Delayed occlusion is suspended while batching is on. A per-section `AnySamplesPassed` query
has to wrap that section's own draw and a batched section has none; deferring query issuance
into the leftover pass would have made the first indirect path also a change to the
occlusion mechanism. The consequence is that a fair A/B needs occlusion off on both sides,
which is now written into the plan and the TODO.

### 3. Reviewing it before anyone ran it

Two defects were found by review and by the checks, both in code written this session.

The drawer's state restoration ran through the same guard that returns early once the drawer
has failed, so a failing pass would never have restored the GL state it captured - and the
engine's own renderer runs immediately afterwards. The check that asserts restoration
happens after a refused batch is what caught it.

Flipping `.vhindirect` did not invalidate the temporal occlusion results. Under batching no
new answers are taken, so switching back would have acted on answers from seconds earlier
and a different camera position - showing as terrain missing right after switching off, in
exactly the comparison the switch exists for. There is now a fourth global-invalidation
site, and the static check that counts them was updated deliberately rather than relaxed.

### 4. Two instruments for symptoms nothing could measure

**Frame timeline.** The owner reports micro-hitches dozens of times a second. Nothing in the
mod could see them: the hitch counters trip at 25 ms and a whole frame at 400 FPS is 2.5 ms.
`LodFrameTimeline` measures the interval between consecutive render frames, this mod's share
of each frame, and how far each interval ran over its own moving average. The excess
histogram exists because the shared per-phase histogram's fine 25 us buckets stop at 1 ms,
so a 2.5 ms interval quantises to 250 us and cannot resolve a 400 us hitch at all. Spikes
are judged against a moving average, so the rule means the same thing at 400 FPS and at 60,
and a world load is excluded rather than counted as a hitch.

The number to read is `SlowFrames` against `SlowFramesWithSlowMod`: if frames stand out but
our callback was ordinary during them, the hitches are not ours.

**Join diagnostic.** Six joins across the archived logs were compared and both cache
databases decoded. Five joins with 2,183-3,291 cached sections reach their first hundred
meshes in 2.3-9.1 s; one with 5,143 took 60.2 s and had built nothing after thirty seconds,
with every queue empty and the storage thread reporting `116 read, 0 async loads in flight`.
Both databases have a complete L0-L6 pyramid, so a missing coarse level is not the cause.
`OnRenderFrame` returns before the selection walk while no mesh exists, and that walk is
what asks storage for sections, so the first mesh has to come from the dirty set instead.
Why that set was empty is not established and was deliberately not guessed at; a `Join:` line
now reports the bootstrap state if nothing has been built after ten seconds.

**This investigation was out of scope and should not have been started without asking.**

---

## Delivered

**0.3.53 through 0.3.56 (packaged, then superseded and removed).** Interim artifacts; none
was installed or run. Only `dist/vintagehorizons_0.3.57.zip` survives.

**0.3.57 (packaged, never run).** Phase 3's visible half: `LodGpuIndirectDrawer`,
`LodGpuOpenGlDrawBackend`, the split shader body with its two wrappers, the indirect pass
inside `DrawLegacyOpaque`, `.vhindirect`, and `VINTAGEHORIZONS_GPU_INDIRECT` plus
`bench-windows.ps1 -GpuIndirect` so either side can be pinned for a scripted run.
`LodFrameTimeline` and the periodic `frame timeline:` line. The `Join:` stall line and a
`first mesh after Xs` milestone. `LodGlStateGuard` grew the vertex-array and draw-indirect
bindings; `LodPhaseCost` grew `P50Us`.

**Tests.** `GpuIndirectChecks.DrawerIssuesEveryBatch` and `DrawerStopsAfterAFailure` over a
recording fake backend; `StaticAssetChecks.IndirectShaderVariant` holding the wrappers, the
attribute locations against the record's offsets, the flat varyings, one declaration per
per-section uniform and one branch per stage; `PhaseCostChecks.FrameTimeline` over supplied
timestamps. 1,969 assertions, 1,466 doc checks.

**Documentation.** Plan status and Phase 3 rewritten with what was built and the two
departures from the phase text; TODO sections for batched drawing, the micro-hitches and the
join; CHANGELOG 0.3.57. A duplicated `## [0.3.52]` section was removed from the changelog -
it still carried the re-mesh claim session 40 withdrew.

## Decisions

**One shader body included by two thin wrappers**, rather than a copied file or one program
name with two prefixes. The gate is pixel identity; the arrangement makes drift structurally
impossible instead of a review responsibility.

**Delayed occlusion suspended under batching**, rather than deferring query issuance into
the leftover pass. Phase 3's purpose is to measure batching independently; bundling a change
to the occlusion mechanism into it would have confounded the only number the phase exists to
produce.

**All-or-nothing multi-draw.** A partially issued pass falls back to the established path
for its whole list. Redrawing opaque geometry costs a submission; a batch that never went
out is a hole.

**No separate CPU-time baseline run.** `DrawCost` has no recorded absolute figure, but the
off side of the same A/B session is a stronger baseline than a separate earlier run, and it
costs the owner nothing extra.

**The arena capability gate was left alone.** `Tier1RuntimeValidated` requires HZB compute
and depth validation that indirect drawing never uses, so a driver with MDI and no compute
would be refused for no reason. Not changed: that flag also gates the measurement shadow
whose accepted numbers were taken under it, everything validates on the owner's machine, and
Phase 4 splits the gates anyway. Recorded as a flagged decision.

**Phase 4 held** at the owner's direction. Phase 3 has no hardware evidence, and the depth
work builds directly on its buffers and command list.

## Traps

**Trigger:** editing a repository source file with a script or an editing tool.
**Failure:** `LodTerrainRenderer.cs` is CRLF; inserted lines were LF, leaving one stray line
ending, and exact-match patches then failed against text that looked correct in the terminal.
**Safer action:** normalise on read and restore the file's own ending on write. G64 already
records this and it still cost time twice.

**Trigger:** a failure path that runs through the same guard as the work it protects.
**Failure:** the drawer's `Try` helper returns early once the drawer has failed, so the
`finally` that restored GL state did nothing precisely when it mattered.
**Safer action:** cleanup runs outside the failure guard, and gets a check that asserts it
happened after a refusal. Promoted to G65.

**Trigger:** a switch that changes what the renderer draws or how it decides.
**Failure:** `.vhindirect` left previous-frame occlusion answers in place; switching back
would have hidden terrain that a stale answer called hidden, and it would have looked like
the fast path losing terrain.
**Safer action:** invalidate cached visibility whenever the path that produced it changes.
Promoted to G66.

**Trigger:** noticing an unexplained measurement in a log while doing something else.
**Failure:** a join anomaly was investigated for most of a turn without asking; the owner
had plans for that work and the session's stated priority was the render plan.
**Safer action:** surface the observation, record it, and ask before changing focus.

## Flagged and unverified

Judgement calls awaiting human review:

- Batched drawing defaults off and is session-only; whether it should ever default on is a
  decision after its gates, not before.
- Delayed occlusion is suspended under batching rather than preserved by a second mechanism.
- The arena capability gate still demands HZB capability that indirect drawing does not use.

Claims lacking the required evidence level:

- **Everything about Phase 3's visible path.** It is built, harness-tested and packaged. It
  has never drawn a pixel. The visual gate, the CPU-time gate, the open-horizon GPU check
  and even whether the indirect shader variant compiles are all open. There is no GLSL
  validator on this machine; the include splice and both preprocessor branches were
  simulated offline and are coherent, which is not the same as compiled.
- The frame timeline and the `Join:` line are source- and harness-tested only. Neither has
  been read against a real client.
- The join anomaly has one sample and a partly traced mechanism. The empty dirty set is
  described, not explained.
