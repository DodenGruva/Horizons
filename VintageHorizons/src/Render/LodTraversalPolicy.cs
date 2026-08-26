namespace VintageHorizons;

/// <summary>
/// Pure spatial policy shared by visibility-aware traversal and mesh residency.
/// Visibility is evaluated every frame; residency deliberately ignores the frustum and
/// keeps the same one-detail-level grace band as CPU section eviction.
/// </summary>
internal static class LodTraversalPolicy
{
    /// <param name="subtree">
    /// Aggregate vertical extent of the meshes resident under this node, when it is known.
    /// Unknown - the default - keeps the bedrock-to-sky box, so anything that cannot supply
    /// an aggregate can only ever traverse too much. See <see cref="LodSubtreeHeights"/>.
    /// </param>
    public static bool NodeInView(LodFrustum frustum, long key,
        double cameraX, double cameraY, double cameraZ, int worldHeight,
        LodHeightSpan subtree = default)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        double minX = LodWorld.KeySx(key) * (double)footprint - cameraX;
        double minZ = LodWorld.KeySz(key) * (double)footprint - cameraZ;
        double minY = subtree.HasGeometry ? subtree.MinY - cameraY : -cameraY;
        double maxY = subtree.HasGeometry ? subtree.MaxY - cameraY : worldHeight - cameraY;

        return frustum.BoxInView(
            minX, minY, minZ,
            minX + footprint, maxY, minZ + footprint);
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
