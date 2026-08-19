# Vintage Horizons — TODO and verification debt

> Tier 2 companion: open work only. Completed narrative moves to `dev/history/DONE.md`; current conclusions belong in `STATUS.md`.

## Top priority — the hole that per-chunk ownership leaves behind

Flying backwards at high speed leaves a gap in the world that survives standing still.
`.vhmask off` fills it, which proves cached terrain is resident and drawable and that
ownership is suppressing it. This is the one thing blocking per-chunk ownership from
becoming the default.

Four fixes have not closed it, so do not write a fifth from reasoning alone. Reproduce it
and read the periodic log first:

- `stale committed found` should be zero once settled. A non-zero figure means committed
  ownership is surviving after vanilla stopped drawing, and the one-second re-confirmation
  is not reaching those cells.
- `count repairs` should always be zero. Any figure at all means the per-section counts the
  whole-mesh skip trusts are drifting, which would hide a section permanently.
- `drawn-but-empty chunks` measures the leading theory: the engine counts an empty chunk as
  drawn, so cached terrain standing taller than the real world sits in cells that report
  drawn while nothing is drawn there. A high figure makes that theory the likely cause and
  the next task is finding a reliable way to identify those chunks - `IWorldChunk.Empty` is
  not one, see G40.

`.vhwhy` is not a useful route and should not be extended; a hole is a screen-space thing
and a ray through it mostly passes through legitimately empty air.

## Top priority — main-thread stutter and renderer scaling

The approved implementation sequence is `dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md`.

### Chunk-aware cached-to-vanilla handoff

The approved rendering design is
`dev/plans/PLAN_CHUNK_AWARE_VANILLA_HANDOFF.md`.

- Implement atomic GPU mask publication, shader sampling for mixed meshes, and CPU skip
  for fully replaced meshes behind the radial fallback. Start with the plan's
  public-wrapper-compatible 2D Y-slice atlas. Phase 1's runtime gate is met, so this is
  no longer blocked; whole-column ownership is reachable, with 232 of 441 columns complete
  and a flat per-Y histogram.
- Measure the readiness-driven handoff's convergence window at join and after a teleport.
  The derived radius starts small, which shows more cached terrain near the camera than the
  old constant did until the tracker converges. Duration and visibility are unknown.
- Reproduce the single-unowned-pocket case deliberately: one column near the camera that
  never becomes ready collapses the global radius and restores overlap everywhere. How
  often this happens in play is the evidence for whether the per-cell mask earns its cost.
- Cover teleport and live view-distance change at runtime; both are harness-only today.
- Establish whether state-agnostic maintenance and the derived handoff cost measurable
  frame time. Repeated alternating runs are required; the current pair is one run per side
  inside the lap spread.
- Preserve independent residency so suppressed fallback remains warm without triggering
  unload/reload/remesh churn.
- Benchmark cache-only, vanilla-settled, moving-frontier, large-cache, teleport, and
  view-distance-change scenarios before claiming neutral or improved performance.
- Human-reported 2026-08-19, per-cell mask enabled, all at far above normal flight speed
  and none judged likely in ordinary play:
  - Approaching terrain fast, cached and vanilla briefly fight where vanilla has just
    loaded. This is ownership gain latency and resolves within a moment. It fails in the
    safe direction, so it is accepted for now; if it becomes objectionable, confirm cells
    near the camera on a shorter path rather than widening the budget again.
  - Flying backwards produced a band of missing terrain, and a hole made that way could be
    left standing still and would persist indefinitely. `.vhmask off` filled it, proving the
    mask was suppressing cached terrain that was resident and drawable. Committed ownership
    is now re-confirmed wholesale every second, which bounds staleness by construction; the
    three cursor sweeps that preceded it could not. A hole still persisted after that, which
    pointed at a different cause: an empty vanilla chunk reports as drawn, so cached terrain
    standing taller than the real world sits in cells the engine has "drawn" as air. Empty
    chunks cannot be identified this way: `IWorldChunk.Empty` is a stale cached flag on the
    client, and acting on it in 0.3.3 removed ownership everywhere and left every cached
    section overlapping vanilla. Reverted in 0.3.4, which instead counts `drawn-but-empty
    chunks` in the periodic log. A high count says the mechanism is real and needs a
    reliable test for it; a zero count says look elsewhere. If holes survive this too, read
    `stale committed found` and `count repairs` from the periodic log: both should be zero,
    and a non-zero count repair is a bug in the ownership aggregate itself.
  - Historical, same symptom: flying backwards produced a clear band of missing terrain
    where vanilla had unloaded and cached coverage had not returned. This is the serious one - a hole outranks an
    overlap - and the loss-detection shell now sweeps completely whenever the camera
    crosses a chunk. If gaps survive that, the next step is expiring committed ownership
    that has not been reconfirmed within a bounded time, which caps hole duration by
    construction at the cost of some churn.
  - Cached water shows the boundary of every chunk, and colouring differs across those
    boundaries. Deliberately not addressed yet. Two candidates, and one cheap experiment
    separates them: if the seams disappear with `.vhmask off`, the mask is drawing cached
    water only in unowned cells and the 32-block ownership edges are visible on a flat
    surface that hides nothing; if they persist, it is the existing per-section water tint
    and predates this work.
- Cached terrain was also observed becoming coarser than expected during fast flight. The
  renderer now reports why: a parent keeps covering ground when a visible child with data
  has no mesh, and each interval logs whether those children were waiting on storage, on a
  mesh worker, on a scheduling slot, or on nothing at all, beside the three backlog depths.
  Read `coarse cover waits` from a fast-flight run before changing any budget; the per-frame
  schedule cap only binds below roughly 60 FPS, so it is probably not the cause.
  Level selection happens in traversal, before any ownership decision, and skipping a draw
  touches neither residency nor mesh scheduling. Confirm whether it also happens with the
  mask off.
- Re-run the visual matrix against the readiness-driven handoff and after any hybrid
  implementation. The 2026-08-18 playtest was reported acceptable overall, but seams,
  boundary flicker, and approach popping were not separately confirmed, and no cliff, water,
  cave, or structure case was reported individually.

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
