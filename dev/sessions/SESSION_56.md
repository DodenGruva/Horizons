# Session 56 - Packed timing closes the regional-memory decision

**Date:** `2026-08-25`
**Branch/commit:** `render-overhaul`, on top of `f13185a`; implementation and records uncommitted
**Mod version:** `0.3.105`, built, verified, and copy-installed; not game-run
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

Phase 7 deliberately retained expanded, whole-packed, and clustered-packed regional geometry so
each later format could be compared against the previous one. Phase 9 still owed a controlled
packed/expanded timing comparison and a final retention policy. The owner asked to discuss the
choice before implementation and selected the lowest-memory option after reviewing its fallback
tradeoff.

## Work narrative

### 1. The first pair exposed a coupled-switch confound

The product-default comparison changed packing while leaving clusters enabled. Turning packing off
also made clusters idle, so that pair changed two stages and could not isolate format decode cost.
The final ABBA matrix pinned clusters off and changed only packing. It also exposed a stale runner
parser: current telemetry reports split-near and split-far timers, but the script still expected the
older opaque/water sentence. The parser now records both split timers, upload tails, and per-format
arena live/committed bytes.

### 2. Controlled primary-driver result

Across two six-viewpoint runs per format, expanded averaged 2.4392 ms and packed averaged 2.4367 ms.
The -0.10% packed delta was smaller than repeat variation, so the result is parity, not a speedup.
The measured representation stayed about 86.4% smaller. All 24 viewpoints settled without timeout.
Tracked CSVs and a hash-bound summary live under
`bench/results/2026-08-25-phase9-packed-memory`.

### 3. Selected-only retention

The regional mirror now chooses one representation at setup: expanded when packing is off,
whole-packed when packing is on and clusters are off, or clustered-packed for the product default.
Only that arena receives allocations and uploads. If its data, allocation, upload, shader, or draw
is unavailable, the already-retained per-section legacy mesh remains the complete fallback; the
mirror does not retain two extra regional copies merely to provide an intermediate fallback.

On the measured route all three old regional copies totalled about 833.7 MiB live. The selected
clustered copy was about 90.33 MiB, so the policy removes about 89% of those live regional duplicate
bytes. This is deliberately not described as total-process memory savings because legacy meshes
remain resident and allocator commitment differs from live occupancy.

---

## Delivered

- Controlled expanded/packed ABBA evidence with zero settle timeouts and timing parity.
- Windows runner parsing for current split GPU timers, upload tails, and arena memory.
- Explicit expanded/packed/clustered retention policy with selected-only arena publication.
- Focused policy, allocation, upload, command-building, and unretained-copy fallback checks.
- Version 0.3.105 with warning-free Release build, 5,104 passing assertions, and a verified package
  copied beside the preserved rollback builds. SHA-256:
  `13975952ED56D178B0AE61571B9164682F9F36746199954A8F9D1EC1FEFAE9AE`.

## Decisions

**Retain exactly the selected regional representation.** Keeping whole-packed as an intermediate
cluster fallback would retain about another 89 MiB without a runtime path that automatically uses
it. Keeping all three would retain roughly 833.7 MiB on the measured route. The established mesh is
already the complete correctness fallback, so duplicate regional buffers do not buy correctness.

**Call the timing result parity.** A -0.10% mean delta is below run-to-run variation and cannot be
reported as a packed speed improvement.

## Traps

- A format comparison is invalid when a dependent stage silently becomes idle in only one half.
- A benchmark can complete with useful CSV rows while a stale telemetry parser silently leaves the
  GPU and arena arrays empty; validate parser cardinality before interpreting the run.

## Flagged and unverified

- Judgement calls awaiting human review: none for the regional-memory policy; the owner selected it
  after discussion.
- Claims lacking their evidence level: 0.3.105 has not been launched or human-watched. Total game
  RSS savings are not measured. Cross-driver coverage remains retired without evidence. Phase 9's
  settings/lifecycle matrix and representative forced-legacy run remain open.
