using System.Buffers.Binary;

namespace VintageHorizons;

/// <summary>The six outward face orientations emitted by <see cref="LodMesher"/>.</summary>
internal enum LodPackedFace : byte
{
    Bottom,
    Top,
    West,
    East,
    North,
    South,
}

/// <summary>
/// Exact twelve-byte representation of one axis-aligned greedy quad.
///
/// X/Z are section-column coordinates in [0, 64], so four endpoints need 28 bits.
/// Y uses quarter-block units because ordinary faces are integral and thin cover is the
/// sole quarter-block case; two endpoints need 32 bits and cover the complete 14-bit run
/// height range. Three face bits retain winding, and the final word is the unmodified
/// RGBA/tint payload. No established shader input is quantized or discarded.
/// </summary>
internal static class LodPackedQuadFormat
{
    public const int WordsPerQuad = 3;
    public const int StrideBytes = WordsPerQuad * sizeof(uint);
    /// <summary>Vertices in the established expanded two-triangle stream.</summary>
    public const int VerticesPerQuad = 6;
    /// <summary>Unique corners shaded by the indexed packed path.</summary>
    public const int PulledVerticesPerQuad = 4;
    public const int IndicesPerQuad = 6;
    public const int MaximumGridCoordinate = LodSection.GridSize;

    const int CoordinateBits = 7;
    const uint CoordinateMask = (1u << CoordinateBits) - 1u;
    const int X0Shift = 0;
    const int X1Shift = 7;
    const int Z0Shift = 14;
    const int Z1Shift = 21;
    const int FaceShift = 28;

    public static long Bytes(int quadCount) => Math.Max(0, (long)quadCount) * StrideBytes;

    public static long ExpandedBytes(int quadCount) => Math.Max(0, (long)quadCount) * 88L;

    public static void Append(
        List<uint> destination,
        LodPackedFace face,
        int x0,
        int x1,
        float y0,
        float y1,
        int z0,
        int z1,
        int color,
        byte alpha)
    {
        Span<uint> words = stackalloc uint[WordsPerQuad];
        Encode(words, face, x0, x1, y0, y1, z0, z1, color, alpha);
        destination.Add(words[0]);
        destination.Add(words[1]);
        destination.Add(words[2]);
    }

    public static void Encode(
        Span<uint> destination,
        LodPackedFace face,
        int x0,
        int x1,
        float y0,
        float y1,
        int z0,
        int z1,
        int color,
        byte alpha)
    {
        if (destination.Length < WordsPerQuad)
            throw new ArgumentException("short packed-quad destination", nameof(destination));
        ValidateRange(x0, x1, nameof(x0));
        ValidateRange(z0, z1, nameof(z0));
        if ((uint)face > (uint)LodPackedFace.South)
            throw new ArgumentOutOfRangeException(nameof(face));

        ushort qy0 = Quarter(y0, nameof(y0));
        ushort qy1 = Quarter(y1, nameof(y1));
        if (qy1 < qy0) throw new ArgumentException("packed quad has an inverted Y range");

        destination[0] = (uint)x0 << X0Shift
            | (uint)x1 << X1Shift
            | (uint)z0 << Z0Shift
            | (uint)z1 << Z1Shift
            | (uint)face << FaceShift;
        destination[1] = qy0 | (uint)qy1 << 16;
        destination[2] = (uint)(color & 0x00FF_FFFF) | (uint)alpha << 24;
    }

    /// <summary>Writes packed words in the byte order GL and the deterministic checks share.</summary>
    public static void EncodeBytes(
        uint[] words, int quadCount, Span<byte> destination)
    {
        int wordCount = checked(quadCount * WordsPerQuad);
        if (quadCount < 0) throw new ArgumentOutOfRangeException(nameof(quadCount));
        if (words.Length < wordCount) throw new ArgumentException("short packed-quad array", nameof(words));
        if (destination.Length < wordCount * sizeof(uint))
            throw new ArgumentException("short packed-quad byte destination", nameof(destination));

        for (int i = 0; i < wordCount; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(destination[(i * sizeof(uint))..], words[i]);
    }

    public static void DecodeVertex(
        ReadOnlySpan<uint> source,
        int quad,
        int emittedVertex,
        int columnBlocks,
        out float x,
        out float y,
        out float z,
        out uint color)
    {
        if (quad < 0 || quad >= source.Length / WordsPerQuad)
            throw new ArgumentOutOfRangeException(nameof(quad));
        if ((uint)emittedVertex >= VerticesPerQuad)
            throw new ArgumentOutOfRangeException(nameof(emittedVertex));
        if (columnBlocks <= 0) throw new ArgumentOutOfRangeException(nameof(columnBlocks));

        int offset = quad * WordsPerQuad;
        uint geometry = source[offset];
        uint heights = source[offset + 1];
        color = source[offset + 2];

        float x0 = Field(geometry, X0Shift) * columnBlocks;
        float x1 = Field(geometry, X1Shift) * columnBlocks;
        float z0 = Field(geometry, Z0Shift) * columnBlocks;
        float z1 = Field(geometry, Z1Shift) * columnBlocks;
        float y0 = (heights & 0xFFFFu) * 0.25f;
        float y1 = (heights >> 16) * 0.25f;
        LodPackedFace face = (LodPackedFace)((geometry >> FaceShift) & 7u);
        int corner = emittedVertex switch { 0 => 0, 1 => 1, 2 => 2, 3 => 0, 4 => 2, _ => 3 };

        (x, y, z) = face switch
        {
            LodPackedFace.Bottom => corner switch
            {
                0 => (x0, y0, z0), 1 => (x1, y0, z0),
                2 => (x1, y0, z1), _ => (x0, y0, z1),
            },
            LodPackedFace.Top => corner switch
            {
                0 => (x0, y0, z0), 1 => (x0, y0, z1),
                2 => (x1, y0, z1), _ => (x1, y0, z0),
            },
            LodPackedFace.West => corner switch
            {
                0 => (x0, y0, z0), 1 => (x0, y0, z1),
                2 => (x0, y1, z1), _ => (x0, y1, z0),
            },
            LodPackedFace.East => corner switch
            {
                0 => (x1, y0, z0), 1 => (x1, y1, z0),
                2 => (x1, y1, z1), _ => (x1, y0, z1),
            },
            LodPackedFace.North => corner switch
            {
                0 => (x0, y0, z0), 1 => (x0, y1, z0),
                2 => (x1, y1, z0), _ => (x1, y0, z0),
            },
            LodPackedFace.South => corner switch
            {
                0 => (x0, y0, z1), 1 => (x1, y0, z1),
                2 => (x1, y1, z1), _ => (x0, y1, z1),
            },
            _ => throw new InvalidOperationException("packed quad has an unknown face"),
        };
    }

    public static LodPackedFace FaceOf(ReadOnlySpan<uint> source, int quad) =>
        (LodPackedFace)((source[quad * WordsPerQuad] >> FaceShift) & 7u);

    static int Field(uint word, int shift) => (int)((word >> shift) & CoordinateMask);

    static void ValidateRange(int min, int max, string name)
    {
        if (min < 0 || max < min || max > MaximumGridCoordinate)
            throw new ArgumentOutOfRangeException(name, $"packed coordinate range {min}..{max}");
    }

    static ushort Quarter(float value, string name)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(name);
        float scaled = value * 4f;
        int rounded = (int)MathF.Round(scaled);
        if (rounded < 0 || rounded > ushort.MaxValue || MathF.Abs(scaled - rounded) > 0.0001f)
            throw new ArgumentOutOfRangeException(name, "packed height must be an exact quarter block");
        return (ushort)rounded;
    }
}
