# Session 29 — The band was terrain the engine had quietly stopped drawing

**Date:** `2026-08-19`
**Branch/commit:** `codex/chunk-aware-mask`
**Mod version:** `0.3.16`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

Session 28 closed with the band of missing terrain under `.vhmask on` unexplained after
eleven builds and five disproved explanations, and left one candidate — cube granularity —
with no evidence of its own. This session opened as a review-and-diagnose request against
that state, with an explicit instruction not to write code until the fault was understood.

The band is closed. It was neither granularity nor any of the five disproved explanations:
the engine applies a per-frame distance cull that none of the four per-chunk signals the
tracker reads reflect, and nothing in six releases of ownership work had ever consulted it.

Out of scope: the join-time residency slowdown, the cached-water seams, and the coarseness
report, all still open in `dev/TODO.md`.

## Work narrative

1. **The owner's own testing supplied the discriminator, again.** An initial diagnosis
   proposed that `WriteMask` re-poisoned the atlas with air ownership on every chunk
   crossing. That theory correctly predicted the trailing direction and the seam location,
   and it was wrong as the primary cause. The owner reported flying *backwards* with the
   band ahead of them, permanent while stationary, and worse than before — a consistent
   band where it had been splotchy. Permanence kills any theory whose mechanism is
   re-poisoning between one-second repairs, because the repair pass would heal it. That
   single report retired the theory in one exchange and redirected the search to a
   suppressor that is systematically, statically wrong rather than periodically wrong.

2. **The answer came from the engine's IL, not from reasoning about the mod.** `ilspycmd`
   over the installed 1.22.7 assemblies produced `ChunkCuller`, `ClientChunk`,
   `ChunkRenderer`, `MeshDataPoolManager`, `MeshDataPool`, `ModelDataPoolLocation`,
   `FrustumCulling`, `TesselatedChunk` and `TesselatedChunkPart`. The real per-frame draw
   test is on the pool location, not the chunk:
   `!Hide && CullVisible[VisibleBufIndex] && culler.InFrustumAndRange(sphere, …, LodLevel)`,
   and `InFrustumAndRange` ends in a horizontal distance test against a per-LOD bound that
   is at most `viewDistance² + 400`. **The engine range-culls every terrain mesh against the
   current camera, every frame, after all four chunk signals have said yes.**

3. **Why it produced exactly the reported shape.** Chunks the player travels away from stay
   loaded with every signal latched: `quantityDrawn` only rises, the mesh stays in the pool,
   `Hide` stays false, and `CullVisible` is not merely stale but *frozen* —
   `ChunkCuller.CullInvisibleChunks` early-returns while the camera stays in one chunk. So
   committed cells in the annulus between the view-distance circle and the tracked window
   edge stayed committed forever and the mask discarded cached terrain there against
   nothing. Trailing side only, because the leading side never had chunks loaded that far
   out to latch. Permanent while stationary, because standing still is precisely when
   nothing is re-evaluated. And "splotchy before, consistent now" is what session 28's own
   fixes produced: a faithful mask renders a pre-existing stale annulus as a clean band.

4. **Every CPU diagnostic had been answering with the same broken signal.** `.vhwhy`,
   `.vhholes` and the owned-but-not-drawn sweep all route through
   `VanillaChunkGeometry.TryIsVanillaDrawing`, so all three reported "the engine is drawing
   this chunk" about ground nothing was drawing. That is why session 28's instruments
   reported health throughout.

5. **Implementation was delegated to an Opus subagent and reviewed.** Its first pass
   compared the column's *nearest face* against the plain view distance and documented that
   as conservative. It is conservative in the overlap direction only. The engine measures
   from the mesh bounding-sphere centre, which `TesselatedChunk` builds as the geometry
   extents midpoint (`positionX + (xMax + xMin) / 2f`) and which therefore sits anywhere
   across the chunk's 32-block span — so a chunk whose near face is just inside the view
   distance can have its centre outside it. That left a residual permanently-stale ring up
   to `32·√2` wide: a thinner copy of the same band. Corrected to deny at
   `viewDistance − 46`, and the regression check's assertion direction was flipped to the
   no-hole property, quantified over every admissible midpoint placement. The subagent
   mutation-tested the corrected check: restoring the defective threshold fails five
   assertions.

6. **Human-tested and confirmed.** The owner reported the band closed on 0.3.16.

## Delivered

Source, 0.3.16, both behind `.vhmask`:

- Ownership denies any cell whose column lies beyond `viewDistance − 46` blocks, air
  included, with the constant derived from the in-chunk diagonal and documented on itself.
  New counter `ReadinessOwnershipDeniedBeyondViewDistance`.
- `VanillaRenderReadiness` stores a per-cell mask-exclusion bit in the existing `flags`
  array, set at publication, refreshed authoritatively by the once-per-second resync, and
  cleared by `ClearSlot` with the rest of the cell. `WriteMask` honours it, so the wholesale
  rebuild on every window change no longer reintroduces air ownership while travelling.
- `.vhwhy`, `.vhholes` and `DescribeOwnershipAt` report "beyond the N-block draw range"
  instead of "engine is drawing this chunk" for these cells.
- `scripts/bench-windows.ps1` readiness regex extended for the new counter, which would
  otherwise have silently stopped matching the log line the benchmark gate parses.

Checks: 1,383 Release assertions (readiness suite 240), 1,383 documentation checks.
New: `OwnershipStopsAtTheEnginesDrawRange` and `MaskExcludedCellsOwnGroundWithoutATexel`.

Documentation: G43 rewritten and retitled — its claim that the culler verdict was the only
drawing signal was the load-bearing error; new G45 for the probe-loop chunk lock; STATUS and
CHANGELOG regenerated around a solved band.

## Decisions

- Read the engine's IL before proposing a sixth explanation. Session 28 recorded this and
  it held again: every confirmed finding in both sessions came from the decompiled game or
  from the owner's picture, none from reasoning about the mod.
- Deny beyond range for air as well as ground, unlike the geometry rule. Beyond the view
  distance vanilla owns nothing at all, so whole columns must demote together — which is
  also what releases the section aggregates the whole-mesh skip reads. This does not repeat
  0.3.3, which collapsed because it refused air *everywhere*.
- Clear the whole chunk when choosing the threshold. Accepting up to a chunk and a half of
  overlap at the seam buys a no-hole guarantee that holds for every possible placement of
  the geometry midpoint, and overlap outranks holes by standing product preference.
- Keep both rules gated on `ChunkMaskEnabled`, leaving the benchmarked radial path
  untouched.
- Store the air exclusion in the tracker rather than passing a chunk-lookup predicate into
  the rebuild, so the wholesale path costs no chunk lock. See G45.

## Traps

- **Trigger:** a diagnosis that explains the *shape* of a symptom. **Failure:** the
  `WriteMask` air-re-poisoning theory predicted the trailing direction and the seam
  location correctly and was still not the cause; it was a real defect sitting beside the
  real one. **Safer:** check the theory against every property of the report, especially
  duration. A mechanism that operates between periodic repairs cannot produce a fault that
  survives standing still, and that one question separated them.
- **Trigger:** a subagent's own description of its safety margin. **Failure:** the first
  implementation called a nearest-face comparison conservative; it was conservative in the
  direction that costs overlap and unsafe in the direction that costs holes, leaving a
  thinner instance of the bug being fixed. **Safer:** for any threshold approximating an
  engine test, identify which direction of error produces the user-visible failure and
  derive the bound against the worst case, then assert that direction in the check.
- **Trigger:** a per-chunk signal that looks like it answers "is this drawn". **Failure:**
  six releases of ownership work on four signals, none of which is the test. Promoted into
  the rewritten G43.

## Flagged and unverified

Judgement calls awaiting human review:

- The accepted seam overlap. Cached terrain may now draw over the outermost chunk and a
  half of live vanilla terrain. The owner confirmed the band closed but has not separately
  judged whether that seam reads acceptably at the horizon.
- Whether the per-cell mask should now become the default. It works and is human-confirmed
  for the first time; the radial handoff remains the shipped path and nothing has been
  benchmarked since 0.3.9.

Claims lacking their evidence level:

- No benchmark since 0.3.9. The culler rule, the atlas resync, the air exclusion, the
  distance clause and the stored exclusion bit are all unmeasured for frame-rate effect.
- The band's closure is one human report on one machine, one world, one view distance, at
  one flight speed. The seams, flicker, cliff/water/cave/structure matrix in `dev/TODO.md`
  has not been re-run since the mask began working correctly.
- Cached terrain is still slow to appear after joining: 100 fill-in meshes at 36.4 s on
  0.3.7 against 6.1 s on 0.3.4. Untouched this session and unmeasured since 0.3.8.
