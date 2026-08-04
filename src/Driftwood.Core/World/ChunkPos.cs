namespace Driftwood.Core.World;

/// <summary>A chunk's position on the chunk grid. World block coords divided by <see cref="Chunk.Size"/>.</summary>
public readonly record struct ChunkPos(int X, int Y, int Z)
{
    /// <summary>World-space block coordinate of this chunk's minimum corner.</summary>
    public (int X, int Y, int Z) Origin => (X * Chunk.Size, Y * Chunk.Size, Z * Chunk.Size);

    public ChunkPos Offset(int dx, int dy, int dz) => new(X + dx, Y + dy, Z + dz);

    /// <summary>
    /// Chunk containing a world block coordinate. Uses arithmetic-shift division so negative
    /// coordinates floor correctly — plain <c>/</c> truncates toward zero and would put blocks
    /// at x = -1 and x = +1 in the same chunk.
    /// </summary>
    public static ChunkPos FromWorld(int wx, int wy, int wz) =>
        new(wx >> Chunk.SizeLog2, wy >> Chunk.SizeLog2, wz >> Chunk.SizeLog2);

    public override string ToString() => $"({X},{Y},{Z})";
}
