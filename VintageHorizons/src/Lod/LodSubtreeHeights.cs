namespace VintageHorizons;

/// <summary>
/// The aggregate vertical extent of every resident mesh in one quadtree subtree, plus how
/// many meshes that covers and whether any of them failed to report bounds.
/// </summary>
/// <param name="Span">Union of the reported spans below and including the node.</param>
/// <param name="Unknown">
/// A resident mesh in the subtree reported no bounds, so the union is incomplete and the
/// caller must fall back to the full-height box.
/// </param>
/// <param name="Meshes">Resident meshes in the subtree, for measuring what a rejection saved.</param>
public readonly record struct LodSubtreeBounds(LodHeightSpan Span, bool Unknown, int Meshes)
{
    /// <summary>
    /// The span a subtree may be culled with, or <see cref="LodHeightSpan.Empty"/> when it
    /// may not. Unknown collapses to empty here rather than at each call site, so the only
    /// way to get a box out of this type is one that covers everything drawable.
    /// </summary>
    public LodHeightSpan CullingSpan => Unknown ? LodHeightSpan.Empty : Span;
}

/// <summary>
/// Per-node aggregate mesh bounds for the quadtree walk.
///
/// <see cref="LodHeightSpan"/> gave individual sections a real vertical extent, but the
/// traversal still bounds whole SUBTREES from bedrock to sky, because a parent's box has to
/// contain its descendants and one mesh's own extent says nothing about theirs. Looking up
/// or down therefore keeps entire subtrees a real box would refuse, and rejecting a coarse
/// node rejects everything beneath it - so the aggregate is worth more per test than the
/// per-section bound is.
///
/// What it aggregates is deliberately narrow: the meshes that are RESIDENT RIGHT NOW.
/// Traversal decides what to draw, and only a node with a mesh is ever drawn, so a subtree
/// whose descendants have not been meshed yet has nothing to hide. It cannot starve them
/// either - mesh demand comes from the orientation-independent radial planner, not from
/// this walk (G8) - and each mesh that arrives updates the aggregate before it can be
/// drawn.
///
/// A mesh that reports no bounds poisons its ancestors with <c>Unknown</c> instead of being
/// skipped, so a missing measurement can only ever draw too much. That is the same rule the
/// per-section box uses, for the same reason: a bound that is too tight deletes terrain a
/// player can see.
///
/// Maintenance is a bottom-up recompute of the changed node's ancestor chain, at most seven
/// nodes of four child lookups each. Recomputing rather than accumulating is what makes
/// removal exact: a union cannot have a member subtracted from it.
/// </summary>
public sealed class LodSubtreeHeights
{
    // Presence means the node has a resident mesh; an empty span means it has one whose
    // bounds are unknown. Kept apart from the aggregates because a node is both a mesh
    // holder and a subtree root, and the two answers differ.
    readonly Dictionary<long, LodHeightSpan> own = new();
    readonly Dictionary<long, LodSubtreeBounds> subtrees = new();

    /// <summary>Nodes currently holding an aggregate; empty subtrees are not stored.</summary>
    public int TrackedNodes => subtrees.Count;

    /// <summary>
    /// Record the bounds of a section's resident mesh. Pass
    /// <see cref="LodHeightSpan.Empty"/> for a mesh that reported none.
    /// </summary>
    public void SetMesh(long key, LodHeightSpan span)
    {
        own[key] = span;
        RefreshAncestors(key);
    }

    public void RemoveMesh(long key)
    {
        if (!own.Remove(key)) return;
        RefreshAncestors(key);
    }

    public void Clear()
    {
        own.Clear();
        subtrees.Clear();
    }

    /// <summary>
    /// The aggregate over this node's subtree. A node with nothing resident beneath it
    /// returns the default, whose span is empty - which reads as "unknown" to a caller and
    /// therefore keeps the full-height box. Nothing is drawn there either way.
    /// </summary>
    public LodSubtreeBounds Of(long key) =>
        subtrees.TryGetValue(key, out LodSubtreeBounds bounds) ? bounds : default;

    void RefreshAncestors(long key)
    {
        for (int level = LodWorld.KeyLevel(key); ; level++)
        {
            LodSubtreeBounds bounds = Recompute(key);
            if (bounds.Meshes == 0) subtrees.Remove(key);
            else subtrees[key] = bounds;

            if (level >= LodWorld.MaxLevel) return;
            key = LodWorld.ParentKey(key);
        }
    }

    LodSubtreeBounds Recompute(long key)
    {
        LodHeightSpan span = LodHeightSpan.Empty;
        bool unknown = false;
        int meshes = 0;

        if (own.TryGetValue(key, out LodHeightSpan mine))
        {
            meshes++;
            if (mine.HasGeometry) span = span.Union(mine);
            else unknown = true;
        }

        if (LodWorld.KeyLevel(key) > 0)
        {
            for (int qz = 0; qz < 2; qz++)
            {
                for (int qx = 0; qx < 2; qx++)
                {
                    if (!subtrees.TryGetValue(LodWorld.ChildKey(key, qx, qz),
                        out LodSubtreeBounds child)) continue;
                    span = span.Union(child.Span);
                    unknown |= child.Unknown;
                    meshes += child.Meshes;
                }
            }
        }

        return new LodSubtreeBounds(span, unknown, meshes);
    }
}
