namespace VintageHorizons;

/// <summary>
/// Facts reported by the current OpenGL context. Advertised support is deliberately
/// separate from resource validation: a version string or extension bit is not enough to
/// make the optional renderer safe to select.
/// </summary>
internal readonly record struct LodGpuCapabilityFacts(
    string Vendor,
    string Renderer,
    string Version,
    string ShadingLanguageVersion,
    int MajorVersion,
    int MinorVersion,
    bool TimerQueryAdvertised,
    bool ComputeShaderAdvertised,
    bool ShaderStorageAdvertised,
    bool MultiDrawIndirectAdvertised,
    bool ImageLoadStoreAdvertised,
    int MaxTextureSize,
    long MaxShaderStorageBlockBytes,
    int MaxShaderStorageBindings,
    int MaxComputeWorkGroupInvocations,
    bool RequiredEntryPointsValidated,
    bool MinimalComputeValidated);

internal enum LodGpuDepthConvention
{
    Unknown,
    Conventional,
    Reversed,
}

/// <summary>Read-only description of the depth target active at the cached-terrain pass.</summary>
internal readonly record struct LodGpuDepthFacts(
    int DrawFramebuffer,
    int ReadFramebuffer,
    string AttachmentType,
    int AttachmentName,
    int DepthBits,
    int Samples,
    string DepthFunction,
    double ClearDepth,
    LodGpuDepthConvention Convention,
    bool CopyValidated,
    string FailureReason)
{
    public bool AttachmentPresent => DepthBits > 0 && string.IsNullOrEmpty(FailureReason);
}

internal readonly record struct LodGpuPathDecision(
    bool TimerQueriesAvailable,
    bool RegionalIndirectAdvertised,
    bool HierarchicalDepthAdvertised,
    bool Tier1RuntimeValidated,
    string Reason);

/// <summary>Pure compatibility policy, covered with synthetic capability inputs.</summary>
internal static class LodGpuCapabilityPolicy
{
    internal const long MinimumShaderStorageBlockBytes = 16L * 1024 * 1024;
    internal const int MinimumShaderStorageBindings = 4;
    internal const int MinimumComputeInvocations = 64;

    public static LodGpuPathDecision Evaluate(
        LodGpuCapabilityFacts capabilities, LodGpuDepthFacts depth)
    {
        bool ssboLimits = capabilities.MaxShaderStorageBlockBytes >= MinimumShaderStorageBlockBytes
            && capabilities.MaxShaderStorageBindings >= MinimumShaderStorageBindings;
        bool computeLimits = capabilities.MaxComputeWorkGroupInvocations >= MinimumComputeInvocations;
        bool regional = capabilities.ShaderStorageAdvertised
            && capabilities.MultiDrawIndirectAdvertised
            && ssboLimits;
        bool hzb = capabilities.ComputeShaderAdvertised
            && capabilities.ShaderStorageAdvertised
            && capabilities.ImageLoadStoreAdvertised
            && ssboLimits
            && computeLimits
            && depth.AttachmentPresent
            && depth.Convention != LodGpuDepthConvention.Unknown;
        bool validated = regional
            && hzb
            && capabilities.RequiredEntryPointsValidated
            && capabilities.MinimalComputeValidated
            && depth.CopyValidated;

        string reason;
        if (!regional)
            reason = "regional indirect requirements are not advertised or their limits are too small";
        else if (!hzb)
            reason = "HZB compute/depth requirements are not advertised or the depth convention is unknown";
        else if (!capabilities.RequiredEntryPointsValidated || !capabilities.MinimalComputeValidated)
            reason = "required entry points and the minimal compute/SSBO probe are not yet runtime-validated";
        else if (!depth.CopyValidated)
            reason = "the active depth copy and private mip chain are not yet runtime-validated";
        else
            reason = "Tier 1 runtime requirements are validated";

        return new LodGpuPathDecision(
            capabilities.TimerQueryAdvertised,
            regional,
            hzb,
            validated,
            reason);
    }

    public static LodGpuDepthConvention ClassifyDepthConvention(
        string depthFunction, double clearDepth)
    {
        if ((depthFunction.Equals("Less", StringComparison.OrdinalIgnoreCase)
                || depthFunction.Equals("Lequal", StringComparison.OrdinalIgnoreCase))
            && clearDepth >= 0.999)
            return LodGpuDepthConvention.Conventional;

        if ((depthFunction.Equals("Greater", StringComparison.OrdinalIgnoreCase)
                || depthFunction.Equals("Gequal", StringComparison.OrdinalIgnoreCase))
            && clearDepth <= 0.001)
            return LodGpuDepthConvention.Reversed;

        return LodGpuDepthConvention.Unknown;
    }
}
