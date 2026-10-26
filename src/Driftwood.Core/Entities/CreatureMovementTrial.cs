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
}
