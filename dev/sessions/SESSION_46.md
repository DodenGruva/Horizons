# Session 46 — Same-frame depth and indexed packed quads accepted

**Date:** `2026-08-24`
**Branch/commit:** `render-overhaul`, on top of `363a286`
**Mod version:** `0.3.84` split accepted; `0.3.85` packed arrays rejected; `0.3.86` indexed packed accepted
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

Session 45 rejected previous-frame cached-on-cached depth after the owner saw terrain flicker in
the middle of the screen while turning. The next implementation split opaque cached terrain into
near and far buckets, rebuilt the depth pyramid between them, and tested the far bucket against
that same-frame picture. The owner played 0.3.84 and reported that the flicker is gone and it runs
very well.

Asked what came next, the owner chose Phase 7 and explicitly confirmed that cluster subdivision
must remain in the optimization sequence. The packed work therefore had two constraints: preserve
the accepted expanded/legacy paths as complete fallbacks, and establish the geometry/range
foundation the later cluster phase will consume.

## Work narrative

### 1. The format choice

One expanded greedy rectangle costs four 16-byte vertices and six 4-byte indices: 88 bytes. Its
exact inputs are four section-column X/Z endpoints in 0-64, two heights (quarter-block precision is
needed by thin cover), one of six face windings, and unmodified 32-bit RGBA/tint. Eight bytes cannot
hold all of that; sixteen adds no fidelity. Twelve bytes fit exactly:

- word 0: four 7-bit X/Z endpoints and three face bits;
- word 1: two quarter-block unsigned 16-bit heights;
- word 2: exact RGBA/tint.

That reduces the regional opaque representation by 86.4% without quantizing an established shader
input.

### 2. Publication and drawing

The greedy mesher writes packed words at the same emission point as the established quad rather
than reconstructing them from expanded arrays. A separate bounded arena publishes packed ranges
beside the accepted vertex/index pair and uses the same fence-retired replacement lifetime.

Packed commands use `MultiDrawArraysIndirect`, padded to the existing five-word command stride so
the accepted cull shader can continue zeroing `instanceCount` unchanged. The vertex shader pulls
three scalar words per quad from an SSBO using `gl_VertexID` and expands the original winding into
six triangle vertices. Scalar `uint[]` is deliberate: an std430 `uvec3[]` would stride at sixteen
bytes and silently lose the chosen format's benefit. Per-section records, ownership addressing,
tinting, lighting, fog and fragments remain in the shared shader body.

`.vhpacked` is session-only and `VINTAGEHORIZONS_GPU_PACKED` / `-GpuPacked` make controlled runs
scriptable. The path is selected only when its shader, arena, builder and drawer are ready. Missing
packed data drops that section to the established same-frame fallback; a refused draw returns the
whole bucket to legacy drawing.

### 3. Verification

The repository fast tier passes **3,905 assertions with zero failures**. Of those, 780 compare
packed decoding with the expanded mesh over all six faces, L0/L1/L3/L6, maximum height, every
tint/material alpha band, quarter-block cover, degenerate rectangles and invalid inputs. Arena
tests pin byte-for-byte upload and fenced replacement; indirect tests pin the padded draw-arrays
layout and page batching; static checks pin the packed shader wrappers and shared-body branches.
The mod build is warning-free, and the benchmark PowerShell script parses after gaining
`-GpuPacked 0|1`.

### 4. The first hardware result rejected the draw topology

The owner reported identical pixels, but the same scene fell from about 300 FPS expanded to about
260 packed: roughly 0.51 ms or 13% slower. The record itself did remove traffic, but the first
backend used `MultiDrawArraysIndirect` and generated six vertices per quad. The accepted expanded
path is indexed and shades four unique corners, so packing had added 50% more vertex-shader
invocations before paying any bit-decode cost.

The follow-up keeps the 12-byte record and binds one reusable `0,1,2,0,2,3` index pattern. Packed
commands start that pattern at zero and use `baseVertex = firstQuad * 4`; `gl_VertexID / 4` selects
the record and its remainder selects the unique corner. `MultiDrawElementsIndirect` and the GPU's
post-transform cache now run the decoder four times per quad. 3,911 assertions pass.

The owner then compared 0.3.86 in the same scene and reported that packing off and on look exactly
the same and hold the exact same FPS. The indexed topology recovered the complete 0.3.85 regression.
That accepts the format and draw path on the primary driver: packing is neutral in this scene, not
a direct FPS optimization, while giving clusters a compact range representation. Because both
regional forms remain published for the A/B, the process does not yet realize the 86.4% format
reduction as total memory savings.

---

## Delivered

- Phase 6 same-frame near/far depth split in 0.3.84, packaged, installed and human-accepted.
- Exact 12-byte opaque quad encoder/decoder and direct greedy-mesher output.
- Bounded packed regional arena with replacement, reclamation, verification and telemetry.
- Padded draw-arrays indirect commands, batching, OpenGL backend and shared shader decoder.
- `.vhpacked`, `VINTAGEHORIZONS_GPU_PACKED`, and `bench-windows.ps1 -GpuPacked` controls.
- Deterministic format, arena, indirect and static shader checks; 3,911 fast assertions pass.
- `vintagehorizons_0.3.85.zip`, packaged and copied to the normal Vintage Story Mods folder.
- `vintagehorizons_0.3.86.zip`, indexed packed follow-up packaged and installed automatically.
- Primary-driver human acceptance for 0.3.86: exact visual and FPS parity with expanded batching.

## Decisions

**Twelve bytes wins over eight and sixteen.** It is the smallest format that preserves every
accepted input exactly. There is no reason to spend four more bytes until measurement identifies
a decode cost that a wider layout actually removes.

**Cluster subdivision remains Phase 8, immediately after the packed gate if sky overlap remains
the measured limiter.** Cluster ranges should point at the compact representation; doing clusters
first would force two geometry-layout migrations. GPU LOD authority and subdivision will not land
in the same measurement patch.

**Keep both regional representations during the A/B.** This temporarily raises total experimental
arena memory but preserves instant comparison and complete fallback. Once packed is accepted, the
expanded regional copy should stop being retained on the selected product path; legacy `MeshRef`
geometry remains for compatibility and failure repair.

**The six-vertex draw-arrays topology is rejected.** Its human-tested frame-time regression erased
the format's memory benefit. The reusable indexed topology is the smallest follow-up because it
changes neither the record, arena, ownership, fragments nor fallback—only how often a corner is
decoded.

**Proceed with cluster subdivision.** The indexed packed format has passed its primary-machine
gate and whole-section sky overlap remains the measured HZB limitation. Phase 8 will therefore add
moderate mesher-produced clusters before considering GPU-owned LOD selection.

## Traps

- An std430 array of `uvec3` has a 16-byte array stride. Use scalar words for a real 12-byte record.
- `DrawArraysIndirectCommand` is four words, but the existing compute cull indexes five-word slots.
  The rejected first version had to pad it; the indexed replacement naturally returns to the
  established five-word layout.
- In the indexed packed command, count is six indices per quad but `baseVertex` advances four
  virtual corners per arena quad. Multiplying either field by the other stride corrupts geometry.

## Flagged and unverified

**Judgement calls awaiting human review.** The primary driver accepts the indexed 12-byte decoder,
but the dual regional representation is appropriate only for validation, not as the final memory
policy. The later paired route must show whether lower upload/live bytes have an effect outside the
owner's same-scene FPS comparison.

**Claims lacking their evidence level.** 0.3.86 has run only on the primary AMD driver. Offline
checks and one same-scene comparison cannot establish cross-vendor behaviour or isolate upload and
opaque-GPU time. The 86.4% figure applies to the regional opaque geometry representation, not total
process memory while both experimental forms and legacy fallback coexist.
