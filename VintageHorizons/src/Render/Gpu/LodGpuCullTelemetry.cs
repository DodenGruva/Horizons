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

/// <summary>Per-command diagnostic words written only while `.vhflicker` is armed.</summary>
internal static class LodGpuFlickerLayout
{
    public const int Verdict = 0;
    public const int NearestDepth = 1;
    public const int FarthestDepth = 2;
    public const int MipLevel = 3;
    public const int X0 = 4;
    public const int Y0 = 5;
    public const int X1 = 6;
    public const int Y1 = 7;
    public const int WordCount = 8;
    public const uint DisabledVerdict = uint.MaxValue;
}

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

/// <summary>
/// Follows stable cluster identities through successive asynchronous split-far samples and
/// ranks the commands whose actual cull verdict changes. This is deliberately pure CPU code:
/// the GL pass merely supplies raw facts, while checks can pin the transition accounting and
/// the human-readable report without needing a graphics context.
/// </summary>
internal sealed class LodGpuFlickerCapture
{
    sealed class Track
    {
        public bool Seen;
        public bool LastPresent;
        public int LastSequence;
        public int SeenSequence;
        public uint LastVerdict = LodGpuFlickerLayout.DisabledVerdict;
        public int Sightings;
        public int PresenceTransitions;
        public int Transitions;
        public int OccludedBackground;
        public int OccludedVisible;
        public int DrawSafe;
        public float Nearest;
        public float Farthest;
        public int Mip;
        public int X0;
        public int Y0;
        public int X1;
        public int Y1;
        public int ScreenWidth;
        public int ScreenHeight;
    }

    readonly Dictionary<LodGpuCullIdentity, Track> tracks = new();
    float[]? lastMatrix;

    public int Samples { get; private set; }
    public long CommandsObserved { get; private set; }
    public int DroppedSamples { get; private set; }
    public float MaximumMatrixDelta { get; private set; }

    public void Reset()
    {
        tracks.Clear();
        lastMatrix = null;
        Samples = 0;
        CommandsObserved = 0;
        DroppedSamples = 0;
        MaximumMatrixDelta = 0f;
    }

    public void AddDroppedSample() => DroppedSamples++;

    public void Add(
        int sequence,
        ReadOnlySpan<LodGpuCullIdentity> identities,
        ReadOnlySpan<uint> words,
        ReadOnlySpan<float> viewProjection,
        int screenWidth,
        int screenHeight)
    {
        if (words.Length < identities.Length * LodGpuFlickerLayout.WordCount)
            throw new ArgumentException("short flicker result sample", nameof(words));

        if (lastMatrix != null && viewProjection.Length >= 16)
        {
            for (int i = 0; i < 16; i++)
                MaximumMatrixDelta = Math.Max(MaximumMatrixDelta,
                    Math.Abs(viewProjection[i] - lastMatrix[i]));
        }
        if (viewProjection.Length >= 16)
        {
            lastMatrix ??= new float[16];
            viewProjection[..16].CopyTo(lastMatrix);
        }

        Samples++;
        for (int command = 0; command < identities.Length; command++)
        {
            int word = command * LodGpuFlickerLayout.WordCount;
            uint verdict = words[word + LodGpuFlickerLayout.Verdict];
            if (verdict == LodGpuFlickerLayout.DisabledVerdict) continue;

            CommandsObserved++;
            LodGpuCullIdentity identity = identities[command];
            if (!tracks.TryGetValue(identity, out Track? track))
            {
                track = new Track();
                tracks.Add(identity, track);
            }

            bool consecutive = track.Seen && track.LastSequence == sequence - 1;
            if (consecutive && !track.LastPresent)
                track.PresenceTransitions++;
            if (consecutive && track.LastPresent && track.LastVerdict != verdict)
            {
                track.Transitions++;
                if (Pair(track.LastVerdict, verdict, 1u, 3u))
                    track.OccludedBackground++;
                else if (Pair(track.LastVerdict, verdict, 1u, 0u))
                    track.OccludedVisible++;
                else
                    track.DrawSafe++;
            }

            track.Seen = true;
            track.LastPresent = true;
            track.LastSequence = sequence;
            track.SeenSequence = sequence;
            track.LastVerdict = verdict;
            track.Sightings++;
            track.Nearest = BitConverter.UInt32BitsToSingle(
                words[word + LodGpuFlickerLayout.NearestDepth]);
            track.Farthest = BitConverter.UInt32BitsToSingle(
                words[word + LodGpuFlickerLayout.FarthestDepth]);
            track.Mip = unchecked((int)words[word + LodGpuFlickerLayout.MipLevel]);
            track.X0 = unchecked((int)words[word + LodGpuFlickerLayout.X0]);
            track.Y0 = unchecked((int)words[word + LodGpuFlickerLayout.Y0]);
            track.X1 = unchecked((int)words[word + LodGpuFlickerLayout.X1]);
            track.Y1 = unchecked((int)words[word + LodGpuFlickerLayout.Y1]);
            track.ScreenWidth = screenWidth;
            track.ScreenHeight = screenHeight;
        }

        foreach (Track track in tracks.Values)
        {
            if (!track.Seen || track.SeenSequence == sequence) continue;
            if (track.LastSequence == sequence - 1 && track.LastPresent)
                track.PresenceTransitions++;
            track.LastPresent = false;
            track.LastSequence = sequence;
        }

    }

    public string Describe(bool armed, string label = "split far")
    {
        string state = armed ? "armed" : "stopped";
        var report = new System.Text.StringBuilder();
        report.Append("flicker capture ").Append(state).Append(": ")
            .Append(label).Append(' ').Append(Samples).Append(" samples, ")
            .Append(CommandsObserved).Append(" command observations, ")
            .Append(DroppedSamples).Append(" samples skipped because readback slots were busy, ")
            .Append("max view-projection change ")
            .Append(MaximumMatrixDelta.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture));

        int presenceTransitions = tracks.Sum(pair => pair.Value.PresenceTransitions);
        int totalTransitions = tracks.Sum(pair => pair.Value.Transitions);
        int backgroundTransitions = tracks.Sum(pair => pair.Value.OccludedBackground);
        int visibleTransitions = tracks.Sum(pair => pair.Value.OccludedVisible);
        int cullTransitions = backgroundTransitions + visibleTransitions;
        int drawSafeTransitions = totalTransitions - cullTransitions;
        int changedCommands = tracks.Count(pair =>
            pair.Value.PresenceTransitions + CullTransitions(pair.Value) > 0);
        report.Append("; ").Append(changedCommands).Append(" commands changed draw state: ")
            .Append(presenceTransitions).Append(" presence flips and ")
            .Append(cullTransitions).Append(" culling flips (occluded/background ")
            .Append(backgroundTransitions).Append(", occluded/visible ")
            .Append(visibleTransitions).Append("), plus ")
            .Append(drawSafeTransitions).Append(" draw-safe verdict flips");

        List<KeyValuePair<LodGpuCullIdentity, Track>> offenders = tracks
            .Where(pair => pair.Value.PresenceTransitions + CullTransitions(pair.Value) > 0)
            .OrderByDescending(pair => pair.Value.PresenceTransitions
                + CullTransitions(pair.Value))
            .ThenBy(pair => pair.Key.SectionKey)
            .ThenBy(pair => pair.Key.ClusterCell)
            .Take(12)
            .ToList();
        if (offenders.Count == 0)
        {
            report.Append("; no command changed draw state in consecutive captured samples");
            return report.ToString();
        }

        foreach ((LodGpuCullIdentity identity, Track track) in offenders)
        {
            report.Append(Environment.NewLine).Append("  ")
                .Append(DescribeIdentity(identity)).Append(": ")
                .Append(track.PresenceTransitions).Append(" presence flips, ")
                .Append(CullTransitions(track)).Append(" culling flips in ")
                .Append(track.Sightings).Append(" sightings (occluded/background ")
                .Append(track.OccludedBackground).Append(", occluded/visible ")
                .Append(track.OccludedVisible).Append(", draw-safe ").Append(track.DrawSafe)
                .Append("); last ").Append(track.LastPresent
                    ? VerdictName(track.LastVerdict)
                    : "absent")
                .Append(", nearest ").Append(Depth(track.Nearest))
                .Append(", HZB farthest ").Append(Depth(track.Farthest))
                .Append(", gap ").Append(Depth(track.Nearest - track.Farthest))
                .Append(", mip ").Append(track.Mip)
                .Append(", texels ").Append(track.X0).Append(',').Append(track.Y0)
                .Append('-').Append(track.X1).Append(',').Append(track.Y1)
                .Append(", screen ").Append(ScreenRegion(track));
        }
        return report.ToString();
    }

    static bool Pair(uint a, uint b, uint x, uint y) =>
        (a == x && b == y) || (a == y && b == x);

    static int CullTransitions(Track track) =>
        track.OccludedBackground + track.OccludedVisible;

    static string ScreenRegion(Track track)
    {
        if (track.Mip < 0 || track.ScreenWidth <= 0 || track.ScreenHeight <= 0)
            return "unknown";
        (int width, int height) = LodHzbReference.LevelSize(
            track.ScreenWidth, track.ScreenHeight, track.Mip);
        if (width <= 0 || height <= 0) return "unknown";
        double centreX = (track.X0 + track.X1 + 1.0) * 0.5 / width;
        double centreY = (track.Y0 + track.Y1 + 1.0) * 0.5 / height;
        string horizontal = centreX < 1.0 / 3.0 ? "left"
            : centreX > 2.0 / 3.0 ? "right" : "centre";
        string vertical = centreY < 1.0 / 3.0 ? "lower"
            : centreY > 2.0 / 3.0 ? "upper" : "middle";
        return horizontal + '-' + vertical;
    }


    static string DescribeIdentity(LodGpuCullIdentity identity)
    {
        string section = $"L{LodWorld.KeyLevel(identity.SectionKey)} "
            + $"({LodWorld.KeySx(identity.SectionKey)},{LodWorld.KeySz(identity.SectionKey)})";
        if (identity.ClusterCell < 0) return section + " whole section";
        int cx = identity.ClusterCell % LodPackedClusterBuilder.SubdivisionsPerAxis;
        int cz = identity.ClusterCell / LodPackedClusterBuilder.SubdivisionsPerAxis;
        return section + $" cluster {identity.ClusterCell} ({cx},{cz})";
    }

    static string VerdictName(uint verdict) => verdict switch
    {
        0u => "visible",
        1u => "occluded",
        2u => "failed-open",
        3u => "background",
        4u => "near-plane",
        5u => "off-screen",
        6u => "degenerate",
        _ => "disabled",
    };

    static string Depth(float value) => float.IsFinite(value)
        ? value.ToString("0.0000000", System.Globalization.CultureInfo.InvariantCulture)
        : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
