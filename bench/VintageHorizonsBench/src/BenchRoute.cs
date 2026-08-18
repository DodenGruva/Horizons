using System.Globalization;

namespace VintageHorizonsBench;

public class BenchWaypoint
{
    public string Name = "";
    public double X;
    public double Y;
    public double Z;
    public float Yaw;
    public float Pitch;
    public bool HasTrajectory;
    public double EndX;
    public double EndY;
    public double EndZ;
    public float EndYaw;
    public float EndPitch;

    public double XAt(double progress) => Lerp(X, EndX, progress);
    public double YAt(double progress) => Lerp(Y, EndY, progress);
    public double ZAt(double progress) => Lerp(Z, EndZ, progress);
    public float YawAt(double progress) => (float)Lerp(Yaw, EndYaw, progress);
    public float PitchAt(double progress) => (float)Lerp(Pitch, EndPitch, progress);

    /// <summary>
    /// Route files use the conventional human-facing sign around a zero-degree horizon
    /// (negative looks down). Vintage Story centres its camera pitch at PI radians, with
    /// larger values looking down, so route -20 degrees becomes engine PI + 20 degrees.
    /// </summary>
    public float CameraPitchAt(double progress) =>
        Vintagestory.API.MathTools.GameMath.PI - PitchAt(progress);

    static double Lerp(double from, double to, double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        return from + (to - from) * progress;
    }
}

/// <summary>
/// A route is a plain text file so it can be written by hand or by a script, and so a
/// diff shows exactly what changed between benchmark runs:
///
///   # name           x       y     z       yawDeg  pitchDeg
///   ridge-north      512400  180   512400  0       -10
///   ridge-flyby      512000  220   512000  0       -10  ->  512400  220  512000  360  -10
///
/// Yaw/pitch are degrees for legibility and converted to the engine's radians here.
/// Yaw 0 faces north, increasing counter-clockwise; pitch is negative looking down.
/// A line with an arrow is a trajectory. The benchmark moves and rotates smoothly from
/// its start to its end over VHBENCH_MEASURE seconds instead of holding one viewpoint.
/// Angles are not normalised, so 0 -> 360 deliberately makes one complete turn.
/// </summary>
public class BenchRoute
{
    public readonly List<BenchWaypoint> Waypoints = new();

    public static BenchRoute Load(string path)
    {
        var route = new BenchRoute();

        foreach (string rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                throw new FormatException($"Route line needs at least 'name x y z': {rawLine}");
            }

            bool hasTrajectory = parts.Length == 12 && parts[6] == "->";
            if (parts.Length > 6 && !hasTrajectory)
            {
                throw new FormatException(
                    $"Route line must be 'name x y z [yaw pitch]' or "
                    + $"'name x y z yaw pitch -> endX endY endZ endYaw endPitch': {rawLine}");
            }

            double x = Parse(parts[1]);
            double y = Parse(parts[2]);
            double z = Parse(parts[3]);
            float yaw = parts.Length > 4 ? Deg(parts[4]) : 0f;
            float pitch = parts.Length > 5 ? Deg(parts[5]) : 0f;

            route.Waypoints.Add(new BenchWaypoint
            {
                Name = parts[0],
                X = x,
                Y = y,
                Z = z,
                Yaw = yaw,
                Pitch = pitch,
                HasTrajectory = hasTrajectory,
                EndX = hasTrajectory ? Parse(parts[7]) : x,
                EndY = hasTrajectory ? Parse(parts[8]) : y,
                EndZ = hasTrajectory ? Parse(parts[9]) : z,
                EndYaw = hasTrajectory ? Deg(parts[10]) : yaw,
                EndPitch = hasTrajectory ? Deg(parts[11]) : pitch,
            });
        }

        if (route.Waypoints.Count == 0) throw new FormatException($"Route {path} has no waypoints");
        return route;
    }

    static double Parse(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    static float Deg(string s) =>
        (float)Parse(s) * Vintagestory.API.MathTools.GameMath.DEG2RAD;
}
