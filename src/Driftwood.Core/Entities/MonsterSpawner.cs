using System.Numerics;
using Driftwood.Core.Gen;
using Driftwood.Core.Lighting;

namespace Driftwood.Core.Entities;

/// <summary>What the monster spawner may ask of the world. Nothing else of it is seen.</summary>
/// <param name="Solid">Whether a cell stops a body.</param>
/// <param name="Support">Whether a body may be introduced standing on a cell's top.</param>
/// <param name="Fluid">Whether a cell holds any fluid.</param>
/// <param name="Water">Whether a cell holds water.</param>
/// <param name="Light">The packed light in a cell.</param>
/// <param name="BiomeAt">The biome of a column.</param>
public sealed record SpawnWorld(
    Func<int, int, int, bool> Solid,
    Func<int, int, int, bool> Support,
    Func<int, int, int, bool> Fluid,
    Func<int, int, int, bool> Water,
    Func<int, int, int, ushort> Light,
    Func<int, int, Biome> BiomeAt);

/// <summary>One monster the spawner placed.</summary>
public readonly record struct MonsterPlacement(SpawnKind Kind, Vector3 Position, float Yaw);

/// <summary>
/// The reference's natural spawner for monsters: random cells at every height, packs round each.
/// </summary>
/// <remarks>
/// <para>⛔ <b>Random heights, not "the first floor under the player".</b> The ring it replaced
/// asked each column for the topmost floor below the player's head — one cave level, the one nearest
/// the surface — so the deep stayed empty and the surface was only tried when the player stood on
/// it. Here a sample is a random cell anywhere in the band and has to land exactly on the air above
/// a floor; every floor in every column is equally likely, which is what fills a cave system rather
/// than the roof of it.</para>
/// <para>⛳ <b>No window, no clock, no GL.</b> The host hands in the world as a handful of
/// questions and a daylight figure; everything here runs under <c>--audit</c>.</para>
/// </remarks>
public sealed class MonsterSpawner
{
    private readonly Random _roll;
    private readonly int _seed;
    private float _tick;

    public MonsterSpawner(int seed)
    {
        _seed = seed;
        _roll = new Random(seed);
    }

    /// <summary>Samples taken and monsters placed since the host last read them. For the log.</summary>
    public long Samples { get; private set; }
    public long Placed { get; private set; }

    /// <summary>
    /// Runs as many reference ticks as <paramref name="dt"/> covers and returns what they placed.
    /// </summary>
    /// <param name="living">Monsters already about. The cap is checked before every placement.</param>
    /// <param name="kinds">The monster kinds this build can draw, by name, with their sizes.</param>
    /// <param name="ticksAtMost">
    /// ⚠ A ceiling on catch-up. A frame that stalled for two seconds owes forty ticks; running them
    /// all in one frame is a second stall caused by the first.
    /// </param>
    public List<MonsterPlacement> Step(
        float dt, SpawnWorld world, Vector3 player, float daylight, int living,
        IReadOnlyDictionary<string, SpawnKind> kinds, int ticksAtMost = 2)
    {
        var placed = new List<MonsterPlacement>();
        if (kinds.Count == 0) return placed;

        _tick += dt * SpawnRules.TicksPerSecond;
        var ticks = Math.Min((int)_tick, ticksAtMost);
        _tick -= (int)_tick;

        for (var t = 0; t < ticks; t++)
        {
            for (var s = 0; s < SpawnRules.SamplesPerTick; s++)
            {
                if (living + placed.Count >= SpawnRules.MonsterCap) return placed;
                Sample(world, player, daylight, living, kinds, placed);
            }
        }

        return placed;
    }

    /// <summary>
    /// One sample: a random cell in the disc and band, then up to three groups wandering from it.
    /// </summary>
    /// <remarks>
    /// The reference's own loop. A group picks its kind at its first good cell, then takes as many
    /// steps as that kind's pack size; each step wanders up to five blocks either way from the last
    /// at the same height, so a pack lands on one floor and spreads along it.
    /// </remarks>
    private void Sample(
        SpawnWorld world, Vector3 player, float daylight, int living,
        IReadOnlyDictionary<string, SpawnKind> kinds, List<MonsterPlacement> placed)
    {
        Samples++;

        // Uniform over the disc by rejection — two rolls and a compare, cheaper than a square root.
        float dx, dz;
        do
        {
            dx = ((float)_roll.NextDouble() * 2f - 1f) * SpawnRules.SpawnRadius;
            dz = ((float)_roll.NextDouble() * 2f - 1f) * SpawnRules.SpawnRadius;
        }
        while (dx * dx + dz * dz > SpawnRules.SpawnRadius * SpawnRules.SpawnRadius);

        var x0 = (int)MathF.Floor(player.X + dx);
        var z0 = (int)MathF.Floor(player.Z + dz);

        var low = Math.Max(TerrainGenerator.WorldBottom + 1, (int)player.Y - (int)SpawnRules.SpawnRadius);
        var high = Math.Min(TerrainGenerator.WorldTop - 4, (int)player.Y + (int)SpawnRules.SpawnRadius);
        var y = low + _roll.Next(high - low + 1);

        // ⚠ The cheap misses first. Almost every sample is inside rock or up in the sky, and both
        // are answered by one or two lookups before any light or biome is asked for.
        if (world.Solid(x0, y, z0)) return;
        if (!world.Solid(x0, y - 1, z0) && !world.Water(x0, y, z0)) return;

        var here = 0;

        for (var group = 0; group < SpawnRules.GroupsPerSample; group++)
        {
            var x = x0;
            var z = z0;
            MonsterEntry? entry = null;
            var steps = (int)MathF.Ceiling((float)_roll.NextDouble() * 4f);

            for (var step = 0; step < steps; step++)
            {
                x += _roll.Next(SpawnRules.GroupWander + 1) - _roll.Next(SpawnRules.GroupWander + 1);
                z += _roll.Next(SpawnRules.GroupWander + 1) - _roll.Next(SpawnRules.GroupWander + 1);

                var at = new Vector3(x + 0.5f, y, z + 0.5f);
                var distance = Vector3.DistanceSquared(at, player);
                if (distance < SpawnRules.MinRadius * SpawnRules.MinRadius) continue;
                if (distance > SpawnRules.SpawnRadius * SpawnRules.SpawnRadius) continue;

                if (entry is null)
                {
                    entry = SpawnRules.Pick(SpawnRules.MonstersFor(world.BiomeAt(x, z), y), kinds.ContainsKey, _roll);
                    if (entry is not { } chosen) return;

                    steps = chosen.PackMin + _roll.Next(1 + chosen.PackMax - chosen.PackMin);
                }

                var kind = kinds[entry.Value.Kind];
                if (!Fits(world, kind, x, y, z, daylight)) continue;

                placed.Add(new MonsterPlacement(kind, at, (float)(_roll.NextDouble() * 360.0)));
                Placed++;
                here++;

                if (here >= SpawnRules.MostPerSample) return;
                if (living + placed.Count >= SpawnRules.MonsterCap) return;
            }
        }
    }

    /// <summary>Whether this kind may stand with its feet in this cell now.</summary>
    public bool Fits(SpawnWorld world, SpawnKind kind, int x, int y, int z, float daylight)
    {
        var aquatic = SpawnRules.Aquatic(kind.Name);

        if (aquatic)
        {
            // In water, feet and head both — the drowned rises out of the deep, not out of a puddle.
            if (!world.Water(x, y, z) || !world.Water(x, y + 1, z)) return false;
        }
        else
        {
            if (!world.Support(x, y - 1, z)) return false;
            if (world.Fluid(x, y, z)) return false;
        }

        if (!Room(world, kind, x, y, z, aquatic)) return false;

        if (kind.Name == "slime") return SlimeFits(world, x, y, z, daylight);

        var light = world.Light(x, y, z);
        if (!SpawnRules.MonsterDark(light, daylight, _roll)) return false;

        if (SpawnRules.NeedsSky(kind.Name) && LightValue.Sky(light)
                < LightValue.Max)
            return false;

        return true;
    }

    /// <summary>
    /// The slime's two doors: deep in a slime patch in any light, or a marsh's shallows at night.
    /// </summary>
    private bool SlimeFits(SpawnWorld world, int x, int y, int z, float daylight)
    {
        if (y < SpawnRules.SlimeDepth && SpawnRules.SlimePatch(_seed, x, z))
            return _roll.Next(10) == 0;

        if (y is > SpawnRules.MarshSlimeLow and < SpawnRules.MarshSlimeHigh
            && world.BiomeAt(x, z) == Biome.Marsh
            && _roll.NextDouble() < 0.5)
        {
            var light = world.Light(x, y, z);
            var seen = Math.Max(
                LightValue.Sky(light) - SpawnRules.SkyDarkening(daylight),
                SpawnRules.BlockLight(light));
            return seen <= _roll.Next(8);
        }

        return false;
    }

    /// <summary>
    /// Whether the whole body fits: every cell its box covers, as tall as it stands.
    /// </summary>
    /// <remarks>
    /// ⚠ The footprint, not the column. A spider is wider than a block and a farwalker three tall;
    /// asked only of its own column, either is placed half inside a wall and spends its life there.
    /// </remarks>
    private static bool Room(SpawnWorld world, SpawnKind kind, int x, int y, int z, bool aquatic)
    {
        var half = MathF.Max(kind.Size.X, kind.Size.Z) * 0.5f;
        var tall = Math.Max(1, (int)MathF.Ceiling(kind.Size.Y - 0.01f));

        var x0 = (int)MathF.Floor(x + 0.5f - half);
        var x1 = (int)MathF.Floor(x + 0.5f + half - 0.001f);
        var z0 = (int)MathF.Floor(z + 0.5f - half);
        var z1 = (int)MathF.Floor(z + 0.5f + half - 0.001f);

        for (var cx = x0; cx <= x1; cx++)
        for (var cz = z0; cz <= z1; cz++)
        for (var cy = y; cy < y + tall; cy++)
        {
            if (world.Solid(cx, cy, cz)) return false;
            if (!aquatic && world.Fluid(cx, cy, cz)) return false;
        }

        return true;
    }
}
