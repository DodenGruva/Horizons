namespace VintageHorizons;

/// <summary>
/// The vertical extent of the geometry a mesh pass actually emitted, in world blocks.
///
/// Sections span the whole world vertically by construction, so until this existed the
/// renderer could only bound one with a bedrock-to-sky box. The side planes make that
/// harmless for frustum rejection, but it is close to fatal for any depth test: asking
/// whether a full-height column is entirely behind a ridge almost always answers no,
/// however low the terrain inside it sits.
///
/// It describes what is DRAWN, not what is stored - a run of blocks with no exposed face
/// emits nothing and contributes no span. A section that emitted nothing has no span at
/// all, which is distinct from a span of zero height: a flat plain is a real, legitimately
/// zero-height span at its own surface Y. That distinction is why the flag is stored
/// rather than inferred from the numbers, and it makes <c>default</c> mean "unknown", so
/// anything that fails to supply bounds falls back to the full-height box instead of
/// culling terrain at y=0.
/// </summary>
public readonly record struct LodHeightSpan(float MinY, float MaxY, bool HasGeometry)
{
    /// <summary>No geometry, and therefore nothing to bound. Also the default value.</summary>
    public static LodHeightSpan Empty => default;

    public static LodHeightSpan Of(float minY, float maxY) =>
        minY <= maxY ? new LodHeightSpan(minY, maxY, true) : Empty;

    /// <summary>Blocks from the lowest emitted vertex to the highest; zero when empty.</summary>
    public float Height => HasGeometry ? MaxY - MinY : 0f;

    /// <summary>
    /// The span covering both, used where one box has to hold two passes. An empty side
    /// contributes nothing rather than dragging the result to zero.
    /// </summary>
    public LodHeightSpan Union(LodHeightSpan other)
    {
        if (!HasGeometry) return other;
        if (!other.HasGeometry) return this;
        return new LodHeightSpan(
            Math.Min(MinY, other.MinY), Math.Max(MaxY, other.MaxY), true);
    }
}

/// <summary>
/// One section's spans, kept per pass because opaque and water are submitted separately
/// and a shoreline's water surface sits nowhere near the seabed below it.
/// </summary>
public readonly record struct LodSectionHeights(LodHeightSpan Opaque, LodHeightSpan Water)
{
    public LodHeightSpan Either => Opaque.Union(Water);
    public bool HasGeometry => Opaque.HasGeometry || Water.HasGeometry;
}

/// <summary>
/// The measured distribution of section heights, accumulated as meshes are published and
/// reported once per interval.
///
/// This exists to settle an argument rather than to watch a system: Phase 4 of the GPU
/// plan expects depth rejection to pay for a pyramid, and that expectation is worthless
/// without knowing how much of the world height an ordinary section actually occupies. If
/// the answer turns out to be "most of it", the saving needs revisiting BEFORE the pyramid
/// is built, not after it reports a disappointing number.
///
/// Buckets are absolute blocks rather than a fraction of world height, because the height
/// of the world is not known on the thread that publishes a mesh, and a fraction computed
/// later from a wrong denominator would be worse than no fraction at all.
/// </summary>
public sealed class LodSectionHeightStats
{
    static readonly float[] BucketCeilings = { 4f, 16f, 64f, 256f, float.PositiveInfinity };

    // Ranges, not "<=4". This string reaches the chat window through a command result, and
    // the game parses chat text as VTML: a leading "<" opens a tag, so "<=4:" was read as
    // an unclosed element and the whole reply was refused with a parse error. Nothing in a
    // player-visible string may start a "<".
    static readonly string[] BucketNames = { "0-4", "4-16", "16-64", "64-256", "256+" };

    readonly long[] buckets = new long[BucketCeilings.Length];

    public long Samples { get; private set; }
    public double SumBlocks { get; private set; }
    public float MaxBlocks { get; private set; }
    public double MeanBlocks => Samples > 0 ? SumBlocks / Samples : 0.0;

    // The height alone cannot distinguish "the terrain here really is 146 blocks of
    // mountain" from "the surface is at 150 and something drags the bound to bedrock".
    // Those want opposite responses, so the floor and the ceiling are reported apart.
    public double SumFloor { get; private set; }
    public double SumCeiling { get; private set; }
    public double MeanFloor => Samples > 0 ? SumFloor / Samples : 0.0;
    public double MeanCeiling => Samples > 0 ? SumCeiling / Samples : 0.0;

    public void Add(LodHeightSpan span)
    {
        if (!span.HasGeometry) return;
        float height = span.Height;
        Samples++;
        SumBlocks += height;
        SumFloor += span.MinY;
        SumCeiling += span.MaxY;
        if (height > MaxBlocks) MaxBlocks = height;
        for (int i = 0; i < BucketCeilings.Length; i++)
        {
            if (height <= BucketCeilings[i]) { buckets[i]++; break; }
        }
    }

    public void Reset()
    {
        Array.Clear(buckets);
        Samples = 0;
        SumBlocks = 0.0;
        SumFloor = 0.0;
        SumCeiling = 0.0;
        MaxBlocks = 0f;
    }

    /// <summary>One line for the periodic report; states the world height it is judged against.</summary>
    public string Describe(int worldHeight)
    {
        if (Samples == 0) return "no meshes published this interval";

        var parts = new string[buckets.Length];
        for (int i = 0; i < buckets.Length; i++)
        {
            parts[i] = $"{BucketNames[i]}: {buckets[i] * 100.0 / Samples:0}%";
        }

        double meanShare = worldHeight > 0 ? MeanBlocks * 100.0 / worldHeight : 0.0;
        return $"{Samples} meshes, mean {MeanBlocks:0.0} blocks tall "
            + $"({meanShare:0.0}% of the {worldHeight}-block world), max {MaxBlocks:0} | "
            + $"mean floor y={MeanFloor:0.0}, mean ceiling y={MeanCeiling:0.0} | "
            + string.Join(", ", parts);
    }
}
