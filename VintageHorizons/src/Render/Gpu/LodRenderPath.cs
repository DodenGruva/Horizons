using Vintagestory.API.Client;

namespace VintageHorizons;

internal enum LodRenderPathPreference
{
    Off,
    Shadow,
    Auto,
    Invalid,
}

internal readonly record struct LodRenderPathSelection(
    LodRenderPathPreference Preference,
    bool ShadowEnabled,
    string VisiblePath,
    string Reason)
{
    public const string LegacyPath = "legacy";
}

/// <summary>
/// Phase 1 selection policy. Every outcome deliberately keeps legacy as the visible
/// renderer; a validated opt-in may only activate the CPU-only metadata shadow.
/// </summary>
internal static class LodRenderPathPolicy
{
    public static LodRenderPathPreference Parse(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals("off", StringComparison.OrdinalIgnoreCase)
            ? LodRenderPathPreference.Off
            : value.Equals("shadow", StringComparison.OrdinalIgnoreCase)
                ? LodRenderPathPreference.Shadow
                : value.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? LodRenderPathPreference.Auto
                    : LodRenderPathPreference.Invalid;

    public static bool RequestsRuntimeValidation(string? value)
    {
        LodRenderPathPreference preference = Parse(value);
        return preference is LodRenderPathPreference.Shadow or LodRenderPathPreference.Auto;
    }

    public static LodRenderPathSelection Evaluate(
        string? value, LodGpuPathDecision capabilities)
    {
        LodRenderPathPreference preference = Parse(value);
        if (preference == LodRenderPathPreference.Invalid)
        {
            return new(preference, false, LodRenderPathSelection.LegacyPath,
                $"unknown VINTAGEHORIZONS_GPU_RENDERER value '{value}'; shadow disabled");
        }
        if (preference == LodRenderPathPreference.Off)
        {
            return new(preference, false, LodRenderPathSelection.LegacyPath,
                "GPU renderer shadow is off");
        }
        if (!capabilities.Tier1RuntimeValidated)
        {
            return new(preference, false, LodRenderPathSelection.LegacyPath,
                "GPU renderer shadow requested, but Tier 1 runtime validation did not pass");
        }

        return new(preference, true, LodRenderPathSelection.LegacyPath,
            "validated CPU-only GPU renderer shadow enabled; visible drawing remains legacy");
    }
}

internal readonly record struct LodRenderResourceIdentity(
    long WorldEpoch,
    long SectionKey,
    long SectionRenderGeneration,
    long OpaqueResourceGeneration,
    long WaterResourceGeneration);

internal sealed class LodRenderIdentityTracker
{
    readonly Dictionary<long, LodRenderResourceIdentity> current = new();
    long worldEpoch = long.MinValue;
    long nextSectionGeneration;
    long nextResourceGeneration;

    public long WorldEpoch => worldEpoch;
    public int Count => current.Count;

    public void BeginWorld(long epoch)
    {
        ValidateEpoch(epoch);
        if (worldEpoch == epoch) return;
        current.Clear();
        worldEpoch = epoch;
    }

    public void ValidateEpoch(long epoch)
    {
        if (worldEpoch != long.MinValue && epoch < worldEpoch)
            throw new InvalidOperationException("render operation belongs to a stale world epoch");
    }

    public LodRenderResourceIdentity PreparePublication(
        long epoch, long key, bool hasOpaque, bool hasWater)
    {
        BeginWorld(epoch);
        return new LodRenderResourceIdentity(
            epoch,
            key,
            ++nextSectionGeneration,
            hasOpaque ? ++nextResourceGeneration : 0,
            hasWater ? ++nextResourceGeneration : 0);
    }

    public void Commit(LodRenderResourceIdentity identity)
    {
        if (identity.WorldEpoch != worldEpoch)
            throw new InvalidOperationException("render publication belongs to a stale world epoch");
        current[identity.SectionKey] = identity;
    }

    public bool Remove(long epoch, long key)
    {
        BeginWorld(epoch);
        return current.Remove(key);
    }

    public void Clear(long epoch)
    {
        ValidateEpoch(epoch);
        current.Clear();
        worldEpoch = epoch;
    }

    public bool IsCurrent(LodRenderResourceIdentity identity) =>
        identity.WorldEpoch == worldEpoch
        && current.TryGetValue(identity.SectionKey, out LodRenderResourceIdentity observed)
        && observed == identity;

    public bool TryGet(long key, out LodRenderResourceIdentity identity) =>
        current.TryGetValue(key, out identity);
}

/// <summary>
/// The CPU arrays the legacy upload was handed, borrowed for the duration of one
/// publication. A path that wants these bytes must copy them: the mesher's arrays are its
/// own, they can be larger than the live counts, and nothing may outlive the call.
/// </summary>
internal readonly record struct LodRenderGeometry(
    float[]? Xyz,
    byte[]? Rgba,
    int[]? Indices,
    uint[]? PackedQuads = null,
    int PackedQuadCount = 0,
    uint[]? ClusteredPackedQuads = null,
    LodPackedCluster[]? PackedClusters = null);

internal readonly record struct LodRenderPublication(
    LodRenderResourceIdentity Identity,
    MeshRef? Opaque,
    MeshRef? Water,
    int OpaqueVertices,
    int OpaqueIndices,
    int WaterVertices,
    int WaterIndices,
    byte AssumedCoveredSides,
    LodRenderGeometry OpaqueGeometry = default,
    LodSectionHeights Heights = default);

internal readonly record struct LodRenderFrame(
    long WorldEpoch,
    long FrameNumber,
    int SelectedSectionCount,
    float CullDistanceSquared);

/// <summary>Narrow lifecycle shared by the established renderer and later GPU paths.</summary>
internal interface ILodRenderPath : IDisposable
{
    string Name { get; }
    bool OwnsGlResources { get; }
    void Publish(in LodRenderPublication publication);
    void Remove(long worldEpoch, long sectionKey);
    void PrepareFrame(in LodRenderFrame frame);
    void DrawOpaque();
    void DrawWater();
    void Clear(long worldEpoch);
}

internal sealed class LodLegacyRenderPath : ILodRenderPath
{
    readonly Action<LodRenderPublication> publish;
    readonly Action<long, long> remove;
    readonly Action<LodRenderFrame> prepareFrame;
    readonly Action drawOpaque;
    readonly Action drawWater;
    readonly Action<long> clear;
    readonly Action dispose;

    public string Name => LodRenderPathSelection.LegacyPath;
    public bool OwnsGlResources => true;

    public LodLegacyRenderPath(
        Action<LodRenderPublication> publish,
        Action<long, long> remove,
        Action<LodRenderFrame> prepareFrame,
        Action drawOpaque,
        Action drawWater,
        Action<long> clear,
        Action? dispose = null)
    {
        this.publish = publish;
        this.remove = remove;
        this.prepareFrame = prepareFrame;
        this.drawOpaque = drawOpaque;
        this.drawWater = drawWater;
        this.clear = clear;
        this.dispose = dispose ?? (() => { });
    }

    public void Publish(in LodRenderPublication publication) => publish(publication);
    public void Remove(long worldEpoch, long sectionKey) => remove(worldEpoch, sectionKey);
    public void PrepareFrame(in LodRenderFrame frame) => prepareFrame(frame);
    public void DrawOpaque() => drawOpaque();
    public void DrawWater() => drawWater();
    public void Clear(long worldEpoch) => clear(worldEpoch);
    public void Dispose() => dispose();
}

/// <summary>
/// The shadow mirror. It always owns identities and immutable counts, and never a MeshRef.
/// From Phase 2 it may additionally own regional arena buffers, which are filled and
/// retired but never drawn: its draw methods exist to satisfy the common lifecycle and the
/// coordinator has no route to them.
/// </summary>
internal sealed class LodGpuShadowRenderPath : ILodRenderPath
{
    internal readonly record struct ShadowSection(
        LodRenderResourceIdentity Identity,
        int OpaqueVertices,
        int OpaqueIndices,
        int WaterVertices,
        int WaterIndices);

    readonly Dictionary<long, ShadowSection> sections = new();
    LodGpuGeometryMirror? mirror;

    public string Name => "gpu-shadow";
    public bool OwnsGlResources => mirror != null;
    public LodGpuGeometryMirror? Mirror => mirror;
    public int Count => sections.Count;
    public long PreparedFrames { get; private set; }
    public long DrawCalls { get; private set; }

    /// <summary>
    /// Attaches the regional arenas. Deferred until path selection because the arenas are
    /// real GL resources and must not exist before capability validation has passed.
    /// </summary>
    public void AttachMirror(LodGpuGeometryMirror value)
    {
        mirror?.Dispose();
        mirror = value;
    }

    /// <summary>Releases the arenas and every buffer they hold.</summary>
    public void DetachMirror()
    {
        mirror?.Dispose();
        mirror = null;
    }

    public void Publish(in LodRenderPublication publication)
    {
        sections[publication.Identity.SectionKey] = new ShadowSection(
            publication.Identity,
            publication.OpaqueVertices,
            publication.OpaqueIndices,
            publication.WaterVertices,
            publication.WaterIndices);
        mirror?.Mirror(publication);
    }

    public void Remove(long worldEpoch, long sectionKey)
    {
        sections.Remove(sectionKey);
        mirror?.Remove(sectionKey);
    }

    public void PrepareFrame(in LodRenderFrame frame)
    {
        PreparedFrames++;
        mirror?.Reclaim();
    }

    public void DrawOpaque() => DrawCalls++;
    public void DrawWater() => DrawCalls++;

    public void Clear(long worldEpoch)
    {
        sections.Clear();
        mirror?.Clear();
    }

    public bool TryGet(long key, out ShadowSection section) => sections.TryGetValue(key, out section);

    public void Dispose()
    {
        sections.Clear();
        mirror?.Dispose();
        mirror = null;
    }
}

/// <summary>
/// The dual-path seam. Visible calls always go to legacy: <see cref="DrawOpaque"/> and
/// <see cref="DrawWater"/> name the visible path directly, so no configuration can route a
/// frame to the shadow. Shadow failures are isolated and permanently disable mirroring for
/// the world rather than affecting visible terrain.
/// </summary>
internal sealed class LodRenderPathCoordinator : IDisposable
{
    readonly ILodRenderPath visible;
    readonly ILodRenderPath shadow;
    readonly Action<string> warn;
    readonly LodRenderIdentityTracker identities = new();
    bool configured;
    bool disposed;

    public bool ShadowEnabled { get; private set; }
    public string VisiblePath => visible.Name;
    public LodRenderIdentityTracker Identities => identities;

    public LodRenderPathCoordinator(
        ILodRenderPath visible, ILodRenderPath shadow, Action<string> warn)
    {
        this.visible = visible;
        this.shadow = shadow;
        this.warn = warn;
        if (visible.Name != LodRenderPathSelection.LegacyPath)
            throw new ArgumentException("the visible path must be legacy", nameof(visible));
        if (visible.Name == shadow.Name || ReferenceEquals(visible, shadow))
            throw new ArgumentException("the shadow path must be distinct from legacy", nameof(shadow));
    }

    public void Configure(LodRenderPathSelection selection)
    {
        if (configured) return;
        configured = true;
        ShadowEnabled = selection.ShadowEnabled;
    }

    /// <summary>
    /// Turns mirroring on or off after selection, for the in-game diagnostic switch. The
    /// selection still has to say the shadow is allowed, so a context that failed capability
    /// validation cannot be talked into mirroring by a chat command. Visible drawing is
    /// unaffected either way: this only decides whether the shadow is fed.
    /// </summary>
    public bool SetShadowEnabled(bool enabled, LodRenderPathSelection selection)
    {
        if (enabled && !selection.ShadowEnabled) return false;
        if (ShadowEnabled == enabled) return true;

        ShadowEnabled = enabled;
        if (!enabled) TryClearShadow();
        return true;
    }

    void TryClearShadow()
    {
        try { shadow.Clear(identities.WorldEpoch); }
        catch (Exception e)
        {
            warn("[VintageHorizons] GPU renderer shadow did not clear cleanly; visible legacy "
                + "rendering is unchanged: " + e.Message);
        }
    }

    public LodRenderResourceIdentity Publish(
        long worldEpoch,
        long sectionKey,
        MeshRef? opaque,
        MeshRef? water,
        int opaqueVertices,
        int opaqueIndices,
        int waterVertices,
        int waterIndices,
        byte assumedCoveredSides,
        LodRenderGeometry opaqueGeometry = default,
        LodSectionHeights heights = default)
    {
        AdvanceWorld(worldEpoch);
        LodRenderResourceIdentity identity = identities.PreparePublication(
            worldEpoch, sectionKey, opaque != null, water != null);
        var publication = new LodRenderPublication(
            identity, opaque, water,
            opaqueVertices, opaqueIndices, waterVertices, waterIndices,
            assumedCoveredSides, opaqueGeometry, heights);

        visible.Publish(publication);
        identities.Commit(identity);
        TryShadow(() => shadow.Publish(publication), "publication");
        return identity;
    }

    public void Remove(long worldEpoch, long sectionKey)
    {
        AdvanceWorld(worldEpoch);
        visible.Remove(worldEpoch, sectionKey);
        identities.Remove(worldEpoch, sectionKey);
        TryShadow(() => shadow.Remove(worldEpoch, sectionKey), "removal");
    }

    public void PrepareFrame(in LodRenderFrame frame)
    {
        long epoch = frame.WorldEpoch;
        AdvanceWorld(epoch);
        visible.PrepareFrame(frame);
        LodRenderFrame copy = frame;
        TryShadow(() => shadow.PrepareFrame(copy), "frame preparation");
    }

    public void DrawOpaque() => visible.DrawOpaque();
    public void DrawWater() => visible.DrawWater();

    public void Clear(long worldEpoch)
    {
        identities.ValidateEpoch(worldEpoch);
        visible.Clear(worldEpoch);
        identities.Clear(worldEpoch);
        TryShadow(() => shadow.Clear(worldEpoch), "clear");
    }

    void AdvanceWorld(long worldEpoch)
    {
        bool changed = identities.WorldEpoch != worldEpoch;
        identities.BeginWorld(worldEpoch);
        if (changed)
        {
            TryShadow(() => shadow.Clear(worldEpoch), "world transition");
        }
    }

    void TryShadow(Action action, string operation)
    {
        if (!ShadowEnabled) return;
        try { action(); }
        catch (Exception e)
        {
            ShadowEnabled = false;
            try { shadow.Clear(identities.WorldEpoch); }
            catch { /* The failed CPU-only shadow has no visible authority or GL state. */ }
            warn($"[VintageHorizons] GPU renderer shadow disabled after {operation} failed; "
                + "visible legacy rendering is unchanged: " + e.Message);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { shadow.Dispose(); }
        finally { visible.Dispose(); }
        ShadowEnabled = false;
    }
}
