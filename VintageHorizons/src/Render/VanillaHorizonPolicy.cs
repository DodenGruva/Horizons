namespace VintageHorizons.Render;

/// <summary>
/// Pure policy for removing only the vanilla render-distance veil and radial fade.
/// Kept apart from the Harmony adapter so the exact arithmetic and program allowlist
/// can be exercised without an OpenGL context.
/// </summary>
internal static class VanillaHorizonPolicy
{
    internal const string ViewDistanceUniform = "viewDistance";

    /// <summary>
    /// Vanilla's radial fades finish well before the configured draw edge. Four times
    /// the real distance also covers the half-diagonal of the outermost chunk at the
    /// smallest supported view distances, leaving the shader fully opaque until the
    /// engine's real culler takes ownership.
    /// </summary>
    internal const float FadeDistanceMultiplier = 4f;

    internal static bool UsesVanillaDistanceFade(string? passName) => passName switch
    {
        "chunkopaque" => true,
        "chunktopsoil" => true,
        "chunktransparent" => true,
        "chunkliquid" => true,
        "chunkliquiddepth" => true,
        "standard" => true,
        "instanced" => true,
        "entityanimated" => true,
        _ => false,
    };

    internal static float DistanceWithoutFade(float configuredDistance, float farTerrainDistance)
    {
        if (!float.IsFinite(configuredDistance) || configuredDistance <= 0f)
            return configuredDistance;

        float fadeFreeDistance = configuredDistance * FadeDistanceMultiplier;
        if (!float.IsFinite(fadeFreeDistance)) fadeFreeDistance = float.MaxValue;

        if (float.IsFinite(farTerrainDistance) && farTerrainDistance > fadeFreeDistance)
            fadeFreeDistance = farTerrainDistance;

        return fadeFreeDistance;
    }

    /// <summary>
    /// AmbientManager blends density modifiers as
    /// w^2 * modifier + (1-w)^2 * previous. This advances only the retained share of
    /// the vanilla base contribution; modifier values themselves remain untouched.
    /// </summary>
    internal static float RetainBaseFog(float retained, float modifierWeight)
        => retained * (1f - modifierWeight) * (1f - modifierWeight);

    /// <summary>
    /// Removes the propagated part of vanilla's default clear-air density. Any density
    /// above that default, including a deliberate base change by another system, remains.
    /// The already-blended upload carries weather, underwater, lava, server and altitude
    /// behavior, so subtracting the one known contribution preserves all of them.
    /// </summary>
    internal static float DensityWithoutVanillaBase(
        float uploadedDensity,
        float currentBaseDensity,
        float vanillaBaseDensity,
        float retainedBaseShare)
    {
        if (!float.IsFinite(uploadedDensity)
            || !float.IsFinite(currentBaseDensity)
            || !float.IsFinite(vanillaBaseDensity)
            || !float.IsFinite(retainedBaseShare))
        {
            return uploadedDensity;
        }

        float removableBase = MathF.Min(
            MathF.Max(0f, currentBaseDensity),
            MathF.Max(0f, vanillaBaseDensity));
        float contribution = removableBase * MathF.Max(0f, retainedBaseShare);
        return MathF.Max(0f, uploadedDensity - contribution);
    }

}
