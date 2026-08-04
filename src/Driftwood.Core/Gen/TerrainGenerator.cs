using Driftwood.Core.Blocks;
using Driftwood.Core.World;

namespace Driftwood.Core.Gen;

/// <summary>
/// Turns a <see cref="WorldSeed"/> into terrain. Fully deterministic: the same seed always
/// produces the same world, and chunks may be generated in any order on any thread.
/// </summary>
/// <remarks>
/// Generation runs in two passes. <see cref="GenerateChunk"/> fills a chunk from its own columns
/// only, so it never needs a neighbour. <see cref="DecorateRegion"/> then plants trees, which do
/// straddle chunk seams and therefore write through the world.
/// <para>That split is deliberate. Once chunks stream in at P1 a tree whose trunk is in a loaded
/// chunk and whose canopy is in an unloaded one needs deferred placement — the structure gets
/// queued against the chunk that is not there yet and applied when it arrives. Keeping decoration
/// already separated from terrain fill is what makes that change a swap of one pass rather than a
/// rewrite of the generator.</para>
/// </remarks>
public sealed class TerrainGenerator
{
    public const int SeaLevel = 62;
    public const int WorldHeight = 128;

    /// <summary>Trees are rolled once per cell of this grid, which spaces them without clumping.</summary>
    private const int TreeGrid = 7;

    private readonly StarterBlocks.Ids _ids;

    private readonly int _seedContinent;
    private readonly int _seedHills;
    private readonly int _seedDetail;
    private readonly int _seedCaveA;
    private readonly int _seedCaveB;
    private readonly int _seedCoal;
    private readonly int _seedIron;
    private readonly int _seedGravel;
    private readonly int _seedTree;

    public WorldSeed Seed { get; }

    public TerrainGenerator(WorldSeed seed, StarterBlocks.Ids ids)
    {
        Seed = seed;
        _ids = ids;

        _seedContinent = seed.Derive("terrain.continent");
        _seedHills = seed.Derive("terrain.hills");
        _seedDetail = seed.Derive("terrain.detail");
        _seedCaveA = seed.Derive("caves.a");
        _seedCaveB = seed.Derive("caves.b");
        _seedCoal = seed.Derive("ore.coal");
        _seedIron = seed.Derive("ore.iron");
        _seedGravel = seed.Derive("deposit.gravel");
        _seedTree = seed.Derive("decor.tree");
    }

    /// <summary>Surface height for a world column: the Y of its topmost terrain block.</summary>
    /// <remarks>
    /// Three scales: continents set the broad land/sea shape, hills give it relief, detail keeps
    /// ridgelines from looking machined.
    /// <para>Continentalness runs through a shaping curve before it becomes height. Raw fBm is
    /// bell-shaped around zero, so scaling it directly produces a world of uniform rolling hills —
    /// every column lands near the mean and nothing is ever flat or ever steep. The curve
    /// compresses the middle and stretches the tails, which is what separates plains from
    /// mountains. It also has to earn back the range fBm never uses: normalising by the sum of
    /// octave amplitudes leaves the output at roughly +/-0.4, not +/-1, so a constant that reads
    /// like "44 blocks of relief" would otherwise deliver about 17.</para>
    /// </remarks>
    public int SurfaceHeight(int wx, int wz)
    {
        var x = wx;
        var z = wz;

        var continent = Noise.Fbm2(x / 640f, z / 640f, _seedContinent, 5);
        var shaped = MathF.Sign(continent) * MathF.Pow(MathF.Min(MathF.Abs(continent) * 2.4f, 1f), 1.6f);

        var hills = Noise.Fbm2(x / 150f, z / 150f, _seedHills, 4);
        var detail = Noise.Fbm2(x / 38f, z / 38f, _seedDetail, 3);

        // Hills flatten out over deep ocean so the seabed does not mirror the mountains.
        var landness = Math.Clamp(shaped * 2f + 0.6f, 0.15f, 1f);

        var h = SeaLevel + shaped * 44f + hills * 16f * landness + detail * 4f;
        return Math.Clamp((int)MathF.Round(h), 1, WorldHeight - 8);
    }

    /// <summary>Fills one chunk with stone, soil, ore, water and caves. Neighbour-free.</summary>
    public void GenerateChunk(Chunk chunk)
    {
        var (ox, oy, oz) = chunk.Position.Origin;

        // One heightmap for the chunk; recomputing per block would evaluate the same
        // three fBm stacks 32 times over.
        Span<int> heights = stackalloc int[Chunk.Size * Chunk.Size];
        for (var z = 0; z < Chunk.Size; z++)
        for (var x = 0; x < Chunk.Size; x++)
            heights[z * Chunk.Size + x] = SurfaceHeight(ox + x, oz + z);

        var raw = chunk.Raw;

        for (var z = 0; z < Chunk.Size; z++)
        for (var x = 0; x < Chunk.Size; x++)
        {
            var surface = heights[z * Chunk.Size + x];
            var wx = ox + x;
            var wz = oz + z;
            var beach = surface <= SeaLevel + 2;

            for (var y = 0; y < Chunk.Size; y++)
            {
                var wy = oy + y;
                if (wy >= WorldHeight) break;

                ushort id;

                if (wy == 0)
                {
                    id = _ids.Bedrock;
                }
                else if (wy > surface)
                {
                    id = wy <= SeaLevel ? _ids.Water.Value : (ushort)0;
                }
                else
                {
                    var depth = surface - wy;
                    if (depth == 0) id = beach ? _ids.Sand.Value : _ids.Grass.Value;
                    else if (depth <= 3) id = beach ? _ids.Sand.Value : _ids.Dirt.Value;
                    else id = _ids.Stone;

                    // Caves cut through everything but bedrock and the seabed. Two independent
                    // fields intersected gives connected tunnels; a single field gives flat sheets.
                    if (wy > 1 && depth > 1 && IsCave(wx, wy, wz))
                    {
                        // Do not drain the ocean into a cave system.
                        id = wy <= SeaLevel && surface <= SeaLevel ? id : (ushort)0;
                    }

                    if (id == _ids.Stone.Value) id = OreAt(wx, wy, wz);
                }

                raw[Chunk.Index(x, y, z)] = id;
            }
        }

        chunk.RecountSolid();
    }

    private bool IsCave(int x, int y, int z)
    {
        const float threshold = 0.08f;
        var a = Noise.Fbm3(x / 48f, y / 24f, z / 48f, _seedCaveA, 3);
        if (MathF.Abs(a) >= threshold) return false;
        var b = Noise.Fbm3(x / 48f, y / 24f, z / 48f, _seedCaveB, 3);
        return MathF.Abs(b) < threshold;
    }

    /// <remarks>
    /// Thresholds are calibrated against what two-octave fBm actually produces, which peaks near
    /// +/-0.5 rather than +/-1 — a threshold that reads as "rare" on paper spawns nothing at all.
    /// <para>Noise thresholding gives blobs but only indirect control over how much ore exists.
    /// P4 replaces this with explicit vein placement — roll N vein origins per chunk per depth
    /// band and grow each one — which is how a designer sets "iron should be twice as common as
    /// gold" and gets it. The census in <c>--audit</c> is what keeps either method honest.</para>
    /// </remarks>
    private ushort OreAt(int x, int y, int z)
    {
        // Iron sits deeper and rarer than coal, which is the first progression gate the
        // survival loop leans on.
        // Target mix, checked by the audit: coal near 0.8% of stone, iron near 0.35%, coal
        // roughly twice as common as iron so the first tool tier is the easy one.
        if (y is >= 4 and <= 58 && Noise.Fbm3(x / 9f, y / 9f, z / 9f, _seedIron, 2) > 0.50f)
            return _ids.IronOre;
        if (y is >= 4 and <= 92 && Noise.Fbm3(x / 12f, y / 12f, z / 12f, _seedCoal, 2) > 0.475f)
            return _ids.CoalOre;
        if (Noise.Fbm3(x / 14f, y / 14f, z / 14f, _seedGravel, 2) > 0.50f)
            return _ids.Gravel;
        return _ids.Stone;
    }

    /// <summary>
    /// Plants trees across a world-block rectangle. Runs after terrain fill because a canopy can
    /// reach into a neighbouring chunk.
    /// </summary>
    public void DecorateRegion(VoxelWorld world, int minX, int minZ, int maxX, int maxZ)
    {
        var cellMinX = (int)MathF.Floor(minX / (float)TreeGrid);
        var cellMaxX = (int)MathF.Floor(maxX / (float)TreeGrid);
        var cellMinZ = (int)MathF.Floor(minZ / (float)TreeGrid);
        var cellMaxZ = (int)MathF.Floor(maxZ / (float)TreeGrid);

        for (var cz = cellMinZ; cz <= cellMaxZ; cz++)
        for (var cx = cellMinX; cx <= cellMaxX; cx++)
        {
            // Roughly half of cells grow a tree; the rest stay clear so forests have gaps.
            if (Noise.Value2(cx, cz, _seedTree) > 0.45f) continue;

            // Jitter inside the cell so the grid never shows through as rows.
            var jx = (int)(Noise.Value2(cx, cz, _seedTree + 17) * TreeGrid);
            var jz = (int)(Noise.Value2(cx, cz, _seedTree + 31) * TreeGrid);
            var wx = cx * TreeGrid + jx;
            var wz = cz * TreeGrid + jz;

            var surface = SurfaceHeight(wx, wz);
            if (surface <= SeaLevel + 2) continue;                              // beaches and water stay bare
            if (world.GetBlock(wx, surface, wz) != _ids.Grass) continue;        // cave mouth or sand
            if (!world.GetBlock(wx, surface + 1, wz).IsAir) continue;

            var height = 4 + (int)(Noise.Value2(cx, cz, _seedTree + 53) * 3f);
            PlantOak(world, wx, surface + 1, wz, height);
        }
    }

    private void PlantOak(VoxelWorld world, int x, int baseY, int z, int trunkHeight)
    {
        var topY = baseY + trunkHeight - 1;

        // Canopy first, so the trunk overwrites any leaf that lands in its column.
        for (var dy = -2; dy <= 1; dy++)
        {
            var y = topY + dy;
            var radius = dy >= 0 ? 1 : 2;

            for (var dz = -radius; dz <= radius; dz++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                // Clip the outer corners so the canopy reads as round, not as a cube.
                if (radius == 2 && Math.Abs(dx) == 2 && Math.Abs(dz) == 2) continue;
                if (!world.GetBlock(x + dx, y, z + dz).IsAir) continue;
                world.SetBlock(x + dx, y, z + dz, _ids.Leaves);
            }
        }

        for (var i = 0; i < trunkHeight; i++)
            world.SetBlock(x, baseY + i, z, _ids.Log);
    }
}
