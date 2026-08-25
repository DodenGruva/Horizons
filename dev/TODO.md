# Vintage Horizons — TODO and verification debt

> Tier 2 companion: open work only. Completed narrative moves to `dev/history/DONE.md`; current conclusions belong in `STATUS.md`.

## TOP PRIORITY - 0.4.0 is accepted; the next work is measurement and portability

**The GPU terrain renderer is the default and has been played and accepted.** Phase 8's correctness
defect was found and fixed in 0.3.101 (G96/G97), the cleanup ran through 0.3.103, and the owner
accepted 0.4.0 in ordinary play: the picture is correct and performance is much improved on his
machine. That is qualitative acceptance on one system, not a measurement.

Do not reopen the flicker: the margin ladder, the sky-silhouette theory and the texture-barrier idea
are all explained as consequences of the texel-mapping defect rather than causes of it. The
narrative is in `dev/history/DONE.md` and Sessions 50-53.

**The path now helps every player on supported hardware, so what is left is proving it elsewhere
and knowing what it actually buys.** Two things are owed before any optimisation is worth starting,
and both are cheap next to the work below them.

### 1. Re-measure the split's suppression and FPS

Every recorded figure for the split predates the 0.3.101 fix, which strictly reduces hiding. Do not
quote 80.5%/77.1% or the 340-to-460 FPS ridge result as current; re-measure before either appears
in a gate. Report a range and its exact conditions rather than a single value: the 2026-08-24
`HzbField` runs showed a seed-sampled figure of this kind moving about seven points on camera
placement alone (G98).

### 2. Close the portability gates the default now depends on

Ordinary play on the primary driver is accepted, which raises the stakes on the two gates that were
never closed:

- **A second GPU driver has never run this path.** It is now the default, so an unsupported or
  misbehaving driver affects a real player rather than an experiment. The capability probe and the
  same-frame fallbacks are the protection, and they have only ever been exercised on one machine.
  This is the single most valuable remaining check.
- **The paired packed route is untimed.** Run `-GpuIndirect 1 -GpuPacked 0|1` over the same route
  and compare GPU opaque time, total frame time, upload time and reported regional bytes. Until
  that runs, the 12-byte format's memory and bandwidth benefit is not separated from its decode
  cost, and the temporary expanded regional mirror cannot be dropped - which is the memory win the
  format was chosen for.

Longer-tail Phase 9 coverage - MSAA and SSAO settings, window resize, fullscreen changes, shader
reload, dimension and world changes, long sessions, large caches, multiplayer and competing-LOD-mod
deferral - is listed under Phase 9 in the renderer plan and is unexercised with the path default-on.

## Renderer optimisation candidates, ranked and not yet funded

Recorded in session 50 at the owner's request. None is started. The ordering is by expected payoff
against effort, and each is explicitly gated on a measurement rather than on argument.

**Note on diagnostics.** The staging switches are retired and the measurement surfaces are not:
every stage keeps an environment override, and the live cull telemetry, GPU stage timings,
section-height counters, distance bands and the offline `HzbField` harness all survive. Session 53
verified against source that no retired command is needed by anything below. The one exception is
item 1, which should get a temporary live toggle of its own - new instrumentation for new work,
retired once the bounds are accepted.

**1. Aggregate vertical bounds for quadtree subtrees.** Phase 3b gave individual sections real mesh
heights, but `LodTraversalPolicy.NodeInView` still bounds whole SUBTREES from bedrock to sky, and
the plan already records the quadtree walk as the largest unattributed per-frame cost. A subtree
needs an aggregate over its descendants rather than one mesh's extent, so it is real work - but
rejecting a coarse node rejects everything beneath it, and looking up or down currently keeps
subtrees a real box would refuse. This improves the established renderer whether or not the fast
path is ever adopted, which is the same argument that made Phase 3b worth doing early. Highest
recommended priority of this list.

**2. Try 16 MiB arena pages.** Page size, not region shape, was the proven lever for batch counts:
8 MiB gave 3.7-4.6x and 32 MiB gave 10.6-11.5x, but 32 MiB pages are only about 49% live against
71% at 8 MiB, so the ceiling must grow faster than the page. 16 MiB is documented as untested and
is exposed as `VINTAGEHORIZONS_GPU_ARENA_PAGE_MB`. This is a measurement run with no code change.

**3. Measure per-frame command/record/box upload volume before optimising it.** Every frame both
buckets re-upload their full command, record and box buffers, because section records are
camera-relative and rebuilt from scratch; clusters multiply commands up to sixteen-fold per section.
That may be immaterial or may be around a megabyte a frame at 400 FPS - nobody has looked. If it
measures material, the structural answer is region-anchored records with the camera offset moved to
a per-frame uniform, which would let most of those buffers upload once instead of every frame. Do
not build that without the number first.

**4. Two-tier cluster culling.** Every far section currently pays sixteen cluster tests of up to 81
texel fetches each, even where the whole-section box would settle the question in one. A
hierarchical test - whole box first, clusters only when the result is neither clearly hidden nor
clearly visible - would cut cull work substantially. Parked: the cull dispatch has never been shown
to cost anything material, so this needs a GPU timing figure before it is worth the complexity.

**Explicitly not recommended, with reasons, so they are not re-proposed:**

- **No third depth stage or previous-frame HZB for far-on-far occlusion.** Split-far already removes
  most far commands, front-to-back order plus early-Z caps the shading cost of what remains, and the
  plan permits only one cached-on-cached strategy. Diminishing returns against real synchronisation
  cost.
- **No verdict hysteresis or temporal damping.** With a conservative mapping restored, a remaining
  knife-edge flip is draw-safe by construction - a hidden thing drawn for a frame, never a visible
  thing missing. Damping would add cross-frame GPU state to suppress something that no longer
  reaches pixels, and would mask the next real conservatism defect.
- **No finer-than-4x4 clusters, no GPU-owned LOD selection, no water batching.** Each is a
  measured-branch decision the plan already gates correctly, and nothing measured so far justifies
  any of them.

## Cached terrain lighting: the sky band is the only untested part left

The four terrain lighting corrections are **done and human-tested** - shipped in 0.3.51, and
the owner reported the result substantially better with nothing else regressed. The
mechanism, the measurements and the disproved theory are in `dev/history/DONE.md` and
session 40; G50 carries the durable rule.

**Owed: one look at the far dissolve band**, the edge where cached terrain fades into sky, at
dusk and at night, flipping `.vhlight sky`. Shipped in 0.3.52 and not yet seen. It is
identical in full daylight, so midday shows nothing. Measured effect: about 20% too dim
through dusk and about 60% too bright on a moonlit night before the correction.

**Open, small, and deliberately not done here:** all five switches default on and none is
persisted. If the owner keeps them on after testing, they can collapse into the shader
unconditionally and the command can go. Nothing depends on that; it is cleanup.

**Sequencing note, now spent.** The Phase 3 fast shader was gated on this lighting being
settled, and it has been written (0.3.53). It is not a port: both paths compile from one
body file and differ only in where the per-section values come from, so a pixel difference
is a wiring bug rather than a transcription slip. The `sky` correction above is the one
part of that body a human has not accepted yet - if it changes, both variants change with
it, which is the point of the arrangement.

## Batched terrain drawing has drawn frames; its A/B has not been run

Phase 3 of `dev/plans/PLAN_GPU_DRIVEN_TERRAIN_RENDERER.md` is complete in source and, since
0.3.69/0.3.70, has been played. Opaque cached terrain draws from the regional arenas with one
multi-draw per page set instead of one call per section, behind `.vhgpu on` and
`.vhindirect on`, saved per install since 0.3.71.

**Established:** the indirect shader variant compiles on the owner's hardware (RX 9070 XT, GL
4.3 core, GLSL 4.60), the batches draw, and the picture was reported correct across two
sessions. The measured draw-call collapse from session 39 stands: 87-182 opaque submissions
per frame become 8-19 multi-draw batches on the frozen route.

**Still owed:** the comparison the phase exists for. Batching suspends the delayed occlusion
queries, which were worth 170 to 500 FPS on a hill view in session 34, so the honest A/B is
batching-plus-culling against the established path with occlusion queries - not batching
against itself. Nothing has run that, which is why the switches are saved rather than
defaulted on.

## Phase 7: portability and final memory policy remain

The source path is complete. Greedy opaque rectangles now have an exact three-word record:
four 7-bit X/Z endpoints, two quarter-block 16-bit heights, three face bits, and the unmodified
32-bit RGBA/tint payload. That is **12 bytes instead of 88**, an 86.4% reduction from the accepted
four-vertex/six-index representation. Eight bytes cannot retain those semantics; sixteen adds no
fidelity. Workers write packed records directly beside the expanded arrays, a separate bounded
regional arena publishes them, and `lodterrainpacked` pulls four unique corners per quad while a
shared index pattern emits the same six triangle indices.

`.vhpacked on|off` provides an immediate, session-only visual comparison. The scriptable gate is
`VINTAGEHORIZONS_GPU_PACKED=0|1` or `bench-windows.ps1 -GpuPacked 0|1`; keep arenas and indirect
drawing on in both halves. A missing shader, arena, page or draw leaves expanded batching active,
and a draw failure repairs the same frame through the established renderer.

**Source evidence:** the fast tier passes 3,911 assertions. The 782 packed-format assertions cover
all six faces, L0/L1/L3/L6, the 14-bit height limit, every tint/material alpha band, quarter-block
cover, degenerate rectangles, rejection cases and exact decoded equality with the expanded mesh.
Arena replacement/fence lifetime, byte-for-byte upload, padded indirect commands, batching and
static shader structure and the indexed-only backend are separately pinned.

**Primary-driver result from 0.3.85:** visual parity passed, but performance did not. The owner
reported the same scene at about 300 FPS expanded and 260 FPS packed, approximately 0.51 ms or 13%
slower. The first backend decoded six independently generated vertices per quad while the expanded
indexed path shaded four unique corners. That draw-arrays implementation is rejected.

0.3.86 keeps the 12-byte record and uses one reusable `0,1,2,0,2,3` index pattern, rebased by four
virtual corners per packed quad. This restores four decoder invocations per quad without restoring
per-section index storage. The owner's same-view comparison reports exact visual parity and the
exact same FPS with packing off and on. The indexed revision therefore recovered the full 0.3.85
loss. Packing is performance-neutral in this scene rather than a standalone FPS optimization, and
the primary-driver format/draw-topology gate is accepted.

**Still owed before portable/default acceptance:**

- Run paired routes with `-GpuIndirect 1 -GpuPacked 0|1`, comparing GPU opaque time, total frame
  time, upload time and reported regional bytes. The decode must not erase the traffic reduction.
- Run a second driver before treating shader-pulling behaviour as portable.
- After the paired route and portability gate, stop retaining the expanded *regional* copy while
  packed drawing is the selected product path. Dual representation is intentional for this A/B
  but is not the final memory state; legacy `MeshRef` geometry remains the complete fallback.

### Phase 8 cluster results: the correctness gate is closed

The implementation, preset ladder, rejected margin search, real-command suppression measurements,
failed 0.3.99 sky guard, and the 0.3.100 capture instrumentation are completed history in
`dev/history/DONE.md` and Sessions 47-50.

**The flicker is fixed in 0.3.101 and human-accepted.** The cause was the HZB texel-mapping defect
recorded as G96, not the cluster ranges, the depth margin, the sky silhouette, or a missing texture
barrier. The 0.3.100 two-bucket capture was never run: the defect was found by source tracing and
proven on a deterministic fixture instead. `.vhflicker` remains available and is now most useful for
confirming that the sky guard has gone idle.

Remaining Phase 8 work is measurement and cleanup, not correctness. `.vhsplitbias` is gone as of
0.3.102 and the sky guard now counts itself; re-measure cluster suppression and FPS on the
corrected mapping before quoting either, and delete the guard once its counter reads zero, both as
described under the top priority. Metadata/dispatch cost against work removed, and turning without
remesh storms, remain the phase's open gates.

Do not repeat the stage ladder or split-bias values, re-add a sky guard, or add a texture barrier
without new evidence. The margin ladder behaved the same at 4/16/64/256 steps because the flip was
between real depth and the exact clear value; the guard inspected outside the rectangle while the
hole lay inside it; and the HZB reduction reads and writes disjoint, explicitly clamped mip levels,
so the same-texel feedback condition for a barrier has never been established.

## Re-mesh: warm-up is the only large mesh cost left, and nobody has looked at it

The change-locality fix and the withdrawal of this section's old headline claim are recorded
in `dev/history/DONE.md` and session 40. Short version: mesh rebuilds per content change fell
from 5.00 to 2.00-2.03, measured over three runs; and the "90 MB every 15 seconds at a
standstill" symptom this section used to lead with was a warm-up measurement recorded as a
steady-state one. G62 and G63 carry the two durable lessons.

**What is still open.** Warm-up is now the only large mesh cost in the profile: about
**237 MiB per 15 seconds for the first two and a half minutes** after joining, while 781
meshes are built for 3,291 cached sections. Nobody has asked whether that period is smooth.
It is the obvious candidate for the join-time stutter recorded further down, and the two
should be investigated together rather than separately.

**Also open, and cheap:** the one-edge-per-change result comes only from a frozen, warm
profile, which produces no first-time captures. A cold or moving route should show two edges
per change and a smaller saving; `ChangeLocalityChecks` pins both cases, but neither has been
run in game.

**Corrected in the GPU plan too.** `dev/plans/PLAN_GPU_DRIVEN_TERRAIN_RENDERER.md` carried
the same withdrawn inference and has been fixed, with the consequence spelled out there:
arena retirement and reclamation must be sized for a burst during load rather than a
sustained trickle, and settled play does not exercise that path enough to validate it.

## The micro-hitches: dozens per second, and current instrumentation cannot see them

The owner reports (2026-08-22, from earlier playtesting) consistent micro-hitches on his
frame-time graph occurring **dozens of times per second**, and wants them gone; a fully
smooth mod is the stated end goal. This is a distinct symptom from the several-seconds-apart
spikes recorded below.

Nothing yet attributes them. What is known:

- The stationary sandbox runs show real frame-time instability: `fps_avg` 386 against
  `fps_1pct_low` 195, i.e. **the worst 1% of frames take about twice the average**
  (`frame_ms_avg` 2.59 vs `frame_ms_1pct_low` 5.12), in every one of the six views and in
  all three runs.
- **The existing hitch counters cannot see this.** `Over25Ms`/`Over50Ms`/`Over100Ms` are
  the only hitch thresholds, and at 400 FPS a whole frame is 2.5 ms. A 426 us mesh upload
  is a 17% frame-time spike and is counted by nothing. The per-phase p95/p99/max
  microsecond histograms are the only instrument with the right resolution.
- **Mesh upload is no longer the leading suspect, and the section above is why.** In
  settled steady state a standing camera does about **three mesh uploads per 15 seconds**,
  several intervals doing none at all - not the ~10/s previously assumed, which came from
  the warm-up period. Three uploads of 100-426 us in fifteen seconds cannot produce dozens
  of hitches per second. It remains a candidate under movement, where capture and meshing
  are genuinely busy, but not while standing still - and the owner's graph shows the
  hitches while standing still too.
- Ruled out for the stationary case: garbage collection (about 0.7 gen0 per second), and
  now re-mesh and upload work as well.
- **Still unattributed, and now the largest unexplained per-frame cost:** the quadtree
  walk at 95 us average and 393 us max per frame, and the readiness shadow at 27.8 us
  average and 710 us max, both from the 2026-08-22 stationary run. At 2.4 ms per frame a
  single 710 us readiness spike is 30% of a frame. These run every frame and their maxima
  are the right order of magnitude for the reported symptom, unlike mesh upload, which is
  too rare.

**The instrument now exists (0.3.55), and it has never been read.** `LodFrameTimeline`
measures three things per frame and reports them on the periodic `frame timeline:` line:
the interval between consecutive render frames, this mod's share of that frame, and how far
the interval ran over its own moving average.

Two design points that matter when reading it. The excess-over-baseline histogram exists
because the shared per-phase histogram's fine 25 us buckets stop at 1 ms, so a 2.5 ms frame
interval is quantised to 250 us and could not resolve a 400 us hitch at all; the excess is
small by construction and lands in the fine buckets. And spikes are judged against a moving
average rather than a fixed threshold, so the same rule means the same thing at 400 FPS and
at 60, and a world load is excluded rather than counted.

**What to read first:** `SlowFrames` against `SlowFramesWithSlowMod`. If frames stand out
but our own callback was ordinary during them, the hitches are not ours and the next session
belongs somewhere other than our phase costs. That is the question that has been unanswered
since the symptom was first reported, and one ordinary session of play now answers it.

If they ARE ours, the per-phase p95/p99/max lines beside it already point at the phase - the
quadtree walk and the readiness shadow are the standing suspects, at 95 us average / 393 us
max and 27.8 us average / 710 us max respectively. Then run
`bench/routes/moving-rotation.txt` for the moving case, where mesh upload is still a live
candidate.

## Validate the periodic-stutter changes in game

Source audit found that ordinary dirty activity could admit six save snapshots every game
tick and write each row independently, while several unrelated whole-collection sweeps
landed on fixed frame/tick intervals. The implementation is complete: 30-second bounded RAM
checkpoints and one SQLite transaction, incremental seasonal sampling, rolling GPU/CPU
eviction, capped render-context queries, a 30-second server-manifest scan, and worker-owned
singleplayer sibling-cache blob reads. Build and 1,555 fast assertions pass.

Still owed is human/runtime evidence. Compare an ordinary moving session with the prior
build and specifically report:

- whether the several-times-per-second tiny spikes are gone or reduced;
- whether the larger three-to-six-second spikes remain;
- whether a new burst appears around the 30-second checkpoint;
- whether seasonal colour changes remain visually smooth; and
- whether turning around after long travel shows delayed mesh recovery or excess RAM.

If a periodic spike remains, capture phase telemetry before changing more cadence. The
readiness tracker/mask still has owning-thread game queries and a once-per-second authority
resync; GPU uploads and live registry publication also must remain on their owning threads.
Do not attribute an unmeasured residual to disk merely because its interval is regular.

## The mask default is settled; what remains is coverage, not the decision

**The band is closed** (0.3.16, human-confirmed 2026-08-19). It was the engine's per-frame
range cull, which no per-chunk signal reflects; see G43 and session 29. **The mask became the
default in 0.3.17** on the owner's decision.

**The frame-rate question is now answered well enough to stop blocking on it** (2026-08-20,
in-game averages on 0.3.23, mask off against on):

| vanilla render distance | mask off | mask on | |
|---|---|---|---|
| 320 | ~315 FPS | ~310 FPS | mask costs ~1.6% |
| 1024 | ~180 FPS | ~190 FPS | mask gains ~5.6% |

The sign flip is the useful part, and it is not "the mask culls more at range" - that would
predict a gain at both distances. With the mask off, cached terrain is suppressed inside a
plain circle; with it on, that circle is pulled in to `MaskNearFloor()` and the per-cell mask
decides instead, so the mask SUBMITS MORE cached geometry and discards it per fragment. That
trade is paid in fragment shading, which is GPU work. At 320 the scene is CPU-bound at
~315 FPS, so only the mask's own overhead is visible; at 1024 it is GPU-bound and the saving
dominates. See G52.

**The default is therefore defensible in the regime that matters:** the distance where the
mask costs anything is a distance nobody runs this mod for. The owner's verdict was that the
difference is negligible either way and the picture is much better, so it stays.

Still owed, but as ordinary coverage rather than a blocker:

- **A controlled benchmark, demoted.** Two samples, one per condition, no alternation, and
  the in-game average may have started before the mask texture finished rebuilding - which
  penalises the "on" figure, so the 1024 result is if anything understated. Run it with
  `-ChunkMask` against a run without; the harness pins the variable in both directions since
  0.3.17, so an unflagged run measures the radial path rather than whatever the sandbox had
  saved. Nothing has been benchmarked since 0.3.9, so the culler rule, the atlas resync, the
  air exclusion, the draw-range clause and the stored exclusion bit still have no number.
- **The visual matrix.** Water, shoreline and cliff were confirmed individually on 0.3.23.
  Boundary flicker, approach popping, cave and structure have never been.
- **Anything that only appears away from this machine:** multiplayer, other drivers, long
  sessions. A default reaches all of them.

Reverting the default remains one line and the saved setting keeps working, but there is no
longer a live reason to expect that to be needed.

### Standing constraints

- Keep air owned in the tracker. The column aggregate feeds the radial handoff and 0.3.3
  proved what happens when it collapses. Exclude air from the mask texel only, and keep the
  exclusion stored per cell so the wholesale rebuild honours it without a chunk lookup (G45).
- Any new ownership rule stays gated on `ChunkMaskEnabled` until it is visually confirmed.
- Do not trust a CPU-side diagnostic to prove a rendering subsystem healthy. Through two
  sessions every one of them reported health while a band of world was missing, because they
  all consumed the same incomplete signal. Make the picture answer.
- Before adding an ownership rule from reasoning, read the engine's IL. Every confirmed
  finding across sessions 28 and 29 came from the decompiled game or from the owner's
  screen; none came from reasoning about the mod.

### Reading the periodic log

- `stale committed found` non-zero once settled means committed ownership is surviving after
  vanilla stopped drawing and the one-second re-confirmation is not reaching those cells.
- `count repairs` should always be zero; any figure is a bug in the ownership aggregate.
- `mask resyncs` should settle near zero. A steady non-zero figure means an incremental
  update path into the atlas is still missing and the once-per-second rebuild is hiding it.
- `denied beyond view distance` counts cells refused ownership because the engine
  range-culls them. A steady non-zero figure while moving is normal and is the trailing
  annulus being released; zero while travelling means the draw-range clause is not running,
  which is how the band returns.
- `drawn-but-empty chunks` is expected to be large and means nothing is wrong: it counts
  sky. Read the corrected G40 first.
- `drawn-without-geometry` and `owned without geometry ... per Y` are dominated by buried
  chunks with no exposed faces, which hold no mesh and are invisible. A blanket rule denying
  those cells ownership would strip it from everything underground and repeat 0.3.3.

## Renderer scaling after delayed occlusion

The accepted current-renderer sequence remains
`dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md`; the staged follow-on is
`dev/plans/PLAN_GPU_DRIVEN_TERRAIN_RENDERER.md`. Phase 0 capability feasibility and Phase 1
source/harness work are complete and recorded in `dev/history/DONE.md`. Open work is:

- Owner-check that the next correctly versioned playable artifact is visually and
  behaviorally indistinguishable with `VINTAGEHORIZONS_GPU_RENDERER=off` and `shadow`,
  including shader reload, a world change, mod deferral and shutdown.
- Let the owner establish FPS baselines/noise. A temporal-occlusion comparison must place
  cached terrain behind vanilla foreground and prove the client is uncapped (G58); the
  aerial Bodanboys pair does neither feature attribution nor Phase 1 acceptance.
- Do not begin a visible fast path until its phase is approved. Phase 2's real regional GL
  shadow buffers also need an explicit bounded dual-residency memory ceiling.

The renderer rejects off-screen quadtree nodes, distance-capped sections, fully
vanilla-owned sections and back-facing opaque triangles; submits opaque sections nearest
first; runs after vanilla terrain; and reuses asynchronous exact-geometry query results to
skip later opaque submissions. Completed rendering narrative lives in `dev/history/DONE.md`.
The accepted owner evidence is:

| change | off | on | frame-time reduction | owner verdict |
|---|---:|---:|---:|---|
| opaque back-face culling | 218 FPS | 260 FPS | about 0.74 ms | no visual difference |
| opaque front-to-back submission | 149 FPS | 173 FPS | about 0.93 ms | no visual difference |
| post-vanilla cached pass | 148 FPS | 179 FPS | about 1.17 ms | minute distant changes; entirely acceptable |
| delayed exact-geometry occlusion, stationary | about 170 FPS | nearly 500 FPS | about 3.88 ms | accepted after motion/seam/edge guards |

All four are default-on in 0.3.37, with `.vhbackface off`, `.vhfront off`, `.vhocclusion off`,
and `.vhtemporal off` as session-only fallbacks. Delayed occlusion defaults to the aggressive
profile; `.vhtemporalprofile safe|aggressive|extreme` exposes the safety and artifact limits.
The first three rows are same-view A/Bs. The temporal row and its roughly 250-350 FPS motion
range are sequential owner playtests across evolving builds and areas, not a controlled
benchmark. The rejected same-frame proxy/query prototype remains rejected: delayed queries
over real geometry after vanilla depth are a different design. Remaining work:

- Run a controlled alternating 0.3.37 `.vhtemporal off/on` comparison in the same settled
  view, plus a second pair during sustained streaming. Quantify the final edge guard rather
  than inferring its cost from the earlier profile ranges.
- Repeat on another driver/machine and cover multiplayer, caves/structures, vertical
  look transitions, long turns, teleports and long sessions. The accepted evidence is one
  owner, machine and world.
- Watch `.vhinfo`: global invalidations should follow profile/view thresholds rather than
  mesh-upload cadence; stale results may occur but must never hide terrain; seam and
  turning-edge protection should rise only in their intended cases.
- Measure remaining CPU draw-submission time before choosing regional combined buffers,
  multi-draw, or instancing. Use the proposed GPU-driven plan's Phase 0 noise floor and
  independent regional/HZB go/no-go gates rather than treating the whole vision as one
  rewrite. Keep visibility independent from residency and persistence.

### Existing near-handoff coverage

The approved rendering design is
`dev/plans/PLAN_CHUNK_AWARE_VANILLA_HANDOFF.md`. Its phases are now built: GPU mask
publication, shader sampling and the CPU whole-mesh skip all exist and, since 0.3.6,
actually run, and since 0.3.16 they do so without the band. What remains is evidence and the
ordinary coverage recorded at the top of this file; the ship/shelve decision is closed.

Measurement still owed:

- Convergence of the readiness-driven handoff at join and after a teleport. The derived
  radius starts small, showing more cached terrain near the camera than the old constant
  did until the tracker converges; duration and visibility are unknown.
- The single-unowned-pocket case: one column near the camera that never becomes ready
  collapses the global radius and restores overlap everywhere. How often that happens in
  play is the evidence for whether the per-cell mask earns its cost at all.
- Teleport and live view-distance change at runtime; both are harness-only today.
- Whether state-agnostic maintenance and the derived handoff cost measurable frame time.
  Repeated alternating runs are required; the existing pair is one run per side inside the
  lap spread.
- Cache-only, vanilla-settled, moving-frontier, large-cache, teleport and
  view-distance-change scenarios, before claiming neutral or improved performance. Nothing
  has been benchmarked since 0.3.9, so the culler-based ownership rule, the once-per-second
  atlas resync and the air exclusion are all unmeasured.
- Independent residency must stay preserved: suppressed fallback should remain warm without
  triggering unload, reload or remesh churn.

Human-reported and still open:

- Approaching terrain fast, cached and vanilla briefly fight where vanilla has just loaded.
  This is ownership gain latency, resolves within a moment, and fails in the safe direction,
  so it is accepted. If it becomes objectionable, confirm cells near the camera on a shorter
  path rather than widening the budget again.
- **Land colour: four causes found and fixed, 0.3.18 to 0.3.20.** The tile-to-tile step is
  human-confirmed fixed ("it looks so much better"); the follow-up green correction in
  0.3.19 has not been seen yet. Original diagnosis follows. Neighbouring cached
  sections rendered as dramatically different flat colours - one green, the one beside it
  brown. `Block.GetColorWithoutTint` answers grass-covered ground with a RANDOM pixel of the
  grass texture, and a palette entry is registered once per section, so each section painted
  its whole surface with its own draw. Measured in the 2026-08-19 cache: 38 stored colours
  for `soil-low-normal` across 1,041 sections, per-channel sd 30-39, while deterministic
  blocks had sd 0. One colour per block id now, averaged and cached, with existing caches
  corrected on load. See G46.

  The same person then reported the agreed-on green itself slightly off, and named the season
  as a suspect, correctly. A seasonal colour map is sixteen shades per point in the year with
  the row picked per block from a position hash, so a field is all sixteen mixed and one
  sample was up to a quarter off in red. 0.3.19 averages the tint over 64 positions. See G47.
  A screenshot on 0.3.19 showed distant grass still substantially greener than vanilla's,
  and the owner guessed the grass and tree colours looked swapped - which was literally true.
  `GetRandomColor` and `GetAverageColor` return opposite channel orders (G48), and grass-
  covered ground is the main thing the engine answers with the former, so grass alone had red
  and blue exchanged. Under that sat a larger fault: vanilla composites untinted dirt with a
  two-thirds-opaque grass overlay and tints only the grass (G49), and the mod tinted
  everything, which removed the olive and nearly all the blue. 0.3.20 fixes both.
  0.3.20 was reported better but still not perfect, and the reason was measurable:
  `GetAverageColor` samples four pixels, and for the see-through grass layer those four also
  decide how much dirt shows through - 0.573 against a true 0.687, a fifth too much dirt.
  0.3.21 reads the whole texture and now reproduces vanilla's blend exactly.
  **What the playtest has to say:** whether distant grass now reads the same as the meadow
  underfoot, and whether sparse/very sparse ground and the non-grass surfaces still look
  right - the change touches every block vanilla draws in the TopSoil pass.

- **Lighting, not colour, was the next thing - and it is resolved.** 0.3.21's albedo was
  accepted as correct, and the owner then found the match held at some times of day and broke
  at others by sweeping the daylight cycle with the season held still. That is the lighting
  half: vanilla never darkens up-facing ground as the sun drops and the mod did. 0.3.22
  adopts vanilla's rule, and the owner confirmed it across the daylight cycle and asked for it
  to stay default. Still unjudged: cached water takes the same floor and was not looked at
  separately, and no one has checked whether flat ground now reads too bright at dawn against
  its own surroundings rather than against vanilla.

- **If the colour is still off at a fixed time of day, this is the remaining candidate.** The mod takes
  its tint from `ApplyColorMapOnRgba`; terrain is actually drawn through `calcColorMapUvs` in
  `colormap.vsh`, and the two compute the climate/season blend weight differently. The C#
  version's `Math.Max(0, 128 - temp) / 512` and `Math.Max(0, temp - 130) / 200` are INTEGER
  divisions and evaluate to zero for every reachable temperature; the shader computes both as
  floats and adds an altitude term. At a temperate summer temperature that is roughly a third
  more seasonal colour in the mod's tint than vanilla draws. Unquantified because it needs the
  world's actual temperature and rainfall. It biases grass and leaves together, so the tell is
  an overall hue shift rather than grass alone being wrong. The `(rain, temp)` overload cannot
  fix it - see G47 - but the mod can sample the climate map alone and blend it against the
  season map with the shader's own weight.
- **Water seams are fixed and human-confirmed (0.3.23); one measurement is still owed.**
  See G51 and session 31 for the cause. The 2026-08-20 log reads `974 seam repairs` beside
  `599 meshes` at the 30-second mark - non-zero as designed, and the same join reached its
  100-mesh mark in 6.6 s, so the repairs did not cost visible fill-in time. Whether the count
  SETTLES is still unknown: `Stats after 30s` fires once, so one sample is all an ordinary
  session produces. A second reading needs `VINTAGEHORIZONS_STATS=1`, which prints every 15
  seconds; a figure still climbing after terrain stops arriving would mean a repair is
  re-queueing itself.
- Re-run the visual matrix. The 2026-08-18 playtest was reported acceptable overall, but
  seams, boundary flicker and approach popping were not separately confirmed, and no cliff,
  water, cave or structure case was reported individually. Nothing in the matrix has been
  re-run since the mask began working. Water, shoreline and cliff were each confirmed
  individually on 0.3.23; seams elsewhere, boundary flicker, approach popping, cave and
  structure remain unconfirmed.

### Documentation and baseline

- Repeat the recorded hardware/settings/config benchmark on the eventual release candidate if its code or sandbox state differs materially.

### Renderer scaling

- Human-check clipping and turn-around behavior on the thousands-section build. Automated
  scaling now covers 3,132 persisted sections; the controlled 601-section pair remains the
  causal traversal comparison.
- Complete the owner runtime-equivalence check for Phase 1 using the next correctly
  versioned playable artifact. Keep owner-run FPS/noise evidence separate from source and
  capability evidence. After that gate, seek approval before Phase 2 creates real regional
  GL shadow buffers; no visible fast-path phase is approved.
- Decide whether startup configuration should change from unlimited cached drawing. The
  `.vhconfig` scale and its `Defaults` button now use 32,768 blocks, but that player-facing
  choice is not a controlled far-cap benchmark. The mask's benefit scales with vanilla
  render distance (above), so the two interact: a larger cap makes the mask worth more,
  not less.

### Low-priority coverage and verification debt

- Keep the report of cached terrain becoming coarser during extreme fast flight. The owner
  has played extensively since and does not see it in normal gameplay; those speeds are not
  normally achievable, so this is deliberately demoted rather than removed. If it becomes
  visible again, `.vhcoarse` already separates storage, mesh-worker, scheduling-slot and
  unexplained waits. Read `coarse cover waits` before changing a budget.
- The brief cached/vanilla fight on extremely fast approach remains recorded for the same
  reason. It resolves toward safe overlap, not a hole, and is outside ordinary movement.

## Flagged decisions awaiting human evidence

- **The arenas are gated on capability the indirect path does not use.**
  `LodGpuCapabilityPolicy.Evaluate` only reports `Tier1RuntimeValidated` when the HZB
  requirements - compute shaders, image load/store, a validated depth copy - all pass, and
  the arenas only attach behind that flag. Indirect drawing needs multi-draw and the arenas
  and nothing else, so on a driver with MDI but no compute it would be refused for no
  reason. Not touched here deliberately: that flag also gates the measurement shadow whose
  accepted numbers were taken under it, and everything validates on the owner's machine, so
  changing it now would alter a validated path to fix a case nobody has hit. Revisit when
  Phase 4 splits the gates anyway, or if a second machine refuses.
- Should new/startup configuration remain unlimited, or adopt the `.vhconfig` Defaults
  value of 32,768 blocks after a controlled benchmark and playtest?
- Does visual quality permit more aggressive off-screen GPU eviction without noticeable turn-around stalls?
- Is the hybrid's one-time chunk handoff pop preferable to any residual overlap? The
  "cliff/water seams" half of this question is closed: the seams were the mesher's frontier
  rule, not the handoff, and both cases were confirmed clean on 0.3.23.

## Verification debt

- The readiness tracker has five isolated runs across moving and stationary routes: no probe
  errors, no dropped events, no renderer phase at 25 ms, 18-28 microseconds average frame
  cost, zero steady-state allocation after a single 377 KiB construction, and 232 of 441
  columns fully owned. A recurring multi-millisecond outlier is attributed to a blocking
  `IsChunkRendered` call by inference only. Teleport, live view-distance change, multiplayer,
  and long soaks remain uncovered.
- The readiness-driven handoff held 192 blocks stationary and 160-192 blocks moving against
  a 64-block constant, with zero fallback samples and the applied radius never exceeding the
  measured ownership bound. Frame rate is neutral within noise rather than proven neutral,
  and no CPU or GPU saving exists yet because suppressed fragments are still rasterized.

- Two one-way 1,600-block capture-frontier runs started with zero active VH databases and
  `0 sections from cache`. Both had zero VH ticks at or above 25 ms; capture backlog stayed
  within 20 results / 1.61 MiB / 234 ms and converged during the endpoint cooldown. The
  first run generated server-save terrain and the second reused it, so aggregate FPS is
  not controlled A/B evidence. No person watched the cold route in motion.
- Asynchronous mip propagation now has a 120-second movement/capture convergence soak,
  graceful restart, deliberate interruption after a durable `ApplyToParent` write,
  successful recovery of one persisted obligation, and a third fresh process reporting
  zero obligations. Both dedicated client/server and integrated-singleplayer durability
  are established for the guarded routes.
- The complete game-backed Release tier passes 1,533 assertions, including the Session 24
  handoff/shader coverage, readiness and water-frontier suites, delayed-occlusion state/view/
  seam/edge coverage, and static renderer/query/default wiring. A real game process still
  supplies the only end-to-end proof of callback ordering, renderer cost, and GPU behavior.
- The world-stable color coordinate, removed approach sink, and conservative radial
  handoff have source/build evidence and a packaged playtest. The user found the first
  render-fixes package better but still observed cached/vanilla mixing; the latest radial
  package has no completed human result. The hybrid's readiness model now has source/
  harness evidence and pixel-neutral renderer wiring, but runtime convergence/timing,
  visual seams, GPU cost, and net performance remain open.
- Live server-assist transfer exercises network request state end to end. An integrated
  command-generation run discovered 211 sibling keys, forced one retryable miss, and
  installed that exact key plus 62 others. Natural miss frequency and default-sweep
  sibling-cache behavior remain unmeasured.
- Cached bounds and projection hysteresis pass 30 isolated assertions. The corrected long
  warm-cache route completed with five projection resets and no tick hitches, and human
  review found motion smooth with no noticed clipping. The earlier route screenshots were
  sky-biased; cold terrain/coverage arrival still needs visual review.
- Visibility-aware traversal passes source/harness checks and a controlled same-cache
  601-section route: selected nodes fell 64.2%, average traversal 19.8%, and average draw
  submission 9.3%, while both sides retained 543 meshes with zero evictions. A later
  3,132-section automated route bounded renderer queues and converged, but aggregate FPS
  is not a controlled comparison and human motion review remains.
- Incremental render-dirty scheduling passes 20 focused assertions and a functional
  601-section moving/rotation route that settled every waypoint and converged all guarded
  queues. A later 3,132-section route supplied functional scaling evidence, but there is
  no controlled old/new scheduler comparison or human review of this build.
- Mesh snapshot/upload budgets pass deterministic accounting/progress checks and a
  zero-warning Release build. A 12,800-block cache-growth route processed 94,285
  snapshots/uploads, bounded sampled queues to 18/four items, measured direct GL upload
  below 6.9 ms, recorded no 25 ms renderer phase, and converged. No person watched visual
  replacement behavior and no second driver has been measured.
- Revisioned persistence passes injected failure/retry, repeated mutation, pending
  coalescing, 300-key drain, and newest-row restart checks. A 3,132-section runtime route
  reopened the cache, wrote 138 revisions, and ended with zero unsaved/backlog/errors.
  Persistent-failure timeout reporting has source/check evidence but has not been forced
  inside a game process.
- Capture publication is source-bounded and passed a warm-cache same-route before/after
  run: its maximum fell from 12.038 to 5.732 ms and backlog peaked at 9 results / 0.70 MiB /
  93 ms. One admitted result remains non-preemptible; unseen terrain and interrupted
  shutdown were not exercised.
- A warm-cache join adopted 558 sections with an 11.180 ms worst Vintage Horizons tick
  and no 25 ms hitch. Background-load backlog reached 181 sections / 51.93 MiB / 11.531 s
  old and drained by 30 seconds. This is one client-only warm sample, not a cold/warm A/B
  comparison. Integrated recovery/postcheck processes later loaded 168 sections and
  converged, but they were correctness routes rather than join-performance evidence.
- A pinned dedicated-server sweep examined 3,249 dependency-aware positions, loaded 1,018
  existing columns, skipped 377 frontier columns, generated nothing, and verified 256/256
  sampled absent positions. Server pipeline ticks peaked at 17.874 ms with no 25 ms hitch;
  probe/load issue maxima were 3.945/6.004 ms. The server cache was warm, and
  savegame-sweep cadence remains unprofiled in integrated singleplayer.
- Completed transient generation produced 289/289 columns with zero timeouts or unusable
  height maps and preserved 256/256 sampled absences. This is one radius-8 dedicated-server
  absence-preservation run. A radius-12 integrated run separately generated 414 columns
  with no timeout/height-map failure and fed live sibling adoption, but all absence samples
  were excluded near the player; neither is a long/default-radius soak.
- Saturated assist now has off-thread-reader separation plus correlated setup/publication/
  admission, send, allocation, and GC-crossing evidence. The reproduced progress-log tail
  is fixed, but the old 32.450 ms sample lacks its raw correlated log and cannot be
  relabelled conclusively. All accepted runs cover one machine and one player at the
  elevated 64/s stress rate; default-rate, multiplayer, long-soak, and human review remain
  open.
- Two warmed steady-stationary on/off pairs measured about 0.7% lower average FPS and
  1.0% lower median FPS with allocation telemetry at roughly 445 uncapped FPS. Their 1%
  lows reversed direction, and ordinary capped-frame-rate overhead remains unmeasured.
- GPU bottleneck attribution remains unmeasured; CPU/render-thread findings must not be presented as proof that the shader or GPU is innocent.
