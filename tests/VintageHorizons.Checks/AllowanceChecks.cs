using VintageHorizons.Net;

namespace VintageHorizons.Checks;

public static class AllowanceChecks
{
    public static void Run(Check c)
    {
        c.Eq(1, LodTickAllowance.MaxPerTick(1), "a one-per-second rate still makes progress");
        c.Eq(1, LodTickAllowance.MaxPerTick(16), "the default sweep rate issues one per tick");
        c.Eq(4, LodTickAllowance.MaxPerTick(64), "a high rate rounds one tick's share upward");

        var low = new LodTickAllowance();
        c.Eq(1, low.Available(0, 8), "a new low-rate queue can serve one item immediately");
        low.Spend(1);
        c.Eq(0, low.Available(100, 8), "fractional credit does not spend early");
        c.Eq(1, low.Available(125, 8), "fractional credit reaches one at the configured rate");
        low.Spend(1);

        int issued = 0;
        for (long now = 175; now <= 1125; now += 50)
        {
            int available = low.Available(now, 8);
            issued += available;
            low.Spend(available);
        }
        c.Eq(8, issued, "normal ticks preserve the long-run per-second rate");

        var high = new LodTickAllowance();
        c.Eq(4, high.Available(0, 64), "initial credit is only one tick's high-rate share");
        high.Spend(4);
        c.Eq(3, high.Available(50, 64), "a 50ms tick accrues its fractional high-rate share");
        high.Spend(3);
        c.Eq(4, high.Available(10_000, 64), "a delayed tick cannot release catch-up work");
        high.Spend(4);
        c.Eq(3, high.Available(10_050, 64), "the tick after a delay resumes the normal rate");
        high.Spend(3);
        c.Eq(0, high.Available(9_000, 64), "a backwards clock jump grants no credit");

        var unused = new LodTickAllowance();
        c.Eq(2, unused.Available(0, 32), "the global default starts with a two-item ceiling");
        c.Eq(2, unused.Available(30_000, 32), "idle credit remains capped to one tick");

        c.Throws<ArgumentOutOfRangeException>(() => unused.Spend(3),
            "callers cannot overspend an allowance silently");
    }
}
