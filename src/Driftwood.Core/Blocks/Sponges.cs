using Driftwood.Core.World;

namespace Driftwood.Core.Blocks;

/// <summary>
/// The sponge (2026-10-25): put down by water, it drinks the water round it and turns wet — the
/// reference's rule, up to sixty-five cells no further than seven steps away through the water.
/// </summary>
public static class Sponges
{
    public const int Reach = 7;
    public const int MostCells = 65;

    /// <summary>Drinks the water touching a sponge at a cell. Returns how many cells it took.</summary>
    public static int Soak(VoxelWorld world, int x, int y, int z, Func<BlockId, bool> isWater,
        Action<int, int, int, BlockId> write)
    {
        var seen = new HashSet<(int, int, int)> { (x, y, z) };
        var queue = new Queue<(int X, int Y, int Z, int Steps)>();
        queue.Enqueue((x, y, z, 0));
        var taken = 0;

        while (queue.Count > 0 && taken < MostCells)
        {
            var (cx, cy, cz, steps) = queue.Dequeue();
            if (steps >= Reach) continue;

            foreach (var (dx, dy, dz) in Faces.Normals)
            {
                var n = (cx + dx, cy + dy, cz + dz);
                if (!seen.Add(n) || !isWater(world.GetBlock(n.Item1, n.Item2, n.Item3))) continue;

                write(n.Item1, n.Item2, n.Item3, BlockId.Air);
                if (++taken >= MostCells) break;
                queue.Enqueue((n.Item1, n.Item2, n.Item3, steps + 1));
            }
        }

        return taken;
    }
}
