using Driftwood.Core.Gen;
using Driftwood.Core.Lighting;

namespace Driftwood.Core.Entities;

/// <summary>One row of a biome's monster table: what, how often against the rest, and in what number.</summary>
public readonly record struct MonsterEntry(string Kind, int Weight, int PackMin, int PackMax);

/// <summary>
/// Where a thing may appear: how dark it is, and whether the sky can reach it.
/// </summary>
/// <remarks>
/// <para>⛔ <b>Two axes, and until fluids landed there was only one.</b> "Is this cell dark" put any
/// hostile anywhere dark and put the bat in fields with the cows, because darkness alone cannot tell
/// a cave from a meadow at midnight. The second axis is free: sky light is what a cell would get at
/// <em>noon</em>, independent of the clock, so it already answers "is there a way out above you".
/// </para>
/// <para>⛔ <b>The monster numbers are the reference's, and the old ones were the fault.</b> A cap of
/// five, placed on a ring round the player and never taken away again, is how a trip underground met
/// two zombies and the surface met nothing: the first five things to appear
/// anywhere dark held every slot for the rest of the session. The reference keeps seventy about,
/// spread through everything within 128 blocks at every height, and lets the far ones go — so most of
/// them are in caves nobody is in, and the ones a player meets are the ones near them.</para>
/// </remarks>
public static class SpawnRules
{
    /// <summary>Light at or below which a cave animal will appear in a cell.</summary>
    public const int Darkness = 7;

    /// <summary>The brightest of the three block channels — what "is there a torch here" means.</summary>
    public static int BlockLight(ushort packed) => LightValue.BlockPeak(packed);

    /// <summary>
    /// True where it is dark <em>now</em>, wherever that is.
    /// </summary>
    /// <param name="daylight">How far up the sun is, 0 at night to 1 with it properly up.</param>
    public static bool Dark(ushort packed, float daylight) =>
        MathF.Max(LightValue.Sky(packed) * daylight, BlockLight(packed)) <= Darkness;

    /// <summary>
    /// True where the sky cannot reach at all, whatever the hour.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>RAW sky light, not the scaled kind.</b> This answers "could the sun ever reach here",
    /// which is a fact about the place rather than about the time. Using the scaled one is exactly
    /// how a cave animal ends up in a meadow.
    /// </remarks>
    public static bool Buried(ushort packed) =>
        LightValue.Sky(packed) == 0 && BlockLight(packed) <= Darkness;

    /// <summary>The least raw sky light an animal's feet may stand in (2026-10-26).</summary>
    public const int AnimalSky = 8;

    /// <summary>
    /// True where an animal may be born: on or above the land's own surface, under real sky.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The user's report, 2026-10-26: cows spawning underground.</b> The animals' top-up asked
    /// for nothing but somewhere to stand, so a player in a cave got a herd in the cave. Both halves
    /// are asked, because each alone lets one through: sky light alone passes a cave mouth or a
    /// ravine, and the surface alone passes a roofed pit dug at the surface. Raw sky light, never
    /// the time-scaled kind — a field is a field at midnight too.
    /// </remarks>
    public static bool AnimalGround(ushort packed, int feetY, int surface) =>
        feetY > surface && LightValue.Sky(packed) >= AnimalSky;

    // ── Monsters, by the reference's own rules ──────────────────────────────────────────────────

    /// <summary>The most monsters about at once, everywhere within <see cref="SpawnRadius"/>.</summary>
    public const int MonsterCap = 70;

    /// <summary>Never closer than this to the player, in blocks.</summary>
    public const float MinRadius = 24f;

    /// <summary>Never further than this, and anything further is let go at once.</summary>
    public const float SpawnRadius = 128f;

    /// <summary>Within this a monster is never let go; beyond it, one left alone may be.</summary>
    public const float KeepRadius = 32f;

    /// <summary>Seconds alone beyond <see cref="KeepRadius"/> before one may be let go.</summary>
    public const float IdleSeconds = 30f;

    /// <summary>The chance per second an idle far one is let go: one in 800 a tick, twenty ticks.</summary>
    public const float DespawnPerSecond = 20f / 800f;

    /// <summary>Spawn ticks a second. The reference's own rate, so its other numbers mean the same.</summary>
    public const float TicksPerSecond = 20f;

    /// <summary>Column samples per tick.</summary>
    /// <remarks>
    /// ⚠ <b>Derived, not chosen.</b> The reference samples one random height in every 16×16 column
    /// within range, every tick, over a column about 130 tall. We sample a disc of the same radius
    /// over a band 256 tall (128 either way of the player, because our chunks are cubes and there is
    /// no "whole column" to ask for), so the same chance per cell per tick needs
    /// π·128² / 256 · 256 / 130 ≈ 396 samples. Nearly all of them miss on the first lookup — a
    /// sample has to land exactly on the air above a floor — which is why the reference can afford
    /// thousands a second and so can we.
    /// </remarks>
    public const int SamplesPerTick = 396;

    /// <summary>Groups tried round one sample, and the most one sample may place.</summary>
    public const int GroupsPerSample = 3;
    public const int MostPerSample = 4;

    /// <summary>How far each step of a group wanders from the last, either way, in blocks.</summary>
    public const int GroupWander = 5;

    /// <summary>
    /// How much the night takes off sky light, 0 at noon to 11 at midnight.
    /// </summary>
    /// <remarks>The reference's sky darkening: an open field at midnight reads 4, not 0.</remarks>
    public static int SkyDarkening(float daylight) =>
        (int)MathF.Round((1f - Math.Clamp(daylight, 0f, 1f)) * 11f);

    /// <summary>
    /// Whether a monster may stand in this cell now — the reference's roll, three parts.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>Block light must be NOTHING.</b> Any torch light at all clears the cell, which
    /// is what makes a lit cave safe and an unlit corner of a lit one not. The old line at seven left
    /// half of every torch's reach spawnable.</para>
    /// <para>⛳ <b>Then two rolls against the sky.</b> Raw sky light against a roll of 0..31, then the
    /// darkened light against a roll of 0..7. A cave passes both every time; a field at midnight
    /// passes about a quarter of the time; a field at noon never. That one rule is the whole of
    /// "caves are busy and the surface at night is thinner but real".</para>
    /// </remarks>
    public static bool MonsterDark(ushort packed, float daylight, Random roll)
    {
        var sky = LightValue.Sky(packed);
        if (sky > roll.Next(32)) return false;

        var block = BlockLight(packed);
        if (block > 0) return false;

        var seen = Math.Max(sky - SkyDarkening(daylight), block);
        return seen <= roll.Next(8);
    }

    /// <summary>The same roll's odds, for the audit and the report — no dice.</summary>
    public static float MonsterDarkChance(ushort packed, float daylight)
    {
        if (BlockLight(packed) > 0) return 0f;

        var sky = LightValue.Sky(packed);
        var skyPass = Math.Clamp((32 - sky) / 32f, 0f, 1f);
        var seen = Math.Max(sky - SkyDarkening(daylight), 0);
        var lightPass = Math.Clamp((8 - seen) / 8f, 0f, 1f);
        return skyPass * lightPass;
    }

    /// <summary>
    /// Whether a 16×16 patch is one where slimes rise from the deep. One in ten, fixed by the seed.
    /// </summary>
    /// <remarks>
    /// ⚠ Sixteen across, not our chunk's thirty-two. The patch is the reference's unit and a slime
    /// hunter's — a quarter of the size means four times the edges to find one by.
    /// </remarks>
    public static bool SlimePatch(int seed, int x, int z)
    {
        var px = x >> 4;
        var pz = z >> 4;
        unchecked
        {
            var h = (uint)seed * 0x9E3779B1u ^ (uint)px * 0x85EBCA77u ^ (uint)pz * 0xC2B2AE3Du ^ 987234911u;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            h *= 0x297A2D39u;
            h ^= h >> 15;
            return h % 10u == 0u;
        }
    }

    /// <summary>Below this a slime patch breeds slimes in any light.</summary>
    public const int SlimeDepth = 40;

    /// <summary>The marsh's own slimes rise at night between these heights.</summary>
    public const int MarshSlimeLow = 51;
    public const int MarshSlimeHigh = 69;

    /// <summary>
    /// The monsters a biome breeds, by weight. Ours by name, the reference's by number.
    /// </summary>
    /// <remarks>
    /// <para>The common table is the reference's own: spider 100, zombie 95, zombie villager 5,
    /// skeleton 100, crawler (its creeper) 100, slime 100, farwalker (its enderman) 10 in ones to
    /// fours, witch 5 alone. A slime that is picked still has to be in a slime patch or a marsh to
    /// stand, so most of its weight is lost turns — exactly as there.</para>
    /// <para>The desert swaps most zombies for husks (80 / 19 / 1), the snow most skeletons for
    /// strays (80 / 20), the marsh shares skeletons with the bogged, and the sea adds the drowned.
    /// ⚠ The sea's drowned weight is ours: the reference keeps its drowned in rivers at 100 and its
    /// oceans at 5, and we have no rivers, so 30 in salt water.</para>
    /// <para>⛳ A row whose creature this build cannot draw is skipped by the caller rather than
    /// filtered here, so a kind added later is a model and nothing else.</para>
    /// </remarks>
    /// <summary>
    /// The table at a height: the biome's, and below the zero line the deep's own spider too.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Ours, not the reference's.</b> Its cave spider only ever comes out of the spawners in
    /// abandoned mines, which this world does not have; without a door of its own it would never be
    /// met at all. So the deep breeds it, in ones to threes — rarer than the common four, and only
    /// where the light has never been.
    /// </remarks>
    public static IReadOnlyList<MonsterEntry> MonstersFor(Biome biome, int y) =>
        y < TerrainGenerator.EmberdeepTop ? Emberdeep
        : y < DeepLine ? DeepOf(MonstersFor(biome)) : MonstersFor(biome);

    /// <summary>
    /// The Emberdeep's own dark, which is not the surface's whatever biome is overhead.
    /// </summary>
    /// <remarks>
    /// ⛳ The reference's Nether, ours: the slime that lives in lava commonest, the big floater of
    /// the wide hollows next, the charred skeleton in its bands, the cinderwisp rarer in the open
    /// (its home is the Emberhold), and the odd farwalker, as the reference's Nether keeps endermen.
    /// </remarks>
    public static readonly MonsterEntry[] Emberdeep =
    [
        new("ember_slime", 100, 2, 3),
        new("ashwail", 40, 1, 1),
        new("charred_skeleton", 30, 1, 3),
        new("cinderwisp", 15, 1, 2),
        new("farwalker", 5, 1, 2),
    ];

    /// <summary>
    /// Inside an Emberhold's walls: the reference's fortress table — its skeletons and its blazes,
    /// a few slimes, and the plain skeleton that wandered in.
    /// </summary>
    public static readonly MonsterEntry[] Emberhold =
    [
        new("charred_skeleton", 40, 2, 4),
        new("cinderwisp", 30, 1, 2),
        new("ember_slime", 10, 1, 2),
        new("skeleton", 10, 1, 2),
    ];

    /// <summary>
    /// Kinds born of the Emberdeep, which stand in its glow: the lava lights every hollow down
    /// there, so asking for darkness would ask for nothing at all. The reference lets its Nether
    /// spawn by any block light for the same reason.
    /// </summary>
    public static bool EmberNative(string kind) =>
        kind is "ember_slime" or "ashwail" or "charred_skeleton" or "cinderwisp";

    /// <summary>Below this the deep's own spider joins the table.</summary>
    public const int DeepLine = 0;

    private static readonly Dictionary<IReadOnlyList<MonsterEntry>, MonsterEntry[]> Deeps = new(ReferenceEqualityComparer.Instance);

    private static IReadOnlyList<MonsterEntry> DeepOf(IReadOnlyList<MonsterEntry> table)
    {
        lock (Deeps)
        {
            if (!Deeps.TryGetValue(table, out var deep))
                Deeps[table] = deep = [.. table, new("cave_spider", 40, 1, 3)];
            return deep;
        }
    }

    /// <summary>
    /// Whether a stone in the highlands has something living in it.
    /// </summary>
    /// <remarks>
    /// The reference's infested stone, which looks like any other and breaks into a silverfish. A
    /// property of the cell rather than a roll at the break, so the same stone gives the same
    /// answer however many times it is asked — about one in a hundred and twenty, below 64.
    /// </remarks>
    public static bool Infested(int seed, int x, int y, int z)
    {
        if (y >= 64) return false;
        unchecked
        {
            var h = (uint)seed ^ (uint)x * 0x9E3779B1u ^ (uint)y * 0x85EBCA77u ^ (uint)z * 0xC2B2AE3Du ^ 0x51F15Eu;
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h % 120u == 0u;
        }
    }

    public static IReadOnlyList<MonsterEntry> MonstersFor(Biome biome) => biome switch
    {
        Biome.Dunes => Desert,
        Biome.Snowfield or Biome.Tundra => Snowy,
        Biome.FrozenSea => SnowySea,
        Biome.Marsh => Marsh,
        Biome.Sea or Biome.Shore => Sea,
        _ => Common,
    };

    private static MonsterEntry[] Table(int zombie, int villager, int husk, int skeleton, int stray, int bogged) =>
    [
        new("spider", 100, 4, 4),
        new("zombie", zombie, 4, 4),
        new("zombie_villager", villager, 1, 1),
        new("husk", husk, 4, 4),
        new("skeleton", skeleton, 4, 4),
        new("stray", stray, 4, 4),
        new("bogged", bogged, 4, 4),
        new("crawler", 100, 4, 4),
        new("slime", 100, 4, 4),
        new("farwalker", 10, 1, 4),
        new("witch", 5, 1, 1),
    ];

    private static readonly MonsterEntry[] Common = Strip(Table(95, 5, 0, 100, 0, 0));
    private static readonly MonsterEntry[] Desert = Strip(Table(19, 1, 80, 100, 0, 0));
    private static readonly MonsterEntry[] Snowy = Strip(Table(95, 5, 0, 20, 80, 0));
    private static readonly MonsterEntry[] Marsh = Strip(Table(95, 5, 0, 70, 0, 30));
    private static readonly MonsterEntry[] Sea = [.. Common, new("drowned", 30, 1, 1)];
    private static readonly MonsterEntry[] SnowySea = [.. Snowy, new("drowned", 30, 1, 1)];

    private static MonsterEntry[] Strip(MonsterEntry[] rows) => [.. rows.Where(row => row.Weight > 0)];

    /// <summary>Kinds that only stand where the sky can see them: the desert's and the snow's own.</summary>
    /// <remarks>
    /// The reference's rule for the husk and the stray. Without it a desert's caves fill with husks
    /// and the zombie becomes the rare one even underground.
    /// </remarks>
    public static bool NeedsSky(string kind) => kind is "husk" or "stray";

    /// <summary>Kinds that stand in water rather than on dry ground.</summary>
    public static bool Aquatic(string kind) => kind is "drowned";

    /// <summary>
    /// Picks a row by weight from a roll, or null for an empty table.
    /// </summary>
    public static MonsterEntry? Pick(IReadOnlyList<MonsterEntry> rows, Func<string, bool> available, Random roll)
    {
        var total = 0;
        foreach (var row in rows) if (available(row.Kind)) total += row.Weight;
        if (total <= 0) return null;

        var at = roll.Next(total);
        foreach (var row in rows)
        {
            if (!available(row.Kind)) continue;
            if (at < row.Weight) return row;
            at -= row.Weight;
        }

        return null;
    }
}
