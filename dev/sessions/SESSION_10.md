# Session 10 — warm-cache evidence correction and human review

**Date:** `2026-08-17`
**Branch/commit:** `codex/main-thread-performance` on `9dc373540ce8169e97f5404d2b8cbb37b93f7e26` plus documentation changes
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

After Session 9 was committed, the human observer clarified that the entire corrected
movement/rotation route crossed terrain already present in the Vintage Horizons cache.
They also watched the route in person and reported that it looked good and smooth, with
no noticeable clipping or turn-around stalls.

The measured capture-publication work remains valid: nearby vanilla chunks were still
received, converted, and merged, and the owning-thread maximum fell from 12.038 to
5.732 ms. The scenario classification was too broad, however. It establishes warm-cache
traversal, recapture/publication, rendering, projection, and mesh-arrival behavior; it
does not establish first-time capture while entering genuinely unseen terrain.

## Work narrative

Current documentation and the preserved benchmark README were corrected to name the
route as warm-cache traversal. The completed human visual review was promoted to
human-tested evidence. The open benchmark task now explicitly requires a clean cache or
coordinates proven absent from the cache before the run, so a later cold-terrain result
cannot inherit this ambiguity.

No source, compatibility number, benchmark CSV, or measured value changed.

---

## Delivered

- Reclassified the Session 9 long route from broad continuous-exploration wording to
  warm-cache continuous movement/rotation.
- Recorded the human verdict that the route looked good and smooth with no noticed
  clipping or turn-around stalls.
- Added a genuine uncached-terrain movement/capture benchmark to the next-session work.
- Added a durable cache-state gotcha and synchronized the changelog, plan, TODO/DONE,
  status, session index, and preserved benchmark README.
- Passed 192 documentation checks under both Windows PowerShell 5.1 and PowerShell 7;
  no source or executable checks changed.

## Decisions

- Retain the capture-publication before/after result because live capture results and
  owning-thread publication were directly measured in both runs.
- Do not describe the route as first-time exploration: pre-existing VH coverage means it
  cannot establish cold discovery, initial cache population, or sustained unseen-terrain
  throughput.
- Treat the visual verdict as human-tested qualitative evidence for the exact warm-cache
  scenario, not as a controlled performance measurement or universal clipping proof.

## Traps

- World coordinates alone do not define a benchmark's cache state. A generated sandbox
  can retain VH coverage across runs even when the route still produces capture work.
- Capture activity does not prove that terrain is new to the VH cache; loaded vanilla
  chunks can refresh and merge terrain that the distant cache already contains.

## Flagged and unverified

- Continuous movement/rotation into genuinely uncached terrain still needs a controlled
  run with cache absence established before launch.
- The warm-cache human review was positive but qualitative; it does not isolate the
  capture-budget change from other branch improvements.
- Join, sweep, server-assist, integrated-server, and interrupted-shutdown scenarios remain
  open.
