namespace VintageHorizons;

/// <summary>Bounded exponential delay for retrying a failed durable row write.</summary>
public static class LodSaveRetry
{
    public const int InitialDelayMilliseconds = 250;
    public const int MaximumDelayMilliseconds = 30000;

    public static int DelayMilliseconds(int consecutiveFailures)
    {
        if (consecutiveFailures <= 1) return InitialDelayMilliseconds;
        int shift = Math.Min(consecutiveFailures - 1, 30);
        long delay = (long)InitialDelayMilliseconds << shift;
        return (int)Math.Min(delay, MaximumDelayMilliseconds);
    }
}
