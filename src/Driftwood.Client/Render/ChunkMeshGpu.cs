using System.Numerics;
using Driftwood.Core.Meshing;
using Driftwood.Core.World;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>GPU buffers for one chunk's geometry.</summary>
public sealed class ChunkMeshGpu : IDisposable
{
    private readonly GL _gl;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;

    public ChunkPos Position { get; }
    public Vector3 Origin { get; }
    public int IndexCount { get; }
    public int VertexCount { get; }

    public unsafe ChunkMeshGpu(GL gl, ChunkMeshData data)
    {
        _gl = gl;
        Position = data.Position;
        IndexCount = data.IndexCount;
        VertexCount = data.VertexCount;

        var (ox, oy, oz) = data.Position.Origin;
        Origin = new Vector3(ox, oy, oz);

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (ChunkVertex* p = data.Vertices)
        {
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                (nuint)(data.VertexCount * ChunkVertex.SizeInBytes),
                p,
                BufferUsageARB.StaticDraw);
        }

        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* p = data.Indices)
        {
            _gl.BufferData(
                BufferTargetARB.ElementArrayBuffer,
                (nuint)(data.IndexCount * sizeof(uint)),
                p,
                BufferUsageARB.StaticDraw);
        }

        // location 0: chunk-local position.
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, ChunkVertex.SizeInBytes, (void*)0);

        // location 1: the packed face/ao/layer word. Integer attributes need the "I" form —
        // the float path would silently convert the bits and the unpack maths would read garbage.
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribIPointer(1, 1, VertexAttribIType.UnsignedInt, ChunkVertex.SizeInBytes, (void*)12);

        _gl.BindVertexArray(0);
    }

    public unsafe void Draw()
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)IndexCount, DrawElementsType.UnsignedInt, (void*)0);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
    }
}
