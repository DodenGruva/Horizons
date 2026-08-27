namespace VintageHorizons.Checks;

public static class GpuRendererChecks
{
    public static void Run(Check c)
    {
        CapabilityPolicy(c);
        DepthConvention(c);
        RuntimeEntryPoints(c);
        DepthCopyPolicy(c);
        RenderPathPolicy(c);
        RenderPathLifecycle(c);
        GlStateOwnership(c);
        DelayedTimerRing(c);
        DelayedTimestampRing(c);
        BenchmarkWiring(c);
        FailureInjectionPolicy(c);
        DiscardedTimingNeverReachesTheTotal(c);
    }

    static void FailureInjectionPolicy(Check c)
    {
        c.Eq(LodGpuFailureStage.None, LodGpuFailureInjection.Parse(null).Stage,
            "no Phase 9 failure injection is the ordinary default");
        c.Eq(LodGpuFailureStage.ArenaSetup, LodGpuFailureInjection.Parse(" ARENA ").Stage,
            "arena injection is trimmed and case-insensitive");
        c.Eq(LodGpuFailureStage.FastShaders, LodGpuFailureInjection.Parse("shader").Stage,
            "the fast-shader failure has one explicit name");
        c.Eq(LodGpuFailureStage.DepthCopy, LodGpuFailureInjection.Parse("depth-copy").Stage,
            "the private depth-copy failure has one explicit name");
        c.Eq(LodGpuFailureStage.IndirectDraw, LodGpuFailureInjection.Parse("draw").Stage,
            "the indirect draw failure has one explicit name");
        c.Eq(LodGpuFailureStage.Invalid, LodGpuFailureInjection.Parse("surprise").Stage,
            "an unknown injection fails closed instead of selecting a nearby stage");

        LodGpuFailureInjection injection = LodGpuFailureInjection.Parse("draw");
        c.True(injection.Armed, "a valid requested injection starts armed");
        c.False(injection.Take(LodGpuFailureStage.DepthCopy),
            "asking the wrong boundary cannot consume the injection");
        c.True(injection.Take(LodGpuFailureStage.IndirectDraw),
            "the named boundary consumes the injection once");
        c.False(injection.Armed, "a consumed injection is visibly disarmed");
        c.False(injection.Take(LodGpuFailureStage.IndirectDraw),
            "the artificial failure cannot repeat every frame");
    }

    static void DepthCopyPolicy(Check c)
    {
        c.Eq(1, LodGpuDepthCopyProbe.CalculateMipLevels(1, 1),
            "a 1x1 private depth texture has one mip");
        c.Eq(11, LodGpuDepthCopyProbe.CalculateMipLevels(1920, 1080),
            "a non-power-of-two viewport allocates through a 1x1 mip");
        c.Eq(12, LodGpuDepthCopyProbe.CalculateMipLevels(2048, 1),
            "mip allocation follows the largest viewport dimension");
        c.Eq(0, LodGpuDepthCopyProbe.CalculateMipLevels(0, 1080),
            "an empty viewport cannot allocate a private depth chain");
        c.True(LodGpuDepthCopyProbe.IsSupportedDepthFormat(0x8CAC),
            "32-bit floating-point depth is accepted for an exact-format private copy");
        c.False(LodGpuDepthCopyProbe.IsSupportedDepthFormat(0x88F0),
            "depth/stencil sources fail closed until their copy semantics are validated");
    }

    static void RuntimeEntryPoints(Check c)
    {
        string[] required = LodGpuRuntimeProbe.RequiredEntryPoints;
        IntPtr[] loaded = Enumerable.Repeat(new IntPtr(1), required.Length).ToArray();
        c.True(LodGpuRuntimeProbe.ValidateEntryPoints(required, loaded, out string missing),
            "all required advanced entry points validate");
        c.Eq("", missing,
            "successful advanced entry-point validation has no missing name");

        loaded[Array.IndexOf(required, "glMultiDrawElementsIndirect")] = IntPtr.Zero;
        c.False(LodGpuRuntimeProbe.ValidateEntryPoints(required, loaded, out missing),
            "a null multi-draw entry point rejects runtime validation");
        c.Eq("glMultiDrawElementsIndirect", missing,
            "entry-point failure identifies multi-draw precisely");

        c.False(LodGpuRuntimeProbe.ValidateEntryPoints(required, loaded[..^1], out missing),
            "mismatched OpenTK binding tables reject runtime validation");
    }

    static void BenchmarkWiring(Check c)
    {
        string runner = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "scripts", "bench-windows.ps1"));
        c.True(runner.Contains("[switch]$GpuStats", StringComparison.Ordinal)
            && runner.Contains("VINTAGEHORIZONS_GPU_STATS = if ($GpuStats)",
                StringComparison.Ordinal)
            && runner.Contains("gpuStatsEnabled = [bool]$GpuStats", StringComparison.Ordinal)
            && runner.Contains("autoCommand = if ($AutoCommand)", StringComparison.Ordinal)
            && runner.Contains("gpuRender = $gpuRenderRecord", StringComparison.Ordinal)
            && runner.Contains("Seed files require -SandboxProfile", StringComparison.Ordinal)
            && runner.Contains("seedFiles = $seedRecords", StringComparison.Ordinal)
            && runner.Contains("[switch]$SeedOnly", StringComparison.Ordinal)
            && runner.Contains("VINTAGEHORIZONS_GPU_RENDERER = $GpuRenderer", StringComparison.Ordinal)
            && runner.Contains("VINTAGEHORIZONS_GPU_ARENA = $GpuArena", StringComparison.Ordinal)
            && runner.Contains("[string]$GpuFailure", StringComparison.Ordinal)
            && runner.Contains("VINTAGEHORIZONS_GPU_INJECT_FAILURE = $GpuFailure",
                StringComparison.Ordinal)
            && runner.Contains("gpuFailure = if ($GpuFailure)", StringComparison.Ordinal)
            && runner.Contains("splitNearP95Microseconds", StringComparison.Ordinal)
            && runner.Contains("splitFarP95Microseconds", StringComparison.Ordinal)
            && runner.Contains("cullTimerSamples", StringComparison.Ordinal)
            && runner.Contains("splitFarCullP95Microseconds", StringComparison.Ordinal)
            && runner.Contains("uploadP95Microseconds", StringComparison.Ordinal)
            && runner.Contains("vertexLiveMiB", StringComparison.Ordinal)
            && runner.Contains("packedLiveMiB", StringComparison.Ordinal)
            && runner.Contains("clusterLiveMiB", StringComparison.Ordinal)
            && runner.Contains("gpuRenderer = if ($GpuRenderer)", StringComparison.Ordinal)
            && runner.Contains("gpuArena = if ($GpuArena)", StringComparison.Ordinal),
            "the Windows runner pins renderer controls and records split GPU timing, upload cost, and arena memory");
    }

    static void CapabilityPolicy(Check c)
    {
        LodGpuCapabilityFacts advertised = FullCapabilities(
            entryPointsValidated: false, computeValidated: false);
        LodGpuDepthFacts observedDepth = ConventionalDepth(copyValidated: false);
        LodGpuPathDecision proposed = LodGpuCapabilityPolicy.Evaluate(advertised, observedDepth);

        c.True(proposed.TimerQueriesAvailable, "GL 4.3 advertises delayed timer queries");
        c.True(proposed.RegionalIndirectAdvertised,
            "SSBO and multi-draw facts advertise the regional path");
        c.True(proposed.HierarchicalDepthAdvertised,
            "compute, image load/store and a known depth convention advertise HZB");
        c.False(proposed.Tier1RuntimeValidated,
            "advertised features alone never select the fast path");

        LodGpuPathDecision validated = LodGpuCapabilityPolicy.Evaluate(
            FullCapabilities(entryPointsValidated: true, computeValidated: true),
            ConventionalDepth(copyValidated: true));
        c.True(validated.Tier1RuntimeValidated,
            "Tier 1 becomes eligible only after compute and depth-copy validation");

        LodGpuCapabilityFacts withoutMdi = advertised with { MultiDrawIndirectAdvertised = false };
        c.False(LodGpuCapabilityPolicy.Evaluate(withoutMdi, observedDepth)
                .RegionalIndirectAdvertised,
            "missing multi-draw support rejects regional indirect drawing independently");

        LodGpuCapabilityFacts smallSsbo = advertised with
        {
            MaxShaderStorageBlockBytes = LodGpuCapabilityPolicy.MinimumShaderStorageBlockBytes - 1
        };
        c.False(LodGpuCapabilityPolicy.Evaluate(smallSsbo, observedDepth)
                .RegionalIndirectAdvertised,
            "an advertised SSBO path with an insufficient limit remains unavailable");

        LodGpuDepthFacts unknownDepth = observedDepth with
        {
            Convention = LodGpuDepthConvention.Unknown
        };
        c.False(LodGpuCapabilityPolicy.Evaluate(advertised, unknownDepth)
                .HierarchicalDepthAdvertised,
            "an unknown depth convention rejects HZB without rejecting regional MDI");
    }

    static void RenderPathPolicy(Check c)
    {
        LodGpuPathDecision validated = LodGpuCapabilityPolicy.Evaluate(
            FullCapabilities(entryPointsValidated: true, computeValidated: true),
            ConventionalDepth(copyValidated: true));
        LodGpuPathDecision unavailable = LodGpuCapabilityPolicy.Evaluate(
            FullCapabilities(entryPointsValidated: false, computeValidated: false),
            ConventionalDepth(copyValidated: false));

        LodRenderPathSelection off = LodRenderPathPolicy.Evaluate(null, validated);
        c.Eq(LodRenderPathSelection.LegacyPath, off.VisiblePath,
            "the default Phase 1 visible renderer is legacy");
        c.False(off.ShadowEnabled,
            "the default Phase 1 policy does not activate even the metadata shadow");

        LodRenderPathSelection shadow = LodRenderPathPolicy.Evaluate("shadow", validated);
        c.True(shadow.ShadowEnabled,
            "an explicit shadow request activates only after Tier 1 runtime validation");
        c.Eq(LodRenderPathSelection.LegacyPath, shadow.VisiblePath,
            "a validated shadow request still cannot select a visible fast path");
        c.False(LodRenderPathPolicy.Evaluate("shadow", unavailable).ShadowEnabled,
            "a shadow request fails closed when runtime validation is incomplete");
        c.False(LodRenderPathPolicy.Evaluate("future-fast-path", validated).ShadowEnabled,
            "an unknown renderer value fails closed to legacy");
        c.True(LodRenderPathPolicy.RequestsRuntimeValidation("auto"),
            "auto requests the disposable runtime probes");
        c.False(LodRenderPathPolicy.RequestsRuntimeValidation("off"),
            "off does not request advanced runtime probes");
    }

    static void RenderPathLifecycle(Check c)
    {
        var identities = new LodRenderIdentityTracker();
        LodRenderResourceIdentity first = identities.PreparePublication(
            4, 99, hasOpaque: true, hasWater: false);
        identities.Commit(first);
        LodRenderResourceIdentity replacement = identities.PreparePublication(
            4, 99, hasOpaque: true, hasWater: true);
        identities.Commit(replacement);
        c.False(identities.IsCurrent(first),
            "a replaced render identity becomes stale immediately");
        c.True(identities.IsCurrent(replacement),
            "the complete replacement identity becomes current");
        c.True(replacement.SectionRenderGeneration > first.SectionRenderGeneration,
            "section render generations increase across replacements");
        c.True(replacement.OpaqueResourceGeneration > first.OpaqueResourceGeneration
            && replacement.WaterResourceGeneration > replacement.OpaqueResourceGeneration,
            "opaque and water resources receive distinct monotonic generations");
        identities.Clear(4);
        c.Eq(0, identities.Count,
            "an explicit clear removes identities even when the world epoch is unchanged");
        identities.Commit(identities.PreparePublication(
            4, 99, hasOpaque: true, hasWater: true));
        identities.BeginWorld(5);
        c.Eq(0, identities.Count,
            "a world epoch transition rejects every prior section identity as a unit");
        c.False(identities.IsCurrent(replacement),
            "an identity from the previous world cannot become current again");
        c.Throws<InvalidOperationException>(() => identities.PreparePublication(
                4, 99, hasOpaque: true, hasWater: true),
            "a delayed publication cannot move identity ownership back to an old world");

        var legacy = new FakeRenderPath("legacy", ownsGlResources: true);
        var shadow = new LodGpuShadowRenderPath();
        var warnings = new List<string>();
        using (var coordinator = new LodRenderPathCoordinator(
            legacy, shadow, warnings.Add))
        {
            coordinator.Configure(new LodRenderPathSelection(
                LodRenderPathPreference.Shadow, true,
                LodRenderPathSelection.LegacyPath, "test"));
            LodRenderResourceIdentity published = coordinator.Publish(
                7, 123, null, null, 12, 18, 4, 6, 0);
            coordinator.PrepareFrame(new LodRenderFrame(7, 10, 1, 500));
            coordinator.DrawOpaque();
            coordinator.DrawWater();

            c.Eq(1, legacy.Publications,
                "the visible legacy path owns the real publication");
            c.True(shadow.TryGet(123, out LodGpuShadowRenderPath.ShadowSection mirrored)
                && mirrored.Identity == published
                && mirrored.OpaqueIndices == 18
                && mirrored.WaterIndices == 6,
                "the GPU shadow mirrors identity and counts without retaining mesh resources");
            c.False(shadow.OwnsGlResources,
                "the Phase 1 shadow declares no GL resource ownership");
            c.Eq(2, legacy.DrawCalls,
                "opaque and water drawing both stay on legacy");
            c.Eq(0L, shadow.DrawCalls,
                "the coordinator has no Phase 1 route to shadow draw methods");
            c.Eq(0, warnings.Count,
                "a healthy shadow lifecycle does not emit a warning");
        }

        // The in-game switch may turn mirroring on and off after selection, but it may not
        // overrule capability validation, and turning it off must release what it held.
        var switchable = new LodGpuShadowRenderPath();
        legacy = new FakeRenderPath("legacy", ownsGlResources: true);
        warnings.Clear();
        using (var coordinator = new LodRenderPathCoordinator(
            legacy, switchable, warnings.Add))
        {
            var refused = new LodRenderPathSelection(
                LodRenderPathPreference.Shadow, false,
                LodRenderPathSelection.LegacyPath, "driver did not validate");
            var allowed = new LodRenderPathSelection(
                LodRenderPathPreference.Shadow, true,
                LodRenderPathSelection.LegacyPath, "validated");

            coordinator.Configure(refused);
            c.False(coordinator.ShadowEnabled, "an unvalidated driver starts with no shadow");
            c.False(coordinator.SetShadowEnabled(true, refused),
                "the in-game switch cannot enable a shadow the driver did not validate");
            c.False(coordinator.ShadowEnabled,
                "a refused switch leaves mirroring off rather than half on");

            c.True(coordinator.SetShadowEnabled(true, allowed),
                "a validated driver accepts the in-game switch");
            coordinator.Publish(13, 77, null, null, 4, 6, 0, 0, 0);
            c.Eq(1, switchable.Count, "mirroring resumes for publications after the switch");

            c.True(coordinator.SetShadowEnabled(false, allowed),
                "the in-game switch turns mirroring back off");
            c.Eq(0, switchable.Count, "switching off clears everything the shadow held");
            coordinator.Publish(13, 78, null, null, 4, 6, 0, 0, 0);
            c.Eq(0, switchable.Count, "a disabled shadow receives no further publication");
            c.Eq(2, legacy.Publications,
                "the visible path published both sections regardless of the switch");
            c.Eq(0, warnings.Count, "switching the shadow on and off warns about nothing");
        }

        // A shadow that owns real GL resources - the Phase 2 regional arenas - is admitted,
        // and the coordinator still has no route to its draw methods. Only the visible path
        // may be legacy, so a second legacy path cannot be smuggled in as the shadow.
        var resourceShadow = new FakeRenderPath("gpu-shadow", ownsGlResources: true);
        legacy = new FakeRenderPath("legacy", ownsGlResources: true);
        using (var coordinator = new LodRenderPathCoordinator(
            legacy, resourceShadow, warnings.Add))
        {
            coordinator.Configure(new LodRenderPathSelection(
                LodRenderPathPreference.Shadow, true,
                LodRenderPathSelection.LegacyPath, "test"));
            coordinator.PrepareFrame(new LodRenderFrame(11, 1, 1, 500));
            coordinator.DrawOpaque();
            coordinator.DrawWater();
            c.Eq(0, resourceShadow.DrawCalls,
                "a shadow owning GL resources still receives no draw call");
            c.Eq(2, legacy.DrawCalls, "every visible draw stays on legacy");
        }

        c.Throws<ArgumentException>(
            () => new LodRenderPathCoordinator(
                new FakeRenderPath("legacy", ownsGlResources: true),
                new FakeRenderPath("legacy", ownsGlResources: false),
                warnings.Add),
            "a second path claiming the legacy name cannot be installed as the shadow");
        c.Throws<ArgumentException>(
            () => new LodRenderPathCoordinator(
                new FakeRenderPath("gpu-shadow", ownsGlResources: true),
                new FakeRenderPath("legacy", ownsGlResources: false),
                warnings.Add),
            "the visible path cannot be anything but legacy");

        var faultingShadow = new FakeRenderPath(
            "gpu-shadow", ownsGlResources: false) { ThrowOnPublish = true };
        legacy = new FakeRenderPath("legacy", ownsGlResources: true);
        warnings.Clear();
        using (var coordinator = new LodRenderPathCoordinator(
            legacy, faultingShadow, warnings.Add))
        {
            coordinator.Configure(new LodRenderPathSelection(
                LodRenderPathPreference.Shadow, true,
                LodRenderPathSelection.LegacyPath, "test"));
            coordinator.Publish(9, 456, null, null, 1, 3, 0, 0, 0);
            c.Eq(1, legacy.Publications,
                "a shadow publication fault cannot roll back the visible legacy publication");
            c.False(coordinator.ShadowEnabled,
                "the first shadow fault permanently disables mirroring");
            c.Eq(1, warnings.Count,
                "a shadow fault is reported once while legacy continues");
        }
    }

    static void GlStateOwnership(Check c)
    {
        var api = new FakeGlStateApi
        {
            Program = 7,
            GenericSsbo = 11,
            IndexedSsbo = 13,
            IndexedSsbo1 = 37,
            IndexedSsbo2 = 41,
            IndexedSsbo3 = 43,
            DrawFramebuffer = 17,
            ReadFramebuffer = 19,
            ActiveTexture = LodGlStateGuard.Texture0 + 3,
            CopyWriteBuffer = 31,
        };
        api.Textures[LodGlStateGuard.Texture0] = 23;
        api.Textures[api.ActiveTexture] = 29;
        LodGlStateSnapshot incoming = LodGlStateGuard.Capture(
            api,
            LodGlStateMask.Program
            | LodGlStateMask.ShaderStorageBuffer
            | LodGlStateMask.Framebuffers
            | LodGlStateMask.Texture2DUnit0
            | LodGlStateMask.CopyWriteBuffer);

        api.Program = 101;
        api.GenericSsbo = 103;
        api.IndexedSsbo = 107;
        api.IndexedSsbo1 = 109;
        api.IndexedSsbo2 = 111;
        api.IndexedSsbo3 = 113;
        api.DrawFramebuffer = 109;
        api.ReadFramebuffer = 113;
        api.ActiveTexture = LodGlStateGuard.Texture0 + 1;
        api.Textures[LodGlStateGuard.Texture0] = 127;
        api.CopyWriteBuffer = 131;
        api.Operations.Clear();

        c.True(LodGlStateGuard.TryRestore(api, incoming, out string failure),
            "the shared GL guard restores every requested state group exactly");
        c.Eq("", failure, "an exact GL state restore has no failure reason");
        c.Eq(7, api.Program, "the incoming shader program is restored");
        c.Eq(11, api.GenericSsbo,
            "the incoming generic SSBO binding survives indexed restoration");
        c.Eq(13, api.IndexedSsbo, "the incoming indexed SSBO binding is restored");

        // Slot 1 as well as slot 0. Both compute passes bind two buffers, and only slot 0
        // used to be put back, so slot 1 kept pointing into a mod-owned buffer for the rest
        // of the frame. The cull pass makes that a second site, and the buffer it leaves
        // bound there is the one the driver reads draw commands from.
        c.Eq(37, api.IndexedSsbo1, "the incoming indexed SSBO binding at slot one is restored");
        c.Eq(41, api.IndexedSsbo2,
            "the incoming indexed SSBO binding at slot two is restored for live counters");
        c.Eq(43, api.IndexedSsbo3,
            "the incoming indexed SSBO binding at slot three is restored for flicker capture");
        c.Eq(17, api.DrawFramebuffer, "the incoming draw framebuffer is restored");
        c.Eq(19, api.ReadFramebuffer, "the incoming read framebuffer is restored");
        c.Eq(LodGlStateGuard.Texture0 + 3, api.ActiveTexture,
            "the incoming active texture unit is restored");
        c.Eq(23, api.Textures[LodGlStateGuard.Texture0],
            "texture unit zero's 2D binding is restored independently");
        c.Eq(31, api.CopyWriteBuffer,
            "the copy-write binding the arenas transfer through is restored exactly");
        c.True(api.Operations.IndexOf("indexed-ssbo")
            < api.Operations.IndexOf("generic-ssbo"),
            "indexed SSBO restoration occurs before generic restoration");
        c.True(api.Operations.IndexOf("indexed-ssbo-1")
            < api.Operations.IndexOf("generic-ssbo"),
            "and so does slot one, since binding it also moves the generic binding");
    }

    static void DepthConvention(Check c)
    {
        c.Eq(LodGpuDepthConvention.Conventional,
            LodGpuCapabilityPolicy.ClassifyDepthConvention("Lequal", 1.0),
            "less-or-equal with a far clear value is conventional depth");
        c.Eq(LodGpuDepthConvention.Reversed,
            LodGpuCapabilityPolicy.ClassifyDepthConvention("Greater", 0.0),
            "greater with a near clear value is reversed depth");
        c.Eq(LodGpuDepthConvention.Unknown,
            LodGpuCapabilityPolicy.ClassifyDepthConvention("Lequal", 0.0),
            "a contradictory comparison and clear value fails closed to unknown");
        c.Eq(LodGpuDepthConvention.Unknown,
            LodGpuCapabilityPolicy.ClassifyDepthConvention("Always", 1.0),
            "a non-occluding depth function is not guessed");
    }

    static void DelayedTimerRing(Check c)
    {
        var api = new FakeGpuTimerApi();
        using var ring = new LodGpuTimerRing(api, slotCount: 2);
        var cost = new LodPhaseCost();

        c.True(ring.TryBegin(), "an empty delayed timer slot begins");
        ring.End();
        c.Eq(1, ring.PendingCount, "ending a timer publishes one pending result");

        ring.Poll(ref cost);
        c.Eq(0, cost.Calls, "an unavailable GPU result is not consumed");
        c.Eq(0, api.ResultReads, "availability is checked before reading a timer result");

        api.MakeAllAvailable(1_000_000);
        ring.Poll(ref cost);
        c.Eq(1, cost.Calls, "an available delayed result becomes one sample");
        c.True(cost.MaxUs >= 999 && cost.MaxUs <= 1001,
            "GPU nanoseconds convert to the existing microsecond histogram scale");

        c.True(ring.TryBegin(), "the second ring slot begins");
        ring.End();
        c.True(ring.TryBegin(), "the first slot can be reused after its result is consumed");
        ring.End();
        c.False(ring.TryBegin(), "a full pending ring skips timing instead of waiting");
        c.Eq(1, ring.UnavailableSlots, "ring-full timing skips are observable");

        ring.ResetInterval();
        api.MakeAllAvailable(2_000_000);
        ring.Poll(ref cost);
        c.Eq(1, cost.Calls,
            "results issued before an interval reset cannot enter the new interval");

        api.TargetFree = false;
        c.False(ring.TryBegin(), "an occupied time-elapsed target is left untouched");
        c.Eq(1, ring.TargetBusy, "time-query target conflicts are observable");
    }

    static void DelayedTimestampRing(Check c)
    {
        var api = new FakeGpuTimestampApi();
        using var ring = new LodGpuTimestampRing(api, slotCount: 2);
        var cost = new LodPhaseCost();

        api.NextTimestamp = 1_000_000;
        c.True(ring.TryBegin(), "an empty timestamp-pair slot begins inside another timer");
        api.NextTimestamp = 1_125_000;
        ring.End();
        c.Eq(1, ring.PendingCount, "ending a timestamp pair publishes one pending result");

        ring.Poll(ref cost);
        c.Eq(0L, cost.Calls, "an unavailable timestamp pair is not consumed");
        api.ResultsAvailable = true;
        ring.Poll(ref cost);
        c.Eq(1L, cost.Calls, "an available timestamp pair becomes one sample");
        c.True(cost.MaxUs >= 124 && cost.MaxUs <= 126,
            "the timestamp difference, not either absolute timestamp, enters the histogram");

        api.NextTimestamp = 2_000_000;
        c.True(ring.TryBegin(), "a timestamp slot begins for a discarded dispatch");
        api.NextTimestamp = 2_001_000;
        ring.Discard();
        ring.Poll(ref cost);
        c.Eq(1L, cost.Calls, "a dispatch that never ran cannot dilute live cull timing");

        api.ResultsAvailable = false;
        c.True(ring.TryBegin(), "the first pair can be pending");
        ring.End();
        c.True(ring.TryBegin(), "the second pair can be pending");
        ring.End();
        c.False(ring.TryBegin(), "a full timestamp ring skips timing instead of waiting");
        c.Eq(1, ring.UnavailableSlots, "timestamp ring-full skips are observable");
    }

    static LodGpuCapabilityFacts FullCapabilities(
        bool entryPointsValidated, bool computeValidated) => new(
        "test vendor", "test renderer", "4.6", "4.60", 4, 6,
        TimerQueryAdvertised: true,
        ComputeShaderAdvertised: true,
        ShaderStorageAdvertised: true,
        MultiDrawIndirectAdvertised: true,
        ImageLoadStoreAdvertised: true,
        MaxTextureSize: 16_384,
        MaxShaderStorageBlockBytes: 128L * 1024 * 1024,
        MaxShaderStorageBindings: 16,
        MaxComputeWorkGroupInvocations: 1024,
        RequiredEntryPointsValidated: entryPointsValidated,
        MinimalComputeValidated: computeValidated);

    static LodGpuDepthFacts ConventionalDepth(bool copyValidated) => new(
        2, 2, "Texture", 9, 32, 1, "Lequal", 1.0,
        LodGpuDepthConvention.Conventional, copyValidated, "");

    sealed class FakeGpuTimerApi : ILodGpuTimerApi
    {
        readonly Dictionary<int, (bool Available, long Nanoseconds)> results = new();
        int nextId = 1;

        public bool TargetFree = true;
        public int ResultReads { get; private set; }

        public int CreateQuery()
        {
            int id = nextId++;
            results.Add(id, (false, 0));
            return id;
        }

        public bool TimeElapsedTargetIsFree() => TargetFree;
        public void BeginTimeElapsed(int queryId) { }
        public void EndTimeElapsed() { }
        public void RecordTimestamp(int queryId) { }

        public bool TryGetResultNanoseconds(int queryId, out long nanoseconds)
        {
            (bool available, long value) = results[queryId];
            if (!available)
            {
                nanoseconds = 0;
                return false;
            }

            ResultReads++;
            nanoseconds = value;
            return true;
        }

        public void DeleteQuery(int queryId) => results.Remove(queryId);

        public void MakeAllAvailable(long nanoseconds)
        {
            foreach (int id in results.Keys.ToArray()) results[id] = (true, nanoseconds);
        }
    }

    sealed class FakeGpuTimestampApi : ILodGpuTimerApi
    {
        readonly Dictionary<int, long> results = new();
        int nextId = 1;

        public long NextTimestamp { get; set; }
        public bool ResultsAvailable { get; set; }

        public int CreateQuery() => nextId++;
        public bool TimeElapsedTargetIsFree() => true;
        public void BeginTimeElapsed(int queryId) { }
        public void EndTimeElapsed() { }
        public void RecordTimestamp(int queryId) => results[queryId] = NextTimestamp;
        public bool TryGetResultNanoseconds(int queryId, out long nanoseconds)
        {
            nanoseconds = ResultsAvailable ? results[queryId] : 0;
            return ResultsAvailable;
        }
        public void DeleteQuery(int queryId) => results.Remove(queryId);
    }

    sealed class FakeRenderPath : ILodRenderPath
    {
        public string Name { get; }
        public bool OwnsGlResources { get; }
        public int Publications { get; private set; }
        public int DrawCalls { get; private set; }
        public bool ThrowOnPublish { get; init; }

        public FakeRenderPath(string name, bool ownsGlResources)
        {
            Name = name;
            OwnsGlResources = ownsGlResources;
        }

        public void Publish(in LodRenderPublication publication)
        {
            Publications++;
            if (ThrowOnPublish) throw new InvalidOperationException("test shadow fault");
        }
        public void Remove(long worldEpoch, long sectionKey) { }
        public void PrepareFrame(in LodRenderFrame frame) { }
        public void DrawOpaque() => DrawCalls++;
        public void DrawWater() => DrawCalls++;
        public void Clear(long worldEpoch) { }
        public void Dispose() { }
    }

    sealed class FakeGlStateApi : ILodGlStateApi
    {
        public int Program;
        public int GenericSsbo;
        public int IndexedSsbo;
        public int IndexedSsbo1;
        public int IndexedSsbo2;
        public int IndexedSsbo3;
        public int DrawFramebuffer;
        public int ReadFramebuffer;
        public int ActiveTexture;
        public int CopyWriteBuffer;
        public int VertexArray;
        public int DrawIndirectBuffer;
        public readonly Dictionary<int, int> Textures = new();
        public readonly List<string> Operations = new();

        public int GetProgram() => Program;
        public void UseProgram(int value) { Program = value; Operations.Add("program"); }
        public int GetGenericShaderStorageBuffer() => GenericSsbo;
        public int GetIndexedShaderStorageBuffer0() => IndexedSsbo;
        public void BindIndexedShaderStorageBuffer0(int value)
        {
            IndexedSsbo = value;
            GenericSsbo = value;
            Operations.Add("indexed-ssbo");
        }
        public int GetIndexedShaderStorageBuffer1() => IndexedSsbo1;
        public void BindIndexedShaderStorageBuffer1(int value)
        {
            IndexedSsbo1 = value;
            GenericSsbo = value;
            Operations.Add("indexed-ssbo-1");
        }
        public int GetIndexedShaderStorageBuffer2() => IndexedSsbo2;
        public void BindIndexedShaderStorageBuffer2(int value)
        {
            IndexedSsbo2 = value;
            GenericSsbo = value;
            Operations.Add("indexed-ssbo-2");
        }
        public int GetIndexedShaderStorageBuffer3() => IndexedSsbo3;
        public void BindIndexedShaderStorageBuffer3(int value)
        {
            IndexedSsbo3 = value;
            GenericSsbo = value;
            Operations.Add("indexed-ssbo-3");
        }
        public void BindGenericShaderStorageBuffer(int value)
        {
            GenericSsbo = value;
            Operations.Add("generic-ssbo");
        }
        public int GetDrawFramebuffer() => DrawFramebuffer;
        public int GetReadFramebuffer() => ReadFramebuffer;
        public void BindDrawFramebuffer(int value)
        {
            DrawFramebuffer = value;
            Operations.Add("draw-fbo");
        }
        public void BindReadFramebuffer(int value)
        {
            ReadFramebuffer = value;
            Operations.Add("read-fbo");
        }
        public int GetActiveTexture() => ActiveTexture;
        public void SetActiveTexture(int value)
        {
            ActiveTexture = value;
            Operations.Add("active-texture");
        }
        public int GetTexture2D() => Textures.GetValueOrDefault(ActiveTexture);
        public void BindTexture2D(int value)
        {
            Textures[ActiveTexture] = value;
            Operations.Add("texture-2d");
        }
        public int GetCopyWriteBuffer() => CopyWriteBuffer;
        public void BindCopyWriteBuffer(int value)
        {
            CopyWriteBuffer = value;
            Operations.Add("copy-write-buffer");
        }
        public int GetVertexArray() => VertexArray;
        public void BindVertexArray(int value)
        {
            VertexArray = value;
            Operations.Add("vertex-array");
        }
        public int GetDrawIndirectBuffer() => DrawIndirectBuffer;
        public void BindDrawIndirectBuffer(int value)
        {
            DrawIndirectBuffer = value;
            Operations.Add("draw-indirect-buffer");
        }
    }

    /// <summary>
    /// A timed pass that then did not happen must leave no trace in the average.
    ///
    /// The 2026-08-24 log reported the depth pyramid at 4.8us over 256,368 timed builds when
    /// 50,733 builds had actually run; the rest were frames with the window minimised, each
    /// contributing a near-zero sample, and they dragged a genuine 24us figure down fivefold.
    /// The gate for the whole phase is that number, so a sample that means nothing is worse
    /// than a missing one.
    /// </summary>
    static void DiscardedTimingNeverReachesTheTotal(Check c)
    {
        var api = new FakeTimerApi();
        var ring = new LodGpuTimerRing(api, slotCount: 4);
        LodPhaseCost cost = default;

        // One real sample, so there is something to dilute.
        c.True(ring.TryBegin(), "a free ring starts a query");
        api.NextResultNanoseconds = 24_000;
        ring.End();
        ring.Poll(ref cost);
        c.Eq(1L, cost.Calls, "the real build is counted");

        // Then several that were timed and abandoned.
        for (int i = 0; i < 3; i++)
        {
            c.True(ring.TryBegin(), "a discarded query still opens normally");
            api.NextResultNanoseconds = 5;
            ring.Discard();
            ring.Poll(ref cost);
        }

        c.Eq(1L, cost.Calls,
            "discarded builds add no samples, so the average stays an average of real work");

        // And the slots they used are released rather than leaked, or the ring would stop
        // timing altogether after a handful of minimised frames.
        c.Eq(0, ring.PendingCount, "discarded slots are freed by the poll like any other");
        c.True(ring.TryBegin(), "the ring still accepts work after discards");
        api.NextResultNanoseconds = 30_000;
        ring.End();
        ring.Poll(ref cost);
        c.Eq(2L, cost.Calls, "a later real build is still counted");
    }

    sealed class FakeTimerApi : ILodGpuTimerApi
    {
        int next = 1;
        readonly Dictionary<int, long> results = new();
        public long NextResultNanoseconds { get; set; } = 1000;

        public int CreateQuery() => next++;
        public bool TimeElapsedTargetIsFree() => true;
        public void BeginTimeElapsed(int queryId) { }
        public void EndTimeElapsed() { }
        public void RecordTimestamp(int queryId) { }

        public bool TryGetResultNanoseconds(int queryId, out long nanoseconds)
        {
            if (!results.ContainsKey(queryId)) results[queryId] = NextResultNanoseconds;
            nanoseconds = results[queryId];
            results.Remove(queryId);
            return true;
        }

        public void DeleteQuery(int queryId) => results.Remove(queryId);
    }
}
