using System.Numerics;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>
/// Minimal GL shader program: compiles vertex + fragment source, links, reports failures with the
/// driver's own log, and caches uniform locations.
/// </summary>
public sealed class Shader : IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<string, int> _uniforms = new(StringComparer.Ordinal);

    public uint Handle { get; }

    public Shader(GL gl, string vertexSource, string fragmentSource)
    {
        _gl = gl;

        var vs = Compile(ShaderType.VertexShader, vertexSource);
        var fs = Compile(ShaderType.FragmentShader, fragmentSource);

        Handle = _gl.CreateProgram();
        _gl.AttachShader(Handle, vs);
        _gl.AttachShader(Handle, fs);
        _gl.LinkProgram(Handle);

        _gl.GetProgram(Handle, GLEnum.LinkStatus, out var linked);
        if (linked == 0)
            throw new InvalidOperationException($"shader link failed: {_gl.GetProgramInfoLog(Handle)}");

        _gl.DetachShader(Handle, vs);
        _gl.DetachShader(Handle, fs);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
    }

    private uint Compile(ShaderType type, string source)
    {
        var handle = _gl.CreateShader(type);
        _gl.ShaderSource(handle, source);
        _gl.CompileShader(handle);

        _gl.GetShader(handle, GLEnum.CompileStatus, out var status);
        if (status == 0)
        {
            var log = _gl.GetShaderInfoLog(handle);
            _gl.DeleteShader(handle);
            throw new InvalidOperationException($"{type} compile failed: {log}");
        }
        return handle;
    }

    public void Use() => _gl.UseProgram(Handle);

    private int Loc(string name)
    {
        if (_uniforms.TryGetValue(name, out var cached)) return cached;
        var loc = _gl.GetUniformLocation(Handle, name);
        _uniforms[name] = loc;
        return loc;
    }

    public unsafe void SetMatrix4(string name, Matrix4x4 m)
    {
        var loc = Loc(name);
        if (loc < 0) return;
        // Matrix4x4 is row-major and GL reads column-major; passing transpose=false lets the
        // row-vector maths here land as the equivalent column-vector form the shader expects.
        _gl.UniformMatrix4(loc, 1, false, (float*)&m);
    }

    public void SetVec3(string name, Vector3 v)
    {
        var loc = Loc(name);
        if (loc < 0) return;
        _gl.Uniform3(loc, v.X, v.Y, v.Z);
    }

    public void SetVec4(string name, Vector4 v)
    {
        var loc = Loc(name);
        if (loc < 0) return;
        _gl.Uniform4(loc, v.X, v.Y, v.Z, v.W);
    }

    public void SetFloat(string name, float value)
    {
        var loc = Loc(name);
        if (loc < 0) return;
        _gl.Uniform1(loc, value);
    }

    /// <summary>Uploads a packed rgb-triplet array into a <c>vec3[]</c> uniform.</summary>
    public unsafe void SetVec3Array(string name, ReadOnlySpan<float> rgbTriplets)
    {
        var loc = Loc(name);
        if (loc < 0) return;
        fixed (float* p = rgbTriplets)
            _gl.Uniform3(loc, (uint)(rgbTriplets.Length / 3), p);
    }

    public void Dispose() => _gl.DeleteProgram(Handle);
}
