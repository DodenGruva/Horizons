namespace VintageHorizons;

/// <summary>Why a depth picture from an earlier frame could not be trusted this frame.</summary>
internal enum LodStaleDepthRefusal
{
    /// <summary>Usable. The picture describes a scene close enough to this one.</summary>
    None,

    /// <summary>No picture from an earlier frame exists yet.</summary>
    NoPicture,

    /// <summary>The view changed shape: field of view, zoom, or a window resize.</summary>
    ProjectionChanged,

    /// <summary>The camera moved further than one frame of travel can explain.</summary>
    CameraJumped,

    /// <summary>The view turned enough that the old picture judges boxes at stale positions.</summary>
    ViewTurned,

    /// <summary>Geometry in the picture has since been replaced, evicted, or cleared.</summary>
    GeometryChanged,
}

/// <summary>
/// Whether a depth picture taken on an earlier frame may decide what to draw on this one.
///
/// Pure, and separate from the renderer, because every one of these conditions is invisible
/// when it goes wrong in either direction. A guard that never fires hides terrain a player can
/// see; a guard that always fires turns the feature off while still reporting itself on, which
/// is precisely the shape of failure that has cost this project playtests before. Neither is
/// discoverable by reading the render path, and neither needs a GPU to check.
///
/// The order is deliberate: most absolute first, so a session with no picture at all is never
/// reported as a camera jump.
///
/// WHAT IS DELIBERATELY NOT GUARDED, and why, so the absence is a decision rather than an
/// oversight:
///
/// The vanilla/cache ownership SEAM. The delayed occlusion queries protect mixed-ownership
/// sections explicitly, and that protection does not transfer here because the two mechanisms
/// fail differently. A query counts pixels, so it cannot tell "occluded" from "every fragment
/// was discarded by the ownership mask" - and acting on the second would delete exactly the
/// cache-owned remainder a player notices at the seam. The depth test never looks at a
/// fragment: it projects the section's bounding box and compares depths, and the mask has no
/// bearing on where that box is. A mixed section is therefore at no more risk here than any
/// other, and a guard would cost the saving for nothing.
///
/// TURNING is guarded, and this one was learned the hard way. Rotation does not change what
/// hides what - occlusion depends on where the camera stands, not where it looks - so the first
/// version of this policy let any amount of turning through. That reasoning is sound about
/// occlusion and wrong about SAMPLING: a box is projected through the old view, so it is judged
/// at the screen position it held last frame, against a picture that only ever covered last
/// frame's screen. Boxes crossing the screen edge are the worst case, because the projection
/// clamps their rectangle to the visible part and a verdict drawn from a sliver is applied to
/// the whole section. The owner saw the result on 2026-08-24: distant terrain flickering while
/// the camera was swung around from a standing position, and at some angles staying gone.
///
/// The shipped temporal-occlusion path already answers this, with a narrow screen-edge band
/// protected while the view is turning. This is the blunter version of the same idea - refuse
/// the whole stale picture for a frame - chosen because it needs no shader change and because
/// a frame that draws everything is never wrong, only slower. It costs culling while the camera
/// is actually moving and restores it the moment the view settles, which is when someone is
/// looking carefully enough to notice.
///
/// VANILLA's own chunks loading and unloading. The picture holds whatever vanilla drew, and a
/// chunk that has since unloaded is still standing in it. The window is one frame, the affected
/// terrain is whatever sat directly behind a chunk at the edge of vanilla's own view distance,
/// and the mod's ownership mask only ever hands a cell to vanilla once vanilla has finished
/// rendering it - so the usual case is cached terrain and vanilla terrain covering the same
/// ground at the same depth, and the picture barely changes. Left unguarded because tracking it
/// would refuse the cull constantly while moving, which is when the cull is worth having; the
/// shipped temporal profile already tolerates the same hazard for eight to sixteen frames.
/// </summary>
internal static class LodStaleDepthPolicy
{
    public static LodStaleDepthRefusal Evaluate(
        long pictureFrame,
        long currentFrame,
        bool projectionMatches,
        bool viewTurned,
        double dx,
        double dy,
        double dz,
        long pictureGeometryRevision,
        long currentGeometryRevision,
        double translationLimitBlocks)
    {
        // Equal frames means the picture is this frame's, which in the late arrangement cannot
        // have happened yet - the build runs after the draw. Either the switch has just been
        // flipped or the ordering is wrong; refuse rather than reconcile.
        if (pictureFrame == long.MinValue || pictureFrame >= currentFrame)
            return LodStaleDepthRefusal.NoPicture;

        if (!projectionMatches) return LodStaleDepthRefusal.ProjectionChanged;

        if (viewTurned) return LodStaleDepthRefusal.ViewTurned;

        // Geometry before distance: a scene that has changed is wrong at any distance, and
        // calling it a camera jump would send anyone reading the log after the wrong thing.
        if (pictureGeometryRevision != currentGeometryRevision)
            return LodStaleDepthRefusal.GeometryChanged;

        if (!double.IsFinite(dx) || !double.IsFinite(dy) || !double.IsFinite(dz))
            return LodStaleDepthRefusal.CameraJumped;

        double limit = translationLimitBlocks;
        if (!double.IsFinite(limit) || limit < 0) return LodStaleDepthRefusal.CameraJumped;

        return dx * dx + dy * dy + dz * dz > limit * limit
            ? LodStaleDepthRefusal.CameraJumped
            : LodStaleDepthRefusal.None;
    }
}
