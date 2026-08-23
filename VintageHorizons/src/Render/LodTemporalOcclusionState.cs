namespace VintageHorizons;

/// <summary>
/// CPU policy for delayed exact-geometry occlusion queries. A query observes the real
/// opaque draw, so it adds no proxy geometry. A zero-sample result may suppress later
/// draws only while the camera/scene epoch is unchanged. Hidden meshes periodically draw
/// again as their own visibility probe; if they became visible, that probe is already the
/// correct terrain draw for the current frame.
/// </summary>
internal sealed class LodTemporalOcclusionState
{
    public bool Pending { get; private set; }
    public bool Occluded { get; private set; }
    public long LastQueryFrame { get; private set; } = long.MinValue;

    /// <summary>
    /// True only when a completed query for the CURRENT view actually observed pixels of
    /// this section. Read-only, and deliberately distinct from <c>!Occluded</c>: that is
    /// false both for a section proven visible and for one whose answer was thrown away,
    /// and only the first of those is evidence of anything.
    ///
    /// This exists so the depth pyramid can be checked against something that measured
    /// real pixels. A pyramid verdict of "hidden" for a section that a query positively
    /// saw is the one failure mode that deletes terrain a player can see, and no amount of
    /// reading the shader establishes its absence.
    /// </summary>
    public bool KnownVisible { get; private set; }

    long resultEpoch = long.MinValue;
    long pendingEpoch = long.MinValue;

    public bool ShouldDraw(long frame, long epoch, int hiddenProbeIntervalFrames)
    {
        ValidateEpoch(epoch);
        return !Occluded || FramesSinceLastQuery(frame) >= hiddenProbeIntervalFrames;
    }

    public bool ShouldIssueQuery(long frame, long epoch,
        int visibleQueryIntervalFrames, int hiddenProbeIntervalFrames)
    {
        ValidateEpoch(epoch);
        if (Pending) return false;
        int interval = Occluded ? hiddenProbeIntervalFrames : visibleQueryIntervalFrames;
        return FramesSinceLastQuery(frame) >= interval;
    }

    public void BeginQuery(long frame, long epoch)
    {
        Pending = true;
        pendingEpoch = epoch;
        LastQueryFrame = frame;
    }

    /// <summary>
    /// Publishes one available GPU result. A result from an older view is consumed but
    /// ignored; stale zero-sample answers can therefore never hide the new view.
    /// </summary>
    public bool CompleteQuery(bool anySamplesPassed, long currentEpoch)
    {
        if (!Pending) return false;
        Pending = false;

        if (pendingEpoch != currentEpoch)
        {
            Occluded = false;
            KnownVisible = false;
            resultEpoch = currentEpoch;
            return false;
        }

        resultEpoch = currentEpoch;
        Occluded = !anySamplesPassed;
        KnownVisible = anySamplesPassed;
        return true;
    }

    public void Invalidate(long epoch)
    {
        Occluded = false;
        KnownVisible = false;
        resultEpoch = epoch;
        // A section-local change (mesh replacement, mixed seam, turning edge) does not
        // advance the global view epoch. Mark an already-issued answer stale explicitly,
        // or the old geometry's zero-sample result could later hide its replacement.
        if (Pending) pendingEpoch = long.MinValue;
    }

    long FramesSinceLastQuery(long frame)
    {
        if (LastQueryFrame == long.MinValue) return long.MaxValue;
        return Math.Max(0, frame - LastQueryFrame);
    }

    void ValidateEpoch(long epoch)
    {
        if (resultEpoch == epoch) return;
        Occluded = false;
        KnownVisible = false;
        resultEpoch = epoch;
    }
}
