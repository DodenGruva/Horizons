namespace VintageHorizons;

/// <summary>One selected opaque section and its nearest horizontal distance to the camera.</summary>
internal readonly record struct LodOpaqueDrawEntry(long Key, double DistanceSq);

/// <summary>
/// Builds a reusable front-to-back submission list. Near opaque terrain writes depth
/// before farther terrain hidden behind it reaches the fragment shader. The destination
/// owns its capacity across frames, so a settled view allocates nothing; distance is
/// computed once per selected section rather than again inside every sort comparison.
/// </summary>
internal static class LodOpaqueDrawOrder
{
    sealed class FrontToBackComparer : IComparer<LodOpaqueDrawEntry>
    {
        public static readonly FrontToBackComparer Instance = new();

        public int Compare(LodOpaqueDrawEntry a, LodOpaqueDrawEntry b)
        {
            int distance = a.DistanceSq.CompareTo(b.DistanceSq);
            return distance != 0 ? distance : a.Key.CompareTo(b.Key);
        }
    }

    public static void FillFrontToBack(List<LodOpaqueDrawEntry> destination,
        IReadOnlyList<long> keys, double cameraX, double cameraZ)
    {
        destination.Clear();
        for (int i = 0; i < keys.Count; i++)
        {
            long key = keys[i];
            destination.Add(new LodOpaqueDrawEntry(
                key, LodWorld.NearestDistanceSqTo(key, cameraX, cameraZ)));
        }
        destination.Sort(FrontToBackComparer.Instance);
    }
}
