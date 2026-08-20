# Session 28 - Five wrong diagnoses, three real bugs, and one unsolved band

**Date:** `2026-08-19`
**Branch/commit:** `codex/chunk-aware-mask`, uncommitted at close
**Mod version:** `0.3.15`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The session opened on a clean tree with session 27 closed and one blocking item: a band of
missing terrain under `.vhmask on` that survived standing still and never filled. Session 27
had left instructions to reproduce it and read three telemetry counters rather than write a
fifth ownership fix from reasoning.

Reading the 0.3.4 playtest log for those counters turned up a startup fault instead, and from
there the session became a long alternation between static source-tracing of the game's own IL
and human playtesting. The band is still open at close. Five separate explanations were
advanced and disproved; three unrelated real defects were found and fixed on the way.

Out of scope throughout: committing, pushing, and the join-time residency slowdown, which was
observed and recorded but never diagnosed.

## Work narrative

1. **A duplicate command name had been silently disabling part of startup.** `.vhwhy` was
   registered twice. `ChatCommandApi.Create` throws on a duplicate, the throw propagates out of
   `StartClientSide`, and the game logs a failed system and carries on: everything registered
   before the collision worked, everything after it did not exist. `.vhdetail` had been absent
   from 0.3.4 entirely. The coarse-draw report became `.vhcoarse`, and a static check now fails
   the tier on any duplicate name.

2. **G40 was wrong about `IWorldChunk.Empty`.** A reflection probe over the installed
   assemblies established from IL that `ICoreClientAPI.IsChunkRendered` is exactly
   `ClientChunk.quantityDrawn > 0`, that `ChunkTesselatorManager.TesselateChunk` advances that
   counter and returns early when the chunk reports empty, and that
   `ClientWorldMap.LoadChunkFromPacket` assigns `chunk.Empty = packet.Empty`. The flag is the
   server's own answer, delivered with the chunk, and the tessellator itself trusts it. 0.3.3
   therefore failed on its rule, not its input: a column is owned only when all of its chunks
   are, every column has sky, so refusing air excluded every column at once.

3. **The mask had never worked.** `maskSectionOrigin` was a `uniform ivec2` set through
   `ShaderProgramBase.Uniform(string, Vec2i)`, which reaches `glUniform2f`; an integer uniform
   rejects that with `GL_INVALID_OPERATION`, so the origin never left the CPU and every fragment
   tested ownership against chunk (0,0). Two isolated sandbox runs separated it: 19,126 GL
   errors with `-ChunkMask`, zero without, zero after the fix. Split into two `uniform int`s; a
   static check now fails the tier on any integer vector uniform. The tens of thousands of
   per-frame GL errors in every client log had been present since 0.3.0 and read as somebody
   else's problem.

4. **Three ownership rules, all aimed at a healthy subsystem.** In sequence: deny ownership to a
   chunk holding no mesh (0.3.8); read the culler's per-frame verdict `CullVisible[bufIndex]`
   instead of the drawn counter (0.3.10, G43, prompted by the user establishing in unmodded
   vanilla that flying high stops the engine drawing the ground directly beneath); and clear the
   atlas column when a ring slot changes hands (0.3.13, G44 - `VanillaReadinessMask.ClearColumn`
   had existed since the feature was written, documented as required, with no callers). The
   third was a genuine defect with a genuine regression test. None of them closed the band.

5. **The user's experiments did the discriminating, not the reasoning.** `.vhgeom off` changed
   nothing, retiring mesh presence. `.vhskip off` changed nothing, retiring whole-mesh skipping.
   `.vhholes` with the mask on reported every owned cell as drawn while holes remained -
   tautological, since the 0.3.8 rule already excluded non-drawn cells - but with the mask off it
   reported a non-zero count and no holes, which established that ownership selection was correct
   and the fault lay in applying it.

6. **`.vhpaint` ended the arguing.** Painting discarded fragments red instead of discarding them
   turned an ambiguous gap into a yes/no question, and the whole band lit up. That proved the
   mask was suppressing it while every CPU structure was correct.

7. **Air was excluded from the mask (0.3.15), and the band survived.** The reasoning is sound and
   is retained: cached terrain overshoots the real surface, that geometry lies in empty chunks,
   vanilla draws nothing there, so discarding removes the only draw. Air now stays owned in the
   tracker - preserving the column aggregate and the radial handoff, so 0.3.3 cannot recur - and
   never reaches the atlas texel. It was not sufficient.

8. **Where it stands.** The hidden cells are not air, are owned, and the engine is drawing
   something in them. The remaining candidate is granularity: one boolean per 32x32x32 cube
   cannot express "the engine covers the ground up to this height here", and at the horizon the
   cached approximation's vertical error is largest. If that is right it is a redesign - a
   per-column surface height compared against the fragment's own height - not a patch. The user's
   closing direction is that populating those cells with cached terrain should be possible, which
   points the same way.

---

## Delivered

Source, 0.3.5 through 0.3.15, all behind `.vhmask` except the startup fix:

- `.vhwhy` duplicate registration fixed; coarse-draw report renamed `.vhcoarse`.
- `maskSectionOrigin` split into two integer uniforms; the per-fragment mask functions for the
  first time.
- `VanillaChunkGeometry`: mesh residency by reflection over `ClientChunk`'s internal pool
  location arrays, the culler's `CullVisible[bufIndex]` verdict, `Hide`, and a combined
  `TryIsVanillaDrawing`; all bindings asserted against the installed assembly.
- `VanillaRenderReadiness.ColumnEvicted`, raised from the single `ClearSlot` funnel; the renderer
  clears the matching atlas column.
- Atlas resynchronised from committed tracker state once per second, with a counter, so a mirror
  desync is bounded to one second rather than being permanent.
- Air excluded from the mask texel while remaining owned in the tracker.
- Commands: `.vhcoarse`, `.vhgeom`, `.vhskip`, `.vhholes`, `.vhpaint`; `.vhwhy` searches to the
  full draw distance rather than 512 blocks and reports every engine signal.

Checks: 1,349 Release assertions, 1,381 documentation checks. New static guards for duplicate
command names, integer vector uniforms, and control characters in source; new runtime guards for
the geometry bindings and for atlas column eviction.

Tooling: `scripts/package.sh` resolves `python` where `python3` is absent; `bench-windows.ps1`
rejects a label containing characters the bench mod rewrites, which previously cost a
five-minute timeout per run.

Documentation: G41 (duplicate command names), G42 (integer uniforms), G43 (the culler is the only
drawing signal), G44 (mirrors of a wrapped ring), and two corrections to G40.

## Decisions

- Read the game's IL rather than reason about its API. Every one of this session's confirmed
  findings came from that or from the picture; none came from reasoning about the mod.
- Keep every ownership rule gated on `ChunkMaskEnabled`, and keep air owned in the tracker while
  excluding it from the mask. The column aggregate feeds the radial handoff, which is the default
  path, and 0.3.3 proved what happens when it collapses.
- Prefer a runtime toggle over another build when a hypothesis needs an A/B. `.vhgeom`, `.vhskip`
  and `.vhpaint` each converted a disputed theory into one observation.
- Accept overlap over holes where the two conflict, per the standing product preference.
- Rebuild the GPU mirror from the authority on a timer rather than continue hunting for missing
  incremental update paths.

## Traps

- **Trigger:** a diagnostic added to a hot loop. **Failure:** the drawn-without-geometry counter
  called `GetChunk` for every probe, taking the client's chunk lock that the loader threads also
  want, worst while a world is coming up. **Safer:** restrict such a lookup to the observations
  that can change a decision, and treat a join-time slowdown as a suspect for any diagnostic
  added since the last known-good join.
- **Trigger:** enabling `glDebugMode` in `clientsettings.json` to identify a GL error.
  **Failure:** the debug callback throws on a non-debug context and crashes the client, leaving
  `VSCrashReporter.exe` holding `launch.log` - which blocks the next run's log rotation - and the
  sandbox server up. **Safer:** expect the crash, take the stack (it names the offending call,
  which is what you wanted), then clean up with `scripts/test-stop.ps1` and kill the crash
  reporter after proving its command line names the sandbox.
- **Trigger:** writing an escape sequence into a file through a heredoc reaching python or perl.
  **Failure:** one level of escaping is eaten, so a word-boundary escape becomes a literal
  backspace and a tab escape a literal tab, invisible in a terminal and silently changing what a
  regex matches. It happened twice, once inside a memory file and once inside a check.
  **Safer:** build such strings from `chr(92)`, and
  `StaticAssetChecks.SourceHasNoControlCharacters` now fails the tier on any control character in
  source.
- **Trigger:** concluding from CPU-side diagnostics that a rendering subsystem is healthy.
  **Failure:** `.vhholes`, `.vhwhy`, the ownership audit and the column aggregate were all correct
  and all reported health while a band of world was missing, because the fault was in the GPU
  mirror and then in the resolution of the model itself. **Safer:** make the picture answer.
  `.vhpaint` settled in one observation what six builds of reasoning had not.

## Flagged and unverified

Judgement calls awaiting human review:

- Whether the per-cell mask is worth continuing at all. It has cost this entire session and
  bought a 7.3% frame rate in one stationary pair; the radial handoff has no holes. Shelving it
  is a legitimate outcome and was offered.
- The accepted overlap where the cached approximation overshoots real ground.
- The once-per-second atlas resync trades a fixed cost for a bounded desync; the cost is
  unmeasured.

Claims lacking their evidence level:

- The band is unexplained. The coarse-cube granularity theory is the only candidate consistent
  with all observations and has no evidence of its own.
- Cached terrain is slow to appear after joining: 100 fill-in meshes at 36.4 s against 6.1 s on
  0.3.4, same cache size and manifest. One diagnostic's lock traffic is a suspect. No measurement
  since.
- No benchmark was run after 0.3.9. Frame-rate effects of the culler-based rule, the resync, and
  the air exclusion are all unmeasured.
- 19,126 GL errors with the mask, zero without, zero after the fix: one pair of sandbox runs on
  one stationary scene.
