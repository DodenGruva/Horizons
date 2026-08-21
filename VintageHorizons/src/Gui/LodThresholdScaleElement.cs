using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageHorizons;

/// <summary>
/// One logarithmic distance scale with six draggable LOD markers. A single ordinary
/// slider cannot express an ordered set, and six unrelated tracks hide the relationship
/// the player is editing. Markers therefore share one axis and clamp against their
/// immediate neighbours with a small gap so every handle remains independently usable.
/// </summary>
internal sealed class LodThresholdScaleElement : GuiElementControl
{
    static readonly int[] MarkerColors =
    {
        ColorUtil.ToRgba(255, 104, 190, 255),
        ColorUtil.ToRgba(255, 108, 230, 170),
        ColorUtil.ToRgba(255, 245, 210, 95),
        ColorUtil.ToRgba(255, 255, 155, 90),
        ColorUtil.ToRgba(255, 215, 125, 240),
        ColorUtil.ToRgba(255, 245, 105, 135)
    };

    readonly Action<int, int> onChanged;
    readonly int[] values = new int[LodWorld.MaxLevel];
    readonly LoadedTexture[] markerLabels = new LoadedTexture[LodWorld.MaxLevel];
    int dragging = -1;

    public override bool Focusable => true;

    public LodThresholdScaleElement(ICoreClientAPI api, IReadOnlyList<int> initialValues,
        Action<int, int> onChanged, ElementBounds bounds) : base(api, bounds)
    {
        this.onChanged = onChanged;
        SetValues(initialValues);
        MouseOverCursor = "move";

        int labelWidth = scaledi(34);
        int labelHeight = scaledi(24);
        for (int i = 0; i < markerLabels.Length; i++)
        {
            markerLabels[i] = api.Gui.TextTexture.GenTextTexture(
                $"L{i + 1}", CairoFont.WhiteSmallText(), labelWidth, labelHeight,
                null!, EnumTextOrientation.Center, true);
        }
    }

    public void SetValues(IReadOnlyList<int> newValues)
    {
        int[] normalized = LodWorld.NormalizeLevelThresholds(newValues, LodWorld.DefaultLevelThresholds[0]);
        Array.Copy(normalized, values, values.Length);
    }

    public int[] GetValues() => (int[])values.Clone();

    public override void RenderInteractiveElements(float deltaTime)
    {
        float left = (float)Bounds.renderX + (float)scaled(12);
        float width = Math.Max(1, (float)Bounds.InnerWidth - (float)scaled(24));
        float trackY = (float)Bounds.renderY + (float)scaled(50);
        const float z = 50;

        // The API's rectangle primitive is an outline, so nested rectangles give the
        // track and active handles enough weight without owning a texture per dialog.
        int trackColor = ColorUtil.ToRgba(255, 175, 180, 190);
        api.Render.RenderRectangle(left, trackY, z, width, (float)scaled(4), trackColor);
        api.Render.RenderRectangle(left + 1, trackY + 1, z, Math.Max(1, width - 2), (float)scaled(2), trackColor);

        // Powers of two are the natural landmarks on this logarithmic scale.
        for (int value = LodWorld.MinDetailDistance; value <= LodWorld.MaxLevelThreshold; value *= 2)
        {
            float x = left + width * FractionFor(value);
            api.Render.RenderRectangle(x, trackY - (float)scaled(5), z,
                1, (float)scaled(14), ColorUtil.ToRgba(255, 115, 120, 130));
        }

        for (int i = 0; i < values.Length; i++)
        {
            float x = left + width * FractionFor(values[i]);
            bool above = (i & 1) == 0;
            float handleY = trackY + (above ? -(float)scaled(42) : (float)scaled(18));
            float connectorY = above ? handleY + (float)scaled(24) : trackY + (float)scaled(4);
            float connectorHeight = Math.Abs(trackY - connectorY) + (float)scaled(1);
            int color = MarkerColors[i];

            api.Render.RenderRectangle(x, Math.Min(connectorY, trackY), z,
                1, connectorHeight, color);
            api.Render.RenderRectangle(x - (float)scaled(17), handleY, z,
                (float)scaled(34), (float)scaled(24), color);
            api.Render.RenderRectangle(x - (float)scaled(16), handleY + (float)scaled(1), z,
                (float)scaled(32), (float)scaled(22), color);
            api.Render.Render2DLoadedTexture(markerLabels[i],
                x - markerLabels[i].Width / 2f, handleY, z + 1);
            if (dragging == i)
            {
                api.Render.RenderRectangle(x - (float)scaled(19), handleY - (float)scaled(2), z,
                    (float)scaled(38), (float)scaled(28), color);
            }
        }
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (args.Button != EnumMouseButton.Left) return;
        dragging = NearestMarker(args.X, args.Y);
        UpdateDragging(args.X);
        args.Handled = true;
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (dragging < 0) return;
        UpdateDragging(args.X);
        args.Handled = true;
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        if (dragging < 0) return;
        UpdateDragging(args.X);
        dragging = -1;
        args.Handled = true;
    }

    int NearestMarker(int mouseX, int mouseY)
    {
        double left = Bounds.renderX + scaled(12);
        double width = Math.Max(1, Bounds.InnerWidth - scaled(24));
        double trackY = Bounds.renderY + scaled(50);
        int nearest = 0;
        double nearestDistance = double.MaxValue;

        for (int i = 0; i < values.Length; i++)
        {
            double x = left + width * FractionFor(values[i]);
            double y = trackY + (((i & 1) == 0) ? -scaled(30) : scaled(30));
            double dx = mouseX - x;
            double dy = mouseY - y;
            double distance = dx * dx + dy * dy;
            if (distance >= nearestDistance) continue;
            nearestDistance = distance;
            nearest = i;
        }
        return nearest;
    }

    void UpdateDragging(int mouseX)
    {
        if (dragging < 0) return;

        double left = Bounds.renderX + scaled(12);
        double width = Math.Max(1, Bounds.InnerWidth - scaled(24));
        double fraction = Math.Clamp((mouseX - left) / width, 0, 1);
        double ratio = LodWorld.MaxLevelThreshold / (double)LodWorld.MinDetailDistance;
        double raw = LodWorld.MinDetailDistance * Math.Pow(ratio, fraction);
        int value = (int)Math.Round(raw / LodWorld.ThresholdStepBlocks) * LodWorld.ThresholdStepBlocks;

        int min = dragging == 0
            ? LodWorld.MinDetailDistance
            : values[dragging - 1] + LodWorld.ThresholdStepBlocks;
        int max = dragging == values.Length - 1
            ? LodWorld.MaxLevelThreshold
            : values[dragging + 1] - LodWorld.ThresholdStepBlocks;
        value = Math.Clamp(value, min, max);
        if (values[dragging] == value) return;

        values[dragging] = value;
        onChanged(dragging, value);
    }

    static float FractionFor(double value)
    {
        double ratio = LodWorld.MaxLevelThreshold / (double)LodWorld.MinDetailDistance;
        return (float)(Math.Log(value / LodWorld.MinDetailDistance, ratio));
    }

    public override void Dispose()
    {
        foreach (LoadedTexture label in markerLabels) label?.Dispose();
        base.Dispose();
    }
}
