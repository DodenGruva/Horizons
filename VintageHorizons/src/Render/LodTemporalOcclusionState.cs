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
            resultEpoch = currentEpoch;
            return false;
        }

        resultEpoch = currentEpoch;
        Occluded = !anySamplesPassed;
        return true;
    }

    public void Invalidate(long epoch)
    {
        Occluded = false;
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
        resultEpoch = epoch;
    }
}
