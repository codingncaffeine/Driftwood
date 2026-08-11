using Driftwood.Core.Gen;

namespace Driftwood.Core.World;

/// <summary>A saved, exploration-only map: one sampled surface tile per visited horizontal chunk.</summary>
public sealed class WorldMap
{
    public enum Surface : byte { Grass, Soil, Sand, Stone, Snow, Wood, Water, Other }

    public readonly record struct Tile(int X, int Z, Biome Biome, Surface Top, short Height);

    public readonly record struct Marker(string Id, string Label, int X, int Z, byte Kind);

    private readonly Dictionary<(int X, int Z), Tile> _tiles = [];
    private readonly HashSet<(int X, int Z)> _visited = [];
    private readonly Dictionary<string, Marker> _markers = new(StringComparer.Ordinal);

    public IReadOnlyCollection<Tile> Tiles => _tiles.Values;
    public IReadOnlyCollection<Marker> Markers => _markers.Values;
    public bool Dirty { get; private set; }

    /// <summary>Records the chunk under the player once, using the generator's authoritative surface.</summary>
    public bool Visit(float worldX, float worldZ, TerrainGenerator terrain, VoxelWorld world)
    {
        var at = ChunkPos.FromWorld((int)MathF.Floor(worldX), 0, (int)MathF.Floor(worldZ));
        if (!_visited.Add((at.X, at.Z))) return false;

        var ox = at.X * Chunk.Size;
        var oz = at.Z * Chunk.Size;
        for (var dz = 0; dz < Chunk.Size; dz++)
        for (var dx = 0; dx < Chunk.Size; dx++)
        {
            var x = ox + dx;
            var z = oz + dz;
            var generated = terrain.SurfaceHeight(x, z);
            var height = generated;
            var top = Surface.Other;

            // Read the whole column, including towers somebody built above the natural surface.
            // Grass and flowers are not the ground; this is the same distinction the audit makes.
            for (var y = TerrainGenerator.WorldTop - 1; y >= TerrainGenerator.WorldBottom; y--)
            {
                var block = world.Registry[world.GetBlock(x, y, z)];
                if (!block.Opaque) continue;
                height = y;
                top = SurfaceOf(block.Name);
                break;
            }

            var biome = terrain.BiomeAt(x, z, generated);
            if (biome is Biome.Sea or Biome.FrozenSea) top = Surface.Water;
            _tiles[(x, z)] = new Tile(x, z, biome, top, (short)height);
        }
        Dirty = true;
        return true;
    }

    public void Reload(IEnumerable<Tile> tiles)
    {
        _tiles.Clear();
        _visited.Clear();
        foreach (var tile in tiles)
        {
            _tiles[(tile.X, tile.Z)] = tile;
            _visited.Add((tile.X >> Chunk.SizeLog2, tile.Z >> Chunk.SizeLog2));
        }
        Dirty = false;
    }

    /// <summary>Reveals a named destination once; charts never erase ordinary exploration.</summary>
    public bool Reveal(Marker marker)
    {
        if (_markers.ContainsKey(marker.Id)) return false;
        _markers.Add(marker.Id, marker);
        Dirty = true;
        return true;
    }

    public void ReloadMarkers(IEnumerable<Marker> markers)
    {
        _markers.Clear();
        foreach (var marker in markers) _markers[marker.Id] = marker;
        Dirty = false;
    }

    private static Surface SurfaceOf(string name) =>
        name.Contains("snow", StringComparison.Ordinal) || name.Contains("ice", StringComparison.Ordinal)
            ? Surface.Snow
        : name.Contains("sand", StringComparison.Ordinal) ? Surface.Sand
        : name.Contains("grass", StringComparison.Ordinal) ? Surface.Grass
        : name.Contains("dirt", StringComparison.Ordinal) || name.Contains("clay", StringComparison.Ordinal)
            ? Surface.Soil
        : name.Contains("log", StringComparison.Ordinal) || name.Contains("wood", StringComparison.Ordinal)
            || name.Contains("plank", StringComparison.Ordinal) ? Surface.Wood
        : name.Contains("stone", StringComparison.Ordinal) || name.Contains("ore", StringComparison.Ordinal)
            || name.Contains("rubble", StringComparison.Ordinal) ? Surface.Stone
        : Surface.Other;

    public void Settled() => Dirty = false;
}
