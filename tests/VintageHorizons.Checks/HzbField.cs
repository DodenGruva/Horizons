using System.Globalization;
using Microsoft.Data.Sqlite;

namespace VintageHorizons.Checks;

/// <summary>
/// The sky-problem lever comparison, measured against a real cache with no game process.
///
/// WHY THIS EXISTS. Phase 4 draws nothing, so its only output is a number, and every number
/// it produced so far arrived through a playtest. Three consecutive playtests measured broken
/// instruments instead of terrain, and the query cross-check that was meant to gate the phase
/// has a noise floor above zero by construction (G73). Nothing about the question actually
/// needs a GPU: the pyramid reduction, the projection, the box test, the mesher and the cache
/// decoder all run on the CPU already. This assembles them into the same measurement,
/// deterministically, so a wrong run costs a re-run rather than a session.
///
/// WHAT IT MODELS. At the moment the pyramid is built the depth buffer holds VANILLA terrain
/// only - the renderer builds it at render order 0.38, after vanilla has drawn and before any
/// cached section is submitted. So the occluder is whatever vanilla draws inside its own view
/// distance, and the population is the cached sections beyond it. That is reproduced here by
/// rasterising the cached sections inside an occluder radius and testing the ones outside it,
/// which also makes the radius a knob: sweeping it answers Phase 6's question - what a second
/// depth opportunity would buy - with the same code and no self-occlusion, because a section
/// is never in both sets.
///
/// --occlude-all models Phase 6's OTHER candidate instead: a pyramid reduced from the
/// previous frame's finished depth, which holds vanilla plus every cached section that
/// actually drew. There the two sets are the same set, and a section being tested against a
/// buffer containing itself is fine rather than a flaw - the pyramid reduces with max, so the
/// farthest depth over a section's own footprint is its own far side, which is never nearer
/// than its box's near side, so it can never hide itself. What this DOES assume is a still
/// camera: the real thing tests this frame's boxes against last frame's view, and the cost of
/// that mismatch is a fail-open guard this harness does not model. Read it as the best case.
///
/// WHAT IT APPROXIMATES, and these belong in any report of its numbers:
///   - The occluder is cached terrain standing in for vanilla's chunks. Inside a short vanilla
///     view distance those are L0 sections, which are one column per block and therefore full
///     horizontal resolution, so the silhouette is close rather than coarse; what it misses is
///     whatever LodBlockPolicy declines to capture. A section is also rasterised whole once
///     any part of it falls inside the radius, so the occluder reaches somewhat past it.
///     Absolute hidden percentages are approximate for those two reasons. The comparison
///     BETWEEN levers is not, because both levers see the same occluder.
///   - Every section that exists in the cache is loaded, so no section is walled to bedrock by
///     a missing neighbour. This is the fully-loaded best case for box heights.
///   - Depth is one float per pixel through the same projection the renderer uses, so the
///     box-versus-scene inequality is the renderer's own. Absolute depth values are not the
///     engine's: the far plane here is set from the measured distance rather than by the game.
/// </summary>
public static class HzbField
{
    // 2560x1440 gives twelve pyramid levels, which is what the 0.3.65 run reported, so this
    // is the owner's screen rather than a round number.
    const int DefaultWidth = 2560;
    const int DefaultHeight = 1440;

    // Reported over the same bands the in-game classifier uses, so a figure from here and a
    // figure from a log line are answers to the same question.
    static readonly double[] BandCeilings = { 1000, 2000, 4000, 8000, 16000, double.PositiveInfinity };
    static readonly string[] BandNames = { "0-1k", "1-2k", "2-4k", "4-8k", "8-16k", "16k+" };

    // The wider-sampling lever, as a curve rather than a single alternative. Two is what the
    // test does today; the rest are what it would do if the level were chosen finer.
    static readonly int[] WideTexels = { 4, 8, 16 };

    /// <summary>
    /// The wide setting the "does subdivision still pay afterwards" column assumes has been
    /// adopted. Eight, because that is the top of the range the offline ridge study explored
    /// and the point where the curve has largely flattened.
    /// </summary>
    const int WideSamplingChoice = 8;

    /// <summary>
    /// The "hidden now" column is deliberately pinned to the OLD width rather than following
    /// the shipped default. The tool exists to quote what widening bought against a fixed
    /// reference; if this tracked the default it would move the moment the default moved, and
    /// every figure it ever printed would silently mean something different.
    /// </summary>
    const int BaselineTexels = LodHzbProjection.NarrowTexelsPerAxis;

    sealed class Options
    {
        public string? Cache;
        public int Width = DefaultWidth;
        public int Height = DefaultHeight;
        public double FovDegrees = 60;
        public int Positions = 8;
        public int Yaws = 8;
        public int Seed = 20260823;
        public double[] Radii = Array.Empty<double>();
        public double MaxDistance = 8000;
        public double Far;
        public double Vanilla;
        public bool OccludeAll;
        public double MoveBlocks;
        public double TurnDegrees;
        public bool Help;
    }

    public static int Run(string[] args)
    {
        Options o;
        try { o = Parse(args); }
        catch (Exception e) { Console.Error.WriteLine("  " + e.Message); return 2; }

        if (o.Help) { PrintHelp(); return 0; }

        string? cache = o.Cache ?? NewestClientCache();
        if (cache == null || !File.Exists(cache))
        {
            Console.Error.WriteLine("  no cache database found - pass one with --cache <path>");
            return 2;
        }

        // The settings file holds whatever the view distance is TODAY, which is not
        // necessarily what the session being reasoned about ran at - it was read as 64 once
        // while every in-game figure on record came from about 350, and the lever comparison
        // moves with this number. It is an input to the measurement, so it is overridable.
        double vanilla = o.Vanilla > 0
            ? o.Vanilla
            : ReadVanillaViewDistance(out _);
        string vanillaSource = o.Vanilla > 0 ? "given on the command line" : SettingsSource();

        // One pass, and the radius is not a parameter of it: the occluder is the whole
        // drawn scene, so there is nothing to sweep.
        if (o.OccludeAll) o.Radii = new[] { double.PositiveInfinity };
        else if (o.Radii.Length == 0) o.Radii = new[] { vanilla, 512, 2048 };
        if (o.Far <= 0) o.Far = Math.Max(2000, o.MaxDistance * 1.5);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        string work = CopyForReading(cache);

        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = work,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
            conn.Open();

            long[] keys = ReadKeys(conn);
            if (keys.Length == 0)
            {
                Console.Error.WriteLine("  the cache holds no sections");
                return 2;
            }

            var sections = new CacheSections(conn);
            var meshes = new MeshCache(sections);

            Console.WriteLine();
            Console.WriteLine("  HZB field measurement");
            Console.WriteLine("  cache      " + cache);
            Console.WriteLine("             " + Describe(keys) + ", "
                + (new FileInfo(cache).Length / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB");
            Console.WriteLine("  screen     " + o.Width + "x" + o.Height + ", "
                + LodHzbReference.LevelCount(o.Width, o.Height) + " pyramid levels, "
                + o.FovDegrees.ToString("0.#", CultureInfo.InvariantCulture) + " deg vertical fov");
            Console.WriteLine("  lod        transitions at "
                + string.Join(", ", LodWorld.GetLevelThresholds()) + " blocks");
            Console.WriteLine("  vanilla    " + vanilla.ToString("0", CultureInfo.InvariantCulture)
                + " blocks (" + vanillaSource + ")");
            Console.WriteLine("  views      " + o.Positions + " positions x " + o.Yaws
                + " yaws, seed " + o.Seed + ", out to "
                + o.MaxDistance.ToString("0", CultureInfo.InvariantCulture) + " blocks");
            Console.WriteLine();

            Camera[] cameras = ChooseCameras(keys, sections, o);
            if (cameras.Length == 0)
            {
                Console.Error.WriteLine("  no camera position could be placed on captured terrain");
                return 2;
            }

            foreach (double radius in o.Radii)
            {
                var run = new RadiusRun(radius);
                foreach (Camera camera in cameras) Measure(camera, keys, meshes, o, radius, run);
                Report(run, o, vanilla);
            }

            Console.WriteLine("  " + clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)
                + "s total");
            Console.WriteLine();
            return 0;
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(work)!, recursive: true); } catch { }
        }
    }

    // ---- inputs ----

    static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                if (string.Equals(a, "hzbfield", StringComparison.OrdinalIgnoreCase)) continue;
                throw new ArgumentException("unexpected argument '" + a + "'");
            }

            string Next() =>
                i + 1 < args.Length ? args[++i] : throw new ArgumentException(a + " needs a value");

            switch (a)
            {
                case "--help": o.Help = true; break;
                case "--cache": o.Cache = Next(); break;
                case "--width": o.Width = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--height": o.Height = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--fov": o.FovDegrees = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--positions": o.Positions = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--yaws": o.Yaws = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--seed": o.Seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--max-distance":
                    o.MaxDistance = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--far": o.Far = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--vanilla": o.Vanilla = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--occlude-all": o.OccludeAll = true; break;
                case "--motion":
                {
                    string[] parts = Next().Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2)
                        throw new ArgumentException("--motion needs <blocks>,<degrees>");
                    o.MoveBlocks = double.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
                    o.TurnDegrees = double.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
                    o.OccludeAll = true;
                    break;
                }
                case "--radii":
                    o.Radii = Next().Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(v => double.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray();
                    break;
                default: throw new ArgumentException("unknown option '" + a + "'");
            }
        }

        if (o.Width < 16 || o.Height < 16) throw new ArgumentException("screen is too small to measure");
        if (o.Positions < 1 || o.Yaws < 1) throw new ArgumentException("need at least one view");
        return o;
    }

    static void PrintHelp()
    {
        Console.WriteLine();
        Console.WriteLine("  hzbfield - measures both answers to the sky problem offline");
        Console.WriteLine();
        Console.WriteLine("    --cache <path>       cache database (default: newest client cache)");
        Console.WriteLine("    --radii a,b,c        occluder radii in blocks (default: vanilla,512,2048)");
        Console.WriteLine("    --positions <n>      camera positions (default 8)");
        Console.WriteLine("    --yaws <n>           yaws per position (default 8)");
        Console.WriteLine("    --max-distance <n>   furthest section tested (default 8000)");
        Console.WriteLine("    --width/--height     screen size (default 2560x1440)");
        Console.WriteLine("    --fov <deg>          vertical field of view (default 60)");
        Console.WriteLine("    --vanilla <blocks>   vanilla view distance to model");
        Console.WriteLine("                         (default: whatever clientsettings.json says today)");
        Console.WriteLine("    --motion b,d         with --occlude-all: the camera walked b blocks forward and");
        Console.WriteLine("                         turned d degrees between the snapshot and the test, which is");
        Console.WriteLine("                         what one frame of play does to a previous-frame pyramid.");
        Console.WriteLine("    --occlude-all        every drawn section occludes every other, which is what a");
        Console.WriteLine("                         pyramid built from the PREVIOUS frame's finished depth");
        Console.WriteLine("                         would see. Ignores --radii; assumes a still camera.");
        Console.WriteLine("    --seed <n>           camera placement seed (default 20260823)");
        Console.WriteLine();
    }

    static string? NewestClientCache()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VintagestoryData", "ModData", "vintagehorizons");
        if (!Directory.Exists(root)) return null;

        return Directory.EnumerateFiles(root, "*.db")
            .Where(p => !p.EndsWith("-server.db", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// The owner's cache is not a test fixture, and opening it through LodStore would let a
    /// format check delete rows out of the only copy. A snapshot beside it costs a file copy
    /// and removes that possibility entirely.
    /// </summary>
    static string CopyForReading(string cache)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-hzbfield-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string copy = Path.Combine(dir, Path.GetFileName(cache));
        File.Copy(cache, copy);
        foreach (string side in new[] { "-wal", "-shm" })
        {
            if (File.Exists(cache + side)) File.Copy(cache + side, copy + side);
        }
        return copy;
    }

    static string SettingsSource()
    {
        ReadVanillaViewDistance(out string source);
        return source;
    }

    static double ReadVanillaViewDistance(out string source)
    {
        source = "assumed - clientsettings.json not readable";
        string settings = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VintagestoryData", "clientsettings.json");
        try
        {
            if (File.Exists(settings))
            {
                foreach (string line in File.ReadLines(settings))
                {
                    int at = line.IndexOf("\"viewDistance\"", StringComparison.Ordinal);
                    if (at < 0) continue;
                    string tail = line[(at + 14)..].TrimStart(' ', ':');
                    string digits = new string(tail.TakeWhile(char.IsDigit).ToArray());
                    if (digits.Length > 0 && double.TryParse(digits, CultureInfo.InvariantCulture, out double v))
                    {
                        source = "clientsettings.json";
                        return v;
                    }
                }
            }
        }
        catch { }
        return 64;
    }

    static long[] ReadKeys(SqliteConnection conn)
    {
        var keys = new List<long>();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Detail, SX, SZ FROM Section";
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            keys.Add(LodWorld.SectionKey(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));
        }
        return keys.ToArray();
    }

    static string Describe(long[] keys)
    {
        var perLevel = new int[LodWorld.MaxLevel + 1];
        foreach (long k in keys)
        {
            int level = LodWorld.KeyLevel(k);
            if (level >= 0 && level < perLevel.Length) perLevel[level]++;
        }

        var parts = new List<string>();
        for (int i = 0; i < perLevel.Length; i++)
        {
            if (perLevel[i] > 0) parts.Add("L" + i + ":" + perLevel[i]);
        }
        return keys.Length.ToString(CultureInfo.InvariantCulture)
            + " sections (" + string.Join(" ", parts) + ")";
    }

    // ---- cache access ----

    sealed class CacheSections
    {
        readonly SqliteCommand cmd;
        readonly LodStore decoder = new(new CaptureLogger());
        readonly Dictionary<long, LodSection?> loaded = new();

        public CacheSections(SqliteConnection conn)
        {
            cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Data FROM Section WHERE Detail=@d AND SX=@x AND SZ=@z";
            cmd.Parameters.Add("@d", SqliteType.Integer);
            cmd.Parameters.Add("@x", SqliteType.Integer);
            cmd.Parameters.Add("@z", SqliteType.Integer);
            cmd.Prepare();
        }

        public LodSection? Get(long key)
        {
            if (loaded.TryGetValue(key, out LodSection? cached)) return cached;

            cmd.Parameters["@d"].Value = LodWorld.KeyLevel(key);
            cmd.Parameters["@x"].Value = LodWorld.KeySx(key);
            cmd.Parameters["@z"].Value = LodWorld.KeySz(key);

            // No world: block ids stay unresolved, which costs nothing here. Geometry comes
            // from the runs and from the water/thin palette flags, both of which are in the
            // blob rather than in the registry.
            LodSection? section = cmd.ExecuteScalar() is byte[] blob
                ? decoder.DeserializeForeign(blob, null)
                : null;

            loaded[key] = section;
            return section;
        }
    }

    /// <summary>
    /// Opaque geometry per section, in world blocks, meshed once and reused by every view. The
    /// mesher is the renderer's own, so the triangles rasterised here and the height span the
    /// box is built from are the ones the game would draw and cull with.
    /// </summary>
    sealed class MeshCache
    {
        readonly CacheSections sections;
        readonly Dictionary<long, Entry?> byKey = new();

        public MeshCache(CacheSections sections) => this.sections = sections;

        public sealed record Entry(float[] Xyz, int[] Indices, int IndexCount, LodHeightSpan Span);

        public Entry? Get(long key)
        {
            if (byKey.TryGetValue(key, out Entry? cached)) return cached;

            Entry? entry = Build(key);
            byKey[key] = entry;
            return entry;
        }

        Entry? Build(long key)
        {
            LodSection? self = sections.Get(key);
            if (self == null) return null;

            var neighbors = new SectionSnapshot?[4];
            for (int d = 0; d < 4; d++)
            {
                long nk = LodWorld.NeighborKey(key,
                    d == 0 ? -1 : d == 1 ? 1 : 0,
                    d == 2 ? -1 : d == 3 ? 1 : 0);
                LodSection? nb = sections.Get(nk);
                if (nb != null) neighbors[d] = SectionSnapshot.Of(nb);
            }

            MeshResult mesh = LodMesher.BuildMesh(new MeshJob
            {
                Key = key,
                Self = SectionSnapshot.Of(self),
                Neighbors = neighbors,
                // Every neighbour that exists in the cache was just supplied, so a side with
                // no neighbour really is the frontier. Nothing is assumed covered.
                AssumedCoveredSides = 0,
            });

            if (mesh.IndexCount == 0) return null;
            return new Entry(mesh.Xyz, mesh.Indices, mesh.IndexCount, mesh.Heights.Opaque);
        }
    }

    // ---- cameras ----

    readonly record struct Camera(double X, double Y, double Z, double[] Yaws);

    static Camera[] ChooseCameras(long[] keys, CacheSections sections, Options o)
    {
        // Placed on the finest level present, because that is where the player stands and
        // where the occluding terrain is drawn from.
        long[] finest = keys.Where(k => LodWorld.KeyLevel(k) == 0).OrderBy(k => k).ToArray();
        if (finest.Length == 0) finest = keys.OrderBy(LodWorld.KeyLevel).ThenBy(k => k).ToArray();

        var rng = new Random(o.Seed);
        var chosen = new List<Camera>();
        var seen = new HashSet<long>();

        var yaws = new double[o.Yaws];
        for (int i = 0; i < o.Yaws; i++) yaws[i] = i * (2 * Math.PI / o.Yaws);

        for (int attempt = 0; attempt < finest.Length * 4 && chosen.Count < o.Positions; attempt++)
        {
            long key = finest[rng.Next(finest.Length)];
            if (!seen.Add(key)) continue;

            LodSection? section = sections.Get(key);
            if (section == null) continue;
            if (!SurfaceY(section, out double surface)) continue;

            int footprint = LodWorld.KeyFootprintBlocks(key);
            double originX = (double)LodWorld.KeySx(key) * footprint;
            double originZ = (double)LodWorld.KeySz(key) * footprint;

            // Eye height above the section's own highest surface. A camera buried inside
            // terrain would measure an occluder that fills the screen and prove nothing.
            chosen.Add(new Camera(
                originX + footprint / 2.0, surface + 1.7, originZ + footprint / 2.0, yaws));
        }

        return chosen.ToArray();
    }

    /// <summary>
    /// The surface of the section's CENTRE column, spiralling outward only if that column was
    /// never captured. Runs are stored top-down, so a captured column's first run is its
    /// surface.
    ///
    /// The centre column and not the section's maximum, which is what this did first and got
    /// badly wrong: taking the maximum stands the camera on the highest point of its own
    /// section every time, and a camera on a hilltop has nothing in front of it to be hidden
    /// behind. That biases the whole measurement toward "nothing is occluded" - the first run
    /// found 7.5% hidden against 46-88% in game, and this was why. The centre column puts the
    /// camera in valleys and on slopes in the proportion the terrain actually has them.
    /// </summary>
    static bool SurfaceY(LodSection section, out double y)
    {
        const int gs = LodSection.GridSize;
        int centre = gs / 2;

        for (int ring = 0; ring < gs; ring++)
        {
            for (int dz = -ring; dz <= ring; dz++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    // Only the perimeter of each ring is new.
                    if (ring > 0 && Math.Abs(dx) != ring && Math.Abs(dz) != ring) continue;

                    int cx = centre + dx, cz = centre + dz;
                    if (cx < 0 || cz < 0 || cx >= gs || cz >= gs) continue;

                    int col = LodSection.ColumnIndex(cx, cz);
                    if (!section.Captured[col]) continue;
                    Span<ulong> runs = section.ColumnRuns(col);
                    if (runs.Length == 0) continue;

                    y = LodSection.RunYTop(runs[0]);
                    return true;
                }
            }
        }

        y = 0;
        return false;
    }

    // ---- per-view measurement ----

    sealed class RadiusRun
    {
        public readonly double Radius;
        public readonly Tally[] Bands = new Tally[BandCeilings.Length];
        public readonly Tally All = new();
        public int Views;
        public long DepthSaturated;

        public RadiusRun(double radius)
        {
            Radius = radius;
            for (int i = 0; i < Bands.Length; i++) Bands[i] = new Tally();
        }
    }

    sealed class Tally
    {
        public long Candidates;
        public long NearPlane, OffScreen, Degenerate;
        public long Tested;
        public long HiddenBaseline;
        public long Remainder;
        public readonly long[] HiddenWide = new long[WideTexels.Length];
        public long SubCellsTested, SubCellsHidden, FullyCellHidden;

        // Subdivision measured on top of the cheap lever rather than instead of it. Without
        // this the two columns invite the wrong comparison: they are not alternatives to
        // choose between once, they are a cheap change and a possible follow-on, and the only
        // number that justifies the follow-on is what it adds AFTER the cheap one is taken.
        public long SubCellsAfterWideTested, SubCellsAfterWideHidden;

        /// <summary>
        /// Of the sections wide-8 called hidden, how many stop being hidden once the box's
        /// near face is pulled toward the camera by a hair.
        ///
        /// This is the self-occlusion meter. In the radius modes it should be near zero and
        /// is a control: the occluder set and the tested set are disjoint there, so no
        /// section can meet its own depth and a verdict resting on a hair means something
        /// else is wrong. Under --occlude-all every section IS in the buffer, and whatever
        /// this counts is suppression the real renderer would produce for one frame and then
        /// undo the next - the section vanishes, so it is no longer in the depth buffer, so
        /// it comes back. A flicker, not a saving. HzbFieldChecks pins the mechanism.
        /// </summary>
        public long HiddenByAHair, HiddenByLessThanCoarse;

        /// <summary>
        /// The safety number, and the only one that can condemn the previous-frame design:
        /// sections last frame's picture called hidden that THIS frame's picture would have
        /// drawn. Each one is terrain a player could see and the renderer would not draw.
        ///
        /// It is an upper bound rather than a count of real defects, because the fresh test
        /// is itself conservative - it refuses plenty that is genuinely hidden - so a
        /// disagreement is "the stale answer was not reproducible", not "the player saw sky".
        /// Bounding it from above is the useful direction for a safety question.
        ///
        /// Its floor is exactly zero, by construction: with no motion the two cameras are the
        /// same camera and the two verdicts are the same computation, so a non-zero reading
        /// at rest is a broken instrument and not a finding (G73).
        /// </summary>
        public long StaleHidFreshDrew, StaleHidFreshUndecided, StaleTestedBothWays, StaleHidTotal;
    }

    /// <summary>
    /// The two margins the meter reports, as fractions of the depth value: one just above
    /// float precision at these depths, and one wide enough to stand for any real gap
    /// between a section's bounding face and its own surface.
    /// </summary>
    static readonly double[] Margins = { 1e-7, 1e-5 };

    static void Measure(Camera camera, long[] keys, MeshCache meshes, Options o,
        double radius, RadiusRun run)
    {
        // The sections the renderer would hold in its draw set at this camera: one level per
        // distance, exactly as WantedLevelFor decides it.
        var selected = new List<(long Key, double Distance)>();
        foreach (long key in keys)
        {
            double distance = Math.Sqrt(LodWorld.NearestDistanceSqTo(key, camera.X, camera.Z));
            if (distance > o.MaxDistance) continue;
            if (LodWorld.WantedLevelFor(distance) != LodWorld.KeyLevel(key)) continue;
            selected.Add((key, distance));
        }
        if (selected.Count == 0) return;

        var depth = new float[o.Width * o.Height];

        // Modelling motion is what makes a second, fresh pyramid meaningful; without it the
        // two cameras coincide and the comparison would compare a thing with itself.
        bool moving = o.MoveBlocks != 0 || o.TurnDegrees != 0;

        var frustum = new LodFrustum();
        var identity = new float[16];
        identity[0] = identity[5] = identity[10] = identity[15] = 1f;

        foreach (double yaw in camera.Yaws)
        {
            // vp is the view the SNAPSHOT was taken from. With --motion it is last frame's;
            // without it, it is simply this frame's and the two are the same.
            float[] vp = ViewProjection(yaw, o);

            // Where the camera stands NOW. Only the frustum uses it: which sections are in
            // the draw set is this frame's question, while the depth picture and every box
            // projected against it belong to the frame that produced it. Getting this
            // backwards would be the whole error the mode exists to measure.
            double turn = o.TurnDegrees * Math.PI / 180.0;
            float[] vpNow = o.TurnDegrees != 0 ? ViewProjection(yaw + turn, o) : vp;
            double nowX = camera.X + Math.Sin(yaw) * o.MoveBlocks;
            double nowZ = camera.Z + Math.Cos(yaw) * o.MoveBlocks;
            var nowCamera = new Camera(nowX, camera.Y, nowZ, camera.Yaws);

            // The classifier only ever sees CPU-approved candidates, and approval starts with
            // this test. Without it the population is the whole world including everything
            // behind the camera, which the projection then refuses as near-plane crossings -
            // 70% of the first run's tests were that, and they are not a property of the
            // pyramid at all.
            frustum.Update(vpNow, identity);

            Array.Fill(depth, 1f);
            foreach ((long key, double distance) in selected)
            {
                if (!o.OccludeAll && distance > radius) continue;
                MeshCache.Entry? mesh = meshes.Get(key);
                if (mesh != null) Rasterize(depth, o.Width, o.Height, vp, key, camera, mesh);
            }

            ChainPyramid pyramid = ChainPyramid.Build(depth, o.Width, o.Height);

            // The comparison pyramid: the same scene as THIS frame would have drawn it. Only
            // built when motion is being modelled, because without motion it is the identical
            // picture and the comparison is a tautology.
            ChainPyramid? fresh = null;
            if (moving)
            {
                Array.Fill(depth, 1f);
                foreach ((long key, double _) in selected)
                {
                    MeshCache.Entry? m = meshes.Get(key);
                    if (m != null) Rasterize(depth, o.Width, o.Height, vpNow, key, nowCamera, m);
                }
                fresh = ChainPyramid.Build(depth, o.Width, o.Height);
            }

            run.Views++;

            foreach ((long key, double distance) in selected)
            {
                if (!o.OccludeAll && distance <= radius) continue;
                MeshCache.Entry? mesh = meshes.Get(key);
                if (mesh == null || !mesh.Span.HasGeometry) continue;

                int footprint = LodWorld.KeyFootprintBlocks(key);
                double relX = (double)LodWorld.KeySx(key) * footprint - camera.X;
                double relZ = (double)LodWorld.KeySz(key) * footprint - camera.Z;
                double minY = mesh.Span.MinY - camera.Y;
                double maxY = mesh.Span.MaxY - camera.Y;

                double nowRelX = (double)LodWorld.KeySx(key) * footprint - nowX;
                double nowRelZ = (double)LodWorld.KeySz(key) * footprint - nowZ;
                if (!frustum.BoxInView(
                        nowRelX, minY, nowRelZ, nowRelX + footprint, maxY, nowRelZ + footprint))
                    continue;

                Tally band = run.Bands[BandOf(distance)];
                band.Candidates++;
                run.All.Candidates++;

                LodHzbScreenBounds bounds = LodHzbProjection.Project(
                    vp, relX, minY, relZ, relX + footprint, maxY, relZ + footprint);

                if (!bounds.Usable)
                {
                    if (bounds.Reason.Contains("near plane", StringComparison.Ordinal))
                    { band.NearPlane++; run.All.NearPlane++; }
                    else if (bounds.Reason.Contains("off screen", StringComparison.Ordinal))
                    { band.OffScreen++; run.All.OffScreen++; }
                    else { band.Degenerate++; run.All.Degenerate++; }
                    continue;
                }

                band.Tested++;
                run.All.Tested++;
                if (bounds.NearestDepth >= 0.999999f) run.DepthSaturated++;

                // Every tested section, and deliberately BEFORE the baseline filter below.
                // Behind it the comparison would only ever see sections the narrow test could
                // not hide, and the rate would be quoted over a subset chosen by a different
                // test - which is the same population-mismatch mistake that made the figure
                // this phase was justified by wrong.
                if (fresh != null)
                {
                    band.StaleTestedBothWays++; run.All.StaleTestedBothWays++;
                    if (LodHzbProjection.IsOccluded(
                            bounds, pyramid, o.Width, o.Height, WideSamplingChoice, out _))
                    {
                        band.StaleHidTotal++; run.All.StaleHidTotal++;
                        LodHzbScreenBounds now = LodHzbProjection.Project(
                            vpNow, nowRelX, minY, nowRelZ,
                            nowRelX + footprint, maxY, nowRelZ + footprint);
                        if (!now.Usable)
                        {
                            band.StaleHidFreshUndecided++; run.All.StaleHidFreshUndecided++;
                        }
                        else if (!LodHzbProjection.IsOccluded(
                                     now, fresh, o.Width, o.Height, WideSamplingChoice, out _))
                        {
                            band.StaleHidFreshDrew++; run.All.StaleHidFreshDrew++;
                        }
                    }
                }

                if (LodHzbProjection.IsOccluded(bounds, pyramid, o.Width, o.Height, BaselineTexels, out _))
                {
                    band.HiddenBaseline++;
                    run.All.HiddenBaseline++;
                    continue;
                }

                // Everything below is counted over exactly the sections the current test could
                // not hide, which is what makes the two levers comparable.
                band.Remainder++;
                run.All.Remainder++;

                for (int w = 0; w < WideTexels.Length; w++)
                {
                    if (LodHzbProjection.IsOccluded(
                            bounds, pyramid, o.Width, o.Height, WideTexels[w], out _))
                    {
                        band.HiddenWide[w]++;
                        run.All.HiddenWide[w]++;
                    }
                }

                // Asked of the shipped width only, because that is the verdict anything real
                // would act on. Pulling the near face TOWARD the camera can only make the
                // inequality harder, so a verdict that survives had margin and one that does
                // not was resting on the last bits.
                if (LodHzbProjection.IsOccluded(
                        bounds, pyramid, o.Width, o.Height, WideSamplingChoice, out _))
                {
                    for (int m = 0; m < Margins.Length; m++)
                    {
                        LodHzbScreenBounds pulled = bounds with
                        {
                            NearestDepth = (float)(bounds.NearestDepth * (1.0 - Margins[m])),
                        };
                        if (LodHzbProjection.IsOccluded(
                                pulled, pyramid, o.Width, o.Height, WideSamplingChoice, out _))
                            continue;

                        if (m == 0) { band.HiddenByAHair++; run.All.HiddenByAHair++; }
                        else { band.HiddenByLessThanCoarse++; run.All.HiddenByLessThanCoarse++; }
                    }
                }

                int hiddenCells = 0;
                int hiddenCellsAfterWide = 0;
                bool wideAlreadyHidThis = LodHzbProjection.IsOccluded(
                    bounds, pyramid, o.Width, o.Height, WideSamplingChoice, out _);

                for (int cell = 0; cell < LodHzbProjection.SubCellCount; cell++)
                {
                    LodHzbProjection.SubCell(cell, relX, minY, relZ,
                        relX + footprint, maxY, relZ + footprint,
                        out double cellMinX, out double cellMinZ,
                        out double cellMaxX, out double cellMaxZ);

                    LodHzbScreenBounds cellBounds = LodHzbProjection.Project(
                        vp, cellMinX, minY, cellMinZ, cellMaxX, maxY, cellMaxZ);
                    if (!cellBounds.Usable) continue;

                    if (LodHzbProjection.IsOccluded(
                            cellBounds, pyramid, o.Width, o.Height, BaselineTexels, out _))
                    {
                        hiddenCells++;
                    }

                    // Only over the sections wide sampling did NOT already take, because a
                    // cell of an already-skipped section is not a saving anyone can bank
                    // twice.
                    if (!wideAlreadyHidThis && LodHzbProjection.IsOccluded(
                            cellBounds, pyramid, o.Width, o.Height, WideSamplingChoice, out _))
                    {
                        hiddenCellsAfterWide++;
                    }
                }

                band.SubCellsTested += LodHzbProjection.SubCellCount;
                band.SubCellsHidden += hiddenCells;
                run.All.SubCellsTested += LodHzbProjection.SubCellCount;
                run.All.SubCellsHidden += hiddenCells;

                if (!wideAlreadyHidThis)
                {
                    band.SubCellsAfterWideTested += LodHzbProjection.SubCellCount;
                    band.SubCellsAfterWideHidden += hiddenCellsAfterWide;
                    run.All.SubCellsAfterWideTested += LodHzbProjection.SubCellCount;
                    run.All.SubCellsAfterWideHidden += hiddenCellsAfterWide;
                }

                if (hiddenCells == LodHzbProjection.SubCellCount)
                {
                    band.FullyCellHidden++;
                    run.All.FullyCellHidden++;
                }
            }
        }
    }

    static int BandOf(double distance)
    {
        for (int i = 0; i < BandCeilings.Length; i++)
        {
            if (distance < BandCeilings[i]) return i;
        }
        return BandCeilings.Length - 1;
    }

    // ---- projection, rasterisation, pyramid ----

    /// <summary>
    /// Column-major m[col * 4 + row], matching LodFrustum, the shader and LodHzbProjection.
    /// The camera sits at the origin because every box is expressed camera-relative, so the
    /// view half is a pure rotation.
    /// </summary>
    internal static float[] ViewProjection(double yaw, int width, int height,
        double fovDegrees, double far)
    {
        double fov = fovDegrees * Math.PI / 180.0;
        double aspect = width / (double)height;
        double near = 0.1;
        double f = 1.0 / Math.Tan(fov / 2.0);

        var p = new double[16];
        p[0] = f / aspect;
        p[5] = f;
        p[10] = (far + near) / (near - far);
        p[11] = -1;
        p[14] = 2 * far * near / (near - far);

        // Level look, which is the case a ridge occludes in. Forward lies on the XZ plane.
        double fx = Math.Sin(yaw), fz = Math.Cos(yaw);
        double rx = fz, rz = -fx;

        var v = new double[16];
        v[0 * 4 + 0] = rx; v[1 * 4 + 0] = 0; v[2 * 4 + 0] = rz;
        v[0 * 4 + 1] = 0; v[1 * 4 + 1] = 1; v[2 * 4 + 1] = 0;
        v[0 * 4 + 2] = -fx; v[1 * 4 + 2] = 0; v[2 * 4 + 2] = -fz;
        v[3 * 4 + 3] = 1;

        var m = new float[16];
        for (int col = 0; col < 4; col++)
        {
            for (int row = 0; row < 4; row++)
            {
                double sum = 0;
                for (int k = 0; k < 4; k++) sum += p[k * 4 + row] * v[col * 4 + k];
                m[col * 4 + row] = (float)sum;
            }
        }
        return m;
    }

    static float[] ViewProjection(double yaw, Options o) =>
        ViewProjection(yaw, o.Width, o.Height, o.FovDegrees, o.Far);

    static void Rasterize(float[] depth, int width, int height, float[] vp,
        long key, in Camera camera, MeshCache.Entry mesh)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        double offsetX = (double)LodWorld.KeySx(key) * footprint - camera.X;
        double offsetZ = (double)LodWorld.KeySz(key) * footprint - camera.Z;
        double offsetY = -camera.Y;

        Span<double> triangle = stackalloc double[3 * 4];
        Span<double> clipped = stackalloc double[8 * 4];

        for (int i = 0; i + 2 < mesh.IndexCount; i += 3)
        {
            for (int corner = 0; corner < 3; corner++)
            {
                int v = mesh.Indices[i + corner] * 3;
                double x = mesh.Xyz[v] + offsetX;
                double y = mesh.Xyz[v + 1] + offsetY;
                double z = mesh.Xyz[v + 2] + offsetZ;

                triangle[corner * 4 + 0] = vp[0] * x + vp[4] * y + vp[8] * z + vp[12];
                triangle[corner * 4 + 1] = vp[1] * x + vp[5] * y + vp[9] * z + vp[13];
                triangle[corner * 4 + 2] = vp[2] * x + vp[6] * y + vp[10] * z + vp[14];
                triangle[corner * 4 + 3] = vp[3] * x + vp[7] * y + vp[11] * z + vp[15];
            }

            int count = ClipNear(triangle, 3, clipped);
            if (count < 3) continue;

            // Fan the clipped polygon. Screen coordinates put v=0 at row 0, which is the
            // indexing LodHzbProjection reads the pyramid with.
            Project(clipped, 0, width, height, out double ax, out double ay, out double az);
            for (int t = 1; t + 1 < count; t++)
            {
                Project(clipped, t, width, height, out double bx, out double by, out double bz);
                Project(clipped, t + 1, width, height, out double cx, out double cy, out double cz);
                RasterTriangle(depth, width, height, ax, ay, az, bx, by, bz, cx, cy, cz);
            }
        }
    }

    const double MinimumW = 1e-4;

    /// <summary>
    /// Sutherland-Hodgman against w &gt;= MinimumW, in homogeneous space and before the
    /// divide. A triangle crossing the near plane cannot be projected corner by corner - the
    /// coordinates wrap through infinity - and the camera stands ON the occluder here, so this
    /// is the ordinary case rather than an edge one.
    /// </summary>
    internal static int ClipNear(Span<double> poly, int count, Span<double> result)
    {
        int outCount = 0;
        for (int i = 0; i < count; i++)
        {
            int j = i + 1 == count ? 0 : i + 1;
            double wi = poly[i * 4 + 3], wj = poly[j * 4 + 3];
            bool insideI = wi >= MinimumW, insideJ = wj >= MinimumW;

            if (insideI)
            {
                if (outCount * 4 + 4 > result.Length) break;
                for (int k = 0; k < 4; k++) result[outCount * 4 + k] = poly[i * 4 + k];
                outCount++;
            }

            if (insideI != insideJ)
            {
                if (outCount * 4 + 4 > result.Length) break;
                double t = (MinimumW - wi) / (wj - wi);
                for (int k = 0; k < 4; k++)
                {
                    result[outCount * 4 + k] =
                        poly[i * 4 + k] + t * (poly[j * 4 + k] - poly[i * 4 + k]);
                }
                outCount++;
            }
        }
        return outCount;
    }

    static void Project(Span<double> poly, int index, int width, int height,
        out double x, out double y, out double z)
    {
        double w = poly[index * 4 + 3];
        x = (poly[index * 4 + 0] / w * 0.5 + 0.5) * width;
        y = (poly[index * 4 + 1] / w * 0.5 + 0.5) * height;
        z = poly[index * 4 + 2] / w * 0.5 + 0.5;
    }

    /// <summary>
    /// Nearest-wins depth only; colour, winding and back-face rejection are all irrelevant to
    /// a depth pyramid. NDC z is affine in screen space over a planar triangle, so
    /// interpolating it barycentrically is exact rather than an approximation.
    /// </summary>
    internal static void RasterTriangle(float[] depth, int width, int height,
        double ax, double ay, double az, double bx, double by, double bz,
        double cx, double cy, double cz)
    {
        double area = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
        if (Math.Abs(area) < 1e-9) return;

        int x0 = (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)));
        int x1 = (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx)));
        int y0 = (int)Math.Floor(Math.Min(ay, Math.Min(by, cy)));
        int y1 = (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy)));

        if (x1 < 0 || y1 < 0 || x0 >= width || y0 >= height) return;

        x0 = Math.Max(x0, 0);
        y0 = Math.Max(y0, 0);
        x1 = Math.Min(x1, width - 1);
        y1 = Math.Min(y1, height - 1);

        double inv = 1.0 / area;
        for (int y = y0; y <= y1; y++)
        {
            double py = y + 0.5;
            int row = y * width;
            for (int x = x0; x <= x1; x++)
            {
                double px = x + 0.5;
                double la = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) * inv;
                if (la < 0) continue;
                double lb = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) * inv;
                if (lb < 0) continue;
                double lc = 1 - la - lb;
                if (lc < 0) continue;

                double z = la * az + lb * bz + lc * cz;
                if (z < 0 || z > 1) continue;
                if (z < depth[row + x]) depth[row + x] = (float)z;
            }
        }
    }

    /// <summary>
    /// A pyramid over a rasterised depth image, reduced by the same code the shader mirrors,
    /// so the levels under test are the ones the reduction actually produces.
    /// </summary>
    internal sealed class ChainPyramid : ILodHzbLevels
    {
        readonly float[][] levels;
        readonly int width, height;

        ChainPyramid(float[][] levels, int width, int height)
        {
            this.levels = levels;
            this.width = width;
            this.height = height;
        }

        public static ChainPyramid Build(float[] level0, int width, int height)
        {
            int count = LodHzbReference.LevelCount(width, height);
            var levels = new float[count][];
            levels[0] = level0;
            for (int level = 1; level < count; level++)
            {
                (int w, int h) = LodHzbReference.LevelSize(width, height, level - 1);
                levels[level] = LodHzbReference.Reduce(levels[level - 1], w, h);
            }
            return new ChainPyramid(levels, width, height);
        }

        public int Levels => levels.Length;

        public (int Width, int Height) Size(int level) =>
            LodHzbReference.LevelSize(width, height, level);

        public float Farthest(int level, int x, int y)
        {
            (int w, int h) = Size(level);
            int cx = Math.Clamp(x, 0, w - 1);
            int cy = Math.Clamp(y, 0, h - 1);
            return levels[level][cy * w + cx];
        }
    }

    // ---- report ----

    static void Report(RadiusRun run, Options o, double vanilla)
    {
        bool primary = !o.OccludeAll && Math.Abs(run.Radius - vanilla) < 0.5;
        string label = o.OccludeAll
            ? "occluder: every drawn section, near and far   [Phase 6 option 3: a pyramid "
                + "built from the previous frame's finished depth, "
                + (o.MoveBlocks == 0 && o.TurnDegrees == 0
                    ? "camera unmoved]"
                    : "camera then walked "
                        + o.MoveBlocks.ToString("0.##", CultureInfo.InvariantCulture)
                        + " blocks and turned "
                        + o.TurnDegrees.ToString("0.##", CultureInfo.InvariantCulture) + " deg]")
            : primary
            ? "occluder: vanilla terrain within "
                + run.Radius.ToString("0", CultureInfo.InvariantCulture)
                + " blocks   [the Phase 4/5 question]"
            : "occluder: everything drawn within "
                + run.Radius.ToString("0", CultureInfo.InvariantCulture)
                + " blocks   [Phase 6 context: cached terrain occluding cached terrain]";

        Console.WriteLine("  " + label);
        Console.WriteLine();
        Console.WriteLine("    band      judged   hidden now       of the remainder, hidden by");
        Console.WriteLine("                                     wide 4   wide 8  wide 16    4x4 cells"
            + "   cells after wide 8");
        Console.WriteLine("    " + new string('-', 93));

        for (int i = 0; i < run.Bands.Length; i++)
        {
            Tally t = run.Bands[i];
            if (t.Candidates == 0) continue;
            Console.WriteLine("    " + Row(BandNames[i], t));
        }

        Console.WriteLine("    " + new string('-', 93));
        Console.WriteLine("    " + Row("all", run.All));
        Console.WriteLine();

        Tally all = run.All;
        long undecided = all.NearPlane + all.OffScreen + all.Degenerate;
        Console.WriteLine("    " + all.Candidates.ToString(CultureInfo.InvariantCulture)
            + " section tests over " + run.Views + " views; "
            + undecided.ToString(CultureInfo.InvariantCulture) + " undecided ("
            + Percent(undecided, all.Candidates) + ": near plane " + all.NearPlane
            + ", off screen " + all.OffScreen + ", degenerate " + all.Degenerate + ")");

        if (all.StaleTestedBothWays > 0)
        {
            Console.WriteLine("    SAFETY: " + all.StaleTestedBothWays
                + " sections judged both ways; last frame's picture hid " + all.StaleHidTotal
                + " of them. Of those hides, " + all.StaleHidFreshDrew
                + " (" + Percent(all.StaleHidFreshDrew, all.StaleHidTotal)
                + ") would have been DRAWN by this frame's picture and "
                + all.StaleHidFreshUndecided + " ("
                + Percent(all.StaleHidFreshUndecided, all.StaleHidTotal)
                + ") refused by it. That is " + Percent(all.StaleHidFreshDrew, all.Candidates)
                + " of everything on screen. Upper bound on terrain the stale test could"
                + " wrongly remove; at zero motion it must read 0.");
        }

        if (o.MoveBlocks != 0 || o.TurnDegrees != 0)
        {
            long mustDraw = undecided + (all.Tested - all.HiddenBaseline - all.HiddenWide[1]);
            Console.WriteLine("    undecided means last frame's picture has no data there, so the"
                + " section must be drawn; with those counted as drawn, "
                + Percent(all.Candidates - mustDraw, all.Candidates)
                + " of everything the frustum approved is still hidden");
        }

        if (all.Remainder > 0)
        {
            Console.WriteLine("    of the " + all.Remainder.ToString(CultureInfo.InvariantCulture)
                + " sections the current test could not hide, wide-8 hides "
                + Percent(all.HiddenWide[1], all.Remainder)
                + " and 4x4 subdivision hides " + Percent(all.SubCellsHidden, all.SubCellsTested)
                + " of pieces (" + Percent(all.FullyCellHidden, all.Remainder)
                + " of those sections entirely)");

            Console.WriteLine("    after adopting wide-" + WideSamplingChoice
                + ", subdivision still hides "
                + Percent(all.SubCellsAfterWideHidden, all.SubCellsAfterWideTested)
                + " of the pieces that remain - this is what the storage and draw rework"
                + " would actually buy");
        }

        long wide8 = all.HiddenWide[1];
        Console.WriteLine("    of the " + wide8.ToString(CultureInfo.InvariantCulture)
            + " wide-8 hides, " + all.HiddenByAHair.ToString(CultureInfo.InvariantCulture)
            + " (" + Percent(all.HiddenByAHair, wide8) + ") rest on a margin thinner than 1e-7 and "
            + all.HiddenByLessThanCoarse.ToString(CultureInfo.InvariantCulture)
            + " (" + Percent(all.HiddenByLessThanCoarse, wide8) + ") on one thinner than 1e-5"
            + (o.OccludeAll
                ? " - under --occlude-all these are sections hiding behind themselves, and each"
                  + " one is a one-frame flicker rather than a saving"
                : " - the radius modes cannot self-occlude, so this is the control"));

        if (run.DepthSaturated > 0)
        {
            Console.WriteLine("    WARNING: " + run.DepthSaturated
                + " boxes projected to a saturated depth; the far plane ("
                + o.Far.ToString("0", CultureInfo.InvariantCulture)
                + ") is too distant to separate them");
        }

        Console.WriteLine();
    }

    static string Row(string name, Tally t) =>
        name.PadRight(10)
        + t.Tested.ToString(CultureInfo.InvariantCulture).PadLeft(6)
        + Percent(t.HiddenBaseline, t.Tested).PadLeft(11)
        + Percent(t.HiddenWide[0], t.Remainder).PadLeft(14)
        + Percent(t.HiddenWide[1], t.Remainder).PadLeft(9)
        + Percent(t.HiddenWide[2], t.Remainder).PadLeft(9)
        + (t.SubCellsTested > 0 ? Percent(t.SubCellsHidden, t.SubCellsTested) : "-").PadLeft(13)
        + (t.SubCellsAfterWideTested > 0
            ? Percent(t.SubCellsAfterWideHidden, t.SubCellsAfterWideTested)
            : "-").PadLeft(20);

    static string Percent(long part, long whole) =>
        whole <= 0
            ? "-"
            : (part * 100.0 / whole).ToString("0.0", CultureInfo.InvariantCulture) + "%";
}
