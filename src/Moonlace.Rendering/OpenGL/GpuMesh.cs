using Moonlace.Core.Models;
using Silk.NET.OpenGL;

namespace Moonlace.Rendering.OpenGL;

/// <summary>
/// VAO + interleaved VBO + EBO for one mesh. Vertex layout (floats):
/// position(3), normal(3), uv(2), tangent(4), color(4) — stride 16 floats.
/// </summary>
public sealed class GpuMesh : IDisposable
{
    private const int FloatsPerVertex = 16;

    private readonly GL _gl;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;

    public int IndexCount { get; }

    public unsafe GpuMesh(GL gl, RenderMesh mesh)
    {
        _gl = gl;
        IndexCount = mesh.Indices.Length;

        var vertexData = new float[mesh.Vertices.Length * FloatsPerVertex];
        var o = 0;
        foreach (ref readonly var v in mesh.Vertices.AsSpan())
        {
            vertexData[o++] = v.Position.X;
            vertexData[o++] = v.Position.Y;
            vertexData[o++] = v.Position.Z;
            vertexData[o++] = v.Normal.X;
            vertexData[o++] = v.Normal.Y;
            vertexData[o++] = v.Normal.Z;
            vertexData[o++] = v.Uv.X;
            vertexData[o++] = v.Uv.Y;
            vertexData[o++] = v.Tangent.X;
            vertexData[o++] = v.Tangent.Y;
            vertexData[o++] = v.Tangent.Z;
            vertexData[o++] = v.Tangent.W;
            vertexData[o++] = v.Color.X;
            vertexData[o++] = v.Color.Y;
            vertexData[o++] = v.Color.Z;
            vertexData[o++] = v.Color.W;
        }

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);

        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = vertexData)
        {
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertexData.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        }

        _ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* p = mesh.Indices)
        {
            gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(mesh.Indices.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);
        }

        const uint stride = FloatsPerVertex * sizeof(float);
        uint offset = 0;
        for (uint attrib = 0; attrib < 5; attrib++)
        {
            var size = attrib switch { 0 => 3, 1 => 3, 2 => 2, _ => 4 };
            gl.EnableVertexAttribArray(attrib);
            gl.VertexAttribPointer(attrib, size, VertexAttribPointerType.Float, false, stride, (void*)(offset * sizeof(float)));
            offset += (uint)size;
        }

        gl.BindVertexArray(0);
    }

    public unsafe void Draw()
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)IndexCount, DrawElementsType.UnsignedInt, null);
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
    }
}
