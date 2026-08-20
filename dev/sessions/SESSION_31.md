# Session 31 — The water seams were geometry, not colour

**Date:** `2026-08-20`
**Branch/commit:** `codex/chunk-aware-mask`
**Mod version:** `0.3.23`
**Assist protocol / blob / schema:** unchanged

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The owner opened the session by correcting the record: `dev/TODO.md` carried the water
problem as "cached water shows the boundary of every chunk, with colour differing across
those boundaries", and colour was never the symptom. The actual report is a visible
**vertical seam standing at every cached water chunk boundary** on a large ocean.

That correction was the whole session. The entry's three candidates — the per-cell mask
drawing water only in unowned cells, the per-section water tint, and cached water
double-blending against vanilla water at 66% alpha — are all colour explanations, and the
cheap experiment it recommended (`.vhmask off`) could not have separated anything.

Investigation was entirely offline, per the standing rule that a rendering question is
answered from the cache and the source rather than from a launch.

## Work narrative

### 1. Reading the mesher first

`LodMesher.CollectSide` culls a wall against the neighbouring column's runs, and takes the
neighbour from `MeshJob.Neighbors[dir]`. A null entry yields `Span<ulong>.Empty`, every run
stays uncovered, and the entire edge emits a wall. That is the documented frontier rule and
it is correct at the edge of explored space.

Tracing where `Neighbors` is filled found the fault immediately.
`LodTerrainRenderer.ScheduleMeshJobs` resolved each neighbour with
`world.Sections.TryGetValue` — **residency** — while the shader's `openEdges`, forty lines
away in the same file, asks `world.HasDataSet` — **existence**. The two had been answering
different questions since the mesher was written.

### 2. Why that reaches every boundary

Three facts compound:

- `LodRenderDirtyScheduler` is a nearest-first index, so sections are loaded and meshed in
  radial order. A section's outward neighbour is by construction scheduled later, and is
  routinely still in flight when the section is meshed.
- `LodWorld.InstallLoaded` deliberately marks nothing render-dirty, with a comment
  explaining why for the arriving section itself. Neighbours were never considered.
- `MarkChanged` does refresh resident neighbours, but only capture, assist install and mip
  application call it. Over an ocean the player is not standing in, none of them ever fire.

So the wall is not a transient. It is built once, on the outward side of nearly every
section, and survives for the session.

### 3. Why only water shows it

A frontier wall on solid ground is hidden: the neighbouring section's opaque surface covers
the ground beyond the boundary and the wall itself is edge-on at the shared plane. Water is
drawn at 66% alpha in a separate blended pass, so the neighbouring surface hides nothing and
the wall reads straight through it as a dark vertical line.

This is also why the fault survived: every land case the visual matrix ever covered was
concealing it.

### 4. Measuring it before changing anything

A scratch check meshed one synthetic ocean section — 64x64 columns, water from y=110 down to
a deliberately uneven seabed around y=58-62, rock below — both ways:

| neighbours | water quads | on the shared plane |
|---|---|---|
| all four present | 1 | 0 |
| one merely unloaded | 65 | 64 quads, 3,200 block² |

The uneven floor is what stops the wall merging into a single ribbon: each column's water run
bottom differs, so the seam is 64 separate quads rather than one. A flat test floor would
have understated it.

### 5. The fix, and the two it was chosen over

`MeshJob` gained `AssumedCoveredSides`, a four-bit mask the scheduler sets for any side whose
neighbour is absent from RAM but present in `HasDataSet` and not in `LoadFailed`. The mesher
treats such a side as covered **for translucent runs only**. The renderer records the mask
against the mesh it installs, and `LodWorld.SectionBecameResident` — a new callback on the
three paths where a section arrives with stored data — re-dirties exactly the neighbours that
guessed, clearing the bit as it hands the obligation over.

Rejected, and worth recording because both are the obvious first thought:

- **Dirty all four neighbours on every load.** Three lines in `InstallLoaded`, correct, and
  it roughly doubles the mesh work of a warm join. Slow join fill-in is the top open item in
  `dev/TODO.md`; spending it here would have traded a visible bug for a measured one.
- **Defer the mesh until every data-bearing neighbour is resident.** No re-mesh at all and
  always correct, but it serialises each section behind up to four loads, which is the same
  join cost by another route.

The water-only restriction is deliberate. Leaving a solid side open would open a see-through
gap at a cliff for as long as the repair takes, and a spurious solid wall costs nothing
because the neighbouring terrain hides it. Water is the reverse on both counts.

### 6. Verification

- Mesher regression check pins all four states: no wall between two loaded sections, a wall
  when the job does not say otherwise, no water wall on an assumed-covered side, and a solid
  wall retained on that same side. It also asserts the repaired mesh matches the
  loaded-neighbour mesh quad for quad.
- `KeyMathChecks` pins the `d ^ 1` opposite-side pairing the repair depends on. A wrong
  pairing there re-meshes the wrong section and the seam simply stays — a silent failure with
  no other signal.
- Full fast tier: 1,441 assertions, 0 failures. Release build clean.
- `seam repairs` added to the periodic log line.

### 7. Playtest

Packaged as 0.3.23 and installed. The owner reported the ocean seams gone and specifically
confirmed shores and cliffs, which was the one case the water-only restriction could have
broken. Human-tested.

### 8. Three questions answered from the log afterwards, at no cost

The playtest log was worth reading for more than the seam verdict. The 2026-08-20
`client-main.log` on 0.3.23 settled two items that had been sitting open for want of a
measurement nobody had taken:

- `Fill-in: 100 meshes after 6.6s`, against `36.4s` on 0.3.7 and a `6.1s` baseline on 0.3.4,
  with the same 3,016-key manifest. **The top TODO item was already fixed** - the 0.3.8
  probe-lock restriction worked and nobody had checked.
- `974 seam repairs` beside `599 meshes` at the 30-second mark. The repair path is active and
  cost no visible fill-in time. Whether it settles is still unknown: `Stats after 30s` fires
  once, so an ordinary session yields exactly one sample.

### 9. The mask's frame-rate question, settled by the owner

Asked whether toggling the mask in game and comparing FPS was sufficient. The answer given
was no for a benchmark - the only prior measurement was 0.7% at ~445 FPS, far below what a
counter resolves - but yes for ruling out a disaster.

The owner then did better than the question implied and measured TWO render distances:

| vanilla render distance | mask off | mask on | |
|---|---|---|---|
| 320 | ~315 FPS | ~310 FPS | mask costs ~1.6% |
| 1024 | ~180 FPS | ~190 FPS | mask gains ~5.6% |

Their reading - the mask helps more at range because it culls more - is directionally right
but does not explain the sign flip, which is the informative part. With the mask off, cached
terrain is suppressed inside a plain radius; with it on, the radius is pulled in to
`MaskNearFloor()` and the per-cell mask decides per fragment, so the mask submits MORE
geometry and pays a texture fetch per fragment to discard it. The saving is fragment shading.
At 320 the scene is CPU-bound and only the overhead shows; at 1024 it is GPU-bound and the
saving dominates. Promoted as G52, because a test run at one distance would have condemned
the feature.

The owner's verdict: negligible either way, looks much better, keep it. That closes the
ship/shelve decision the mask has carried since 0.3.17, and the benchmark drops from top
priority to ordinary coverage.

---

## Delivered

**Source (0.3.23):**

- `MeshJob.AssumedCoveredSides` and `MeshResult.AssumedCoveredSides`.
- `LodMesher.CollectSide` skips translucent wall emission on an assumed-covered side.
- `LodTerrainRenderer` computes the mask from `HasDataSet`, tracks it per live mesh in
  `meshedWithoutNeighbor`, and repairs on residency via `OnSectionBecameResident`.
- `LodWorld.SectionBecameResident`, fired from the three paths where a section arrives
  carrying stored data.
- `SeamRepairsQueued` telemetry, reported as `seam repairs`.

**Tests:** `MesherChecks.UnloadedNeighbourIsNotTheFrontier` plus an ocean fixture;
opposite-side pairing in `KeyMathChecks`; `Fixtures.Job` takes the mask. 1,441 assertions
pass.

**Documentation:** G51 and G52; this record; TODO and STATUS corrected away from the colour
framing; the join-fill-in and mask-benchmark items retired from top priority on measured
evidence; CHANGELOG 0.3.23.

**Repository:** master fast-forwarded from `Release 0.2.1` to 0.3.23, 56 commits covering
sessions 24-31; `codex/main-thread-performance` deleted after confirming it held nothing
unique.

**Release:** `dist/vintagehorizons_0.3.23.zip`, installed and playtested.

## Decisions

- **Ask `HasDataSet`, not `Sections`, for terrain existence.** Residency was standing in for
  existence and the two diverge constantly.
- **Repair the sides that guessed, rather than pre-empting or blanket-dirtying.** Keeps the
  cost proportional to the actual uncertainty and off the join path.
- **Permissive for water, conservative for solids.** The two fail in opposite directions.
- **No load request for an absent neighbour.** A neighbour outside the draw set is meant to
  end in nothing; forcing it resident pulls the cache into memory a ring at a time.
- **A new version rather than a repackaged 0.3.22**, so the owner can name the build that
  changed the behaviour.

## Traps

- **Trigger:** deciding whether neighbouring terrain exists during meshing.
  **Failure:** reading `Sections`, which answers residency, and walling off a data-bearing
  neighbour that simply had not loaded. Invisible on land, a permanent seam on water.
  **Safer action:** read `HasDataSet`. Promoted as G51.
- **Trigger:** a symptom recorded in the TODO in the reporter's words being re-read later as
  a diagnosis. **Failure:** "colour differing across those boundaries" hardened into three
  colour candidates and an experiment that could not have separated them, and the geometry
  was never examined. **Safer action:** keep the observation and the hypothesis visibly
  apart in the entry.
- **Trigger:** building a mesher fixture. **Failure:** a flat test floor merges the wall into
  one ribbon and understates the artefact 64-fold. **Safer action:** give the fixture the
  irregularity the real data has.

## Flagged and unverified

Judgement calls awaiting human review:

- None outstanding for this change; the owner confirmed the ocean, shores and cliffs.

Claims lacking their evidence level:

- The repair's cost is unmeasured. It is bounded by construction — one re-mesh per side that
  actually guessed — and the 974 repairs observed did not delay the 100-mesh mark, but no
  benchmark separates it and nothing has been benchmarked since 0.3.9 regardless.
- `seam repairs` has been read once, at 30 seconds. Whether it SETTLES is still unknown, and
  that is the reading that would rule out a repair re-queueing itself.
- The join fill-in recovery is one sample. `6.6s` against a `6.1s` baseline is convincing,
  but it is one join on one machine against a regression nobody has reproduced since.
- The mask frame-rate figures are the owner's in-game averages, not a controlled benchmark:
  two samples, one per condition, no alternation, and possibly including the mask texture
  rebuild on the "on" side.
- Multiplayer, other view distances and long sessions are untouched by this session, as they
  are by every session so far.
