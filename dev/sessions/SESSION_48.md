# Session 48 — Radial cache loading accepted; Phase 8 resumes

**Date:** `2026-08-24`
**Branch/commit:** `render-overhaul`, on top of `b525fcf`; closing commit contains this record
**Mod version:** `0.3.92` through `0.3.95`; 0.3.95 packaged, verified, installed, and human-accepted
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

Session 47 paused Phase 8 after its preset ladder exposed a still-unlocated depth-cull flicker. The
owner's higher priority was the cost of reaching any repeatable renderer test: a 5,317-section cache
could sit with zero dirty keys, loads, or mesh jobs for 30 seconds and did not produce its first mesh
until 75.1 seconds. Terrain behind the camera also waited for a turn before it loaded or refined.

Source tracing separated this into policy failures rather than throughput: persisted rows created a
quadtree skeleton but the empty renderer returned before its own demand walk; the frustum rejected
subtrees before demand; and visible-only obligations could not create a stable panoramic progression.

## Work narrative

### 1. Exact persisted demand and the radial wave

`LodWorld` now distinguishes exact stored/offered rows from synthetic structural ancestors. A
bounded planner runs before the empty-mesh return and services eight radial lanes without camera
orientation. It admits only exact rows, reuses asynchronous loads and exact render-dirty ownership,
and retains the existing parent-until-children-ready rule. Nearby coarse coverage starts first;
nearer refinement pipelines with the next outward coarse band. Version 0.3.92 carried the first
playable implementation.

### 2. Under-player L0 foundation and final priority

The 0.3.92 playtest accepted the radial shape but found an important omission: planning began
outside vanilla's range, so movement could expose cached terrain beneath the player while it was
still coarse. Version 0.3.93 added an independent inner foundation covering the first configured
LOD band through exact L0. The owner reported that loading and sharpening worked great.

After the final visual review, version 0.3.95 moved three quarters of the unchanged demand allowance
to that eye-priority foundation: 24 of 32 unresolved rows and six of eight new requests per frame.
The outward planner retains eight/two, including one outstanding reservation per radial lane. The
owner accepted the resulting near speed and panoramic progress.

### 3. Appearance readiness before reveal

Faster geometry exposed a pre-existing color race. The first seasonal refresh could complete while
only identity slot 0 existed; palettes loaded afterward registered grass, foliage, and water slots
as identity white, and the time-only cadence left them untinted for 30 seconds. Version 0.3.94 makes
late slots wake the incremental sampler immediately. A section retains its exact mesh obligation
until every tint it uses is atomically published; untinted sections and existing parent coverage do
not wait. `.vhinfo` reports ready/registered tint slots. The owner accepted the proper-color first
reveal.

### 4. Phase 8 resume point

The loading detour changed demand scheduling only; it did not resolve or alter the Phase 8 culling
verdict. Resume with one adjacent `.vhphase8 late` versus `.vhphase8 cull` comparison in a settled
session, without visiting `off` between them. Version 0.3.91 preserves filled arenas across active
presets. The old 362 FPS `late` number remains invalid because 0.3.90 re-meshed 1,678 sections when
the preset was selected.

---

## Delivered

- Exact available-row tracking, a bounded orientation-independent eight-lane radial planner, and
  pre-empty-render bootstrap in 0.3.92.
- Independent under-player L0 foundation in 0.3.93, human-accepted.
- Immediate late-tint sampling and per-section appearance readiness in 0.3.94, human-accepted.
- Dominant 24/6 closest-foundation allocation with an 8/2 outward reservation in 0.3.95,
  human-accepted.
- `.vhinfo` radial and tint readiness diagnostics; G92-G94; completed loading plan, architecture,
  changelog, TODO/DONE, status, and session records.
- **5,013 fast assertions**, **1,521 documentation checks**, and a warning-free Release build.
- Verified and installed `vintagehorizons_0.3.95.zip`, SHA-256
  `79D89E4C1CF9636F7371E6933E9FD49CFA71621B455E9FAE7A321C4365D5E9AF`.
- No game process was launched by the assistant; no push, tag, or public release was made.

## Decisions

**Demand is radial; visibility is draw-only.** Camera orientation cannot decide whether persisted
terrain becomes eligible. Eight lanes preserve panoramic progress.

**The closest band receives dominant, not exclusive, priority.** The player looks there first, so it
gets three quarters of the fixed allowance. The outward reservation remains large enough to keep
one outstanding obligation per direction.

**Appearance is part of reveal readiness.** Data-resident and meshable do not mean player-ready when
the client-only tint palette has not been sampled. A brief exact-obligation wait is preferable to a
wrong-color horizon that snaps later.

**Phase 8 resumes without rediscovery.** Loading is closed unless a new regression is observed. The
next experiment is the already-defined adjacent `late`/`cull` boundary.

## Traps

- A demand-driven producer cannot require its own first product (G92).
- Visibility-independent residency does not imply visibility-independent demand (G93).
- Faster data availability can expose slower appearance state; reveal readiness must include every
  client-only dependency (G94).
- A dominant near priority must reserve explicit outward capacity or it can quietly violate the
  owner's all-direction requirement.

## Flagged and unverified

**Judgement calls awaiting human review.** None for cache startup/refinement; the owner accepted the
complete 0.3.95 behavior.

**Claims lacking their evidence level.** Phase 8's remaining precise-angle flicker is still not
localized between whole-section HZB culling and the same-frame near/far split. The 0.3.90 `late`
performance number remains contaminated. Cross-driver packed/cluster portability and paired-route
memory/GPU-cost gates remain open.
