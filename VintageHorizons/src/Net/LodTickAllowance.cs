using System.Diagnostics;

namespace VintageHorizons.Net;

/// <summary>
/// Converts a per-second rate into small allowances for the normal 50 ms server tick.
/// Each grant is capped at one tick's rounded-up share, so a delayed tick or an idle
/// queue cannot release a full second's work as a catch-up burst.
/// </summary>
internal sealed class LodTickAllowance
{
    public const int TickMilliseconds = 50;

    double credit;
    long lastMilliseconds;
    bool started;

    /// <summary>
    /// Accrue through <paramref name="nowMilliseconds"/> and return the whole credit that
    /// may be spent now. The caller reports actual use with <see cref="Spend"/> so cheap
    /// skipped candidates do not consume a load or transfer allowance.
    /// </summary>
    public int Available(long nowMilliseconds, int perSecond)
    {
        perSecond = Math.Max(1, perSecond);
        int cap = MaxPerTick(perSecond);

        if (!started)
        {
            started = true;
            lastMilliseconds = nowMilliseconds;
            credit = cap;
        }
        else if (nowMilliseconds >= lastMilliseconds)
        {
            long elapsed = nowMilliseconds - lastMilliseconds;
            lastMilliseconds = nowMilliseconds;
            double accrued = elapsed / 1000.0 * perSecond;
            if (accrued >= cap)
            {
                // This tick arrived late enough to earn the whole burst by itself. Drop
                // older credit so the next tick does not become a synchronous catch-up.
                credit = cap;
            }
            else
            {
                // Retain less than one token of fractional overflow. Without that carry,
                // rates that do not divide 20 Hz (8/s, for example) run systematically
                // slow because each rounded polling boundary throws the excess away.
                credit = Math.Min(cap + 0.999999, credit + accrued);
            }
        }
        else
        {
            // World clocks can reset while a system object is being torn down. Treat a
            // backwards jump as a new origin, never as a huge unsigned elapsed interval.
            lastMilliseconds = nowMilliseconds;
        }

        return Math.Min(cap, (int)credit);
    }

    public void Spend(int count)
    {
        if (count < 0 || count > (int)credit)
            throw new ArgumentOutOfRangeException(nameof(count));
        credit -= count;
    }

    internal static int MaxPerTick(int perSecond) =>
        Math.Max(1, (Math.Max(1, perSecond) * TickMilliseconds + 999) / 1000);
}

/// <summary>Small owning-thread deadline checked between independently resumable items.</summary>
internal readonly struct LodWorkBudget
{
    readonly long deadline;

    public LodWorkBudget(double milliseconds)
    {
        long duration = Math.Max(1,
            (long)Math.Ceiling(milliseconds * Stopwatch.Frequency / 1000.0));
        deadline = Stopwatch.GetTimestamp() + duration;
    }

    public bool Expired => Stopwatch.GetTimestamp() >= deadline;
}
