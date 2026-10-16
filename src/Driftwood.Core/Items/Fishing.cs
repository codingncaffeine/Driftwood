using System.Numerics;

namespace Driftwood.Core.Items;

/// <summary>Where a cast line is in its life.</summary>
public enum FishingState
{
    /// <summary>Nothing out: the next use casts.</summary>
    Idle,

    /// <summary>The float is in the air.</summary>
    Flying,

    /// <summary>The float sits on the water, waiting for a fish.</summary>
    Floating,

    /// <summary>A fish has it: the float is pulled under, and reeling now lands it.</summary>
    Biting,

    /// <summary>The float came down on something that is not water. Reeling brings it back empty.</summary>
    Resting,
}

/// <summary>
/// One player's fishing line: the float in flight, on the water, and the bite — the reference's
/// rules, measured in seconds rather than ticks.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Fishing, 2026-10-16</b> — the user: "we should be able to do some fishing from the
/// coast". Cast with a rod, wait for the float to be pulled under, reel in while it is under.</para>
/// <para>The reference's numbers: a fish comes after 5 to 30 seconds on the water (100 to 600
/// ticks with no Lure), stays on for 1 to 2 seconds (20 to 40 ticks), and a line longer than 32
/// blocks breaks. Reeling at any other moment brings the float back with nothing on it.</para>
/// <para>Pure state and arithmetic over two questions about the world, so the checks can drive a
/// whole cast without a window: "is this cell water" and "does this cell stop a float".</para>
/// </remarks>
public sealed class FishingLine
{
    public const float CastSpeed = 13f;
    public const float Gravity = 14f;
    public const float MaxLength = 32f;
    public const float WaitShortest = 5f;
    public const float WaitLongest = 30f;
    public const float BiteShortest = 1f;
    public const float BiteLongest = 2f;

    /// <summary>How far under the surface a biting fish pulls the float.</summary>
    public const float BiteDip = 0.22f;

    /// <summary>Where the float rides on a water cell, as a share of the cell's height.</summary>
    public const float Waterline = 0.88f;

    private readonly Random _random;
    private float _timer;
    private float _surface;
    private float _clock;

    public FishingLine(Random? random = null) => _random = random ?? new Random();

    public FishingState State { get; private set; }

    public Vector3 Position { get; private set; }

    public Vector3 Velocity { get; private set; }

    /// <summary>True when a line is out, in any state.</summary>
    public bool IsOut => State != FishingState.Idle;

    /// <summary>Set on the step a fish took the float, for the splash; cleared by the next step.</summary>
    public bool JustBit { get; private set; }

    /// <summary>Set on the step the float came down on water, for its own small splash.</summary>
    public bool JustLanded { get; private set; }

    /// <summary>Throws the float from the rod's tip, along the way the player is looking.</summary>
    public void Cast(Vector3 from, Vector3 direction)
    {
        if (direction.LengthSquared() < 1e-6f) direction = Vector3.UnitZ;
        Position = from;
        Velocity = Vector3.Normalize(direction) * CastSpeed + new Vector3(0f, 2f, 0f);
        State = FishingState.Flying;
        JustBit = JustLanded = false;
    }

    /// <summary>Takes the line in without anything on it.</summary>
    public void Drop() => State = FishingState.Idle;

    /// <summary>
    /// Moves the float on by one step. Returns false when the line broke — stretched past its
    /// length — and is gone.
    /// </summary>
    public bool Step(Vector3 rodTip, float dt, Func<int, int, int, bool> isWater, Func<int, int, int, bool> stops)
    {
        JustBit = JustLanded = false;
        if (State == FishingState.Idle) return true;

        if (Vector3.Distance(rodTip, Position) > MaxLength)
        {
            State = FishingState.Idle;
            return false;
        }

        _clock += dt;

        switch (State)
        {
            case FishingState.Flying:
                Fly(dt, isWater, stops);
                break;

            case FishingState.Floating:
                Position = Position with { Y = _surface + MathF.Sin(_clock * 2.4f) * 0.025f };
                _timer -= dt;
                if (_timer <= 0f)
                {
                    State = FishingState.Biting;
                    _timer = Between(BiteShortest, BiteLongest);
                    JustBit = true;
                }
                break;

            case FishingState.Biting:
                Position = Position with { Y = _surface - BiteDip };
                _timer -= dt;
                if (_timer <= 0f)
                {
                    // It got away: back up, and another fish in its own good time.
                    State = FishingState.Floating;
                    _timer = Between(WaitShortest, WaitLongest);
                }
                break;
        }

        return true;
    }

    private void Fly(float dt, Func<int, int, int, bool> isWater, Func<int, int, int, bool> stops)
    {
        // A few small steps rather than one long one, so a fast float cannot pass through a block.
        const int Pieces = 4;
        var piece = dt / Pieces;
        for (var i = 0; i < Pieces; i++)
        {
            Velocity = Velocity with { Y = Velocity.Y - Gravity * piece } ;
            var next = Position + Velocity * piece;
            var (x, y, z) = Cell(next);

            if (isWater(x, y, z))
            {
                // Up to the top of the water it fell into, and onto its surface.
                while (isWater(x, y + 1, z)) y++;
                _surface = y + Waterline;
                Position = next with { Y = _surface };
                Velocity = Vector3.Zero;
                State = FishingState.Floating;
                _timer = Between(WaitShortest, WaitLongest);
                JustLanded = true;
                return;
            }

            if (stops(x, y, z))
            {
                Velocity = Vector3.Zero;
                State = FishingState.Resting;
                return;
            }

            Position = next;
        }
    }

    /// <summary>
    /// Takes the line in. When a fish has it, returns what came up with it; otherwise null.
    /// </summary>
    public FishingLoot.Catch? Reel()
    {
        var biting = State == FishingState.Biting;
        State = FishingState.Idle;
        return biting ? FishingLoot.Roll(_random.NextDouble(), _random.NextDouble()) : null;
    }

    /// <summary>Sets a float straight onto the water at a point, for the checks.</summary>
    public void SettleForCheck(Vector3 at, float surface, float wait)
    {
        Position = at with { Y = surface };
        _surface = surface;
        _timer = wait;
        State = FishingState.Floating;
    }

    private float Between(float low, float high) => low + (float)_random.NextDouble() * (high - low);

    private static (int X, int Y, int Z) Cell(Vector3 at) =>
        ((int)MathF.Floor(at.X), (int)MathF.Floor(at.Y), (int)MathF.Floor(at.Z));
}

/// <summary>
/// What a bite brings up: the reference's split of fish, junk and treasure, over what Driftwood has.
/// </summary>
/// <remarks>
/// <para>The reference with no Luck: fish 85%, junk 10%, treasure 5%. Its fish are cod 60 parts to
/// salmon 25 (its rarer two, the pufferfish and the tropical fish, are not in Driftwood and are
/// folded into cod). Its junk table is kept where Driftwood has the thing: a bowl, leather, leather
/// boots, a stick, string and a bone, weighted as it weights them. Its treasure that exists here is
/// a bow and a fishing rod.</para>
/// </remarks>
public static class FishingLoot
{
    /// <summary>One catch: the item, by name, and how many.</summary>
    public readonly record struct Catch(string Item, int Count, string Kind);

    public static readonly (string Item, int Weight)[] Fish = [("raw_cod", 60), ("raw_salmon", 25)];

    public static readonly (string Item, int Weight)[] Junk =
        [("bowl", 10), ("leather", 10), ("leather_boots", 10), ("stick", 5), ("string", 5), ("bone", 10)];

    public static readonly (string Item, int Weight)[] Treasure = [("bow", 1), ("fishing_rod", 1)];

    /// <summary>A catch from two rolls in [0, 1): which table, then which row of it.</summary>
    public static Catch Roll(double table, double row) => table switch
    {
        < 0.85 => Pick(Fish, row, "fish"),
        < 0.95 => Pick(Junk, row, "junk"),
        _ => Pick(Treasure, row, "treasure"),
    };

    private static Catch Pick((string Item, int Weight)[] rows, double roll, string kind)
    {
        var total = rows.Sum(r => r.Weight);
        var at = roll * total;
        foreach (var (item, weight) in rows)
        {
            if (at < weight) return new Catch(item, 1, kind);
            at -= weight;
        }

        return new Catch(rows[^1].Item, 1, kind);
    }
}
