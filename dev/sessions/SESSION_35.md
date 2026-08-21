# Session 35 — configurable cached-terrain LOD distances

**Date:** `2026-08-20`
**Branch/commit:** `codex/gpu-overdraw-culling` (final commit recorded in Git history)
**Mod version:** `0.3.40`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The owner asked how cached terrain currently became coarser with distance. Source tracing
found two important details. First, `.vhdetail 512` was described as the first transition,
but the selection formula applied the first coarser level at 1,024 blocks and doubled every
later transition as well. Second, distance is measured to the nearest edge of each square
LOD section, not its centre. That nearest-edge rule makes the visible boundary approximate
a circle without pulling a large coarse square inward merely because its centre crossed a
threshold.

The owner chose to correct the threshold semantics while retaining the nearest-edge rule,
then requested a player-facing configuration window and refined its first in-game layout.

## Work narrative

### 1. Correct transition semantics

The default policy now changes at 512, 1,024, 2,048, 4,096, 8,192 and 16,384 blocks for
L1 through L6. This lowers every old transition by one level: L3, for example, begins at
2,048 rather than 4,096. Parent fallback remains intact, so a temporarily unavailable fine
section can still be covered by coarser cached data.

The owner tested 0.3.38 in game and reported an enormous performance increase with very
little visual-fidelity loss. That establishes the corrected defaults as accepted product
behavior, not merely source intent.

### 2. Make all six transitions independently configurable

The single `DetailDistance` policy was replaced internally by an ordered six-threshold
policy. Existing configuration files migrate their legacy value into the equivalent
doubling sequence. `.vhdetail` remains available as a quick way to reset that sequence,
while policy revision identity now changes for edits to any threshold, not only L1. The
dirty scheduler and hot squared-distance lookup both observe that revision, so changing a
middle marker takes effect without restarting or moving the camera.

### 3. Add `.vhconfig`

Version 0.3.39 added a client settings dialog. L1-L6 share one logarithmic distance track,
with each marker independently draggable but constrained between its neighbours. A
separate slider controls how far cached terrain itself is drawn. `Defaults`, `Cancel` and
`Save` respectively restore the recommended policy, discard pending edits, or apply and
persist them live.

The owner confirmed that the window opened and requested a more legible, tighter layout.
Version 0.3.40 enlarged the marker boxes, moved them farther from the track, placed L1-L6
inside them, displayed full comma-separated block counts, limited both scales to 32,768
blocks, and changed cached draw distance to 512-block increments. `Defaults` restores a
32,768-block cached draw cap. The advanced `.vhfar 0` command still permits unlimited
drawing outside the dialog.

### 4. Verification and packaging

Regression coverage pins the corrected transition boundaries, independent middle-level
changes, policy-revision refresh, ordered-neighbour constraints, command registration,
buttons, marker labels, scale ceilings, full-number formatting and the 512-block draw step.
The complete game-backed Release tier passed 1,533 assertions, `dev/DocCheck.ps1` passed,
and the Release build completed with zero warnings and zero errors.

The final game-ready archive is `dist/vintagehorizons_0.3.40.zip`. The same archive was
installed in the owner's Vintage Story Mods folder; both copies had SHA-256
`52B89A30B95187F335DFB6043318B7652C63844E6BD91F27750CE10DCFA2662A` when packaged.

---

## Delivered

- Source: corrected default LOD transitions, explicit six-level policy, live policy
  revision refresh, legacy configuration migration and `.vhconfig`.
- UI: one shared logarithmic LOD scale, six constrained labelled handles, a separate
  512-step cached draw-distance slider, and Defaults/Cancel/Save behavior.
- Tests and documentation: 1,533 Release assertions, current command documentation,
  changelog/status/history updates and a passing documentation check.
- Playable build: `vintagehorizons_0.3.40.zip` in `dist` and the owner's mod folder.

## Decisions

- Retain nearest-edge square-section distance. Centre distance would allow a large coarse
  section to intrude inside the intended fine-detail boundary; farthest-corner distance
  would retain excess fine terrain and surrender much of the performance gain.
- Store six explicit thresholds rather than deriving every level from L1. Players can tune
  the shape of the quality curve, and every change has an explicit revision identity.
- Use one logarithmic scale because the meaningful range spans powers of two, but retain
  individual draggable handles and a minimum 64-block gap so the level order cannot invert.
- Limit the player-facing scales to 32,768 blocks and make cached draw distance advance in
  512-block steps. Keep `.vhfar 0` as the advanced unlimited escape hatch.
- Preserve `.vhdetail` and migrate old configuration rather than invalidating existing
  player settings. No assist protocol, cache blob or database schema change was required.

## Traps

- Trigger: a setting name describes the first transition but the selection formula adds
  one before applying it. Failure: every LOD boundary silently occurs twice as far away.
  Safer action: pin the exact boundary immediately below and at every default threshold.
- Trigger: policy refresh is keyed only by the first threshold. Failure: moving L2-L6 can
  leave traversal or dirty scheduling on stale distances. Safer action: use a monotonic
  policy revision observed by every derived table and scheduler.
- Trigger: custom moving labels are rendered as generated GUI textures. Failure: stale
  labels or leaked textures accumulate across redraws. Safer action: regenerate only when
  needed and dispose every owned texture with the element.

These traps are covered by focused assertions; no broader durable gotcha was added.

## Flagged and unverified

- The owner's first dialog review drove the 0.3.40 layout, but the revised handle spacing,
  full-number labels and finer cached-distance slider still await an in-game visual check.
- The 32,768-block UI cap/default-button value is a product choice, not a controlled
  performance benchmark. Startup configuration remains backward-compatible with unlimited
  cached drawing unless the player saves a capped value.
- Older 0.3.37-0.3.39 archives remain in the Mods folder; Vintage Story's version selection
  should prefer 0.3.40, but they were not deleted without a separate cleanup request.
