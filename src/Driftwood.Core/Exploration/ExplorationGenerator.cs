using System.Collections.Concurrent;
using Driftwood.Core.Blocks;
using Driftwood.Core.Gen;
using Driftwood.Core.World;

namespace Driftwood.Core.Exploration;

/// <summary>The authored discoveries P14 adds to deterministic terrain.</summary>
public enum StructureKind : byte
{
    BuriedGallery,
    Driftstead,
    Tidewreck,
    StormVault,
    StarfallCrown,

    /// <summary>The Emberdeep's fortress (2026-10-25): the reference's Nether fortress, ours.</summary>
    Emberhold,

    /// <summary>The Sanctum (2026-10-25): the reference's stronghold — the gate down to the Core.</summary>
    Sanctum,
}

/// <summary>A structure's identity and bounds. The id survives block-id and palette changes.</summary>
public readonly record struct StructureSite(
    string Id, StructureKind Kind, int CellX, int CellZ, int X, int Y, int Z, int Radius,
    int MinY, int MaxY)
{
    public (int X, int Y, int Z) Centre => (X, Y, Z);
    public (int Min, int Max) VerticalBounds => (MinY, MaxY);
}

/// <summary>
/// Pure, order-independent modular structures. A caller asks for the cells reaching one chunk;
/// no structure ever reads another chunk or remembers which chunk happened to arrive first.
/// </summary>
public sealed class ExplorationGenerator
{
    public const int PaletteVersion = 1;
    public const int MaxRadius = 20;

    private readonly WorldSeed _seed;
    private readonly StarterBlocks.Ids _ids;
    private readonly Func<int, int, int> _surface;
    private readonly Func<int, int, int, Biome> _biome;

    private readonly int _layoutSeed;
    private readonly ConcurrentDictionary<(StructureKind Kind, int X, int Z), StructureSite?> _siteCache = new();

    /// <summary>
    /// Names, never runtime ids: save migrations and audits can resolve this palette after any
    /// registration-order change.
    /// </summary>
    public static readonly string[] PaletteNames =
    [
        "air", "stone", "rubble", "moss", "bricks", "driftoak_log", "driftoak_planks",
        "chest_north", "rail_x", "lantern", "sandstone", "cobweb", "glass", "mossy_rubble",
    ];

    public ExplorationGenerator(
        WorldSeed seed,
        StarterBlocks.Ids ids,
        Func<int, int, int> surface,
        Func<int, int, int, Biome> biome)
    {
        _seed = seed;
        _ids = ids;
        _surface = surface;
        _biome = biome;
        _layoutSeed = seed.Derive("exploration.p14.layout.v1");
    }

    public static int GridFor(StructureKind kind) => kind switch
    {
        StructureKind.BuriedGallery => 224,
        StructureKind.Driftstead => 320,
        StructureKind.Tidewreck => 256,
        StructureKind.StormVault => 448,
        StructureKind.StarfallCrown => 960,
        StructureKind.Emberhold => 384,
        StructureKind.Sanctum => 768,
        _ => 320,
    };

    /// <summary>Returns one deterministic candidate for a kind/grid cell, when its site fits.</summary>
    public bool TrySiteAt(StructureKind kind, int cellX, int cellZ, out StructureSite site)
    {
        var cached = _siteCache.GetOrAdd((kind, cellX, cellZ), key =>
            BuildSite(key.Kind, key.X, key.Z));
        if (cached is { } found)
        {
            site = found;
            return true;
        }
        site = default;
        return false;
    }

    private StructureSite? BuildSite(StructureKind kind, int cellX, int cellZ)
    {
        var grid = GridFor(kind);
        var salt = _layoutSeed + (int)kind * 7919;

        // Not every coarse cell gets a landmark. The chance is kind-specific, but the roll's
        // stream is stable and independent of all later shape rolls.
        var chance = kind switch
        {
            StructureKind.BuriedGallery => 0.58f,
            StructureKind.Driftstead => 0.42f,
            StructureKind.Tidewreck => 0.62f,
            StructureKind.StormVault => 0.38f,
            StructureKind.StarfallCrown => 0.52f,
            StructureKind.Emberhold => 0.7f,
            StructureKind.Sanctum => 0.8f,
            _ => 0f,
        };
        if (Noise.Value2(cellX, cellZ, salt) >= chance) return null;

        var margin = MaxRadius + 6;
        var span = Math.Max(1, grid - margin * 2);
        var x = cellX * grid + margin + (int)(Noise.Value2(cellX, cellZ, salt + 17) * span);
        var z = cellZ * grid + margin + (int)(Noise.Value2(cellX, cellZ, salt + 31) * span);

        // A wreck genuinely belongs to the sea. Search a fixed nine-point rosette inside its own
        // cell; if that cell has no water, it simply has no wreck.
        if (kind == StructureKind.Tidewreck)
        {
            var found = false;
            ReadOnlySpan<(int X, int Z)> offsets =
            [
                (0, 0), (28, 0), (-28, 0), (0, 28), (0, -28),
                (28, 28), (-28, 28), (28, -28), (-28, -28),
            ];
            foreach (var offset in offsets)
            {
                var px = x + offset.X;
                var pz = z + offset.Z;
                if (_surface(px, pz) > TerrainGenerator.SeaLevel - 3) continue;
                x = px;
                z = pz;
                found = true;
                break;
            }
            if (!found) return null;
        }

        var surface = _surface(x, z);
        if (kind == StructureKind.Driftstead)
        {
            if (surface <= TerrainGenerator.SeaLevel + 2) return null;
            var region = _biome(x, z, surface);
            if (region is Biome.Sea or Biome.FrozenSea or Biome.Dunes or Biome.Drylands or Biome.Highlands)
                return null;
        }

        if (kind == StructureKind.StarfallCrown)
        {
            if (surface <= TerrainGenerator.SeaLevel + 5) return null;
            // The Crown likes a view, but is allowed on a strong hill outside the named highlands.
            var rim = Math.Max(
                Math.Abs(_surface(x + 12, z) - surface),
                Math.Abs(_surface(x, z + 12) - surface));
            if (_biome(x, z, surface) != Biome.Highlands && surface < 78 && rim < 5) return null;
        }

        var y = kind switch
        {
            StructureKind.BuriedGallery => Math.Max(TerrainGenerator.WorldBottom + 12, surface - 24),
            StructureKind.Tidewreck => TerrainGenerator.SeaLevel - 5,
            StructureKind.StormVault => Math.Max(TerrainGenerator.WorldBottom + 12, surface - 15),

            // ⛳ The Emberhold keeps to the Emberdeep whatever the surface does: a floor somewhere
            // forty to fifty below its top, above the molten sea at the bottom of the world.
            StructureKind.Emberhold => TerrainGenerator.EmberdeepTop - 40 - (int)(Noise.Value2(cellX, cellZ, salt + 47) * 10f),

            // The Sanctum sits in the hollows, between the deep and the Emberdeep.
            StructureKind.Sanctum => TerrainGenerator.HollowsTop - 26,
            _ => surface + 1,
        };
        var radius = kind switch
        {
            StructureKind.BuriedGallery => 19,
            StructureKind.Driftstead => 20,
            StructureKind.Tidewreck => 13,
            StructureKind.StormVault => 12,
            StructureKind.StarfallCrown => 18,
            StructureKind.Emberhold => 20,
            StructureKind.Sanctum => 18,
            _ => MaxRadius,
        };

        var minY = kind switch
        {
            StructureKind.BuriedGallery => y,
            StructureKind.Driftstead => y - 1,
            StructureKind.Tidewreck => y - 1,
            StructureKind.StormVault => y - 1,
            StructureKind.StarfallCrown => y,
            StructureKind.Emberhold => y - HoldPillars - 1,
            StructureKind.Sanctum => y - 2,
            _ => y,
        };
        var maxY = kind switch
        {
            StructureKind.BuriedGallery => y + 5,
            StructureKind.Driftstead => y + 4,
            StructureKind.Tidewreck => TerrainGenerator.SeaLevel + 3,
            StructureKind.StormVault => y + 9,
            StructureKind.StarfallCrown => y + 14,
            StructureKind.Emberhold => y + 8,
            StructureKind.Sanctum => y + 8,
            _ => y,
        };

        // Driftstead's paths follow the actual ground rather than hovering at the well's height.
        // Capture their exact vertical reach in the immutable site, so a path crossing a vertical
        // chunk edge cannot be skipped by the paint fast-path.
        if (kind == StructureKind.Driftstead)
        {
            for (var d = -14; d <= 14; d++)
            {
                var eastWest = _surface(x + d, z) + 1;
                var northSouth = _surface(x, z + d) + 1;
                minY = Math.Min(minY, Math.Min(eastWest, northSouth));
                maxY = Math.Max(maxY, Math.Max(eastWest, northSouth));
            }
        }

        return new StructureSite(
            $"p14/{kind.ToString().ToLowerInvariant()}/{cellX}/{cellZ}",
            kind, cellX, cellZ, x, y, z, radius, minY, maxY);
    }

    /// <summary>Every site whose horizontal bounds can reach a rectangle, in one global order.</summary>
    public IEnumerable<StructureSite> SitesAffecting(int minX, int maxX, int minZ, int maxZ, int reach)
    {
        var gathered = new List<StructureSite>();
        foreach (var kind in Enum.GetValues<StructureKind>())
        {
            var grid = GridFor(kind);
            var cx0 = FloorDiv(minX - reach, grid);
            var cx1 = FloorDiv(maxX + reach, grid);
            var cz0 = FloorDiv(minZ - reach, grid);
            var cz1 = FloorDiv(maxZ + reach, grid);

            for (var cz = cz0; cz <= cz1; cz++)
            for (var cx = cx0; cx <= cx1; cx++)
            {
                if (!TrySiteAt(kind, cx, cz, out var candidate)) continue;
                if (candidate.X + candidate.Radius < minX || candidate.X - candidate.Radius > maxX
                    || candidate.Z + candidate.Radius < minZ || candidate.Z - candidate.Radius > maxZ)
                    continue;
                gathered.Add(candidate);
            }
        }

        return gathered.OrderBy(one => one.Kind).ThenBy(one => one.CellZ).ThenBy(one => one.CellX);
    }

    /// <summary>Nearest authored site of a kind, searching a bounded deterministic ring.</summary>
    public StructureSite? FindNearest(StructureKind kind, int x, int z, int rings = 12)
    {
        var grid = GridFor(kind);
        var centreX = FloorDiv(x, grid);
        var centreZ = FloorDiv(z, grid);
        StructureSite? best = null;
        long bestDistance = long.MaxValue;

        for (var ring = 0; ring <= rings; ring++)
        for (var dz = -ring; dz <= ring; dz++)
        for (var dx = -ring; dx <= ring; dx++)
        {
            if (ring > 0 && Math.Abs(dx) != ring && Math.Abs(dz) != ring) continue;
            if (!TrySiteAt(kind, centreX + dx, centreZ + dz, out var site)) continue;
            var sx = (long)site.X - x;
            var sz = (long)site.Z - z;
            var distance = sx * sx + sz * sz;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = site;
        }
        return best;
    }

    public bool TrySiteById(string id, out StructureSite site)
    {
        site = default;
        var parts = id.Split('/');
        if (parts.Length != 4 || parts[0] != "p14"
            || !Enum.TryParse<StructureKind>(parts[1], ignoreCase: true, out var kind)
            || !int.TryParse(parts[2], out var cellX)
            || !int.TryParse(parts[3], out var cellZ)) return false;
        return TrySiteAt(kind, cellX, cellZ, out site) && site.Id == id;
    }

    /// <summary>Writes this chunk's share of every authored structure reaching it.</summary>
    public void PaintChunk(Chunk chunk, int reach)
    {
        var (ox, oy, oz) = chunk.Position.Origin;
        var touched = false;
        foreach (var site in SitesAffecting(
                     ox, ox + Chunk.Size - 1, oz, oz + Chunk.Size - 1, reach))
        {
            var vertical = site.VerticalBounds;
            if (vertical.Max < oy || vertical.Min >= oy + Chunk.Size) continue;
            Walk(site, (x, y, z, block) =>
            {
                if (x < ox || x >= ox + Chunk.Size || y < oy || y >= oy + Chunk.Size
                    || z < oz || z >= oz + Chunk.Size || !TerrainGenerator.InWorld(y)) return;
                chunk.Set(x - ox, y - oy, z - oz, block);
                touched = true;
            });
        }
        if (touched) chunk.Dirty = true;
    }

    /// <summary>The whole authored structure, shared by generation, loot lookup and audits.</summary>
    public void Walk(StructureSite site, Action<int, int, int, BlockId> emit)
    {
        switch (site.Kind)
        {
            case StructureKind.BuriedGallery: WalkGallery(site, emit); break;
            case StructureKind.Driftstead: WalkSettlement(site, emit); break;
            case StructureKind.Tidewreck: WalkWreck(site, emit); break;
            case StructureKind.StormVault: WalkVault(site, emit); break;
            case StructureKind.StarfallCrown: WalkCrown(site, emit); break;
            case StructureKind.Emberhold: WalkHold(site, emit); break;
            case StructureKind.Sanctum: WalkSanctum(site, emit); break;
        }
    }

    public IReadOnlyList<(int X, int Y, int Z)> ChestCells(StructureSite site)
    {
        var cells = new List<(int X, int Y, int Z)>();
        Walk(site, (x, y, z, block) => { if (block == _ids.Chest) cells.Add((x, y, z)); });
        return cells.Distinct().ToArray();
    }

    /// <summary>Where an Emberhold's cinder cages stand — the cells the cinderwisps come out of.</summary>
    public IReadOnlyList<(int X, int Y, int Z)> CageCells(StructureSite site)
    {
        if (site.Kind != StructureKind.Emberhold) return [];
        var cells = new List<(int X, int Y, int Z)>();
        Walk(site, (x, y, z, block) => { if (block == _ids.CinderCage) cells.Add((x, y, z)); });
        return cells.Distinct().ToArray();
    }

    /// <summary>A Sanctum's twelve gate sockets, round the three-by-three.</summary>
    public static IReadOnlyList<(int X, int Y, int Z)> SocketCells(StructureSite site)
    {
        if (site.Kind != StructureKind.Sanctum) return [];
        var cells = new List<(int, int, int)>(12);
        for (var d = -1; d <= 1; d++)
        {
            cells.Add((site.X + d, site.Y, site.Z - 2));
            cells.Add((site.X + d, site.Y, site.Z + 2));
            cells.Add((site.X - 2, site.Y, site.Z + d));
            cells.Add((site.X + 2, site.Y, site.Z + d));
        }
        return cells;
    }

    /// <summary>The nine cells a Sanctum's gate opens in.</summary>
    public static IReadOnlyList<(int X, int Y, int Z)> GateCells(StructureSite site)
    {
        if (site.Kind != StructureKind.Sanctum) return [];
        var cells = new List<(int, int, int)>(9);
        for (var dz = -1; dz <= 1; dz++)
        for (var dx = -1; dx <= 1; dx++)
            cells.Add((site.X + dx, site.Y, site.Z + dz));
        return cells;
    }

    /// <summary>One brushable rubble pocket in each gallery and wreck.</summary>
    public (int X, int Y, int Z)? ArchaeologyCell(StructureSite site) => site.Kind switch
    {
        StructureKind.BuriedGallery => (site.X - 7, site.Y + 1, site.Z + 7),
        StructureKind.Tidewreck => (site.X + 5, site.Y, site.Z + 2),
        _ => null,
    };

    /// <summary>Four owned home/work/commons anchors for a generated settlement.</summary>
    public IReadOnlyList<(int X, int Y, int Z, int HomeX, int HomeZ, int WorkX, int WorkZ)> Residents(
        StructureSite site)
    {
        if (site.Kind != StructureKind.Driftstead) return [];
        var y = site.Y;
        return
        [
            (site.X - 8, y, site.Z - 5, site.X - 9, site.Z - 7, site.X, site.Z),
            (site.X + 8, y, site.Z - 4, site.X + 9, site.Z - 7, site.X + 3, site.Z + 5),
            (site.X, y, site.Z + 9, site.X - 1, site.Z + 10, site.X - 5, site.Z + 4),
            (site.X, y, site.Z - 10, site.X + 1, site.Z - 11, site.X - 3, site.Z - 4),
        ];
    }

    private void WalkGallery(StructureSite s, Action<int, int, int, BlockId> emit)
    {
        // A mineshaft spine with two rooms. Interiors are carved first, then supports and track.
        CarveBox(s.X - 18, s.Y, s.Z - 2, s.X + 18, s.Y + 4, s.Z + 2, emit);
        CarveBox(s.X - 9, s.Y, s.Z - 9, s.X + 9, s.Y + 5, s.Z + 9, emit);
        CarveBox(s.X + 11, s.Y, s.Z - 7, s.X + 17, s.Y + 4, s.Z + 7, emit);

        for (var x = s.X - 17; x <= s.X + 17; x++) emit(x, s.Y, s.Z, _ids.Rail);
        for (var x = s.X - 15; x <= s.X + 15; x += 5)
        {
            for (var y = s.Y + 1; y <= s.Y + 4; y++)
            {
                emit(x, y, s.Z - 2, _ids.Log);
                emit(x, y, s.Z + 2, _ids.Log);
            }
            for (var z = s.Z - 2; z <= s.Z + 2; z++) emit(x, s.Y + 4, z, _ids.Planks);
        }

        for (var z = s.Z - 8; z <= s.Z + 8; z++)
        {
            emit(s.X - 9, s.Y, z, _ids.Rubble);
            emit(s.X + 9, s.Y, z, _ids.Rubble);
        }
        emit(s.X - 7, s.Y + 1, s.Z + 7, _ids.MossyRubble);
        emit(s.X + 14, s.Y + 1, s.Z + 5, _ids.Chest);
        emit(s.X - 7, s.Y + 1, s.Z - 7, _ids.Chest);
        emit(s.X, s.Y + 4, s.Z + 8, _ids.Cobweb);
        emit(s.X + 10, s.Y + 3, s.Z, _ids.Lantern);
    }

    private void WalkSettlement(StructureSite s, Action<int, int, int, BlockId> emit)
    {
        // Commons and well. A cross of rubble paths ties the three homes together.
        for (var d = -14; d <= 14; d++)
        {
            emit(s.X + d, _surface(s.X + d, s.Z) + 1, s.Z, _ids.Rubble);
            emit(s.X, _surface(s.X, s.Z + d) + 1, s.Z + d, _ids.Rubble);
        }
        Ring(s.X, s.Y, s.Z, 2, _ids.Bricks, emit);
        emit(s.X, s.Y, s.Z, _ids.Water);
        emit(s.X, s.Y + 1, s.Z, _ids.Lantern);

        Hut(s.X - 12, s.Y, s.Z - 10, 7, 6, emit, chest: true);
        Hut(s.X + 6, s.Y, s.Z - 10, 7, 6, emit, chest: false);
        Hut(s.X - 4, s.Y, s.Z + 7, 8, 7, emit, chest: true);
    }

    private void Hut(int x, int y, int z, int wide, int deep, Action<int, int, int, BlockId> emit, bool chest)
    {
        for (var dz = 0; dz < deep; dz++)
        for (var dx = 0; dx < wide; dx++)
        {
            emit(x + dx, y - 1, z + dz, _ids.Rubble);
            for (var dy = 0; dy <= 3; dy++)
            {
                var wall = dx == 0 || dz == 0 || dx == wide - 1 || dz == deep - 1;
                emit(x + dx, y + dy, z + dz, wall ? _ids.Planks : BlockId.Air);
            }
            emit(x + dx, y + 4, z + dz, _ids.Planks);
        }
        // Door and windows, then a log frame over the wall skin.
        emit(x + wide / 2, y, z, BlockId.Air);
        emit(x + wide / 2, y + 1, z, BlockId.Air);
        emit(x, y + 2, z + deep / 2, _ids.Glass);
        emit(x + wide - 1, y + 2, z + deep / 2, _ids.Glass);
        for (var dy = 0; dy <= 4; dy++)
        {
            emit(x, y + dy, z, _ids.Log);
            emit(x + wide - 1, y + dy, z, _ids.Log);
            emit(x, y + dy, z + deep - 1, _ids.Log);
            emit(x + wide - 1, y + dy, z + deep - 1, _ids.Log);
        }
        if (chest) emit(x + 1, y, z + deep - 2, _ids.Chest);
    }

    private void WalkWreck(StructureSite s, Action<int, int, int, BlockId> emit)
    {
        // A broken clinker hull, canted only in silhouette so every cell stays grid honest.
        for (var x = -10; x <= 10; x++)
        {
            var half = 2 + (10 - Math.Abs(x)) / 3;
            emit(s.X + x, s.Y - 1, s.Z, _ids.Log);
            emit(s.X + x, s.Y, s.Z - half, _ids.Planks);
            emit(s.X + x, s.Y, s.Z + half, _ids.Planks);
            if ((x & 1) == 0)
            {
                emit(s.X + x, s.Y + 1, s.Z - half, _ids.Planks);
                emit(s.X + x, s.Y + 1, s.Z + half, _ids.Planks);
            }
        }
        for (var y = s.Y; y <= TerrainGenerator.SeaLevel + 3; y++) emit(s.X - 2, y, s.Z, _ids.Log);
        for (var z = s.Z - 4; z <= s.Z + 4; z++) emit(s.X - 2, TerrainGenerator.SeaLevel + 2, z, _ids.Planks);
        emit(s.X + 6, s.Y, s.Z, _ids.Chest);
        emit(s.X + 5, s.Y, s.Z + 2, _ids.MossyRubble);
    }

    private void WalkVault(StructureSite s, Action<int, int, int, BlockId> emit)
    {
        CarveBox(s.X - 10, s.Y, s.Z - 10, s.X + 10, s.Y + 8, s.Z + 10, emit);
        for (var y = s.Y; y <= s.Y + 8; y++) Ring(s.X, y, s.Z, 10, _ids.Bricks, emit);
        for (var x = s.X - 10; x <= s.X + 10; x++)
        for (var z = s.Z - 10; z <= s.Z + 10; z++)
        {
            emit(x, s.Y - 1, z, ((x + z) & 3) == 0 ? _ids.Moss : _ids.Rubble);
            emit(x, s.Y + 9, z, _ids.Bricks);
        }
        foreach (var (dx, dz) in new[] { (-7, -7), (7, -7), (-7, 7), (7, 7) })
        for (var y = s.Y; y <= s.Y + 6; y++) emit(s.X + dx, y, s.Z + dz, _ids.BrickOrRubble(y, s.Y));
        emit(s.X, s.Y, s.Z, _ids.Chest);
        emit(s.X, s.Y + 5, s.Z, _ids.Lantern);
    }

    /// <summary>How far an Emberhold's bridges stand on their pillars.</summary>
    private const int HoldPillars = 12;

    /// <summary>
    /// The Emberhold: a keep of ember bricks with a cinder cage at its heart and ashcap beds along
    /// its north wall, four bridges out on pillars, and a tower at the end of each.
    /// </summary>
    /// <remarks>
    /// <para>⛳ The reference's Nether fortress, shaped to the radius every site here keeps. It is
    /// cut through whatever the Emberdeep has at that depth — rock, a hollow, a lava lake — so its
    /// bridges cross open fire in one place and run as tunnels in the next, the way the reference's
    /// fortress cuts through netherrack.</para>
    /// <para>The north and south towers hold the other two cages, the east one a second chest; the
    /// keep's own two chests stand in its southern corners.</para>
    /// </remarks>
    private void WalkHold(StructureSite s, Action<int, int, int, BlockId> emit)
    {
        var brick = _ids.EmberBricks;
        var (x0, y0, z0) = (s.X, s.Y, s.Z);

        // The keep.
        CarveBox(x0 - 6, y0, z0 - 6, x0 + 6, y0 + 6, z0 + 6, emit);
        for (var x = x0 - 7; x <= x0 + 7; x++)
        for (var z = z0 - 7; z <= z0 + 7; z++)
        {
            emit(x, y0 - 1, z, brick);
            emit(x, y0 + 7, z, brick);
        }

        for (var y = y0; y <= y0 + 6; y++)
        {
            for (var d = -7; d <= 7; d++)
            {
                // Doorways three wide and four tall, one in each wall.
                var door = Math.Abs(d) <= 1 && y <= y0 + 3;
                if (!door)
                {
                    emit(x0 + d, y, z0 - 7, brick);
                    emit(x0 + d, y, z0 + 7, brick);
                    emit(x0 - 7, y, z0 + d, brick);
                    emit(x0 + 7, y, z0 + d, brick);
                }
            }

            foreach (var (px, pz) in (ReadOnlySpan<(int, int)>)[(-4, -4), (4, -4), (-4, 4), (4, 4)])
                emit(x0 + px, y, z0 + pz, brick);
        }

        // The garden: ashcap ripe in ashsand along the north wall, either side of the door.
        for (var d = 2; d <= 5; d++)
        foreach (var side in (ReadOnlySpan<int>)[-1, 1])
        {
            emit(x0 + d * side, y0 - 1, z0 - 6, _ids.Ashsand);
            emit(x0 + d * side, y0, z0 - 6, _ids.AshcapRipe);
        }

        emit(x0, y0, z0, _ids.CinderCage);
        emit(x0 - 5, y0, z0 + 5, _ids.Chest);
        emit(x0 + 5, y0, z0 + 5, _ids.Chest);

        // The bridges, and the towers at their ends.
        foreach (var (dx, dz) in (ReadOnlySpan<(int, int)>)[(1, 0), (-1, 0), (0, 1), (0, -1)])
        {
            var (sx, sz) = (dz, dx);   // across the bridge
            for (var along = 8; along <= 15; along++)
            {
                var cx = x0 + dx * along;
                var cz = z0 + dz * along;
                for (var across = -2; across <= 2; across++)
                {
                    var bx = cx + sx * across;
                    var bz = cz + sz * across;
                    emit(bx, y0 - 1, bz, brick);
                    for (var y = y0; y <= y0 + 3; y++)
                        emit(bx, y, bz, Math.Abs(across) == 2 && y == y0 ? brick : BlockId.Air);
                }

                if (along % 4 == 0)
                    foreach (var side in (ReadOnlySpan<int>)[-1, 1])
                    for (var y = y0 - 2; y >= y0 - HoldPillars; y--)
                        emit(cx + sx * side, y, cz + sz * side, brick);
            }

            var tx = x0 + dx * 18;
            var tz = z0 + dz * 18;
            CarveBox(tx - 1, y0, tz - 1, tx + 1, y0 + 4, tz + 1, emit);
            for (var x = tx - 2; x <= tx + 2; x++)
            for (var z = tz - 2; z <= tz + 2; z++)
            {
                emit(x, y0 - 1, z, brick);
                emit(x, y0 + 5, z, brick);
            }

            for (var y = y0; y <= y0 + 4; y++)
            for (var d = -2; d <= 2; d++)
            {
                // The tower's one door faces the keep.
                bool Door(int ox, int oz) => ox == -dx * 2 && Math.Abs(oz) <= 0 || oz == -dz * 2 && Math.Abs(ox) <= 0;
                foreach (var (ox, oz) in (ReadOnlySpan<(int, int)>)[(d, -2), (d, 2), (-2, d), (2, d)])
                {
                    if (y <= y0 + 2 && Door(ox, oz)) continue;
                    emit(tx + ox, y, tz + oz, brick);
                }
            }

            for (var y = y0 - 2; y >= y0 - HoldPillars; y--)
            {
                emit(tx - 2, y, tz - 2, brick);
                emit(tx + 2, y, tz - 2, brick);
                emit(tx - 2, y, tz + 2, brick);
                emit(tx + 2, y, tz + 2, brick);
            }

            if (dz != 0) emit(tx, y0, tz, _ids.CinderCage);
            else if (dx > 0) emit(tx, y0, tz, _ids.Chest);
        }
    }

    /// <summary>
    /// The Sanctum: a hall of deepstone bricks with the gate at its middle — twelve sockets round a
    /// three-by-three pit on a raised dais — four corridors out, and a library with a chest.
    /// </summary>
    /// <remarks>
    /// ⛳ The reference's stronghold, at this radius: the portal room is the point of it, and a seeker's
    /// eye leads here. A few sockets come with an eye already in them — the reference sets each one
    /// at a tenth — fixed by the site, so the same Sanctum always wants the same number of eyes.
    /// </remarks>
    private void WalkSanctum(StructureSite s, Action<int, int, int, BlockId> emit)
    {
        var brick = _ids.DeepstoneBricks;
        var polished = _ids.PolishedDeepstone;
        var (x0, y0, z0) = (s.X, s.Y, s.Z);

        // The hall.
        CarveBox(x0 - 6, y0 - 1, z0 - 6, x0 + 6, y0 + 6, z0 + 6, emit);
        for (var x = x0 - 7; x <= x0 + 7; x++)
        for (var z = z0 - 7; z <= z0 + 7; z++)
        {
            emit(x, y0 - 2, z, brick);
            emit(x, y0 + 7, z, brick);
            if (Math.Abs(x - x0) <= 3 && Math.Abs(z - z0) <= 3) emit(x, y0 - 1, z, polished);
            else emit(x, y0 - 1, z, brick);
        }

        for (var y = y0 - 1; y <= y0 + 6; y++)
        for (var d = -7; d <= 7; d++)
        {
            var door = Math.Abs(d) <= 1 && y >= y0 && y <= y0 + 3;
            if (door) continue;
            emit(x0 + d, y, z0 - 7, brick);
            emit(x0 + d, y, z0 + 7, brick);
            emit(x0 - 7, y, z0 + d, brick);
            emit(x0 + 7, y, z0 + d, brick);
        }

        // The gate: the pit sunk one under the dais, the sockets round it.
        for (var dz = -1; dz <= 1; dz++)
        for (var dx = -1; dx <= 1; dx++)
            emit(x0 + dx, y0 - 1, z0 + dz, BlockId.Air);

        var sockets = SocketCells(s);
        for (var i = 0; i < sockets.Count; i++)
        {
            var (sx, sy, sz) = sockets[i];
            var seeded = Noise.Value2(s.CellX * 13 + i, s.CellZ * 7 - i, _layoutSeed + 997) < 0.1f;
            emit(sx, sy, sz, seeded ? _ids.SanctumSocketFilled : _ids.SanctumSocket);
        }

        foreach (var (lx, lz) in (ReadOnlySpan<(int, int)>)[(-5, -5), (5, -5), (-5, 5), (5, 5)])
            emit(x0 + lx, y0 + 5, z0 + lz, _ids.Lantern);

        // Four corridors out, and the library at the end of the north one.
        foreach (var (dx, dz) in (ReadOnlySpan<(int, int)>)[(1, 0), (-1, 0), (0, 1), (0, -1)])
        {
            var (sx, sz) = (dz, dx);
            for (var along = 8; along <= 13; along++)
            for (var across = -2; across <= 2; across++)
            for (var y = y0 - 1; y <= y0 + 4; y++)
            {
                var (bx, bz) = (x0 + dx * along + sx * across, z0 + dz * along + sz * across);
                var shell = Math.Abs(across) == 2 || y == y0 - 1 || y == y0 + 4;
                emit(bx, y, bz, shell ? brick : BlockId.Air);
            }
        }

        // The library: a small room at the corridor's end, its long walls lined with books.
        var (cx, cz) = (x0, z0 - 16);
        CarveBox(cx - 3, y0, cz - 2, cx + 3, y0 + 4, cz + 2, emit);
        for (var x = cx - 4; x <= cx + 4; x++)
        for (var z = cz - 3; z <= cz + 3; z++)
        {
            emit(x, y0 - 1, z, brick);
            emit(x, y0 + 5, z, brick);

            var wall = Math.Abs(x - cx) == 4 || Math.Abs(z - cz) == 3;
            if (!wall) continue;
            var doorway = z == cz + 3 && Math.Abs(x - cx) <= 1;
            for (var y = y0; y <= y0 + 4; y++)
            {
                if (doorway && y <= y0 + 3) emit(x, y, z, BlockId.Air);
                else emit(x, y, z, Math.Abs(x - cx) == 4 && y <= y0 + 2 ? _ids.Bookshelf : brick);
            }
        }

        emit(cx, y0, cz - 2, _ids.Chest);
        emit(cx, y0 + 3, cz, _ids.Lantern);
    }

    private void WalkCrown(StructureSite s, Action<int, int, int, BlockId> emit)
    {
        // Driftwood's own late landmark: a broken astrolabe on a high crown, not another dimension.
        for (var y = s.Y; y <= s.Y + 13; y++)
        {
            var radius = y < s.Y + 5 ? 3 : y < s.Y + 10 ? 2 : 1;
            Ring(s.X, y, s.Z, radius, y % 3 == 0 ? _ids.Moss : _ids.Bricks, emit);
        }
        Ring(s.X, s.Y, s.Z, 15, _ids.Rubble, emit);
        Ring(s.X, s.Y + 4, s.Z, 11, _ids.Bricks, emit);
        for (var spoke = -10; spoke <= 10; spoke++)
        {
            emit(s.X + spoke, s.Y, s.Z, _ids.Rubble);
            emit(s.X, s.Y, s.Z + spoke, _ids.Rubble);
        }
        emit(s.X, s.Y + 1, s.Z, _ids.Chest);
        emit(s.X, s.Y + 14, s.Z, _ids.Lantern);
    }

    private static void CarveBox(
        int x0, int y0, int z0, int x1, int y1, int z1, Action<int, int, int, BlockId> emit)
    {
        for (var y = y0; y <= y1; y++)
        for (var z = z0; z <= z1; z++)
        for (var x = x0; x <= x1; x++) emit(x, y, z, BlockId.Air);
    }

    private static void Ring(
        int x, int y, int z, int radius, BlockId block, Action<int, int, int, BlockId> emit)
    {
        for (var d = -radius; d <= radius; d++)
        {
            emit(x + d, y, z - radius, block);
            emit(x + d, y, z + radius, block);
            if (Math.Abs(d) == radius) continue;
            emit(x - radius, y, z + d, block);
            emit(x + radius, y, z + d, block);
        }
    }

    private static int FloorDiv(int value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }
}

internal static class ExplorationBlockChoices
{
    public static BlockId BrickOrRubble(this StarterBlocks.Ids ids, int y, int floor) =>
        ((y - floor) & 2) == 0 ? ids.Bricks : ids.Rubble;
}
