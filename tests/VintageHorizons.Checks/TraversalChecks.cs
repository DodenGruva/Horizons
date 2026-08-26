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
        KnownVisibleIsEvidenceNotAbsenceOfEvidence(c);
    }

    /// <summary>
    /// `KnownVisible` is the ground truth the depth pyramid is checked against, so what it
    /// must NOT do is say yes when nothing was measured.
    ///
    /// The trap it exists to avoid: `!Occluded` reads as "visible" but is equally true of a
    /// section whose answer was thrown away by a view change, a mesh replacement, or a query
    /// that never completed. Comparing pyramid verdicts against that would manufacture
    /// disagreements out of missing data - and since the whole point of the comparison is a
    /// count that must stay at zero, a false alarm there is as damaging as a missed one.
    /// </summary>
    static void KnownVisibleIsEvidenceNotAbsenceOfEvidence(Check c)
    {
        var fresh = new LodTemporalOcclusionState();
        c.False(fresh.KnownVisible, "a section nobody has measured is not known visible");
        c.False(fresh.Occluded, "nor known hidden");

        // A completed query that saw pixels is the only thing that establishes it.
        var seen = new LodTemporalOcclusionState();
        seen.BeginQuery(frame: 10, epoch: 1);
        c.False(seen.KnownVisible, "a query in flight establishes nothing yet");
        c.True(seen.CompleteQuery(anySamplesPassed: true, currentEpoch: 1), "the result lands");
        c.True(seen.KnownVisible, "and a query that saw pixels establishes visibility");
        c.False(seen.Occluded, "which is not hidden");

        var hidden = new LodTemporalOcclusionState();
        hidden.BeginQuery(frame: 10, epoch: 1);
        hidden.CompleteQuery(anySamplesPassed: false, currentEpoch: 1);
        c.True(hidden.Occluded, "a query that saw nothing establishes hidden");
        c.False(hidden.KnownVisible, "and is not known visible");

        // A result from an older view is consumed and must establish nothing at all - this
        // is the case that would otherwise read as "visible" forever after a camera move.
        var stale = new LodTemporalOcclusionState();
        stale.BeginQuery(frame: 10, epoch: 1);
        c.False(stale.CompleteQuery(anySamplesPassed: true, currentEpoch: 2), "a stale result is refused");
        c.False(stale.KnownVisible, "and establishes nothing");
        c.False(stale.Occluded, "in either direction");

        // Invalidation throws the evidence away, not just the hidden flag.
        var invalidated = new LodTemporalOcclusionState();
        invalidated.BeginQuery(frame: 10, epoch: 1);
        invalidated.CompleteQuery(anySamplesPassed: true, currentEpoch: 1);
        c.True(invalidated.KnownVisible, "established first");
        invalidated.Invalidate(epoch: 1);
        c.False(invalidated.KnownVisible, "a section-local change discards the evidence");

        // And so does the view moving on, which is checked lazily on the next read.
        var moved = new LodTemporalOcclusionState();
        moved.BeginQuery(frame: 10, epoch: 1);
        moved.CompleteQuery(anySamplesPassed: true, currentEpoch: 1);
        moved.ShouldDraw(frame: 11, epoch: 2, hiddenProbeIntervalFrames: 8);
        c.False(moved.KnownVisible, "a new view epoch discards the evidence");
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

        // The aggregate subtree bound. Without it a node is bounded from bedrock to sky,
        // which the side planes make harmless and the horizontal planes make useless: a
        // subtree whose every mesh sits kilometres above the view is still traversed.
        c.False(LodTraversalPolicy.NodeInView(frustum, ahead,
            cameraX, cameraY, cameraZ, worldHeight, LodHeightSpan.Of(5000f, 5100f)),
            "an aggregate wholly above the view rejects a node the full-height box kept");
        c.True(LodTraversalPolicy.NodeInView(frustum, ahead,
            cameraX, cameraY, cameraZ, worldHeight, LodHeightSpan.Of(120f, 140f)),
            "an aggregate at the camera's own height is kept");
        c.True(LodTraversalPolicy.NodeInView(frustum, ahead,
            cameraX, cameraY, cameraZ, worldHeight, LodHeightSpan.Empty),
            "and an unknown aggregate falls back to the full-height box");

        // One-sided in the other direction too: the vertical extent may only ever take a
        // node away, never hand one back that the cheaper planes already refused.
        c.False(LodTraversalPolicy.NodeInView(frustum, behind,
            cameraX, cameraY, cameraZ, worldHeight, LodHeightSpan.Of(120f, 140f)),
            "a real aggregate cannot rescue a node behind the camera");
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

            long twoLevelsFine = LodWorld.SectionKey(2, 0, 0);
            long oneLevelFine = LodWorld.SectionKey(3, 0, 0);

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
