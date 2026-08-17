# CLAUDE.md — Vintage Horizons working agreement

> Read this at the start of consequential project work. This is a briefing and routing document, not a second copy of the project documentation.

## What this project is

Vintage Horizons is a C#/.NET 10 mod for Vintage Story 1.22.5 and later. It builds and renders a persistent multi-level terrain cache beyond vanilla view distance. The client works on servers that do not have the mod; an optional server installation can capture, sweep, generate, and share LOD sections.

For the exact current version, compatibility numbers, evidence, open work, and known uncertainty, read `STATUS.md`. Do not duplicate those mutable facts here.

## Canonical workspace and source authority

- This repository is the canonical working tree for Horizons development.
- `origin` is the user's fork at `https://github.com/DodenGruva/Horizons` and is the future push target.
- The supplied source snapshot was matched to the fork's August 3 history. Current work starts from `origin/master` release 0.2.1 on `codex/main-thread-performance`; do not reapply older snapshot files over it.
- Source code and executable checks outrank prose about current behavior.
- `dev/ARCHITECTURE.md` owns durable design and settled invariants.
- `STATUS.md` owns current state and verification evidence.
- `dev/TODO.md` owns open work and verification debt.
- `dev/WIRE_HISTORY.md` owns protocol, blob-format, and database-schema compatibility history.

## Authority map

Human authority:

- Product direction, player experience, priorities, and acceptance of design tradeoffs.
- In-game evaluation of rendering quality, stutter, controls, and multiplayer behavior.
- Approval to commit, push, publish, package a public release, or make an irreversible change.

AI authority:

- Inspect the repository and local development evidence.
- Make requested edits inside the workspace and run proportionate local checks.
- Make reversible implementation judgments within an approved plan, then flag material choices clearly.
- Maintain documentation when the human requests a documentation update or session finalization.

Ask first:

- Before pushing, publishing, releasing, deleting material project data, or materially expanding scope.
- When a missing product decision would produce meaningfully different player-facing behavior.

Completion evidence:

- Relevant automated checks pass, or failures and their scope are reported precisely.
- Performance claims include a reproducible scenario and measured before/after evidence.
- Runtime or visual claims are distinguished from source inspection until playtested.
- Current-state documentation is updated when the human requests session finalization.

## If you are doing this, read this first

| Task | Read first |
|---|---|
| Changing tick, render, scheduling, or performance behavior | `dev/GOTCHAS.md` G2–G8, then `dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md` |
| Adding worker-thread work | `dev/ARCHITECTURE.md` concurrency invariants and `dev/GOTCHAS.md` G1, G7, G9 |
| Changing storage or serialization | `dev/WIRE_HISTORY.md`, `dev/ARCHITECTURE.md`, and `dev/GOTCHAS.md` G1, G7 |
| Adding or changing a network message | `dev/WIRE_HISTORY.md` and `dev/GOTCHAS.md` G6 |
| Changing traversal, mesh residency, or eviction | `dev/GOTCHAS.md` G5 and G8 |
| Touching shaders | `dev/GOTCHAS.md` G10 |
| Starting a Vintage Story client/server test | `dev/GOTCHAS.md` G11 and `README.md` testing instructions |
| Deciding whether an unusual design is intentional | `dev/ARCHITECTURE.md` settled decisions, then `dev/GOTCHAS.md` reversals |
| Updating documentation | Follow the ritual below and run `dev/DocCheck.ps1` |

## Build and validation

- Build: `dotnet build VintageHorizons/VintageHorizons.csproj`
- Fast checks: `dotnet run --project tests/VintageHorizons.Checks/VintageHorizons.Checks.csproj --configuration Release`
- Full test tiers and isolated game-process rules are documented in `README.md`.
- Set `VINTAGE_STORY` to the game install when it is not at the project fallback location.
- Never launch or stop a Vintage Story test process outside the repository's isolation scripts.

## Working method

For substantial changes, keep these phases distinct:

1. Explore and source-trace the problem.
2. Decide using explicit criteria and record material alternatives.
3. Implement the smallest independently measurable change.
4. Verify against the original problem and regression risks.
5. Convert repeatable lessons into tests, checks, or durable gotchas.

Review and implementation are separate activities. A report-only review should finish and consolidate its findings before fixes begin.

Budget frame-critical work by elapsed time or bytes, not only by item count. Any asynchronous result that can become stale must carry enough identity or revision information to be rejected safely.

## Evidence vocabulary

- **Built:** compilation succeeded.
- **Source-traced:** behavior was established by inspection.
- **Harness-tested:** an isolated automated check passed.
- **Integration-tested:** connected components passed together.
- **Human-tested:** a person used or evaluated the result in game.
- **Production-observed:** behavior was observed in a real deployment.

Do not present one evidence level as another.

## Documentation update ritual

When the human says to update the documentation or finalize a session, run this order:

1. `dev/sessions/SESSION_<n>.md` — create from `TEMPLATE.md`.
2. `dev/sessions/INDEX.md` — add one newest-first row.
3. `CHANGELOG.md` — update only for released or explicitly changelog-worthy changes.
4. `dev/GOTCHAS.md` — add newly proven traps or reversals.
5. `dev/WIRE_HISTORY.md` — update only when protocol, blob format, or schema meaning changes.
6. `dev/TODO.md` — keep open work only; move completed narrative to `dev/history/DONE.md`.
7. `STATUS.md` — regenerate as a coherent current-state document.
8. `CLAUDE.md` — edit only if a working rule or routing pointer changed.
9. Run `dev/DocCheck.ps1`; it must pass or the exact limitation must be recorded.

## Document tiers

- Tier 0, always loaded: `CLAUDE.md` and the `AGENTS.md` bridge.
- Tier 1, durable: `dev/ARCHITECTURE.md`, `dev/GOTCHAS.md`.
- Tier 2, current: `STATUS.md`.
- Tier 3, history: `CHANGELOG.md`, `dev/plans/`, `dev/sessions/`, `dev/history/`.
- Tier 4, frozen archive: `dev/archive/`; never cite it as current.

Information is organized by lifetime. Stale authoritative context is more dangerous than missing context, so do not maintain two living documents for the same fact.
