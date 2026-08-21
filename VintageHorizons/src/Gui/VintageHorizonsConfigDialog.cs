using System.Globalization;
using Vintagestory.API.Client;

namespace VintageHorizons;

/// <summary>Client-only settings window opened by .vhconfig.</summary>
internal sealed class VintageHorizonsConfigDialog : GuiDialog
{
    const int MinDrawDistance = 1024;
    const int MaxDrawDistance = 32768;
    const int DrawDistanceStep = 512;

    readonly Action<int[], int> save;
    readonly int[] pendingThresholds;
    int pendingFarCap;
    LodThresholdScaleElement scale = null!;

    public override string DebugName => "vintagehorizons-config";
    public override string? ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => true;

    public VintageHorizonsConfigDialog(ICoreClientAPI capi, IReadOnlyList<int> thresholds,
        int farCap, Action<int[], int> save) : base(capi)
    {
        this.save = save;
        pendingThresholds = LodWorld.NormalizeLevelThresholds(
            thresholds, LodWorld.DefaultLevelThresholds[0]);
        pendingFarCap = Math.Clamp(farCap <= 0 ? MaxDrawDistance : farCap,
            MinDrawDistance, MaxDrawDistance);
        ComposeDialog();
    }

    void ComposeDialog()
    {
        const double width = 760;
        const double height = 440;
        ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, width, height)
            .WithAlignment(EnumDialogArea.CenterMiddle);

        GuiComposer composer = capi.Gui
            .CreateCompo(DebugName, dialogBounds)
            .AddShadedDialogBG(ElementBounds.Fill, true)
            .AddDialogTitleBar("Vintage Horizons configuration", OnTitleClose)
            .BeginChildElements();

        composer.AddStaticText(
            "LOD transition distances",
            CairoFont.WhiteMediumText(), ElementBounds.Fixed(30, 48, 700, 28));
        composer.AddStaticText(
            "Drag L1-L6 on one logarithmic scale. Markers stop at their neighbours.",
            CairoFont.WhiteSmallText(), ElementBounds.Fixed(30, 76, 700, 24));

        scale = new LodThresholdScaleElement(capi, pendingThresholds,
            OnThresholdChanged, ElementBounds.Fixed(30, 101, 700, 104));
        composer.AddInteractiveElement(scale, "lod-threshold-scale");

        composer.AddStaticText("256", CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(38, 205, 80, 20));
        composer.AddStaticText("32,768 blocks", CairoFont.WhiteSmallText(),
            EnumTextOrientation.Right, ElementBounds.Fixed(620, 205, 100, 20));

        const double labelWidth = 112;
        for (int i = 0; i < LodWorld.MaxLevel; i++)
        {
            composer.AddDynamicText(ThresholdLabel(i), CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(30 + i * 116, 230, labelWidth, 24), $"threshold-{i}");
        }

        composer.AddStaticText("Cached terrain draw distance", CairoFont.WhiteMediumText(),
            ElementBounds.Fixed(30, 270, 700, 28));
        composer.AddSlider(OnDrawDistanceChanged,
            ElementBounds.Fixed(30, 302, 560, 30), "draw-distance");
        composer.AddDynamicText(DrawDistanceLabel(), CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(605, 306, 120, 24), "draw-distance-label");

        composer.AddButton("Defaults", OnDefaults,
            ElementBounds.Fixed(30, 376, 135, 38), EnumButtonStyle.Normal, "defaults");
        composer.AddButton("Cancel", OnCancel,
            ElementBounds.Fixed(455, 376, 120, 38), EnumButtonStyle.Normal, "cancel");
        composer.AddButton("Save", OnSave,
            ElementBounds.Fixed(590, 376, 135, 38), EnumButtonStyle.Normal, "save");

        SingleComposer = composer.EndChildElements().Compose();

        GuiElementSlider drawSlider = SingleComposer.GetSlider("draw-distance");
        drawSlider.SetValues(pendingFarCap, MinDrawDistance, MaxDrawDistance, DrawDistanceStep, " blocks");
        drawSlider.ShowTextWhenResting = false;
    }

    void OnThresholdChanged(int index, int value)
    {
        pendingThresholds[index] = value;
        if (SingleComposer?.Composed == true)
        {
            SingleComposer.GetDynamicText($"threshold-{index}")
                .SetNewText(ThresholdLabel(index), false, true, false);
        }
    }

    bool OnDrawDistanceChanged(int blocks)
    {
        pendingFarCap = Math.Clamp(blocks, MinDrawDistance, MaxDrawDistance);
        if (SingleComposer?.Composed == true)
        {
            SingleComposer.GetDynamicText("draw-distance-label")
                .SetNewText(DrawDistanceLabel(), false, true, false);
        }
        return true;
    }

    bool OnDefaults()
    {
        Array.Copy(LodWorld.DefaultLevelThresholds, pendingThresholds, pendingThresholds.Length);
        pendingFarCap = MaxDrawDistance;
        scale.SetValues(pendingThresholds);

        for (int i = 0; i < pendingThresholds.Length; i++)
        {
            SingleComposer.GetDynamicText($"threshold-{i}")
                .SetNewText(ThresholdLabel(i), false, true, false);
        }
        SingleComposer.GetSlider("draw-distance")
            .SetValues(pendingFarCap, MinDrawDistance, MaxDrawDistance, DrawDistanceStep, " blocks");
        SingleComposer.GetDynamicText("draw-distance-label")
            .SetNewText(DrawDistanceLabel(), false, true, false);
        return true;
    }

    bool OnSave()
    {
        save(scale.GetValues(), pendingFarCap);
        TryClose();
        return true;
    }

    bool OnCancel()
    {
        TryClose();
        return true;
    }

    void OnTitleClose() => TryClose();

    string ThresholdLabel(int index) => $"L{index + 1}: {FormatBlocks(pendingThresholds[index])}";

    string DrawDistanceLabel() => $"{FormatBlocks(pendingFarCap)} blocks";

    static string FormatBlocks(int blocks) => blocks.ToString("N0", CultureInfo.InvariantCulture);
}
