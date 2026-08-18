# Session 21 — large-cache renderer proof and durable save acknowledgements

**Date:** `2026-08-18`
**Branch/commit:** `codex/main-thread-performance` (session-close commit)
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

Session 20 left the renderer's snapshot/upload boundaries source- and check-tested but not
run in game, and Phase 8 still treated save enqueue as durability. Automated work was
authorized without human availability. The existing isolated 601-section cache supplied
the first renderer route; a one-way 12,800-block corridor then created a 3,132-section
scale fixture without touching the user's ordinary game data.

The first current-code launch found a `LodDrainBudget` regression: converting the helper
to a struct left `new LodDrainBudget()` zero-initialized because only an optional-argument
constructor existed. An earlier completed route was also rejected because the Windows
runner had deployed a stale Debug DLL. The current Debug build was rebuilt before every
accepted runtime result.

## Work narrative

1. Added the explicit value-type parameterless constructor and a check that exercises the
   exact production form through multiple items.
   The Windows runner now rejects a Debug assembly older than its project or C# inputs.
2. Ran the corrected 601-section route and the sustained cache-growth route. Snapshot and
   upload queues remained bounded, direct driver calls stayed below one frame, no renderer
   phase reached 25 ms, and all guarded work converged. The large route exposed one separate
   36.289 ms atomic capture-publication tail.
3. Source-traced `LodWorld`, `LodPipeline`, `LodStorageThread`, foreign fallback routing,
   and close ordering. Content revision could not identify persistence because clearing
   `ApplyToParent` changes a row without changing terrain content.
4. Added runtime-only persistence revisions and exact acknowledgements. Dirty membership
   now survives enqueue. The single storage consumer coalesces a newer still-pending
   same-key snapshot, emits completion for each executed revision, and keeps at most one
   newer snapshot behind an executing one.
5. Failures retain the exact dirty revision and retry with 250 ms exponential delay capped
   at 30 seconds. Successful local writes retire foreign fallback only after durability.
   Close alternates drain, acknowledgement publication, and remaining-dirty enqueue until
   clean or a 15-second deadline, then logs exact unresolved coordinates/revisions.
6. Added deterministic failure, repeated-mutation, coalescing, backlog, and restart checks,
   then ran the revised pipeline against the 3,132-section cache. It wrote 138 revisions
   and reached zero unsaved/backlog/errors before graceful shutdown.

---

## Delivered

- Correct current-build renderer evidence at 601 and 3,132 cached sections.
- `LodDrainBudget` runtime constructor fix and regression coverage.
- Preflight rejection of stale mod or benchmark Debug assemblies.
- Revision-acknowledged persistence, failure retry, pending coalescing, durable remote
  promotion, and two-stage shutdown draining without schema/blob/protocol change.
- 1,050 passing Release assertions across 25 suites and a zero-warning Debug build.
- Tracked CSV/scenario/context artifacts under
  `bench/results/2026-08-18-renderer-budgets-large-cache`.

## Decisions

- Persistence revision remains separate from mip content revision because row flags can
  change without terrain mutation.
- Pending snapshots coalesce by key, but an executing write is never cancelled; an exact
  stale acknowledgement simply cannot clear newer dirty state.
- Retries are unbounded in count while the world is active but bounded in delay. Shutdown
  remains time-bounded and reports exact unresolved obligations.
- No compatibility number changed because revision identity is neither serialized nor
  transmitted.

## Traps

- Optional parameters do not provide the intended default constructor semantics after a
  class-to-struct conversion. Use an explicit parameterless constructor and exact-form
  regression check.
- The Windows runner deploys existing build output. Build immediately before evidence and
  verify source-specific telemetry; plausible frame CSVs do not prove source freshness.

## Flagged and unverified

- No person watched the 3,132-section route for clipping, turn-around stalls, or visual
  replacement artifacts.
- The renderer measurements cover one GPU/driver and are not controlled before/after FPS
  comparisons.
- Persistent write failure and exact timeout reporting were not injected into a game
  process; deterministic checks and source paths cover them.
- Integrated-singleplayer interruption and sibling-cache retry behavior remain open.
