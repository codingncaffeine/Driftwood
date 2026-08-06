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

    /// <summary>
    /// How many of <see cref="Indices"/> belong to the opaque pass. The rest are see-through.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>One buffer with a split point rather than two meshes</b>, because both halves index
    /// the same vertices and a second buffer would duplicate every one of them. The mesher collects
    /// see-through quads into a list of their own and appends it, so drawing either half is an offset
    /// and a count.</para>
    /// <para>⛔ <b>Water, not "fluid".</b> Lava is opaque and emissive and stays in the first pass —
    /// which is the whole reason it was built first, since it dodges sorting entirely. Sorting is the
    /// debt this pays, and it comes due for water alone.</para>
    /// </remarks>
    public required int OpaqueIndexCount { get; init; }

    public int VertexCount => Vertices.Length;
    public int IndexCount => Indices.Length;
    public int TriangleCount => Indices.Length / 3;

    /// <summary>True when this chunk draws anything in the second pass.</summary>
    public bool HasTranslucent => Indices.Length > OpaqueIndexCount;
}
