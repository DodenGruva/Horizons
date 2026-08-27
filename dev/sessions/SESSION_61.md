# Session 61 — Renderer candidates measured and retired

**Date:** 2026-08-27
**Branch/commit:** `codex/global-cave-classifier`, following cave-work commit
`2a1310f`; Session 61 work remains uncommitted.
**Mod version:** 0.3.123-0.3.124; 0.3.124 built, verified, copy-installed, and exercised by one
isolated six-view GPU-timing benchmark.
**Assist protocol / blob / schema:** 1 / 4 / 6, unchanged.

## Context and investigation

The owner returned to the performance TODO after accepting the cave classifier. Three user-visible
debts could be closed immediately from play evidence: the tiny sawtooth appeared with every mod
disabled, no 30-second stutter had appeared in extensive testing, and join-time warm-up felt smooth
after the loading protocol was rebuilt. One real defect remained in our diagnostic: a completed
frame interval was attributed to the following callback instead of the callback that preceded it.

The remaining renderer candidates had all been written as measurement gates: try 16-MiB pages,
measure per-frame metadata upload volume, and isolate live cluster-cull GPU cost before considering
a two-tier whole-section/cluster classifier. This session ran those gates rather than implementing
the proposed optimizations.

## Work narrative

### 1. Frame attribution was corrected, while the sawtooth was exonerated

`LodFrameTimeline` already resolves sub-millisecond work. Version 0.3.123 stores the completed
callback duration and pairs it with the following begin-to-begin interval, which is the interval
that callback can actually have lengthened. Tests pin the one-frame association. The owner then
reproduced the graph's tiny teeth with no mods installed, closing the symptom as external to
Vintage Horizons.

### 2. Sixteen-MiB pages lost their measurement gate

Fresh, otherwise identical six-view BodanBoys runs compared 8- and 16-MiB pages with clustered
packed drawing and HZB enabled. Both completed every view with no settle timeout, missing coverage,
stale span, fallback, or allocation failure.

| page | mean frame | mean FPS | mean 1% low | observed batches | final cluster arena |
|---:|---:|---:|---:|---:|---:|
| 8 MiB | 2.3433 ms | 432.65 | 268.47 | 76 | 69.42 / 200 MiB, 25 pages |
| 16 MiB | 2.3300 ms | 435.15 | 276.63 | 74 | 69.39 / 368 MiB, 23 pages |

The 0.58% FPS movement is within route repeat noise. Saving two batches while committing 84% more
memory and halving live utilization from 34.7% to 18.9% is a clear rejection.

### 3. Metadata upload volume was small enough to close by arithmetic

The actual clustered layouts are 20 bytes per indirect command, 64 per section record, and 32 per
cull box: 116 bytes per command. The busiest measured frame carried 2,107 commands, about 239 KiB
per frame. Around 400 FPS that is under 100 MiB/s. It is not the suspected megabyte per frame, so
moving camera origin out of every record and introducing region-anchored persistent metadata would
spend substantial complexity on a small stream.

### 4. Cull-only GPU timing rejected two-tier culling

The existing split-pass TIME_ELAPSED query includes upload, cull, barriers, and drawing, so it could
not answer the candidate's gate. Version 0.3.124 adds asynchronous GPU timestamp pairs immediately
around each cull compute dispatch, separated into ordinary, split-near, and split-far buckets. A
timestamp pair can sit inside the whole-pass query; results use an eight-slot delayed ring, are
polled without waiting, and are discarded if the dispatch never happened. The Windows benchmark
runner captures each distribution in its scenario proof.

The approved six-view route completed at 2.3717 ms / 427.43 FPS average with zero settle timeouts.
Every settled interval reported split-far p95 and p99 at 25 us over thousands of samples; the
largest interval maximum was 108 us. There were zero ring-full skips and time-query target
conflicts, and the draw path reported no fallback or stale span. Even removing culling entirely
would save only about 1% of average frame time. A two-tier test can remove only part of that cost
and adds a whole-section test, so it is rejected.

---

## Delivered

- Correct preceding-callback frame attribution in 0.3.123.
- Nonblocking, nested-safe cull-dispatch GPU timestamp pairs in 0.3.124.
- Ordinary, split-near, and split-far cull distributions in the periodic client log and benchmark
  scenario JSON.
- Focused delayed-timestamp tests covering availability, differences, discard, saturation, and
  benchmark wiring.
- Warning-free Release and Debug builds, 5,381 passing fast assertions, and 1,575 documentation
  checks.
- Verified 14-entry 0.3.124 package with no PDB, copy-installed with matching SHA-256
  `484F058D54BDD92408C52C9BA4A4EEDE2411653C1587EC80F948DDFAFF592867`.
- Benchmark evidence in `.testdata/profiles/bodanboys/bench/p10page8-03123-a*`,
  `p10page16-03123-a*`, and `p10culltiming-03124-a*`.

## Decisions

- Retire the tiny sawtooth as not caused by Vintage Horizons; retain the corrected frame timeline.
- Retire 30-second stutter and join-time warm-up verification on the owner's human evidence.
- Keep 8-MiB arena pages; reject 16 MiB on memory cost for negligible batching change.
- Do not build region-anchored persistent command records for a roughly 239-KiB/frame stream.
- Do not build two-tier cluster culling for a 25-us p95/p99 split-far dispatch.
- Keep the cull timers as a cheap measurement surface; they change no rendering decision.
- Protocol 1, blob 4, and schema 6 remain unchanged.

## Traps

- A TIME_ELAPSED query cannot be nested inside the existing split-pass TIME_ELAPSED query. GPU
  timestamp pairs isolate an inner dispatch without splitting the accepted outer measurement.
- PowerShell `Compress-Archive` over a recursively enumerated list of files flattened asset paths.
  The mandatory archive inspection caught it before installation; package from the mod directory
  root and verify shader entry paths. Recorded as G116.

## Flagged and unverified

- GPU timings are from the owner's RX 9070 XT and one frozen world route. They are decisive for the
  current primary-machine funding gate, not a cross-driver cost claim.
- The existing batched-fast-path comparison against the established delayed-occlusion renderer
  remains open. The optional subtree-height on/off confirmation also remains open.
- Phase 9's longer-tail MSAA/SSAO, resize/fullscreen, reload, world/dimension, long-session,
  multiplayer, competing-LOD, and representative forced-legacy coverage remains open.
- No commit, push, tag, or public release was made.
