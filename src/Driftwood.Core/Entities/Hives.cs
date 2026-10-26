using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.World;

namespace Driftwood.Core.Entities;

/// <summary>
/// The bee nests and hives near the player (2026-10-26): what the client keeps bees at.
/// </summary>
/// <remarks>
/// ⛳ <b>Swept, not tracked.</b> A nest grows with its tree and a hive is put down anywhere, so a
/// list kept by placement would miss every wild one; a sweep of the chunks round the player finds
/// both, the fires' own way, and costs a box of reads every couple of seconds.
/// </remarks>
public sealed class Hives
{
    /// <summary>How far round the player a house keeps its bees out.</summary>
    public const int Reach = 24;

    /// <summary>How many bees a house keeps.</summary>
    public const int BeesPerHouse = 3;

    private readonly bool[] _house;
    private readonly List<(int X, int Y, int Z)> _found = [];

    public Hives(BlockRegistry registry)
    {
        _house = new bool[registry.Count];
        for (var id = 1; id < registry.Count; id++)
            _house[id] = registry[(ushort)id].Use == BlockUse.Hive;
    }

    /// <summary>Whether this block is a nest or a hive at any honey level.</summary>
    public bool IsHouse(BlockId block) => block.Value < _house.Length && _house[block.Value];

    /// <summary>What the last sweep found.</summary>
    public IReadOnlyList<(int X, int Y, int Z)> Found => _found;

    /// <summary>Walks the loaded chunks in the box round the viewer for houses.</summary>
    public void Sweep(VoxelWorld world, Vector3 viewer)
    {
        _found.Clear();

        var cx = (int)MathF.Floor(viewer.X);
        var cy = (int)MathF.Floor(viewer.Y);
        var cz = (int)MathF.Floor(viewer.Z);

        var min = ChunkPos.FromWorld(cx - Reach, cy - Reach, cz - Reach);
        var max = ChunkPos.FromWorld(cx + Reach, cy + Reach, cz + Reach);

        for (var chunkY = min.Y; chunkY <= max.Y; chunkY++)
        for (var chunkZ = min.Z; chunkZ <= max.Z; chunkZ++)
        for (var chunkX = min.X; chunkX <= max.X; chunkX++)
        {
            if (!world.TryGetChunk(new ChunkPos(chunkX, chunkY, chunkZ), out var chunk)) continue;
            if (chunk.IsEmpty) continue;

            var (ox, oy, oz) = chunk.Position.Origin;
            var raw = chunk.Raw;
            var x0 = Math.Max(0, cx - Reach - ox);
            var x1 = Math.Min(Chunk.SizeMask, cx + Reach - ox);
            var y0 = Math.Max(0, cy - Reach - oy);
            var y1 = Math.Min(Chunk.SizeMask, cy + Reach - oy);
            var z0 = Math.Max(0, cz - Reach - oz);
            var z1 = Math.Min(Chunk.SizeMask, cz + Reach - oz);

            for (var y = y0; y <= y1; y++)
            for (var z = z0; z <= z1; z++)
            for (var x = x0; x <= x1; x++)
                if (_house[raw[Chunk.Index(x, y, z)]])
                    _found.Add((ox + x, oy + y, oz + z));
        }
    }
}
