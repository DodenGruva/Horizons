namespace VintageHorizons.Checks;

public static class RenderDirtySchedulerChecks
{
    public static void Run(Check c)
    {
        RefreshPrunesAndOrders(c);
        AdditionsAreIncremental(c);
        BusyNearestDoesNotBlockLaterWork(c);
        BoundedScanCanPassTheMaximumBusyPrefix(c);
        CameraCellsRefreshPriority(c);
        ClearInvalidatesOldEntries(c);
    }

    static void RefreshPrunesAndOrders(Check c)
    {
        var dirty = new LodRenderDirtySet();
        var scheduler = new LodRenderDirtyScheduler();
        long near = Key(2);
        long far = Key(10);
        long meaningless = Key(20);
        dirty.Add(far);
        dirty.Add(meaningless);
        dirty.Add(near);

        scheduler.Refresh(dirty, 0, 0, 512, key => key != meaningless);

        c.False(dirty.Contains(meaningless), "a refresh prunes a meaningless dirty key");
        c.True(Take(scheduler, dirty, out long first), "the refreshed index yields work");
        c.Eq(near, first, "the refreshed index yields the nearest dirty key first");
    }

    static void AdditionsAreIncremental(Check c)
    {
        var dirty = new LodRenderDirtySet();
        var scheduler = new LodRenderDirtyScheduler();
        long far = Key(10);
        long near = Key(2);
        dirty.Add(far);
        scheduler.Refresh(dirty, 0, 0, 512, _ => true);
        int rebuilds = scheduler.RebuildCount;
        int inspected = 0;

        dirty.Add(near);
        scheduler.Refresh(dirty, 64, 0, 512, _ => { inspected++; return true; });

        c.Eq(rebuilds, scheduler.RebuildCount,
            "movement inside one coarse cell does not rebuild the complete index");
        c.Eq(1, inspected,
            "an ordinary refresh inspects only the newly added dirty key");
        c.True(Take(scheduler, dirty, out long first), "an incremental addition is indexed");
        c.Eq(near, first, "an incrementally added nearer key takes priority");
    }

    static void BusyNearestDoesNotBlockLaterWork(Check c)
    {
        var dirty = new LodRenderDirtySet();
        var scheduler = new LodRenderDirtyScheduler();
        long near = Key(2);
        long far = Key(10);
        dirty.Add(near);
        dirty.Add(far);
        scheduler.Refresh(dirty, 0, 0, 512, _ => true);

        c.True(scheduler.TryTake(dirty, _ => true, key => key == near, 4, out long first),
            "a busy nearest key does not stop scheduling");
        c.Eq(far, first, "the next available key is returned while the nearest is busy");
        c.True(dirty.Contains(near), "a busy key retains its dirty obligation");
        c.True(Take(scheduler, dirty, out long second), "the formerly busy key remains indexed");
        c.Eq(near, second, "the formerly busy key schedules once available");
    }

    static void CameraCellsRefreshPriority(Check c)
    {
        var dirty = new LodRenderDirtySet();
        var scheduler = new LodRenderDirtyScheduler();
        long west = Key(0);
        long east = Key(7);
        dirty.Add(west);
        dirty.Add(east);
        scheduler.Refresh(dirty, 0, 0, 512, _ => true);
        int rebuilds = scheduler.RebuildCount;

        scheduler.Refresh(dirty, 300, 0, 512, _ => true);

        c.Eq(rebuilds + 1, scheduler.RebuildCount,
            "crossing a coarse camera cell rebuilds priorities once");
        c.True(Take(scheduler, dirty, out long first), "the rebuilt index yields work");
        c.Eq(east, first, "priority follows the new camera cell");
    }

    static void BoundedScanCanPassTheMaximumBusyPrefix(Check c)
    {
        var dirty = new LodRenderDirtySet();
        var scheduler = new LodRenderDirtyScheduler();
        var busy = new HashSet<long>();
        for (int sx = 0; sx < 48; sx++)
        {
            long key = Key(sx);
            dirty.Add(key);
            busy.Add(key);
        }
        long available = Key(60);
        dirty.Add(available);
        scheduler.Refresh(dirty, 0, 0, 512, _ => true);

        c.True(scheduler.TryTake(dirty, _ => true, busy.Contains, 49, out long selected),
            "a finite scan passes every permitted in-flight key");
        c.Eq(available, selected,
            "the first available key behind the maximum busy prefix can schedule");
    }

    static void ClearInvalidatesOldEntries(Check c)
    {
        var dirty = new LodRenderDirtySet();
        var scheduler = new LodRenderDirtyScheduler();
        long old = Key(2);
        long current = Key(6);
        dirty.Add(old);
        scheduler.Refresh(dirty, 0, 0, 512, _ => true);

        dirty.Clear();
        dirty.Add(current);
        scheduler.Refresh(dirty, 0, 0, 512, _ => true);

        c.True(Take(scheduler, dirty, out long first), "the replacement world yields work");
        c.Eq(current, first, "clearing dirty state invalidates old priority entries");
        c.False(dirty.Contains(old), "an old-world key cannot reappear after clear");
    }

    static bool Take(LodRenderDirtyScheduler scheduler, LodRenderDirtySet dirty, out long key) =>
        scheduler.TryTake(dirty, _ => true, _ => false, 8, out key);

    static long Key(int sx) => LodWorld.SectionKey(0, sx, 0);
}
