namespace VintageHorizons.Render;

/// <summary>
/// Pure policy for moving only vanilla terrain's radial edge fade beyond the terrain
/// that its own range culler can submit. Kept apart from the Harmony adapter so the
/// exact arithmetic and program allowlist can be exercised without an OpenGL context.
/// </summary>
internal static class VanillaHorizonPolicy
{
    internal const string ViewDistanceUniform = "viewDistance";

    /// <summary>
    /// The official range culler admits terrain whose mesh-sphere centre is less than
    /// sqrt(viewDistance^2 + 400) blocks away. A terrain vertex may be half a 32-block
    /// chunk diagonal beyond that centre. The shader measures from the camera rather than
    /// the culler's floored player position, so the final constant also covers the official
    /// ten-block third-person maximum and sub-block horizontal flooring.
    /// </summary>
    internal const double RangePaddingSquared = 400d;
    internal const double HalfChunkDiagonal = 22.627416997969522d;
    internal const double CameraPositionSlack = 11.414213562373096d;

    /// <summary>
    /// Of the five official terrain programs, chunkliquid is the first whose distance
    /// expression can change a vertex: its 1.5 ceiling is retained through 72.5% of the
    /// uploaded distance. The other terrain fades begin at 75% or 80%.
    /// </summary>
    internal const double EarliestTerrainFadeRatio = 0.725d;

    internal static bool UsesVanillaDistanceFade(string? passName) => passName switch
    {
        "chunkopaque" => true,
        "chunktopsoil" => true,
        "chunktransparent" => true,
        "chunkliquid" => true,
        "chunkliquiddepth" => true,
        _ => false,
    };

    /// <summary>
    /// Returns the smallest whole-block shader distance whose earliest terrain fade lies
    /// strictly outside every vertex that the official range culler can submit.
    /// Invalid or unrepresentable inputs are returned unchanged so vanilla behavior wins.
    /// </summary>
    internal static float DistanceBeyondDrawableTerrain(float configuredDistance)
    {
        TryDistanceBeyondDrawableTerrain(configuredDistance, out float replacement);
        return replacement;
    }

    internal static bool TryDistanceBeyondDrawableTerrain(
        float configuredDistance, out float replacement)
    {
        replacement = configuredDistance;
        if (!float.IsFinite(configuredDistance) || configuredDistance <= 0f)
            return false;

        double centreBoundary = Math.Sqrt(
            (double)configuredDistance * configuredDistance + RangePaddingSquared);
        double lastDrawableVertex =
            centreBoundary + HalfChunkDiagonal + CameraPositionSlack;
        double calculated = Math.Ceiling(lastDrawableVertex / EarliestTerrainFadeRatio);

        if (!double.IsFinite(calculated) || calculated > float.MaxValue)
            return false;

        // Ceil is normally strictly outside. Preserve that property if the exact quotient
        // happens to be an integer or float conversion rounds back onto the boundary.
        if ((float)calculated * EarliestTerrainFadeRatio <= lastDrawableVertex)
            calculated++;

        float result = (float)calculated;
        if (!float.IsFinite(result)
            || result * EarliestTerrainFadeRatio <= lastDrawableVertex)
        {
            return false;
        }

        replacement = result;
        return true;
    }
}
