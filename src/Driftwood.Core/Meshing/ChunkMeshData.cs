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

    /// <summary>
    /// The chunk's tint colours as rgb triplets, indexed by the tint field in each vertex.
    /// </summary>
    /// <remarks>
    /// A palette per chunk rather than a colour per vertex, and that is what makes tinting free.
    /// Storing rgb in the vertex would need a fourth word, and worse, it would break greedy
    /// merging: climate varies continuously, so no two neighbouring blocks would agree on a colour
    /// and every quad would collapse to one face. An index into a short list is stable over a whole
    /// chunk, so faces merge exactly as they did before.
    /// </remarks>
    public required float[] TintPalette { get; init; }

    public int VertexCount => Vertices.Length;
    public int IndexCount => Indices.Length;
    public int TriangleCount => Indices.Length / 3;
}
