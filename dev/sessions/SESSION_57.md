# Session 57 — Subtree bounds answered, cave culling built and defective

**Date:** `2026-08-26`
**Branch/commit:** `cave-culling`, branched from `render-overhaul`
**Mod version:** `0.3.106` through `0.3.113`
**Assist protocol / blob / schema:** `1 / 4 / 6` (unchanged; nothing here touches storage)

> **Handoff note.** This session ends with a defect that is understood in its symptoms and
> not in its cause. Section 5 and the TODO entry are written to be picked up cold by
> somebody who was not here. Read those two first.

## Context and investigation

Opened on `dev/TODO.md` item 1, aggregate vertical bounds for quadtree subtrees, which was
the highest-recommended unfunded renderer optimisation. That work was built, measured in
game, and found marginal. Following its measurements outward - why are the culling boxes
half the height of the world? - led to the owner observing, with no-clip, that the mod
renders every buried cave in range. That became the rest of the session: an offline study of
how much of the cached terrain is sealed underground, a rule for removing it, and a first
implementation that is currently making things worse rather than better.

Out of scope and untouched: storage, protocol, the GPU arena and indirect paths, and the
established culling switches.

## Work narrative

### 1. Aggregate vertical bounds (TODO item 1)

`LodTraversalPolicy.NodeInView` bounded whole quadtree SUBTREES from bedrock to sky, while
Phase 3b had given individual sections real mesh heights. `LodSubtreeHeights` now maintains,
per node, the union of the vertical extents of every mesh resident beneath it, plus a count
of those meshes and a flag for any that failed to report bounds. Maintenance is a bottom-up
recompute of the changed node's ancestor chain - at most seven nodes of four child lookups -
on every mesh publish and removal. Recomputing rather than accumulating is what makes removal
exact, since a union has no inverse.

The aggregate covers meshes that are RESIDENT, not data that exists. That is safe because
only a node with a mesh is ever drawn, and it cannot starve anything: mesh demand comes from
the orientation-independent radial planner, not from this walk (G8).

**One call site was deliberately left alone.** `AllVisibleChildrenCovered` does not decide
what is drawn; it decides whether a parent may STOP drawing its own coarse mesh over a
quadrant. The aggregate describes the fine meshes under a child, not the parent's coarser
rendition of the same ground, and those have different vertical extents. A child holding
nothing but a deep cave mesh would be rejected vertically, the parent would descend believing
the quadrant covered, and the surface it had been drawing there would become a hole. That
gate keeps the full-height box, and `StaticAssetChecks.SubtreeBoundsStayOutOfTheCoverageGate`
pins the decision because passing the span there looks like an obvious consistency fix.

**Measured in game across five views** (0.3.108, `.vhsubtree reset` before each):

| view | nodes rejected | meshes inside them | per node | worst frame |
|---|---:|---:|---:|---:|
| a | 18,258 | 20,326 | 1.1 | 5 |
| b | 9,717 | 9,717 | 1.0 | 3 |
| c | 87,024 | 1,430,122 | **16.4** | 131 |
| d | 63,846 | 63,846 | 1.0 | 9 |
| e | 58,506 | 147,658 | 2.5 | 21 |

At about 470 fps those totals are **2 to 20 nodes rejected per frame**, against 33-74 the
walk already rejected horizontally.

**The finding that closes the item.** For a LEAF, the aggregate box is that section's own
mesh span, and the per-section box shipped in 0.3.58 is a tighter version of the same thing
split per pass. The aggregate box therefore CONTAINS the per-section box, and can only ever
reject a subset of what the per-section test already rejects. So a leaf-level subtree
rejection removes a draw that was not happening anyway. Three of the five views show a ratio
of about 1.0 mesh per rejected node - all leaves, no saving. The one view that rejected real
branches (16.4 meshes per node) drew 2 sections total, i.e. the camera was pointed at sky.

The ceiling was never checked before the work started, and should have been: the quadtree
walk costs **44.3 us average of a ~2000 us frame** (p95 100, p99 225), against 239 us for
draw submission. Deleting the entire walk would be about 2% of frame time.

### 2. Why the boxes are so tall, and the disproved theory

`ReportLiveSectionHeights` was added because the periodic `section heights:` line measures
the meshes PUBLISHED in an interval, which is empty once a world settles, and the only
interval it had ever reported was the first thirty seconds after joining - the noisiest
possible sample.

Settled reading (0.3.109, two identical readings 39 s apart):

```
1713 meshes, mean 188.9 blocks tall (49.2% of the 384-block world)
mean floor y=19.7, mean ceiling y=208.7
floors: y0-8 37%, y8-32 44%, y32-64 11%, y64-128 6%, y128+ 1%
edges: 181 meshes still carry a guessed side; 2104 seam repairs queued
```

Floors rose from y=5.7 during fill-in to y=19.7 settled, so seam repairs do work. But 81% of
floors still sit below y=32. **The phantom-wall theory was disproved**: only 10.6% of
resident meshes carry a guessed edge against 37% with floors at bedrock, so missing
neighbours cannot account for most of it. That is what sent the investigation to caves.

### 3. The cave study, offline

`LodMesher` emits a face for every run boundary nothing rests against, and a cave is exactly
a gap between two runs in a column - so buried caverns get floors, ceilings and walls. The
owner confirmed it visually with no-clip.

`tests/VintageHorizons.Checks/CaveField.cs` (`cavefield`) was built to size it against the
owner's real cache with no game process: flood air from the sky and the window edge, fill
what the flood cannot reach, and mesh again with the renderer's OWN mesher, so both sides of
the comparison come from shipping code. Its rebuild path was proven neutral - 24 of 24
sections mesh byte-identically when every column is rebuilt with nothing filled.

Results on 200 level-0 sections unless noted:

| rule | geometry removed | mean box height |
|---|---:|---:|
| connected at any distance (192-block window) | 16.5% | - |
| connected at any distance (320-block window) | 21.1% | 156 |
| daylight reaches 32 blocks (120 sections) | 34.1% | 138 |
| **daylight 32 + two ways in (120 sections)** | **30.1%** | **142** |
| everything below ground (ceiling, not shippable) | 55.0% | 145 |

**55% of all cached geometry is below the surface.** The gap between that and 30% is
geometry that is buried but reachable - the price of a conservative rule.

Two dead ends worth not repeating:

- **Depth cutoffs** were rejected by the owner: they bake in assumptions about terrain shape
  that a third-party world generator can break.
- **Straight-line sight** is the better model of visibility - light turns corners, eyes do
  not - but the measurement would not converge: 26 directions gave 51.1%, 98 gave 42.4%, 290
  gave 38.9%. Both ends are wrong in opposite directions. Too few directions miss real sight
  lines and over-remove; the denser sets step more than one cell per move and let sight slip
  through one-block walls, which under-removes. A trustworthy answer needs real ray traversal
  that visits every cell a line passes through, and it has to be PROVEN convergent. Diffuse
  light dominates straight sight at equal budget (a straight path is one of the paths light
  takes), so the diffuse rule can never remove something a straight ray could have reached -
  which is why it was chosen.

### 4. `LodCaveCull` and `.vhcaves`

Shipped in 0.3.110-0.3.113. Runs inside `LodMesher.BuildMesh` before any geometry is emitted,
returning the same snapshot instance when there is nothing to fill.

- **Daylight, 32-block reach.** Full strength falls straight down an open column; a sideways
  step costs the width of a column; buckets drained brightest-first so no cell is revisited.
- **Two ways in.** A dead end touches daylight in one patch; a passage that goes somewhere
  touches it in two, one per mouth, however long or winding. Patches are grouped among the
  frontier cells themselves - grouping through open air would call both mouths of a
  through-tunnel one way in and plug it.
- **Declines at coarse levels.** Light is measured in blocks but travels between columns, and
  a column is 64 blocks wide at L6. Below four columns of spread the rule refuses to run
  rather than read every cavity as dark.
- `.vhcaves on|off` re-meshes the world, because the switch decides what geometry EXISTS and
  a switch whose effect arrives on the next relog cannot be judged by a person.

Two real defects were found and fixed by measuring the shipped code against the cache:

1. **The working window was as wide as the light could travel.** Its outer wall is treated as
   open sky, which makes the wall a light SOURCE - so a margin equal to the reach put it
   exactly in range and flooded the section it was meant to protect. Margin is now a full
   section. 15.5% -> 17.2%.
2. **A mesh job had no diagonal neighbours.** Absent means assumed-open, so four
   section-sized blocks of imaginary sky sat against the section's corners and rescued caves
   that are genuinely dark. `MeshJob.Neighbors` now carries eight; the mesher still asks only
   the four edges about side coverage. 17.2% -> **32.7%**.

Also ruled out with measurements rather than argument: the material handed to filled rock is
correct in all 12,631 audited spans (no thin, no water, no material the column never
contained), and the wall grown where two sections disagree at a shared boundary costs 0.4%.

### 5. THE OPEN DEFECT - read this first

**In game, cave culling ADDS geometry.** From the owner's log (0.3.112), same view, waiting
for each rebuild:

```
off   1895.7 MiB   88,607,168 opaque vertices   1730 meshes
on    1949.0 MiB   91,146,636 opaque vertices   1730 meshes    (+2.5M, +2.9%)
off   1895.7 MiB   88,606,900 opaque vertices   1730 meshes
```

Reproducible, reverses cleanly, identical mesh count. The rule runs; it does the opposite of
its job.

The offline harness, on the same cache, disagrees in SIGN at every level tested:

| level | removed offline |
|---|---:|
| L0 | 32.7% |
| L1 | 24.5% |
| L2 | 21.1% |
| L3 | 10.7% |

So the harness is not modelling something the game does. Ruled out already: mesh errors
(none in the log), GPU arena failures (zero), wrong fill materials, seam walls between
sections (0.4%), and accounting drift (the second `off` reading returns to within 268
vertices of the first).

**One unexplained lead, recorded because it may be the cause.** A colour audit comparing the
two meshes vertex by vertex found **17.47% of vertices that exist at the same position in
both meshes are emitted with a different colour**. Faces at identical coordinates should not
change colour when unrelated geometry is removed. That points at the greedy merge grouping
faces differently after a fill - and different merge grouping is also a mechanism that could
raise the quad count. This was deprioritised when the owner correctly redirected to the
geometry question and was never followed up.

**The next step is already built and shipped.** 0.3.113 contains a double-build audit: with
`VINTAGEHORIZONS_CAVE_AUDIT=1` set at launch, every rebuilt section is meshed twice, with and
without culling, and `.vhcaves` reports both vertex totals. It has NOT been run. It splits the
question in two: if the audit reports geometry removed while the live total rises, the fault
is downstream of the mesher; if the audit itself reports geometry added, the mesher genuinely
emits more from a filled section and the two counts are side by side to compare.

---

## Delivered

**Source (branch `cave-culling`, uncommitted at session start, committed at session close):**

- `VintageHorizons/src/Lod/LodSubtreeHeights.cs` - per-node aggregate mesh bounds.
- `VintageHorizons/src/Lod/LodCaveCull.cs` - daylight plus two-ways-in cavity filling.
- `LodTraversalPolicy.NodeInView` takes an optional subtree span; unknown keeps the
  full-height box.
- `LodTerrainRenderer` - `SubtreeHeightCulling`, `CaveCulling`, `CaveCullReach`, `RemeshAll`,
  `ReportSubtreeCulling`, `ReportLiveSectionHeights`, subtree-cull telemetry.
- `MeshJob.CaveCullReach`; `MeshJob.Neighbors` widened to eight (four edges, four corners for
  light only).
- `LodMesher` - calls `LodCaveCull.FillUnseen`; carries the `VINTAGEHORIZONS_CAVE_AUDIT`
  double-build counters.
- Commands `.vhsubtree` (report / reset / on / off / heights) and `.vhcaves`
  (report / on / off).
- Environment overrides `VINTAGEHORIZONS_SUBTREE_HEIGHT_CULLING`,
  `VINTAGEHORIZONS_CAVE_CULLING`, `VINTAGEHORIZONS_CAVE_AUDIT`.

**Tests:** `SubtreeHeightChecks` (33 assertions), `CaveCullChecks` (14),
`SubtreeBoundsStayOutOfTheCoverageGate` in `StaticAssetChecks`, traversal additions. 5,158
assertions pass, warning-free build.

**Offline harness:** `cavefield` in the checks project - `--light`, `--twowaysin`,
`--straight`, `--rays`, `--radius`, `--level`, `--subsurface`, `--shipping`, `--verify`.

**Playable artifacts:** 0.3.106 (defective, superseded before it was run), 0.3.107, 0.3.108,
0.3.109, 0.3.110, 0.3.111, 0.3.112, 0.3.113. All verified and copy-installed. 0.3.107-0.3.109
were human-played; 0.3.110-0.3.112 were human-played with cave culling; 0.3.113 has not run.

## Decisions

- **Subtree bounds are answered, not pending.** Built, correct, and marginal for a structural
  reason: at leaves the aggregate is redundant with the per-section box, and the walk it
  speeds up is 2% of the frame. Kept because it is free and stops those leaves reaching the
  draw list.
- **The aggregate is not used in the child-coverage gate.** Hole risk; pinned by a static
  check.
- **Diffuse light over straight-line sight.** Straight sight is the better visibility model
  and worth more, but its measurement bracketed instead of converging. Diffuse light provably
  cannot remove anything a straight ray could reach.
- **Two-ways-in retained at a cost of about 4 points** (34.1% -> 30.1%), on the owner's
  requirement that a tunnel through a mountain must never be plugged. It also makes the light
  budget nearly irrelevant - 32 and 128 blocks of reach differ by 0.2 points once the rule
  carries the through-passages - so the cheap small budget is the right one.
- **Cave culling defaults OFF.** It changes what is drawn and its failure mode is missing
  terrain.
- **No chat command for subtree culling A/B**, only an environment override, matching the
  0.3.103 staging-command retirement; `.vhsubtree` exists to REPORT and to reset counters.

## Traps

- **Trigger:** adding a bound that aggregates over descendants when a per-item bound already
  exists. **Failure:** at leaves the aggregate contains the per-item box and can only reject a
  subset of what the per-item test already rejects, so the work is redundant exactly where it
  fires most often. **Safer:** check whether the new bound is tighter than the existing one
  before funding it, and measure the cost of the thing being optimised first - the quadtree
  walk was 2% of the frame.
- **Trigger:** resolving an unknown region toward "open" in a flood fill. **Failure:** open air
  is also a LIGHT SOURCE, so the conservative choice became a false illuminant; absent
  diagonal neighbours halved the saving and a window margin equal to the light's reach put
  its wall exactly in range. **Safer:** when unknown-means-open, keep the unknown boundary
  further away than the effect can travel, and supply real data for every direction the
  effect can arrive from.
- **Trigger:** an offline harness built alongside the feature it measures. **Failure:** the
  harness and the shipping code agreed with each other and both disagreed with the game by
  SIGN. **Safer:** make the harness run the shipping function (`--shipping` does this now),
  and treat a harness that has never been reconciled against an in-game number as an
  unvalidated instrument.
- **Trigger:** a direction-sampled visibility test. **Failure:** coarse sampling misses sight
  lines and over-removes; denser lattice directions step more than one cell and let sight
  pass through thin walls, under-removing. The two errors bracket rather than converge.
  **Safer:** require a convergence demonstration before quoting any number from one.
- **Trigger:** quoting a percentage measured on a harness configuration to describe what a
  person will SEE. **Failure:** "30% of geometry" was heard as "the buried caves go away";
  55% of geometry is underground, so even a perfect result leaves half the tangle. **Safer:**
  translate a measurement into the observable before offering it as an expectation.

## Flagged and unverified

**Judgement calls awaiting human review:**

- Cave culling changes what is drawn. The owner has seen it and reported the caves looking
  white; that report is unexplained (see below) and no acceptance has been given.
- The 32-block daylight reach and the two-ways-in rule are the owner's stated preference,
  measured but not visually accepted.

**Claims lacking the evidence level to be treated as established:**

- **The in-game defect in section 5 is unexplained.** Everything about the cave rule's value
  is harness-measured only, and the harness is contradicted by the game.
- **"The caves turned white."** Reported twice by the owner with before/after screenshots
  (`~/Pictures/Vintagestory/2026-08-26_09-33-04.png` and `-31.png`). Not reproduced or
  explained. The colour audit's 17.47% recoloured-surviving-vertices figure may be the same
  phenomenon and is the only lead.
- **No cost measurement for `LodCaveCull`.** It runs per mesh build on a worker thread over a
  192x192-column voxel window with pooled per-thread buffers, and has never been timed. The
  data is stored as runs, so an interval-based implementation should be far cheaper; that is
  an estimate, not a measurement.
- **Subtree bounds have no A/B.** The walk cost of 44.3 us is a with-it-on figure. No
  controlled `VINTAGEHORIZONS_SUBTREE_HEIGHT_CULLING=0` comparison was run.
- **No frame-rate evidence for any of this session's work.** The owner's screenshot showed
  457 fps while looking at every cave in the region at once, which is weak evidence against a
  large frame-rate win from removing them.
