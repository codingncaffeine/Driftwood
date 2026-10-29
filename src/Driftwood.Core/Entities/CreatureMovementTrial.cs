using System.Numerics;

namespace Driftwood.Core.Entities;

/// <summary>
/// How well creatures cross awkward ground, measured rather than watched.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Why this exists:</b> the user, 2026-10-25 — "our creatures get hung up on terrain far
/// more than they should". Watching a herd in a world is a mood; this is a number. A grove of trunks,
/// one-block steps, a two-block wall with a single gap and a two-block ledge, a herd wandering it and a
/// pack hunting across it, all at a fixed seed so two builds can be compared.</para>
/// </remarks>
public static class CreatureMovementTrial
{
    public readonly record struct Result(
        float WanderStuckShare, float WanderBlocksPerMinute,
        int HuntersReached, int Hunters, float MeanSecondsToReach);

    /// <summary>The grove: floor at 64, trunks, steps, a wall with a gap at z 0, a ledge for x &lt; −20.</summary>
    public static bool Grove(int x, int y, int z)
    {
        if (y < 64) return true;
        if (x < -20 && y < 66) return true;                                    // a two-block ledge up
        if (y >= 70) return false;

        // The wall across z = 0, two high, with a three-wide gap at x 10..12.
        if (z == 0 && y < 66 && x is >= -18 and <= 30 && x is < 10 or > 12) return true;

        // Trunks four high on a jittered five-block grid.
        if (y < 68)
        {
            var cx = Math.Floor(x / 5.0);
            var cz = Math.Floor(z / 5.0);
            var h = Hash((int)cx, (int)cz);
            var tx = (int)cx * 5 + (int)(h % 4);
            var tz = (int)cz * 5 + (int)(h / 4 % 4);
            if (x == tx && z == tz && h % 3 != 0) return true;
        }

        // Single-block bumps to step over.
        if (y == 64 && Hash(x * 7 + 3, z * 11 + 5) % 23 == 0) return true;

        return false;
    }

    private static uint Hash(int x, int z)
    {
        unchecked
        {
            var h = (uint)x * 0x9E3779B1u ^ (uint)z * 0x85EBCA77u;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            return h;
        }
    }

    public static Result Run()
    {
        const float dt = 1f / 20f;

        // ── Wandering ──
        var herd = new CreatureHerd(11);
        var cow = new SpawnKind("cow", new Vector3(0.75f, 1.56f, 1.50f));
        herd.Spawn(Grove, [cow], new Vector3(4f, 64f, 14f), 24);

        var last = herd.All.Select(c => c.Position).ToArray();
        float stuck = 0f, walking = 0f, travelled = 0f;
        for (var t = 0f; t < 120f; t += dt)
        {
            herd.Update(dt, Grove);
            for (var i = 0; i < herd.Count; i++)
            {
                var c = herd.All[i];
                var step = new Vector2(c.Position.X - last[i].X, c.Position.Z - last[i].Z).Length();
                travelled += step;
                last[i] = c.Position;
                if (!c.Moving) continue;
                walking += dt;
                if (c.Stuck > 0.25f) stuck += dt;
            }
        }

        // ── Hunting: across the wall to a player who stands still on the far side ──
        var hunt = new CreatureHerd(12);
        var zombie = new SpawnKind("zombie", new Vector3(0.6f, 1.95f, 0.6f), Hostile: true);
        var player = new Vector3(-6.5f, 64f, -8.5f);
        var starts = new List<Creature>();
        for (var i = 0; i < 12; i++)
        {
            var at = new Vector3(-14f + i * 3.2f + 0.5f, 64f, 9.5f + i % 3 * 2f);
            if (Grove((int)MathF.Floor(at.X), 64, (int)MathF.Floor(at.Z))
                || Grove((int)MathF.Floor(at.X), 65, (int)MathF.Floor(at.Z))) at.X += 1f;
            var made = hunt.SpawnAt(zombie, at, 270f);
            made.Provoked = true;
            starts.Add(made);
        }

        var reachedAt = new float?[starts.Count];
        for (var t = 0f; t < 60f; t += dt)
        {
            hunt.Update(dt, Grove, player);
            hunt.TakeAttacks();
            for (var i = 0; i < starts.Count; i++)
                if (reachedAt[i] is null
                    && Vector2.Distance(new(starts[i].Position.X, starts[i].Position.Z), new(player.X, player.Z)) < 1.6f)
                    reachedAt[i] = t;
        }

        var reached = reachedAt.Where(at => at is not null).Select(at => at!.Value).ToList();
        return new Result(
            walking > 0f ? stuck / walking : 0f,
            travelled / herd.Count / 2f,
            reached.Count, starts.Count,
            reached.Count > 0 ? reached.Average() : float.PositiveInfinity);
    }

    /// <summary>One hunter put on a raised floor with the player standing below it.</summary>
    public readonly record struct LedgeCase(string Kind, int Drop, int Out, bool Wounded, float? ReachedAt);

    /// <summary>
    /// Hunters on a raised floor (x &lt; 0) chasing a player standing on the ground below it.
    /// </summary>
    /// <remarks>
    /// ⛔ USER, 2026-10-29: a spider "clearly saw me but was jittering at a slightly higher
    /// elevation, it would have been a drop to get to me but nothing crazy." Drops of two, four and
    /// six, the player at the foot of the cliff and six out, the hunter whole and at half health. A
    /// half-health spider stood at the lip of anything over three; the reference's wall-climber walks
    /// straight at its target once its path is spent. A zombie runs the same ground as the control:
    /// whole it takes all three drops, at half health it keeps to three, which is the walker's rule.
    /// <para>⚠ Health is put back every tick, so the drop a hunter will take stays the one the case
    /// names — a fall's damage would otherwise turn a whole hunter into a wounded one mid-case.</para>
    /// </remarks>
    public static List<LedgeCase> RunLedges()
    {
        const float dt = 1f / 20f;
        var cases = new List<LedgeCase>();
        var kinds = new[]
        {
            new SpawnKind("spider", new Vector3(1.4f, 0.9f, 1.4f), Hostile: true),
            new SpawnKind("zombie", new Vector3(0.6f, 1.95f, 0.6f), Hostile: true),
        };

        foreach (var kind in kinds)
        foreach (var drop in (ReadOnlySpan<int>)[2, 4, 6])
        foreach (var outward in (ReadOnlySpan<int>)[0, 6])
        foreach (var wounded in (ReadOnlySpan<bool>)[false, true])
        {
            var top = 64 + drop;
            bool Ground(int x, int y, int z) => y < 64 || (x < 0 && y < top);

            var hunt = new CreatureHerd(21);
            var hunter = hunt.SpawnAt(kind, new Vector3(-4.5f, top, 0.5f), 0f);
            hunter.Provoked = true;
            var health = wounded ? hunter.MaxHealth / 2 : hunter.MaxHealth;
            var player = new Vector3(outward + 0.5f, 64f, 0.5f);

            float? reachedAt = null;
            for (var t = 0f; t < 20f && reachedAt is null; t += dt)
            {
                hunter.Health = health;
                hunt.Update(dt, Ground, player);
                hunt.TakeAttacks();

                if (Vector2.Distance(new(hunter.Position.X, hunter.Position.Z), new(player.X, player.Z)) < 1.6f
                    && MathF.Abs(hunter.Position.Y - player.Y) < 1.5f)
                    reachedAt = t;
            }

            cases.Add(new LedgeCase(kind.Name, drop, outward, wounded, reachedAt));
        }

        return cases;
    }

    /// <summary>
    /// How high a spider gets in three seconds when it stands pressed to a two-block face, a little
    /// short of touching it, with the player on top.
    /// </summary>
    /// <remarks>
    /// ⛔ Where a real chase left one (2026-10-29): a walk step that would overlap the face is
    /// refused whole, so a body comes to rest anywhere within one step of it. Its wall reach was
    /// measured from a 0.3 half-width while its own sides stopped it at 0.45, so from 0.08 short it
    /// never felt the wall it was pressed to and stood under the player for good.
    /// </remarks>
    public static float SpiderAtFaceClimbs()
    {
        static bool Face(int x, int y, int z) => y < 64 || (x >= 6 && y < 66);
        var herd = new CreatureHerd(3);
        var spider = herd.SpawnAt(
            new SpawnKind("spider", new Vector3(1.4f, 0.9f, 1.4f), Hostile: true),
            new Vector3(6f - 0.45f - 0.08f, 64f, 0.5f), 0f);
        spider.Provoked = true;

        var highest = spider.Position.Y;
        for (var t = 0f; t < 3f; t += 1f / 20f)
        {
            spider.Health = spider.MaxHealth;
            herd.Update(1f / 20f, Face, new Vector3(9.5f, 66f, 0.5f));
            herd.TakeAttacks();
            highest = MathF.Max(highest, spider.Position.Y);
        }

        return highest - 64f;
    }

    /// <summary>How hunters did on real ground: how many got there, and how many sank through it.</summary>
    public readonly record struct TerrainResult(int Tried, int Reached, int SankThrough, string FirstMiss);

    /// <summary>
    /// Whole-health hunters put on generated ground one to six blocks above a still player eight to
    /// sixteen blocks off, in sight of them, and given twenty seconds to get there.
    /// </summary>
    /// <param name="stand">The top a player could stand on at a column, or null for water or a tree.</param>
    /// <remarks>
    /// ⛔ USER, 2026-10-29: a whole-health spider "saw me just a distance but was unable to reach
    /// me", jittering a little above them. A flat cliff never showed it; real ground did — 195 of
    /// 300 spiders got there, and some sank a hundred blocks. The floor search had been the spawn
    /// one, which refuses a floor with anything within three cells over it, so walking under leaves
    /// or an overhang dropped a body through the rock; and a spider's wall reach had not grown with
    /// its body, so it could not climb the face it stood against.
    /// <para><b>SankThrough</b> counts bodies that ended more than eight blocks under where both
    /// began — no chase in this ground goes that deep on purpose.</para>
    /// </remarks>
    public static TerrainResult RunOnTerrain(
        Func<int, int, int, bool> solid, Func<int, int, int, bool> water, Func<int, int, int?> stand,
        int from, int to, SpawnKind kind, int count, int seed)
    {
        const float dt = 1f / 20f;
        var random = new Random(seed);
        int tried = 0, reached = 0, sank = 0, attempts = 0;
        var firstMiss = "";

        while (tried < count && attempts++ < count * 200)
        {
            var px = random.Next(from, to);
            var pz = random.Next(from, to);
            if (stand(px, pz) is not { } py || solid(px, py + 1, pz)) continue;

            var angle = random.NextDouble() * Math.PI * 2;
            var distance = 8 + random.NextDouble() * 8;
            var sx = px + (int)Math.Round(Math.Cos(angle) * distance);
            var sz = pz + (int)Math.Round(Math.Sin(angle) * distance);
            if (sx < from || sx >= to || sz < from || sz >= to) continue;
            if (stand(sx, sz) is not { } sy || sy - py is < 1 or > 6 || solid(sx, sy + 1, sz)) continue;

            var player = new Vector3(px + 0.5f, py, pz + 0.5f);
            var start = new Vector3(sx + 0.5f, sy, sz + 0.5f);
            if (!CreatureHerd.LineOfSight(solid, start + new Vector3(0f, 0.6f, 0f), player + new Vector3(0f, 1.5f, 0f)))
                continue;

            tried++;
            var herd = new CreatureHerd(seed + tried);
            var hunter = herd.SpawnAt(kind, start, 0f);
            hunter.Provoked = true;

            var got = false;
            for (var t = 0f; t < 20f && !got; t += dt)
            {
                hunter.Health = hunter.MaxHealth;
                herd.Update(dt, solid, player, water: water);
                herd.TakeAttacks();
                got = Vector2.Distance(new(hunter.Position.X, hunter.Position.Z), new(player.X, player.Z)) < 1.6f
                      && MathF.Abs(hunter.Position.Y - player.Y) < 1.5f;
            }

            if (got) reached++;
            else if (firstMiss.Length == 0)
                firstMiss = $"from {start} to {player}, ended at {hunter.Position}";
            if (hunter.Position.Y < MathF.Min(player.Y, start.Y) - 8f) sank++;
        }

        return new TerrainResult(tried, reached, sank, firstMiss);
    }
}
