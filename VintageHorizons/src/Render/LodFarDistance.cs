namespace VintageHorizons;

/// <summary>
/// World-space horizontal bounds of every section that currently owns an opaque or
/// water mesh. Adding a mesh expands the bounds in constant time. Removing an extreme
/// marks them for a rare rebuild from the remaining mesh keys; ordinary frames never
/// enumerate the mesh dictionaries.
/// </summary>
internal sealed class LodMeshBounds
{
    double minX, maxX, minZ, maxZ;

    public bool HasBounds { get; private set; }
    public bool Dirty { get; private set; }

    public void Include(long key)
    {
        GetFootprint(key, out double keyMinX, out double keyMaxX, out double keyMinZ, out double keyMaxZ);

        if (!HasBounds)
        {
            minX = keyMinX;
            maxX = keyMaxX;
            minZ = keyMinZ;
            maxZ = keyMaxZ;
            HasBounds = true;
            return;
        }

        minX = Math.Min(minX, keyMinX);
        maxX = Math.Max(maxX, keyMaxX);
        minZ = Math.Min(minZ, keyMinZ);
        maxZ = Math.Max(maxZ, keyMaxZ);
    }

    /// <summary>
    /// An interior removal cannot change the outer bounds. An extreme removal may or
    /// may not (another section can share that edge), so defer one exact rebuild until
    /// the distance is next requested.
    /// </summary>
    public void Remove(long key)
    {
        if (!HasBounds || Dirty) return;

        GetFootprint(key, out double keyMinX, out double keyMaxX, out double keyMinZ, out double keyMaxZ);
        if (keyMinX == minX || keyMaxX == maxX || keyMinZ == minZ || keyMaxZ == maxZ)
        {
            Dirty = true;
        }
    }

    public void Rebuild(IEnumerable<long> opaqueKeys, IEnumerable<long> waterKeys)
    {
        Clear();
        foreach (long key in opaqueKeys) Include(key);
        foreach (long key in waterKeys) Include(key);
        Dirty = false;
    }

    public double FarthestDistanceTo(double x, double z)
    {
        if (!HasBounds) return 0;

        double dx = Math.Max(Math.Abs(x - minX), Math.Abs(x - maxX));
        double dz = Math.Max(Math.Abs(z - minZ), Math.Abs(z - maxZ));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    public void Clear()
    {
        minX = maxX = minZ = maxZ = 0;
        HasBounds = false;
        Dirty = false;
    }

    static void GetFootprint(long key, out double minX, out double maxX, out double minZ, out double maxZ)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        minX = LodWorld.KeySx(key) * (double)footprint;
        minZ = LodWorld.KeySz(key) * (double)footprint;
        maxX = minX + footprint;
        maxZ = minZ + footprint;
    }
}

/// <summary>Pure far-edge arithmetic shared by the renderer and isolated checks.</summary>
internal static class LodFarDistance
{
    public const float VanillaMargin = 1536;
    public const float ProjectionMargin = 512;
    public const float MinimumProjectionDistance = 3000;

    public static float Effective(double farthestMeshDistance, float vanillaViewDistance, int farViewDistanceCap)
    {
        float far = (float)farthestMeshDistance;
        if (farViewDistanceCap > 0) far = Math.Min(far, farViewDistanceCap);
        return Math.Max(far, vanillaViewDistance + VanillaMargin);
    }

    public static float RequiredProjection(float effectiveFarDistance) =>
        Math.Max(MinimumProjectionDistance, effectiveFarDistance + ProjectionMargin);
}

/// <summary>
/// Conservative inner handoff from cached terrain to vanilla chunks. The old boundary
/// sat at 78.5% of configured view distance and could outrun streaming; removing it
/// entirely made approximate cached surfaces compete with ready vanilla geometry. Keep
/// at least 192 blocks of overlap and never hand off outside half the configured radius.
/// </summary>
internal static class LodNearHandoff
{
    public const float MinimumFallbackOverlap = 192;
    public const float MaximumInnerFraction = 0.5f;

    public static float InnerDiscardRadius(float vanillaViewDistance)
    {
        float distance = Math.Max(0, vanillaViewDistance);
        return Math.Max(0, Math.Min(
            distance * MaximumInnerFraction,
            distance - MinimumFallbackOverlap));
    }
}

internal readonly record struct LodFarPlaneUpdate(float Distance, bool Changed);

/// <summary>
/// Stabilizes the expensive game-camera projection reset. Required growth is applied
/// immediately. Shrinkage must remain in the same lower 512-block band for five seconds,
/// preventing ordinary camera movement from bouncing the projection between two values.
/// </summary>
internal sealed class LodFarPlaneState
{
    public const float Step = 512;
    public const long ShrinkCooldownMilliseconds = 5000;

    float appliedDistance;
    float shrinkCandidate;
    long shrinkCandidateSince;
    bool hasShrinkCandidate;

    public LodFarPlaneUpdate Update(float requiredDistance, long nowMilliseconds)
    {
        float target = QuantizeUp(requiredDistance);

        if (appliedDistance == 0 || target > appliedDistance)
        {
            appliedDistance = target;
            hasShrinkCandidate = false;
            return new LodFarPlaneUpdate(appliedDistance, true);
        }

        if (target == appliedDistance)
        {
            hasShrinkCandidate = false;
            return new LodFarPlaneUpdate(appliedDistance, false);
        }

        if (!hasShrinkCandidate || shrinkCandidate != target || nowMilliseconds < shrinkCandidateSince)
        {
            shrinkCandidate = target;
            shrinkCandidateSince = nowMilliseconds;
            hasShrinkCandidate = true;
            return new LodFarPlaneUpdate(appliedDistance, false);
        }

        if (nowMilliseconds - shrinkCandidateSince < ShrinkCooldownMilliseconds)
        {
            return new LodFarPlaneUpdate(appliedDistance, false);
        }

        appliedDistance = target;
        hasShrinkCandidate = false;
        return new LodFarPlaneUpdate(appliedDistance, true);
    }

    public void Reset()
    {
        appliedDistance = 0;
        shrinkCandidate = 0;
        shrinkCandidateSince = 0;
        hasShrinkCandidate = false;
    }

    static float QuantizeUp(float distance) => MathF.Ceiling(distance / Step) * Step;
}
