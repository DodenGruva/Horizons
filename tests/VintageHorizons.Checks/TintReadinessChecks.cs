namespace VintageHorizons.Checks;

public static class TintReadinessChecks
{
    public static void Run(Check c)
    {
        LateSlotBypassesCadence(c);
        MeshWaitsForEveryUsedSlot(c);
    }

    static void LateSlotBypassesCadence(Check c)
    {
        const long cadence = 30_000;

        c.True(!LodTintRegistry.RefreshDue(
                initialized: true,
                hasUnreadySlots: false,
                elapsedMilliseconds: 1_000,
                intervalMilliseconds: cadence),
            "a stable published table keeps the ordinary 30-second cadence");
        c.True(LodTintRegistry.RefreshDue(
                initialized: true,
                hasUnreadySlots: true,
                elapsedMilliseconds: 1_000,
                intervalMilliseconds: cadence),
            "a tint registered after publication bypasses the 30-second cadence");
        c.True(LodTintRegistry.RefreshDue(
                initialized: false,
                hasUnreadySlots: false,
                elapsedMilliseconds: 0,
                intervalMilliseconds: cadence),
            "a fresh world samples its tint table immediately");
        c.True(LodTintRegistry.RefreshDue(
                initialized: true,
                hasUnreadySlots: false,
                elapsedMilliseconds: cadence,
                intervalMilliseconds: cadence),
            "the stable table still refreshes when its ordinary cadence expires");
    }

    static void MeshWaitsForEveryUsedSlot(Check c)
    {
        var section = new LodSection();
        section.Palette.Add(new LodPaletteEntry { TintSlot = LodTintRegistry.SlotNone });
        section.Palette.Add(new LodPaletteEntry { TintSlot = 1 });
        section.Palette.Add(new LodPaletteEntry { TintSlot = 3 });

        c.True(!LodTintRegistry.SectionTintsReady(section, readySlotCount: 3),
            "a mesh waits when any used registered tint has not been published");
        c.True(LodTintRegistry.SectionTintsReady(section, readySlotCount: 4),
            "the mesh becomes eligible when every used registered tint is published");

        section.Palette.Add(new LodPaletteEntry { TintSlot = LodTintRegistry.MaxSlots });
        c.True(LodTintRegistry.SectionTintsReady(section, readySlotCount: 4),
            "an unencodable tint agrees with the mesher's identity-tint fallback");

        var plain = new LodSection();
        plain.Palette.Add(new LodPaletteEntry { TintSlot = LodTintRegistry.SlotNone });
        c.True(LodTintRegistry.SectionTintsReady(plain, readySlotCount: 1),
            "untinted terrain does not wait for climate sampling");
    }
}
