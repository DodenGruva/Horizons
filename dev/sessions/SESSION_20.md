# Session 20 — frame-budgeted mesh snapshots and GPU uploads

**Date:** `2026-08-18`
**Branch/commit:** `codex/main-thread-performance`, working tree based on `a365295`
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> This session changes render-thread mesh scheduling, replacement, and telemetry. It does
> not change terrain data, CPU mesh output, assist protocol, stored blob format, database
> schema, shaders, or server behavior.

## Context and investigation

Mesh snapshot production and completed-mesh upload were each limited to four items per
frame. That bounded queue operations but not latency: a snapshot can retain several large
section arrays, and one completed mesh can contain two large opaque/water buffer sets.
The approved Phase 7 plan therefore still required elapsed-time and byte ceilings, safe
replacement ordering, and enough telemetry to tune those ceilings from runtime evidence.

The existing synthetic mesher benchmark showed why count-only upload was insufficient.
Its dense rolling-hills and uneven-plateau cases each emitted about 49,664 vertices and
allocated roughly 1.04 MiB of result arrays per mesh, while a flat plain emitted only 20
vertices. Four items therefore did not describe a stable amount of work.

## Work narrative

1. Added exact payload estimates for immutable section snapshots and completed mesh
   uploads. Snapshot estimates include shared immutable run/column arrays because a
   queued job can retain an old array after the live section swaps to a replacement.
2. Added a 1 ms / 2 MiB boundary budget to snapshot production and a 2 ms / 4 MiB
   boundary budget to GPU upload. The previous four-item caps remain final safety rails.
3. Preserved progress for an oversized first item. Work is atomic at one snapshot or one
   upload-result boundary, so one item may exceed a ceiling but later work waits.
4. Restored exact render-dirty membership when a selected snapshot cannot enter the
   current frame budget. Deferral therefore cannot silently discard mesh work.
5. Changed mesh replacement to create the complete new opaque/water pair before
   publishing it and disposing the old pair. A partial upload failure frees only the new
   resources, retains the old visible mesh, and restores the dirty obligation.
6. Added render telemetry for snapshot/upload items and bytes, queued bytes, oldest age,
   direct GL upload-call time, and GPU-resource disposal time.
7. Converted the shared drain-budget helper to a value type. The renderer creates two
   budgets per frame, so the guard itself must not introduce steady heap traffic.
8. Expanded deterministic byte-accounting and budget checks. The Release mod build
   succeeds with zero warnings, and the full game-backed fast tier passes 1,002
   assertions across 24 suites.

---

## Delivered

- Boundary-budgeted mesh snapshot production and completed GPU upload.
- Progress-safe deferral and complete-before-dispose mesh replacement.
- Snapshot/result backlog bytes and age plus direct GL upload/disposal telemetry.
- Allocation-free per-frame budget state.
- Seven additional focused assertions; 1,002 full-tier assertions pass.
- Zero-warning Release mod build.
- 265 documentation checks pass under Windows PowerShell 5.1 and PowerShell 7.

## Decisions

- Keep the existing four-item limits beside the new time/byte ceilings. Counts remain a
  useful final cap, but they are no longer treated as a latency budget.
- Start with 1 ms / 2 MiB for snapshot production and 2 ms / 4 MiB for upload. These are
  conservative initial policy values, not tuned product defaults; the new telemetry is
  the evidence source for later adjustment.
- Count retained shared arrays conservatively per job. Double-counting a section used by
  neighbouring jobs is safer than admitting a burst on the assumption that shared arrays
  are free.
- Keep one-item atomic progress rather than attempting to split a section snapshot or an
  engine GPU upload. The latter has no safe preemption point in the exposed API.

## Traps

- A four-item limit can still mean four megabytes of result arrays or two GPU passes per
  item. Every frame-critical count cap also needs time and content-byte evidence.
- A per-frame budget implemented as a heap object creates the GC pressure it is intended
  to control. Keep frame-local budget state allocation-free.
- Disposing the current opaque/water pair before both replacements upload can turn a
  transient failure into a visible hole. Prepare the whole replacement first, publish it,
  and only then dispose the previous resources.

## Flagged and unverified

- No game process exercised the new GPU path. Source/build/harness evidence establishes
  boundary logic and accounting, not actual driver upload latency or visual behavior.
- The 1 ms / 2 MiB and 2 ms / 4 MiB ceilings require runtime queue-age and convergence
  evidence before being treated as tuned values.
- One admitted snapshot or upload remains non-preemptible and may exceed its time or byte
  ceiling by itself.
- Thousands-section scaling and human clipping/turn-around review remain open.
