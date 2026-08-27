using System.Reflection;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace VintageHorizons.Render;

/// <summary>
/// Rewrites two engine-owned render values without changing client settings, ambient
/// modifiers, shader assets, draw range or chunk streaming.
///
/// Vintage Story exposes the ambient inputs but no event between a vanilla renderer's
/// shader activation and draw. Three narrow hooks avoid the per-draw Uniform hot path:
/// one runs after the once-per-frame ambient blend, one after shader activation, and one
/// catches the liquid-depth program's explicit late distance upload. They survive shader
/// reloads because the methods belong to the stable program classes, not their GL objects.
/// </summary>
internal sealed class VanillaHorizonEffects : IDisposable
{
    const string HarmonyId = "vintagehorizons.horizoneffects";

    // Read the game's declared default once. Current base density may later be raised by
    // another system; policy removes no more than this original clear-air contribution.
    static readonly float VanillaBaseFogDensity =
        AmbientModifier.DefaultAmbient.FogDensity.Value;

    static VanillaHorizonEffects? active;

    readonly ICoreClientAPI capi;
    readonly Func<float> farTerrainDistance;
    readonly Harmony harmony = new(HarmonyId);
    readonly List<MethodBase> patchedMethods = new();
    bool installed;

    internal VanillaHorizonEffects(ICoreClientAPI capi, Func<float> farTerrainDistance)
    {
        this.capi = capi;
        this.farTerrainDistance = farTerrainDistance;
    }

    internal bool Install(ILogger logger)
    {
        if (installed) return true;

        MethodInfo? ambientUpdate = AccessTools.Method(
            typeof(AmbientManager), nameof(AmbientManager.UpdateAmbient),
            new[] { typeof(float) });
        MethodInfo? shaderUse = AccessTools.Method(
            typeof(ShaderProgramBase), nameof(ShaderProgramBase.Use));
        MethodInfo? liquidDistance = AccessTools.PropertySetter(
            typeof(ShaderProgramChunkliquiddepth),
            nameof(ShaderProgramChunkliquiddepth.ViewDistance));
        MethodInfo? afterAmbient = AccessTools.Method(
            typeof(VanillaHorizonEffects), nameof(AfterAmbientUpdate));
        MethodInfo? afterUse = AccessTools.Method(
            typeof(VanillaHorizonEffects), nameof(AfterShaderUse));
        MethodInfo? beforeLiquidDistance = AccessTools.Method(
            typeof(VanillaHorizonEffects), nameof(BeforeLiquidDistance));

        if (ambientUpdate == null || shaderUse == null || liquidDistance == null
            || afterAmbient == null || afterUse == null || beforeLiquidDistance == null)
        {
            logger.Error(
                "Vintage Horizons could not find the horizon-effect hooks; vanilla horizon effects remain active.");
            return false;
        }

        if (Interlocked.CompareExchange(ref active, this, null) != null)
        {
            logger.Error(
                "Vintage Horizons found a second horizon-effects owner; vanilla horizon effects remain active.");
            return false;
        }

        try
        {
            harmony.Patch(ambientUpdate, postfix: new HarmonyMethod(afterAmbient));
            patchedMethods.Add(ambientUpdate);
            harmony.Patch(shaderUse, postfix: new HarmonyMethod(afterUse));
            patchedMethods.Add(shaderUse);
            harmony.Patch(liquidDistance, prefix: new HarmonyMethod(beforeLiquidDistance));
            patchedMethods.Add(liquidDistance);
            installed = true;
            logger.Notification(
                "Vanilla render-distance fog and smoothing fade suppressed; weather and local fog remain active");
            return true;
        }
        catch (Exception e)
        {
            Interlocked.CompareExchange(ref active, null, this);
            try
            {
                UnpatchInstalledMethods();
            }
            catch (Exception cleanupError)
            {
                // Active is already null, so even a structural hook that Harmony could
                // not remove is inert. Keep the original install failure as the useful
                // diagnosis, but make the cleanup limitation visible too.
                logger.Error(
                    "Vintage Horizons horizon-hook cleanup was refused but remains inactive: {0}",
                    cleanupError.Message);
            }
            logger.Error(
                "Vintage Horizons could not install horizon suppression; vanilla behavior remains active: {0}",
                e.Message);
            return false;
        }
    }

    static void AfterAmbientUpdate(AmbientManager __instance)
    {
        VanillaHorizonEffects? owner = Volatile.Read(ref active);
        owner?.RemoveVanillaBaseFog(__instance);
    }

    void RemoveVanillaBaseFog(AmbientManager ambient)
    {
        float retainedBaseShare = 1f;
        foreach (KeyValuePair<string, AmbientModifier> entry in ambient.CurrentModifiers)
        {
            retainedBaseShare = VanillaHorizonPolicy.RetainBaseFog(
                retainedBaseShare, entry.Value.FogDensity.Weight);
        }

        ambient.BlendedFogDensity = VanillaHorizonPolicy.DensityWithoutVanillaBase(
            ambient.BlendedFogDensity,
            ambient.Base.FogDensity.Value,
            VanillaBaseFogDensity,
            retainedBaseShare);
    }

    static void AfterShaderUse(ShaderProgramBase __instance)
    {
        VanillaHorizonEffects? owner = Volatile.Read(ref active);
        owner?.RemoveVanillaDistanceFade(__instance);
    }

    void RemoveVanillaDistanceFade(ShaderProgramBase program)
    {
        if (!VanillaHorizonPolicy.UsesVanillaDistanceFade(program.PassName)
            || !program.HasUniform(VanillaHorizonPolicy.ViewDistanceUniform))
        {
            return;
        }

        float configuredDistance = capi.Settings.Int["viewDistance"];
        program.Uniform(
            VanillaHorizonPolicy.ViewDistanceUniform,
            VanillaHorizonPolicy.DistanceWithoutFade(
                configuredDistance, farTerrainDistance()));
    }

    static void BeforeLiquidDistance(
        ShaderProgramChunkliquiddepth __instance, ref float value)
    {
        VanillaHorizonEffects? owner = Volatile.Read(ref active);
        if (owner == null
            || !VanillaHorizonPolicy.UsesVanillaDistanceFade(__instance.PassName))
        {
            return;
        }

        value = VanillaHorizonPolicy.DistanceWithoutFade(
            value, owner.farTerrainDistance());
    }

    public void Dispose()
    {
        // Disable the behavior first. If teardown happens on an unusual engine thread and
        // Harmony refuses the structural unpatch, every subsequent call is still a no-op.
        Interlocked.CompareExchange(ref active, null, this);

        if (!installed) return;
        UnpatchInstalledMethods();
        installed = false;
    }

    void UnpatchInstalledMethods()
    {
        foreach (MethodBase method in patchedMethods)
            harmony.Unpatch(method, HarmonyPatchType.All, HarmonyId);
        patchedMethods.Clear();
    }
}
