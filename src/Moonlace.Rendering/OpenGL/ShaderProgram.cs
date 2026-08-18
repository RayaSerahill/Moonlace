using System.Numerics;
using Silk.NET.OpenGL;

namespace Moonlace.Rendering.OpenGL;

/// <summary>Compiled + linked GL program with uniform helpers. Explicitly disposed.</summary>
public sealed class ShaderProgram : IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<string, int> _uniformLocations = new(StringComparer.Ordinal);

    public uint Handle { get; }

    public ShaderProgram(GL gl, string vertexSource, string fragmentSource)
    {
        _gl = gl;

        var vs = Compile(ShaderType.VertexShader, vertexSource);
        var fs = Compile(ShaderType.FragmentShader, fragmentSource);

        Handle = _gl.CreateProgram();
        _gl.AttachShader(Handle, vs);
        _gl.AttachShader(Handle, fs);
        _gl.LinkProgram(Handle);
        _gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out var linked);
        if (linked == 0)
        {
            var log = _gl.GetProgramInfoLog(Handle);
            _gl.DeleteProgram(Handle);
            throw new InvalidOperationException($"Shader link failed: {log}");
        }

        _gl.DetachShader(Handle, vs);
        _gl.DetachShader(Handle, fs);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
    }

    private uint Compile(ShaderType type, string source)
    {
        var shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out var ok);
        if (ok == 0)
        {
            var log = _gl.GetShaderInfoLog(shader);
            _gl.DeleteShader(shader);
            throw new InvalidOperationException($"{type} compile failed: {log}");
        }

        return shader;
    }

    public void Use() => _gl.UseProgram(Handle);

    private int Location(string name)
    {
        if (!_uniformLocations.TryGetValue(name, out var loc))
        {
            loc = _gl.GetUniformLocation(Handle, name);
            _uniformLocations[name] = loc;
        }

        return loc;
    }

    public void SetInt(string name, int value) => _gl.Uniform1(Location(name), value);

    public void SetFloat(string name, float value) => _gl.Uniform1(Location(name), value);

    public void SetVector3(string name, Vector3 v) => _gl.Uniform3(Location(name), v.X, v.Y, v.Z);

    public unsafe void SetMatrix(string name, Matrix4x4 m) =>
        _gl.UniformMatrix4(Location(name), 1, transpose: false, (float*)&m);

    public unsafe void SetVector3Array(string name, Vector3[] values)
    {
        fixed (Vector3* ptr = values)
            _gl.Uniform3(Location(name), (uint)values.Length, (float*)ptr);
    }

    public unsafe void SetFloatArray(string name, float[] values)
    {
        fixed (float* ptr = values)
            _gl.Uniform1(Location(name), (uint)values.Length, ptr);
    }

    public void Dispose() => _gl.DeleteProgram(Handle);
}
