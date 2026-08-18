# Session 19 — incremental render-dirty priority scheduling

**Date:** `2026-08-18`
**Branch/commit:** `codex/main-thread-performance`, working tree based on `a3a475a`
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> This session changes render-dirty bookkeeping and mesh-job selection. It does not
> change terrain data, mesh output, assist protocol, stored blob format, database schema,
> GPU upload policy, or server behavior.

## Context and investigation

Visibility-aware traversal reduced off-screen selection and demand, but the renderer still
walked the complete `RenderDirty` set once to prune it and again to find the nearest few
mesh jobs on every rendered frame. That work therefore continued to scale with dirty
history even when only a handful of keys had changed.

Dirty keys can originate from live section mutation, neighbour invalidation, and
demand-driven traversal. A nearest key can also be temporarily unavailable because its
mesh or asynchronous reload is already in flight. Any replacement therefore had to
preserve deduplication, background-load routing, gate-mesh pruning, and dirty obligations
while removing the ordinary whole-set scan.

## Work narrative

1. Replaced the raw render-dirty hash set with an owning-thread set that retains exact
   membership while publishing only newly added keys to its single renderer consumer.
2. Added a nearest-first priority index anchored to a 256-block camera cell. It rebuilds
   after a camera-cell crossing, detail-distance change, or world clear; ordinary frames
   inspect only newly added keys.
3. Kept queue removal lazy and safe: stale heap entries validate against exact set
   membership, temporarily blocked keys are restored with their dirty obligation, and a
   finite examination allowance includes every permitted in-flight mesh/load key.
4. Preserved the existing pruning rule: a key with no live mesh may be dropped only when
   it is finer than the level currently wanted there. Gate meshes and stale live meshes
   remain eligible.
5. Added a dedicated fast-check suite covering pruning, nearest order, incremental
   additions, busy-prefix progress, camera-cell reprioritization, and world-clear
   invalidation. The full tier now passes 995 assertions across 24 suites.
6. Ran a short isolated warm-cache moving/rotation route against 601 cached sections.
   All four waypoints settled, 543 meshes became resident with no evictions, and the
   final semantic sample reported zero capture, mesh, mip, render-dirty, save, load, or
   storage backlog/errors before graceful shutdown.

---

## Delivered

- Incremental render-dirty additions and a coarse-cell-refreshed nearest-first index.
- Bounded progress past busy mesh/load entries without dropping their dirty state.
- Twenty focused scheduler assertions; 995 full-tier assertions pass.
- Zero-warning mod and benchmark-harness builds plus a successful isolated functional
  moving/rotation smoke.
- 263 documentation checks pass under both PowerShell 7 and Windows PowerShell 5.1.

## Decisions

- Keep exact dirty membership separate from the priority heap. Lazy stale-entry
  validation makes removal and re-addition safe without requiring arbitrary heap edits.
- Rank against the centre of a 256-block camera cell. This bounds priority drift while
  avoiding an O(dirty-count) rebuild during ordinary movement.
- Treat the runtime route as integration evidence only. It had no controlled old/new
  pair and its 601-section cache is below the planned thousands-section scale.
- Leave mesh-snapshot time/byte budgeting and GPU-upload budgeting as separate changes.

## Traps

- A priority queue does not by itself preserve work. Removing a busy head or trusting a
  stale heap entry can lose a newer dirty obligation; blocked entries must be restored
  and every dequeued key must validate against exact membership.
- Recomputing camera-relative priorities on every movement frame recreates the original
  whole-collection cost in a different data structure. Rebuild at a coarse spatial
  boundary and ingest additions incrementally between rebuilds.

## Flagged and unverified

- The functional route proves scheduling convergence and lifecycle safety, not a causal
  frame-rate or phase-time improvement.
- Scaling and rebuild cost at thousands of dirty/cached sections remain unmeasured.
- Mesh snapshot creation and GPU upload are still item-count bounded rather than bounded
  by elapsed time and retained/uploaded bytes.
- No person watched this short route in motion; the existing human review applies only
  to the earlier warm-cache traversal build.
