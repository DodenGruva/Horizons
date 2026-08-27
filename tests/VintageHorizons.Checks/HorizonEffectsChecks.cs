using System.Reflection;
using VintageHorizons.Render;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace VintageHorizons.Checks;

public static class HorizonEffectsChecks
{
    public static void Run(Check c)
    {
        ProgramAllowlist(c);
        HookTargets(c);
        HookInstallation(c);
        FadeDistance(c);
        FogContribution(c);
    }

    static void HookInstallation(Check c)
    {
        ILogger logger = DispatchProxy.Create<ILogger, SilentProxy>();
        var effects = new VanillaHorizonEffects(null!, () => 3000);
        bool installed = false;

        c.NoThrow(() => installed = effects.Install(logger),
            "the three hooks install against the current game assembly without an OpenGL context");
        c.True(installed,
            "Harmony accepts the ambient postfix, shader-use postfix and liquid-distance prefix");
        c.NoThrow(effects.Dispose,
            "the exact Vintage Horizons hooks remove cleanly");
        c.NoThrow(effects.Dispose,
            "hook disposal is idempotent");
    }

    public class SilentProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.ReturnType == typeof(void) || targetMethod == null) return null;
            return targetMethod.ReturnType.IsValueType
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }

    static void HookTargets(Check c)
    {
        c.True(typeof(AmbientManager).GetMethod(
                nameof(AmbientManager.UpdateAmbient), new[] { typeof(float) }) != null,
            "the installed engine exposes the once-per-frame ambient hook");
        c.True(typeof(ShaderProgramBase).GetMethod(nameof(ShaderProgramBase.Use)) != null,
            "the installed engine exposes the shader-activation hook");
        c.True(typeof(ShaderProgramChunkliquiddepth).GetProperty(
                nameof(ShaderProgramChunkliquiddepth.ViewDistance))?.SetMethod != null,
            "the installed engine exposes the liquid-depth late distance upload");
    }

    static void ProgramAllowlist(Check c)
    {
        string[] fadedPrograms =
        {
            "chunkopaque",
            "chunktopsoil",
            "chunktransparent",
            "chunkliquid",
            "chunkliquiddepth",
            "standard",
            "instanced",
            "entityanimated",
        };

        foreach (string pass in fadedPrograms)
        {
            c.True(VanillaHorizonPolicy.UsesVanillaDistanceFade(pass),
                pass + " has its vanilla radial fade suppressed");
        }

        foreach (string pass in new[] { "lodterrain", "lodterrainpacked", "sky", "particlescube", "" })
        {
            c.False(VanillaHorizonPolicy.UsesVanillaDistanceFade(pass),
                pass + " keeps its own view-distance meaning");
        }

        c.False(VanillaHorizonPolicy.UsesVanillaDistanceFade(null),
            "an unidentified shader is never rewritten");
        c.False(VanillaHorizonPolicy.UsesVanillaDistanceFade("Chunkopaque"),
            "the allowlist is exact rather than matching unrelated program names");
    }

    static void FadeDistance(Check c)
    {
        c.Near(3000, VanillaHorizonPolicy.DistanceWithoutFade(256, 3000), 0.001,
            "cached-terrain reach pushes the vanilla fade beyond the visible horizon");
        c.Near(1024, VanillaHorizonPolicy.DistanceWithoutFade(256, 500), 0.001,
            "the fallback multiplier removes the fade even before cached reach grows");
        c.Near(128, VanillaHorizonPolicy.DistanceWithoutFade(32, float.NaN), 0.001,
            "invalid cached reach leaves the bounded configured-distance fallback");
        c.Near(256, VanillaHorizonPolicy.DistanceWithoutFade(64, -1), 0.001,
            "negative cached reach cannot shrink the fallback");
        c.Eq(0f, VanillaHorizonPolicy.DistanceWithoutFade(0, 3000),
            "an invalid zero configured distance fails open");
        c.True(float.IsPositiveInfinity(
                VanillaHorizonPolicy.DistanceWithoutFade(float.PositiveInfinity, 3000)),
            "a non-finite configured distance passes through unchanged");
    }

    static void FogContribution(Check c)
    {
        const float vanilla = 0.00125f;

        c.Near(0, VanillaHorizonPolicy.DensityWithoutVanillaBase(
                vanilla, vanilla, vanilla, 1), 1e-8,
            "clear-air vanilla density is removed exactly");

        // Engine blend for a half-weight 0.07 underwater modifier:
        // 0.5^2 * 0.07 + 0.5^2 * 0.00125 = 0.0178125.
        float retained = VanillaHorizonPolicy.RetainBaseFog(1, 0.5f);
        c.Near(0.25, retained, 1e-8,
            "a half-weight modifier retains one quarter of the base contribution");
        c.Near(0.0175, VanillaHorizonPolicy.DensityWithoutVanillaBase(
                0.0178125f, vanilla, vanilla, retained), 1e-7,
            "underwater density remains after only the propagated clear-air share is removed");

        retained = VanillaHorizonPolicy.RetainBaseFog(retained, 0.25f);
        c.Near(0.140625, retained, 1e-8,
            "stacked modifier retention follows the engine's ordered squared weights");

        c.Near(0.00875, VanillaHorizonPolicy.DensityWithoutVanillaBase(
                0.01f, 0.01f, vanilla, 1), 1e-8,
            "a deliberate base increase above vanilla remains");
        c.Near(0.0095, VanillaHorizonPolicy.DensityWithoutVanillaBase(
                0.01f, 0.0005f, vanilla, 1), 1e-8,
            "a lower current base removes no more density than it contributes");
        c.Near(0.02, VanillaHorizonPolicy.DensityWithoutVanillaBase(
                0.02f, vanilla, vanilla, 0), 1e-8,
            "a full-weight atmospheric modifier owns the result completely");
        c.Near(0, VanillaHorizonPolicy.DensityWithoutVanillaBase(
                0.0002f, vanilla, vanilla, 1), 1e-8,
            "subtraction clamps rather than creating negative fog");
        c.True(float.IsNaN(VanillaHorizonPolicy.DensityWithoutVanillaBase(
                float.NaN, vanilla, vanilla, 1)),
            "invalid engine fog fails open rather than inventing a replacement");
    }

}
