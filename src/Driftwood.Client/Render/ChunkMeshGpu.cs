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

    /// <summary>How many indices belong to the first pass. The rest are see-through.</summary>
    public int OpaqueIndexCount { get; }

    /// <summary>Climate colours this chunk's vertices index into, as rgb triplets.</summary>
    public float[] TintPalette { get; }

    /// <summary>World-space bounds of the chunk this mesh belongs to, for frustum rejection.</summary>
    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }

    public unsafe ChunkMeshGpu(GL gl, ChunkMeshData data)
    {
        _gl = gl;
        Position = data.Position;
        IndexCount = data.IndexCount;
        OpaqueIndexCount = data.OpaqueIndexCount;
        VertexCount = data.VertexCount;
        TintPalette = data.TintPalette;

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

        // Every attribute is a bit field, so all three take the integer path. VertexAttribPointer
        // would convert the bits to float on the way in and every unpack would read garbage.
        // location 0: x, y, face, occlusion, coplanar pass. location 1: z, texture layer, tint.
        // location 2: baked light and model texture coordinates.
        for (uint slot = 0; slot < 3; slot++)
        {
            _gl.EnableVertexAttribArray(slot);
            _gl.VertexAttribIPointer(
                slot, 1, VertexAttribIType.UnsignedInt, ChunkVertex.SizeInBytes, (void*)(slot * 4));
        }

        _gl.BindVertexArray(0);
    }

    /// <summary>Everything that writes depth: the world, and lava with it.</summary>
    public unsafe void Draw()
    {
        if (OpaqueIndexCount == 0) return;

        _gl.BindVertexArray(_vao);
        _gl.DrawElements(
            PrimitiveType.Triangles, (uint)OpaqueIndexCount, DrawElementsType.UnsignedInt, (void*)0);
    }

    /// <summary>
    /// The see-through half, which is water: the same buffer, from an offset.
    /// </summary>
    /// <remarks>
    /// ⚠ The offset is in <b>bytes</b> and an index is four of them — the sort of mistake that draws
    /// the wrong triangles rather than failing.
    /// </remarks>
    public unsafe void DrawTranslucent()
    {
        var count = IndexCount - OpaqueIndexCount;
        if (count <= 0) return;

        _gl.BindVertexArray(_vao);
        _gl.DrawElements(
            PrimitiveType.Triangles, (uint)count, DrawElementsType.UnsignedInt,
            (void*)(nint)(OpaqueIndexCount * sizeof(uint)));
    }

    /// <summary>True when this chunk draws anything in the second pass.</summary>
    public bool HasTranslucent => IndexCount > OpaqueIndexCount;

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
    }
}
