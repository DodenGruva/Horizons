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
        BenchmarkWiring(c);
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
            && runner.Contains("[switch]$SeedOnly", StringComparison.Ordinal),
            "the Windows runner pins and records delayed GPU timing and renderer controls");
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
            DrawFramebuffer = 17,
            ReadFramebuffer = 19,
            ActiveTexture = LodGlStateGuard.Texture0 + 3,
        };
        api.Textures[LodGlStateGuard.Texture0] = 23;
        api.Textures[api.ActiveTexture] = 29;
        LodGlStateSnapshot incoming = LodGlStateGuard.Capture(
            api,
            LodGlStateMask.Program
            | LodGlStateMask.ShaderStorageBuffer
            | LodGlStateMask.Framebuffers
            | LodGlStateMask.Texture2DUnit0);

        api.Program = 101;
        api.GenericSsbo = 103;
        api.IndexedSsbo = 107;
        api.DrawFramebuffer = 109;
        api.ReadFramebuffer = 113;
        api.ActiveTexture = LodGlStateGuard.Texture0 + 1;
        api.Textures[LodGlStateGuard.Texture0] = 127;
        api.Operations.Clear();

        c.True(LodGlStateGuard.TryRestore(api, incoming, out string failure),
            "the shared GL guard restores every requested state group exactly");
        c.Eq("", failure, "an exact GL state restore has no failure reason");
        c.Eq(7, api.Program, "the incoming shader program is restored");
        c.Eq(11, api.GenericSsbo,
            "the incoming generic SSBO binding survives indexed restoration");
        c.Eq(13, api.IndexedSsbo, "the incoming indexed SSBO binding is restored");
        c.Eq(17, api.DrawFramebuffer, "the incoming draw framebuffer is restored");
        c.Eq(19, api.ReadFramebuffer, "the incoming read framebuffer is restored");
        c.Eq(LodGlStateGuard.Texture0 + 3, api.ActiveTexture,
            "the incoming active texture unit is restored");
        c.Eq(23, api.Textures[LodGlStateGuard.Texture0],
            "texture unit zero's 2D binding is restored independently");
        c.True(api.Operations.IndexOf("indexed-ssbo")
            < api.Operations.IndexOf("generic-ssbo"),
            "indexed SSBO restoration occurs before generic restoration");
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
        public int DrawFramebuffer;
        public int ReadFramebuffer;
        public int ActiveTexture;
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
    }
}
