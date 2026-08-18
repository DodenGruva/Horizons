namespace VintageHorizons;

/// <summary>
/// Pure spatial policy shared by visibility-aware traversal and mesh residency.
/// Visibility is evaluated every frame; residency deliberately ignores the frustum and
/// keeps the same one-detail-level grace band as CPU section eviction.
/// </summary>
internal static class LodTraversalPolicy
{
    public static bool NodeInView(LodFrustum frustum, long key,
        double cameraX, double cameraY, double cameraZ, int worldHeight)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        double minX = LodWorld.KeySx(key) * (double)footprint - cameraX;
        double minZ = LodWorld.KeySz(key) * (double)footprint - cameraZ;

        return frustum.BoxInView(
            minX, -cameraY, minZ,
            minX + footprint, worldHeight - cameraY, minZ + footprint);
    }

    /// <summary>
    /// Keep meshes whose area currently wants this level or at most one level coarser.
    /// This is the same distance band used by LodWorld.EvictColdSections, so visibility
    /// never decides whether turning around has a resident mesh to reuse.
    /// </summary>
    public static bool WithinResidencyBand(long key, double cameraX, double cameraZ)
    {
        int level = LodWorld.KeyLevel(key);
        int wanted = LodWorld.WantedLevelForSq(LodWorld.NearestDistanceSqTo(key, cameraX, cameraZ));
        return wanted < level + 2;
    }
}
