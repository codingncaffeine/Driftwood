using System.Numerics;

namespace Driftwood.Core.Entities;

/// <summary>One animal in the world: what it is, where it is, and which way it is pointing.</summary>
public sealed class Creature
{
    public required string Kind { get; init; }
    public Vector3 Position { get; set; }

    /// <summary>Degrees, measured the way the camera's is, from +x.</summary>
    public float Yaw { get; set; }

    /// <summary>Where it is trying to be pointing. It turns toward this rather than snapping.</summary>
    public float WantsYaw { get; set; }

    /// <summary>Seconds until it makes up its mind again.</summary>
    public float Thinks { get; set; }

    /// <summary>True while it is walking rather than standing.</summary>
    public bool Moving { get; set; }

    /// <summary>Seconds until it makes a noise.</summary>
    public float Speaks { get; set; }

    /// <summary>Set for the one step on which it did. Read and cleared by whoever plays sounds.</summary>
    public bool Spoke { get; set; }
}

/// <summary>
/// What each creature sounds like: our name for it against a clip's name.
/// </summary>
/// <remarks>
/// ⛳ The same our-name / their-file shape as <c>BlockTextureSet.Layers</c> and
/// <see cref="CreatureSet"/>, for the same reason — a table nothing can derive, written once. A
/// creature with no entry here is simply quiet, which is what most of them are for now: the sounds
/// are recordings the user chose, not a set that has to cover everything.
/// </remarks>
public static class CreatureSounds
{
    private static readonly Dictionary<string, string> Idle = new(StringComparer.Ordinal)
    {
        ["cow"] = "cow",
        ["pig"] = "pig",
        ["sheep"] = "sheep",
        ["chicken"] = "chicken",
        ["frog"] = "frog",

        // The hostiles. ⚠ A spider's is its walk rather than its bite: what a player needs to hear
        // in the dark is that one is nearby, and the bite arrives with the damage anyway.
        ["bat"] = "bat",
        ["spider"] = "spider",
        ["zombie"] = "zombie",

        // ⛳ Ours by name and theirs by skeleton, so the sound follows the same rule as the art:
        // the drowned and the husk are zombies as far as a recording is concerned.
        ["drowned"] = "zombie",
        ["husk"] = "zombie",
    };

    /// <summary>What one makes when it means it. Empty where there is only the one recording.</summary>
    private static readonly Dictionary<string, string> Angry = new(StringComparer.Ordinal)
    {
        ["spider"] = "spider_attack",
    };

    /// <summary>The clip a creature makes when it has nothing else to say, or empty for none.</summary>
    public static string IdleFor(string kind) => Idle.GetValueOrDefault(kind, "");

    /// <summary>The clip it makes when it goes for somebody, or its idle one if it has only one.</summary>
    public static string AngryFor(string kind) =>
        Angry.TryGetValue(kind, out var clip) ? clip : IdleFor(kind);

    /// <summary>Every clip named here, for the check that they all resolve.</summary>
    public static IEnumerable<string> All => Idle.Values.Concat(Angry.Values);
}

/// <summary>
/// A few animals standing about in the world, walking slowly and not falling through it.
/// </summary>
/// <remarks>
/// <para>⛳ <b>In Core and taking the world as two delegates</b>, so the whole of it runs under
/// <c>--audit</c> with no window, no streamer and no textures — the same reason
/// <see cref="HeldGrip"/> and <see cref="PlayerAnimator"/> are here. Where a creature ends up
/// standing is arithmetic, and arithmetic that can only be checked by looking at it is arithmetic
/// that gets checked once.</para>
/// <para><b>Deliberately not a physics body.</b> <c>PlayerBody</c> is a swept box with gravity,
/// stepping, climbing and drowning in it, all keyed to one player; an animal that walks about on a
/// surface wants the ground under its feet and nothing else yet. Fleeing, falling and being hit come
/// with the behaviour pass, and that is when it is worth sharing a body.</para>
/// </remarks>
public sealed class CreatureHerd
{
    /// <summary>Blocks a second. A cow is slower than a walking player, which is most of the point.</summary>
    public const float WalkSpeed = 1.6f;

    /// <summary>Degrees a second it can turn. Fast enough to look intentional, slow enough to see.</summary>
    public const float TurnSpeed = 140f;

    /// <summary>How far above a creature's feet counts as head room when looking for a place to stand.</summary>
    public const int HeadRoom = 3;

    /// <summary>Roughly how often one of them makes a noise, in seconds, varied per animal.</summary>
    /// <remarks>
    /// Twelve, which sounds long written down and is not: with a dozen animals about, something is
    /// heard every second or so. A cow that lows every three seconds is a cow nobody wants near
    /// their house.
    /// </remarks>
    public const float SpeaksEvery = 12f;

    private readonly List<Creature> _creatures = [];
    private readonly Random _random;

    public CreatureHerd(int seed) => _random = new Random(seed);

    public IReadOnlyList<Creature> All => _creatures;

    public int Count => _creatures.Count;

    /// <summary>Puts creatures on the ground near a point, and says how many found room.</summary>
    /// <param name="solid">Whether the block at a cell would hold something up.</param>
    /// <param name="kinds">What to place, taken in turn.</param>
    /// <remarks>
    /// ⚠ <b>Spirals out rather than trying the same ring twice.</b> A spawn that gives up when its
    /// first guess is inside a hill puts nothing in the world and looks exactly like a renderer that
    /// draws nothing, which is a day of looking in the wrong place.
    /// </remarks>
    public int Spawn(Func<int, int, int, bool> solid, IReadOnlyList<string> kinds, Vector3 near, int count)
    {
        if (kinds.Count == 0) return 0;

        var placed = 0;

        for (var attempt = 0; attempt < count * 40 && placed < count; attempt++)
        {
            // Out in a widening ring, so a crowded spawn spreads rather than stacking.
            var angle = (float)(_random.NextDouble() * Math.Tau);
            var radius = 4f + attempt * 0.6f;

            var x = (int)MathF.Floor(near.X + MathF.Cos(angle) * radius);
            var z = (int)MathF.Floor(near.Z + MathF.Sin(angle) * radius);

            if (!TryGround(solid, x, z, (int)near.Y + 24, out var y)) continue;

            _creatures.Add(new Creature
            {
                Kind = kinds[placed % kinds.Count],
                Position = new Vector3(x + 0.5f, y, z + 0.5f),
                Yaw = (float)(_random.NextDouble() * 360.0),
                WantsYaw = (float)(_random.NextDouble() * 360.0),
                Thinks = (float)(_random.NextDouble() * 4.0),

                // ⚠ Spread across the whole gap from the start rather than all set to the full
                // wait. A herd placed together and given the same clock lows in chorus every twelve
                // seconds, which is the one thing a field of cows never does.
                Speaks = SpeaksEvery * (float)_random.NextDouble(),
            });

            placed++;
        }

        return placed;
    }

    /// <summary>
    /// The first surface at or below <paramref name="from"/> with room to stand on it.
    /// </summary>
    public static bool TryGround(Func<int, int, int, bool> solid, int x, int z, int from, out float y)
    {
        for (var at = from; at > 1; at--)
        {
            if (!solid(x, at, z)) continue;

            // Room above it, or this is a ledge under an overhang and standing there buries the
            // animal's head in rock.
            for (var head = 1; head <= HeadRoom; head++)
            {
                if (!solid(x, at + head, z)) continue;

                at -= head;
                goto next;
            }

            y = at + 1;
            return true;

            next: ;
        }

        y = 0f;
        return false;
    }

    /// <summary>Walks the herd on by one step.</summary>
    /// <remarks>
    /// One decision every few seconds, a slow turn toward it, and a step forward that is refused when
    /// there is no ground to put it on. ⚠ <b>The refusal turns rather than stopping</b>, so an animal
    /// that walks into a cliff wanders off along it instead of standing there pressing into the rock.
    /// </remarks>
    public void Update(float dt, Func<int, int, int, bool> solid)
    {
        foreach (var creature in _creatures)
        {
            creature.Spoke = false;
            creature.Speaks -= dt;

            if (creature.Speaks <= 0f)
            {
                creature.Speaks = SpeaksEvery * (0.6f + (float)_random.NextDouble() * 0.8f);
                creature.Spoke = true;
            }

            creature.Thinks -= dt;

            if (creature.Thinks <= 0f)
            {
                creature.Thinks = 2f + (float)_random.NextDouble() * 5f;
                creature.Moving = _random.NextDouble() < 0.6;
                creature.WantsYaw = (float)(_random.NextDouble() * 360.0);
            }

            // Turn the short way round, so a creature wanting to face 350 from 10 turns twenty
            // degrees rather than three hundred and forty.
            var difference = Wrap(creature.WantsYaw - creature.Yaw);
            var step = MathF.Min(MathF.Abs(difference), TurnSpeed * dt);
            creature.Yaw = Wrap(creature.Yaw + MathF.Sign(difference) * step);

            if (!creature.Moving) continue;

            var yaw = float.DegreesToRadians(creature.Yaw);
            var ahead = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
            var wanted = creature.Position + ahead * (WalkSpeed * dt);

            var x = (int)MathF.Floor(wanted.X);
            var z = (int)MathF.Floor(wanted.Z);

            // A step is allowed onto ground within a block of the one being stood on. Anything more
            // is a cliff or a wall, and the answer to both is to go somewhere else.
            if (TryGround(solid, x, z, (int)creature.Position.Y + 1, out var y)
                && MathF.Abs(y - creature.Position.Y) <= 1.01f)
            {
                creature.Position = new Vector3(wanted.X, y, wanted.Z);
                continue;
            }

            creature.WantsYaw = Wrap(creature.Yaw + 90f + (float)_random.NextDouble() * 180f);
            creature.Thinks = 1f + (float)_random.NextDouble() * 2f;
        }
    }

    private static float Wrap(float degrees)
    {
        degrees %= 360f;
        if (degrees > 180f) degrees -= 360f;
        if (degrees < -180f) degrees += 360f;
        return degrees;
    }

    /// <summary>
    /// Checks a herd lands on the ground, stays on it, and does not walk through walls.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Every claim is paired with the state that would satisfy a weaker one.</b> "They all
    /// spawned" says nothing if they spawned inside a hill, so the ground under each one is asked
    /// for as well; "they moved" says nothing if they moved through a wall, so the walled run is a
    /// separate arm; and a herd that never moves at all passes both of those, so the flat arm has to
    /// show them somewhere else by the end.
    /// </remarks>
    public static List<string> Validate()
    {
        var faults = new List<string>();

        // A floor at y 64 and nothing else. Ground is solid below 64, air at and above it.
        static bool Flat(int x, int y, int z) => y < 64;

        var herd = new CreatureHerd(7);
        var kinds = new[] { "cow", "pig" };
        var placed = herd.Spawn(Flat, kinds, new Vector3(0f, 64f, 0f), 6);

        if (placed != 6) faults.Add($"{placed} of 6 creatures found room to stand on an open plain");

        foreach (var creature in herd.All)
        {
            if (MathF.Abs(creature.Position.Y - 64f) < 0.01f) continue;
            faults.Add($"a {creature.Kind} spawned at y {creature.Position.Y:F2} on a floor whose top is 64");
        }

        var start = herd.All.Select(c => c.Position).ToList();

        for (var i = 0; i < 600; i++) herd.Update(1f / 60f, Flat);

        var moved = 0;
        for (var i = 0; i < herd.Count; i++)
        {
            if (Vector3.Distance(herd.All[i].Position, start[i]) > 0.5f) moved++;

            if (MathF.Abs(herd.All[i].Position.Y - 64f) > 0.01f)
                faults.Add($"a {herd.All[i].Kind} walked off its own floor to y {herd.All[i].Position.Y:F2}");
        }

        // ⚠ Ten seconds at a 60% chance of walking per decision. All six standing still is a herd
        // that is not being stepped at all, which every other claim here is happy with.
        if (moved == 0) faults.Add("not one of six creatures moved in ten seconds of walking");

        // ⛔ The control arm: a floor with a wall down the middle. Nothing may cross it — and the
        // flat arm above cannot catch that, because there is nothing there to cross.
        static bool Walled(int x, int y, int z) => y < 64 || (x == 3 && y < 70);

        var penned = new CreatureHerd(11);
        penned.Spawn(Walled, ["cow"], new Vector3(0f, 64f, 0f), 6);

        var side = penned.All.Select(c => c.Position.X < 3f).ToList();

        for (var i = 0; i < 900; i++) penned.Update(1f / 60f, Walled);

        for (var i = 0; i < penned.Count; i++)
        {
            if (penned.All[i].Position.X < 3f == side[i]) continue;
            faults.Add($"a creature crossed a wall it started {(side[i] ? "west" : "east")} of");
        }

        return faults;
    }
}
