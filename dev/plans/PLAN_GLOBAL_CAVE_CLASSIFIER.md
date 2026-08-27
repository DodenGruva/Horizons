# Plan - Global cave classifier

**Status:** **Closed as superseded on 2026-08-27.** The background global classifier described
below was measured but never built. The owner narrowed the product requirement to geometry visible
from the ground surface; hidden connectivity between entrances is irrelevant. The accepted runtime
answer is the existing local classifier with reach 64, exact straight surface sight, and a
four-block all-direction clearance halo, default on in 0.3.121. Version 0.3.122 renames its command
to `.vhcavecull`. The global graph, masks, route policy, separate fluid classifier, and invalidation
system are not open work. The body below is retained as the historical proposal and measurement
record, not as current implementation instructions.
**Created:** 2026-08-26
**Scope:** How Vintage Horizons decides which cached cave geometry is never built. Capture, storage
format, assist networking, the GPU renderer, and vanilla handoff are out of scope and must not
change. Nothing in this plan alters the persisted database or any wire format.

> **Read before touching source:** `dev/GOTCHAS.md` G106-G115 are all about this exact work and were
> each paid for with a wasted iteration. `dev/ARCHITECTURE.md` "Thread ownership" and "Concurrency
> invariants" govern the background worker in Phase 2. `dev/sessions/SESSION_58.md` carries the
> narrative behind the numbers below; `dev/sessions/SESSION_60.md` records why this plan closed.

---

## 1. What this is for

The mesher emits a face for every run boundary nothing rests against, and a cave is exactly a gap
between two runs in a column. Buried caverns therefore get a floor, a ceiling and walls, all sealed
inside rock, built and uploaded and drawn for an audience of nobody. Session 57 measured that about
55% of all cached geometry sits below the surface.

The feature that removes it is `LodCaveCull`, run at mesh time and now controlled by
`.vhcavecull`, default on after Session 60's acceptance. It decides from a 3x3-section window, and a
window cannot prove anything about a cave system larger than itself. The historical proposal below
was about replacing that local decision with a global one, before the owner established that global
connectivity was not a product requirement.

**Visual fidelity of real terrain outranks the saving.** The owner ranks tunnels, cliffs and
overhangs highest. That gate was met in Session 60; cave culling now defaults on.

---

## 2. Current state

### 2.1 Repository position

- Closure branch `codex/global-cave-classifier`, based on commit `efd713b`.
- The accumulated 0.3.114-0.3.122 work is committed at Session 60 close on the owner's direction.
- The proposal is retained for provenance; current state is in `STATUS.md` and Session 60.

### 2.2 Versions and their evidence level

| Version | What it is | Evidence |
|---|---|---|
| 0.3.114 | Fixed the sign defect: one coherent post-cull coverage view (G107) | **Human-tested.** Owner: "It works." |
| 0.3.115 | Coarse L4-L6 reach, bridge-tree backbone pruning, opaque-only fill | **Human-tested**, and marginal: about +0.3 percentage points |
| 0.3.116 | Retention telemetry in `LodCaveCull`, reported by the cave-cull command into the client log | **Built**, packaged, copy-installed. **Not yet human-run at that boundary.** |
| 0.3.117-0.3.121 | Surface-only rule, reach 64, exact straight sight, long-tunnel fix, four-block 3D clearance, default on | **Human-tested and accepted.** |
| 0.3.122 | Command renamed to `.vhcavecull` | **Built, verified, and copy-installed at Session 60 close.** |

0.3.116's zip is `dist/vintagehorizons_0.3.116.zip`, SHA-256
`AE3291B9CA12A0EC5583A197A284B232630DB75AE287767F12A1046A8A15F7D1`, copied into
`%APPDATA%\VintagestoryData\Mods` beside the owner's rollback set.

### 2.3 The live result, human-tested

Owner's settled A/B at the same 1,713 meshes, 0.3.115:

| state | opaque vertices | live geometry | cave worker timing |
|---|---:|---:|---:|
| off | 86,884,340 | 1,858.7 MiB | no completed sections |
| on | 64,791,952 | 1,393.7 MiB | 1,713 sections, 44.984 ms mean, 149.648 ms max |

That is **-25.43% of opaque vertices**. The 44.984 ms is the whole cave preparation pass per
section on a worker thread, not the incremental cost of any one part of it.

### 2.4 The three offline measurement iterations

All figures below are **harness-measured** against the owner's real client cache: 4,637 level-0
sections, 18,425,190 captured columns, decoded into 27,456,106 air spans, 5,439,699 water spans and
51,310,867 graph edges. They are shares of *estimated subterranean geometry*, not of the whole
world and not of measured vertices - see the caveats in section 2.6.

**Iteration 1 - is a global classifier worth building?**

| | share of estimated geometry |
|---|---:|
| Removed by the shipping local rule | **30.3%** |
| Removable by the global span-graph prototype | 30.8% |
| Additional over the local rule | **+5.4 pp** |
| Both together | 35.6% |
| Kept as undecidable "unknown frontier" | **29.5%** |

The undecidable share was the finding. Only 0.2% of captured columns sit beside something
uncaptured, but "unknown" is a property of a whole component, and components are enormous:
386,066 air components, of which **the largest holds 18,466,178 of 27,456,106 spans**. One
uncaptured column anywhere taints an entire network.

**Iteration 2 - treat the frontier as a pseudo-portal.** `LodCaveCull` already treats its window
wall and every uncaptured column as a *light source* and still fills dark air beyond that light's
reach. Applying the same stance globally removes the undecidable bucket entirely.

| | Open frontier | Portal frontier |
|---|---:|---:|
| Global removable | 30.8% | 31.4% |
| Additional over local | +5.4 pp | **+5.6 pp** |
| Both together | 35.6% | 35.8% |
| Route between real portals (kept) | 9.3% | **34.2%** |
| Unknown frontier (kept) | 29.5% | gone |

The 29.5% did not become removal. It became *route retention*: those components were never
undecidable because they hug the frontier, they are colossal networks that touch it somewhere and
also have many genuine surface mouths. **Net gain of the whole frontier change: +0.2 pp.** That is
G109 at global scale - the bridge-tree peel keeps the entire 2-edge-connected core, and a natural
cave network is overwhelmingly one 2-edge-connected blob.

**Iteration 3 - tighten the route rule.** Keep a route span only within slack `W` of a shortest
path between two nearby mouths, using the same cost metric as the light flood. Whole cache, portal
frontier, reach 32, four terminal labels:

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

Readings that matter:

- **The corridor rule does all the work.** It roughly doubles removal. The curve **flattens below
  W ~= 32**: W=128->64 gains 5.0 pp, 64->32 gains 1.5, 32->16 gains 0.7, 16->0 gains 0.8. W=32 is
  one light-reach of slack and is the natural stopping point.
- **The sightline rule is worth ~0.2 pp and should be dropped.** Its fixture case is real (two
  mouths a few blocks apart joined by a hundred blocks of serpentine) but worldgen mouths are dense
  enough that it almost never fires. Do not carry it into runtime.
- **Route retention was deep interior, not passages.** The bridge rule was keeping regions hundreds
  of blocks from any mouth simply because they were connected.
- **Enclosed water is 0.4 pp.** Fluid is 57% of subterranean *cells* and 7.9% of the *geometry*,
  because a flooded volume has a surface and no interior. Implement it for correctness, not payoff.
- Disagreement with the shipping rule collapses from 3,018,736 cells to about 620,000 under the
  corridor rule: the two rules stop disagreeing about safety and differ only in reach.

### 2.5 Label sensitivity - the one approximation that errs toward removing

Each span is judged against its `L` nearest distinct mouths. That is **stricter** than the rule as
stated ("near a shortest path between *some* pair"), so a small `L` removes more than it should.

| L | removable at W=32 | route kept |
|---:|---:|---:|
| 2 | 65.5% | 1.0% |
| 4 | **62.6%** | 3.9% |
| 6 | **not obtained** | - |

More labels can only keep more. **The bracket is one-sided:** the literal rule's value is at or
below 62.6%, and how far below is unmeasured. An L=6 run was attempted and abandoned; see the cost
trap in section 3.2.

### 2.6 Caveats that apply to every number above

- **Face estimate, not vertices.** "Geometry" is a count of unit cell-faces where cavity meets
  solid, reported as `faces x 4`. The mesher merges faces greedily before emitting, so this is an
  **upper bound** on vertices. It scales all rows alike and does not distort a curve, but it is not
  a vertex prediction.
- **No climbing charge.** The prototype's flood costs one column width per sideways hop and nothing
  for movement inside a span. It never charges for climbing, so it lights *more* than the live
  per-voxel flood would. Every removal figure is therefore a **floor**.
- **Route tightening can remove genuinely visible geometry.** It is the only rule in the prototype
  with that property: it shreds the interior of large chambers and can drop a passage visible from a
  mouth pair outside a span's `L` nearest. The sweep exposes the trade; it certifies nothing.
- **Nothing global has ever run in game.** Every figure in section 2.4 is harness-tested. No global
  classifier exists in shipping source.

### 2.7 Reproducing the measurements

```
# One answer, portal frontier
dotnet run --project tests/VintageHorizons.Checks/VintageHorizons.Checks.csproj \
  --configuration Release -- cavefield --global --frontier portal

# The route-tightening sweep, as run for the table above
dotnet run --project tests/VintageHorizons.Checks/VintageHorizons.Checks.csproj \
  --configuration Release -- cavefield --global --frontier portal --sweep --labels 4
```

- `VINTAGE_STORY` must point at the game install or the build cannot resolve game assemblies. On the
  owner's machine that is `%APPDATA%\Vintagestory` (note: *not* `VintagestoryData`).
- The harness locates the newest client cache under
  `%APPDATA%\VintagestoryData\ModData\vintagehorizons\*.db`, **copies it to a temp directory** and
  opens the copy `Mode=ReadOnly`. Never open the owner's cache directly and never write to it.
- `--frontier open|portal` selects the frontier stance; `--tiles n` restricts to a centred n-by-n
  block of sections for a fast smoke run; `--labels n` sets the terminal-label count.
- `cavefield` asserts nothing. It reports numbers. The assertions live in
  `tests/VintageHorizons.Checks/CaveSpanChecks.cs` and `CaveCullChecks.cs`.

**Runtime, measured on the owner's machine:** decode 9.5 s, span graph build 4.2-11.7 s, local-rule
comparison pass 21.5 s (parallel), per-setting re-tally 1.5 s at L=2 and about 14 s at L=4. Whole
sweep 1 m 06 s at L=2 and 3 m 21 s at L=4.

**Treat every timing here as indicative, not as a budget.** The same span-graph build step was
observed at 4.2 s and at 50.9 s on identical input in the same session, and an independent
verification run reported a range as wide as 12 s to 277 s. The cause is not understood. Do not
size anything from these figures without re-measuring.

---

## 3. Phase 1 - remaining measurements

**Offline, cheap, and a hard gate. No runtime work until these numbers exist.** Every step here is
harness-only: tests project, read-only cache, no shipping-source change, no version bump, no zip.

### 3.1 Re-run the corridor sweep at reach 64 and reach 128

The owner has directed that light reach expand from 32 blocks. The motivation is overhangs, and the
mechanism is worth stating precisely because it decides what "safe" means here:

> A deep rock shelf leaves an air gap in the columns beneath it. The outermost of those gaps touches
> the open air beside the shelf, so it is portal-adjacent and lit. Gaps further under the shelf are
> further from that one portal, and past the reach they go dark. A dark span in a component with a
> single portal is classified `RemovableOverflow` and filled. At reach 32 the underside of any
> overhang deeper than about 32 blocks is therefore **removable, and filling it is visible damage to
> exactly the terrain the owner ranks highest.**

Note what this implies: **the corridor rule cannot help or hurt overhangs.** Under-shelf air is
single-portal, so it never reaches the route rule at all. Reach is the only lever.

Do:

1. Add a `--reach n` option to the `cavefield --global`/`--sweep` path if one is not already
   reachable through `--light`, and re-run the full sweep at reach 64 and reach 128 under
   `--frontier portal --labels 4`.
2. Report, per reach, the same table as section 2.4: removable total, route kept, additional over
   the local rule, both together, and the disagreement line. Expect removal to **fall** as reach
   rises, because more spans become `KeptLit`.
3. Recommend 64 or 128 against the owner's target.

**The owner's rule, which must be encoded in the recommendation:** stay close to **60% removal**. A
few points below the reach-32 ceiling are explicitly acceptable in exchange for overhang safety. If
reach 128 drops removal well below ~60%, fall back to 64.

**Do not present 62.9% as achievable at reach 64 or 128.** That figure is reach 32. The entire
purpose of this step is that expanding reach costs points; the expected landing is near 60% and it
is currently **unmeasured**.

### 3.2 Close the label-convergence bracket

At the reach chosen in 3.1, establish where the corridor number converges as `L` rises. L=2 gave
65.5% and L=4 gave 62.6% at reach 32; the true value of the literal rule is at or below 62.6% and
the distance is unknown.

**The cost trap that stopped the previous attempt.** Per setting, the corridor test walks every span
and evaluates all `L*(L-1)/2` label pairs, each a `Dictionary<long,int>` lookup into a pair table
that grew to 2,528,304 entries at L=6. That is `O(L^2 * V)` hashed lookups per parameter value:
about 412 million at L=6 over 27.5 M spans, and the sweep never finished.

The fix is to notice that **the expensive part does not depend on W at all**. For each span compute
once:

```
minSlack[s] = min over its label pairs of ( through_ij - geodesic_ij )
```

`through` and `geodesic` are both independent of `W`, so every corridor setting collapses to a
single comparison `minSlack[s] <= W` over a flat array. That turns the whole sweep into one
`O(L^2 * V)` pass plus one `O(V)` scan per setting, and makes L=6 or L=8 cheap. (If the sightline
rule is kept for the record, the same trick needs the Pareto set of `(slack, tortuosity)` per span,
which is tiny in practice - but see 2.4: dropping it is recommended.)

Alternatively, replace the label heuristic with an exact or near-exact pass and report which was
used. Whatever is chosen, **state the approximation's direction**: any shortcut must err toward
keeping geometry, never toward removing it.

### 3.3 Add overhang coverage

1. **Harness fixture, required.** Build a deep overhang in `CaveSpanChecks`: a rock shelf extending
   well past the chosen reach over open ground, and assert that the air beneath its full depth is
   **kept** at the chosen reach and, as a control, that it is *not* kept at reach 32. This fixture
   is the executable form of the owner's directive and must not be dropped.
2. **Real-cache spot-check, if feasible.** The owner can name coordinates of known overhang and
   cliff terrain. Add a `cavefield` mode that reports the verdict distribution for a named section
   or block range so those places can be inspected offline without launching the game. If the
   owner's coordinates are not available, say so and leave the claim unmade.

### 3.4 Gate

Phase 1 is complete when: the reach-64 and reach-128 curves exist and a recommendation is on record
against the 60% target; the label bracket is closed at the chosen reach; the overhang fixture passes
at the chosen reach and fails at reach 32; and the full fast tier still passes with no assertion
regression. **Do not begin Phase 2 before this.**

---

## 4. Phase 2 - runtime architecture

Only after Phase 1. This is the first change to shipping source and the first thing the owner can
see.

### 4.1 Shape

A background classifier on the **client** that owns a span graph over the cached L0 sections and
publishes per-section cull masks the mesh workers consume. The prototype in
`tests/VintageHorizons.Checks/CaveSpanGraph.cs` is the reference for the algorithm and its comments
carry the reasoning; it is not shippable as written (it reads the whole cache at once, has no
budget, and no invalidation).

Pipeline, in order:

1. **Span extraction.** Nodes are maximal vertical air gaps between stored runs, per column. Never
   contract a whole column into one node - that is what merges stacked cave layers and makes dead
   branches look cyclic (G109).
2. **Edges** join spans in 4-adjacent columns whose Y ranges overlap. Nothing else.
3. **Exterior and portal identity.** Exterior is the air above each captured column's top run. A
   span is portal-adjacent when a neighbouring column's exterior reaches into its Y range.
   Contiguous portal-adjacent spans are **one** portal - this is the whole point, because a single
   rough mouth otherwise reads as several entrances (G108).
4. **Daylight flood from portals only**, at the Phase 1 reach, one column width per sideways hop,
   free inside a span.
5. **Classification.** Zero terminals -> removable sealed. Lit -> keep. One terminal -> removable
   beyond the light. Two or more -> keep the corridor, remove the rest.
6. **Corridor route rule** at the `W` chosen in Phase 1 (W=32 unless the reach sweep moves the
   knee). **Drop the sightline rule.**
7. **Enclosed-water absorption.** Fluid is its own graph. Keep anything touching open sky, unknown
   space, or cavity air the air pass itself retained; only fully enclosed fluid is removable.
8. **Derived masks for L1-L6**, mip-style: a coarse cell is removable only when every contributing
   fine cell supports removal *and* a valid opaque fill material exists.

### 4.2 The frontier decision

Two stances are implemented and measured in the prototype:

- **Component-conservative (`Open`)**: any component touching unknown space is kept whole. Leaves
  29.5% of the geometry undecided.
- **Pseudo-portal (`Portal`)**: a frontier contact lights and terminates routes, exactly as
  `LodCaveCull` already treats its own window wall.

**Measured difference between them: +0.2 pp.** Recommend **pseudo-portal**, because it is the stance
the shipping rule already takes and it removes a large undecidable bucket without materially
changing the answer - not because it saves anything. Record the choice and this number.

### 4.3 Masks: RAM-resident, revision-keyed, never persisted

- A mask is a per-section, per-level bitset of removable cells, keyed by **`LodWorld.ContentRevision(key)`
  plus a classifier epoch**. A mask whose section revision has moved is stale and must be discarded.
- **The mask is RAM-resident and is NOT written to the canonical database.** No schema change, no
  blob-format change, no wire change. Assist protocol 1 / blob format 4 / schema 6 must all be
  unchanged when this ships.
- If a future iteration ever wants to persist masks, that is a schema-meaning change and goes
  through `dev/WIRE_HISTORY.md` first, with an explicit version and invalidation story. Do not
  silently reinterpret existing rows.
- Budget the mask memory explicitly. The offline prototype held 27.5 M spans and 51.3 M edges for a
  4,637-section cache with no budget at all; a client cannot. Expect to need **tiling** - classify a
  bounded neighbourhood at a time, carrying boundary spans between tiles with union-find - and to
  cap resident masks with an eviction policy. Size it from a measurement, not from these numbers.

### 4.4 Consumption and the fate of the local rule

- Mesh jobs consume the mask instead of running the 3x3 classification. This is the point at which
  the **44.984 ms per-section pass goes away**.
- **A section with no mask is simply not culled.** Fail-open. Do not stall a mesh waiting for a
  classification, and do not fall back to growing the local window (see section 5.1 for why that is
  not available at the new reach).
- `.vhcavecull` remains the toggle, with the same immediate `RemeshAll` rollback. The 0.3.116 retention
  telemetry and its log block must be **preserved and extended**, not replaced: it is the only way
  the owner can tell which rule kept a cave they can see.

### 4.5 Invalidation

**Fail-open on every change.** When a section's content revision moves - capture, assist, sweep,
generation, or any edit - drop its mask, remesh it *unculled*, and queue reclassification. A cave
that reappears for a few seconds is a non-event; a cave that vanishes because a stale mask outlived
its terrain is exactly the failure the owner would report as missing terrain.

The classifier epoch exists for the case where the *rule* changes rather than the data: bump it and
every mask is stale at once.

### 4.6 Threading

Follow `dev/ARCHITECTURE.md` "Thread ownership" and "Concurrency invariants" exactly. The closest
existing patterns to imitate:

- **Mip worker** - receives immutable child-section arrays plus identity/revision and a world epoch;
  the owning thread rejects stale results and only then clears the durable obligation. This is the
  nearest analogue to what the classifier does.
- **Local-offer discovery worker** (integrated-singleplayer sibling-cache scanner) - exclusively owns
  its own read-only SQLite connection, never touches `LodWorld`, publishes batches the owning thread
  applies once. Imitate this if the classifier ever reads storage directly.
- **Storage worker** - coalesced immutable snapshots, bounded outstanding allowance, publication at
  result boundaries.

Non-negotiable, from the invariants register:

- No worker mutates `LodWorld` or a live section.
- No background task reads the live block registry.
- Every worker result carries a world epoch; clearing a queue alone cannot stop an in-progress job
  from publishing afterward.
- A stale result cannot overwrite a newer section revision.
- Backpressure is applied before retaining large snapshots.
- Diagnostics must not be capable of killing a worker thread.

Run the classifier below normal priority. Budget its publication by elapsed time and bytes, not by
item count.

---

## 5. Hard constraints the next AI must not trip over

### 5.1 Reach 128 is incompatible with the live 3x3 local rule at L0

This is the single most important constraint in this document.

`LodCaveCull` builds a 3x3-section window and treats the window's outer wall as **open sky**, because
what lies beyond it is unknown and keeping geometry is the safe answer. That makes the wall a light
*source*, so it must stand further off than the light can travel or it floods the very section it was
meant to protect. The margin is one full section: **64 columns at L0**.

Therefore, with a sideways step cost of one column at L0:

- **reach 32** - wall light dies 32 columns in, well outside the centre section. Correct today.
- **reach 64** - wall light arrives at the centre section boundary with exactly zero budget. Exactly
  borderline; safe only because the flood rejects a value of zero.
- **reach 128** - wall light penetrates 64 columns *into* the centre section, lighting all of it.

Note the failure is **not visual damage** - false light keeps geometry, so it fails safe - but it
destroys the entire saving. Session 58 measured the same effect as "the difference between removing
15% and removing about 30%".

**What to do about it:** when the global mask exists, mask-absent sections are simply not culled
(section 4.4). Do **not** grow the window to 128; that would triple the workspace and re-import all
the local rule's other problems. If the local rule survives at all as a fallback, it keeps **reach
64 or less at L0** and must never be handed the global reach.

### 5.2 Byte cell values cap the local implementation at 253

`LodCaveCull` stores light levels in a `byte` per cell and reserves 254 for "kept" and 255 for
"solid", so `MaximumReach` is 253. There is no spare value; 0.3.116's provenance marks needed a
separate parallel array for exactly this reason. Any reach above 253 requires a wider cell type in
the local pass. The global classifier has no such limit.

### 5.3 Workspace memory scales with the square of the window

If the local pass is kept, its window is `grid + 2 * margin` columns square. At L0 today that is
192, and the workspace is three arrays of `192 * 192 * worldHeight` bytes per mesh thread. Growing
the margin to match a 128-block reach makes it 320 square - about **2.8 times the memory, on every
mesh thread**. This is a second, independent reason not to grow the window.

### 5.4 Classifier CPU cost on a live client is unknown

Nothing has measured what a background global classifier costs on the owner's machine while playing.
The offline figures in section 2.7 are wall times on an unloaded machine with no frame budget, and
their run-to-run variance is large and unexplained. Add a live timing surface to `.vhcavecull` before
claiming any cost, and expect to iterate on the tiling budget.

### 5.5 Other traps already paid for

- **G107** - one mesh operation must own one coherent effective-coverage view for current and
  adjacent cells. Comparing post-cull geometry against a pre-cull neighbour manufactures walls
  through every cavern and made cave culling *add* 2.9% geometry.
- **G108** - a bounded fail-open window cannot prove a large component globally enclosed.
- **G109** - a one-cell bridge fixture overstates pruning value; real caves are one 2-edge-connected
  blob. This is why the corridor rule exists.
- **G110** - water is stored geometry, not air. Capture reads `BlockLayersAccess.FluidOrSolid`, so an
  air-only fill never sees a flooded chamber. Never borrow water as an opaque cave fill.
- **G106** - do not quote a direction-sampled visibility number without showing it converges.
- **Rejected and not to be re-proposed:** per-column floor/depth envelopes. They cannot distinguish
  hidden cavities from cliffs, arches, overhangs, ravines, tunnels, floating terrain, or third-party
  worldgen. The owner considered and rejected this.

---

## 6. Acceptance gates

### 6.1 Fixtures that must keep passing

The cave suites currently stand at **36 assertions** (`CaveCullChecks`) and **98 assertions**
(`CaveSpanChecks`), inside a full fast tier of **5,278 assertions, 0 failures**. Assertion counts
must not regress.

`CaveCullChecks` covers: sealed bubble filled; cave open to sky kept; through-tunnel kept and its
dead-end twin filled; dead branches not rescuing a network; coarse L4-L6 behaviour; a column with no
opaque material never used as fill; nothing touched when off; the live `BuildMesh` integration; a
cave crossing a section seam growing no wall; and the 0.3.116 retention telemetry naming the right
reason for each cell.

`CaveSpanChecks` covers: a sealed network spanning four sections (with the shipping local rule shown
*keeping* it); a looped branch peeled; stacked cave layers as separate components; one ragged mouth
counted as one portal; two real mouths keeping a route; enclosed water removable; lake-connected
water kept; an uncaptured neighbour keeping everything beside it, with a sealed control; a frontier
contact that lights but does not save what is beyond it; a ragged frontier contact as one
pseudo-portal; routes from a real mouth to the frontier and between two frontier contacts; settled
shapes agreeing in both frontier modes; a straight through-tunnel and a gently curved one surviving
every swept parameter; a winding route plugged in the middle while both mouths stay lit; a wide room
keeping its corridor and shedding its corners at small W; a bypass loop the bridge rule cannot peel
being shed by the corridor; a symmetric two-mouth loop keeping both arcs; and tightening never
touching a verdict that is not a route.

**Add** the deep-overhang fixture from 3.3. Keep every existing one.

### 6.2 Build and packaging

- Release build must be **warning-free**: `dotnet build VintageHorizons/VintageHorizons.csproj -c Release`.
- Fast tier: `dotnet run --project tests/VintageHorizons.Checks/VintageHorizons.Checks.csproj --configuration Release`.
- `VINTAGE_STORY` must point at the game install (`%APPDATA%\Vintagestory` on this machine).
- **Patch +1 in both `VintageHorizons/modinfo.json` and the csproj before any playable artifact.**
  Ordinary compile and test runs do not consume a version number. Never put changed binaries behind
  an already-used version.
- Package with `scripts/package.sh`, verify the zip, then **copy** it into
  `%APPDATA%\VintagestoryData\Mods`. Copy only: never delete, move, rename, overwrite or back up an
  existing zip there - those are the owner's rollback set. If the destination already exists, stop
  and inspect rather than overwrite.
- One zip per build. Do not also copy it under a second name.

### 6.3 The owner's in-game protocol

- **Never launch or stop a Vintage Story process. Ask first, every time, and wait for a yes.** Prior
  approval of one run is not approval of the next. The machine is the owner's.
- The owner's A/B is `.vhcavecull on` / `.vhcavecull off`, compared **only at the same settled mesh count**
  - an unsettled reading is not comparable and must not be quoted. The relevant numbers are opaque
  vertices and live geometry, both reported by `.vhcavecull` with no argument.
- The retention breakdown goes to the **client log** under `caves:`, because chat cannot be copied
  out of the game and the periodic report fires once at 30 s. Any new telemetry must reach the log
  on demand the same way.
- Ask the owner for small, specific observations, not open-ended reports.

### 6.4 Final visual acceptance

Before any change to the default state, the owner had to look at and accept: **overhangs, cliffs,
tunnels, and cave mouths**. Session 60 met this gate after the long-tunnel and uneven-floor fixes,
and cave culling now defaults on. Geometry totals establish that the rule ran; the owner's playtest
establishes the visual acceptance.

### 6.5 Process

No commit, push, tag, merge, branch change, or documentation edit without the owner explicitly
asking. Source completion, packaging and installation do **not** authorize a documentation update -
see the authority map in `CLAUDE.md`.

---

## 7. Closed owner decisions

1. **Reach:** 64. On the same 40-section sample, 96 retained 1.30% more baseline geometry and 128
   retained 4.76% more than 64, without an accepted visual need.
2. **0.3.115 backbone code:** route/backbone retention is not part of the shipping decision; the
   owner's surface-only requirement supersedes it.
3. **Default:** on, accepted after the surface, straight-tunnel, and clearance playtests.
4. **Commit:** authorized for the accumulated cave work at Session 60 close.

---

## 8. Explicit non-goals

- No persisted classification, no schema change, no wire-format change resulted from this plan.
- The global sampled sightline/tortuosity proposal was dropped. The accepted local runtime instead
  uses a small exact lattice-line test with bounded clearance.
- No growing of the mesh-time 3x3 window (section 5.1).
- No per-column floor or depth envelope, ever (section 5.5).
- No server-side classification. This is a client rendering decision over the client's own cache.
- No change to capture, storage, assist networking, the GPU renderer, or vanilla handoff.
