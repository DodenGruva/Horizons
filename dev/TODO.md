# Vintage Horizons — TODO and verification debt

> Tier 2 companion: open work only. Completed narrative moves to `dev/history/DONE.md`; current conclusions belong in `STATUS.md`.

## Top priority — main-thread stutter and renderer scaling

The approved implementation sequence is `dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md`.

### Documentation and baseline

- Repeat the recorded hardware/settings/config benchmark on the eventual release candidate if its code or sandbox state differs materially.

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

### Integrated validation

- Repeat durable mip interruption/recovery under integrated-singleplayer load.
- Exercise sibling-cache discovery and retryable local misses in that process.

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
- Asynchronous mip propagation now has a 120-second movement/capture convergence soak,
  graceful restart, deliberate interruption after a durable `ApplyToParent` write,
  successful recovery of one persisted obligation, and a third fresh process reporting
  zero obligations. Dedicated client/server durability is established;
  integrated-singleplayer interruption remains unverified.
- The complete game-backed fast tier passes 968 assertions across 22 suites. A real game process still supplies the only end-to-end proof of thread ownership and GPU behavior.
- Live server-assist transfer now exercises network request state end to end. Incremental
  sibling-cache discovery and retry-safe local misses remain unexercised in integrated
  singleplayer.
- Cached bounds and projection hysteresis pass 30 isolated assertions. The corrected long
  warm-cache route completed with five projection resets and no tick hitches, and human
  review found motion smooth with no noticed clipping. The earlier route screenshots were
  sky-biased; cold terrain/coverage arrival still needs visual review.
- Capture publication is source-bounded and passed a warm-cache same-route before/after
  run: its maximum fell from 12.038 to 5.732 ms and backlog peaked at 9 results / 0.70 MiB /
  93 ms. One admitted result remains non-preemptible; unseen terrain and interrupted
  shutdown were not exercised.
- A warm-cache join adopted 558 sections with an 11.180 ms worst Vintage Horizons tick
  and no 25 ms hitch. Background-load backlog reached 181 sections / 51.93 MiB / 11.531 s
  old and drained by 30 seconds. This is one client-only warm sample, not a cold/warm A/B
  or integrated-singleplayer join.
- A pinned dedicated-server sweep examined 3,249 dependency-aware positions, loaded 1,018
  existing columns, skipped 377 frontier columns, generated nothing, and verified 256/256
  sampled absent positions. Server pipeline ticks peaked at 17.874 ms with no 25 ms hitch;
  probe/load issue maxima were 3.945/6.004 ms. The server cache was warm, and
  savegame-sweep cadence remains unprofiled in integrated singleplayer.
- Completed transient generation produced 289/289 columns with zero timeouts or unusable
  height maps and preserved 256/256 sampled absences. This is one radius-8 dedicated-server
  run, not a long/default-radius or integrated-singleplayer soak.
- The repeated 64/s saturated-assist run again requested, received, and installed 395
  sections with zero declines. One 17.481 ms background read coincided with a 0.989 ms
  owning-thread assist maximum, proving separation. A later interval had a 32.450 ms
  assist outlier while reader calls stayed below 0.2 ms, so packet publication, GC, or
  process scheduling remains a distinct tail to isolate. No person watched the route.
- Two warmed steady-stationary on/off pairs measured about 0.7% lower average FPS and
  1.0% lower median FPS with allocation telemetry at roughly 445 uncapped FPS. Their 1%
  lows reversed direction, and ordinary capped-frame-rate overhead remains unmeasured.
- GPU bottleneck attribution remains unmeasured; CPU/render-thread findings must not be presented as proof that the shader or GPU is innocent.
