namespace VintageHorizons.Checks;

/// <summary>
/// The aggregate that lets the quadtree walk reject a whole subtree by height.
///
/// Every check here is one-sided in the same direction: the aggregate may be looser than
/// the meshes beneath it, never tighter. A loose box costs a traversal that finds nothing;
/// a tight one deletes terrain the player can see, and does it silently, from a camera
/// angle nobody was testing.
/// </summary>
public static class SubtreeHeightChecks
{
    public static void Run(Check c)
    {
        AggregateCoversEveryDescendant(c);
        RemovalIsExactRatherThanAccumulated(c);
        AMeshWithoutBoundsPoisonsItsAncestors(c);
        AnEmptySubtreeCarriesNoBox(c);
    }

    // L0 keys under one L2 parent, so a two-step ancestor walk is exercised rather than one.
    static long Leaf(int sx, int sz) => LodWorld.SectionKey(0, sx, sz);
    static long ParentOf(long key) => LodWorld.ParentKey(key);

    static void AggregateCoversEveryDescendant(Check c)
    {
        var heights = new LodSubtreeHeights();
        long a = Leaf(4, 4);            // L1 parent (2,2), L2 grandparent (1,1)
        long b = Leaf(5, 5);            // same L1 parent
        long far = Leaf(6, 4);          // a different L1 parent, same L2 grandparent

        heights.SetMesh(a, LodHeightSpan.Of(60f, 70f));
        heights.SetMesh(b, LodHeightSpan.Of(90f, 130f));
        heights.SetMesh(far, LodHeightSpan.Of(10f, 20f));

        LodSubtreeBounds leaf = heights.Of(a);
        c.Eq(60f, leaf.Span.MinY, "a leaf's aggregate is its own mesh");
        c.Eq(70f, leaf.Span.MaxY, "at both ends");
        c.Eq(1, leaf.Meshes, "and counts one mesh");

        LodSubtreeBounds parent = heights.Of(ParentOf(a));
        c.Eq(60f, parent.Span.MinY, "a parent's floor is the lowest mesh beneath it");
        c.Eq(130f, parent.Span.MaxY, "and its ceiling the highest");
        c.Eq(2, parent.Meshes, "over both children");

        LodSubtreeBounds grandparent = heights.Of(ParentOf(ParentOf(a)));
        c.Eq(10f, grandparent.Span.MinY, "the union reaches down through two levels");
        c.Eq(130f, grandparent.Span.MaxY, "and up through them");
        c.Eq(3, grandparent.Meshes, "counting every mesh in the subtree");

        // A coarse node's own mesh is drawn instead of its children, not as well as them,
        // but the walk reaches it through the same box - so the box has to hold both.
        long coarse = ParentOf(a);
        heights.SetMesh(coarse, LodHeightSpan.Of(200f, 240f));
        c.Eq(240f, heights.Of(coarse).Span.MaxY, "a node's own mesh joins the aggregate");
        c.Eq(60f, heights.Of(coarse).Span.MinY, "without displacing its descendants");
        c.Eq(4, heights.Of(ParentOf(coarse)).Meshes, "and is counted with them");
    }

    /// <summary>
    /// The reason the ancestor chain is recomputed rather than folded into: a union has no
    /// inverse. Removing the tallest mesh must lower the box, and an implementation that
    /// accumulated would keep the old ceiling forever and never be noticed, because a box
    /// that is too TALL hides nothing.
    /// </summary>
    static void RemovalIsExactRatherThanAccumulated(Check c)
    {
        var heights = new LodSubtreeHeights();
        long low = Leaf(4, 4);
        long high = Leaf(5, 5);
        long parent = ParentOf(low);

        heights.SetMesh(low, LodHeightSpan.Of(60f, 70f));
        heights.SetMesh(high, LodHeightSpan.Of(200f, 260f));
        c.Eq(260f, heights.Of(parent).Span.MaxY, "the tall mesh raised the parent");

        heights.RemoveMesh(high);
        c.Eq(70f, heights.Of(parent).Span.MaxY, "and evicting it lowers the parent again");
        c.Eq(1, heights.Of(parent).Meshes, "the count follows the removal");

        // Re-publication with a different extent is the same path, and is what an ordinary
        // re-mesh does several times a second while terrain streams.
        heights.SetMesh(low, LodHeightSpan.Of(-40f, 0f));
        c.Eq(-40f, heights.Of(parent).Span.MinY, "a replaced mesh replaces its contribution");
        c.Eq(0f, heights.Of(parent).Span.MaxY, "rather than merging with the one it replaced");

        heights.RemoveMesh(low);
        c.Eq(0, heights.Of(parent).Meshes, "an emptied subtree holds nothing");
        c.False(heights.Of(parent).Span.HasGeometry, "and offers no box");
        c.Eq(0, heights.TrackedNodes, "emptied nodes are dropped rather than left at zero");
    }

    static void AMeshWithoutBoundsPoisonsItsAncestors(Check c)
    {
        var heights = new LodSubtreeHeights();
        long measured = Leaf(4, 4);
        long unmeasured = Leaf(5, 5);
        long parent = ParentOf(measured);
        long top = LodWorld.SectionKey(LodWorld.MaxLevel, 0, 0);

        heights.SetMesh(measured, LodHeightSpan.Of(60f, 70f));
        heights.SetMesh(unmeasured, LodHeightSpan.Empty);

        c.True(heights.Of(parent).Unknown, "one unmeasured mesh makes the subtree unknown");
        c.True(heights.Of(parent).Span.HasGeometry,
            "the measured part is still summed, so a later fix needs no rebuild");
        c.False(heights.Of(parent).CullingSpan.HasGeometry,
            "but nothing may be culled with a partial union");
        c.True(heights.Of(top).Unknown, "and unknown reaches the quadtree root");

        heights.RemoveMesh(unmeasured);
        c.False(heights.Of(parent).Unknown, "removing it clears the doubt");
        c.Eq(70f, heights.Of(parent).CullingSpan.MaxY, "and the real box comes back");
    }

    static void AnEmptySubtreeCarriesNoBox(Check c)
    {
        var heights = new LodSubtreeHeights();
        long untouched = Leaf(9, 9);

        c.Eq(0, heights.Of(untouched).Meshes, "a node nobody has published under is empty");
        c.False(heights.Of(untouched).CullingSpan.HasGeometry,
            "and reads as unknown, which keeps the full-height box");
        c.False(heights.Of(untouched).Unknown, "without being flagged as a missing measurement");

        heights.SetMesh(untouched, LodHeightSpan.Of(5f, 5f));
        c.True(heights.Of(untouched).CullingSpan.HasGeometry,
            "a flat plain is a legitimate zero-height box, not an absent one");
        c.Eq(5f, heights.Of(untouched).CullingSpan.MaxY, "at its own surface");

        heights.Clear();
        c.Eq(0, heights.TrackedNodes, "a world change drops every aggregate");
        c.False(heights.Of(untouched).CullingSpan.HasGeometry, "including the one just set");
    }
}
