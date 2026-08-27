# Session 59 - Why the caves survived, measured, and the ceiling that route tightening opens

**Date:** `2026-08-26`
**Branch/commit:** `codex/cave-culling-refinement` at base commit `efd713bc65918285412720a92d07d2301046f329`; Sessions 58 and 59 source, test, and documentation changes are all uncommitted
**Mod version:** `0.3.116`, built, verified, copy-installed, and **not yet human-run**
**Assist protocol / blob / schema:** `1 / 4 / 6` (unchanged; nothing in this session persists a cave decision)

> **Handoff note.** This session answered the question Session 58 could only pose. Three offline
> measurement iterations over the owner's real cache established that the shipping local rule
> removes about 30.3% of estimated subterranean geometry, that a global classifier adds only about
> 5.6 percentage points on its own, and that the binding constraint was never the unknown frontier
> but the rule that keeps every route between two entrances. Bounding routes by distance from a
> real mouth-to-mouth path roughly doubles removal to about 62.9% at reach 32. The owner then
> directed that light reach expand to 64 or 128 blocks to protect overhangs, accepting a few points
> in exchange, with a target near 60%. Nothing global has been implemented. The plan for that work
> is `dev/plans/PLAN_GLOBAL_CAVE_CLASSIFIER.md`; read it before touching cave source again.

## Context and investigation

Session 58 closed with cave culling working and accepted at about 25% of opaque vertices in game,
but with the owner reporting that large dry and flooded cave systems remained visible, and that
branches hanging off caves which do open to the surface also remained. It proposed a global
classifier and, correctly, refused to fund it without evidence: the aggregate `.vhcaves` line
could show that caves survived but not *which* safety rule had saved them.

This session began as a review of that record and of the shipping culler, then became a funded
measurement programme. The owner's standing priority was restated and honoured throughout: visual
fidelity of real terrain - tunnels, cliffs, overhangs - outranks the saving.

Every figure below is **harness-measured** against the owner's real client cache (4,637 level-0
sections, 18,425,190 captured columns, decoded into 27,456,106 air spans, 5,439,699 water spans and
51,310,867 graph edges) and each was independently reproduced by a separate verification run of the
same command before being recorded here. No global classification has ever run in game.

Out of scope and untouched: capture, storage format, assist networking, the GPU renderer, vanilla
handoff, and the default state of cave culling. No game process was launched. No commit, push, tag,
or publication occurred.

## Work narrative

### 1. Retention telemetry, so a survivor can name its own reason (0.3.116)

The first deliverable was diagnostic, not behavioural. `LodCaveCull` now carries a per-cell `Marks`
byte beside its light values - one byte, pooled with the existing workspace - recording whether the
light that reached a cell entered from unknown territory, and whether a dark component was retained
by a terminal of that kind. After classification, `Account()` walks **only the centre section** of
the 3x3 window (the window recentres per mesh job, so counting the margin would count the same
terrain nine times) and tallies six reasons:

| reason | meaning |
|---|---|
| REMOVED dark and filled | the success case |
| kept lit by daylight | real light reached it inside the budget |
| kept backbone, known ends | on a route between terminals we actually hold terrain for |
| kept backbone, unknown end | at least one terminal is light invented at the window wall |
| kept no opaque fill | removable, but the column has no honest rock to extend |
| water never classified | fluid: occupied geometry the air pass cannot see |

Each row reports cells, share, a face-area estimate rendered as `~vertices`, and cells per section.
`.vhcaves` with no argument writes the block to the **client log** under `caves:`; chat keeps the
one-line summary and points at the log, because the owner cannot copy game chat and the periodic
report fires once at 30 seconds. `.vhcaves on|off` resets the tally beside the existing timing
reset.

The safety-critical property is that this changes no decision. One ordering change was needed - the
window walls now seed before the sky - and it seeds the same cells to the same values, deciding only
which provenance a shared corner records. Thirteen new assertions in `CaveCullChecks` pin exact
counts for a sealed bubble, a water-only column, route-versus-unknown attribution, and the reset.

Version 0.3.116 was built warning-free, packaged, verified, and copy-installed beside the owner's
rollback set. SHA-256:

`AE3291B9CA12A0EC5583A197A284B232630DB75AE287767F12A1046A8A15F7D1`

**It has not been run in game.** Its live cost was not isolated either: an offline A/B put the
accounting inside run-to-run noise, and the honest estimate of roughly 1 ms against Session 58's
44.984 ms mean is arithmetic from the work it does, not a measurement.

### 2. Iteration 1 - the global span-graph prototype, and the undecidable bucket

`tests/VintageHorizons.Checks/CaveSpanGraph.cs` implements the architecture Session 58 recommended,
offline and without a budget, purely to price it. Nodes are maximal vertical air gaps between stored
runs per column - never whole columns, which is what merges stacked cave layers and makes dead
branches look cyclic (G109). Edges join spans in 4-adjacent columns whose Y ranges overlap. Exterior
is the air above each captured column's top run; contiguous portal-adjacent spans form **one**
portal identity, so a single ragged mouth cannot read as several entrances (G108). Daylight floods
from portals only, carrying portal identity. Fluid gets its own graph.

| | share of estimated geometry |
|---|---:|
| Removed by the shipping local rule | **30.3%** |
| Removable by the global prototype | 30.8% |
| Additional over the local rule | **+5.4 pp** |
| Both together | 35.6% |
| Kept as undecidable "unknown frontier" | **29.5%** |

The undecidable share was the finding, and it is a scale effect rather than a frontier effect. Only
0.2% of captured columns sit beside something uncaptured, but "unknown" is a property of a whole
component and the components are enormous: 386,066 air components, of which **the largest holds
18,466,178 of 27,456,106 spans**. One uncaptured column anywhere taints an entire network.

### 3. Iteration 2 - pseudo-portals removed the bucket and gained 0.2 points

The shipping rule already treats its window wall and every uncaptured column as a light *source* and
still fills dark air beyond that light's reach. Applying the same accepted stance globally - every
frontier contact becomes a pseudo-portal that lights and terminates routes, with contiguous contacts
grouped into one identity - eliminates the undecidable verdict entirely.

| | Open frontier | Portal frontier |
|---|---:|---:|
| Global removable | 30.8% | 31.4% |
| Additional over local | +5.4 pp | **+5.6 pp** |
| Both together | 35.6% | 35.8% |
| Route between real portals (kept) | 9.3% | **34.2%** |
| Unknown frontier (kept) | 29.5% | gone |

The 29.5% did not become removal. It became **route retention**. Those components were never
undecidable *because* they hug the frontier; they are colossal networks that touch it somewhere and
also carry many genuine surface mouths, so removing the excuse simply moved them into the
multi-portal branch, where the bridge-tree peel keeps the whole 2-edge-connected core. Net gain of
the entire frontier change: **+0.2 pp**. Frontier-caused retention became visible and small - 2.3%
lit via a pseudo-portal, 0.8% routed to one.

Attribution is exact rather than inferred: the flood and the peel each run twice, once from real
mouths only and once from all terminals, and a span kept by the second and not the first is charged
to the frontier. Ties go to the real mouth, which under-reports frontier influence and never
over-reports it.

### 4. Iteration 3 - route tightening, and the ceiling it opens

If route retention is the binding constraint, the question is whether those routes deserve keeping.
The visual argument for keeping a route is that a player might see daylight through it, and sight
travels in straight lines, so a winding passage whose middle is far from both mouths shows nothing
of either. Two candidate rules were implemented and swept:

- **Corridor (W):** keep a route span only within slack `W` of a shortest path between two of its
  nearby mouths, using the same cost metric as the light flood.
- **Sightline (k):** a mouth pair earns a route only when the passage between them is at most `k`
  times the straight-line distance.

Whole cache, portal frontier, reach 32, four terminal labels:

| route rule | removable | route kept | additional over local | both together |
|---|---:|---:|---:|---:|
| bridge (baseline) | 31.4% | 34.9% | 5.6% | 35.8% |
| W=0 | 64.1% | 2.4% | 34.7% | 65.0% |
| W=16 | 63.3% | 3.2% | 33.9% | 64.2% |
| **W=32** | **62.6%** | 3.9% | **33.2%** | **63.5%** |
| W=64 | 61.1% | 5.4% | 31.9% | 62.1% |
| W=128 | 56.1% | 10.3% | 28.3% | 58.6% |
| k=1.5 (sightline only) | 31.6% | 34.8% | 5.7% | 35.9% |
| k=3.0 (sightline only) | 31.5% | 34.9% | 5.6% | 35.8% |
| **W=32 k=2.0** | **62.9%** | 3.6% | **33.5%** | **63.8%** |

The corridor rule roughly **doubles** removal, and the curve **flattens below W ~= 32**: W=128->64
gains 5.0 pp, 64->32 gains 1.5, 32->16 gains 0.7, 16->0 gains 0.8. W=32 is one light-reach of slack
and is the natural stopping point; W=64 is the cautious one. The sightline rule is worth about
0.2 pp and was dropped from the runtime design, its fixtures kept for the record.

What route retention actually was, then, is **deep interior**: regions hundreds of blocks from any
mouth, kept only because they were connected to something that had two mouths. Distance from a real
path sees that immediately, and a cycle does not fool it - which is G109 answered directly.

A second result matters as much as the headline. The disagreement between the two rules - cells the
shipping local rule fills that the global rule keeps - **collapses from 3,018,736 to about 620,000,
a 79% reduction**. Under the corridor rule the two stop disagreeing about what is safe and differ
only in reach. This is evidence that corridor tightening is not a more dangerous *kind* of rule than
the one the owner already accepted in 0.3.114.

### 5. The owner's directive: reach expands for overhangs

On reviewing the sweep the owner directed that light reach expand from 32 to **64 or 128 blocks**,
to ensure large surface overhangs are not cut off prematurely, and accepted losing a few points as
long as removal stays **close to 60%**.

The mechanism deserves stating because it decides what "safe" means for the remaining work. A deep
rock shelf leaves an air gap in the columns beneath it. The outermost gap touches open air beside
the shelf and is lit. Gaps further under the shelf are further from that one portal and, past the
reach, they go dark - and a dark span in a component with a single portal is classified removable
and filled. At reach 32 the underside of any overhang deeper than about 32 blocks is therefore
removable, which is visible damage to exactly the terrain the owner ranks highest.

Two consequences follow, both recorded in the plan:

- **The corridor rule cannot help or hurt overhangs.** Under-shelf air is single-portal, so it never
  reaches the route rule. Reach is the only lever, and the coming sweep is pricing one knob, not
  balancing two.
- **Reach 128 is incompatible with the live 3x3 window rule at L0** (see Traps below).

### 6. The plan document

`dev/plans/PLAN_GLOBAL_CAVE_CLASSIFIER.md` was written as a cold-start handoff: current state with
evidence levels, all three measurement tables with reproduction commands, Phase 1 as a hard
measurement gate (reach 64/128 sweep, closing the label bracket, a required deep-overhang fixture),
Phase 2's runtime architecture (background classifier, RAM-resident revision-keyed masks, never
persisted, fail-open invalidation, mask-absent means not culled), the hard constraints, acceptance
gates including the owner's in-game protocol, and the open owner decisions.

It was reviewed against the verified numbers and against source: `LodWorld.ContentRevision` exists
as cited, the cache path and `scripts/package.sh` are correct, and the reach-64 borderline
arithmetic and 2.8x workspace figure both re-derive correctly.

## Verification

- Full Release fast tier: **5,278 assertions, 0 failures**, re-run independently at session close.
- Cave suites: `CaveCullChecks` 36 assertions (was 23), `CaveSpanChecks` 98 assertions (new).
- Both frontier modes reproduce their whole-cache numbers **byte-identically** after every refactor,
  which is what makes the iteration-to-iteration comparisons legitimate.
- Release builds of both projects are warning-free.
- 0.3.116 zip verified by hash in `dist/` and in the Mods folder; all eight older zips untouched.

---

## Delivered

**0.3.116 source (retention telemetry):**

- `LodCaveCull`: per-cell provenance marks, six-reason accounting over the centre section,
  `ResetRetention` / `CurrentRetention` / `DescribeRetention`. No cull decision changed.
- `VintageHorizonsModSystem`: `.vhcaves` writes the reason breakdown to the client log; the toggle
  resets the tally.
- Verified, copy-installed package; SHA-256
  `AE3291B9CA12A0EC5583A197A284B232630DB75AE287767F12A1046A8A15F7D1`. **Not human-run.**

**Offline measurement instruments (tests project only):**

- `CaveSpanGraph.cs`: global span-graph classifier prototype - span nodes, Y-overlap edges, portal
  identity, portal-only flood, corridor and sightline route rules, separate fluid graph, and both
  frontier stances behind `CaveFrontier`.
- `CaveField.cs`: `--global`, `--frontier open|portal`, `--sweep`, `--labels n`, `--tiles n`.
- `CaveSpanChecks.cs`: 98 assertions across the prototype's fixture set.
- `CaveCullChecks.cs`: 13 new assertions pinning live telemetry; 36 total.

**Documentation:**

- `dev/plans/PLAN_GLOBAL_CAVE_CLASSIFIER.md`, the cold-start handoff for the funded work.
- Session 59, session index, changelog, gotchas G111-G113, TODO/DONE, and status.
- No wire-history edit: assist protocol, blob format, and schema meaning are all unchanged.

## Decisions

- **The measurement gate is satisfied and the global classifier is worth building.** Sealed-network
  removal plus a corridor route rule is the difference between removing roughly a quarter of
  underground geometry and removing roughly three fifths of it. Session 58's demand that reason
  telemetry precede funding was met.
- **Light reach expands to 64 or 128 blocks; the choice is measured in Phase 1.** Owner directive,
  motivated by overhangs, with a target near 60% removal and a few points explicitly sacrificed for
  it. The reach-32 figure of 62.9% must never be presented as achievable at the new reach.
- **The corridor rule is adopted in the design; the sightline rule is dropped.** Corridor doubles
  removal and its knee is at W ~= 32; sightline measured +0.2 pp and is not worth runtime
  complexity. Its fixtures are retained.
- **Pseudo-portal frontier semantics are recommended, for consistency rather than payoff.** They
  gain +0.2 pp. The reason to adopt them is that the shipping rule already takes that stance at its
  own window wall, and having one stance is worth more than the number is.
- **Derived cull masks stay out of canonical storage.** RAM-resident, keyed by section content
  revision plus a classifier epoch, fail-open on any change. Persisting them would be a
  schema-meaning change and must go through `dev/WIRE_HISTORY.md` first.
- **The mesh-time 3x3 window will not be grown.** At the new reach it cannot be made safe without
  tripling per-thread workspace, and mask-absent sections simply not being culled is the better
  fallback.
- **Cave culling stays default-off.** Nothing global has been seen in game.

## Traps

- **Trigger:** assuming the fail-open unknown boundary is what costs the saving. **Failure:**
  replacing component-conservative unknown handling with pseudo-portals eliminated a 29.5%
  undecidable bucket and gained 0.2 percentage points, because that geometry moved intact into
  route retention. **Safer:** measure which retention reason dominates before funding a fix aimed at
  one of them; a large bucket is not the same as a recoverable one. (G111)
- **Trigger:** keeping every graph route between two entrances of a connected cave network.
  **Failure:** route retention held 34.2% of subterranean geometry, and it was deep interior
  hundreds of blocks from any mouth rather than passages, because a natural cave system is one
  2-edge-connected blob. **Safer:** bound routes by distance from a shortest mouth-to-mouth path;
  the corridor rule cut route retention to 3.6% and roughly doubled removal. (G112)
- **Trigger:** raising the daylight reach of the mesh-time rule. **Failure:** the window margin is
  one section (64 columns at L0) and must exceed the reach, or the wall treated as open sky floods
  the very section it protects; at reach 128 it lights the whole centre section and the saving
  disappears. It fails safe visually and destroys the payoff. **Safer:** keep any surviving local
  reach at 64 or less at L0, and move classification out of the window rather than growing it.
  (G113)
- **Trigger:** sweeping a parameter by recomputing everything per setting. **Failure:** the corridor
  test evaluates `L*(L-1)/2` hashed pair lookups per span - about 412 million at L=6 over 27.5M
  spans - and the L=6 convergence run had to be abandoned unfinished. **Safer:** hoist the
  parameter-independent quantity out of the sweep (`minSlack[s]` does not depend on W), leaving one
  array scan per setting. Recorded in the plan, not yet implemented.
- **Trigger:** quoting a measurement whose approximation has an unstated direction. **Failure:**
  judging each span against its `L` nearest mouths is stricter than the rule as stated, so the
  reported 62.6% is an upper bound on the literal rule and the bracket is one-sided. **Safer:**
  state which way every approximation errs, and prefer approximations that err toward keeping
  geometry. See also G106 on convergence.

## Flagged and unverified

**Judgement calls awaiting human review:**

- Reach 64 versus 128, after Phase 1 measures the cost of each against the ~60% target.
- Whether to keep or revert 0.3.115's coarse/backbone code once the global path exists; its bridge
  peel is superseded by the corridor rule and it contributes about 0.3 pp.
- Whether cave culling ever becomes default-on.
- When to commit the accumulated uncommitted work on `codex/cave-culling-refinement`.

**Claims lacking the evidence level to be treated as established:**

- **The L=6 label-convergence figure was not obtained.** The attempt was abandoned unfinished. The
  bracket is one-sided: L=2 gives 65.5% and L=4 gives 62.6% at W=32, so the literal rule sits at or
  below 62.6% by an unmeasured margin.
- **Removal at reach 64 or 128 is unmeasured.** The ~60% expectation is the owner's target and a
  projection from the reach-32 curve, not a result.
- **0.3.116 has not been run in game**, and its live cost was never isolated; roughly 1 ms is an
  estimate from the work it does.
- **The runtime classifier's CPU cost on a live client is unknown.** Offline timings are wall times
  on an unloaded machine, and their variance is large and unexplained: the same span-graph build was
  observed between about 4 s and 277 s on identical input. Nothing should be sized from them.
- **The corridor rule can remove geometry a player could actually see.** It shreds large chamber
  interiors and can drop a passage visible from a mouth pair outside a span's nearest few. The sweep
  prices the trade; it certifies nothing, and only the owner's eyes can accept it.
- **All removal figures are floors expressed in an upper-bound unit.** Geometry is `faces x 4`,
  which overstates vertices because the mesher merges greedily; the flood never charges for
  climbing, so it over-lights and under-removes.
- Portal grouping still counts two holes in one rock face as two portals when solid rock separates
  them, even if they open into the same cave head. Bounded in effect, documented in source.
- Multiplayer, changing-terrain invalidation, cache-frontier convergence, and third-party worldgen
  behaviour remain unaddressed for any global classification.
