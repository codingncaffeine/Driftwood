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

    /// <summary>World-space bounds of the chunk this mesh belongs to, for frustum rejection.</summary>
    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }

    public unsafe ChunkMeshGpu(GL gl, ChunkMeshData data)
    {
        _gl = gl;
        Position = data.Position;
        IndexCount = data.IndexCount;
        VertexCount = data.VertexCount;

        var (ox, oy, oz) = data.Position.Origin;
        Origin = new Vector3(ox, oy, oz);
        BoundsMin = Origin;
        BoundsMax = Origin + new Vector3(Chunk.Size);

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

        // Both attributes are bit fields, so both take the integer path. VertexAttribPointer
        // would convert the bits to float on the way in and every unpack would read garbage.
        // location 0: position, face and ambient occlusion. location 1: texture layer.
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribIPointer(0, 1, VertexAttribIType.UnsignedInt, ChunkVertex.SizeInBytes, (void*)0);

        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribIPointer(1, 1, VertexAttribIType.UnsignedInt, ChunkVertex.SizeInBytes, (void*)4);

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
