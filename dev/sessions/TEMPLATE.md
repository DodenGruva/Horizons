# Session <n> — <short title>

**Date:** `<yyyy-mm-dd>`
**Branch/commit:** `<branch and immutable commit when available>`
**Mod version:** `<version>`
**Assist protocol / blob / schema:** `<p> / <b> / <s>`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

What prompted the session, which evidence was inspected, and what remained outside scope.

## Work narrative

Record the reasoning arc, important alternatives, and verification in enough detail to avoid rediscovery. Number subsections when later documents may cite them.

---

## Delivered

Factual outcomes, grouped by revision or coherent artifact. Distinguish documentation, source, tests, and playable releases.

## Decisions

What was chosen and why. Include rejected alternatives when a later contributor might reasonably propose them again.

## Traps

Anything that cost time and would cost it again. Write each as trigger, failure, and safer action so it can be promoted into `dev/GOTCHAS.md`.

## Flagged and unverified

Keep two categories separate:

- Judgement calls awaiting human review.
- Claims that lack the evidence level required to treat them as established.

At session close, update the session index; update the changelog for every release and for
significant established session changes under `Unreleased`; update gotchas, the
compatibility ledger when needed, TODO/DONE, and regenerated status; then run
`dev/DocCheck.ps1`.
