# Vintage Horizons — TODO and verification debt

> Tier 2 companion: open work only. Completed narrative moves to `dev/history/DONE.md`; current conclusions belong in `STATUS.md`.

## Top priority — main-thread stutter and renderer scaling

The approved implementation sequence is `dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md`.

### Documentation and baseline

- Preserve the four short active-exploration CSV runs and summarized telemetry when preparing a reviewable commit; the raw `.testdata` sandbox remains intentionally ignored.
- Add a longer continuous-movement/rotation route that does not rely only on teleports.
- Record hardware, graphics settings, view distance, save, and mod configuration beside a release-candidate benchmark.

### Instrumentation

- Extend the implemented client tick/pipeline/render percentiles, hitch counts, projection resets, and upload bytes with queue ages and allocation/GC deltas.
- Add equivalent phase telemetry for sweep, generation, and server-assist serving.
- Add moving-camera, warm-cache join, sweep, and server-assist benchmark scenarios.
- Measure the overhead of enabled versus disabled instrumentation in a steady stationary scenario.

### P1 fixes

- Replace the once-per-second main-thread full SQLite key scan with background delta discovery.
- Apply remote manifest keys once rather than re-enumerating the full set each tick.
- Fix transient local-offer misses so request state remains retryable.
- Cache mesh world bounds and stabilize the far plane with quantization and hysteresis.
- Spread sweep and assist allowances across ticks.
- Time/byte-budget foreign-section and background-load installation.
- Move foreign blob inflation/structural decode off the owning thread.
- Soak the new asynchronous mip worker during long exploration, restart, and shutdown; verify stale/failure retries and durable `ApplyToParent` convergence under interruption.
- Time-budget capture-result publication if the newly exposed 11–14 ms maximum reproduces in longer movement runs.

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

- The short active-exploration harness reproduced and attributed the largest game-tick spike, but it is teleport-driven and not a substitute for a human continuous-movement playtest.
- Asynchronous mip propagation passed two short before/after route runs with zero mip backlog/errors at interval close; longer soak, restart interruption, and integrated-server load remain unverified.
- The complete game-backed fast tier passes 707 assertions. A real game process still supplies the only end-to-end proof of thread ownership and GPU behavior.
- Projection resets are now counted live (2–19 per active interval), but the bounds/hysteresis fix still needs a dedicated moving-camera run.
- Server-assist and savegame-sweep spike cadence has not yet been profiled in integrated singleplayer.
- GPU bottleneck attribution remains unmeasured; CPU/render-thread findings must not be presented as proof that the shader or GPU is innocent.
