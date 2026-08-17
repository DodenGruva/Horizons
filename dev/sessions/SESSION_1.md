# Session 1 — performance review and project-memory foundation

**Date:** 2026-08-17
**Branch/commit:** `master`; supplied snapshot, fork history not yet reconciled
**Mod version:** `0.2.0`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## 1. Context and scope

The user supplied code developed by another individual and reported recurring lag and substantial FPS drops. The review focused on workloads placed on Vintage Story's client/render and integrated-server owning threads. It was report-only: no performance source changes were made.

The user then identified their GitHub repository as the working fork and requested that future work use the documentation system developed for the separate Layout project.

## 2. Performance review

The review traced client lifecycle hooks, game ticks, render frames, capture and mesh workers, persistence, local sibling-cache adoption, network assist, savegame sweeping, and transient generation.

Highest-confidence findings were:

- Full local SQLite key enumeration every 50 ms and repeated remote-manifest enumeration.
- Whole-mesh far-distance scans and movement-sensitive projection resets.
- One-second sweep and assist bursts.
- Unbounded main-thread result drains.
- Full-section mip merging and allocation on the owning thread.
- Whole-cache render traversal and scheduling work before frustum rejection.
- Mesh-count rather than byte/time-bounded GPU upload.

A separate request-state defect can strand transient local-offer misses permanently.

## 3. Evidence

The bundled fast-check executable compiled and ran. It reported 538 assertions before two environmental/platform-assumption suite crashes: one SQLite stale-file reopen and one repository-root check requiring `.git` in the supplied archive. No in-game performance run was performed, so causal attribution remains source-derived rather than production-observed.

## 4. Documentation system

The Layout documentation overhaul and general AI project playbook were read. Their central rule—organize information by lifetime—was adopted:

- Brief entry-point working agreement.
- Durable architecture and gotchas.
- One regenerated current-status document.
- Append-only plans, sessions, history, and compatibility ledger.
- Frozen superseded archive.
- Mechanical documentation checks for facts and pointers.

The original `DESIGN.md` and `docs/STATUS.md` were preserved under a dated archive before their living successors were created.

---

## Delivered

- **Documentation only, v0.2.0 unchanged** — Established the complete project-memory scaffold and compatibility ledger.
- **Documentation only, v0.2.0 unchanged** — Saved the approved main-thread performance remediation plan.
- **Repository setup** — Configured the user's fork as `origin`; no commit or push performed.

## Decisions

- Documentation is organized by rate of change rather than by subject alone.
- `CLAUDE.md` is the canonical model-neutral working agreement; `AGENTS.md` is a minimal bridge.
- Existing design/status prose is preserved but cannot remain a competing current authority.
- Performance implementation begins with measurement and low-risk P1 fixes before asynchronous mip or renderer redesign.
- Count limits alone are not accepted as latency budgets.

## Traps

- A full cache index can remain a main-thread problem even when blob reads and writes use a worker.
- A request removed from one queue without clearing its in-flight identity can be lost permanently.
- A static benchmark with a settlement delay can systematically hide moving-camera and active-capture stutter.
- An extracted repository can break test helpers that incorrectly treat `.git` as the only valid root marker.

## Flagged and unverified

- The fork and supplied snapshot histories still need reconciliation before committing implementation work.
- The reported lag has not yet been reproduced under the new instrumentation because that instrumentation does not exist yet.
- Default render-distance policy and acceptable turn-around eviction behavior require human playtesting after measurement.
