# Session 33 — vanilla-first depth rejection

**Date:** `2026-08-20`
**Branch/commit:** `codex/gpu-overdraw-culling`, baseline `d1d2e8f3080c77629c1dbfaaf73844124f299a39`
**Mod version:** `0.3.30`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

Session 32 left about 150 FPS while looking across roughly 4,000 blocks of mountainous
cached terrain against more than 300 FPS when facing away. Back-face rejection and
front-to-back submission had removed avoidable work but could not reject terrain hidden by a
nearer hill. The owner approved a default-off occlusion experiment with a live command.

The installed 1.22.7 client was inspected read-only. Vintage Horizons registered at opaque
order 0.36 and `SystemRenderTerrain` at 0.37. `ClientEventManager.RegisterRenderer` reads
`IRenderer.RenderOrder` and inserts the renderer into a sorted list once; changing the
property later cannot move it without unregistering and registering again.

## Work narrative

### 1. Same-frame queries were a measurable rejection

The first prototype computed exact opaque mesh bounds, rendered depth-only proxy boxes, and
used same-frame OpenGL conditional rendering. It was off by default behind `.vhocclusion`.
The first package exposed a missing shader dependency: the game's `vertexwarp.vsh` references
`WindModeBitMask`, defined by `vertexflagbits.ash`. Version 0.3.28 added the include and a
static ordering check.

The working query path initially reported 1.5% hidden and changed 156 FPS to 155 FPS. Its
cumulative result later reached 83% hidden with no FPS change. The high rejection rate did
not represent equal work per box; CPU preparation/submission remained, and proxy raster plus
query dependencies replaced the GPU work suppressed. It also could not see the foreground
the owner cared about: queries ran at 0.36, before the current vanilla hill entered depth at
0.37. The query objects, direct OpenTK reference, proxy shaders, exact bounds metadata and
their tests were removed rather than retained as dormant production complexity.

### 2. The accepted design uses depth the game already writes

Version 0.3.29 reused `.vhocclusion` as a render-order experiment. Off registered the cached
pass at the established 0.36; on re-registered it at 0.38, immediately after vanilla terrain.
No new geometry or query is issued. Vanilla ground populates depth, then the ordinary depth
test rejects cached fragments behind it. The ownership mask remains the authority for which
cached fragments may appear; the old order remains the immediate fallback if the changed
overlap precedence is visually objectionable.

The owner repeated the valley comparison with roughly 4,000 blocks of cache behind a current
hill: 148 FPS off and 179 on, about +20.9% and 1.17 ms saved per frame. Looking down at the
ground measured 590 off and 651 on, about +10.3% but only 0.16 ms because the entire frame was
already below 1.7 ms. The ground case is expected: at 0.36 cached pixels shade into an empty
depth buffer before the screen-filling vanilla ground overwrites them; at 0.38 that ground
rejects the cached pixels before their derivative normal, noise, lighting, fog and optional
sky work.

The owner saw minute distant changes, detectable only by toggling immediately, and judged
them entirely acceptable. Version 0.3.30 makes post-vanilla order the default. The command
and `VINTAGEHORIZONS_OCCLUSION_CULLING=0` retain the pre-vanilla fallback.

### 3. Verification and artifacts

Static checks pin the two orders around vanilla's established 0.37, the live unregister /
single registration path, the default-on source and environment override, and the absence
of the rejected query path. The generated Release mod folder was cleared before packaging
so removed proxy shaders could not survive as stale copied assets. The assistant did not
launch or restart Vintage Story; all visual and FPS evidence is owner-run.

---

## Delivered

**Source:** default-on post-vanilla cached pass at order 0.38; live `.vhocclusion off` order
0.36 fallback; rejected query implementation fully removed.

**Tests:** static renderer-order/default/re-registration/query-absence checks; clean Release
build and full fast tier.

**Playable tests:** 0.3.27 shader-failing query prototype, corrected 0.3.28 query prototype,
0.3.29 post-vanilla experiment, and finalized default-on 0.3.30 package.

**Documentation:** changelog, architecture, performance plan, TODO/DONE, G54, status and
session index record both the accepted result and the query dead end.

## Decisions

- Accept post-vanilla depth rejection as default-on. It uses a depth buffer the game already
  needs and produced a material owner-measured gain with acceptable visuals.
- Retain `.vhocclusion off` and an environment override. Post-vanilla rendering changes which
  surface wins overlap while readiness catches up, so an immediate compatibility fallback
  remains proportionate even after acceptance.
- Reject and remove same-frame section queries. An 83% rejection counter with zero FPS gain
  is direct evidence against that implementation, not a reason to tune it further.
- Interpret high-FPS changes in frame time. The 590-to-651 result is 10.3% in FPS but only
  about 0.16 ms of absolute work.

## Traps

- A renderer's live `RenderOrder` property does not reorder it. The engine reads and sorts it
  during registration; a toggle must unregister and register again.
- A high hidden-box percentage neither weights meshes by cost nor proves net savings.
- A pre-vanilla visibility test cannot use a nearby vanilla hill as an occluder.
- Removed assets can persist in the generated mod folder unless that folder is cleared before
  building the replacement archive.

## Flagged and unverified

- The accepted FPS and visual evidence covers one owner, machine, driver, world and immediate
  A/B views. No GPU timers, repeated alternating benchmark, second driver or second machine
  isolates the result.
- CPU traversal, uniform uploads and mesh submission remain. Post-vanilla depth rejection is
  a fragment-side saving, not section-level CPU culling.
- Sustained fast chunk loading, every cave/structure boundary, multiplayer and long-session
  behavior remain ordinary coverage debt despite the owner's overall visual acceptance.
