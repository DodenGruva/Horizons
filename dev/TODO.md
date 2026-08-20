# Vintage Horizons — TODO and verification debt

> Tier 2 companion: open work only. Completed narrative moves to `dev/history/DONE.md`; current conclusions belong in `STATUS.md`.

## Top priority — cached terrain is slow to appear after joining

Reported 2026-08-19 on 0.3.7: no cached terrain at all for a while after loading in, and it
only appeared after flying around. Never seen before.

The user's log for that session says the mask was **off** and `handoff 0 blocks`, so
ownership was suppressing nothing and this is not the mask. It is residency or meshing:
`Fill-in: 100 meshes after 36.4s`, against `6.1s` on 0.3.4 with the same 3,012-section
cache and the same 3,016-key manifest.

One suspect, unproven, and it is ours: the drawn-without-geometry diagnostic added in 0.3.5
called `BlockAccessor.GetChunk` for every probe. That takes `ClientWorldMap.chunksLock`,
the same lock `IsChunkRendered` has just taken and the same one the chunk loader wants,
and the probe queue is at its longest while a world is coming up. 0.3.8 restricts the
lookup to observations that can change ownership. Whether that is the cause is unmeasured.

Measure it from an ordinary join rather than a benchmark: `Fill-in: 100 meshes after Xs`
in the client log is the number, and 0.3.4 is the baseline at 6.1 s. If 0.3.8 does not
recover it, bisect residency against 0.3.4 rather than assuming the diagnostic.

## Top priority - the mask is the default now, and its evidence has not caught up

**The band is closed** (0.3.16, human-confirmed 2026-08-19). It was the engine's per-frame
range cull, which no per-chunk signal reflects; see G43 and session 29. **The mask became the
default in 0.3.17** on the owner's decision: the band was gone and the seam overlap went
unnoticed in play.

That decision is made and is not reopened here. What it outran is the evidence, and the debt
is now shipping to players rather than sitting behind an opt-in:

- **A benchmark, and it is now the first priority rather than one input among three.**
  Nothing has been measured since 0.3.9, so the culler rule, the atlas resync, the air
  exclusion, the draw-range clause and the stored exclusion bit are all unmeasured. The only
  performance evidence is one controlled stationary pair at +7.3%, taken before most of them
  existed. Run it with `-ChunkMask` against a run without: the harness pins the variable in
  both directions since 0.3.17, so an unflagged run measures the radial path rather than
  whatever the sandbox had saved.
- **The visual matrix, unexercised since the mask began working.** Seams, boundary flicker,
  approach popping, cliff, water, cave and structure cases. One flight on one machine is the
  whole of the current visual evidence.
- **Anything that only appears away from this machine:** multiplayer, other view distances,
  other drivers, long sessions. A default reaches all of them.

If a benchmark shows the mask costs frame rate rather than gaining it, reverting the default
is one line and the saved setting keeps working; do not treat the default as load-bearing
before it is measured.

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

## Top priority — main-thread stutter and renderer scaling

The approved implementation sequence is `dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md`.

### Chunk-aware cached-to-vanilla handoff

The approved rendering design is
`dev/plans/PLAN_CHUNK_AWARE_VANILLA_HANDOFF.md`. Its phases are now built: GPU mask
publication, shader sampling and the CPU whole-mesh skip all exist and, since 0.3.6,
actually run, and since 0.3.16 they do so without the band. What remains is evidence and the
ship/shelve decision recorded at the top of this file.

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

- **If the colour is still off after 0.3.21, this is the remaining candidate.** The mod takes
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
- Cached water shows the boundary of every chunk, with colour differing across those
  boundaries. This is a SEPARATE report from the land colour above and is untouched by the
  0.3.18 fix - water's palette colour was already stable (`water-still-7`, sd 4). One cheap
  experiment separates the two candidates: if the seams disappear with `.vhmask off`, the
  mask is drawing cached water only in unowned cells and the 32-block ownership edges are
  visible on a flat surface that hides nothing; if they persist, it is the existing
  per-section water tint and predates this work. A third candidate worth eliminating first:
  cached water is drawn at 66% alpha, so anywhere cached and vanilla water overlap it blends
  twice and reads as a different colour from where only one of them draws.
- Cached terrain becoming coarser than expected during fast flight. `.vhcoarse` reports why:
  a parent keeps covering ground when a visible child with data has no mesh, and each
  interval logs whether those children were waiting on storage, a mesh worker, a scheduling
  slot, or nothing, beside the three backlog depths. Read `coarse cover waits` from a
  fast-flight run before changing any budget; the per-frame schedule cap only binds below
  roughly 60 FPS. Level selection happens in traversal, before any ownership decision, so
  confirm whether it also happens with the mask off.
- Re-run the visual matrix. The 2026-08-18 playtest was reported acceptable overall, but
  seams, boundary flicker and approach popping were not separately confirmed, and no cliff,
  water, cave or structure case was reported individually. Nothing in the matrix has been
  re-run since the mask began working.

### Documentation and baseline

- Repeat the recorded hardware/settings/config benchmark on the eventual release candidate if its code or sandbox state differs materially.

### Renderer scaling

- Human-check clipping and turn-around behavior on the thousands-section build. Automated
  scaling now covers 3,132 persisted sections; the controlled 601-section pair remains the
  causal traversal comparison.
- Measure whether regional buffers or multi-draw are warranted after CPU fixes.
- Select a practical default far cap only from benchmark and playtest evidence.

## Flagged decisions awaiting human evidence

- What default far-distance cap, if any, gives the best product experience after the renderer fixes?
- Is temporary coarseness acceptable while time-budgeted installs catch up during fast travel?
- Does visual quality permit more aggressive off-screen GPU eviction without noticeable turn-around stalls?
- Is the hybrid's one-time chunk handoff pop preferable to any residual overlap, and do
  observed cliff/water seams require a frontier-only correction?

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
- The complete game-backed Release tier passes 1,176 assertions, including the Session 24
  handoff/shader coverage, 89 readiness-model assertions, and static guards that prevent
  the Phase 1 shadow state from entering draw classification. A real game process still
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
