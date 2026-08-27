# Session 62 — Vanilla horizon effects removed

**Date:** `2026-08-27`
**Branch/commit:** `codex/fog-removal`, based on `3ea5ba4`; final session commit pending at record time
**Mod version:** `0.3.125`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

Every earlier Vintage Horizons visual test had also used a third-party mod that hid Vintage
Story's render-distance fog wall and smoothing circle. The owner asked for an original replacement
and explicitly prohibited inspecting, copying, translating, or adapting that mod. Investigation was
therefore limited to Vintage Horizons and the installed official Vintage Story 1.22.7 assemblies
and shader assets.

The engine has no separate horizon-wall renderer. The visible result comes from two independent
mechanisms: `AmbientManager` contributes a default clear-air fog density of `0.00125`, which is
blended with weather and local modifiers, while eight vertex programs apply a radial geometry fade
through `viewDistance`. The affected program names are `chunkopaque`, `chunktopsoil`,
`chunktransparent`, `chunkliquid`, `chunkliquiddepth`, `standard`, `instanced`, and
`entityanimated`. `viewDistanceLod0` is a different control and is not part of this policy.

## Work narrative

### 1. Keep the policy narrower than global fog or view-distance changes

Zeroing the final fog density would also erase weather, underwater, lava, flat-fog, minimum-fog,
fog-sphere, and cloud contributions. Raising the client view-distance setting would change
streaming and culling rather than only the visual fade. Copying engine shaders would be brittle,
would multiply compatibility work, and was unnecessary. A first review also rejected intercepting
every `ShaderProgramBase.Uniform` call: it is a hot, high-frequency upload path.

The final policy instead removes only the propagated share of the engine's default clear-air base
density after ambient blending. It retains a modifier share as `(1 - weight)^2` and clamps safely,
so competing modifiers remain authoritative. The geometry fade gets a visual distance of four
times the configured vanilla distance, or at least the effective far-terrain distance, without
mutating the saved setting or the engine's visibility distance.

### 2. Patch low-frequency official-engine seams and fail open

`VanillaHorizonEffects` installs three Harmony hooks after Vintage Horizons has passed competing-LOD
deferral:

- a postfix on `AmbientManager.UpdateAmbient(float)` removes only the default clear-air base
  contribution from the completed blend;
- a postfix on `ShaderProgramBase.Use()` rewrites `viewDistance` only for the explicit eight-program
  allowlist; and
- a prefix on the `ShaderProgramChunkliquiddepth.ViewDistance` setter catches that program's later
  explicit upload.

Missing targets, invalid values, patch failures, and absent active ownership all preserve vanilla
behavior. Disposal first makes the hooks inert, then removes only the exact Harmony ID. Shader
reloads require no retained GL object because the hooks attach to engine class methods. Deferral and
shutdown restore vanilla behavior cleanly.

### 3. Verify source behavior and deliver a playable artifact

`HorizonEffectsChecks` pins allowlist exactness, target availability, real in-process Harmony
installation/removal, idempotent disposal, distance arithmetic, modifier math, and invalid-value
fail-open behavior. Static checks pin lifecycle ordering, exact unpatching, the absence of a hot
`Uniform` interception, and the exclusion of `viewDistanceLod0`.

Warning-free Release and Debug builds pass. The final fast tier passes 5,425 assertions, including
37 focused horizon-effect assertions, and 1,580 documentation checks pass. The verified 14-entry
`0.3.125` package contains no PDB,
preserves the asset tree, and its DLL matches the Release output. It was copy-installed with
matching SHA-256 `C84DBA0CDB035B7B7B8ADA68D9A16CE4FBFC4832E1AADCAC76679CF5A625E3B1`.
The assistant did not launch the game. The owner played the build and reported, "This looked
great," accepting the clear-horizon result.

---

## Delivered

- Added `VanillaHorizonPolicy` and `VanillaHorizonEffects`, wired after LOD deferral and before the
  terrain renderer's lifetime ends.
- Removed the vanilla clear-air distance veil and the explicit radial smoothing circle without
  changing saved view distance, streaming/culling distance, or `viewDistanceLod0`.
- Added 37 focused assertions and static lifecycle/hot-path checks; 5,425 assertions and 1,580
  documentation checks pass overall.
- Built, verified, copy-installed, and human-accepted `vintagehorizons_0.3.125.zip`, SHA-256
  `C84DBA0CDB035B7B7B8ADA68D9A16CE4FBFC4832E1AADCAC76679CF5A625E3B1`.
- Updated completion history, changelog, gotchas, TODO, status, and the session index. Protocol,
  blob format, and database schema remain 1/4/6, so `dev/WIRE_HISTORY.md` did not change.

## Decisions

- Use only installed official Vintage Story 1.22.7 behavior and interfaces; no third-party mod code
  was inspected or used.
- Remove only the propagated default clear-air fog contribution rather than zeroing the final fog
  density.
- Rewrite only the explicit `viewDistance` fade uploads through a fixed allowlist; do not change the
  user setting, engine visibility range, `viewDistanceLod0`, or copied shader assets.
- Patch `Use()` and the liquid-depth late setter rather than the hot general `Uniform` path.
- Fail open and restore vanilla behavior when deferred, inactive, incompatible, or disposed.

## Traps

- The fog wall and smoothing circle are separate mechanisms. Fixing only one leaves the other
  visible.
- Final fog density contains unrelated weather and local effects; blanket zeroing destroys them.
- `viewDistance` and `viewDistanceLod0` are not interchangeable.
- A general uniform-upload interception appears convenient but runs on a high-frequency path.
  Prefer explicit low-frequency lifecycle seams and an allowlist. Promoted as G117.

## Flagged and unverified

- The owner accepted the ordinary clear-horizon appearance. Weather, underwater, lava, local fog,
  shader reload, competing-LOD deferral, and unload restoration were preserved by source design and
  harness checks but were not separately human-played in this session.
- The hooks target official Vintage Story 1.22.7 methods. Future engine signature changes should
  fail open and log instead of changing rendering, but that compatibility path has not been tested
  against another engine version.
- No push, tag, or public release was performed.
