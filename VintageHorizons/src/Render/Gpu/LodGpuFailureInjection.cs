namespace VintageHorizons;

/// <summary>
/// Phase 9's explicit failure points. These exist only for guarded test runs: ordinary sessions
/// parse no stage and execute no extra branch after startup. Each injection is consumed once so a
/// run proves the fallback transition rather than logging the same artificial fault every frame.
/// </summary>
internal enum LodGpuFailureStage
{
    None,
    ArenaSetup,
    FastShaders,
    DepthCopy,
    IndirectDraw,
    Invalid,
}

internal sealed class LodGpuFailureInjection
{
    public const string EnvironmentVariable = "VINTAGEHORIZONS_GPU_INJECT_FAILURE";

    bool consumed;

    LodGpuFailureInjection(LodGpuFailureStage stage, string requested)
    {
        Stage = stage;
        Requested = requested;
    }

    public LodGpuFailureStage Stage { get; }
    public string Requested { get; }
    public bool Armed => Stage is not LodGpuFailureStage.None and not LodGpuFailureStage.Invalid
        && !consumed;

    public static LodGpuFailureInjection Parse(string? value)
    {
        string requested = value?.Trim() ?? "";
        LodGpuFailureStage stage = requested.ToLowerInvariant() switch
        {
            "" or "off" or "none" => LodGpuFailureStage.None,
            "arena" or "arena-setup" => LodGpuFailureStage.ArenaSetup,
            "shader" or "shaders" or "fast-shaders" => LodGpuFailureStage.FastShaders,
            "depth" or "depth-copy" => LodGpuFailureStage.DepthCopy,
            "draw" or "indirect-draw" => LodGpuFailureStage.IndirectDraw,
            _ => LodGpuFailureStage.Invalid,
        };
        return new LodGpuFailureInjection(stage, requested);
    }

    public bool Take(LodGpuFailureStage stage)
    {
        if (consumed || Stage != stage) return false;
        consumed = true;
        return true;
    }
}
