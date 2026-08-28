using System.Reflection;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace VintageHorizons.Render;

/// <summary>
/// Moves only vanilla terrain's visual edge fade beyond terrain the engine can submit,
/// without changing client settings, atmosphere, shader assets, draw range or streaming.
///
/// Vintage Story exposes the ambient inputs but no event between a vanilla renderer's
/// shader activation and draw. A shader-activation hook avoids the per-uniform hot path,
/// and a second hook catches the liquid-depth program's explicit late distance upload.
/// They survive shader reloads because the methods belong to the stable program classes,
/// not their GL objects. Each terrain pass fails open independently.
/// </summary>
internal sealed class VanillaHorizonEffects : IDisposable
{
    internal const string HarmonyId = "vintagehorizons.horizoneffects";

    static VanillaHorizonEffects? active;

    readonly ICoreClientAPI capi;
    readonly Harmony harmony = new(HarmonyId);
    readonly List<MethodBase> patchedMethods = new();
    readonly HashSet<string> failedPasses = new(StringComparer.Ordinal);
    ILogger? logger;
    bool installed;

    internal VanillaHorizonEffects(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    internal IReadOnlyList<MethodBase> PatchedMethods => patchedMethods;
    internal bool PassFailed(string passName) => failedPasses.Contains(passName);

    internal bool Install(ILogger logger)
    {
        if (installed) return true;
        this.logger = logger;

        MethodInfo? shaderUse = AccessTools.Method(
            typeof(ShaderProgramBase), nameof(ShaderProgramBase.Use));
        MethodInfo? liquidDistance = AccessTools.PropertySetter(
            typeof(ShaderProgramChunkliquiddepth),
            nameof(ShaderProgramChunkliquiddepth.ViewDistance));
        MethodInfo? afterUse = AccessTools.Method(
            typeof(VanillaHorizonEffects), nameof(AfterShaderUse));
        MethodInfo? beforeLiquidDistance = AccessTools.Method(
            typeof(VanillaHorizonEffects), nameof(BeforeLiquidDistance));

        if (Interlocked.CompareExchange(ref active, this, null) != null)
        {
            logger.Error(
                "Vintage Horizons found a second horizon-effects owner; vanilla horizon effects remain active.");
            return false;
        }

        TryInstallHook(shaderUse, afterUse, postfix: true, "terrain shader activation");
        TryInstallHook(
            liquidDistance, beforeLiquidDistance, postfix: false,
            "liquid-depth distance upload");

        if (patchedMethods.Count == 0)
        {
            Interlocked.CompareExchange(ref active, null, this);
            logger.Error(
                "Vintage Horizons could not install terrain-wall suppression; vanilla behavior remains active.");
            return false;
        }

        installed = true;
        logger.Notification(
            "Vanilla terrain render-threshold wall suppressed; atmospheric and non-terrain fades remain unchanged");
        return true;
    }

    void TryInstallHook(
        MethodInfo? target, MethodInfo? patch, bool postfix, string description)
    {
        if (target == null || patch == null)
        {
            logger?.Error(
                "Vintage Horizons could not find the {0} hook; that pass keeps vanilla behavior.",
                description);
            return;
        }

        try
        {
            var harmonyMethod = new HarmonyMethod(patch);
            if (postfix) harmony.Patch(target, postfix: harmonyMethod);
            else harmony.Patch(target, prefix: harmonyMethod);
            patchedMethods.Add(target);
        }
        catch (Exception e)
        {
            try
            {
                // Harmony patching is expected to be atomic, but exact-ID cleanup keeps a
                // partially applied target from escaping this independently failing seam.
                harmony.Unpatch(target, HarmonyPatchType.All, HarmonyId);
            }
            catch
            {
            }
            logger?.Error(
                "Vintage Horizons could not install the {0} hook; that pass keeps vanilla behavior: {1}",
                description, e.Message);
        }
    }

    static void AfterShaderUse(ShaderProgramBase __instance)
    {
        VanillaHorizonEffects? owner = Volatile.Read(ref active);
        owner?.TryMoveTerrainFade(__instance);
    }

    internal void TryMoveTerrainFade(ShaderProgramBase program)
    {
        string? passName = program.PassName;
        if (passName == null || !VanillaHorizonPolicy.UsesVanillaDistanceFade(passName)
            || failedPasses.Contains(passName))
        {
            return;
        }

        try
        {
            if (!program.HasUniform(VanillaHorizonPolicy.ViewDistanceUniform))
            {
                FailPass(passName, "the active shader variant has no viewDistance uniform");
                return;
            }

            float configuredDistance = capi.Settings.Int["viewDistance"];
            if (!VanillaHorizonPolicy.TryDistanceBeyondDrawableTerrain(
                    configuredDistance, out float replacement))
            {
                FailPass(passName, "the configured view distance is invalid");
                return;
            }

            program.Uniform(VanillaHorizonPolicy.ViewDistanceUniform, replacement);
        }
        catch (Exception e)
        {
            FailPass(passName, e.Message);
        }
    }

    static void BeforeLiquidDistance(
        ShaderProgramChunkliquiddepth __instance, ref float value)
    {
        VanillaHorizonEffects? owner = Volatile.Read(ref active);
        string? passName = __instance.PassName;
        if (owner == null || passName == null || passName != "chunkliquiddepth"
            || owner.failedPasses.Contains(passName))
        {
            return;
        }

        try
        {
            if (!VanillaHorizonPolicy.TryDistanceBeyondDrawableTerrain(
                    value, out float replacement))
            {
                owner.FailPass(passName, "the liquid-depth view distance is invalid");
                return;
            }

            value = replacement;
        }
        catch (Exception e)
        {
            owner.FailPass(passName, e.Message);
        }
    }

    void FailPass(string passName, string reason)
    {
        if (!failedPasses.Add(passName)) return;

        try
        {
            logger?.Error(
                "Vintage Horizons left terrain pass '{0}' unchanged after its wall-suppression override failed: {1}",
                passName, reason);
        }
        catch
        {
            // A diagnostic failure must not turn a fail-open shader callback into a render failure.
        }
    }

    public void Dispose()
    {
        // Disable the behavior first. If teardown happens on an unusual engine thread and
        // Harmony refuses the structural unpatch, every subsequent call is still a no-op.
        Interlocked.CompareExchange(ref active, null, this);

        if (!installed) return;
        try
        {
            UnpatchInstalledMethods();
        }
        catch (Exception cleanupError)
        {
            // Active is already null, so even a structural hook Harmony could not remove
            // is inert. Do not let unusual engine-thread teardown skip later cleanup.
            try
            {
                logger?.Error(
                    "Vintage Horizons terrain-wall hook cleanup was refused but remains inactive: {0}",
                    cleanupError.Message);
            }
            catch
            {
            }
        }
        installed = false;
    }

    void UnpatchInstalledMethods()
    {
        foreach (MethodBase method in patchedMethods)
            harmony.Unpatch(method, HarmonyPatchType.All, HarmonyId);
        patchedMethods.Clear();
    }
}
