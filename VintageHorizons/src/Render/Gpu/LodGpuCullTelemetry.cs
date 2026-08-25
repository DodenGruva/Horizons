namespace VintageHorizons;

/// <summary>Which depth picture and command bucket a live compute dispatch used.</summary>
internal enum LodGpuCullBucket
{
    General,
    SplitNear,
    SplitFar,
}

/// <summary>A stable draw-command identity, independent of its per-frame command slot.</summary>
internal readonly record struct LodGpuCullIdentity(long SectionKey, int ClusterCell);

/// <summary>
/// Shared word layout for the tiny GPU counter buffer written beside real command culling.
/// It is deliberately separate from the shadow classifier: these counters describe the
/// exact command slots the following multi-draw consumes.
/// </summary>
internal static class LodGpuCullTelemetryLayout
{
    public const int TestedCommands = 0;
    public const int CulledCommands = 1;
    public const int TestedIndices = 2;
    public const int CulledIndices = 3;
    public const int VisibleVerdicts = 4;
    public const int OccludedVerdicts = 5;
    public const int FailedOpenVerdicts = 6;
    public const int BackgroundVerdicts = 7;
    public const int NearPlaneVerdicts = 8;
    public const int OffScreenVerdicts = 9;
    public const int DegenerateVerdicts = 10;
    public const int BandBase = 11;
    public const int BandStride = 6;
    public const int BandCount = 6;
    public const int WordCount = BandBase + BandStride * BandCount;

    public static readonly string[] BandNames =
        ["0-1k", "1-2k", "2-4k", "4-8k", "8-16k", "16k+"];

    public static int BandFor(float distanceBlocks) => distanceBlocks switch
    {
        < 1000f => 0,
        < 2000f => 1,
        < 4000f => 2,
        < 8000f => 3,
        < 16000f => 4,
        _ => 5,
    };
}

/// <summary>CPU accumulation of asynchronously read live-cull counter samples.</summary>
internal sealed class LodGpuCullStatistics
{
    readonly ulong[] bandTestedCommands = new ulong[LodGpuCullTelemetryLayout.BandCount];
    readonly ulong[] bandCulledCommands = new ulong[LodGpuCullTelemetryLayout.BandCount];
    readonly ulong[] bandTestedIndices = new ulong[LodGpuCullTelemetryLayout.BandCount];
    readonly ulong[] bandCulledIndices = new ulong[LodGpuCullTelemetryLayout.BandCount];
    readonly ulong[] bandBackground = new ulong[LodGpuCullTelemetryLayout.BandCount];
    readonly ulong[] bandUndecided = new ulong[LodGpuCullTelemetryLayout.BandCount];

    public long Samples { get; private set; }
    public ulong TestedCommands { get; private set; }
    public ulong CulledCommands { get; private set; }
    public ulong TestedIndices { get; private set; }
    public ulong CulledIndices { get; private set; }
    public ulong VisibleVerdicts { get; private set; }
    public ulong OccludedVerdicts { get; private set; }
    public ulong FailedOpenVerdicts { get; private set; }
    public ulong BackgroundVerdicts { get; private set; }
    public ulong NearPlaneVerdicts { get; private set; }
    public ulong OffScreenVerdicts { get; private set; }
    public ulong DegenerateVerdicts { get; private set; }


    public void Add(ReadOnlySpan<uint> words)
    {
        if (words.Length < LodGpuCullTelemetryLayout.WordCount)
            throw new ArgumentException("short live-cull telemetry sample", nameof(words));

        Samples++;
        TestedCommands += words[LodGpuCullTelemetryLayout.TestedCommands];
        CulledCommands += words[LodGpuCullTelemetryLayout.CulledCommands];
        TestedIndices += words[LodGpuCullTelemetryLayout.TestedIndices];
        CulledIndices += words[LodGpuCullTelemetryLayout.CulledIndices];
        VisibleVerdicts += words[LodGpuCullTelemetryLayout.VisibleVerdicts];
        OccludedVerdicts += words[LodGpuCullTelemetryLayout.OccludedVerdicts];
        FailedOpenVerdicts += words[LodGpuCullTelemetryLayout.FailedOpenVerdicts];
        BackgroundVerdicts += words[LodGpuCullTelemetryLayout.BackgroundVerdicts];
        NearPlaneVerdicts += words[LodGpuCullTelemetryLayout.NearPlaneVerdicts];
        OffScreenVerdicts += words[LodGpuCullTelemetryLayout.OffScreenVerdicts];
        DegenerateVerdicts += words[LodGpuCullTelemetryLayout.DegenerateVerdicts];

        for (int band = 0; band < LodGpuCullTelemetryLayout.BandCount; band++)
        {
            int word = LodGpuCullTelemetryLayout.BandBase
                + band * LodGpuCullTelemetryLayout.BandStride;
            bandTestedCommands[band] += words[word];
            bandCulledCommands[band] += words[word + 1];
            bandTestedIndices[band] += words[word + 2];
            bandCulledIndices[band] += words[word + 3];
            bandBackground[band] += words[word + 4];
            bandUndecided[band] += words[word + 5];
        }
    }

    public void Reset()
    {
        Samples = 0;
        TestedCommands = 0;
        CulledCommands = 0;
        TestedIndices = 0;
        CulledIndices = 0;
        VisibleVerdicts = 0;
        OccludedVerdicts = 0;
        FailedOpenVerdicts = 0;
        BackgroundVerdicts = 0;
        NearPlaneVerdicts = 0;
        OffScreenVerdicts = 0;
        DegenerateVerdicts = 0;
        Array.Clear(bandTestedCommands);
        Array.Clear(bandCulledCommands);
        Array.Clear(bandTestedIndices);
        Array.Clear(bandCulledIndices);
        Array.Clear(bandBackground);
        Array.Clear(bandUndecided);
    }

    public string Describe(string label)
    {
        if (Samples == 0) return label + ": awaiting an asynchronous live-command sample";

        var report = new System.Text.StringBuilder();
        report.Append(label).Append(": ").Append(Samples).Append(" samples, ");
        report.Append(CulledCommands).Append('/').Append(TestedCommands)
            .Append(" commands zeroed (").Append(Percent(CulledCommands, TestedCommands)).Append("), ");
        report.Append(CulledIndices).Append('/').Append(TestedIndices)
            .Append(" triangle indices removed (").Append(Percent(CulledIndices, TestedIndices)).Append(')');
        report.Append("; verdicts background ").Append(Percent(BackgroundVerdicts, TestedCommands));
        ulong undecided = FailedOpenVerdicts + NearPlaneVerdicts
            + OffScreenVerdicts + DegenerateVerdicts;
        report.Append(", undecided ").Append(Percent(undecided, TestedCommands));

        var bands = new List<string>();
        for (int band = 0; band < LodGpuCullTelemetryLayout.BandCount; band++)
        {
            if (bandTestedCommands[band] == 0) continue;
            bands.Add(LodGpuCullTelemetryLayout.BandNames[band] + " "
                + Percent(bandCulledCommands[band], bandTestedCommands[band]) + " commands/"
                + Percent(bandCulledIndices[band], bandTestedIndices[band]) + " indices, "
                + Percent(bandBackground[band], bandTestedCommands[band]) + " background, "
                + Percent(bandUndecided[band], bandTestedCommands[band]) + " undecided");
        }
        if (bands.Count > 0) report.Append("; by distance: ").Append(string.Join(", ", bands));
        return report.ToString();
    }

    static string Percent(ulong part, ulong total) =>
        total == 0 ? "0.0%" : (100.0 * part / total).ToString(
            "0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";
}
