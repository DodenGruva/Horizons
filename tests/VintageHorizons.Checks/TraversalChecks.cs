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
