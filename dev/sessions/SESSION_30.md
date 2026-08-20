# Session 30 — Four faults in one colour

**Date:** `2026-08-19`
**Branch/commit:** `codex/chunk-aware-mask`, from `809c74c`
**Mod version:** `0.3.18`, `0.3.19`, then `0.3.20`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The session opened as "let's tackle the coloring issue" against a `dev/TODO.md` that
described one colour problem: seams in cached water. The owner stopped the work in progress
to say the TODO was wrong. The real report was **land**: one cached tile flat green, the tile
beside it flat brown, a hard edge between them, "dramatically jarring". Water was a separate
symptom and stayed out of scope.

Both faults this session were diagnosed **entirely offline** — no game run, and no build
shipped to test a hypothesis — from the owner's own cache database and the decompiled engine.

Out of scope and still open: the water seams, the join-time residency slowdown, the
coarseness report.

## Work narrative

1. **The stored data was the evidence.** Before theorising about the renderer, the client
   cache at `ModData/vintagehorizons/<world>.db` was decoded directly — version byte, raw
   deflate, palette / run counts / runs / captured mask per `LodStore.Serialize`. Grouping
   every level-0 palette entry by block code answered the question immediately: **73 block
   codes were stored under more than one colour**, and `soil-low-normal` — ordinary grassy
   ground, the most common surface block in the world — had **38 different colours across
   1,041 sections**, with a per-channel standard deviation of 30-39. Deterministic blocks
   (gravel, rock, forest floor) had standard deviation exactly 0.

   Two candidate mechanisms were then discriminated with the same data. Mapping the colours
   spatially showed them interleaved like static, not clustered, and mean surface height was
   identical across colour families (117.6 / 119.1 / 118.2), which killed snow, climate and
   altitude. What remained was a per-section random draw.

2. **The engine confirmed it in one decompile.** `BlockSoil` extends
   `BlockWithGrassOverlay`, whose `GetColorWithoutTint` answers with
   `BlockTextureAtlas.GetRandomColor(grassTex)` — and `GetRandomColor` is literally
   `RndColors[rand.Next(30)]`, one of thirty pixels sampled out of the texture. A palette
   entry is registered once per section, so one draw decided the colour of every instance of
   that block across a whole 64-block tile. The measured 38 colours against a pool of 30
   draws is the signature.

   The same call's base implementation also delegates to whatever DECOR sits on the up face,
   so the answer depends on the sample position for a second, independent reason.

3. **The fix is one colour per block id, averaged.** Averaging many draws collapses a
   randomized answer to the texture's own mean and leaves a deterministic answer exactly
   where it was, so a single code path serves both without needing to know which blocks
   randomize. Probing in the sky above the world origin removes the decor branch. Blocks
   carrying an entity keep the real position, which is the case that argument exists for.

   The expected result was predictable from the cache before building: the mean of the
   stored draws for `soil-low-normal` is `#79827B`, a near-neutral base for the live tint to
   green — which is what it should have been all along.

4. **Existing caches repair rather than wipe.** `Reclassify` already re-derived flags and
   tint slots from the live block on load; colour joined them, with 0 meaning "keep what is
   stored" so a chiselled block's captured materials survive. A schema bump would have
   thrown away weeks of exploration to fix a colour.

5. **Human-tested: "it looks so much better".** The owner then answered the question the
   0.3.18 report had asked — the green itself was still slightly off — and volunteered the
   right suspect: *"it may relate to the fact that the grass color can change with the
   season"*.

6. **The second dice roll was the tint.** `seasonalGrass` is a **128x16** map. X is the point
   in the year, which the mod already followed. Y is picked from a hash of each individual
   block's position, which is what makes a real meadow mottled — and the mod sampled one
   position, took one of the sixteen rows, and painted every distant field with it. Measured
   off the shipped map: at midsummer the rows run `#628100` to `#97B825` about a mean of
   `#7B9C0D`, up to a quarter off in red, and it re-rolled whenever the player moved far
   enough to change the hash. 0.3.19 averages 64 positions on an 8-block lattice.

   The `(rain, temp)` overload looked like a cheaper way to walk the rows directly. It is not
   usable: `seasonYPixelRel` is not exposed on `IClientWorldAccessor`, so it silently pins
   every sample to row 0, and it drops the height-above-sealevel term the two-altitude tint
   depends on.

7. **A screenshot reopened it, and there were two more.** On 0.3.19 the owner reported
   distant grass still substantially greener than vanilla's and offered a guess: *"it almost
   looks like the colors should be swapped between the grass and the tree color."* That was
   literally true. `GetAverageColor` is byte-reversed and `GetRandomColor` is not, so red
   arrives in opposite bytes; `BlockWithGrassOverlay` is one of the few classes answering
   `GetColorWithoutTint` with the latter, so grass-covered ground alone had red and blue
   exchanged while leaves and rock were correct. Both populations are visible in the cache:
   rgb(105,83,60) from the repair path, which is the dirt texture read correctly, and
   rgb(128,141,140), which is the grass overlay's rgb(148,149,129) swapped.

   Under it sat a larger fault. `chunktopsoil.fsh` draws these blocks as
   `brownSoil * (1 - grass.a) + grass * grass.a` and colour-maps only the grass; the overlay
   is about 69% opaque, so a third of every grassy block is untinted brown. The mod tinted
   all of it, which removed the olive and — because the seasonal tint's blue channel is near
   zero — essentially all the blue. 0.3.20 composites the two from the atlas and dilutes the
   tint slot by the untinted share, which is now part of the slot key.

   Measured against the shader for `soil-low-normal` at midsummer: G/R 1.03 against vanilla's
   1.09 where 0.3.19 was 1.40, and B/G 0.33 against 0.26 where it was 0.08.

---

## Delivered

Source, 0.3.18:

- `StableColorOf`: one colour per block id, averaged over 64 draws of
  `GetColorWithoutTint`, probed in the sky so the decor branch cannot contaminate it, cached
  for the session. `DescribePalette` uses it for every block with `EntityClass == null`;
  chiselled and ground-storage blocks keep the per-position sample.
- `RecolorForeignSection` and `AtlasColorOf` route through the same value, so a
  server-supplied section cannot become a colour step against a locally captured one.
- `LodStableColorResolver` carries the colour into `LodStore.ClassifyBlock`, so sections
  already on disk are corrected as they load. 0 means "keep what is stored".
- `RepairPlaceholder` factors out the unknown.png repair with an explicit fallback, so the
  chisel path keeps its sampled colour as the last resort and the repair paths keep grey.

Source, 0.3.20:

- `TryTopSoilColor` rebuilds vanilla's top-soil composite from the atlas for any block in the
  `TopSoil` render pass, using `GetAverageColor` for both textures so nothing arrives
  byte-swapped, and taking the overlay's coverage from the alpha `AvgColor` carries.
- `LodUntintedShare` and `LodTopSoil` carry the split; the tint-slot key includes the share
  bucket so full, sparse and very sparse coverage do not share one dilution.
- New `TopSoilColorChecks`: 43 assertions, including the identity
  `composite * (share + (1 - share) * tint) == soil*(1-a) + grass*a*tint` over 1,440
  combinations. Totals now 1,431 Release assertions and 1,388 documentation checks.
- G48 (opposite channel orders) and G49 (top-soil compositing).

Source, 0.3.19:

- `LodTintRegistry.Sample` averages 64 positions on an 8-block lattice, clamped into the
  map, unpacking the returned int by hand to avoid 64 `float[4]` allocations per slot per
  height.

Checks: 1,388 Release assertions (was 1,383), 1,385 documentation checks.
New: `AStoredColourIsRefreshedFromTheLiveBlock`.

Documentation: G46 and G47; STATUS, CHANGELOG and TODO updated, including a correction to
the TODO entry that had merged the land and water colour reports.

## Decisions

- Average draws rather than switch to `GetAverageColor`. The atlas average for grass-covered
  soil is the **dirt** texture, which the live tint would then green into mud; the average of
  the draws is the grass overlay's own mean, which is the correct untinted base.
- Predicate on `EntityClass != null` rather than a list of block codes. Chisels, ground
  storage and display blocks are exactly the blocks whose colour is a function of position,
  and they are exactly the blocks with entities.
- Repair on load rather than bump the schema. A cache is worth weeks of exploration and the
  load path already re-derived two other palette fields from the live block.
- Probe the sky, not the block's own position. The decor branch is a second position
  dependence and would have let one snowy sample decide a block's colour everywhere.
- Fix the tint by averaging positions, not by nudging a constant. A constant would have been
  wrong again at the next season.

## Traps

- **Trigger:** an engine call that reads like a property of a thing — "the colour of this
  block", "the tint for this season". **Failure:** both were draws from a distribution, and
  a single call stored as a fact produced a visible artifact at LOD distance in each case.
  **Safer:** for anything a distant pixel integrates over many blocks, take the mean, and
  check the engine IL for `Rnd`/hash terms before trusting one call. Promoted to G46, G47.
- **Trigger:** an open TODO entry describing a symptom nobody has re-read against the
  original report. **Failure:** the entry merged the land colour report into the water seam
  report, and the session began investigating water. The owner caught it in one sentence.
  **Safer:** when a report is second-hand prose, confirm the symptom with the person before
  spending the session on the write-up's version of it.
- **Trigger:** reaching for a public overload that takes the parameter you want.
  **Failure:** `ApplyColorMapOnRgba(rain, temp, ...)` exposes no `seasonYPixelRel`, so it
  would have pinned every sample to row 0 and looked like it worked. **Safer:** check what
  the interface actually exposes, not what the implementation accepts.

## Flagged and unverified

Judgement calls awaiting human review:

- 0.3.20 has not been seen in game, and 0.3.19's tint averaging was never judged on its own
  either - the screenshot that prompted 0.3.20 was taken on it. Only 0.3.18 is
  human-confirmed.
- The atlas reports overlay coverage from four pixels: 146 against a true 175 for full grass.
  The LOD therefore shows slightly more untinted dirt than vanilla does, which is why the
  predicted B/G lands at 0.33 against vanilla's 0.26. Conservative direction, deliberately
  not corrected by a texture-specific fudge factor.
- 0.3.20 changes the stored colour of every block vanilla draws in the TopSoil pass - soil,
  peat, clay, cob and forest floor - not only full-coverage grass.
- The averaged tint is now the *mean* of a spread that vanilla renders as per-block mottling.
  That is right for distant ground, where a pixel covers many blocks, and it is a deliberate
  loss of variation at the near edge of the cached band.
- The lattice is 64 positions over a 56-block square, centred on the camera. Distant terrain
  in a different climate still takes the viewer's tint; that predates this work and is
  unchanged.

Claims lacking their evidence level:

- Still no benchmark since 0.3.9. Neither change was measured for frame-rate effect, though
  both run off the hot path: the colour once per block id per session, the tint every 240
  frames.
- The 38-colours measurement is one world's cache. The mechanism is certain from the engine
  IL; the magnitude in other worlds is not measured.
