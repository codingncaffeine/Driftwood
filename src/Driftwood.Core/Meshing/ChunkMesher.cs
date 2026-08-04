using Driftwood.Core.Blocks;
using Driftwood.Core.World;

namespace Driftwood.Core.Meshing;

/// <summary>
/// Builds renderable geometry for a chunk: culls hidden faces and bakes per-corner ambient
/// occlusion.
/// </summary>
/// <remarks>
/// <para>One instance per worker thread. It carries its own snapshot and growable output buffers
/// so a steady-state remesh allocates nothing — meshing is the hot path of the whole engine and
/// the thing most likely to show up as a frame hitch.</para>
/// <para>This is the straightforward one-quad-per-face mesher. P1 replaces the emit loop with
/// greedy merging; the snapshot, the AO rules and the vertex format all stay, so that is a
/// contained change rather than a rewrite.</para>
/// </remarks>
public sealed class ChunkMesher
{
    private readonly BlockRegistry _registry;
    private readonly bool[] _opaque;
    private readonly ChunkSnapshot _snapshot = new();

    private ChunkVertex[] _vertices = new ChunkVertex[16 * 1024];
    private uint[] _indices = new uint[24 * 1024];
    private int _vertexCount;
    private int _indexCount;

    public ChunkMesher(BlockRegistry registry)
    {
        _registry = registry;
        _opaque = registry.BuildOpacityTable();
    }

    /// <summary>
    /// Meshes one chunk. Returns null when the chunk contributes no geometry — either it is empty
    /// or every face is buried, both of which are common enough underground that allocating an
    /// empty mesh for them would be wasteful.
    /// </summary>
    public ChunkMeshData? Build(VoxelWorld world, ChunkPos pos)
    {
        if (!world.TryGetChunk(pos, out var chunk) || chunk.IsEmpty) return null;

        _snapshot.Capture(world, pos);
        _vertexCount = 0;
        _indexCount = 0;

        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        for (var x = 0; x < Chunk.Size; x++)
        {
            var here = _snapshot.Get(x, y, z);
            if (here == 0) continue;

            var type = _registry[here];

            for (var face = 0; face < Faces.Count; face++)
            {
                var n = Faces.Normals[face];
                var neighbour = _snapshot.Get(x + n.X, y + n.Y, z + n.Z);

                // Hidden behind something solid, or an interior seam between two blocks of the
                // same see-through kind (water against water, leaves against leaves).
                if (_opaque[neighbour] || neighbour == here) continue;

                EmitFace(x, y, z, face, type.LayerForFace(face));
            }
        }

        if (_indexCount == 0) return null;

        return new ChunkMeshData
        {
            Position = pos,
            Vertices = _vertices.AsSpan(0, _vertexCount).ToArray(),
            Indices = _indices.AsSpan(0, _indexCount).ToArray(),
        };
    }

    private void EmitFace(int x, int y, int z, int face, ushort layer)
    {
        EnsureCapacity();

        var n = Faces.Normals[face];
        var nx = x + n.X;
        var ny = y + n.Y;
        var nz = z + n.Z;

        var corners = Faces.Corners[face];
        var offsets = Faces.AoOffsets[face];

        Span<int> ao = stackalloc int[4];
        for (var c = 0; c < 4; c++)
        {
            var o = offsets[c];
            var side1 = _opaque[_snapshot.Get(nx + o[0].X, ny + o[0].Y, nz + o[0].Z)];
            var side2 = _opaque[_snapshot.Get(nx + o[1].X, ny + o[1].Y, nz + o[1].Z)];

            // Two blocking edges seal the corner; the diagonal behind them cannot lighten it.
            if (side1 && side2)
            {
                ao[c] = 0;
                continue;
            }

            var corner = _opaque[_snapshot.Get(nx + o[2].X, ny + o[2].Y, nz + o[2].Z)];
            ao[c] = 3 - ((side1 ? 1 : 0) + (side2 ? 1 : 0) + (corner ? 1 : 0));
        }

        var baseIndex = (uint)_vertexCount;
        for (var c = 0; c < 4; c++)
        {
            var p = corners[c];
            _vertices[_vertexCount++] = new ChunkVertex(x + p.X, y + p.Y, z + p.Z, face, ao[c], layer);
        }

        // Split the quad along whichever diagonal keeps the shading gradient smooth. Without this
        // the default split makes AO corners look like they lean one way, and the bias is visible
        // as a herringbone across large flat surfaces.
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
    }

    private void EnsureCapacity()
    {
        if (_vertexCount + 4 > _vertices.Length)
            Array.Resize(ref _vertices, _vertices.Length * 2);
        if (_indexCount + 6 > _indices.Length)
            Array.Resize(ref _indices, _indices.Length * 2);
    }
}
