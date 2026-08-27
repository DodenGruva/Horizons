# Session 58 — Cave culling fixed, refined, and bounded by its local view

**Date:** `2026-08-26`
**Branch/commit:** `codex/cave-culling-refinement` at base commit `efd713bc65918285412720a92d07d2301046f329`; all Session 58 source and documentation changes are uncommitted
**Mod version:** `0.3.114` and `0.3.115`, both built, verified, copy-installed, and human-run
**Assist protocol / blob / schema:** `1 / 4 / 6` (unchanged; cave decisions remain mesh-time and are not persisted)

> **Handoff note.** The original sign defect is fixed and human-proven in 0.3.114. The
> subsequent 0.3.115 refinement is correct in its narrow fixtures but adds only about 0.3
> percentage points in the owner's settled scene, while the current pass costs about 45 ms of
> worker time per section. The remaining dry and flooded cave geometry is not explained by one
> more reach constant: the classifier sees only a 3x3-section window, its "entrances" are
> light-expiration patches rather than verified surface portals, and water is occupied geometry
> rather than cavity air. Read sections 4-7 before changing `LodCaveCull` again.

## Context and investigation

Session 57 ended with the first cave-culling implementation doing the opposite of its purpose in
game. Against the same 1,730 settled meshes, `.vhcaves on` raised opaque vertices from about 88.6
million to 91.1 million even though the offline culler measured a large removal. A double-build
audit existed in 0.3.113 but had not yet been run. The session began as an investigation-only task:
read the most recent session record, source-trace the complete mesh integration, explain the sign
disagreement, and propose a proper correction without changing source.

After that diagnosis, the owner authorized the correction. The owner then tested 0.3.114, reported
that it worked, and asked whether more caves beyond natural-light reach could be removed without
damaging terrain above, cliffs, or tunnels. Two conservative follow-ons were proposed and then
authorized on a new branch:

1. apply the same rule to L4-L6 instead of declining at coarse levels; and
2. retain only the useful entrance-to-entrance backbone of a connected cave network instead of
   restoring every dark branch attached to it.

Version 0.3.115 implemented both. The owner then reported that it made little apparent difference
and that clearly submerged caves with no visible surface access remained. The final part of the
session was again investigation-only. Source, current and prior log evidence, the real-cache
`cavefield` measurements, and the owner's morning screenshots were reviewed. No source changes
were made after that report.

Out of scope and untouched: storage meanings, assist networking, database schema, terrain capture,
the GPU renderer, and the default state of cave culling. No game process was launched or stopped by
the AI. No commit, push, tag, or publication occurred.

## Work narrative

### 1. The 0.3.113 sign defect was a post-cull coverage disagreement

The double-build question was answered by source inspection and then pinned in the real shipping
path. `LodCaveCull.FillUnseen` rebuilt the current section with selected cave air replaced by opaque
terrain, but vertical face collection still read neighbouring columns from the original
`MeshJob.Self`. Two adjacent cave columns therefore disagreed about what existed:

- the column being emitted was post-cull rock;
- its in-section neighbour was read as the old cave air; and
- the mesher emitted a wall between them.

Every filled multi-column cavity could become a lattice of internal walls. The same ownership
problem continued across section boundaries: the current section was rebuilt, but the immutable
neighbour snapshot still contained the original cave. This is why an operation that filled air and
should have removed faces instead manufactured enough walls to reverse the sign in game. The
offline harness had originally pre-filled data before meshing and therefore concealed the exact
shipping integration error.

The correction introduced one internally consistent `LodCaveCull.Prepared` result per mesh build:

- `Prepared.Self` is the rebuilt current snapshot.
- All in-section neighbour reads use that rebuilt snapshot, never `job.Self`.
- The same 3x3 classification retains boundary coverage for immediately adjacent external columns.
- External side collection subtracts both the neighbour's original stored runs and any dark air
  that the shared classification says would become opaque.
- The four complete neighbours are not rebuilt again; the current pass already owns the needed
  boundary answer.

`CaveCullChecks` gained live-integration coverage for a multi-column sealed chamber and a cave that
crosses a section seam. `cavefield --shipping` was changed to hand the original snapshot to
`LodMesher.BuildMesh` with cave culling enabled, then compare it with an independently pre-filled
reference. That makes the harness exercise the integration boundary that failed rather than only
the culler and mesher in isolation.

### 2. Version 0.3.114 fixed the defect and was accepted

The focused cave suite and full fast suite passed, the Release build was warning-free, and
`vintagehorizons_0.3.114.zip` was verified and copy-installed. SHA-256:

`AED61A89F84528B52FFBC1595C1C4EF77255BD1F873A69C42B364E9BE26A7871`

The owner's settled live A/B used the same 1,730 meshes:

| state | opaque vertices | live geometry |
|---|---:|---:|
| off | 88,606,884 | 1,895.7 MiB |
| on | 66,372,948 | 1,427.7 MiB |

That is 22,233,936 fewer opaque vertices, about **25.1%**, and 468.0 MiB less live geometry. The
owner reported, "It works." The original top-priority sign defect is therefore closed. The white
cave report and the 17.47% recolour lead from Session 57 were consequences or correlates of the
invalid geometry comparison and did not remain the active diagnosis after the sign correction.

### 3. Rejected whole-problem alternative: lower every column floor

The owner considered reducing each LOD column's floor to the depth natural light can penetrate.
That would cheaply discard everything beneath a surface-derived envelope and would retain holes
where the envelope followed an opening. It was rejected after discussion because an independent
floor per column cannot distinguish hidden caves from terrain visible sideways or from below. It
can erase cliffs, arches, overhangs, ravines, tunnel walls, floating terrain, and unusual worldgen.
Making it conservative enough to protect those cases surrenders most of the saving. Do not
re-propose this as a safe equivalent to connectivity classification.

### 4. Version 0.3.115: coarse levels and passage-backbone pruning

The owner explicitly requested a new branch for the two follow-ons. The branch is
`codex/cave-culling-refinement`; it was created from the uncommitted 0.3.114 working tree, so both
versions' changes remain together and uncommitted above base `efd713bc...`.

#### 4.1 Coarse-level classification

The old rule declined when a fixed 32-block light reach covered fewer than four represented
columns, leaving L4-L6 untouched. The refinement keeps mesh-time classification and scales the
effective reach to approximately four columns at each level, capped at 253 because byte values
254 and 255 are reserved for kept and solid cells. It does not add a cache format, persisted mask,
or invalidation protocol; `.vhcaves off` remains an immediate remesh rollback.

The safety argument is deliberately limited: coarse snapshots have already made their occupancy
choice, and the culler fills only air that is still represented as enclosed at that level. The
real-cache payoff is small:

| level | samples | vertices before | vertices after | removed |
|---|---:|---:|---:|---:|
| L0 | 20 | 740,820 | 510,296 | 31.1% |
| L1 | 20 | 897,436 | 628,516 | 30.0% |
| L2 | 20 | 1,338,988 | 964,248 | 28.0% |
| L3 | 10 | 438,912 | 392,524 | 10.6% |
| L4 | 10 | 427,732 | 420,548 | 1.7% |
| L5 | 10 | 432,848 | 430,892 | 0.5% |
| L6 | 10 | 154,336 | 153,852 | 0.3% |

The coarse extension therefore works, but the levels it newly covers contain little removable
geometry in the measured cache.

#### 4.2 Passage backbone

Session 57's "two ways in" rule restored an entire dark connected component as soon as it touched
two disconnected light-frontier patches. One useful through-tunnel could rescue every unrelated
dead branch attached to it. `KeepPassageBackbones` now:

- finds each dark voxel component;
- labels its disconnected lit frontier patches using 26-neighbour connectivity;
- contracts dark cells into horizontal-column graph nodes;
- finds graph bridges with iterative Tarjan traversal;
- contracts non-bridge regions;
- builds the resulting bridge tree; and
- repeatedly removes terminal-free leaves, keeping the subtree needed to join the frontier
  terminals.

It is intentionally fail-open. Rooms, loops, wide junctions, and every ambiguous route remain.
Only a branch separated by a true graph bridge and containing no terminal is removed. The focused
fixture added for this is a one-column-wide dead branch hanging from a through-tunnel; it passes.

#### 4.3 Material safety and diagnostics

The first real-cache L3 audit found one cavity fill using water. Rebuilding now searches only for
an opaque run to extend; water, thin, and skipped materials are never borrowed. A column with no
honest opaque material remains unchanged. Re-running L3-L6 reported zero water or thin fills.

`LodMesher` now measures the classification portion of every cave-enabled mesh job. `.vhcaves`
reports completed sections plus mean and maximum worker time, and toggling resets the measurement.
This times the complete current cave pass, not the incremental cost of the new graph alone; 0.3.114
did not carry the same timer, so no claim about the refinement's isolated CPU delta is justified.

The cave suite now passes 23 assertions. The complete Release fast tier passes **5,167 assertions**
with zero failures, and the Release build is warning-free with `VINTAGE_STORY` set to the owner's
installed game. The verified package is `dist/vintagehorizons_0.3.115.zip`, 15 entries, no PDB,
SHA-256:

`69C6E8FAA2B0D3E2AFCAB3B385FC6E68A4398B507167A890FC425F194FEB9671`

It was copied to `%APPDATA%/VintagestoryData/Mods` beside all older rollback zips. No existing zip
was removed or overwritten.

### 5. The 0.3.115 live result is valid and marginal

The current client log confirms that Vintage Story selected and loaded 0.3.115. The first `off`
reading at 1,564 meshes was not settled and must not be compared. The valid controlled pair used
the same 1,713 meshes:

| state | opaque vertices | live geometry | cave worker timing |
|---|---:|---:|---:|
| off | 86,884,340 | 1,858.7 MiB | no completed sections |
| on | 64,791,952 | 1,393.7 MiB | 1,713 sections, 44.984 ms mean, 149.648 ms max |

That is 22,092,388 fewer opaque vertices (**25.43%**) and 465.0 MiB less live geometry
(**25.02%**). The 0.3.114 run removed about 25.1% of opaque vertices. Scene population differs
slightly between runs, so absolute totals are not directly comparable, but the normalized result
supports the owner's observation: the refinement added only roughly **0.3 percentage points**.

The timer represents about 77 seconds of aggregate worker CPU over the 1,713-section rebuild. Work
runs in parallel, so wall time is lower and no direct main-thread hitch follows from this number.
It also includes the pre-existing flood and rebuild, so it cannot be attributed wholly to the
0.3.115 graph. It is nevertheless the first honest cost measurement of the current pass and is
large relative to the marginal added removal.

The log contains zero capture/mesh/mip worker errors and zero GPU arena failures. Later vanilla
block-entity dictionary errors are unrelated. Initial shader include/link attempts occurred before
asset readiness and later compiled normally.

### 6. Why dry caves with no apparent surface access still survive

Water is not the complete answer. A genuinely isolated dry pocket that is wholly represented
inside a complete 3x3 working set, has an opaque fill material in its columns, and touches no lit
frontier should be filled by the current code. A survivor means at least one conservative escape
condition was encountered. The dominant conditions are structural:

#### 6.1 A local window cannot prove global enclosure

Every section classifies through only itself and eight neighbours. The outer wall and any missing
or uncaptured column are resolved as open air. The full-section margin prevents that false light
from directly reaching the centre section, but it cannot answer where a cave goes beyond the
window. A large cave network crossing two portions of the window looks exactly like a through-
tunnel entering and leaving unknown space. It receives multiple frontier terminals and is kept,
even if the complete network is globally sealed and never reaches the real surface.

The window recentres for every mesh job. A cave system larger than the window can therefore escape
every local decision. The owner's screenshots show enormous connected networks spanning far more
than three sections, matching this failure mode. This is a deliberate fail-open limitation, not a
constant that can be safely tuned away.

#### 6.2 Frontier patches are not verified entrances

The terminal labels are disconnected patches where simulated light meets the dark component. They
do not carry the identity of an actual surface mouth. One complicated or obstructed opening can
produce multiple disconnected light-expiration patches and be mistaken for two entrances. Unknown
window edges can produce them as well. The rule then preserves a route between abstract frontier
patches, not necessarily between two genuine surface portals.

#### 6.3 Bridge pruning is weak on natural volumetric caves

The algorithm removes only terminal-free bridge-separated leaves. Natural caves are wide, contain
parallel column connections, and form loops. Those redundant connections make most edges
non-bridges, so a wide dead branch belongs to an ambiguous cyclic component and stays. The focused
fixture proves the narrow bridge case, not the distribution of topology in real worldgen.

Contracting every dark height in one horizontal column into one graph node is additionally
conservative. Within one already-connected cave component, vertically separate lobes that overlap
in X/Z can be collapsed together and create graph shortcuts or cycles. It cannot merge two
entirely disconnected voxel components because graph construction is performed per component, but
it can still make branches inside one large system look less separable than they are.

#### 6.4 Coarse snapshots can alter connectivity

Mip construction has already reduced multiple fine columns to a coarse occupancy choice. Narrow
rock dividers or openings may disappear or merge at L1-L6. The cave pass can only reason about that
representation. This is another reason the coarse extension must remain conservative, and its
measured L4-L6 saving is too small to account for much visible cleanup.

### 7. Why flooded and partially flooded caves survive

Capture reads `BlockLayersAccess.FluidOrSolid`, so water is stored as a run and carries
`FlagWater`. `BuildOccupancy` marks every run as occupied/solid for the air-light flood. Therefore:

- a water-filled volume is not dark cavity air;
- the air culler never replaces or suppresses the water cells;
- the mesher retains water geometry and the rock faces that remain visible through it; and
- a partially flooded cave can lose some dark air while its flooded portion remains.

The opaque-material safeguard can also leave a dry-looking gap above or beside water when that
coarse column contains water/thin/skip runs but no opaque terrain to extend. This is an intentional
fail-open against translucent cave plugs and foreign material, not evidence that water explains
every survivor.

### 8. Recommended architecture for the remaining problem

Do not keep extending the per-mesh 3x3 bridge analysis. Reliable classification requires context
that survives section boundaries:

1. Build a background connectivity graph across known cached L0 sections.
2. Represent maximal vertical air spans as nodes; connect spans in adjacent columns only where
   their Y ranges overlap. This preserves distinct cave layers and avoids whole-column shortcuts.
3. Label genuine surface opening components as portal identities. Propagate source identity with
   daylight so multiple expiration patches from one mouth remain one terminal.
4. Treat an unknown cache frontier as visible/fail-open until enough data exists. A component fully
   enclosed by known opaque terrain and carrying no real portal can be culled globally.
5. For components with multiple real portals, retain graph routes needed to connect different
   portals and prune dark branches outside them. Validate this on wide rooms, loops, overlapping
   cave levels, cliffs, arches, ravines, and through-tunnels—not only a one-cell branch.
6. Classify fluid separately. Keep any water component connected to an exposed ocean/lake or an
   unknown frontier; only a completely enclosed fluid component is eligible to be absorbed or have
   its internal geometry suppressed.
7. Derive conservative masks for L1-L6 from the L0 answer. A coarse cell should be removable only
   when all contributing fine evidence supports removal and a valid opaque fill material exists.
8. Version and invalidate the derived classification explicitly, or keep it outside canonical
   storage with a fail-open recomputation lifecycle. Do not silently make old cache rows mean
   something new.

Before funding that implementation, add reason telemetry over the real cache and live remesh:
cells/vertices kept by daylight, kept by a true portal, kept by unknown boundary, kept by multiple
terminals/backbone ambiguity, retained water, and refused for lack of opaque fill. Without those
counts, screenshots can show a survivor but cannot say which safety rule retained it.

---

## Delivered

**0.3.114 source and integration correction:**

- `LodCaveCull.Prepared`: one shared post-cull snapshot and boundary classification.
- `LodMesher`: uses the rebuilt self snapshot for every internal neighbour comparison and folds
  matching cave-fill coverage into external side subtraction.
- `CaveField --shipping`: now exercises `LodMesher.BuildMesh` with culling enabled rather than
  pre-filling away the integration boundary.
- `CaveCullChecks`: multi-column live-mesh and cross-section seam regressions.
- Human-validated settled reduction: about 25.1% of opaque vertices and 468 MiB at 1,730 meshes.
- Verified, copy-installed 0.3.114 package; SHA-256
  `AED61A89F84528B52FFBC1595C1C4EF77255BD1F873A69C42B364E9BE26A7871`.

**0.3.115 experimental refinement:**

- `LodCaveCull`: effective coarse-level reach; terminal-to-terminal bridge-tree pruning; opaque-only
  fill safeguard.
- `LodMesher` and `.vhcaves`: worker mean/max timing and reset.
- `CaveCullChecks`: dead-branch, L6 sealed/open/through, and water-only fail-open coverage; 23 cave
  assertions.
- Complete fast tier: 5,167 assertions, zero failures; warning-free Release build.
- Verified, copy-installed 0.3.115 package; SHA-256
  `69C6E8FAA2B0D3E2AFCAB3B385FC6E68A4398B507167A890FC425F194FEB9671`.
- Human-observed result: works but makes little additional visible difference; dry and flooded
  underground systems remain.

**Documentation:** Session 58, session index, changelog, cave TODO, completed-history record,
current status, and G107-G110. No wire-history or architecture edit: compatibility did not change,
and the proposed global classifier is not yet an accepted durable design.

## Decisions

- **The 0.3.113 sign defect is closed.** It was a mixed pre-/post-cull coverage view in the mesher,
  fixed and human-proven in 0.3.114. Do not reopen the colour/greedy-merge lead as the primary cause
  without new evidence.
- **The column-floor alternative remains rejected.** It cannot safely distinguish hidden cavities
  from cliffs, arches, overhangs, ravines, tunnels, floating terrain, or third-party worldgen.
- **The 0.3.115 refinement is not accepted as the final solution.** It passes its fixtures and is
  safe by construction, but the live gain is marginal and the remaining problem lies outside its
  local information. Whether to retain its code while developing the next design or revert to the
  simpler 0.3.114 behavior is still an owner decision; no merge or commit has been requested.
- **Do not treat the 45 ms timing as the refinement's isolated overhead.** It is the whole cave
  preparation pass. It is valid current cost evidence and invalid attribution evidence.
- **Water requires its own visibility/connectivity semantics.** Treating it as air would let light
  leak through oceans; treating it as opaque forever leaves enclosed flooded caves. A separate
  exterior-connected fluid classification is the safe direction.
- **Cave culling stays default-off.** The original defect is fixed, but the feature changes terrain
  geometry, the refined behavior is not accepted, and large unknown-connected systems remain.

## Traps

- **Trigger:** rebuild one side of a geometry comparison and leave the other side canonical.
  **Failure:** filled cave columns were compared with original cave-air neighbours, manufacturing
  internal and cross-section walls until culling added geometry. **Safer:** one mesh operation must
  own one coherent effective-coverage view for current and adjacent cells.
- **Trigger:** infer global enclosure from a bounded fail-open window. **Failure:** a globally
  sealed cave crossing two window edges is indistinguishable from a useful through-tunnel and is
  retained in every recentered window. **Safer:** make the classification cross-section/global, or
  state plainly that large components are unknowable and retained.
- **Trigger:** validate topology pruning with a one-cell-wide branch. **Failure:** bridge pruning
  looks effective in the fixture while wide natural caves form cycles and almost never expose a
  removable bridge. **Safer:** measure the distribution of removal reasons in real terrain and add
  wide/looped/vertically overlapping fixtures before funding the algorithm.
- **Trigger:** call every lit/dark contact patch an entrance. **Failure:** one complex mouth or an
  unknown boundary can create several disconnected expiration patches and falsely satisfy the
  two-entrance rule. **Safer:** carry the identity of genuine surface portals through propagation.
- **Trigger:** describe flooded caves as empty cavities. **Failure:** capture stores water as
  occupied geometry, so an air-only fill pass never sees the submerged volume as removable.
  **Safer:** model exterior-connected fluid components separately and fail open at oceans, lakes,
  openings, and unknown boundaries.
- **Trigger:** attribute a before/after CPU delta without symmetric instrumentation. **Failure:**
  0.3.115 times the whole cave pass while 0.3.114 recorded no equivalent number. **Safer:** time the
  same boundaries in both variants or add phase timers before assigning cost.

## Flagged and unverified

**Judgement calls awaiting human review:**

- Whether to keep the 0.3.115 coarse/backbone experiment in the branch or restore the simpler
  0.3.114 behavior before pursuing a global classifier.
- Whether global portal-aware air classification plus separate fluid-component classification is
  worth its storage/invalidation complexity. It is the recommended technical direction, not yet an
  approved implementation plan.
- Whether cave culling should ever become default-on. The owner accepted that 0.3.114 works but has
  not accepted the feature as a product default.

**Claims lacking the evidence level to be treated as established:**

- The exact retention reason for each cave the owner saw. Current logs report aggregate geometry
  and timing, not classification reasons or coordinates. The 3x3-window/global-network explanation
  is strongly supported by source and the screenshots' scale but is not tagged per surviving cave.
- The incremental CPU cost of coarse classification and bridge pruning. Only 0.3.115 measures the
  complete pass.
- Performance impact on FPS or frame pacing. Geometry and worker time were measured; no controlled
  frame-rate comparison was run.
- Correctness and payoff of the proposed global graph, portal identities, fluid pass, or derived
  coarse masks. None is implemented.
- Multiplayer, changing-terrain invalidation, cache-frontier convergence, and third-party worldgen
  behavior for any future global classification.
