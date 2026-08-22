# Vintage Horizons — TODO and verification debt

> Tier 2 companion: open work only. Completed narrative moves to `dev/history/DONE.md`; current conclusions belong in `STATUS.md`.

## Re-mesh amplification: 64 real terrain changes cost 5,057 mesh rebuilds

Measured 2026-08-22 on 0.3.49, sandbox, frozen `bodanboys` profile, camera stationary at six
fixed viewpoints for about six minutes.

**What is measured, not inferred:**

- Diffing the post-run client cache against the frozen seed it started from: **64 of 3,291
  sections had genuinely different terrain data** (40 at L0 plus 24 mip ancestors), and no
  new sections were added. The world was effectively static.
- The renderer published **5,057 mesh replacements** over the same window (mirror
  `replaced` counter), against 781 live meshes.
- Cost: **204-289 MiB of mesh data built and 76-119 MiB uploaded to the GPU every 15
  seconds**, continuously, at a standstill. Individual uploads are 100-125 us p95, 426 us
  max.
- Garbage collection is **not** the mechanism here: about 10 gen0 per 15 seconds, one gen2
  in the whole run.
- Capture ran throughout (~30 capture batches per 15 s) but almost all were no-ops:
  `LodSection.ReplaceColumns` compares packed runs and only reports a change when the bytes
  differ.

**The amplification is arithmetic out of the source, and it is a deliberate deferral.**
`LodWorld.MarkChanged` marks the changed section render-dirty **and all four neighbours
unconditionally** - the comment says "conservatively refresh all four (change locality
tracking can come later)". A neighbour's mesh does hide faces against our edge columns, so
the dependency is real, but nothing checks whether the change was anywhere near an edge.
Then `LodWorld` line 580 calls `MarkChanged(parentKey)` when a mip result changes the
parent, so the same five-way fan-out repeats at every level of the pyramid: **up to 35
rebuilds from one changed section.**

**Inferred, not measured:** ~145 change events across those 64 sections (about 2-3 each, as
neighbouring vanilla chunks stream in and feed one section a slice at a time). 145 x 35
matches the observed 5,057, but no counter records actual `MarkChanged` calls. Add one
before quoting the amplification factor as measured.

**The available fix.** `ReplaceColumns` already walks column by column and knows exactly
which columns changed; it just does not report whether any were on an edge. Returning the
touched edges would let an interior change rebuild one section instead of five, at every
level. Unknown: what fraction of real changes are interior. The saving could be most of the
5,057 or a fraction of it, and it is background work rather than frame work, so it may show
up as fewer hitches rather than higher frame rates.

**Next step:** add a `MarkChanged` counter and an edge-touch mask, then re-run the same
frozen route. The route, profile and diff method above are reproducible as-is.

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
- Mesh upload is the leading suspect by shape: ~10/s while stationary at 100-426 us each,
  and far more under movement, when capture and re-meshing are at their busiest. The
  amplification recorded above multiplies exactly this work.
- Ruled out for the stationary case: garbage collection (about 0.7 gen0 per second).

**Next step:** run `bench/routes/moving-rotation.txt` on the frozen `bodanboys` profile with
GPU stats on, and compare per-phase p95/p99/max and 1% lows against the stationary baseline
already recorded. Movement is when the suspected mechanism is loudest, and the owner's
observation was made while playing normally, not standing still. Add a frame-time histogram
with sub-millisecond buckets if the phase histograms cannot attribute it.

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

## Cached terrain slow to appear after joining — recovered, on one sample

Reported 2026-08-19 on 0.3.7: `Fill-in: 100 meshes after 36.4s` against `6.1s` on 0.3.4 with
the same cache and manifest. The suspect was ours - the drawn-without-geometry diagnostic
added in 0.3.5 called `BlockAccessor.GetChunk` per probe, taking `ClientWorldMap.chunksLock`
while the world was coming up - and 0.3.8 restricted that lookup. Nobody checked afterwards.

**The 2026-08-20 client log reads `Fill-in: 100 meshes after 6.6s` on 0.3.23**, against the
same 3,016-key manifest (`3016 from cache`). That is the 0.3.4 baseline, so 0.3.8 appears to
have fixed it and the regression is not live.

What is left is confidence, not investigation: one join, one machine, one world. If it ever
comes back, the number is in the client log of any ordinary session and 6.1 s is the
baseline - bisect residency against 0.3.4 rather than assuming the diagnostic again.

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
