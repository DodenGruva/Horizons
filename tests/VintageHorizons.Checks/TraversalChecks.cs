using Vintagestory.API.MathTools;

namespace VintageHorizons.Checks;

/// <summary>
/// Visibility-aware quadtree rejection and its deliberately separate residency policy.
/// </summary>
public static class TraversalChecks
{
    public static void Run(Check c)
    {
        VisibilityRejectsWholeNodesConservatively(c);
        ResidencyIgnoresCameraDirection(c);
        ResidencyUsesTheColdSectionGraceBand(c);
        OpaqueSubmissionOrdersNearestFirst(c);
        TemporalOcclusionFailsTowardDrawing(c);
    }

    static void TemporalOcclusionFailsTowardDrawing(Check c)
    {
        var state = new LodTemporalOcclusionState();

        c.True(state.ShouldDraw(10, 1, 8), "an unmeasured section draws");
        c.True(state.ShouldIssueQuery(10, 1, 4, 8), "an unmeasured section starts an exact query");
        state.BeginQuery(10, 1);
        c.False(state.ShouldIssueQuery(11, 1, 4, 8), "a pending query is never overlapped");

        c.True(state.CompleteQuery(anySamplesPassed: false, currentEpoch: 1),
            "a current GPU answer is accepted");
        c.False(state.ShouldDraw(11, 1, 8), "a zero-sample result skips a stable hidden draw");
        c.False(state.ShouldDraw(17, 1, 8), "a hidden result remains useful before its probe interval");
        c.True(state.ShouldDraw(18, 1, 8), "hidden exact geometry periodically draws as its own probe");
        c.True(state.ShouldIssueQuery(18, 1, 4, 8), "the periodic hidden draw is queried");

        state.BeginQuery(18, 1);
        c.True(state.ShouldDraw(19, 2, 8), "a changed camera or scene invalidates hiding immediately");
        c.False(state.CompleteQuery(anySamplesPassed: false, currentEpoch: 2),
            "an older-view GPU answer is consumed but rejected");
        c.True(state.ShouldDraw(20, 2, 8), "a stale zero-sample result cannot hide a newer view");

        c.True(state.ShouldIssueQuery(22, 2, 4, 8), "visible geometry is sampled on a bounded cadence");
        state.BeginQuery(22, 2);
        c.True(state.CompleteQuery(anySamplesPassed: true, currentEpoch: 2),
            "a current visible answer is accepted");
        c.True(state.ShouldDraw(23, 2, 8), "a positive sample result keeps terrain visible");
        c.False(state.ShouldIssueQuery(23, 2, 4, 8), "visible query overhead is not paid every frame");

        state.BeginQuery(26, 2);
        state.Invalidate(2);
        c.False(state.CompleteQuery(anySamplesPassed: false, currentEpoch: 2),
            "section-local invalidation rejects an answer issued for replaced geometry");
        c.True(state.ShouldDraw(27, 2, 8),
            "an old local zero-sample answer cannot hide a replacement or protected seam");

        c.False(LodTerrainRenderer.TranslationExceeded(
                0.249, 0, 0, 0, 0, 0, LodTerrainRenderer.SafeTemporalTranslationBlocks),
            "sub-quarter-block movement keeps a recent hidden result coherent");
        c.True(LodTerrainRenderer.TranslationExceeded(
                0.25, 0, 0, 0, 0, 0, LodTerrainRenderer.SafeTemporalTranslationBlocks),
            "a quarter-block cumulative translation fails back to drawing");

        float[] identity = Mat4f.Create();
        float[] tinyTurn = (float[])identity.Clone();
        tinyTurn[0] += LodTerrainRenderer.SafeTemporalRotationMatrix * 0.9f;
        c.False(LodTerrainRenderer.ViewRotationExceeded(
                tinyTurn, identity, LodTerrainRenderer.SafeTemporalRotationMatrix),
            "a sub-threshold view turn retains temporal visibility briefly");
        tinyTurn[0] += LodTerrainRenderer.SafeTemporalRotationMatrix * 0.2f;
        c.True(LodTerrainRenderer.ViewRotationExceeded(
                tinyTurn, identity, LodTerrainRenderer.SafeTemporalRotationMatrix),
            "a threshold-sized view turn fails back to drawing");
        c.False(LodTerrainRenderer.ViewRotationExceeded(
                tinyTurn, identity, float.PositiveInfinity),
            "a persistent profile never globally invalidates on camera rotation");
        c.True(LodTerrainRenderer.ViewRotationChanged(tinyTurn, identity),
            "turning is still detected so persistent profiles can probe hidden draws faster");
        c.False(LodTerrainRenderer.ViewRotationChanged(identity, identity),
            "an unchanged view does not pay the turning probe cadence");
        c.False(LodTerrainRenderer.TranslationExceeded(
                1000, 1000, 1000, 0, 0, 0, double.PositiveInfinity),
            "the extreme profile can retain visibility through all finite translation");

        float[] projection = Projection();
        float[] changedProjection = (float[])projection.Clone();
        changedProjection[10] = MathF.BitIncrement(changedProjection[10]);
        c.True(LodTerrainRenderer.ProjectionChanged(changedProjection, projection),
            "any projection change invalidates temporal visibility");
    }

    static void OpaqueSubmissionOrdersNearestFirst(Check c)
    {
        const double cameraX = 100;
        const double cameraZ = 100;
        long containing = LodWorld.SectionKey(0, 1, 1); // 64..128 in both axes
        long adjacent = LodWorld.SectionKey(0, 2, 1);   // starts 28 blocks away
        long farther = LodWorld.SectionKey(0, 4, 1);    // starts 156 blocks away
        var source = new List<long> { farther, adjacent, containing };
        var ordered = new List<LodOpaqueDrawEntry> { new(-1, -1) };

        LodOpaqueDrawOrder.FillFrontToBack(ordered, source, cameraX, cameraZ);

        c.SeqEq(new[] { containing, adjacent, farther }, ordered.Select(entry => entry.Key).ToArray(),
            "opaque submission is nearest-first rather than traversal/hash order");
        c.Eq(3, ordered.Count, "rebuilding the order clears stale entries from the reusable list");
        c.True(ordered[0].DistanceSq <= ordered[1].DistanceSq
            && ordered[1].DistanceSq <= ordered[2].DistanceSq,
            "front-to-back distances are monotonic");

        LodOpaqueDrawOrder.FillFrontToBack(ordered, new[] { adjacent, farther, containing }, cameraX, cameraZ);
        c.SeqEq(new[] { containing, adjacent, farther }, ordered.Select(entry => entry.Key).ToArray(),
            "front-to-back order is independent of input order");
    }

    static void VisibilityRejectsWholeNodesConservatively(Check c)
    {
        var frustum = new LodFrustum();
        frustum.Update(Projection(), View());

        const double cameraX = 10016;
        const double cameraY = 128;
        const double cameraZ = 10016;
        const int worldHeight = 512;

        long ahead = LodWorld.SectionKey(0, 156, 154);
        long behind = LodWorld.SectionKey(0, 156, 158);
        long containingCamera = LodWorld.SectionKey(2, 39, 39);

        c.True(LodTraversalPolicy.NodeInView(frustum, ahead,
            cameraX, cameraY, cameraZ, worldHeight),
            "a quadtree node ahead of the camera is traversed");
        c.False(LodTraversalPolicy.NodeInView(frustum, behind,
            cameraX, cameraY, cameraZ, worldHeight),
            "a quadtree node wholly behind the camera rejects its subtree");
        c.True(LodTraversalPolicy.NodeInView(frustum, containingCamera,
            cameraX, cameraY, cameraZ, worldHeight),
            "a coarse node containing the camera is kept conservatively");
    }

    static void ResidencyIgnoresCameraDirection(Check c)
    {
        const double cameraX = 10016;
        const double cameraZ = 10016;
        long ahead = LodWorld.SectionKey(0, 156, 154);
        long behind = LodWorld.SectionKey(0, 156, 158);

        c.Eq(LodWorld.NearestDistanceSqTo(ahead, cameraX, cameraZ),
            LodWorld.NearestDistanceSqTo(behind, cameraX, cameraZ),
            "the residency fixtures are equally distant on opposite camera sides");
        c.Eq(LodTraversalPolicy.WithinResidencyBand(ahead, cameraX, cameraZ),
            LodTraversalPolicy.WithinResidencyBand(behind, cameraX, cameraZ),
            "camera direction cannot change mesh residency");
    }

    static void ResidencyUsesTheColdSectionGraceBand(Check c)
    {
        double oldDetailDistance = LodWorld.DetailDistance;
        try
        {
            LodWorld.DetailDistance = 512;
            const double cameraX = 5000;
            const double cameraZ = 0;

            long twoLevelsFine = LodWorld.SectionKey(1, 0, 0);
            long oneLevelFine = LodWorld.SectionKey(2, 0, 0);

            c.False(LodTraversalPolicy.WithinResidencyBand(twoLevelsFine, cameraX, cameraZ),
                "a mesh two detail levels finer than wanted may age out");
            c.True(LodTraversalPolicy.WithinResidencyBand(oneLevelFine, cameraX, cameraZ),
                "one finer fallback level stays resident independently of visibility");
        }
        finally
        {
            LodWorld.DetailDistance = oldDetailDistance;
        }
    }

    static float[] View() =>
        Mat4f.LookAt(Mat4f.Create(),
            eye: new[] { 0f, 0f, 0f },
            center: new[] { 0f, 0f, -1f },
            up: new[] { 0f, 1f, 0f });

    static float[] Projection() =>
        Mat4f.Perspective(Mat4f.Create(), fovy: 1.05f, aspect: 16f / 9f,
            near: 0.1f, far: 1000f);
}
