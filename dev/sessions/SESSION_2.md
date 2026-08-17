# Session 2 — fork reconciliation and 0.2.1 plan rebase

**Date:** 2026-08-17
**Branch/commit:** `codex/main-thread-performance` at `f8d4b03`, tracking `origin/master`
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## 1. Context and scope

The supplied files came from the original project, while `https://github.com/DodenGruva/Horizons` is the user's active fork. The repository began as an extracted, uncommitted tree, so the source and newly created documentation had to be attached to the fork without fabricating unrelated ancestry or overwriting the fork's later work.

## 2. Reconciliation evidence

- Fetched `origin/master` at `f8d4b03` (`Release 0.2.1`).
- Located fork release 0.2.0 at `cd0f196` and compared the supplied tree against later commits.
- Hash-compared every source, asset, project, and mod metadata file across the fork history. The supplied code has zero mismatches at `27e5e6a` (`Test the host privilege in survival, where players actually are`).
- Confirmed that 0.2.1 contains 57 changed paths relative to release 0.2.0, including substantial performance, correctness, benchmark, and test work.
- Created `codex/main-thread-performance` directly at `origin/master` and set its upstream to the fork's `master`.
- Restored fork-current source, tests, scripts, changelog, release guide, README, ignore rules, and design journal. The new tiered documentation remained as intentional worktree changes.

Before replacement, exact local recovery archives were created for the documentation and for every affected source/test/script path. The supplied private research notes were moved to ignored `notes/research/`, matching the fork's publication boundary.

## 3. Plan rebase

The original performance review remains useful, but 0.2.1 is now the implementation baseline. It already fixed or reduced several reviewed costs:

- Cold-cache capture moved from synchronous read/inflate to ordered background reload.
- Cache key startup lookup was indexed.
- Sibling-cache discovery cadence changed from twice per frame to once per second.
- Dirty scheduling stopped rescanning the complete set for each scheduled key.
- Render traversal removed per-node square roots/logarithms.
- Mesher scratch buffers were pooled and basic renderer phase timing was added.

The current source still confirms the full key scan at its lower cadence, full remote-key re-enumeration, whole-mesh far scan and exact projection reset, one-second burst callbacks, unbounded result drains, synchronous mip merging, whole-collection renderer work, and count-only upload limits. The plan and TODO were updated to build on 0.2.1 rather than reimplement its existing fixes.

## Delivered

- **Git ancestry** — Working branch now descends from the user's fork at release 0.2.1.
- **Source preservation** — Older supplied content remains recoverable from fork history and local recovery archives.
- **Documentation integration** — Restored the fork's current player/release documents and added routing into the tiered memory system.
- **Plan rebase** — Recorded existing 0.2.1 optimizations as baseline and retained only remaining work as open.
- **Documentation checks** — 153 checks pass in both Windows PowerShell 5.1 and PowerShell 7 after removing host-specific path APIs.
- **Fast-check evidence** — The 0.2.1 executable completed 658 assertions with no assertion failures; 16 suites completed and the SQLite fixture was blocked by the temporary reference layout's missing native provider.
- **No runtime behavior change** — This session changed documentation and repository state only.

## Decisions

- Start new performance work from `f8d4b03`, never by committing the extracted snapshot as an unrelated root.
- Keep root `DESIGN.md` as a historical design/evidence journal, while `dev/ARCHITECTURE.md` owns current durable architecture.
- Keep private research outside published `docs/`, under the fork's ignored `notes/` boundary.
- Treat release-note measurements as historical project evidence, not as a new local benchmark.

## Flagged and unverified

- The blob-format fixture still needs a complete game install/provider path; the earlier Windows stale-database reopen was not reached.
- Current moving-camera and exploration hitch attribution remains unmeasured.
- No documentation or implementation commit has been created or pushed.
