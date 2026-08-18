# Session 8 — continuous benchmark route and camera evidence correction

**Date:** 2026-08-17
**Branch/commit:** `codex/main-thread-performance`, changes based on `51dd175`
**Mod version:** `0.2.1`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## 1. Context and investigation

The next open performance item was a longer deterministic movement and camera-rotation
scenario. The existing benchmark understood only fixed waypoints: every sample began with
a teleport, settled, and then held a constant position and view. That route reproduced
capture and mip work but could not exercise ordinary continuous camera movement,
projection hysteresis, or turn-around behavior.

After the first trajectory smoke, the human noticed that the camera was looking into the
sky rather than at terrain. Inspection of the screenshots confirmed the problem existed
in both the new and old routes. Source tracing the installed Vintage Story 1.22.5 client
showed that camera pitch is PI-centred and normally clamped between approximately PI/2 and
3PI/2. The harness had passed conventional zero-centred route radians directly and had
never pinned `MousePitch`.

This invalidated the old route as terrain-rendering or visual-quality evidence. It did not
invalidate its owning-thread mip diagnosis: teleports, chunk capture, pipeline work, and
mip propagation still occurred, pipeline time tracked total game-tick time directly, and
the before/after comparison used the same route defect on both sides.

## 2. Deterministic trajectory routes

Route entries can now optionally add `->` followed by an end position, yaw, and pitch.
During the measurement interval the harness interpolates player position and camera angle
from start to end using elapsed time, so path speed is independent of frame rate. Existing
fixed routes retain their original syntax and behavior.

The new `bench/routes/moving-rotation.txt` route forms a closed 1,600-block loop. Each of
its four 400-block legs rotates the camera through one complete turn. A 30-second measure
interval produces a 13.3-block/second path; warm-up and measured laps remain controlled by
the existing runner options.

The Windows runner now declares its PowerShell 7 requirement explicitly. It depends on
`ConvertFrom-Json -AsHashtable` and `Start-Process -Environment`, which Windows PowerShell
5.1 does not provide. The documented invocation uses `pwsh -File`.

## 3. Correct camera mapping and evidence scope

Route files keep their readable convention: zero degrees is the horizon and negative
pitch looks down. The harness translates that to the engine with
`enginePitch = PI - routePitch`; for example, route -20 degrees becomes engine PI +
20 degrees. It pins `CameraPitch`, `MousePitch`, camera yaw, mouse yaw, and entity view
angles every frame.

A one-view high-overlook smoke verified the mapping visually, followed by a corrected
four-leg trajectory smoke. Both completed through the isolated Windows runner and shut
down client and server gracefully. The corrected trajectory screenshots show terrain;
the fast cold east leg also visibly showed coarse/incomplete LOD fill-in, demonstrating
why the sky-facing route could not support visual conclusions.

The short corrected trajectory reported no mod errors and no game ticks at or above
25 ms. It captured 1,554 columns by the final telemetry sample and reported zero to two
projection resets per 15-second telemetry interval. The run used only five seconds per
leg with no warm-up, so these numbers are integration evidence, not controlled performance
or clipping acceptance.

The four small before/after mip CSVs were promoted from the ignored sandbox into
`bench/results/2026-08-17-mip-worker`. Their README records the scenario, summarized mip
telemetry, and the camera limitation. The large sandbox world, logs, and screenshots stay
ignored and are reproducible through the committed harness.

## 4. Verification and handoff

- Debug builds of the mod and benchmark harness succeed against Vintage Story 1.22.5 with
  zero warnings and errors.
- The full Release fast tier passes 900 assertions across 22 suites; the new benchmark
  route suite contributes 23 assertions for legacy parsing, trajectory parsing,
  interpolation/clamping, full-turn yaw preservation, PI-centred pitch conversion, and
  closed-loop continuity.
- The corrected static and trajectory smokes completed with no mod error signature, no
  settle timeout, and no lingering isolated client/server pidfile.
- `git diff --check` passes.
- `dev/DocCheck.ps1` passes 187 checks under both Windows PowerShell 5.1 and PowerShell 7.

---

## Delivered

- Backward-compatible fixed and deterministic trajectory route parsing.
- Elapsed-time position/yaw/pitch interpolation and a reusable 1,600-block moving route.
- Correct PI-centred Vintage Story camera-pitch mapping with both mouse axes pinned.
- A clear PowerShell 7 requirement for the Windows benchmark runner.
- Twenty-three benchmark-route regression assertions and 900 total fast assertions.
- Corrected terrain-facing static and moving integration smokes.
- Four tracked mip before/after CSVs with an explicit evidence-scope README.
- Corrected current status, plan, TODO/DONE, changelog, gotchas, and session history.

## Decisions

- Keep route pitch human-readable and translate at the engine boundary instead of changing
  every existing route to PI-centred radians.
- Interpolate a harness-owned trajectory rather than synthesize keyboard input. This keeps
  position and camera progress reproducible across frame rates while the game continues
  normal streaming and capture work.
- Preserve the old mip CSVs because they support the same-route owning-thread comparison,
  but explicitly reject them as terrain-rendering, GPU, clipping, or visual evidence.
- Commit harness source, routes, checks, and small CSV evidence. Keep the game sandbox,
  save, logs, and multi-megabyte screenshots ignored.
- Require PowerShell 7 explicitly rather than partially adapting the runner to 5.1; its
  process-environment isolation relies on PowerShell 7 functionality.

## Traps

- Vintage Story camera pitch is PI-centred. A plausible zero-centred route silently looks
  skyward while still producing believable frame-time CSVs.
- Pinning `CameraPitch` without `MousePitch` leaves the input-owned value able to restore a
  different view on the next camera update.
- A benchmark can correctly exercise owning-thread capture work while being visually
  invalid for renderer conclusions. Inspect screenshots before assigning evidence scope.
- The Windows runner's JSON and process-environment features require PowerShell 7; direct
  invocation from Windows PowerShell 5.1 fails before launch.

## Flagged and unverified

- The moving route still needs a controlled run using its documented 30-second legs,
  warm-up, and multiple measured laps.
- A human must watch the corrected route for clipping and turn-around stalls; endpoint
  screenshots alone cannot establish either.
- The short cold trajectory exposed temporary coarse/incomplete fill-in. Whether that is
  acceptable at realistic movement speed remains a product judgement.
- Old route frame-rate and render-phase values are sky-biased. Only their directly measured
  capture/pipeline/mip comparison remains accepted.
- No compatibility number or public release changed.
