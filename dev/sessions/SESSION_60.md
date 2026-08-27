# Session 60 — Surface-only cave culling accepted

**Date:** 2026-08-27
**Branch/commit:** `codex/global-cave-classifier`, based on
`efd713bc65918285412720a92d07d2301046f329`; the accumulated cave work is committed at session
close.
**Mod version:** 0.3.117-0.3.122; 0.3.121 human-accepted, 0.3.122 changes only the command name and
is built, verified, and copy-installed at close.
**Assist protocol / blob / schema:** 1 / 4 / 6, unchanged.

## Context and investigation

Session 59 priced a background whole-cache cave classifier before building it. The measurement was
useful because it found that keeping every graph route between cave entrances was the real reason
deep geometry survived. The owner then supplied the decisive product rule: Vintage Horizons is
judged only from the ground surface. Preserving an underground network merely because it connects
two cave entrances has no value when no part of that network is visible from outside.

That direction changed the problem. A persistent global connectivity graph, portal identities,
fluid-component classification, derived coarse masks, and invalidation machinery would have spent
considerable memory and background work preserving relationships the product explicitly does not
care about. The needed visible guarantees were much smaller:

- keep the terrain's surface and the air immediately exposed to it;
- keep deep overhangs and cave mouths within a conservative daylight envelope;
- keep straight tunnels that can genuinely be seen through from outside, even when their mouths
  lie beyond one mesh job's local data window;
- keep a small volume around that proven sightline so an uneven floor or wall is not rebuilt into
  the tunnel;
- do not preserve hidden branches merely because they connect to the visible passage.

A sea-level test was considered and rejected. Sea level is world-generation data, can vary by
world or generator, and does not describe mountains, cliffs, overhangs, or player-made tunnels.
The classifier instead derives the local exterior from the highest stored terrain in each column,
so it follows the actual geography represented by the cache.

## Work narrative

### 1. The runtime rule became surface-only

Version 0.3.117 removed route connectivity from the shipping decision and increased the
surface-light reach from 32 to 64 world blocks. The reach remains bounded by the existing 3x3
classification window; at level 0 its one-section margin is 64 columns, so a larger local reach
would allow the fail-open outer wall to illuminate the centre section and erase the saving.

The owner played the surface-only build with cave culling enabled. The tested scene looked
visually identical while frame rate rose from about 350 FPS to about 400 FPS. This is a qualitative
same-scene observation, not a scripted benchmark, but it established that the simpler rule had
both visible acceptance and practical value.

### 2. Straight surface sight replaced underground connectivity

Version 0.3.118 added a bounded exact-line pass after the daylight flood. It derives exterior air
from each captured column's actual top surface and projects unobstructed lattice lines through four
horizontal direction families with vertical steps -1, 0, and +1. The implementation is bit-parallel
over height words, stops at solids, and does not turn around corners. It therefore keeps a direct
surface view without resurrecting a hidden cave network.

The first human test found a deliberately simple east-west tunnel through a mountain plugged in
its middle. The initial implementation had required a visible exterior seed inside the same local
window. For a mesh job near the middle of a long tunnel, both real mouths were outside that 3x3
window, so the centre was indistinguishable from a sealed straight corridor.

### 3. A complete-window ray fixed the long tunnel

Version 0.3.119 treats an uninterrupted line that crosses the complete available window as
surface-visible. This is the conservative local answer: when both ends continue beyond known data,
the local classifier cannot prove the line sealed, so it keeps it. The owner reported the mountain
tunnel was much better.

The cost was measured on the same 40-section real-cache sample. With sight disabled the shipping
path changed 1,554,712 estimated vertices to 1,122,200; with sight enabled it produced 1,134,464.
The tunnel protection retained 12,264 vertices, 0.79% of the original geometry in that sample.
The accepted limitation is explicit: a globally sealed, perfectly straight corridor crossing the
whole window can also survive. Distinguishing those cases would require the global state the owner
chose not to fund.

### 4. Clearance preserved the tunnel rather than only its centre ray

The next human test exposed a smaller defect. A direct sightline survived, but one-block humps in
an uneven tunnel floor fell just outside the exact ray and were rebuilt as solid geometry.
Version 0.3.120 first added a one-block vertical guard around each proven line. The real-cache
sample retained only 13 additional air blocks, below 0.01% of original geometry.

The owner then clarified that the actual tunnel structure should survive in every direction, not
only vertically. Version 0.3.121 expanded the guard to four blocks through all 26 neighbouring
directions. Expansion is fixed-radius, starts only from a proven straight sightline, and stops at
solid terrain, so it cannot wander through bends or pull in a connected cave system.

On the 40-section sample the final rule changed 1,554,740 estimated vertices to 1,136,824, removing
26.88%. Relative to the one-block guard it retained 118 additional air blocks and about 2,250
estimated vertices, 0.15% of the original geometry. Paired harness wall time was effectively the
same, 14.712 versus 14.716 seconds. The owner accepted the result and directed cave culling to ship
on by default. `VINTAGEHORIZONS_CAVE_CULLING=0` and the in-game off command remain immediate
comparison and rollback paths; changing the command remeshes resident distant terrain.

### 5. Reach 64 beat 96 and 128

The final reach comparison used the same 40 sections and 1,554,740-vertex baseline:

| reach | resulting vertices | removed | removal | extra geometry vs 64 |
|---:|---:|---:|---:|---:|
| 64 | 1,136,824 | 417,916 | 26.88% | - |
| 96 | 1,157,100 | 397,640 | 25.58% | 20,276 / 1.30% of baseline |
| 128 | 1,210,820 | 343,920 | 22.12% | 73,996 / 4.76% of baseline |

Reach 96 gives back 4.85% of reach 64's saving. Reach 128 gives back 17.71%, because the local
window's assumed-open boundary begins illuminating the centre. The owner chose 64. This closes the
reach decision without tying behavior to a world generator's sea level.

### 6. The player command now states what it controls

Version 0.3.122 renames `.vhcaves` to `.vhcavecull`. The old registered command is removed rather
than retained as an alias, so the player-facing surface has one unambiguous name. The no-argument
report, `on`/`off` behavior, immediate remesh, environment override, and log telemetry are otherwise
unchanged.

---

## Delivered

- A surface-only cave decision: daylight reach plus exact straight exterior sight, with no hidden
  entrance-network retention.
- Terrain-derived exterior classification with no sea-level assumption.
- Full-window fail-open handling for long straight mountain tunnels whose mouths lie beyond a
  mesh job's local window.
- A four-block, solid-stopped, all-26-direction clearance halo around proven sightlines.
- Default-on cave culling with `VINTAGEHORIZONS_CAVE_CULLING=0` and `.vhcavecull off` as rollback
  paths.
- Real-cache fixtures and checks for surface light, straight sight, window-spanning tunnels,
  clearance, coarse levels, mesh-boundary coverage, accounting, and reach comparisons.
- An offline whole-cache span graph retained as measurement infrastructure and historical evidence;
  it is not part of the runtime product.
- The global-classifier plan closed as superseded rather than falsely marked implemented.
- Version 0.3.122, whose only change beyond the accepted 0.3.121 behavior is the command rename.
- Warning-free Release builds of the mod and benchmark project; 5,368 fast assertions and 1,570
  documentation checks pass. The 14-entry package contains the licence and no PDB, reports 0.3.122
  in its manifest, and was copy-installed with matching SHA-256
  `F971FC4643016AC2EB5A07C8702453C2E31C01257425BB826E51B805F05D9800`.

## Decisions

- Only geometry visible from the ground surface is a cave-culling product requirement.
- Underground connectivity is not visibility and must not preserve hidden tunnel networks.
- Surface geography comes from captured column tops, never a fixed or inferred sea level.
- A locally uninterrupted complete-window ray fails open. The small false-retention ambiguity is
  preferable to plugging a real long straight tunnel.
- The sightline clearance is four blocks in every direction. Its measured 0.15% geometry cost is
  within the owner's requested 1% budget.
- Daylight reach remains 64. The measured costs at 96 and 128 do not buy an accepted visual need,
  and the local window makes larger reaches progressively less meaningful.
- Cave culling is on by default after the owner's visual and performance acceptance.
- The command name is `.vhcavecull`; `.vhcaves` is retired without an alias.
- The background global classifier, persisted masks, route slack, global fluid graph, and their
  invalidation protocol are not open work. The plan remains only as a historical decision record.
- Assist protocol 1, blob format 4, and schema 6 remain unchanged; no wire-history entry is needed.

## Traps

- **Trigger:** requiring the local window to contain a tunnel mouth before preserving its ray.
  **Failure:** the middle section of a long straight tunnel contains neither mouth and is plugged
  even though the complete tunnel is visible from outside. **Safer:** preserve an uninterrupted
  line that crosses the complete known window and record the unavoidable local ambiguity. (G114)
- **Trigger:** preserving only the exact mathematical sightline. **Failure:** the open centre ray
  survives while nearby floor and wall irregularities are filled into the visible passage.
  **Safer:** expand a small, fixed, solid-stopped clearance volume from the proven line, measure its
  geometry cost, and never let it become an unbounded connectivity flood. (G115)
- **Trigger:** using sea level as a proxy for exterior visibility. **Failure:** world generators
  choose different sea levels, mountains cross it, and player-made tunnels ignore it. **Safer:**
  derive exterior from the cached terrain surface in each column.
- **Trigger:** increasing local daylight reach without measuring boundary-created light.
  **Failure:** reach 96 and especially 128 retain geometry because the assumed-open edge of the
  3x3 window becomes a false light source. **Safer:** couple reach to window margin and keep the
  accepted value at 64. (G113)

## Flagged and unverified

- The approximately 350-to-400 FPS change is one owner's same-scene observation. It is useful
  human evidence, not a controlled performance benchmark or a claim that every scene gains 50 FPS.
- Versions through 0.3.121 were tested on the owner's machine and selected world. Other world
  generators, drivers, multiplayer, long sessions, and arbitrary tunnel shapes have ordinary
  coverage risk.
- A globally sealed straight corridor that crosses the entire local window can be retained. This
  costs geometry but fails safely; distinguishing it from a real through-tunnel requires global
  knowledge the product does not currently need.
- Flooded underground geometry remains represented as fluid rather than cavity air. The owner
  accepted the current surface picture; no separate global fluid classifier was built.
- The 40-section geometry figures are paired harness estimates on a real cache, not live GPU
  vertex totals. They support relative policy choices and do not predict the owner's whole scene.
- 0.3.122 changes only the command registration and help text after the accepted 0.3.121 playtest;
  it was copy-installed but not launched by the assistant. Smoke and install-matrix tiers were not
  rerun because they launch Vintage Story and this session did not authorize a game launch.
