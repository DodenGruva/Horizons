using System.Reflection;
using OpenTK.Graphics.OpenGL4;

namespace VintageHorizons;

internal readonly record struct LodGpuRuntimeValidation(
    bool RequiredEntryPointsValidated,
    bool MinimalComputeValidated,
    uint ExpectedValue,
    uint ObservedValue,
    string FailureReason)
{
    public bool Succeeded => RequiredEntryPointsValidated && MinimalComputeValidated;
}

/// <summary>
/// One-time Phase 0 validation of the advanced OpenGL binding surface and a minimal
/// compute-to-SSBO write. The probe restores every binding it touches and never selects a
/// renderer path by itself.
/// </summary>
internal static class LodGpuRuntimeProbe
{
    internal const uint ProbeValue = 0x56484750; // ASCII "VHGP".

    internal static readonly string[] RequiredEntryPoints =
    [
        "glBindBufferBase",
        "glBindImageTexture",
        "glCopyBufferSubData",
        "glDispatchCompute",
        "glGetBufferSubData",
        "glMemoryBarrier",
        "glMultiDrawElementsIndirect",
    ];

    const string ComputeSource = """
        #version 430 core
        layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;
        layout(std430, binding = 0) buffer ProbeBuffer
        {
            uint value;
        } probe;

        void main()
        {
            probe.value = 0x56484750u;
        }
        """;

    public static LodGpuRuntimeValidation Probe(LodGpuCapabilityFacts capabilities)
    {
        if (!capabilities.ComputeShaderAdvertised || !capabilities.ShaderStorageAdvertised
            || !capabilities.MultiDrawIndirectAdvertised)
        {
            return new LodGpuRuntimeValidation(
                false, false, ProbeValue, 0,
                "compute, SSBO, or multi-draw indirect support is not advertised");
        }

        if (!TryReadEntryPointTable(out string[] names, out IntPtr[] addresses,
                out string tableFailure))
        {
            return new LodGpuRuntimeValidation(
                false, false, ProbeValue, 0, tableFailure);
        }

        if (!ValidateEntryPoints(names, addresses, out string missing))
        {
            return new LodGpuRuntimeValidation(
                false, false, ProbeValue, 0,
                "required OpenGL entry point is unavailable: " + missing);
        }

        return RunMinimalComputeProbe();
    }

    internal static bool ValidateEntryPoints(
        IReadOnlyList<string> names,
        IReadOnlyList<IntPtr> addresses,
        out string missing)
    {
        if (names.Count != addresses.Count)
        {
            missing = "OpenTK entry-point table length mismatch";
            return false;
        }

        var loaded = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < names.Count; i++)
        {
            if (addresses[i] != IntPtr.Zero) loaded.Add(names[i]);
        }

        foreach (string required in RequiredEntryPoints)
        {
            if (loaded.Contains(required)) continue;
            missing = required;
            return false;
        }

        missing = "";
        return true;
    }

    static bool TryReadEntryPointTable(
        out string[] names,
        out IntPtr[] addresses,
        out string failure)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static;
        Type glType = typeof(GL);
        names = glType.GetField("EntryPointNames", flags)?.GetValue(null) as string[] ?? [];
        addresses = glType.GetField("EntryPoints", flags)?.GetValue(null) as IntPtr[] ?? [];
        if (names.Length > 0 && addresses.Length > 0)
        {
            failure = "";
            return true;
        }

        failure = "the OpenTK entry-point table could not be inspected safely";
        return false;
    }

    static LodGpuRuntimeValidation RunMinimalComputeProbe()
    {
        var stateApi = new LodOpenGlStateApi();
        LodGlStateSnapshot state = default;
        int shader = 0;
        int program = 0;
        int buffer = 0;
        uint observed = 0;
        bool stateCaptured = false;
        LodGpuRuntimeValidation result = Failure("minimal compute/SSBO probe did not run", 0);

        try
        {
            ErrorCode existingError = GL.GetError();
            if (existingError != ErrorCode.NoError)
            {
                throw new InvalidOperationException(
                    $"pre-existing OpenGL error {existingError} prevented an isolated probe");
            }

            state = LodGlStateGuard.Capture(
                stateApi,
                LodGlStateMask.Program | LodGlStateMask.ShaderStorageBuffer);
            stateCaptured = true;

            shader = GL.CreateShader(ShaderType.ComputeShader);
            GL.ShaderSource(shader, ComputeSource);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
            if (compiled == 0)
            {
                throw new InvalidOperationException(
                    "compute shader compile failed: " + GL.GetShaderInfoLog(shader));
            }

            program = GL.CreateProgram();
            GL.AttachShader(program, shader);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0)
            {
                throw new InvalidOperationException(
                    "compute program link failed: " + GL.GetProgramInfoLog(program));
            }

            buffer = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, buffer);
            uint initial = 0;
            GL.BufferData(
                BufferTarget.ShaderStorageBuffer,
                sizeof(uint),
                ref initial,
                BufferUsageHint.StaticDraw);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, buffer);

            GL.UseProgram(program);
            GL.DispatchCompute(1, 1, 1);
            GL.MemoryBarrier(
                MemoryBarrierFlags.ShaderStorageBarrierBit
                | MemoryBarrierFlags.BufferUpdateBarrierBit);
            GL.GetBufferSubData(
                BufferTarget.ShaderStorageBuffer,
                IntPtr.Zero,
                sizeof(uint),
                ref observed);

            ErrorCode error = GL.GetError();
            if (error != ErrorCode.NoError)
            {
                throw new InvalidOperationException(
                    "minimal compute/SSBO probe raised " + error);
            }
            if (observed != ProbeValue)
            {
                throw new InvalidOperationException(
                    $"minimal compute/SSBO probe returned 0x{observed:X8}, expected 0x{ProbeValue:X8}");
            }

            result = new LodGpuRuntimeValidation(true, true, ProbeValue, observed, "");
        }
        catch (Exception e)
        {
            result = Failure("minimal compute/SSBO probe failed: " + e.Message, observed);
        }
        finally
        {
            if (stateCaptured && !LodGlStateGuard.TryRestore(
                    stateApi, state, out string restoreFailure))
            {
                result = Failure("minimal compute/SSBO state restore failed: "
                    + restoreFailure, observed);
            }
            try { if (buffer != 0) GL.DeleteBuffer(buffer); }
            catch { /* Context teardown must continue. */ }
            try { if (program != 0) GL.DeleteProgram(program); }
            catch { /* Context teardown must continue. */ }
            try { if (shader != 0) GL.DeleteShader(shader); }
            catch { /* Context teardown must continue. */ }
        }

        return result;
    }

    static LodGpuRuntimeValidation Failure(string reason, uint observed) =>
        new(true, false, ProbeValue, observed, reason);
}
