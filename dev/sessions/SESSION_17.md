# Session 17 — active mip interruption and recovery proof

**Date:** `2026-08-18`
**Branch/commit:** `codex/main-thread-performance`, working tree based on `1087955`
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> This session adds a sandbox-only interruption hook, benchmark guards, regression
> coverage, and evidence. It does not change ordinary terrain behavior, assist protocol,
> stored blob format, or database schema compatibility.

## Context and investigation

The asynchronous mip worker had controlled before/after performance evidence, a long
movement/capture convergence soak, and graceful fresh-process restart proof. The remaining
P1 durability gate was deliberately different: terminate the client after an unfinished
`ApplyToParent` obligation is durable, then prove that a later process consumes and clears
that exact class of work.

A fixed-delay process termination was rejected because it could hit a moment with no
durable obligation and still appear to pass. A separate SQLite watcher was also rejected
as the final design because it duplicated the game's native-provider dependency and could
race a later clearing write.

## Work narrative

1. Added an opt-in storage-thread hook controlled only by private benchmark environment
   paths. After SQLite accepts a snapshot with `ApplyToParent=1`, it writes a marker and
   pauses that writer until the runner releases it. Ordinary processes have no marker path
   and never execute this branch.
2. Added `-InterruptWhenPersistedMip` to the Windows runner. It waits for the durable
   marker, revalidates the exact client PID against the sandbox command line, terminates
   only that client, preserves a scenario record, and leaves the isolated server for one
   explicit `-ReuseServer` recovery run.
3. Added `-RequireMipRecovery`. The recovery client must report one or more persisted mip
   obligations at cache open and must also pass the existing semantic convergence guard.
4. Added a storage fixture proving that the marker appears while the write is outstanding
   and that the named row carries `ApplyToParent` on disk after release.
5. Interrupted the warm-cache frontier run immediately after level-0 section `8004,8000`
   was durably flagged. The recovery process opened the same 601-section cache with one
   persisted obligation and converged every capture/mip/save/load/storage guard to zero.
6. Started a third fresh server/client process. It opened the resulting cache with zero
   persisted obligations and again converged every guarded field to zero, proving that
   the cleared state reached disk.
7. Preserved all three scenario records and both completed-run CSVs under
   `bench/results/2026-08-18-mip-interruption`.

---

## Delivered

- Deterministic sandbox-only durable-mip interruption trigger and PID-verified client
  termination path.
- Recovery guard requiring a real persisted obligation plus clean semantic convergence.
- Fresh-process proof that the recovered obligation's cleared state persisted.
- 968 passing game-backed fast assertions and 247 passing documentation checks.

## Decisions

- Observe a completed flagged database write rather than infer active persistence from a
  timer, queue depth, or route label.
- Pause only the test storage writer after the flagged write so a clearing snapshot cannot
  overtake orchestration before the deliberate interruption.
- Keep integrated-singleplayer recovery as separate verification debt. The completed test
  establishes client-cache behavior across dedicated server/client processes.

## Traps

- A graceful close exercises the shutdown drain and cannot prove crash recovery.
- Killing after a fixed delay can produce a meaningless pass when no `ApplyToParent` row
  is durable at that instant.
- Observing a flagged row without preventing the later clearing write leaves a race between
  the watcher and process termination.

## Flagged and unverified

- The same interruption/recovery path is not yet exercised in an integrated-singleplayer
  process or together with sibling-cache adoption.
- No person watched the interrupted or recovered route; evidence is semantic persistence,
  pipeline telemetry, and process isolation rather than visual review.
