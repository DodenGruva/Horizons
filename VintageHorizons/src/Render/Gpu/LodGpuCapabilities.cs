using OpenTK.Graphics.OpenGL4;

namespace VintageHorizons;

/// <summary>
/// Read-only OpenGL capability and active-depth inspection. Call only from the render
/// thread while the cached opaque stage owns the current context.
/// </summary>
internal static class LodGpuCapabilities
{
    // OpenTK's GetPName enum in the game binding omits this core 4.3 token even though the
    // corresponding GetInteger64 overload is present.
    const int MaxShaderStorageBlockSizeToken = 0x90DE;

    public static LodGpuCapabilityFacts Probe()
    {
        int major = GL.GetInteger(GetPName.MajorVersion);
        int minor = GL.GetInteger(GetPName.MinorVersion);
        HashSet<string> extensions = ReadExtensions();

        bool AtLeast(int requiredMajor, int requiredMinor) =>
            major > requiredMajor || major == requiredMajor && minor >= requiredMinor;
        bool Has(string extension) => extensions.Contains(extension);

        bool timer = AtLeast(3, 3) || Has("GL_ARB_timer_query");
        bool compute = AtLeast(4, 3) || Has("GL_ARB_compute_shader");
        bool ssbo = AtLeast(4, 3) || Has("GL_ARB_shader_storage_buffer_object");
        bool mdi = AtLeast(4, 3) || Has("GL_ARB_multi_draw_indirect");
        bool image = AtLeast(4, 2) || Has("GL_ARB_shader_image_load_store");

        long maxSsboBytes = ssbo
            ? GL.GetInteger64((GetPName)MaxShaderStorageBlockSizeToken)
            : 0;
        int maxSsboBindings = ssbo
            ? GL.GetInteger(GetPName.MaxShaderStorageBufferBindings)
            : 0;
        int maxComputeInvocations = compute
            ? GL.GetInteger(GetPName.MaxComputeWorkGroupInvocations)
            : 0;

        return new LodGpuCapabilityFacts(
            GL.GetString(StringName.Vendor) ?? "unknown",
            GL.GetString(StringName.Renderer) ?? "unknown",
            GL.GetString(StringName.Version) ?? "unknown",
            GL.GetString(StringName.ShadingLanguageVersion) ?? "unknown",
            major,
            minor,
            timer,
            compute,
            ssbo,
            mdi,
            image,
            GL.GetInteger(GetPName.MaxTextureSize),
            maxSsboBytes,
            maxSsboBindings,
            maxComputeInvocations,
            RequiredEntryPointsValidated: false,
            MinimalComputeValidated: false);
    }

    public static LodGpuDepthFacts ProbeActiveDepth()
    {
        try
        {
            int drawFramebuffer = GL.GetInteger(GetPName.DrawFramebufferBinding);
            int readFramebuffer = GL.GetInteger(GetPName.ReadFramebufferBinding);
            FramebufferAttachment attachment = drawFramebuffer == 0
                ? FramebufferAttachment.Depth
                : FramebufferAttachment.DepthAttachment;

            GL.GetFramebufferAttachmentParameter(
                FramebufferTarget.DrawFramebuffer,
                attachment,
                FramebufferParameterName.FramebufferAttachmentObjectType,
                out int objectTypeValue);
            GL.GetFramebufferAttachmentParameter(
                FramebufferTarget.DrawFramebuffer,
                attachment,
                FramebufferParameterName.FramebufferAttachmentObjectName,
                out int objectName);
            GL.GetFramebufferAttachmentParameter(
                FramebufferTarget.DrawFramebuffer,
                attachment,
                FramebufferParameterName.FramebufferAttachmentDepthSize,
                out int depthBits);

            string depthFunction = ((DepthFunction)GL.GetInteger(GetPName.DepthFunc)).ToString();
            double clearDepth = GL.GetDouble(GetPName.DepthClearValue);
            LodGpuDepthConvention convention =
                LodGpuCapabilityPolicy.ClassifyDepthConvention(depthFunction, clearDepth);

            return new LodGpuDepthFacts(
                drawFramebuffer,
                readFramebuffer,
                ((FramebufferAttachmentObjectType)objectTypeValue).ToString(),
                objectName,
                depthBits,
                Math.Max(1, GL.GetInteger(GetPName.Samples)),
                depthFunction,
                clearDepth,
                convention,
                CopyValidated: false,
                FailureReason: "");
        }
        catch (Exception e)
        {
            return new LodGpuDepthFacts(
                0, 0, "unknown", 0, 0, 0, "unknown", 0,
                LodGpuDepthConvention.Unknown,
                CopyValidated: false,
                FailureReason: e.Message);
        }
    }

    static HashSet<string> ReadExtensions()
    {
        int count = GL.GetInteger(GetPName.NumExtensions);
        var extensions = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string? extension = GL.GetString(StringNameIndexed.Extensions, i);
            if (!string.IsNullOrEmpty(extension)) extensions.Add(extension);
        }
        return extensions;
    }
}
