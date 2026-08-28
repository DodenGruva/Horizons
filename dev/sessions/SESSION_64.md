# Session 64 — Terrain-only horizon wall correction accepted

**Date:** `2026-08-27`
**Branch/commit:** `codex/fog-removal` at `c88154f`; session work uncommitted at record time
**Mod version:** `0.3.127`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

Session 63 rejected 0.3.125's ambient clear-air subtraction and established a strict product and
clean-room boundary: keep all vanilla atmosphere unchanged and remove only the pale circular wall
at vanilla terrain's render threshold. This session used only Vintage Horizons and a fresh
inspection of the installed official Vintage Story 1.22.7 assets and assemblies.

The official terrain renderer draws five relevant programs: `chunkopaque`, `chunktopsoil`,
`chunktransparent`, `chunkliquid`, and `chunkliquiddepth`. The visually similar `viewDistance`
expressions in `standard`, `instanced`, and `entityanimated` belong to general objects, instanced
content, and entities rather than the terrain wall, so they are deliberately excluded. The
official range culler admits terrain mesh centres inside `sqrt(viewDistance^2 + 400)`. The safe
replacement additionally covers half a 32-block chunk diagonal, the official ten-block maximum
third-person camera offset, and sub-block camera/player rounding. The earliest of the five terrain
expressions can begin changing liquid vertices at 72.5% of its uploaded distance; rounding the
derived quotient up gives the smallest safe whole-block replacement.

## Work narrative

The ambient-manager Harmony patch, default-density lookup, blend-share arithmetic, and their tests
were removed completely. The remaining adapter has two structural hooks: a postfix on stable shader
activation and a prefix on the liquid-depth program's later explicit distance setter. It does not
patch the general uniform-dispatch path, alter shader assets, change saved view distance, touch
`viewDistanceLod0`, or change culling, streaming, far-terrain selection, or any fog value.

The allowlist now contains only the five official terrain programs. Missing uniforms, invalid or
unrepresentable values, shader callback exceptions, and structural hook failures preserve the
original vanilla value. Runtime failures disable and diagnose only the affected terrain pass once;
one incompatible pass cannot disable another. Disposal makes callbacks inert before exact-ID
unpatching, and competing-LOD deferral still occurs before installation.

Version 0.3.126 was packaged and copy-installed after the first complete implementation. Final
review then added one-time invalid-value diagnosis and partial-Harmony-patch cleanup. Because that
changed the binary after 0.3.126 existed, G57 required 0.3.127 rather than reusing the patch number.
Both zips remain in the owner's Mods folder; 0.3.126 was not played and is superseded by 0.3.127.

Release and Debug builds completed with zero warnings or errors. The full fast tier passed 5,450
assertions, including 57 focused terrain-wall checks that install and remove the real hooks against
the official assembly without an OpenGL context; 1,585 documentation checks pass. The verified
14-entry 0.3.127 archive contains no
PDB or game dependency, its DLL matches the Release output, and source/installed archives have
matching SHA-256 `5FBEFFCF790B743100A958EA80ED82E9E3C6FA0D02085C6A37B9B61D1A83B886`.
The assistant did not launch the game. The owner played the final build, said it looks good now,
and accepted the requested combination: the terrain-threshold radius is gone while vanilla
atmospheric haze remains visually unchanged.

---

## Delivered

- Removed every ambient fog mutation from the horizon-effects production path.
- Restricted wall suppression to the five official vanilla terrain programs.
- Derived the minimum safe whole-block fade distance from official culling, chunk, camera, and
  shader bounds rather than from Vintage Horizons' far-terrain reach or the old multiplier.
- Added per-pass fail-open isolation, one-time diagnosis, partial-install cleanup, exact hook
  ownership checks, and invalid-input preservation.
- Built, verified, copy-installed, and human-accepted 0.3.127.
- Closed the reopened terrain-wall TODO and finalized current-state documentation with 1,585
  passing documentation checks.

## Decisions

- Vanilla atmosphere is entirely authoritative. Vintage Horizons writes no ambient/base/blended
  fog density or environmental modifier.
- Only `chunkopaque`, `chunktopsoil`, `chunktransparent`, `chunkliquid`, and
  `chunkliquiddepth` are terrain-wall passes. General objects, instances, and entities keep vanilla
  distance behavior.
- The visual fade moves only beyond terrain the engine can draw; it does not follow distant-cache
  reach and does not change the engine's real terrain boundary.
- No command, setting, environment variable, or runtime toggle is added.
- 0.3.126 remains an unplayed intermediate artifact; 0.3.127 is the accepted correction.

## Traps

- Similar shader arithmetic does not establish shared product ownership. The official eight-program
  search result had to be intersected with the actual terrain renderer, yielding five passes.
- Range culling uses a mesh-sphere centre and floored player position while shader distance is
  vertex- and camera-relative. The safe boundary therefore needs chunk and camera slack before the
  earliest fade ratio is applied.
- A changed binary cannot reuse an already-created test version even when the earlier artifact was
  never played. The final hardening change required 0.3.127.

## Flagged and unverified

- Human acceptance covers the reported terrain-threshold wall and ordinary vanilla atmosphere on
  the owner's machine and tested conditions. Underwater, lava, shader reload, competing-LOD
  deferral, unusual shader variants, multiplayer, and other drivers remain source/harness or
  ordinary compatibility coverage rather than separately observed cases.
- The correction has no isolated performance measurement. Its hook surface is smaller than 0.3.125
  and remains off the general uniform hot path, but no frame-time claim is made.
- No commit, push, tag, public release, or assistant-launched game run occurred.
