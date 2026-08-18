using VintageHorizonsBench;

namespace VintageHorizons.Checks;

public static class BenchRouteChecks
{
    public static void Run(Check c)
    {
        string routeDir = Path.Combine(GameAssemblies.RepoRoot, "bench", "routes");

        BenchRoute fixedRoute = BenchRoute.Load(Path.Combine(routeDir, "vhsurvival.txt"));
        c.Eq(5, fixedRoute.Waypoints.Count, "legacy fixed-view route still loads");
        c.False(fixedRoute.Waypoints.Any(wp => wp.HasTrajectory),
            "legacy fixed-view entries remain stationary");

        BenchRoute movingRoute = BenchRoute.Load(Path.Combine(routeDir, "moving-rotation.txt"));
        c.Eq(4, movingRoute.Waypoints.Count, "moving route loads all four legs");
        c.True(movingRoute.Waypoints.All(wp => wp.HasTrajectory),
            "moving route marks every leg as a trajectory");

        BenchWaypoint east = movingRoute.Waypoints[0];
        c.Near(512020, east.XAt(-1), 0.001, "trajectory clamps before its start");
        c.Near(512220, east.XAt(0.5), 0.001, "trajectory interpolates position");
        c.Near(512420, east.XAt(2), 0.001, "trajectory clamps after its end");
        c.Near(Math.PI, east.YawAt(0.5), 0.0001,
            "0-to-360 yaw preserves a half-turn at midpoint");
        c.Near(Math.PI * 2, east.YawAt(1), 0.0001,
            "yaw is not normalised and completes the requested full turn");
        c.Near(-12 * Math.PI / 180, east.PitchAt(0.5), 0.0001,
            "route-space trajectory pitch remains fixed");
        c.Near(Math.PI + 12 * Math.PI / 180, east.CameraPitchAt(0.5), 0.0001,
            "negative route pitch converts from a zero horizon to engine PI plus look-down");

        for (int i = 1; i < movingRoute.Waypoints.Count; i++)
        {
            BenchWaypoint previous = movingRoute.Waypoints[i - 1];
            BenchWaypoint current = movingRoute.Waypoints[i];
            c.Near(previous.EndX, current.X, 0.001, $"leg {i + 1} starts at prior end X");
            c.Near(previous.EndY, current.Y, 0.001, $"leg {i + 1} starts at prior end Y");
            c.Near(previous.EndZ, current.Z, 0.001, $"leg {i + 1} starts at prior end Z");
        }

        BenchWaypoint last = movingRoute.Waypoints[^1];
        c.Near(movingRoute.Waypoints[0].X, last.EndX, 0.001, "route loop closes on X");
        c.Near(movingRoute.Waypoints[0].Y, last.EndY, 0.001, "route loop closes on Y");
        c.Near(movingRoute.Waypoints[0].Z, last.EndZ, 0.001, "route loop closes on Z");
    }
}
