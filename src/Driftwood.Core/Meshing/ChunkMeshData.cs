using Driftwood.Core.World;

namespace Driftwood.Core.Meshing;

/// <summary>
/// CPU-side geometry for one chunk, ready to upload. Produced off the render thread; the client
/// only touches GL when it takes delivery.
/// </summary>
public sealed class ChunkMeshData
{
    public required ChunkPos Position { get; init; }
    public required ChunkVertex[] Vertices { get; init; }
    public required uint[] Indices { get; init; }

    public int VertexCount => Vertices.Length;
    public int IndexCount => Indices.Length;
    public int TriangleCount => Indices.Length / 3;
}
