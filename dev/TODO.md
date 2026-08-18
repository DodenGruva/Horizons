# Vintage Horizons — TODO and verification debt

> Tier 2 companion: open work only. Completed narrative moves to `dev/history/DONE.md`; current conclusions belong in `STATUS.md`.

## Top priority — main-thread stutter and renderer scaling

The approved implementation sequence is `dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md`.

### Documentation and baseline

- Repeat the recorded hardware/settings/config benchmark on the eventual release candidate if its code or sandbox state differs materially.

### Instrumentation

- Add equivalent phase telemetry for sweep, generation, and server-assist serving.
- Add warm-cache join, sweep, and server-assist benchmark scenarios.
- Measure the overhead of enabled versus disabled instrumentation in a steady stationary scenario.

### P1 fixes

- Soak the new asynchronous mip worker during long exploration, restart, and shutdown; verify stale/failure retries and durable `ApplyToParent` convergence under interruption.

### Renderer scaling

- Frustum-test subtrees before traversal while keeping residency independent of visibility.
- Replace repeated full `RenderDirty` scans with spatial or priority scheduling.
- Bound GPU uploads by elapsed time and bytes.
- Measure whether regional buffers or multi-draw are warranted after CPU fixes.
- Select a practical default far cap only from benchmark and playtest evidence.

### Persistence hardening

- Add save revisions and completion acknowledgements.
- Retry failed writes without losing dirty state.
- Coalesce superseded snapshots for the same section.
- Make shutdown drain existing backlog and then persist remaining dirty revisions.

## Flagged decisions awaiting human evidence

- What default far-distance cap, if any, gives the best product experience after the renderer fixes?
- Is temporary coarseness acceptable while time-budgeted installs catch up during fast travel?
- Does visual quality permit more aggressive off-screen GPU eviction without noticeable turn-around stalls?

## Verification debt

- Two one-way 1,600-block capture-frontier runs started with zero active VH databases and
  `0 sections from cache`. Both had zero VH ticks at or above 25 ms; capture backlog stayed
  within 20 results / 1.61 MiB / 234 ms and converged during the endpoint cooldown. The
  first run generated server-save terrain and the second reused it, so aggregate FPS is
  not controlled A/B evidence. No person watched the cold route in motion.
- Asynchronous mip propagation passed two short before/after route runs with zero mip backlog/errors at interval close; longer soak, restart interruption, and integrated-server load remain unverified.
- The complete game-backed fast tier passes 900 assertions across 22 suites. A real game process still supplies the only end-to-end proof of thread ownership and GPU behavior.
- Incremental sibling-cache discovery and retry-safe local/server request transitions are source-traced and fixture-tested, but not yet exercised in integrated singleplayer or live server assist.
- Cached bounds and projection hysteresis pass 30 isolated assertions. The corrected long
  warm-cache route completed with five projection resets and no tick hitches, and human
  review found motion smooth with no noticed clipping. The earlier route screenshots were
  sky-biased; cold terrain/coverage arrival still needs visual review.
- Capture publication is source-bounded and passed a warm-cache same-route before/after
  run: its maximum fell from 12.038 to 5.732 ms and backlog peaked at 9 results / 0.70 MiB /
  93 ms. One admitted result remains non-preemptible; unseen terrain and interrupted
  shutdown were not exercised.
- Server-assist and savegame-sweep spike cadence has not yet been profiled in integrated singleplayer.
- Tick-smoothed server work and time/byte-bounded client installs are source-traced and harness-tested, but their frame-time effect and backlog policy have not yet been evaluated in an integrated game process.
- Storage-owned foreign decode is source-traced and harness-tested. A brief human test
  reported a noticeable subjective improvement, but no controlled assist or sibling-cache
  run has isolated decode time, publication time, backlog age, or throughput.
- Per-phase allocation telemetry is harness-tested; its enabled-versus-disabled overhead
  still needs a steady stationary comparison.
- GPU bottleneck attribution remains unmeasured; CPU/render-thread findings must not be presented as proof that the shader or GPU is innocent.
