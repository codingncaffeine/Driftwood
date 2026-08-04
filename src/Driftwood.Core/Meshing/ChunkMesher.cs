using Driftwood.Core.Blocks;
using Driftwood.Core.World;

namespace Driftwood.Core.Meshing;

/// <summary>
/// Builds renderable geometry for a chunk: culls hidden faces, bakes per-corner ambient
/// occlusion, and merges coplanar faces into the largest rectangles it can.
/// </summary>
/// <remarks>
/// <para>One instance per worker thread. It carries its own snapshot, mask and output buffers so
/// a steady-state remesh allocates nothing — meshing is the hot path of the engine and the thing
/// most likely to surface as a frame hitch.</para>
/// <para>Merging is per face direction, per slice. Two faces may only join when they agree on
/// texture layer <em>and</em> on all four ambient occlusion values; merging across differing AO
/// would flatten the shading gradient that makes corners read. That constraint is why a merged
/// quad can carry the same four corner values as the unit faces it replaced.</para>
/// </remarks>
public sealed class ChunkMesher
{
    private readonly BlockRegistry _registry;
    private readonly bool[] _opaque;
    private readonly ChunkSnapshot _snapshot = new();

    /// <summary>Merge key per cell of the current slice: 0 is empty, else (layer+1) &lt;&lt; 8 | packed AO.</summary>
    private readonly int[] _mask = new int[Chunk.Size * Chunk.Size];

    private ChunkVertex[] _vertices = new ChunkVertex[16 * 1024];
    private uint[] _indices = new uint[24 * 1024];
    private int _vertexCount;
    private int _indexCount;

    /// <summary>Quads emitted by the last <see cref="Build"/>.</summary>
    public int LastQuadCount { get; private set; }

    /// <summary>
    /// Unit block faces covered by the last <see cref="Build"/>, summing width by height over
    /// every merged quad. Must equal <see cref="CountVisibleFaces"/> for the same chunk.
    /// </summary>
    public int LastCoveredFaces { get; private set; }

    public ChunkMesher(BlockRegistry registry)
    {
        _registry = registry;
        _opaque = registry.BuildOpacityTable();
    }

    /// <summary>
    /// Meshes one chunk. Returns null when the chunk contributes no geometry — either it is empty
    /// or every face is buried, both common enough underground that allocating an empty mesh for
    /// them would be wasteful.
    /// </summary>
    public ChunkMeshData? Build(VoxelWorld world, ChunkPos pos)
    {
        LastQuadCount = 0;
        LastCoveredFaces = 0;

        if (!world.TryGetChunk(pos, out var chunk) || chunk.IsEmpty) return null;

        _snapshot.Capture(world, pos);
        _vertexCount = 0;
        _indexCount = 0;

        for (var face = 0; face < Faces.Count; face++)
            MeshDirection(face);

        if (_indexCount == 0) return null;

        return new ChunkMeshData
        {
            Position = pos,
            Vertices = _vertices.AsSpan(0, _vertexCount).ToArray(),
            Indices = _indices.AsSpan(0, _indexCount).ToArray(),
        };
    }

    /// <summary>
    /// Counts visible unit faces the simple way, one block at a time. Independent of the merge
    /// path, so it can be used to prove the merge neither dropped nor invented surface.
    /// </summary>
    public int CountVisibleFaces(VoxelWorld world, ChunkPos pos)
    {
        if (!world.TryGetChunk(pos, out var chunk) || chunk.IsEmpty) return 0;

        _snapshot.Capture(world, pos);

        var count = 0;
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        for (var x = 0; x < Chunk.Size; x++)
        {
            var here = _snapshot.Get(x, y, z);
            if (here == 0) continue;

            for (var face = 0; face < Faces.Count; face++)
            {
                var n = Faces.Normals[face];
                var neighbour = _snapshot.Get(x + n.X, y + n.Y, z + n.Z);
                if (!_opaque[neighbour] && neighbour != here) count++;
            }
        }

        return count;
    }

    private void MeshDirection(int face)
    {
        var n = Faces.Normals[face];
        var axis = n.X != 0 ? 0 : n.Y != 0 ? 1 : 2;

        // The two axes lying in this face's plane, taken in increasing order. The mask is indexed
        // [v, u] over them, and merged quads grow along the same pair.
        var au = axis == 0 ? 1 : 0;
        var av = axis == 2 ? 1 : 2;

        for (var slice = 0; slice < Chunk.Size; slice++)
        {
            BuildMask(face, axis, au, av, slice);
            MergeMask(face, axis, au, av, slice);
        }
    }

    private void BuildMask(int face, int axis, int au, int av, int slice)
    {
        Array.Clear(_mask);

        var n = Faces.Normals[face];
        var offsets = Faces.AoOffsets[face];

        for (var v = 0; v < Chunk.Size; v++)
        for (var u = 0; u < Chunk.Size; u++)
        {
            var (x, y, z) = Compose(axis, au, av, slice, u, v);

            var here = _snapshot.Get(x, y, z);
            if (here == 0) continue;

            var neighbour = _snapshot.Get(x + n.X, y + n.Y, z + n.Z);

            // Hidden behind something opaque, or an interior seam between two blocks of the same
            // see-through kind (water against water, leaves against leaves).
            if (_opaque[neighbour] || neighbour == here) continue;

            var nx = x + n.X;
            var ny = y + n.Y;
            var nz = z + n.Z;

            var ao = 0;
            for (var c = 0; c < 4; c++)
                ao |= AmbientOcclusion(offsets[c], nx, ny, nz) << (c * 2);

            var layer = _registry[here].LayerForFace(face);
            _mask[v * Chunk.Size + u] = ((layer + 1) << 8) | ao;
        }
    }

    private int AmbientOcclusion((int X, int Y, int Z)[] offsets, int nx, int ny, int nz)
    {
        var side1 = _opaque[_snapshot.Get(nx + offsets[0].X, ny + offsets[0].Y, nz + offsets[0].Z)];
        var side2 = _opaque[_snapshot.Get(nx + offsets[1].X, ny + offsets[1].Y, nz + offsets[1].Z)];

        // Two blocking edges seal the corner; the diagonal behind them cannot lighten it.
        if (side1 && side2) return 0;

        var corner = _opaque[_snapshot.Get(nx + offsets[2].X, ny + offsets[2].Y, nz + offsets[2].Z)];
        return 3 - ((side1 ? 1 : 0) + (side2 ? 1 : 0) + (corner ? 1 : 0));
    }

    /// <summary>
    /// Sweeps the mask, growing each remaining cell into the widest run it can and then the
    /// tallest block of identical runs, emitting one quad per rectangle and clearing it as it goes.
    /// </summary>
    private void MergeMask(int face, int axis, int au, int av, int slice)
    {
        for (var v = 0; v < Chunk.Size; v++)
        for (var u = 0; u < Chunk.Size;)
        {
            var key = _mask[v * Chunk.Size + u];
            if (key == 0)
            {
                u++;
                continue;
            }

            var width = 1;
            while (u + width < Chunk.Size && _mask[v * Chunk.Size + u + width] == key)
                width++;

            var height = 1;
            var grew = true;
            while (v + height < Chunk.Size && grew)
            {
                for (var i = 0; i < width; i++)
                {
                    if (_mask[(v + height) * Chunk.Size + u + i] == key) continue;
                    grew = false;
                    break;
                }
                if (grew) height++;
            }

            for (var dv = 0; dv < height; dv++)
            for (var du = 0; du < width; du++)
                _mask[(v + dv) * Chunk.Size + u + du] = 0;

            EmitQuad(face, axis, au, av, slice, u, v, width, height, key);

            u += width;
        }
    }

    private void EmitQuad(
        int face, int axis, int au, int av, int slice, int u, int v, int width, int height, int key)
    {
        EnsureCapacity();

        var layer = (ushort)((key >> 8) - 1);
        var corners = Faces.Corners[face];

        Span<int> ao = stackalloc int[4];
        for (var c = 0; c < 4; c++) ao[c] = (key >> (c * 2)) & 0x3;

        var baseIndex = (uint)_vertexCount;
        for (var c = 0; c < 4; c++)
        {
            var corner = corners[c];
            Span<int> unit = [corner.X, corner.Y, corner.Z];

            // The corner's offset along the normal picks the near or far plane and stays a unit
            // step; its offsets in the plane stretch to the merged rectangle's extent.
            Span<int> p = stackalloc int[3];
            p[axis] = slice + unit[axis];
            p[au] = u + unit[au] * width;
            p[av] = v + unit[av] * height;

            _vertices[_vertexCount++] = new ChunkVertex(p[0], p[1], p[2], face, ao[c], layer);
        }

        // Split the quad along whichever diagonal keeps the shading gradient smooth. Without this
        // the default split biases AO corners one way, visible as a herringbone across flat walls.
        if (ao[0] + ao[2] > ao[1] + ao[3])
        {
            _indices[_indexCount++] = baseIndex + 1;
            _indices[_indexCount++] = baseIndex + 2;
            _indices[_indexCount++] = baseIndex + 3;
            _indices[_indexCount++] = baseIndex + 1;
            _indices[_indexCount++] = baseIndex + 3;
            _indices[_indexCount++] = baseIndex + 0;
        }
        else
        {
            _indices[_indexCount++] = baseIndex + 0;
            _indices[_indexCount++] = baseIndex + 1;
            _indices[_indexCount++] = baseIndex + 2;
            _indices[_indexCount++] = baseIndex + 0;
            _indices[_indexCount++] = baseIndex + 2;
            _indices[_indexCount++] = baseIndex + 3;
        }

        LastQuadCount++;
        LastCoveredFaces += width * height;
    }

    /// <summary>Maps slice/u/v on an axis triple back to block coordinates.</summary>
    private static (int X, int Y, int Z) Compose(int axis, int au, int av, int slice, int u, int v)
    {
        Span<int> p = stackalloc int[3];
        p[axis] = slice;
        p[au] = u;
        p[av] = v;
        return (p[0], p[1], p[2]);
    }

    private void EnsureCapacity()
    {
        if (_vertexCount + 4 > _vertices.Length)
            Array.Resize(ref _vertices, _vertices.Length * 2);
        if (_indexCount + 6 > _indices.Length)
            Array.Resize(ref _indices, _indices.Length * 2);
    }
}
