namespace VintageHorizons.Checks;

/// <summary>
/// The conditions under which a depth picture from an earlier frame may decide what to draw.
///
/// These matter more than their size suggests. Every other way this renderer can be wrong makes
/// it draw too much; this is the one path that can REMOVE terrain a player can see, and it does
/// so silently. The guards are also silent in the other direction - one that always fires turns
/// the feature off while the switch still reads "on", which is how a playtest gets spent
/// measuring nothing. Neither direction is visible from reading the render path.
/// </summary>
public static class StaleDepthChecks
{
    const double Limit = LodTerrainRenderer.LateDepthTranslationLimitBlocks;

    public static void Run(Check c)
    {
        AnOrdinaryFrameIsAccepted(c);
        NoPictureIsRefused(c);
        APictureFromThisFrameIsRefused(c);
        AChangedProjectionIsRefused(c);
        ChangedGeometryIsRefused(c);
        ATeleportIsRefused(c);
        WalkingIsNeverATeleport(c);
        NonsenseIsRefused(c);
        ReasonsAreReportedInTheOrderThatHelps(c);
        TurningIsRefused(c);
    }

    static LodStaleDepthRefusal Judge(
        long pictureFrame = 10, long now = 11, bool projection = true, bool turning = false,
        double dx = 0, double dy = 0, double dz = 0,
        long pictureGeometry = 7, long nowGeometry = 7, double limit = Limit) =>
        LodStaleDepthPolicy.Evaluate(
            pictureFrame, now, projection, turning, dx, dy, dz,
            pictureGeometry, nowGeometry, limit);

    static void AnOrdinaryFrameIsAccepted(Check c)
    {
        // The case that must work, or the feature is off and nobody is told. One frame of
        // walking, nothing else changed.
        c.Eq(LodStaleDepthRefusal.None, Judge(dx: 0.1),
            "a picture from last frame, one step of walking, is usable");
        c.Eq(LodStaleDepthRefusal.None, Judge(pictureFrame: 0, now: 1_000_000),
            "an old frame number is not itself a reason to refuse");
    }

    static void NoPictureIsRefused(Check c)
    {
        c.Eq(LodStaleDepthRefusal.NoPicture, Judge(pictureFrame: long.MinValue),
            "a session that has taken no picture yet refuses");
    }

    static void APictureFromThisFrameIsRefused(Check c)
    {
        // The late build runs after the draw, so a picture stamped with this frame cannot
        // exist. If one appears, the ordering has changed underneath this policy.
        c.Eq(LodStaleDepthRefusal.NoPicture, Judge(pictureFrame: 11, now: 11),
            "a picture claiming to be from this frame refuses");
        c.Eq(LodStaleDepthRefusal.NoPicture, Judge(pictureFrame: 12, now: 11),
            "a picture from the future refuses");
    }

    static void AChangedProjectionIsRefused(Check c)
    {
        c.Eq(LodStaleDepthRefusal.ProjectionChanged, Judge(projection: false),
            "a zoom, field-of-view change or resize refuses");
    }

    static void ChangedGeometryIsRefused(Check c)
    {
        // A section evicted or re-meshed since the picture is still standing in it, at its old
        // distance, able to hide terrain that is now visible.
        c.Eq(LodStaleDepthRefusal.GeometryChanged, Judge(pictureGeometry: 7, nowGeometry: 8),
            "geometry replaced since the picture refuses");
        c.Eq(LodStaleDepthRefusal.GeometryChanged, Judge(pictureGeometry: 9, nowGeometry: 8),
            "any disagreement refuses, in either direction");
    }

    static void ATeleportIsRefused(Check c)
    {
        c.Eq(LodStaleDepthRefusal.CameraJumped, Judge(dx: Limit + 0.01),
            "a step past the limit on one axis refuses");
        c.Eq(LodStaleDepthRefusal.CameraJumped, Judge(dx: 400, dy: -90, dz: 250),
            "a teleport refuses");

        // Diagonal distance, not per-axis: three components each inside the limit can still
        // add up to a jump, and testing them separately would let one through.
        double each = Limit * 0.7;
        c.Eq(LodStaleDepthRefusal.CameraJumped, Judge(dx: each, dy: each, dz: each),
            "three axes each inside the limit still refuse when the distance is not");
    }

    static void WalkingIsNeverATeleport(Check c)
    {
        // The limit exists to catch a cut or a teleport, not ordinary movement. One frame of
        // sprinting is about 0.13 blocks; if that ever refused, the feature would be off
        // whenever anyone was moving, which is most of play.
        foreach (double step in new[] { 0.0, 0.05, 0.1, 0.13, 0.25, 0.5 })
        {
            c.Eq(LodStaleDepthRefusal.None, Judge(dx: step, dz: step),
                "moving " + step + " blocks on two axes is still ordinary movement");
        }

        c.Eq(LodStaleDepthRefusal.None, Judge(dy: Limit - 0.001),
            "falling just inside the limit is accepted");
    }

    static void NonsenseIsRefused(Check c)
    {
        foreach (double bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            c.Eq(LodStaleDepthRefusal.CameraJumped, Judge(dx: bad),
                "a camera position of " + bad + " refuses");
        }

        c.Eq(LodStaleDepthRefusal.CameraJumped, Judge(limit: double.NaN),
            "a limit that is not a number refuses");
        c.Eq(LodStaleDepthRefusal.CameraJumped, Judge(limit: -1),
            "a negative limit refuses");

        // Zero is a legitimate setting - refuse anything that moved at all - and must not be
        // read as nonsense.
        c.Eq(LodStaleDepthRefusal.None, Judge(limit: 0),
            "a zero limit still accepts a camera that did not move");
        c.Eq(LodStaleDepthRefusal.CameraJumped, Judge(dx: 0.001, limit: 0),
            "a zero limit refuses a camera that moved at all");
    }

    static void ReasonsAreReportedInTheOrderThatHelps(Check c)
    {
        // When several are wrong at once the reason reported is the one worth acting on. These
        // pin the order so the log never sends anyone after the wrong thing: "no picture" is
        // not a camera jump, and "terrain changed" is not a camera jump either.
        c.Eq(LodStaleDepthRefusal.NoPicture,
            Judge(pictureFrame: long.MinValue, projection: false, dx: 500, nowGeometry: 99),
            "with everything wrong at once, no picture is reported first");
        c.Eq(LodStaleDepthRefusal.ProjectionChanged,
            Judge(projection: false, dx: 500, nowGeometry: 99),
            "a changed projection outranks both a jump and changed terrain");
        c.Eq(LodStaleDepthRefusal.GeometryChanged, Judge(dx: 500, nowGeometry: 99),
            "changed terrain outranks a camera jump");
    }

    /// <summary>
    /// Turning refuses the stale picture, and this is the guard the owner's own eyes found.
    ///
    /// Rotation does not change what hides what - occlusion depends on where the camera stands.
    /// It does change where every box LANDS, and the old picture judges each one at the screen
    /// position it held last frame, against depth that only ever covered last frame's screen.
    /// On 2026-08-24 that showed up as distant terrain flickering while the camera was swung
    /// around from standing, and at some angles staying gone.
    /// </summary>
    static void TurningIsRefused(Check c)
    {
        c.Eq(LodStaleDepthRefusal.ViewTurned, Judge(turning: true),
            "a turning view refuses the stale picture");
        c.Eq(LodStaleDepthRefusal.None, Judge(turning: false),
            "a settled view still uses it");

        // Turning outranks a camera jump but not the two that make the picture meaningless,
        // so the log names the cause someone can act on.
        c.Eq(LodStaleDepthRefusal.ViewTurned, Judge(turning: true, dx: 500),
            "turning is reported ahead of a jump");
        c.Eq(LodStaleDepthRefusal.ProjectionChanged, Judge(turning: true, projection: false),
            "a changed projection still outranks turning");
        c.Eq(LodStaleDepthRefusal.NoPicture,
            Judge(turning: true, pictureFrame: long.MinValue),
            "having no picture at all still outranks turning");

        // Turning must not be mistaken for a translation limit: the two are independent, and a
        // player standing still and looking around moves not at all.
        c.Eq(LodStaleDepthRefusal.ViewTurned, Judge(turning: true, dx: 0, dy: 0, dz: 0),
            "a stationary player who turns is refused on the turn, not on movement");
    }
}
