using System.Numerics;

namespace Driftwood.Core.Entities;

/// <summary>One kind that can be spawned: our name for it, and how big it stands.</summary>
/// <remarks>
/// ⚠ <b>The size comes from the mesh, never from a table written beside it.</b> A creature's box is
/// what the boxes it is made of actually add up to — <see cref="CreatureMesh.PosedBounds"/> — and a
/// second set of numbers is a second thing to keep in step with the model. It is carried here rather
/// than looked up per frame because a herd is stepped sixty times a second and a skeleton never
/// changes shape.
/// </remarks>
public readonly record struct SpawnKind(string Name, Vector3 Size);

/// <summary>One animal in the world: what it is, where it is, and which way it is pointing.</summary>
public sealed class Creature
{
    public required string Kind { get; init; }

    /// <summary>How big it stands, in blocks. What a blow has to land inside.</summary>
    public required Vector3 Size { get; init; }

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

    /// <summary>Half-hearts remaining — the same unit the player's health is kept in.</summary>
    public int Health { get; set; }

    /// <summary>Half-hearts it started with, so a hurt one can be drawn as a fraction.</summary>
    public required int MaxHealth { get; init; }

    /// <summary>Seconds left of the red flash a blow leaves. Zero when it is not being hit.</summary>
    public float HurtFor { get; set; }

    /// <summary>Seconds left of running away. It moves faster and re-thinks less while this runs.</summary>
    public float FleeFor { get; set; }

    /// <summary>Blocks a second it is falling at, when the ground has gone from under it.</summary>
    public float FallSpeed { get; set; }

    /// <summary>How far it has fallen without touching down, in blocks.</summary>
    public float FellFor { get; set; }

    /// <summary>True once its fleece has been taken and until it has grown back.</summary>
    public bool Shorn { get; set; }

    /// <summary>Seconds until a shorn one is worth shearing again.</summary>
    public float Regrows { get; set; }

    /// <summary>Seconds until it leaves something behind without being touched.</summary>
    public float Sheds { get; set; }

    /// <summary>Set for the one step on which it did. Read and cleared by whoever spawns drops.</summary>
    public bool Shed { get; set; }

    /// <summary>Seconds left of falling over. Counts down from <see cref="CreatureHerd.DyingSeconds"/>.</summary>
    /// <remarks>
    /// ⛳ <b>A death is a moment, not an event.</b> Taken out of the world on the frame its health
    /// reached nothing, an animal simply stops existing — the blow lands and the field is empty, with
    /// nothing on screen connecting the two. Half a second of it going over is what makes a kill read
    /// as a kill. It is not alive for any of it: nothing can hit it, it does not walk, and its drops
    /// were handed out on the frame it died.
    /// </remarks>
    public float DyingFor { get; set; }

    public bool Alive => Health > 0;

    /// <summary>How far over it has gone, 0 upright to 1 flat. Zero for anything still alive.</summary>
    public float TippedOver =>
        Health > 0 ? 0f : Math.Clamp(1f - DyingFor / CreatureHerd.DyingSeconds, 0f, 1f);

    /// <summary>The lowest and highest corners of the box a blow has to land inside.</summary>
    /// <remarks>
    /// ⚠ <b>Square in plan, taking the wider of the two horizontal measurements.</b> A cow is 0.75
    /// across and 1.50 long, so a box that turned with it would be four times harder to hit end-on
    /// than broadside — for a target that is walking away from you, which is the whole of the case.
    /// Generous on the narrow axis is the right way to be wrong here.
    /// </remarks>
    public (Vector3 Min, Vector3 Max) Bounds()
    {
        var half = MathF.Max(MathF.Max(Size.X, Size.Z), 0.3f) * 0.5f;
        return (
            new Vector3(Position.X - half, Position.Y, Position.Z - half),
            new Vector3(Position.X + half, Position.Y + MathF.Max(Size.Y, 0.3f), Position.Z + half));
    }

    /// <summary>Where a blow lands and a sound comes from — its middle, not its feet.</summary>
    public Vector3 Middle => Position + new Vector3(0f, MathF.Max(Size.Y, 0.3f) * 0.5f, 0f);
}

/// <summary>One animal that has just died, for whoever turns that into drops and a noise.</summary>
public readonly record struct CreatureDeath(string Kind, Vector3 Position, bool Shorn);

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

    /// <summary>
    /// What a blow sounds like landing, and what a death sounds like.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Not per species, and that is a decision rather than a shortcut.</b> The user's recordings
    /// hold one voice per animal and a whole folder of impacts that nothing had ever played — a
    /// punch, a slap, a swipe, a crunch. A blow is the sound of the blow; what tells a cow from a
    /// chicken is the voice that cries out with it, which is the clip above played at a pitch that
    /// says it means it. Two recordings and a species table would need eight more files nobody has.
    /// </remarks>
    public static readonly string[] Blows = ["punch", "punch_2", "punch_3", "slap"];

    /// <summary>What the last blow sounds like.</summary>
    public static readonly string[] Deaths = ["crunch", "crunch_quick", "crunch_splat"];

    /// <summary>What taking a fleece off sounds like — the one shear-like noise in the set.</summary>
    public const string Shear = "swipe";

    /// <summary>The clip a creature makes when it has nothing else to say, or empty for none.</summary>
    public static string IdleFor(string kind) => Idle.GetValueOrDefault(kind, "");

    /// <summary>The clip it makes when it goes for somebody, or its idle one if it has only one.</summary>
    public static string AngryFor(string kind) =>
        Angry.TryGetValue(kind, out var clip) ? clip : IdleFor(kind);

    /// <summary>Every clip named here, for the check that they all resolve.</summary>
    public static IEnumerable<string> All =>
        Idle.Values.Concat(Angry.Values).Concat(Blows).Concat(Deaths).Append(Shear);
}

/// <summary>
/// A few animals standing about in the world, walking slowly, falling when the ground goes, and
/// running from whoever hits them until they stop getting up.
/// </summary>
/// <remarks>
/// <para>⛳ <b>In Core and taking the world as two delegates</b>, so the whole of it runs under
/// <c>--audit</c> with no window, no streamer and no textures — the same reason
/// <see cref="HeldGrip"/> and <see cref="PlayerAnimator"/> are here. Where a creature ends up
/// standing is arithmetic, and arithmetic that can only be checked by looking at it is arithmetic
/// that gets checked once.</para>
/// <para><b>Deliberately not a physics body.</b> <c>PlayerBody</c> is a swept box with gravity,
/// stepping, climbing and drowning in it, all keyed to one player. An animal wants the ground under
/// its feet, a fall when that ground is dug out, and nothing else — sharing the player's body would
/// mean a cow that can sneak, climb ladders and drown, none of which anything ever asks it to do.
/// </para>
/// </remarks>
public sealed class CreatureHerd
{
    /// <summary>Blocks a second. A cow is slower than a walking player, which is most of the point.</summary>
    public const float WalkSpeed = 1.6f;

    /// <summary>
    /// Blocks a second while running away. Below the player's 4.3, and deliberately.
    /// </summary>
    /// <remarks>
    /// A creature that outran a player would be one nobody ever caught, and the whole reason to run
    /// is to make catching it a chase rather than a formality. Twice its walk and four fifths of a
    /// player's stride: it gets away from a stroll and not from someone who means it.
    /// </remarks>
    public const float PanicSpeed = 3.4f;

    /// <summary>Seconds of running away one blow buys.</summary>
    public const float PanicSeconds = 5f;

    /// <summary>Seconds the red flash of a blow lasts.</summary>
    public const float HurtSeconds = 0.35f;

    /// <summary>Seconds before a hit one can be hit again, so one swing is one blow.</summary>
    public const float HitCooldown = 0.25f;

    /// <summary>Seconds a dead one takes to go over before it is taken out of the world.</summary>
    public const float DyingSeconds = 0.55f;

    /// <summary>Degrees a second it can turn. Fast enough to look intentional, slow enough to see.</summary>
    public const float TurnSpeed = 140f;

    /// <summary>Degrees a second a frightened one can turn. It does not deliberate.</summary>
    public const float PanicTurnSpeed = 540f;

    /// <summary>How far above a creature's feet counts as head room when looking for a place to stand.</summary>
    public const int HeadRoom = 3;

    /// <summary>Blocks a second squared, pulling anything with no ground under it down.</summary>
    /// <remarks>
    /// Gentler than the player's 32 and capped lower, because this is not a jumping body — it is the
    /// answer to "somebody dug the block out from under a cow", and a cow that drops like a stone
    /// reads as a fault where one that sinks reads as an animal.
    /// </remarks>
    public const float Gravity = 26f;

    /// <summary>Fastest anything falls, blocks a second.</summary>
    public const float TerminalSpeed = 42f;

    /// <summary>Blocks of falling that cost nothing. The player's own figure.</summary>
    public const float SafeFall = 3f;

    /// <summary>Roughly how often one of them makes a noise, in seconds, varied per animal.</summary>
    /// <remarks>
    /// Twelve, which sounds long written down and is not: with a dozen animals about, something is
    /// heard every second or so. A cow that lows every three seconds is a cow nobody wants near
    /// their house.
    /// </remarks>
    public const float SpeaksEvery = 12f;

    private readonly List<Creature> _creatures = [];
    private readonly List<CreatureDeath> _dead = [];
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
    public int Spawn(Func<int, int, int, bool> solid, IReadOnlyList<SpawnKind> kinds, Vector3 near, int count)
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

            var kind = kinds[placed % kinds.Count];

            _creatures.Add(new Creature
            {
                Kind = kind.Name,
                Size = kind.Size,
                MaxHealth = CreatureVitals.HealthFor(kind.Name),
                Health = CreatureVitals.HealthFor(kind.Name),
                Position = new Vector3(x + 0.5f, y, z + 0.5f),
                Yaw = (float)(_random.NextDouble() * 360.0),
                WantsYaw = (float)(_random.NextDouble() * 360.0),
                Thinks = (float)(_random.NextDouble() * 4.0),

                // ⚠ Spread across the whole gap from the start rather than all set to the full
                // wait. A herd placed together and given the same clock lows in chorus every twelve
                // seconds, which is the one thing a field of cows never does.
                Speaks = SpeaksEvery * (float)_random.NextDouble(),
                Sheds = CreatureVitals.ShedsEvery * (0.4f + (float)_random.NextDouble()),
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

    /// <summary>
    /// Hits one, and says whether that was the blow that finished it.
    /// </summary>
    /// <param name="from">
    /// Where the blow came from. It runs directly away from this, which is the only thing that makes
    /// a chase read as a chase rather than as an animal wandering off.
    /// </param>
    /// <remarks>
    /// ⛔ <b>The cooldown is here and not at the caller.</b> A swing is one blow however many frames
    /// the button is held and however many things ask — the arm strikes on an animation beat, the
    /// hand may hold a tool that hits twice, and both would otherwise be free to whittle a cow down
    /// in a frame. Owned by the thing being hit, it is one rule rather than one per attacker.
    /// </remarks>
    public bool Hurt(Creature creature, int halfHearts, Vector3 from)
    {
        if (halfHearts <= 0 || !creature.Alive) return false;

        // Still ringing from the last one. Reading the flash rather than keeping a second clock:
        // they measure the same thing and two of them is one that can disagree.
        if (creature.HurtFor > HurtSeconds - HitCooldown) return false;

        creature.Health = Math.Max(0, creature.Health - halfHearts);
        creature.HurtFor = HurtSeconds;

        if (!creature.Alive)
        {
            creature.DyingFor = DyingSeconds;
            _dead.Add(new CreatureDeath(creature.Kind, creature.Middle, creature.Shorn));
            return true;
        }

        var away = creature.Position - from;
        away.Y = 0f;

        // Hit from exactly above or below there is no direction to run in, so it keeps the one it
        // had. Normalising a zero vector is how an animal ends up at NaN and is never seen again.
        if (away.LengthSquared() > 1e-6f)
            creature.Yaw = float.RadiansToDegrees(MathF.Atan2(away.Z, away.X));

        creature.WantsYaw = creature.Yaw;
        creature.Moving = true;
        creature.FleeFor = PanicSeconds;
        creature.Thinks = 0.6f;

        return false;
    }

    /// <summary>Everything that has died since this was last asked, and forgets them.</summary>
    /// <remarks>
    /// Drained rather than read, so the caller cannot spawn one cow's worth of leather twice by
    /// looking at the same list on two frames. The same shape as
    /// <c>PlayerAnimator.TakeStrikes</c> and for the same reason.
    /// </remarks>
    public List<CreatureDeath> TakeDead()
    {
        if (_dead.Count == 0) return [];
        var taken = new List<CreatureDeath>(_dead);
        _dead.Clear();
        return taken;
    }

    /// <summary>Takes one out of the world without it having died — what a despawn is.</summary>
    public void Remove(Creature creature) => _creatures.Remove(creature);

    /// <summary>
    /// The nearest creature a ray from <paramref name="origin"/> runs into, or null.
    /// </summary>
    /// <param name="distance">How far along the ray it was hit, or the reach when nothing was.</param>
    /// <remarks>
    /// ⚠ <b>The caller compares this against its block target and takes the nearer.</b> Doing it here
    /// would mean the herd knowing about the world's geometry; doing it at the caller is one
    /// comparison in the one place that already has both answers — and it is what stops a swing
    /// passing through a wall to hit the cow standing behind it.
    /// </remarks>
    public Creature? Pick(Vector3 origin, Vector3 direction, float reach, out float distance)
    {
        Creature? nearest = null;
        distance = reach;

        foreach (var creature in _creatures)
        {
            if (!creature.Alive) continue;

            var (min, max) = creature.Bounds();
            if (!RayBox(origin, direction, min, max, out var at) || at >= distance) continue;

            nearest = creature;
            distance = at;
        }

        return nearest;
    }

    /// <summary>Where a ray first enters a box, or false when it misses or the box is behind it.</summary>
    /// <remarks>
    /// The slab method: each axis gives the interval of the ray inside that pair of planes, and the
    /// box is entered where the last of them starts and left where the first of them ends. A zero
    /// component divides to an infinity, which orders correctly against real numbers — so the
    /// degenerate case needs no branch, and a branch is exactly where this is usually got wrong.
    /// </remarks>
    public static bool RayBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, out float at)
    {
        var inverse = new Vector3(1f / direction.X, 1f / direction.Y, 1f / direction.Z);

        var a = (min - origin) * inverse;
        var b = (max - origin) * inverse;

        var near = Vector3.Min(a, b);
        var far = Vector3.Max(a, b);

        var enter = MathF.Max(MathF.Max(near.X, near.Y), near.Z);
        var leave = MathF.Min(MathF.Min(far.X, far.Y), far.Z);

        // Standing inside it counts as a hit at zero rather than as a miss, which is what makes a
        // swing at something pressed against you land.
        at = MathF.Max(enter, 0f);
        return leave >= enter && leave >= 0f;
    }

    /// <summary>Walks the herd on by one step.</summary>
    /// <remarks>
    /// One decision every few seconds, a slow turn toward it, and a step forward that is refused when
    /// there is no ground to put it on. ⚠ <b>The refusal turns rather than stopping</b>, so an animal
    /// that walks into a cliff wanders off along it instead of standing there pressing into the rock.
    /// </remarks>
    public void Update(float dt, Func<int, int, int, bool> solid)
    {
        // ⛔ The dead are swept HERE and never inside Hurt, because a fall calls Hurt from inside the
        // walk below — and taking a creature out of the list being walked is how a herd that loses
        // one to a cliff takes the whole update down with it.
        for (var i = _creatures.Count - 1; i >= 0; i--)
        {
            var dying = _creatures[i];
            if (dying.Alive) continue;

            dying.DyingFor -= dt;
            if (dying.DyingFor <= 0f) _creatures.RemoveAt(i);
        }

        foreach (var creature in _creatures)
        {
            if (!creature.Alive) continue;

            creature.Spoke = false;
            creature.Shed = false;

            creature.HurtFor = MathF.Max(0f, creature.HurtFor - dt);
            creature.FleeFor = MathF.Max(0f, creature.FleeFor - dt);

            if (creature.Shorn)
            {
                creature.Regrows -= dt;
                if (creature.Regrows <= 0f) creature.Shorn = false;
            }

            // What it leaves behind without being touched. ⚠ Only for the kinds that shed anything
            // — otherwise every animal in the world would be running a clock to answer "no".
            if (CreatureVitals.Sheds(creature.Kind))
            {
                creature.Sheds -= dt;
                if (creature.Sheds <= 0f)
                {
                    creature.Sheds = CreatureVitals.ShedsEvery * (0.7f + (float)_random.NextDouble() * 0.6f);
                    creature.Shed = true;
                }
            }

            creature.Speaks -= dt;

            if (creature.Speaks <= 0f)
            {
                creature.Speaks = SpeaksEvery * (0.6f + (float)_random.NextDouble() * 0.8f);
                creature.Spoke = true;
            }

            if (Fall(creature, dt, solid)) continue;

            creature.Thinks -= dt;

            if (creature.Thinks <= 0f)
            {
                creature.Thinks = 2f + (float)_random.NextDouble() * 5f;

                // ⚠ A frightened animal keeps running and keeps its heading. Re-deciding on the
                // ordinary clock is what turned a bolting cow back toward whoever hit it, every few
                // seconds, which reads as an animal that wants another go rather than one fleeing.
                if (creature.FleeFor <= 0f)
                {
                    creature.Moving = _random.NextDouble() < 0.6;
                    creature.WantsYaw = (float)(_random.NextDouble() * 360.0);
                }
            }

            // Turn the short way round, so a creature wanting to face 350 from 10 turns twenty
            // degrees rather than three hundred and forty.
            var panicking = creature.FleeFor > 0f;
            var difference = Wrap(creature.WantsYaw - creature.Yaw);
            var step = MathF.Min(
                MathF.Abs(difference), (panicking ? PanicTurnSpeed : TurnSpeed) * dt);
            creature.Yaw = Wrap(creature.Yaw + MathF.Sign(difference) * step);

            if (!creature.Moving) continue;

            var yaw = float.DegreesToRadians(creature.Yaw);
            var ahead = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
            var wanted = creature.Position + ahead * ((panicking ? PanicSpeed : WalkSpeed) * dt);

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

            // ⚠ A cornered animal turns a little rather than the same ninety every time. Frightened
            // it turns tightly and keeps running; grazing it may as well go anywhere.
            creature.WantsYaw = panicking
                ? Wrap(creature.Yaw + (_random.NextDouble() < 0.5 ? -1f : 1f) * (35f + (float)_random.NextDouble() * 55f))
                : Wrap(creature.Yaw + 90f + (float)_random.NextDouble() * 180f);

            creature.Thinks = panicking ? 0.4f : 1f + (float)_random.NextDouble() * 2f;
        }
    }

    /// <summary>
    /// Drops one with nothing under it, and says whether it spent this step falling.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The distance has to be read while it is still in the air.</b> Exactly the trap
    /// <see cref="PlayerVitals"/> names: the frame it lands is the frame the fall is over, so
    /// anything that waits for the landing to ask how far it fell is told nothing, every time. The
    /// total is kept here and spent on the touchdown step.
    /// </remarks>
    private bool Fall(Creature creature, float dt, Func<int, int, int, bool> solid)
    {
        var x = (int)MathF.Floor(creature.Position.X);
        var z = (int)MathF.Floor(creature.Position.Z);

        // ⚠ From a block above its feet, not from its feet. Standing on a surface, the cell its feet
        // are in is the air just above the ground — starting the search there would find that same
        // surface, which is right, but starting one lower would find whatever is under the floor.
        var ground = TryGround(solid, x, z, (int)MathF.Floor(creature.Position.Y) + 1, out var y)
            ? y
            : 0f;

        if (creature.Position.Y - ground <= 0.02f)
        {
            // Landed, or never left. Anything that fell far enough pays for it now.
            if (creature.FellFor > SafeFall)
                Hurt(creature, (int)MathF.Round(creature.FellFor - SafeFall), creature.Middle);

            creature.Position = creature.Position with { Y = ground };
            creature.FallSpeed = 0f;
            creature.FellFor = 0f;
            return false;
        }

        creature.FallSpeed = MathF.Min(creature.FallSpeed + Gravity * dt, TerminalSpeed);

        var drop = creature.FallSpeed * dt;
        var to = MathF.Max(creature.Position.Y - drop, ground);

        creature.FellFor += creature.Position.Y - to;
        creature.Position = creature.Position with { Y = to };

        return true;
    }

    private static float Wrap(float degrees)
    {
        degrees %= 360f;
        if (degrees > 180f) degrees -= 360f;
        if (degrees < -180f) degrees += 360f;
        return degrees;
    }

    /// <summary>
    /// Checks a herd lands on the ground, stays on it, does not walk through walls, falls when the
    /// ground goes, and can be hit until it stops getting up.
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

        var kinds = new[]
        {
            new SpawnKind("cow", new Vector3(0.75f, 1.56f, 1.50f)),
            new SpawnKind("pig", new Vector3(0.63f, 0.88f, 1.13f)),
        };

        var herd = new CreatureHerd(7);
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
        penned.Spawn(Walled, [kinds[0]], new Vector3(0f, 64f, 0f), 6);

        var side = penned.All.Select(c => c.Position.X < 3f).ToList();

        for (var i = 0; i < 900; i++) penned.Update(1f / 60f, Walled);

        for (var i = 0; i < penned.Count; i++)
        {
            if (penned.All[i].Position.X < 3f == side[i]) continue;
            faults.Add($"a creature crossed a wall it started {(side[i] ? "west" : "east")} of");
        }

        faults.AddRange(ValidateFalling());
        faults.AddRange(ValidateFighting());

        return faults;
    }

    /// <summary>Checks one dropped down a shaft lands on the floor rather than in mid-air.</summary>
    /// <remarks>
    /// ⛔ <b>Paired with a control that does not fall.</b> "It ended up on the floor" is also true of
    /// an animal that was teleported there on the first step, and of one that was never in the air at
    /// all — so the arm that matters is the one that asserts it was still above the floor part way
    /// down, and that its fall took roughly the time gravity says a fall of that height takes.
    /// </remarks>
    private static List<string> ValidateFalling()
    {
        var faults = new List<string>();

        // A pillar one block across at x 0, z 0, with a floor twenty blocks below it.
        static bool Pillar(int x, int y, int z) => y < 44 || (x == 0 && z == 0 && y < 64);

        var kind = new SpawnKind("cow", new Vector3(0.75f, 1.56f, 1.50f));

        var herd = new CreatureHerd(3);
        herd.Spawn(Pillar, [kind], new Vector3(0f, 64f, 0f), 1);

        if (herd.Count != 1) return ["nothing found room to stand for the falling check"];

        var creature = herd.All[0];

        // Stood on the pillar, whether it spawned there or beside it. Both are legitimate places for
        // the spiral to have put it, and only one of them is a fall.
        creature.Position = new Vector3(0.5f, 64f, 0.5f);
        creature.Moving = false;

        // ⛔ The pillar is dug out from under it — which is the case this exists for, and is not the
        // same as spawning it in mid-air. A creature that only ever falls at the moment it is placed
        // would pass a check written the other way round.
        static bool Dug(int x, int y, int z) => y < 44;

        var airborne = 0;
        var landedAfter = -1f;

        for (var step = 0; step < 240; step++)
        {
            herd.Update(1f / 60f, Dug);

            if (creature.Position.Y > 44.01f) airborne++;
            else if (landedAfter < 0f) landedAfter = step / 60f;
        }

        if (MathF.Abs(creature.Position.Y - 44f) > 0.01f)
            faults.Add($"a creature with nothing under it ended at y {creature.Position.Y:F2} rather than on the floor at 44");

        // Twenty blocks under 26 blocks/s² is about 1.24 s. Anything under a quarter of a second
        // means it was moved rather than dropped, which is exactly what a teleport would read as.
        if (landedAfter is >= 0f and < 0.25f)
            faults.Add($"a creature crossed twenty blocks in {landedAfter:F2} s, which is not a fall");

        if (airborne < 20)
            faults.Add($"a creature was above the floor for {airborne} steps of a twenty-block drop");

        // And it hurt itself doing it — twenty blocks is seventeen past the safe three.
        if (creature.Health >= creature.MaxHealth)
            faults.Add("a creature fell twenty blocks and took no damage");

        return faults;
    }

    /// <summary>Checks a creature can be aimed at, hurt, killed, and only dies once.</summary>
    private static List<string> ValidateFighting()
    {
        var faults = new List<string>();

        static bool Flat(int x, int y, int z) => y < 64;

        var kind = new SpawnKind("cow", new Vector3(0.75f, 1.56f, 1.50f));

        var herd = new CreatureHerd(19);
        herd.Spawn(Flat, [kind], new Vector3(0f, 64f, 0f), 1);
        if (herd.Count != 1) return ["nothing found room to stand for the fighting check"];

        var creature = herd.All[0];
        creature.Position = new Vector3(0.5f, 64f, 0.5f);

        // Aimed at from four blocks away, level with its middle.
        var eye = new Vector3(0.5f, 64.78f, -3.5f);
        var forward = Vector3.Normalize(creature.Middle - eye);

        if (herd.Pick(eye, forward, 5f, out var range) != creature)
            faults.Add("a ray straight at a creature four blocks away did not find it");
        else if (MathF.Abs(range - 4f) > 0.9f)
            faults.Add($"a creature four blocks away was picked at {range:F2} blocks");

        // ⛔ THE CONTROLS, and both of them are needed. Aimed a quarter turn away it must miss — a
        // pick that answered "the nearest creature" whatever the direction would pass the arm above
        // and fail here. And a reach that stops short must miss too, or reach means nothing.
        if (herd.Pick(eye, new Vector3(1f, 0f, 0f), 5f, out _) is not null)
            faults.Add("a ray aimed ninety degrees away from a creature still hit it");

        if (herd.Pick(eye, forward, 2f, out _) is not null)
            faults.Add("a creature four blocks away was hit by a swing that reaches two");

        // Hurting it. One blow, then the same blow again immediately, which must not land.
        var full = creature.Health;
        herd.Hurt(creature, 3, eye);
        var afterOne = creature.Health;

        herd.Hurt(creature, 3, eye);
        if (creature.Health != afterOne)
            faults.Add("two blows inside one cooldown both landed");

        if (afterOne != full - 3)
            faults.Add($"a blow of 3 took {full - afterOne} half-hearts");

        if (creature.FleeFor <= 0f) faults.Add("a creature that was hit did not run");

        // ⚠ Away from the blow, not toward it. Written as the sign of the dot product rather than as
        // an angle, so it says nothing about a convention for where zero points.
        var heading = float.DegreesToRadians(creature.Yaw);
        var facing = new Vector3(MathF.Cos(heading), 0f, MathF.Sin(heading));
        var fled = creature.Position - new Vector3(eye.X, creature.Position.Y, eye.Z);

        if (Vector3.Dot(facing, Vector3.Normalize(fled)) < 0.5f)
            faults.Add("a creature hit from the south did not turn away from it");

        // Killing it. Enough blows, spaced past the cooldown.
        var blows = 0;
        while (creature.Alive && blows < 40)
        {
            herd.Update(HitCooldown + 0.02f, Flat);
            herd.Hurt(creature, 3, eye);
            blows++;
        }

        if (creature.Alive) faults.Add($"a creature survived {blows} blows of 3 with {creature.MaxHealth} health");

        var dead = herd.TakeDead();
        if (dead.Count != 1) faults.Add($"{dead.Count} deaths were reported for one creature");

        // ⛔ And exactly once. A list that is read rather than drained hands the same cow's leather
        // out on every frame after it dies, which is a duplication bug that looks like generosity.
        if (herd.TakeDead().Count != 0) faults.Add("a death was reported twice");

        // A dead one cannot be hit again, and cannot be aimed at.
        herd.Hurt(creature, 3, eye);
        if (herd.TakeDead().Count != 0) faults.Add("hitting a dead creature reported another death");

        if (herd.Pick(eye, forward, 5f, out _) is not null)
            faults.Add("a dead creature was still in the way of a swing");

        return faults;
    }
}
