# Session 55 - Phase 9 begins with guarded GPU failures

**Date:** `2026-08-25`
**Branch/commit:** `render-overhaul`, on top of `f13185a`; implementation and records uncommitted
**Mod version:** `0.3.104`, packaged and copy-installed; four isolated failure injections passed
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

The owner withdrew the 0.4.0 development promotion, directed the next changed build back onto the
latest 0.3.x line, and started Phase 9. The version change was folded into real hardening work rather
than producing a version-only artifact: 0.3.104 follows the last 0.3.x artifact, 0.3.103.

The Phase 9 audit found broad pure coverage for capability refusal, arena allocation refusal,
shadow publication failure, cull refusal, partial multi-draw failure, and same-frame legacy repair.
What did not exist was a guarded runtime way to force the real renderer through those paths. A
hardware fault is the wrong test mechanism: it is nondeterministic and can leave driver state
ambiguous.

## Work narrative

### 1. One named failure per isolated run

`VINTAGEHORIZONS_GPU_INJECT_FAILURE` accepts `arena`, `shader`, `depth-copy`, or `draw`. Parsing is
case-insensitive, unknown values warn and do nothing, and a valid injection is consumed once.

- `arena` refuses setup before any regional mirror is published.
- `shader` withholds both fast terrain variants after the meaningful shader load, leaving the
  established terrain program untouched.
- `depth-copy` enters the existing exception route that disables HZB; unmodified uploaded commands
  then draw without suppression.
- `draw` puts both drawers into the same permanent failed state as a backend refusal before the
  frame chooses its path.

The Windows runner exposes this as `-GpuFailure` and records it in `scenario.json`, so a result can
never lose which artificial boundary it exercised.

### 2. Version and artifact

Both version authorities now read 0.3.104. The Release package contains the licence, no PDB, and a
0.3.104 manifest. SHA-256:
`40EBC8A3215699796EF59C158A828B5ABB23A8603F8FB4F4254174E31DE786AE`.

The zip was copied beside 0.3.103 and 0.4.0 without removing either. That exposes an unavoidable
selection fact: an ordinary launch still chooses 0.4.0 because it is higher. The isolated runner
loads the current build independently; changing the rollback set requires a separate owner
direction.

### 3. Game-backed failure matrix

With fresh owner approval for the four launches, the guarded Windows runner exercised every named
boundary against the frozen `bodanboys` profile. Each run completed all six fixed viewpoints and
reported zero settle timeouts:

- `p9fail-arena-03104`: logged the injected regional-arena setup refusal and that visible legacy
  rendering was unchanged. Because the fault is deliberately one-shot, a later setup attempt
  recovered the arena and the route continued on the fast path.
- `p9fail-shader-03104`: withheld both fast terrain programs at both client initialization stages,
  logged that cached terrain stayed on the established renderer, and completed the route.
- `p9fail-depthcopy-03104`: disabled the depth pyramid for the session, then continued reporting
  1,700-2,059 packed cluster commands in active multi-draw batches. This proves the intended
  fail-open distinction: batching remains active and every CPU-approved command draws without HZB.
- `p9fail-draw-03104`: permanently failed both indirect drawers, reported zero cluster
  multi-draws thereafter, kept expanded/established rendering active, and completed the route.

These are game-backed automated integration results on the primary AMD system. Nobody visually
watched the route, so they do not expand the existing human-acceptance claim.
The route CSVs and a hash-bound proof summary are preserved under
`bench/results/2026-08-25-phase9-failure-injection`.

---

## Delivered

- Phase 9 runtime failure-injection policy and wiring for four GPU boundaries.
- `bench-windows.ps1 -GpuFailure arena|shader|depth-copy|draw`, recorded in the scenario manifest.
- Version 0.3.104 Release build and verified private test zip, copied to the Mods folder.
- Four game-backed isolated failure runs, each completing six viewpoints with zero settle timeouts.
- Warning-free build, 5,083 passing fast assertions, PowerShell parser pass, and documentation
  checks at session close.

## Decisions

**Inject policy outcomes, not invalid GL.** The tests enter the same refusal states real backends
use without asking the driver to execute malformed operations.

**Keep depth failure on the safe indirect path.** A missing pyramid does not require abandoning
batching; it requires drawing every uploaded CPU-approved command. That is the plan's explicit
fallback and avoids pretending every GPU failure must take the identical route.

**Preserve 0.4.0.** Copy-installing 0.3.104 does not authorize deleting or moving the rollback zip.
The lower build will be tested through isolation unless the owner decides otherwise.

**Keep arena injection one-shot.** Its first setup refusal proves the no-publication fallback, while
the later retry proves recovery is possible without a restart. Making the artificial failure sticky
would test a different policy from the named once-per-run mechanism.

## Traps

- G100: a lower development version cannot outrank a preserved higher test zip.

## Flagged and unverified

**Judgement calls awaiting human review.** Whether ordinary play should be made to select 0.3.104
by changing the preserved 0.4.0 artifact. No such change was made.

**Claims lacking their evidence level.** The injection matrix is game-backed on one machine but was
not human-watched and says nothing about another driver. The paired packed route,
settings/lifecycle matrix, multiplayer, long session, allocation pressure, and representative
forced-legacy coverage remain open.
