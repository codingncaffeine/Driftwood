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
public readonly record struct SpawnKind(
    string Name, Vector3 Size, bool Hostile = false, CreatureMove Move = CreatureMove.Walk);

/// <summary>One blow a creature landed on the player, for whoever turns that into damage.</summary>
public readonly record struct CreatureAttack(string Kind, Vector3 Position, int HalfHearts);

/// <summary>One animal in the world: what it is, where it is, and which way it is pointing.</summary>
public sealed class Creature
{
    /// <summary>
    /// Session-stable identity used to attribute combat, effects, pet targets and rewards. It is
    /// deliberately regenerated when a world is loaded: no persistent rule refers to a wild
    /// creature after that creature has left the herd.
    /// </summary>
    public string RuntimeId { get; } = Guid.NewGuid().ToString("N");

    public required string Kind { get; init; }

    /// <summary>How big it stands, in blocks. What a blow has to land inside.</summary>
    public required Vector3 Size { get; init; }

    /// <summary>True for anything that comes at you rather than away.</summary>
    /// <remarks>
    /// ⚠ Carried on the creature rather than looked up per step. It is decided once, at the spawn
    /// that placed it, and a per-frame lookup into a table by string is the sort of thing that costs
    /// nothing until there are two hundred of them.
    /// </remarks>
    public required bool Hostile { get; init; }

    /// <summary>How it crosses the world. Carried for the same reason <see cref="Hostile"/> is.</summary>
    public CreatureMove Move { get; init; } = CreatureMove.Walk;

    /// <summary>Seconds until a hopping kind may launch again. Meaningless for anything else.</summary>
    public float HopRest { get; set; }

    /// <summary>Seconds until it may swing again.</summary>
    public float Swings { get; set; }

    /// <summary>
    /// Seconds of daylight exposure, up to the scorch threshold. Damage itself belongs to the
    /// shared effect service; this is only the grace-period sensor and the renderer's warning.
    /// </summary>
    public float Burning { get; set; }

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

    /// <summary>Transient spell control, refreshed by the owner of the effect service each frame.</summary>
    public float MagicMovementScale { get; set; } = 1f;

    public bool MagicallyFeared { get; set; }

    /// <summary>True once a blow has turned a retaliating kind on the player. Never unset: a wolf
    /// crossed stays crossed, which is what gives striking one a price worth thinking about.</summary>
    public bool Provoked { get; set; }

    /// <summary>Blocks a second it is falling at, when the ground has gone from under it.</summary>
    public float FallSpeed { get; set; }

    /// <summary>How far it has fallen without touching down, in blocks.</summary>
    public float FellFor { get; set; }

    /// <summary>Seconds spent beyond the keep radius with nobody near. See <see cref="CreatureHerd.Despawn"/>.</summary>
    public float Idle { get; set; }

    /// <summary>The walk it is following toward its target, and how far along it is.</summary>
    public List<PathStep>? Path { get; set; }
    public int PathAt { get; set; }

    /// <summary>Where the path was aimed, so a target that has moved on gets a new one.</summary>
    public PathStep PathGoal { get; set; }

    /// <summary>Seconds until the path is looked at again whatever happens.</summary>
    public float Repath { get; set; }

    /// <summary>
    /// A wander's own route — somewhere picked and walked to, the reference's stroll — kept apart
    /// from <see cref="Path"/>, which the hunt owns and clears whenever there is nothing to hunt.
    /// </summary>
    public List<PathStep>? Stroll { get; set; }
    public int StrollAt { get; set; }

    /// <summary>True while it has someone to come at — acquired by sight, kept by memory.</summary>
    public bool Targeting { get; set; }

    /// <summary>Whether the last look found the target in view, and when it looks again.</summary>
    public bool SeesTarget { get; set; }
    public float SightCheck { get; set; }

    /// <summary>Seconds the target has been out of view while still being hunted.</summary>
    public float Unseen { get; set; }

    /// <summary>Seconds of walking that went nowhere. A path that is not working is replaced.</summary>
    public float Stuck { get; set; }

    /// <summary>True from a leap's launch to its landing; the leap travels while it is in the air.</summary>
    public bool Leaping { get; set; }

    /// <summary>True while a climber is holding a wall rather than standing on anything.</summary>
    public bool Clinging { get; set; }

    /// <summary>A flier's circle: where round it is, how high it rides, and when it next dives.</summary>
    public float Orbit { get; set; }
    public float RideHeight { get; set; }
    public float SwoopIn { get; set; }
    public bool Swooping { get; set; }
    public float SwoopFor { get; set; }

    /// <summary>How grown it is: 0 for a newborn, 1 for an adult. Everything spawns grown.</summary>
    public float Grown { get; set; } = 1f;

    /// <summary>True once it is full-grown — what may court, and what drops anything.</summary>
    public bool Adult => Grown >= 1f;

    /// <summary>Its present size against its adult size: drawn at this, and hit at this.</summary>
    /// <remarks>
    /// ⚠ A floor of just over half rather than a straight lerp from nothing — a newborn a tenth
    /// of its mother's size is a rodent wearing her skin, and it would be nearly unclickable.
    /// </remarks>
    public float Scale => (0.55f + 0.45f * Math.Clamp(Grown, 0f, 1f)) * Bulk;

    /// <summary>
    /// A slime's size class — 1, 2 or 4, the reference's — which it is drawn, hit and hurt by.
    /// Every other kind is 1.
    /// </summary>
    public int Bulk { get; set; } = 1;

    /// <summary>Shots left in a cinderwisp's volley, and the wait before the next.</summary>
    public int Volley { get; set; }
    public float VolleyIn { get; set; }

    /// <summary>How long an ashwail has been drawing breath for a bolt.</summary>
    public float Charge { get; set; }

    /// <summary>Where a big floater is drifting to.</summary>
    public Vector3 Drift { get; set; }

    /// <summary>Seconds before a witch will drink again.</summary>
    public float DrinkRest { get; set; }

    /// <summary>True on the step a witch drank, for the host to play.</summary>
    public bool Drank { get; set; }

    /// <summary>Seconds of courtship left after being fed. Two courting adults of a kind pair.</summary>
    public float LovedFor { get; set; }

    /// <summary>Seconds until it may court again.</summary>
    public float BreedRest { get; set; }

    /// <summary>True once its fleece has been taken and until it has grown back.</summary>
    public bool Shorn { get; set; }

    /// <summary>Seconds until a shorn one is worth shearing again.</summary>
    public float Regrows { get; set; }

    /// <summary>Seconds until it leaves something behind without being touched.</summary>
    public float Sheds { get; set; }

    /// <summary>Set for the one step on which it did. Read and cleared by whoever spawns drops.</summary>
    public bool Shed { get; set; }

    /// <summary>True when this one owes itself a teleport step at the next chance.</summary>
    /// <remarks>
    /// ⚠ A flag rather than the act, because the act needs the world and <c>Hurt</c> does not
    /// have it — the blow books the step and the next update, which holds <c>solid</c>, takes it.
    /// </remarks>
    public bool BlinkPending { get; set; }

    /// <summary>Seconds until a blinking kind may step again. Stops a crowded one strobing.</summary>
    public float BlinkRest { get; set; }

    /// <summary>Seconds this one's fuse has been burning. Zero for anything unlit.</summary>
    public float FuseFor { get; set; }

    /// <summary>How far through its fuse it is, 0 unlit to 1 at the blast. What the swell draws.</summary>
    public float FuseFraction => Math.Clamp(FuseFor / CreatureHerd.FuseSeconds, 0f, 1f);

    /// <summary>Set for the one step on which the fuse caught. Read by whoever plays the hiss.</summary>
    public bool FuseLit { get; set; }

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
        var half = MathF.Max(MathF.Max(Size.X, Size.Z), 0.3f) * 0.5f * Scale;
        return (
            new Vector3(Position.X - half, Position.Y, Position.Z - half),
            new Vector3(Position.X + half, Position.Y + MathF.Max(Size.Y, 0.3f) * Scale, Position.Z + half));
    }

    /// <summary>Where a blow lands and a sound comes from — its middle, not its feet.</summary>
    public Vector3 Middle => Position + new Vector3(0f, MathF.Max(Size.Y, 0.3f) * Scale * 0.5f, 0f);
}

/// <summary>One animal that has just died, for whoever turns that into drops and a noise.</summary>
/// <param name="Grown">False for a young one, which leaves nothing — killing calves must not pay.</param>
public readonly record struct CreatureDeath(
    string RuntimeId, string Kind, Vector3 Position, bool Shorn, bool Grown, bool Hostile, int Bulk = 1);

/// <summary>One animal that has just been born, for whoever plays the moment.</summary>
public readonly record struct CreatureBirth(string Kind, Vector3 Position);

/// <summary>One fuse that has burned down, for whoever carves the world and hurts the standing.</summary>
public readonly record struct CreatureBlast(string Kind, Vector3 Position);

/// <summary>One teleport step taken, both ends of it, for whoever draws the departure.</summary>
public readonly record struct CreatureBlink(string Kind, Vector3 From, Vector3 To);

/// <summary>One arrow a creature loosed, for whoever owns the flight pool to put in the air.</summary>
/// <param name="Flask">True for a thrown flask, whose brew the host picks — it knows what ails the target.</param>
/// <param name="Ember">A cinderwisp's small fireball: straight, quick, and it sets alight.</param>
/// <param name="Wail">An ashwail's bolt: slow, and it bursts where it lands — unless it is struck back.</param>
public readonly record struct CreatureShot(
    string Kind, Vector3 From, Vector3 Direction, int HalfHearts, ShotTag Tag = ShotTag.None, bool Flask = false,
    bool Ember = false, bool Wail = false);

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
    /// <summary>Numbered variants of one recording: <c>stem1, stem2, …</c>.</summary>
    private static string[] Run(string stem, int count)
    {
        var names = new string[count];
        for (var i = 0; i < count; i++) names[i] = $"{stem}{i + 1}";
        return names;
    }

    private static readonly string[] None = [];

    /// <summary>
    /// Each kind's ordinary voice — several recordings where the pack has them, so a field of one
    /// animal is not one recording on a loop.
    /// </summary>
    /// <remarks>
    /// The farm animals speak with the pack's sets now; the frog and the hostiles keep our own
    /// recordings, because the pack has no frog, no spider, no zombie and no bat. ⚠ A spider's is
    /// its walk rather than its bite: what a player needs to hear in the dark is that one is
    /// nearby, and the bite arrives with the damage anyway.
    /// </remarks>
    private static readonly Dictionary<string, string[]> Voices = new(StringComparer.Ordinal)
    {
        ["cow"] = Run("mob/cow/say", 4),
        ["pig"] = Run("mob/pig/say", 3),
        ["sheep"] = Run("mob/sheep/say", 3),
        ["chicken"] = Run("mob/chicken/say", 3),
        ["frog"] = ["animals/frog"],

        // A wolf at ease pants; everything sharper it saves for being crossed.
        ["wolf"] = ["mob/wolf/classic/panting"],

        ["bat"] = ["enemies/bat"],
        ["spider"] = ["enemies/spider"],
        ["zombie"] = ["enemies/zombie"],

        // ⛳ Ours by name and theirs by skeleton, so the sound follows the same rule as the art:
        // the drowned and the husk are zombies as far as a recording is concerned.
        ["drowned"] = ["enemies/zombie"],
        ["husk"] = ["enemies/zombie"],
        ["zombie_villager"] = ["enemies/zombie"],
        ["cave_spider"] = ["enemies/spider"],

        // ⛳ The Emberdeep's, under the reference's own sound names, so a sound pack voices them.
        ["cinderwisp"] = Run("mob/blaze/breathe", 4),
        ["ashwail"] = Run("mob/ghast/moan", 7),
        ["ember_slime"] = Run("mob/magmacube/big", 4),
        ["charred_skeleton"] = Run("mob/wither_skeleton/idle", 3),
    };

    /// <summary>What one cries when a blow lands on it.</summary>
    private static readonly Dictionary<string, string[]> Hurt = new(StringComparer.Ordinal)
    {
        ["cow"] = Run("mob/cow/hurt", 3),
        ["chicken"] = Run("mob/chicken/hurt", 2),
        ["wolf"] = Run("mob/wolf/classic/hurt", 3),
        ["cinderwisp"] = Run("mob/blaze/hit", 4),
        ["ashwail"] = Run("mob/ghast/scream", 5),
        ["ember_slime"] = Run("mob/magmacube/small", 5),
        ["charred_skeleton"] = Run("mob/wither_skeleton/hurt", 4),
    };

    /// <summary>What one sounds like going for somebody.</summary>
    private static readonly Dictionary<string, string[]> Angry = new(StringComparer.Ordinal)
    {
        ["spider"] = ["enemies/spider_attack"],
        ["cave_spider"] = ["enemies/spider_attack"],
        ["wolf"] = Run("mob/wolf/classic/growl", 3),
    };

    /// <summary>A real death recording, for the kinds that have one.</summary>
    private static readonly Dictionary<string, string[]> DeathCries = new(StringComparer.Ordinal)
    {
        ["pig"] = ["mob/pig/death"],
        ["wolf"] = ["mob/wolf/classic/death"],
        ["cinderwisp"] = ["mob/blaze/death"],
        ["ashwail"] = ["mob/ghast/death"],
        ["charred_skeleton"] = Run("mob/wither_skeleton/death", 2),
    };

    /// <summary>A fireball leaving its maker — the reference uses one recording for both kinds.</summary>
    public static readonly string[] Fireballs = ["mob/ghast/fireball4"];

    /// <summary>The ashwail drawing breath before it fires.</summary>
    public static readonly string[] WailCharge = ["mob/ghast/charge"];

    /// <summary>What a laying hen leaves behind, sound-wise.</summary>
    private static readonly Dictionary<string, string[]> Shed = new(StringComparer.Ordinal)
    {
        ["chicken"] = ["mob/chicken/plop"],
    };

    /// <summary>What a blow sounds like landing — the impact itself, one set for every species.</summary>
    /// <remarks>
    /// ⛳ <b>Not per species, and that is a decision rather than a shortcut.</b> A blow is the sound
    /// of the blow; what tells a cow from a chicken is the voice that cries out with it.
    /// </remarks>
    public static readonly string[] Blows = Run("damage/hit", 3);

    /// <summary>The hiss of a fuse catching, and the blast at the end of it.</summary>
    /// <remarks>
    /// ⚠ Named here rather than at the call site, because <see cref="All"/> is what the audio
    /// check walks — the Meals rule. The pack ships both; the crawler is why they are wired.
    /// </remarks>
    public static readonly string[] Fuses = ["random/fuse"];

    public static readonly string[] Explosions = Run("random/explode", 4);

    /// <summary>The soft pop of a blink — the pack's own pearl-throw, repurposed.</summary>
    public static readonly string[] Blinks = ["item/throwables/throw_enderpearl"];

    /// <summary>A creature's arrow finding the player, and finding anything else.</summary>
    public static readonly string[] ArrowHits = ["enemies/arrow_hit"];
    public static readonly string[] ArrowMisses = ["enemies/arrow_miss"];

    /// <summary>What the last blow sounds like.</summary>
    public static readonly string[] Deaths = Run("damage/gore/bleed", 3);

    /// <summary>What taking a fleece off sounds like — a soft tearing, off the vine set.</summary>
    public static readonly string[] Shears = Run("block/vine/tear", 5);

    /// <summary>
    /// And what eating sounds like.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Here rather than in a table of its own, because <see cref="All"/> is what the audio check
    /// walks.</b> A clip named anywhere else is a clip nobody proves is on disk, and a table pointing
    /// at a missing sound is silent in exactly the way a working game is silent. This class stopped
    /// being only voices the moment it gained the impacts: it is the sounds around a creature.
    /// </remarks>
    public static readonly string[] Meals = Run("entity/player/eating/chew", 7);

    /// <summary>The clips a creature idles with, or empty for a silent kind.</summary>
    public static string[] VoicesFor(string kind) => Voices.GetValueOrDefault(kind, None);

    /// <summary>Its cry when struck, falling back to its ordinary voice.</summary>
    public static string[] HurtFor(string kind) =>
        Hurt.TryGetValue(kind, out var clips) ? clips : VoicesFor(kind);

    /// <summary>Its sound going for somebody, falling back to its ordinary voice.</summary>
    public static string[] AngryFor(string kind) =>
        Angry.TryGetValue(kind, out var clips) ? clips : VoicesFor(kind);

    /// <summary>A real death recording, or empty — the caller falls back to the voice, lowered.</summary>
    public static string[] DeathCryFor(string kind) => DeathCries.GetValueOrDefault(kind, None);

    /// <summary>The sound of leaving something behind, or empty for most kinds.</summary>
    public static string[] ShedFor(string kind) => Shed.GetValueOrDefault(kind, None);

    /// <summary>Every clip named here, for the check that they all resolve.</summary>
    public static IEnumerable<string> All =>
        Voices.Values
            .Concat(Hurt.Values)
            .Concat(Angry.Values)
            .Concat(DeathCries.Values)
            .Concat(Shed.Values)
            .SelectMany(clips => clips)
            .Concat(Blows)
            .Concat(Deaths)
            .Concat(Shears)
            .Concat(Meals)
            .Concat(Fuses)
            .Concat(Explosions)
            .Concat(Blinks)
            .Concat(ArrowHits)
            .Concat(ArrowMisses)
            .Concat(Fireballs)
            .Concat(WailCharge);
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

    /// <summary>Blocks a second a hostile closes at. Slower than a player, faster than a walk.</summary>
    /// <remarks>
    /// ⚠ <b>Below the player's 4.3 and above their sneak.</b> Something that could outrun you is
    /// something you can only fight, and the whole texture of a night in this genre is that running
    /// is a real option — so a hostile catches anyone who dithers and nobody who leaves.
    /// </remarks>
    public const float HuntSpeed = 3.0f;

    /// <summary>How far off one notices somebody, in blocks.</summary>
    public const float SightRange = 16f;

    /// <summary>
    /// Path searches allowed in one frame, across the whole herd.
    /// </summary>
    /// <remarks>
    /// ⚠ A budget, because a night of seventy can all want a path on the same frame — the one the
    /// player stepped round a corner. The rest keep the walk they have and ask again next frame; at
    /// sixty frames a second nobody waits long.
    /// </remarks>
    public const int PathsPerFrame = 4;

    /// <summary>Seconds a hunter keeps after a target it can no longer see. The reference's three.</summary>
    public const float UnseenMemory = 3f;

    /// <summary>A witch throws inside this, every so often. The reference's ten blocks and three seconds.</summary>
    public const float WitchRange = 10f;
    public const float WitchEvery = 3f;

    /// <summary>A phantom's circle: how wide, how high over the player, how fast round and in the dive.</summary>
    public const float PhantomRadius = 10f;
    public const float PhantomHeight = 14f;
    public const float PhantomSpeed = 6f;
    public const float PhantomDive = 11f;

    /// <summary>An archer stands off and shoots inside this, in blocks. The reference's fifteen.</summary>
    public const float ArcherRange = 15f;

    /// <summary>Seconds between arrows, and the draw before the first. The reference's two and one.</summary>
    public const float ArcherEvery = 2f;
    public const float ArcherDraw = 1f;

    /// <summary>How far an arrow is thrown off its line, per axis, as a fraction of its direction.</summary>
    public const float ArcherSpread = 0.045f;

    /// <summary>The furthest a wanderer will walk off an edge. The reference's three blocks.</summary>
    public const int WanderDrop = 3;

    /// <summary>
    /// Half the width a body takes against the terrain.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Capped under half a block, whatever the creature measures.</b> A spider is wider than
    /// a block; given its whole width against the walls it can never enter the one-wide tunnels that
    /// most caves are made of, and every path through one is a path it cannot walk. The hit box a
    /// blow lands in is still the whole creature — this is only what scrapes on the rock.
    /// </remarks>
    public const float BodyHalf = 0.3f;

    /// <summary>How fast a climber goes up a wall, in blocks a second.</summary>
    public const float ClimbSpeed = 2.4f;

    /// <summary>And how near it has to be to land a blow.</summary>
    /// <remarks>
    /// ⚠ Measured between middles rather than between surfaces, so it has to allow for half a
    /// creature and half a player. Two and a quarter is about a pace closer than the player's own
    /// reach, which is what makes backing away a defence.
    /// </remarks>
    public const float StrikeRange = 2.25f;

    /// <summary>Seconds between blows.</summary>
    public const float StrikeEvery = 1.1f;

    /// <summary>Blocks within which a struck retaliator's own kind takes it personally.</summary>
    public const float PackAggroRange = 12f;

    /// <summary>Seconds of full daylight before one that burns starts taking damage.</summary>
    public const float ScorchSeconds = 1.5f;

    /// <summary>Half-hearts a second the sun costs one that burns in it.</summary>
    public const float ScorchRate = 1.6f;

    /// <summary>Blocks within which a timid kind bolts from an approach nothing has swung in.</summary>
    public const float ShyRange = 6f;

    /// <summary>Blocks a second a flier crosses. Quicker than a walk, jittery by rethink.</summary>
    public const float FlySpeed = 2.4f;

    /// <summary>Blocks a second a flier climbs or sinks at, when it does.</summary>
    public const float FlyLift = 0.9f;

    /// <summary>Blocks a second a swimmer drifts at. Slow is what reads as underwater.</summary>
    public const float SwimSpeed = 1.2f;

    /// <summary>The two ends of a blink's reach, in blocks. Far enough to break a corner.</summary>
    public const float BlinkNear = 4f;

    public const float BlinkFar = 8f;

    /// <summary>Blocks within which an unprovoked blinker feels crowded and steps away.</summary>
    public const float BlinkCrowdRange = 3.5f;

    /// <summary>Seconds between steps. A cornered one is hard to pin, not impossible.</summary>
    public const float BlinkRestSeconds = 2.5f;

    /// <summary>Seconds a fuse burns before the blast.</summary>
    /// <remarks>
    /// A second and a half is the whole fight: long enough to land one swing or to get clear if
    /// you leave NOW, short enough that dithering is fatal. The hiss is the tell.
    /// </remarks>
    public const float FuseSeconds = 1.5f;

    /// <summary>Blocks within which an exploder stops walking and lights itself.</summary>
    public const float FuseRange = 3f;

    /// <summary>Blocks past which a burning fuse goes out. Running works; backing up does not.</summary>
    public const float FuseAbortRange = 7f;

    /// <summary>Blocks a second straight up, the moment a hopping kind launches.</summary>
    /// <remarks>
    /// Eight against a gravity of 26 is a hop of about one and a quarter blocks — enough to clear a
    /// step and land on a ledge, short of clearing a two-block wall, which keeps walls an answer.
    /// </remarks>
    public const float HopLaunch = 8f;

    /// <summary>Blocks a second a hopper covers while it is in the air. The hop IS its stride.</summary>
    public const float HopStride = 3.2f;

    /// <summary>Seconds a grazing hopper sits between hops. Hunting, it barely pauses.</summary>
    public const float HopRestSeconds = 0.9f;

    public const float HopRestHunting = 0.25f;

    /// <summary>A spider's leap: up at this, carried forward at the hop's own stride.</summary>
    public const float LeapLaunch = 7f;

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
    private readonly List<CreatureAttack> _attacks = [];
    private readonly List<CreatureBirth> _births = [];
    private readonly List<CreatureBlast> _blasts = [];
    private readonly List<CreatureBlink> _blinks = [];

    private readonly List<CreatureShot> _shots = [];
    private readonly List<Creature> _newborn = [];
    private readonly Random _random;

    /// <summary>The one searcher every hunter shares. Searches are short and never overlap.</summary>
    private readonly CreaturePaths _paths = new();

    /// <summary>Searches left this frame. See <see cref="PathsPerFrame"/>.</summary>
    private int _pathBudget;

    public CreatureHerd(int seed) => _random = new Random(seed);

    public IReadOnlyList<Creature> All => _creatures;

    public int Count => _creatures.Count;

    /// <summary>Places one authored actor exactly, bypassing the random ground-search spawner.</summary>
    public Creature SpawnAt(SpawnKind kind, Vector3 position, float yaw = 0f, int bulk = 1)
    {
        var creature = new Creature
        {
            Kind = kind.Name,
            Size = kind.Size,
            Hostile = kind.Hostile,
            Move = kind.Move,
            Bulk = Math.Max(1, bulk),
            MaxHealth = CreatureVitals.HealthFor(kind.Name, bulk),
            Health = CreatureVitals.HealthFor(kind.Name, bulk),
            Position = position,
            Yaw = yaw,
            WantsYaw = yaw,
            Thinks = 0.5f,
            Speaks = SpeaksEvery * 0.5f,
            Sheds = CreatureVitals.ShedsEvery,
        };
        _creatures.Add(creature);
        return creature;
    }

    /// <summary>Puts creatures on the ground near a point, and says how many found room.</summary>
    /// <param name="solid">Whether the block at a cell would hold something up.</param>
    /// <param name="kinds">What to place, taken in turn.</param>
    /// <remarks>
    /// ⚠ <b>Spirals out rather than trying the same ring twice.</b> A spawn that gives up when its
    /// first guess is inside a hill puts nothing in the world and looks exactly like a renderer that
    /// draws nothing, which is a day of looking in the wrong place.
    /// </remarks>
    /// <param name="where">
    /// An extra test the cell a creature would stand in has to pass, or null for anywhere with
    /// ground. ⛳ This is how a hostile is placed in the dark: the caller decides what dark means and
    /// the herd never learns what light is, which is the same posture that keeps the whole of this
    /// file runnable under <c>--audit</c> with no world at all.
    /// </param>
    public int Spawn(
        Func<int, int, int, bool> solid, IReadOnlyList<SpawnKind> kinds, Vector3 near, int count,
        Func<int, int, int, bool>? where = null, float minRadius = 4f,
        Func<int, int, int, bool>? spawnSupport = null)
    {
        if (kinds.Count == 0) return 0;

        // Collision and legal ground are deliberately different questions. Leaves and trunks stop
        // a body, but their top faces are never a place a new creature should enter the world.
        // Keeping the second predicate optional preserves the small pure-simulation callers while
        // the live world can reject tree material without pretending it is non-solid headroom.
        spawnSupport ??= solid;

        var placed = 0;

        for (var attempt = 0; attempt < count * 40 && placed < count; attempt++)
        {
            // Out in a widening ring, so a crowded spawn spreads rather than stacking.
            var angle = (float)(_random.NextDouble() * Math.Tau);
            var radius = minRadius + attempt * 0.6f;

            var x = (int)MathF.Floor(near.X + MathF.Cos(angle) * radius);
            var z = (int)MathF.Floor(near.Z + MathF.Sin(angle) * radius);

            if (!TryGround(solid, spawnSupport, x, z, (int)near.Y + 24, out var y)) continue;
            if (where is not null && !where(x, (int)y, z)) continue;

            var kind = kinds[placed % kinds.Count];

            _creatures.Add(new Creature
            {
                Kind = kind.Name,
                Size = kind.Size,
                Hostile = kind.Hostile,
                Move = kind.Move,
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
        => TryGround(solid, solid, x, z, from, out y);

    /// <summary>
    /// The first allowed support surface with real collision-free headroom. A support predicate is
    /// separate from collision so tree blocks can remain solid without becoming spawn platforms.
    /// </summary>
    public static bool TryGround(
        Func<int, int, int, bool> solid,
        Func<int, int, int, bool> spawnSupport,
        int x,
        int z,
        int from,
        out float y)
    {
        // ⛔ Down to the world's floor rather than to y 1, which was the floor before the world grew
        // a deep. Bounded too, so a column of open air under a ledge is not a 384-cell walk.
        var lowest = Math.Max(Gen.TerrainGenerator.WorldBottom, from - 160);
        for (var at = from; at > lowest; at--)
        {
            if (!solid(x, at, z)) continue;

            // Keep looking below a forbidden surface. Its real solidity still participates in the
            // headroom check when a lower candidate is considered, so this cannot place a body
            // inside the trunk or under a canopy that is too low for it.
            if (!spawnSupport(x, at, z)) continue;

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
            _dead.Add(new CreatureDeath(
                creature.RuntimeId, creature.Kind, creature.Middle, creature.Shorn,
                creature.Adult, creature.Hostile, creature.Bulk));
            return true;
        }

        // ⛳ THE THIRD ANSWER TO A BLOW. A beast runs and a hostile was already coming; a
        // retaliating kind turns on whoever struck it — and so does every packmate of its kind
        // near enough to have seen. The anger is permanent on purpose: a wolf that forgot being
        // struck after five seconds would be a beast with an animation, not a risk.
        if (CreatureVitals.Retaliates(creature.Kind))
        {
            creature.Provoked = true;

            // A struck blinker is somewhere else before the second swing — the blow books the
            // step here and the next update, which holds the world, takes it.
            if (CreatureVitals.Blinks(creature.Kind)) creature.BlinkPending = true;

            foreach (var packmate in _creatures)
            {
                if (packmate.Kind != creature.Kind || !packmate.Alive) continue;
                if (Vector3.DistanceSquared(packmate.Position, creature.Position) > PackAggroRange * PackAggroRange)
                    continue;
                packmate.Provoked = true;
            }

            var toward = from - creature.Position;
            toward.Y = 0f;
            if (toward.LengthSquared() > 1e-6f)
                creature.Yaw = float.RadiansToDegrees(MathF.Atan2(toward.Z, toward.X));

            creature.WantsYaw = creature.Yaw;
            creature.Moving = true;
            creature.Thinks = 0.5f;

            return false;
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

    /// <summary>Births since last asked, cleared by the asking — the deaths' own shape.</summary>
    public List<CreatureBirth> TakeBirths()
    {
        var births = new List<CreatureBirth>(_births);
        _births.Clear();
        return births;
    }

    /// <summary>What feeding an animal did, so the client can spend the food or keep it.</summary>
    public enum FeedResult
    {
        /// <summary>Not its food, or not the moment — nothing was taken.</summary>
        Refused,

        /// <summary>An adult took it and is courting.</summary>
        Courting,

        /// <summary>A young one took it and grew a little.</summary>
        Grew,
    }

    /// <summary>
    /// Offers an animal something from the hand.
    /// </summary>
    /// <remarks>
    /// ⛳ The pairings are <see cref="Breeding"/>'s and the moods are here, because whether THIS
    /// animal is resting or already courting is herd state no table can know. A refusal takes
    /// nothing — the caller keeps the food and decides what to say.
    /// </remarks>
    public FeedResult Feed(Creature creature, string item)
    {
        if (!creature.Alive || !Breeding.Takes(creature.Kind, item)) return FeedResult.Refused;

        // A meal moves a young one along — the field hurrying the herd, bone meal's own shape.
        if (!creature.Adult)
        {
            creature.Grown = MathF.Min(1f, creature.Grown + Breeding.FeedGrowth);
            return FeedResult.Grew;
        }

        if (creature.BreedRest > 0f || creature.LovedFor > 0f) return FeedResult.Refused;

        creature.LovedFor = Breeding.CourtSeconds;
        return FeedResult.Courting;
    }

    /// <summary>
    /// Walks a courting animal toward the nearest courting adult of its kind, and pays the calf.
    /// </summary>
    /// <returns>True while courtship owns the animal's legs.</returns>
    /// <remarks>
    /// ⚠ <b>The calf goes into <see cref="_newborn"/>, never straight into the list</b> — this runs
    /// inside the update's own walk over <see cref="_creatures"/>, and a list grown mid-walk is the
    /// classic way a herd loses its whole update to one happy event.
    /// </remarks>
    private bool Court(Creature creature, float dt)
    {
        if (creature.LovedFor <= 0f) return false;

        creature.LovedFor -= dt;
        if (creature.LovedFor <= 0f) return false;   // the mood passed unanswered

        Creature? mate = null;
        var nearest = float.MaxValue;

        foreach (var other in _creatures)
        {
            if (ReferenceEquals(other, creature) || !other.Alive) continue;
            if (other.Kind != creature.Kind || other.LovedFor <= 0f || !other.Adult) continue;

            var apart = Vector3.DistanceSquared(other.Position, creature.Position);
            if (apart < nearest)
            {
                nearest = apart;
                mate = other;
            }
        }

        if (mate is null || nearest > Breeding.PairRange * Breeding.PairRange) return false;

        var toward = mate.Position - creature.Position;
        toward.Y = 0f;

        if (toward.Length() > Breeding.MeetRange)
        {
            creature.WantsYaw = float.RadiansToDegrees(MathF.Atan2(toward.Z, toward.X));
            creature.Moving = true;
            creature.Thinks = 0.5f;   // held short, the hunt's own trick, so wandering cannot interrupt
            return true;
        }

        // Met. One calf between them, both spent, both resting.
        creature.LovedFor = 0f;
        mate.LovedFor = 0f;
        creature.BreedRest = Breeding.RestSeconds;
        mate.BreedRest = Breeding.RestSeconds;

        var between = (creature.Position + mate.Position) * 0.5f;

        _newborn.Add(new Creature
        {
            Kind = creature.Kind,
            Size = creature.Size,
            Hostile = false,
            Move = creature.Move,
            MaxHealth = CreatureVitals.HealthFor(creature.Kind),
            Health = CreatureVitals.HealthFor(creature.Kind),
            Position = between,
            Grown = 0f,
            Yaw = (float)(_random.NextDouble() * 360.0),
            WantsYaw = (float)(_random.NextDouble() * 360.0),
            Thinks = 1f,
            Speaks = 2f,
            Sheds = CreatureVitals.ShedsEvery * (0.4f + (float)_random.NextDouble()),
        });
        _births.Add(new CreatureBirth(creature.Kind, between));

        return true;
    }

    /// <summary>Takes one out of the world without it having died — what a despawn is.</summary>
    public void Remove(Creature creature) => _creatures.Remove(creature);

    /// <summary>
    /// Lets the far ones go, by the reference's rule, and says how many went.
    /// </summary>
    /// <param name="transient">Which creatures may be let go at all. Animals and residents may not.</param>
    /// <remarks>
    /// <para>⛔ <b>This did not exist, and its absence was most of the fault.</b> Nothing hostile was
    /// ever taken out of the world, so the cap was spent once — on whatever appeared first, wherever
    /// that was — and the dark stopped spawning for the rest of the session.</para>
    /// <para>Beyond 128 blocks a monster goes at once. Within 32 it never goes, and its idle clock is
    /// reset. Between, once it has been alone for thirty seconds, it has a one-in-800 chance a tick,
    /// which averages forty seconds more — long enough that walking away from a fight and back finds
    /// it still there, short enough that the slots follow the player.</para>
    /// </remarks>
    public int Despawn(Vector3 player, float dt, Func<Creature, bool> transient)
    {
        var gone = 0;
        var far = SpawnRules.SpawnRadius * SpawnRules.SpawnRadius;
        var keep = SpawnRules.KeepRadius * SpawnRules.KeepRadius;

        for (var i = _creatures.Count - 1; i >= 0; i--)
        {
            var creature = _creatures[i];
            if (!creature.Alive || !transient(creature)) continue;

            var distance = Vector3.DistanceSquared(creature.Position, player);

            if (distance > far)
            {
                _creatures.RemoveAt(i);
                gone++;
                continue;
            }

            if (distance < keep)
            {
                creature.Idle = 0f;
                continue;
            }

            creature.Idle += dt;
            if (creature.Idle <= SpawnRules.IdleSeconds) continue;
            if (_random.NextDouble() >= SpawnRules.DespawnPerSecond * dt) continue;

            _creatures.RemoveAt(i);
            gone++;
        }

        return gone;
    }

    /// <summary>One creature as the save carries it — the identity, never the mood.</summary>
    /// <remarks>
    /// ⛳ <b>What persists is what a player would miss.</b> Where it stands, what it is, how hurt
    /// it is, whether its fleece is off, whether it has been crossed, and how grown it is. The
    /// transient mind — where it was walking, when it would next speak, how frightened it was —
    /// re-rolls on load exactly as it rolled at spawn, because an animal that wakes up mid-thought
    /// is indistinguishable from one that just had a new one.
    /// </remarks>
    public readonly record struct SavedCreature(
        string Kind, Vector3 Position, float Yaw, int Health, bool Shorn, float Regrows,
        bool Provoked, float Grown, int Bulk = 1);

    /// <summary>Everything alive, as the save wants it.</summary>
    public List<SavedCreature> Capture()
    {
        var saved = new List<SavedCreature>(_creatures.Count);

        foreach (var creature in _creatures)
        {
            if (!creature.Alive) continue;

            saved.Add(new SavedCreature(
                creature.Kind, creature.Position, creature.Yaw, creature.Health,
                creature.Shorn, creature.Regrows, creature.Provoked, creature.Grown, creature.Bulk));
        }

        return saved;
    }

    /// <summary>
    /// Puts back what a save carried, and says how many of them this build no longer knows.
    /// </summary>
    /// <param name="resolve">
    /// Size and temperament for a kind, or null for one this build cannot stand up — the caller
    /// owns the meshes, and a size table written here would be a second copy of the skeleton.
    /// </param>
    /// <remarks>
    /// ⚠ Unknown kinds are skipped and counted, never fatal — the same posture the block palette
    /// takes with a save from a newer build. Health is clamped into the kind's present range so a
    /// retuned MaxHealth cannot load an animal healthier than its own bar.
    /// </remarks>
    public int Restore(IReadOnlyList<SavedCreature> saved, Func<string, SpawnKind?> resolve, out int unknown)
    {
        unknown = 0;
        var placed = 0;

        foreach (var one in saved)
        {
            if (resolve(one.Kind) is not { } kind)
            {
                unknown++;
                continue;
            }

            var bulk = Math.Clamp(one.Bulk, 1, 4);
            var most = CreatureVitals.HealthFor(one.Kind, bulk);

            _creatures.Add(new Creature
            {
                Kind = one.Kind,
                Size = kind.Size,
                Hostile = kind.Hostile,
                Move = kind.Move,
                MaxHealth = most,
                Health = Math.Clamp(one.Health, 1, most),
                Position = one.Position,
                Yaw = one.Yaw,
                WantsYaw = one.Yaw,
                Shorn = one.Shorn,
                Regrows = one.Regrows,
                Provoked = one.Provoked,
                Grown = Math.Clamp(one.Grown, 0f, 1f),
                Bulk = bulk,
                Thinks = (float)(_random.NextDouble() * 4.0),
                Speaks = SpeaksEvery * (float)_random.NextDouble(),
                Sheds = CreatureVitals.ShedsEvery * (0.4f + (float)_random.NextDouble()),
            });

            placed++;
        }

        return placed;
    }

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
    /// <param name="player">Where the player's feet are, or null when nobody is in the world.</param>
    /// <param name="sunlit">
    /// Whether a cell is standing in full daylight. Null means nothing burns — which is what the
    /// headless checks pass, and what a world at night amounts to.
    /// </param>
    /// <param name="known">
    /// Whether a cell's chunk actually exists yet, or null when everything does. ⛔ A restored
    /// creature can stand a long way from wherever the player loads in, and unloaded space reads
    /// as air — stepped there, it falls through the floor its chunk will contain. Un-known
    /// creatures are frozen whole: no clocks, no gravity, no thought, until the world arrives.
    /// </param>
    /// <param name="water">
    /// Whether a cell is water, or null when nothing is — which beaches every swimmer, honestly.
    /// The herd never learns what a fluid is, the same posture the dark and the daylight take.
    /// </param>
    /// <param name="scorch">
    /// Called while a daylight-sensitive creature is past its grace period. The owner routes this
    /// into the same burning effect used by Ignite; the herd never keeps a second damage clock.
    /// </param>
    public void Update(
        float dt, Func<int, int, int, bool> solid,
        Vector3? player = null, Func<int, int, int, bool>? sunlit = null,
        Func<int, int, int, bool>? known = null, Func<int, int, int, bool>? water = null,
        Action<Creature>? scorch = null)
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

        _pathBudget = PathsPerFrame;

        foreach (var creature in _creatures)
        {
            if (!creature.Alive) continue;

            if (known is not null && !known(
                    (int)MathF.Floor(creature.Position.X),
                    (int)MathF.Floor(creature.Position.Y),
                    (int)MathF.Floor(creature.Position.Z)))
                continue;

            creature.Spoke = false;
            creature.Shed = false;
            creature.FuseLit = false;

            creature.HurtFor = MathF.Max(0f, creature.HurtFor - dt);
            creature.FleeFor = MathF.Max(0f, creature.FleeFor - dt);
            var magicMovement = Math.Clamp(creature.MagicMovementScale, 0f, 1f);

            if (creature.MagicallyFeared && player is { } fearedFrom)
            {
                var away = creature.Position - fearedFrom;
                away.Y = 0f;
                if (away.LengthSquared() > 1e-6f)
                {
                    creature.WantsYaw = float.RadiansToDegrees(MathF.Atan2(away.Z, away.X));
                    creature.Moving = true;
                }
                creature.FleeFor = MathF.Max(creature.FleeFor, dt + 0.1f);
            }

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

            // A young one grows on its own clock; a meal only hurries it.
            if (creature.Grown < 1f)
                creature.Grown = MathF.Min(1f, creature.Grown + dt / Breeding.GrowSeconds);

            creature.BreedRest = MathF.Max(0f, creature.BreedRest - dt);

            // A flier owes gravity nothing, and a swimmer owes it nothing while the water holds
            // it — beached, it falls like anything else and then can only lie there.
            // ⛳ The phantom flies its own way: a wide circle high over the player and a dive.
            if (creature.Kind == "phantom")
            {
                Scorch(creature, dt, sunlit, scorch);
                if (magicMovement > 0f) Swoop(creature, dt * magicMovement, player, solid);
                continue;
            }

            // ⛳ The Emberdeep's two fliers keep their own air: the cinderwisp hangs over you and
            // looses volleys, the ashwail drifts the hollows and draws breath for a bolt.
            if (creature.Kind == "cinderwisp")
            {
                if (magicMovement > 0f) Kindle(creature, dt * magicMovement, player, solid);
                continue;
            }

            if (creature.Kind == "ashwail")
            {
                if (magicMovement > 0f) Wail(creature, dt * magicMovement, player, solid);
                continue;
            }

            var swimming = creature.Move == CreatureMove.Swim
                && water is not null
                && water(
                    (int)MathF.Floor(creature.Position.X),
                    (int)MathF.Floor(creature.Position.Y),
                    (int)MathF.Floor(creature.Position.Z));

            if (creature.Move != CreatureMove.Fly && !swimming && Fall(creature, dt, solid))
            {
                // A hopper travels while it is in the air — the hop IS the stride. Straight along
                // the yaw it launched with, no steering: it aimed on the ground.
                if (creature.Move == CreatureMove.Hop || creature.Leaping)
                    HopDrift(creature, dt, solid, creature.Targeting ? player : null);
                continue;
            }

            // The teleport step, taken here because this is where the world is in hand. A struck
            // one owes itself the step; an unprovoked one that is being crowded books its own.
            if (CreatureVitals.Blinks(creature.Kind))
            {
                creature.BlinkRest = MathF.Max(0f, creature.BlinkRest - dt);

                if (!creature.BlinkPending && !creature.Provoked && creature.BlinkRest <= 0f
                    && player is { } near
                    && Vector3.DistanceSquared(near, creature.Position) < BlinkCrowdRange * BlinkCrowdRange)
                {
                    creature.BlinkPending = true;
                }

                if (creature.BlinkPending)
                {
                    Blink(creature, solid);
                    creature.BlinkPending = false;
                }
            }

            // A timid kind bolts from an approach nothing has swung in — the fox's whole
            // character. Renewed while the walker keeps closing, so it stays ahead of them.
            if (creature.FleeFor <= 0f && player is { } stroller
                && CreatureVitals.Timid(creature.Kind)
                && Vector3.DistanceSquared(stroller, creature.Position) < ShyRange * ShyRange)
            {
                var off = creature.Position - stroller;
                off.Y = 0f;

                if (off.LengthSquared() > 1e-6f)
                {
                    creature.Yaw = float.RadiansToDegrees(MathF.Atan2(off.Z, off.X));
                    creature.WantsYaw = creature.Yaw;
                }

                creature.Moving = true;
                creature.FleeFor = 1.2f;
                creature.Thinks = 0.6f;
            }

            Scorch(creature, dt, sunlit, scorch);
            var hunting = !creature.MagicallyFeared && Hunt(creature, dt, player, solid);
            var courting = !hunting && Court(creature, dt);

            creature.Thinks -= dt;

            if (creature.Thinks <= 0f)
            {
                creature.Thinks = 2f + (float)_random.NextDouble() * 5f;

                // ⚠ A frightened animal keeps running and keeps its heading, and so does one that
                // has seen you. Re-deciding on the ordinary clock is what turned a bolting cow back
                // toward whoever hit it every few seconds, which reads as an animal that wants
                // another go rather than one fleeing. A courting one likewise owns its own legs.
                if (creature.FleeFor <= 0f && !hunting && !courting)
                {
                    creature.Moving = _random.NextDouble() < 0.6;
                    creature.WantsYaw = (float)(_random.NextDouble() * 360.0);
                    creature.Stroll = null;

                    // ⛳ The reference's stroll: a walker picks somewhere it can actually reach and
                    // walks the route there, rather than setting off on a bearing and turning when
                    // it meets a tree. Without a path to hand (none found, or this frame's budget
                    // spent) it falls back to the bearing, as before.
                    if (creature.Moving && creature.Move is CreatureMove.Walk && creature.FallSpeed == 0f
                        && _pathBudget > 0)
                        PlanStroll(creature, solid);

                    // A flier or swimmer also re-rolls which way is up. FallSpeed carries the
                    // vertical wander for the kinds gravity has let go of — down positive, the
                    // fall's own convention.
                    if (creature.Move is CreatureMove.Fly or CreatureMove.Swim)
                        creature.FallSpeed = ((float)_random.NextDouble() * 2f - 1f)
                            * (creature.Move == CreatureMove.Fly ? FlyLift : SwimSpeed * 0.6f);
                }
            }

            // A stroll in hand steers the walk; one walked to its end is a walk over.
            if (creature.Stroll is { } stroll)
            {
                if (hunting || courting || creature.FleeFor > 0f || creature.Stuck > 1f)
                {
                    creature.Stroll = null;
                }
                else
                {
                    var at = creature.StrollAt;
                    var going = FollowPath(creature, stroll, ref at);
                    creature.StrollAt = at;
                    if (!going)
                    {
                        creature.Stroll = null;
                        creature.Moving = false;
                    }
                }
            }

            // Turn the short way round, so a creature wanting to face 350 from 10 turns twenty
            // degrees rather than three hundred and forty.
            var panicking = creature.FleeFor > 0f || hunting;
            var difference = Wrap(creature.WantsYaw - creature.Yaw);
            var step = MathF.Min(
                MathF.Abs(difference), (panicking ? PanicTurnSpeed : TurnSpeed) * dt);
            creature.Yaw = Wrap(creature.Yaw + MathF.Sign(difference) * step);

            if (!creature.Moving) continue;

            if (magicMovement <= 0f) continue;

            if (creature.Move == CreatureMove.Fly)
            {
                Glide(creature, dt * magicMovement, solid, null);
                continue;
            }

            if (swimming)
            {
                Glide(creature, dt * magicMovement, solid, water);
                continue;
            }

            // A hopping kind never walks: grounded, it sits out its rest and then launches. The
            // travel itself happens in HopDrift while the body is airborne.
            if (creature.Move == CreatureMove.Hop)
            {
                creature.HopRest -= dt;
                if (creature.HopRest > 0f) continue;

                creature.HopRest = hunting
                    ? HopRestHunting
                    : HopRestSeconds * (0.7f + (float)_random.NextDouble() * 0.6f);
                creature.FallSpeed = -HopLaunch;
                continue;
            }

            var speed = hunting ? HuntSpeed : creature.FleeFor > 0f ? PanicSpeed : WalkSpeed;
            var before = creature.Position;

            // ⛔ A body against the rock, one axis at a time, a block's step up, and a fall when
            // there is nothing ahead — the old step refused any change of more than one block, so
            // nothing ever walked off a ledge and a two-block cliff was a wall. A wanderer still
            // declines a fall of more than three; a hunter takes
            // whatever its health will stand, which is the reference's own rule.
            var drop = hunting ? ChaseDrop(creature) : WanderDrop;
            if (Walk(creature, speed * magicMovement * dt, solid, drop))
            {
                creature.Clinging = false;

                // Going nowhere while trying to go somewhere: a path that is not working.
                var moved = Vector3.DistanceSquared(before, creature.Position);
                creature.Stuck = moved < (speed * dt * 0.2f) * (speed * dt * 0.2f) ? creature.Stuck + dt : 0f;
                continue;
            }

            // ⛳ A climber that meets a wall goes up it — the spider's whole difference, and what
            // makes a two-block wall round a base a thing it laughs at.
            if (CreatureVitals.Climbs(creature.Kind) && Climb(creature, dt, solid))
            {
                creature.Stuck = 0f;
                continue;
            }

            creature.Stuck += dt;

            // A hunter on a path owns its heading; the blocked step is the path's problem, and a
            // path that stays blocked is replaced once Stuck runs up.
            if (hunting) continue;

            // ⚠ A cornered animal turns a little rather than the same ninety every time. Frightened
            // it turns tightly and keeps running; grazing it may as well go anywhere.
            creature.WantsYaw = panicking
                ? Wrap(creature.Yaw + (_random.NextDouble() < 0.5 ? -1f : 1f) * (35f + (float)_random.NextDouble() * 55f))
                : Wrap(creature.Yaw + 90f + (float)_random.NextDouble() * 180f);

            creature.Thinks = panicking ? 0.4f : 1f + (float)_random.NextDouble() * 2f;
        }

        // The calves born mid-walk join the herd once the walk is over — see Court for why.
        if (_newborn.Count > 0)
        {
            _creatures.AddRange(_newborn);
            _newborn.Clear();
        }
    }

    /// <summary>
    /// One step of free movement: forward along the yaw and up or down the vertical wander, each
    /// axis refused independently where the world says no.
    /// </summary>
    /// <param name="water">
    /// Null for a flier, whose element is anywhere not solid; for a swimmer, the cells it may
    /// occupy — which is what keeps one inside its own pond without it knowing what a pond is.
    /// </param>
    /// <remarks>
    /// ⚠ <b>A refused axis turns the wander rather than stopping the animal</b> — a flier at a
    /// cave wall picks a new heading, one at the roof drifts back down, and neither ever presses
    /// into the rock the way a walker presses into a cliff.
    /// </remarks>
    private void Glide(Creature creature, float dt, Func<int, int, int, bool> solid, Func<int, int, int, bool>? water)
    {
        bool Open(int x, int y, int z) => water is null ? !solid(x, y, z) : water(x, y, z);

        var yaw = float.DegreesToRadians(creature.Yaw);
        var ahead = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        var speed = water is null ? FlySpeed : SwimSpeed;
        var wanted = creature.Position + ahead * (speed * dt);

        var y = (int)MathF.Floor(creature.Position.Y);

        if (Open((int)MathF.Floor(wanted.X), y, (int)MathF.Floor(wanted.Z))
            && Open((int)MathF.Floor(wanted.X), y + 1, (int)MathF.Floor(wanted.Z)))
        {
            creature.Position = new Vector3(wanted.X, creature.Position.Y, wanted.Z);
        }
        else
        {
            creature.WantsYaw = Wrap(creature.Yaw + 90f + (float)_random.NextDouble() * 180f);
            creature.Thinks = MathF.Min(creature.Thinks, 0.8f);
        }

        // The vertical wander, down positive by the fall's own convention. A flier keeps off the
        // floor and from under the roof; a swimmer additionally never leaves its element.
        var lift = -creature.FallSpeed;

        var fx = (int)MathF.Floor(creature.Position.X);
        var fz = (int)MathF.Floor(creature.Position.Z);

        if (lift <= 0f && !Open(fx, (int)MathF.Floor(creature.Position.Y - 0.3f), fz))
            lift = water is null ? FlyLift * 0.7f : SwimSpeed * 0.4f;

        if (lift > 0f && !Open(fx, (int)MathF.Floor(creature.Position.Y + creature.Size.Y * creature.Scale + 0.3f), fz))
            lift = water is null ? -FlyLift * 0.7f : -SwimSpeed * 0.4f;

        creature.FallSpeed = -lift;

        var toY = creature.Position.Y + lift * dt;
        if (Open(fx, (int)MathF.Floor(toY), fz))
            creature.Position = creature.Position with { Y = toY };
    }

    /// <summary>Carries an airborne hopper forward, unless a wall is in the way.</summary>
    /// <remarks>
    /// ⚠ The refusal leaves the vertical motion alone: blocked, it rises and falls where it is,
    /// which is what a thing bouncing against a wall looks like. The ground decision will turn it.
    /// </remarks>
    private static void HopDrift(Creature creature, float dt, Func<int, int, int, bool> solid, Vector3? quarry = null)
    {
        var yaw = float.DegreesToRadians(creature.Yaw);
        var ahead = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));

        // A bigger slime takes a longer hop — the reference's jump grows with the size class.
        var wanted = creature.Position + ahead * (HopStride * MathF.Sqrt(creature.Bulk) * dt);

        // ⚠ A hunter's hop ends at its own edge of its quarry rather than landing on top of them.
        if (quarry is { } at)
        {
            var girth = MathF.Max(MathF.Max(creature.Size.X, creature.Size.Z) * creature.Scale * 0.5f, BodyHalf);
            var flat = new Vector2(at.X - wanted.X, at.Z - wanted.Z).Length();
            if (flat < girth + 0.5f) return;
        }

        if (creature.Bulk > 1)
        {
            if (BodyFree(creature, wanted with { Y = creature.Position.Y + 0.05f }, solid)) creature.Position = wanted;
            return;
        }

        var x = (int)MathF.Floor(wanted.X);
        var y = (int)MathF.Floor(creature.Position.Y + 0.5f);
        var z = (int)MathF.Floor(wanted.Z);

        if (solid(x, y, z) || solid(x, y + 1, z)) return;

        creature.Position = new Vector3(wanted.X, creature.Position.Y, wanted.Z);
    }

    /// <summary>
    /// Points a hostile at whoever is in the world, and lets it swing when it gets there.
    /// </summary>
    /// <returns>True while it has somebody in sight, which is what makes it move at hunting pace.</returns>
    /// <remarks>
    /// A hunt begins with a line between the eyes inside the kind's follow range, measured in three
    /// dimensions, and ends three seconds after the line is lost; in between it walks a path over
    /// the block grid (see <see cref="Steer"/>). The look is taken twice a second rather than every
    /// frame — a ray per hostile per frame is the cost that kept this out before.
    /// </remarks>
    private bool Hunt(Creature creature, float dt, Vector3? player, Func<int, int, int, bool> solid)
    {
        creature.Swings = MathF.Max(0f, creature.Swings - dt);

        if ((!creature.Hostile && !creature.Provoked) || player is not { } target)
        {
            // Nobody to burn for. A fuse with no player in the world goes out rather than
            // carrying a blast into the next time somebody logs in beside it.
            if (creature.FuseFor > 0f) creature.FuseFor = 0f;
            LoseTarget(creature);
            return false;
        }

        var toward = target - creature.Position;
        var distance = toward.Length();
        toward.Y = 0f;
        var range = toward.Length();

        // ⛔ In three dimensions, and by sight. Measured flat, a zombie forty blocks down in another
        // cave was "within sixteen" of a player standing over it and walked about under them all
        // night; now there has to be a line between the eyes to begin, and a hunt that loses that
        // line for three seconds is over — the reference's own memory.
        if (distance > CreatureVitals.FollowRange(creature.Kind) || range < 1e-4f)
        {
            if (creature.FuseFor > 0f) creature.FuseFor = 0f;
            LoseTarget(creature);
            return false;
        }

        creature.SightCheck -= dt;
        if (creature.SightCheck <= 0f)
        {
            creature.SightCheck = 0.4f + (float)_random.NextDouble() * 0.2f;
            var eye = creature.Position + new Vector3(0f, MathF.Max(0.4f, creature.Size.Y * creature.Scale * 0.85f), 0f);
            creature.SeesTarget = LineOfSight(solid, eye, target + new Vector3(0f, 1.5f, 0f));
        }

        creature.Unseen = creature.SeesTarget ? 0f : creature.Unseen + dt;

        if (!creature.Targeting)
        {
            // ⚠ A struck one knows where the blow came from without seeing it.
            if (!creature.SeesTarget && !creature.Provoked) return false;
            creature.Targeting = true;
            creature.Repath = 0f;

            // The draw: an archer that has just seen you is not already at full stretch.
            if (CreatureVitals.Archer(creature.Kind)) creature.Swings = MathF.Max(creature.Swings, ArcherDraw);
        }
        else if (creature.Unseen > UnseenMemory && !creature.Provoked)
        {
            if (creature.FuseFor > 0f) creature.FuseFor = 0f;
            LoseTarget(creature);
            return false;
        }

        Steer(creature, dt, target, toward, range, solid);

        // In reach it stands and swings rather than walking circles on your toes. ⚠ Measured from
        // its own edge, so a body two blocks wide stops beside you rather than on top of you.
        var girth = MathF.Max(0f, MathF.Max(creature.Size.X, creature.Size.Z) * creature.Scale * 0.5f - BodyHalf);
        creature.Moving = !(creature.SeesTarget && range < 1.1f + girth && MathF.Abs(target.Y - creature.Position.Y) < 1.2f + girth);

        // ⚠ Held short so the ordinary re-think cannot fire mid-chase and send it wandering. The
        // heading is rewritten every step anyway; this is what stops the *decision to move* being
        // taken away from it by a clock that knows nothing about the player.
        creature.Thinks = 0.5f;

        // ⛔ Vertically as well as horizontally. Measured flat, a hostile at the bottom of a shaft
        // is "next to" a player standing at the top of it and hits them through the floor.
        var height = MathF.Abs(target.Y - creature.Position.Y);

        // ⛳ The exploder's whole difference. Close enough, it stops walking and lights itself; a
        // lit fuse keeps burning unless real distance is put between you — backing off a pace
        // changes nothing, which is what makes the hiss a decision and not a nudge.
        if (CreatureVitals.Explodes(creature.Kind))
        {
            var burning = creature.FuseFor > 0f
                ? range <= FuseAbortRange
                : range <= FuseRange && height <= 2f;

            if (burning)
            {
                if (creature.FuseFor == 0f) creature.FuseLit = true;

                creature.Moving = false;
                creature.FuseFor += dt;

                if (creature.FuseFor >= FuseSeconds)
                {
                    // Gone in the flash: no dying fall, no death event, no drops — the blast is
                    // the event, and whoever drains it owns what it does to the world. Health
                    // zero with no DyingFor puts it out of the list on the next sweep.
                    creature.Health = 0;
                    _blasts.Add(new CreatureBlast(creature.Kind, creature.Middle));
                }
            }
            else
            {
                creature.FuseFor = 0f;
            }

            // An exploder never swings; the fuse is its blow.
            return true;
        }

        // ⛳ The archer's whole difference: it stands off and shoots. The reference's bow goal —
        // within fifteen and in view it holds its ground, turns to you, and looses every two
        // seconds after a second's draw; out of view or out of range it walks its path like anyone.
        // ⛳ The witch stands off at ten and throws a flask every three seconds — the reference's
        // own range and beat. Which brew is the host's choice, because only it knows what already
        // ails the target and how hurt it is.
        if (creature.Kind == "witch")
        {
            // ⛳ Hurt past half, she drinks healing — the reference's witch keeps her own potions.
            creature.DrinkRest = MathF.Max(0f, creature.DrinkRest - dt);
            creature.Drank = false;
            if (creature.Health * 2 < creature.MaxHealth && creature.DrinkRest <= 0f)
            {
                creature.Health = Math.Min(creature.MaxHealth, creature.Health + 8);
                creature.DrinkRest = 12f;
                creature.Drank = true;
                return true;
            }

            if (!creature.SeesTarget || distance > WitchRange) return true;

            creature.WantsYaw = float.RadiansToDegrees(MathF.Atan2(toward.Z, toward.X));
            creature.Moving = distance > WitchRange * 0.7f;

            if (creature.Swings <= 0f && MathF.Abs(Wrap(creature.WantsYaw - creature.Yaw)) < 25f)
            {
                creature.Swings = WitchEvery;
                var from = creature.Position + new Vector3(0f, creature.Size.Y * creature.Scale * 0.8f, 0f);
                var line = target + new Vector3(0f, 1.1f, 0f) - from;
                var flat = MathF.Sqrt(line.X * line.X + line.Z * line.Z);
                var time = flat / Projectiles.ProjectileSystem.FlaskSpeed;
                line.Y += 0.5f * Projectiles.ProjectileSystem.FlaskGravity * time * time;
                var aim = Vector3.Normalize(line);
                _shots.Add(new CreatureShot(creature.Kind, from + aim * 0.6f, aim, 0, Flask: true));
            }

            return true;
        }

        if (CreatureVitals.Archer(creature.Kind))
        {
            if (!creature.SeesTarget || distance > ArcherRange) return true;

            creature.WantsYaw = float.RadiansToDegrees(MathF.Atan2(toward.Z, toward.X));
            creature.Moving = distance > ArcherRange * 0.75f;

            var facing = MathF.Abs(Wrap(creature.WantsYaw - creature.Yaw)) < 20f;
            if (creature.Swings <= 0f && facing)
            {
                creature.Swings = ArcherEvery * (0.9f + (float)_random.NextDouble() * 0.3f);
                Loose(creature, target);
            }

            return true;
        }

        // ⛳ The spider's leap: two to four blocks out, on the ground and in view, it springs —
        // the reference's leap-at-target, which is what makes backing off one feel like a mistake.
        if (CreatureVitals.Climbs(creature.Kind) && creature.SeesTarget && !creature.Leaping
            && range is > 2f and < 4f && height < 1f && creature.FallSpeed == 0f && !creature.Clinging
            && _random.NextDouble() < dt * 1.5f)
        {
            creature.Leaping = true;
            creature.Yaw = creature.WantsYaw;
            creature.FallSpeed = -LeapLaunch;
        }

        // ⚠ Reach is measured from the middle, so a body wider than the standard one reaches as far
        // past its own edge as a small one does — a big ember slime is not harmless at its own side.
        var reach = StrikeRange + girth;
        if (range <= reach && height <= 2f + (creature.Bulk - 1) * 0.5f && creature.Swings <= 0f)
        {
            creature.Swings = StrikeEvery;
            _attacks.Add(new CreatureAttack(creature.Kind, creature.Middle, CreatureVitals.DamageFor(creature.Kind, creature.Bulk)));
        }

        return true;
    }

    /// <summary>Measures daylight exposure for the kinds the sun does not agree with.</summary>
    /// <remarks>
    /// ⛳ <b>Why this is worth having at all:</b> it is what makes daylight a resource rather than a
    /// backdrop. Without it a night's worth of hostiles simply accumulates and the morning changes
    /// nothing — with it, the world clears itself and going out at dawn is a different activity from
    /// going out at dusk. ⚠ There is a grace period, so one step through a doorway is survivable and
    /// a creature caught in the open is not.
    /// </remarks>
    /// <summary>
    /// The highest floor under any part of the body's footprint, or negative infinity for none.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The whole footprint, not the centre's column.</b> Asked of the centre alone, a spider
    /// that had climbed to the lip of a wall and stepped a hand's width onto the top was still
    /// "over" the drop it had just climbed, and fell straight back down it.
    /// </remarks>
    private static float Footing(Func<int, int, int, bool> solid, Vector3 at)
    {
        const float hair = 1e-3f;
        var from = (int)MathF.Floor(at.Y) + 1;
        var best = float.NegativeInfinity;

        var x0 = (int)MathF.Floor(at.X - BodyHalf + hair);
        var x1 = (int)MathF.Floor(at.X + BodyHalf - hair);
        var z0 = (int)MathF.Floor(at.Z - BodyHalf + hair);
        var z1 = (int)MathF.Floor(at.Z + BodyHalf - hair);

        for (var x = x0; x <= x1; x++)
        for (var z = z0; z <= z1; z++)
        {
            // ⚠ Never a floor above the feet: a corner of the box touching a wall would otherwise
            // read the wall's top as ground and lift the body onto it.
            if (TryGround(solid, x, z, from, out var y) && y <= at.Y + 0.02f && y > best) best = y;
        }

        return best;
    }

    /// <summary>
    /// A phantom's step: round a circle high over the player, and every few seconds a dive at them.
    /// </summary>
    /// <remarks>
    /// <para>The reference's flight, plainly: it circles out of reach, picks a moment, comes down
    /// straight at you, bites, and climbs away to circle again. A dive that has not landed in four
    /// seconds is given up, and one that meets rock pulls up — a phantom never flies through a roof
    /// to reach somebody under it.</para>
    /// <para>⚠ No path and no sight to begin: it is the sky's, and the only test it needs is the
    /// line it is about to fly down.</para>
    /// </remarks>
    private void Swoop(Creature creature, float dt, Vector3? player, Func<int, int, int, bool> solid)
    {
        creature.Swings = MathF.Max(0f, creature.Swings - dt);
        creature.Moving = true;

        if (player is not { } target)
        {
            Glide(creature, dt, solid, null);
            return;
        }

        if (creature.RideHeight <= 0f)
        {
            creature.RideHeight = PhantomHeight + (float)_random.NextDouble() * 6f;
            creature.SwoopIn = 4f + (float)_random.NextDouble() * 4f;
            creature.Orbit = MathF.Atan2(creature.Position.Z - target.Z, creature.Position.X - target.X);
        }

        var chest = target + new Vector3(0f, 1.1f, 0f);
        Vector3 goal;
        float speed;

        if (creature.Swooping)
        {
            goal = chest;
            speed = PhantomDive;
            creature.SwoopFor += dt;

            if (Vector3.Distance(creature.Middle, chest) < 1.6f && creature.Swings <= 0f)
            {
                creature.Swings = 1f;
                _attacks.Add(new CreatureAttack(creature.Kind, creature.Middle, CreatureVitals.DamageFor(creature.Kind)));
                EndSwoop(creature);
            }
            else if (creature.SwoopFor > 4f)
            {
                EndSwoop(creature);
            }
        }
        else
        {
            creature.Orbit += dt * PhantomSpeed / PhantomRadius;
            goal = new Vector3(
                target.X + MathF.Cos(creature.Orbit) * PhantomRadius,
                target.Y + creature.RideHeight,
                target.Z + MathF.Sin(creature.Orbit) * PhantomRadius);
            speed = PhantomSpeed;

            creature.SwoopIn -= dt;
            if (creature.SwoopIn <= 0f)
            {
                if (LineOfSight(solid, creature.Middle, chest))
                {
                    creature.Swooping = true;
                    creature.SwoopFor = 0f;
                }
                else
                {
                    creature.SwoopIn = 1.5f;
                }
            }
        }

        var to = goal - creature.Position;
        var length = to.Length();
        if (length < 1e-3f) return;

        var next = creature.Position + to / length * MathF.Min(speed * dt, length);
        var cell = (X: (int)MathF.Floor(next.X), Y: (int)MathF.Floor(next.Y + 0.1f), Z: (int)MathF.Floor(next.Z));

        if (solid(cell.X, cell.Y, cell.Z))
        {
            // Rock in the way: a dive pulls up, a circle rides higher.
            if (creature.Swooping) EndSwoop(creature);
            else creature.RideHeight += 2f;
            return;
        }

        creature.Position = next;
        creature.Yaw = creature.WantsYaw = float.RadiansToDegrees(MathF.Atan2(to.Z, to.X));
    }

    /// <summary>A cinderwisp's volley: three shots this far apart, and this long between volleys.</summary>
    public const int VolleyShots = 3;
    public const float VolleyGap = 0.3f;
    public const float VolleyRest = 3.5f;
    public const float VolleyCharge = 0.8f;
    public const float VolleyRange = 16f;
    public const float CinderwispSpeed = 2.4f;

    /// <summary>An ashwail's beat: a second's breath, a bolt, three seconds' rest.</summary>
    public const float WailCharge = 1f;
    public const float WailRest = 3f;
    public const float WailRange = 40f;
    public const float AshwailSpeed = 1.2f;

    /// <summary>A cinderwisp's fireball, on the player: the reference's five.</summary>
    public const int EmberDamage = 5;

    /// <summary>
    /// A cinderwisp's step: hover two and a half over the target's head, between four and ten off,
    /// and loose volleys of three.
    /// </summary>
    /// <remarks>
    /// The reference's blaze: it bobs in the air, rises to fight, flares for a moment and fires three
    /// small fireballs a third of a second apart, then rests. With nobody to fight it drifts, bobbing.
    /// </remarks>
    private void Kindle(Creature creature, float dt, Vector3? player, Func<int, int, int, bool> solid)
    {
        creature.Swings = MathF.Max(0f, creature.Swings - dt);
        creature.Orbit += dt;
        var bob = MathF.Sin(creature.Orbit * 2.2f) * 0.5f;
        Vector3 goal;

        if (player is { } target
            && Vector3.Distance(target, creature.Position) < CreatureVitals.FollowRange(creature.Kind))
        {
            creature.SightCheck -= dt;
            if (creature.SightCheck <= 0f)
            {
                creature.SightCheck = 0.4f + (float)_random.NextDouble() * 0.2f;
                creature.SeesTarget = LineOfSight(solid, creature.Middle, target + new Vector3(0f, 1.5f, 0f));
            }

            if (creature.SeesTarget || creature.Provoked)
            {
                creature.Targeting = true;
                var flat = target - creature.Position;
                flat.Y = 0f;
                var range = flat.Length();
                var hold = range > 10f ? flat / range * (range - 8f)
                    : range is < 4f and > 0.01f ? -flat / range * (4f - range)
                    : Vector3.Zero;
                goal = creature.Position + hold;
                goal.Y = target.Y + 2.5f + bob;
                if (range > 0.01f) creature.Yaw = creature.WantsYaw = float.RadiansToDegrees(MathF.Atan2(flat.Z, flat.X));

                var distance = Vector3.Distance(creature.Middle, target);
                if (creature.Volley > 0)
                {
                    creature.VolleyIn -= dt;
                    if (creature.VolleyIn <= 0f)
                    {
                        creature.Volley--;
                        creature.VolleyIn = VolleyGap;
                        if (creature.SeesTarget) Fling(creature, target, ember: true);
                    }
                }
                else if (creature.Swings <= 0f && creature.SeesTarget && distance <= VolleyRange)
                {
                    creature.Swings = VolleyRest + (float)_random.NextDouble();
                    creature.Volley = VolleyShots;
                    creature.VolleyIn = VolleyCharge;
                    creature.Spoke = true;
                }
            }
            else goal = Idle(creature, dt, bob);
        }
        else
        {
            creature.Targeting = false;
            goal = Idle(creature, dt, bob);
        }

        creature.Moving = true;
        FlyTo(creature, goal, CinderwispSpeed * dt, solid);
    }

    /// <summary>Where an unbothered flier drifts next: a slow heading, bobbing.</summary>
    private Vector3 Idle(Creature creature, float dt, float bob)
    {
        creature.Thinks -= dt;
        if (creature.Thinks <= 0f)
        {
            creature.Thinks = 3f + (float)_random.NextDouble() * 4f;
            creature.WantsYaw = (float)(_random.NextDouble() * 360.0);
        }

        creature.Yaw = creature.WantsYaw;
        var yaw = float.DegreesToRadians(creature.WantsYaw);
        return creature.Position + new Vector3(MathF.Cos(yaw) * 0.4f, bob * 0.3f, MathF.Sin(yaw) * 0.4f);
    }

    /// <summary>
    /// An ashwail's step: drift to somewhere in the open, and when the target is in sight, draw
    /// breath for a second and loose a bolt.
    /// </summary>
    /// <remarks>
    /// The reference's ghast. It wanders its caverns slowly, picking open air to float to; seeing
    /// you, it turns, wails and fires, then rests three seconds. Its body is the size of a room, so
    /// where it may go is asked of the whole box, not one cell.
    /// </remarks>
    private void Wail(Creature creature, float dt, Vector3? player, Func<int, int, int, bool> solid)
    {
        creature.Swings = MathF.Max(0f, creature.Swings - dt);
        creature.SwoopIn -= dt;

        var reached = Vector3.DistanceSquared(creature.Position, creature.Drift) < 1f;
        if (creature.SwoopIn <= 0f || reached || creature.Drift == Vector3.Zero)
        {
            creature.SwoopIn = 5f + (float)_random.NextDouble() * 5f;
            creature.Drift = creature.Position;
            for (var attempt = 0; attempt < 6; attempt++)
            {
                var spot = creature.Position + new Vector3(
                    (float)(_random.NextDouble() * 16.0 - 8.0),
                    (float)(_random.NextDouble() * 8.0 - 4.0),
                    (float)(_random.NextDouble() * 16.0 - 8.0));
                if (!BodyFree(creature, spot, solid)) continue;
                creature.Drift = spot;
                break;
            }
        }

        if (player is { } target && Vector3.Distance(target, creature.Position) < WailRange)
        {
            creature.SightCheck -= dt;
            if (creature.SightCheck <= 0f)
            {
                creature.SightCheck = 0.5f;
                creature.SeesTarget = LineOfSight(solid, creature.Middle, target + new Vector3(0f, 1.5f, 0f));
            }

            if (creature.SeesTarget)
            {
                var flat = target - creature.Position;
                if (flat.X * flat.X + flat.Z * flat.Z > 0.01f)
                    creature.Yaw = creature.WantsYaw = float.RadiansToDegrees(MathF.Atan2(flat.Z, flat.X));

                if (creature.Swings <= 0f)
                {
                    if (creature.Charge == 0f) creature.Spoke = true;
                    creature.Charge += dt;
                    if (creature.Charge >= WailCharge)
                    {
                        creature.Charge = 0f;
                        creature.Swings = WailRest;
                        Fling(creature, target, ember: false);
                    }
                }
            }
            else creature.Charge = 0f;
        }
        else creature.Charge = 0f;

        creature.Moving = true;
        FlyTo(creature, creature.Drift, AshwailSpeed * dt, solid);
    }

    /// <summary>A fireball or a bolt, from the edge of the body at the target's chest.</summary>
    private void Fling(Creature creature, Vector3 target, bool ember)
    {
        var from = creature.Middle;
        var aim = target + new Vector3(0f, 1.1f, 0f) - from;
        if (aim.LengthSquared() < 1e-4f) return;
        aim = Vector3.Normalize(aim);

        // The reference's blaze scatters its shots a little; the ghast's goes where it looks.
        if (ember)
        {
            aim = Vector3.Normalize(aim + new Vector3(
                (float)(_random.NextDouble() - 0.5) * 0.12f,
                (float)(_random.NextDouble() - 0.5) * 0.06f,
                (float)(_random.NextDouble() - 0.5) * 0.12f));
        }

        var clear = MathF.Max(creature.Size.X, creature.Size.Z) * creature.Scale * 0.5f + 0.4f;
        _shots.Add(new CreatureShot(creature.Kind, from + aim * clear, aim,
            ember ? EmberDamage : 0, Ember: ember, Wail: !ember));
    }

    /// <summary>Moves a flier toward a point, each axis refused where its whole body would meet rock.</summary>
    private static void FlyTo(Creature creature, Vector3 goal, float step, Func<int, int, int, bool> solid)
    {
        var to = goal - creature.Position;
        var length = to.Length();
        if (length < 1e-3f) return;
        var move = to / length * MathF.Min(step, length);

        foreach (var axis in (ReadOnlySpan<int>)[0, 1, 2])
        {
            var next = creature.Position;
            if (axis == 0) next.X += move.X;
            else if (axis == 1) next.Y += move.Y;
            else next.Z += move.Z;
            if (BodyFree(creature, next, solid)) creature.Position = next;
        }
    }

    /// <summary>Whether the creature's whole box would stand clear of rock with its feet here.</summary>
    private static bool BodyFree(Creature creature, Vector3 at, Func<int, int, int, bool> solid)
    {
        var half = MathF.Max(MathF.Max(creature.Size.X, creature.Size.Z) * creature.Scale * 0.5f, BodyHalf);
        var tall = MathF.Max(creature.Size.Y * creature.Scale, 0.5f);
        const float hair = 1e-3f;

        for (var x = (int)MathF.Floor(at.X - half + hair); x <= (int)MathF.Floor(at.X + half - hair); x++)
        for (var z = (int)MathF.Floor(at.Z - half + hair); z <= (int)MathF.Floor(at.Z + half - hair); z++)
        for (var y = (int)MathF.Floor(at.Y + hair); y <= (int)MathF.Floor(at.Y + tall - hair); y++)
            if (solid(x, y, z)) return false;

        return true;
    }

    private void EndSwoop(Creature creature)
    {
        creature.Swooping = false;
        creature.SwoopIn = 5f + (float)_random.NextDouble() * 5f;
        creature.RideHeight = PhantomHeight + (float)_random.NextDouble() * 6f;
    }

    /// <summary>Forgets the hunt: no target, no path.</summary>
    /// <summary>
    /// Picks somewhere up to ten blocks off that the body can stand, and the route to it.
    /// </summary>
    /// <remarks>The reference's random stroll: ten across, three up or down, a few tries.</remarks>
    private void PlanStroll(Creature creature, Func<int, int, int, bool> solid)
    {
        var from = Cell(creature.Position);
        var tall = Tall(creature);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var x = from.X + _random.Next(-10, 11);
            var z = from.Z + _random.Next(-10, 11);
            if (Math.Abs(x - from.X) + Math.Abs(z - from.Z) < 3) continue;

            for (var y = from.Y + 3; y >= from.Y - 3; y--)
            {
                if (!CreaturePaths.Stands(solid, x, y, z, tall, false)) continue;

                _pathBudget--;
                var route = _paths.Find(solid, from, new PathStep(x, y, z), tall, WanderDrop,
                    budget: 250, near: 0.5f);
                if (route is { Count: > 1 })
                {
                    creature.Stroll = route;
                    creature.StrollAt = 1;
                    creature.Stuck = 0f;
                }
                return;
            }
        }
    }

    private static void LoseTarget(Creature creature)
    {
        creature.Targeting = false;
        creature.Path = null;
        creature.Unseen = 0f;
    }

    /// <summary>How many cells of head room a body needs.</summary>
    public static int Tall(Creature creature) =>
        Math.Max(1, (int)MathF.Ceiling(creature.Size.Y * creature.Scale - 0.05f));

    /// <summary>
    /// The furthest a hunter will drop to reach its target: three, and more the healthier it is.
    /// </summary>
    /// <remarks>
    /// The reference's rule on its normal setting: three, plus whatever health it has over a third
    /// of its most, less four. Our health is counted in halves, so it is halved first. A zombie at
    /// full health will take twelve blocks; a wounded one stays on the ledge.
    /// </remarks>
    public static int ChaseDrop(Creature creature)
    {
        var spare = (int)(creature.Health * 0.5f - creature.MaxHealth * 0.5f * 0.33f) - 4;
        return WanderDrop + Math.Max(0, spare);
    }

    /// <summary>
    /// Points it along its path toward the target, finding a new path when this one has gone stale.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>The last few blocks are straight.</b> In view, level and close, the grid has nothing to
    /// add and a path would only make it walk to the middle of a cell before turning to you.
    /// </remarks>
    private void Steer(Creature creature, float dt, Vector3 target, Vector3 toward, float range,
        Func<int, int, int, bool> solid)
    {
        var height = MathF.Abs(target.Y - creature.Position.Y);

        if (creature.SeesTarget && range < 3f && height < 1.2f)
        {
            creature.WantsYaw = float.RadiansToDegrees(MathF.Atan2(toward.Z, toward.X));
            creature.Path = null;
            return;
        }

        creature.Repath -= dt;

        var goal = Cell(target);
        var stale = creature.Path is null
                    || creature.Repath <= 0f
                    || creature.Stuck > 0.8f
                    || Math.Abs(goal.X - creature.PathGoal.X) + Math.Abs(goal.Z - creature.PathGoal.Z)
                       + Math.Abs(goal.Y - creature.PathGoal.Y) > 2;

        // ⚠ Only from standing. A body in the air has no cell to start from, and the fall will
        // put it somewhere a path can begin.
        if (stale && _pathBudget > 0 && creature.FallSpeed == 0f)
        {
            _pathBudget--;
            creature.Repath = 1f + (float)_random.NextDouble() * 0.6f;
            creature.PathGoal = goal;
            creature.Stuck = 0f;
            creature.Path = _paths.Find(
                solid, Cell(creature.Position), goal, Tall(creature), ChaseDrop(creature),
                CreatureVitals.Climbs(creature.Kind));
            creature.PathAt = 1;
        }

        if (creature.Path is { } path)
        {
            var at = creature.PathAt;
            var going = FollowPath(creature, path, ref at);
            creature.PathAt = at;
            if (going) return;

            // Walked it all and still not there: the path was partial. Look again soon.
            creature.Path = null;
            creature.Repath = 0f;
        }

        creature.WantsYaw = float.RadiansToDegrees(MathF.Atan2(toward.Z, toward.X));
    }

    /// <summary>
    /// Aims a creature down a route, and says whether there is any of it left to walk.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>The reference's waypoint rules, both of them.</b> A step counts as reached inside
    /// 0.45 of its centre (the old 0.3 was smaller than one frame's turn at a run, so a hunter
    /// overshot the ring, swung round and circled the corner it was meant to pass); and a step the
    /// body is already past — nearer the step after it than that step is — is skipped rather than
    /// walked back to.</para>
    /// <para>⚠ A climber's steps up a face share one column, and "face the next step" from directly
    /// under it has no direction at all — the first spider stood at the foot of its wall turning in
    /// circles. In the column is enough for it; pressing on toward the step after is what puts it
    /// against the wall, and the wall does the rest.</para>
    /// </remarks>
    private static bool FollowPath(Creature creature, List<PathStep> path, ref int at)
    {
        const float reach = 0.45f;
        var climbs = CreatureVitals.Climbs(creature.Kind);

        while (at < path.Count)
        {
            var step = path[at];
            var dx = step.X + 0.5f - creature.Position.X;
            var dz = step.Z + 0.5f - creature.Position.Z;
            var level = MathF.Abs(step.Y - creature.Position.Y) < 1.2f || climbs;

            if (level && dx * dx + dz * dz < reach * reach)
            {
                at++;
                continue;
            }

            if (level && at + 1 < path.Count)
            {
                var after = path[at + 1];
                var bodyToAfter = new Vector2(after.X + 0.5f - creature.Position.X, after.Z + 0.5f - creature.Position.Z).LengthSquared();
                var stepToAfter = new Vector2(after.X - step.X, after.Z - step.Z).LengthSquared();
                if (after.Y == step.Y && bodyToAfter < stepToAfter)
                {
                    at++;
                    continue;
                }
            }

            creature.WantsYaw = float.RadiansToDegrees(MathF.Atan2(dz, dx));
            return true;
        }

        return false;
    }

    /// <summary>The cell a position's feet are in.</summary>
    private static PathStep Cell(Vector3 at) =>
        new((int)MathF.Floor(at.X), (int)MathF.Floor(at.Y + 0.01f), (int)MathF.Floor(at.Z));

    /// <summary>
    /// Whether a straight line between two points passes through nothing solid.
    /// </summary>
    /// <remarks>Sampled every quarter block — at two looks a second per hunter, cheaper than exact.</remarks>
    public static bool LineOfSight(Func<int, int, int, bool> solid, Vector3 from, Vector3 to)
    {
        var line = to - from;
        var length = line.Length();
        if (length < 1e-3f) return true;

        var steps = (int)MathF.Ceiling(length / 0.25f);
        var step = line / steps;
        var at = from;

        for (var i = 1; i < steps; i++)
        {
            at += step;
            if (solid((int)MathF.Floor(at.X), (int)MathF.Floor(at.Y), (int)MathF.Floor(at.Z))) return false;
        }

        return true;
    }

    /// <summary>
    /// One step forward along the yaw against the terrain, and whether it went anywhere at all.
    /// </summary>
    /// <param name="maxDrop">
    /// The furthest it will walk off an edge. A step whose column has no floor that close is refused,
    /// as a wall is.
    /// </param>
    /// <remarks>
    /// <para>⛳ <b>Each axis on its own.</b> A body pressed into a wall at an angle slides along it
    /// rather than stopping dead, which is most of what a creature caught on the terrain looked like.</para>
    /// <para>A step that meets a wall one block high is lifted onto it, if there is head room for
    /// the lift; anything higher is a wall. A step into open air is taken — the fall that follows is
    /// <see cref="Fall"/>'s, and it is charged for.</para>
    /// </remarks>
    private static bool Walk(Creature creature, float distance, Func<int, int, int, bool> solid, int maxDrop)
    {
        // ⛳ Along where it MEANS to go, not where its body happens to face. The body turns at a
        // walking pace and used to carry the feet with it, so every corner was taken wide and every
        // wall met mid-turn was pressed into until the turn came round. The reference moves to its
        // waypoint and lets the body catch up; a turned head a step behind reads as natural.
        var yaw = float.DegreesToRadians(creature.WantsYaw);
        var move = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw)) * distance;
        var tall = Tall(creature);
        var start = creature.Position;
        var at = start;

        foreach (var axis in (ReadOnlySpan<int>)[0, 2])
        {
            var next = axis == 0 ? at with { X = at.X + move.X } : at with { Z = at.Z + move.Z };
            if (axis == 0 ? MathF.Abs(move.X) < 1e-6f : MathF.Abs(move.Z) < 1e-6f) continue;

            if (Fits(solid, next, tall))
            {
                at = next;
                continue;
            }

            // A one-block step: the same move, a block up, with the head room to rise into.
            var lifted = next with { Y = MathF.Floor(at.Y + 0.01f) + 1f };
            if (Fits(solid, lifted, tall) && Fits(solid, at with { Y = lifted.Y }, tall))
                at = lifted;
        }

        if (Vector3.DistanceSquared(at, start) < 1e-8f) return false;

        // ⚠ The edge, asked of the column the centre is about to be over. Floor within the drop it
        // will take, or that move is not taken — but each axis alone is offered before giving up,
        // so a body walking along a cliff at an angle slides along its lip instead of freezing.
        if (Floored(solid, start, at, maxDrop)) { creature.Position = at; return true; }

        foreach (var only in (ReadOnlySpan<Vector3>)[at with { Z = start.Z }, at with { X = start.X }])
        {
            if (Vector3.DistanceSquared(only, start) < 1e-8f || !Fits(solid, only, tall)) continue;
            if (!Floored(solid, start, only, maxDrop)) continue;
            creature.Position = only;
            return true;
        }

        return false;
    }

    /// <summary>Whether a step from one point to another has ground within the drop it will take.</summary>
    private static bool Floored(Func<int, int, int, bool> solid, Vector3 start, Vector3 at, int maxDrop)
    {
        if (at.Y > start.Y + 0.01f) return true;

        var x = (int)MathF.Floor(at.X);
        var z = (int)MathF.Floor(at.Z);
        var feet = (int)MathF.Floor(at.Y + 0.01f);

        for (var d = 0; d <= maxDrop; d++)
            if (solid(x, feet - 1 - d, z)) return true;

        return false;
    }

    /// <summary>Lifts a climber up the face in front of it, if there is room above.</summary>
    private static bool Climb(Creature creature, float dt, Func<int, int, int, bool> solid)
    {
        var tall = Tall(creature);
        var up = creature.Position with { Y = creature.Position.Y + ClimbSpeed * dt };
        if (!Fits(solid, up, tall) || !TouchingWall(solid, creature.Position, tall)) return false;

        creature.Position = up;
        creature.Clinging = true;
        creature.FallSpeed = 0f;
        return true;
    }

    /// <summary>Whether a wall is within reach of the body's sides at any height it stands.</summary>
    private static bool TouchingWall(Func<int, int, int, bool> solid, Vector3 at, int tall)
    {
        const float reach = BodyHalf + 0.15f;
        var y0 = (int)MathF.Floor(at.Y + 0.01f);

        for (var y = y0; y < y0 + tall; y++)
        {
            if (solid((int)MathF.Floor(at.X + reach), y, (int)MathF.Floor(at.Z))) return true;
            if (solid((int)MathF.Floor(at.X - reach), y, (int)MathF.Floor(at.Z))) return true;
            if (solid((int)MathF.Floor(at.X), y, (int)MathF.Floor(at.Z + reach))) return true;
            if (solid((int)MathF.Floor(at.X), y, (int)MathF.Floor(at.Z - reach))) return true;
        }

        return false;
    }

    /// <summary>Whether a body of this height fits with its feet at this point.</summary>
    private static bool Fits(Func<int, int, int, bool> solid, Vector3 at, int tall)
    {
        // ⚠ Shaved by a hair. A body slid flush against a wall sits at 5.700006 against a face at
        // 6, and unshaved its own position overlaps the wall — every move from there, the climb
        // included, was refused, and the first spider stood against its wall for good.
        const float hair = 1e-3f;
        var x0 = (int)MathF.Floor(at.X - BodyHalf + hair);
        var x1 = (int)MathF.Floor(at.X + BodyHalf - hair);
        var z0 = (int)MathF.Floor(at.Z - BodyHalf + hair);
        var z1 = (int)MathF.Floor(at.Z + BodyHalf - hair);
        var y0 = (int)MathF.Floor(at.Y + 0.01f);

        for (var x = x0; x <= x1; x++)
        for (var z = z0; z <= z1; z++)
        for (var y = y0; y < y0 + tall; y++)
            if (solid(x, y, z)) return false;

        return true;
    }

    private static void Scorch(
        Creature creature,
        float dt,
        Func<int, int, int, bool>? sunlit,
        Action<Creature>? scorch)
    {
        if (sunlit is null || !CreatureVitals.BurnsInDaylight(creature.Kind)) return;

        var x = (int)MathF.Floor(creature.Position.X);
        var y = (int)MathF.Floor(creature.Position.Y + 0.5f);
        var z = (int)MathF.Floor(creature.Position.Z);

        if (!sunlit(x, y, z))
        {
            creature.Burning = 0f;
            return;
        }

        creature.Burning = MathF.Min(creature.Burning + dt, ScorchSeconds);
        if (creature.Burning < ScorchSeconds) return;
        scorch?.Invoke(creature);
    }

    /// <summary>Every blow landed on the player since this was last asked, and forgets them.</summary>
    public List<CreatureAttack> TakeAttacks()
    {
        if (_attacks.Count == 0) return [];
        var taken = new List<CreatureAttack>(_attacks);
        _attacks.Clear();
        return taken;
    }

    /// <summary>Every arrow loosed since last asked, drained like the attacks are.</summary>
    public List<CreatureShot> TakeShots()
    {
        if (_shots.Count == 0) return [];
        var taken = new List<CreatureShot>(_shots);
        _shots.Clear();
        return taken;
    }

    /// <summary>
    /// Aims one arrow at the target's chest, lobbed for the drop and thrown off a little.
    /// </summary>
    /// <remarks>
    /// The lob is worked out from the pool's own arrow speed and gravity, so a change to either moves
    /// the aim with it. ⚠ The spread is the reference's on its normal setting — about two and a half
    /// degrees a side — which is what makes strafing worth doing and standing still a mistake.
    /// </remarks>
    private void Loose(Creature creature, Vector3 target)
    {
        var from = creature.Position + new Vector3(0f, MathF.Max(0.6f, creature.Size.Y * creature.Scale * 0.8f), 0f);
        var chest = target + new Vector3(0f, 1.1f, 0f);
        var line = chest - from;

        var flat = MathF.Sqrt(line.X * line.X + line.Z * line.Z);
        var time = flat / Projectiles.ProjectileSystem.ArrowSpeed;
        line.Y += 0.5f * Projectiles.ProjectileSystem.ArrowGravity * time * time;

        var aim = Vector3.Normalize(line);
        aim += new Vector3(Gauss(), Gauss(), Gauss()) * ArcherSpread;

        var damage = CreatureVitals.DamageFor(creature.Kind) + _random.Next(3);

        // The stray's arrows slow and the bogged's poison — the tipped arrows they are known by.
        var tag = creature.Kind switch
        {
            "stray" => ShotTag.Slowing,
            "bogged" => ShotTag.Poisoned,
            _ => ShotTag.None,
        };
        _shots.Add(new CreatureShot(creature.Kind, from + Vector3.Normalize(aim) * 0.6f, Vector3.Normalize(aim), damage, tag));

        float Gauss()
        {
            var u = 1.0 - _random.NextDouble();
            var v = _random.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(Math.Tau * v));
        }
    }

    /// <summary>Every fuse that burned down since last asked, drained like the attacks are.</summary>
    public List<CreatureBlast> TakeBlasts()
    {
        if (_blasts.Count == 0) return [];
        var taken = new List<CreatureBlast>(_blasts);
        _blasts.Clear();
        return taken;
    }

    /// <summary>Every teleport step taken since last asked, drained like the attacks are.</summary>
    public List<CreatureBlink> TakeBlinks()
    {
        if (_blinks.Count == 0) return [];
        var taken = new List<CreatureBlink>(_blinks);
        _blinks.Clear();
        return taken;
    }

    /// <summary>
    /// Steps one somewhere else nearby: a ring out at blink reach, on ground, or nowhere.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Failing to find a spot is answered by staying, not by trying harder.</b> Eight tries
    /// over a ring of four to eight blocks covers anything that is not a sealed cell, and a sealed
    /// cell is exactly where a blink that loops forever would hang the update.
    /// </remarks>
    private bool Blink(Creature creature, Func<int, int, int, bool> solid)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var angle = (float)(_random.NextDouble() * Math.Tau);
            var reach = BlinkNear + (float)_random.NextDouble() * (BlinkFar - BlinkNear);

            var x = (int)MathF.Floor(creature.Position.X + MathF.Cos(angle) * reach);
            var z = (int)MathF.Floor(creature.Position.Z + MathF.Sin(angle) * reach);

            if (!TryGround(solid, x, z, (int)creature.Position.Y + 6, out var y)) continue;

            // A step, not a journey: a ledge six blocks up or a pit six down is not "beside".
            if (MathF.Abs(y - creature.Position.Y) > 6f) continue;

            var to = new Vector3(x + 0.5f, y, z + 0.5f);
            _blinks.Add(new CreatureBlink(creature.Kind, creature.Position, to));

            creature.Position = to;
            creature.BlinkRest = BlinkRestSeconds;

            // Whatever fall it was owed stays where it left it — arriving is not landing.
            creature.FallSpeed = 0f;
            creature.FellFor = 0f;
            return true;
        }

        return false;
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
        // ⛔ Nothing found is a fall, not a floor at nought. The search used to give up at y 1 and
        // the answer was "the ground is at 0", so anything in a cave below the zero line was put
        // back up inside the rock the first frame it stood there.
        var ground = Footing(solid, creature.Position);

        // Out of the bottom of the world: gone, with nothing left behind — there is nowhere for it.
        if (creature.Position.Y < Gen.TerrainGenerator.WorldBottom - 8)
        {
            creature.Health = 0;
            creature.DyingFor = 0f;
            return true;
        }

        // A leap is over the moment it is back on the ground — or on a wall.
        if (creature.Leaping && creature.FallSpeed >= 0f && creature.Position.Y - ground <= 0.02f)
            creature.Leaping = false;

        // A climber holding a wall does not fall off it.
        if (creature.Clinging && creature.FallSpeed >= 0f && TouchingWall(solid, creature.Position, Tall(creature)))
        {
            creature.FallSpeed = 0f;
            creature.FellFor = 0f;
            return false;
        }

        creature.Clinging = false;

        // ⚠ Only a body on its way DOWN can be standing. A hopper the instant it launches is at
        // ground height with upward speed, and the unguarded test read that as "landed" and zeroed
        // the launch before it moved a frame.
        if (creature.Position.Y - ground <= 0.02f && creature.FallSpeed >= 0f)
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

        // Rising into a roof stops the rise where it is — a slime under a two-block ceiling bonks
        // and comes straight back down rather than drawing its head through the rock.
        if (creature.FallSpeed < 0f
            && solid(x, (int)MathF.Floor(to + creature.Size.Y * creature.Scale), z))
        {
            creature.FallSpeed = 0f;
            to = creature.Position.Y;
        }

        // ⚠ Signed on purpose: the rise pays its height back before the fall counts. A hop's own
        // arc then nets to nothing, and a hop off a cliff is charged the true drop below the
        // launch point rather than the whole arc.
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

        // A solid canopy at y 70 used to win the top-down search and put every new animal at y 71.
        // It remains solid collision, but the support predicate admits only the soil underneath.
        static bool UnderTrees(int x, int y, int z) => y < 64 || y == 70;
        static bool NaturalGround(int x, int y, int z) => y < 64;
        var shaded = new CreatureHerd(17);
        shaded.Spawn(
            UnderTrees, [kinds[0]], new Vector3(0f, 64f, 0f), 3,
            spawnSupport: NaturalGround);
        if (shaded.Count != 3 || shaded.All.Any(one => MathF.Abs(one.Position.Y - 64f) > 0.01f))
            faults.Add("a solid tree canopy became legal creature spawn support");

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
        faults.AddRange(ValidateHunting());
        faults.AddRange(ValidateRetaliation());
        faults.AddRange(ValidateHopping());
        faults.AddRange(ValidateFusing());
        faults.AddRange(ValidateBlinking());
        faults.AddRange(ValidateShyness());
        faults.AddRange(ValidateFlying());
        faults.AddRange(ValidateSwimming());

        return faults;
    }

    /// <summary>
    /// Checks a flier gets off the floor of a roofed cave, stays out of its rock, and is still
    /// airborne rather than parked at the end.
    /// </summary>
    /// <remarks>
    /// ⛔ The roof is the arm that matters: an unroofed check passes a flier that climbs for
    /// ever, and every walker arm already proves grounded kinds stay grounded.
    /// </remarks>
    private static List<string> ValidateFlying()
    {
        var faults = new List<string>();

        // A cave six blocks tall: floor below 64, roof from 70 up.
        static bool Cave(int x, int y, int z) => y < 64 || y >= 70;

        var bats = new CreatureHerd(79);
        bats.Spawn(
            Cave, [new SpawnKind("bat", new Vector3(0.9f, 0.85f, 0.9f), Move: CreatureMove.Fly)],
            new Vector3(0f, 64f, 0f), 2);
        if (bats.Count != 2) return ["no bats stood up for the flying check"];

        var peak = 0f;
        var buried = 0;

        for (var i = 0; i < 900; i++)
        {
            bats.Update(1f / 60f, Cave);

            foreach (var one in bats.All)
            {
                peak = MathF.Max(peak, one.Position.Y - 64f);

                if (Cave(
                        (int)MathF.Floor(one.Position.X),
                        (int)MathF.Floor(one.Position.Y),
                        (int)MathF.Floor(one.Position.Z)))
                    buried++;
            }
        }

        if (peak < 1f)
            faults.Add($"a flier peaked {peak:F2} blocks off the cave floor in fifteen seconds");

        if (buried > 0)
            faults.Add($"a flier spent {buried} steps of the run inside solid rock");

        foreach (var one in bats.All)
            if (one.Position.Y >= 70f || one.Position.Y < 64f)
                faults.Add($"a flier ended the run at y {one.Position.Y:F1} in a cave of 64..70");

        return faults;
    }

    /// <summary>
    /// Checks a swimmer swims — it rises off the pond floor and never leaves the water — and
    /// that the same body demoted to a walker only ever wades.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The walker control is the whole check.</b> On a flooded floor, "every step was in
    /// water" is true of a beast standing in the shallows too; what only a swimmer does is leave
    /// the ground without leaving the water, so the climb is the discriminating claim.
    /// </remarks>
    private static List<string> ValidateSwimming()
    {
        var faults = new List<string>();

        // A pond: bedrock below 40, ten blocks of water out to eight from the middle, air above.
        static bool Bed(int x, int y, int z) => y < 40;
        static bool Pond(int x, int y, int z) =>
            y is >= 40 and < 50 && Math.Abs(x) <= 8 && Math.Abs(z) <= 8;

        var squids = new CreatureHerd(83);
        squids.Spawn(
            Bed, [new SpawnKind("squid", new Vector3(1.5f, 2.1f, 1.5f), Move: CreatureMove.Swim)],
            new Vector3(0f, 41f, 0f), 1, where: Pond);
        if (squids.Count != 1) return ["no squid found water to stand in for the swimming check"];

        var squid = squids.All[0];
        var peak = 0f;
        var dry = 0;

        for (var i = 0; i < 900; i++)
        {
            squids.Update(1f / 60f, Bed, water: Pond);

            peak = MathF.Max(peak, squid.Position.Y - 40f);

            if (!Pond(
                    (int)MathF.Floor(squid.Position.X),
                    (int)MathF.Floor(squid.Position.Y),
                    (int)MathF.Floor(squid.Position.Z)))
                dry++;
        }

        if (peak < 1f)
            faults.Add($"a swimmer rose {peak:F2} blocks off the pond floor in fifteen seconds");

        if (dry > 0)
            faults.Add($"a swimmer spent {dry} steps of the run out of the water");

        // ⛔ The control: the same body walking. It stands in the same water and must never
        // climb it — a rule that floated every wader would pass the arm above.
        var waders = new CreatureHerd(89);
        waders.Spawn(
            Bed, [new SpawnKind("squid", new Vector3(1.5f, 2.1f, 1.5f))],
            new Vector3(0f, 41f, 0f), 1, where: Pond);
        if (waders.Count == 1)
        {
            var wader = waders.All[0];
            var high = 0f;

            for (var i = 0; i < 300; i++)
            {
                waders.Update(1f / 60f, Bed, water: Pond);
                high = MathF.Max(high, wader.Position.Y - 40f);
            }

            if (high > 0.5f)
                faults.Add($"a walking body rose {high:F2} blocks in water it should only wade");
        }

        return faults;
    }

    /// <summary>
    /// Checks a timid kind bolts from a mere approach — and that an ordinary beast does not.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The cow is the control.</b> Both stand four blocks from somebody who never swings;
    /// the fox must be fleeing and further away within two seconds, the cow must not be fleeing
    /// at all — or "it ran" is true of a rule that panics every beast at the sight of people.
    /// </remarks>
    private static List<string> ValidateShyness()
    {
        var faults = new List<string>();

        static bool Flat(int x, int y, int z) => y < 64;

        var walker = new Vector3(0.5f, 64f, 0.5f);

        var foxes = new CreatureHerd(71);
        foxes.Spawn(Flat, [new SpawnKind("fox", new Vector3(0.6f, 0.9f, 1.1f))], new Vector3(0f, 64f, 0f), 1);
        if (foxes.Count != 1) return ["no fox stood up for the shyness check"];

        var fox = foxes.All[0];
        fox.Position = new Vector3(0.5f, 64f, 4.5f);
        fox.Moving = false;

        // ⚠ "Did it bolt", not "is it fleeing at the final frame" — the flee flag honestly
        // expires once the fox has put the shy range behind it, which is the mechanism working.
        var was = Vector3.Distance(fox.Position, walker);
        var bolted = false;

        for (var i = 0; i < 120; i++)
        {
            foxes.Update(1f / 60f, Flat, walker);
            bolted |= fox.FleeFor > 0f;
        }

        if (!bolted) faults.Add("a fox four blocks from somebody never bolted");
        if (Vector3.Distance(fox.Position, walker) < was + 1.5f)
            faults.Add($"a shy fox opened only {Vector3.Distance(fox.Position, walker) - was:F1} blocks in two seconds");

        var cows = new CreatureHerd(73);
        cows.Spawn(Flat, [new SpawnKind("cow", new Vector3(0.75f, 1.56f, 1.50f))], new Vector3(0f, 64f, 0f), 1);
        if (cows.Count == 1)
        {
            var cow = cows.All[0];
            cow.Position = new Vector3(0.5f, 64f, 4.5f);

            for (var i = 0; i < 120; i++) cows.Update(1f / 60f, Flat, walker);

            if (cow.FleeFor > 0f) faults.Add("a cow fled from somebody who never swung");
        }

        return faults;
    }

    /// <summary>
    /// Checks a struck blinker is somewhere else on the next step, that crowding one moves it
    /// without a blow, and that the step is a step — landed on ground, inside the ring.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The wolf is the control.</b> It shares the whole retaliation path — struck, provoked,
    /// pack-angry — and must NOT teleport, or "it moved when hit" is true of the flinch and the
    /// chase too. Half a second is the window: a blink is instant and a walk covers under a block.
    /// </remarks>
    private static List<string> ValidateBlinking()
    {
        var faults = new List<string>();

        static bool Flat(int x, int y, int z) => y < 64;

        var walker = new SpawnKind("farwalker", new Vector3(0.6f, 3.1f, 0.6f), Hostile: false);
        var player = new Vector3(0.5f, 64f, -6.5f);

        var herd = new CreatureHerd(59);
        herd.Spawn(Flat, [walker], new Vector3(0f, 64f, 0f), 1);
        if (herd.Count != 1) return ["no farwalker stood up for the blink check"];

        var struck = herd.All[0];
        struck.Position = new Vector3(0.5f, 64f, 0.5f);
        var stood = struck.Position;

        herd.Hurt(struck, 3, player);
        if (!struck.Provoked) faults.Add("a struck farwalker did not take it personally");

        for (var i = 0; i < 30; i++) herd.Update(1f / 60f, Flat);

        // ⚠ The blink is judged from its own record, not from where the body stands half a
        // second later — a provoked one WALKS after arriving, honestly, and measuring the sum
        // of a blink and a walk-back read a working step as a failure. The position assert
        // below only says it is genuinely elsewhere.
        var blinks = herd.TakeBlinks();
        if (blinks.Count != 1) faults.Add($"one blow booked {blinks.Count} blinks");
        foreach (var blink in blinks)
        {
            var step = Vector3.Distance(blink.From, blink.To);

            // Landing on a cell's own middle can shave most of a diagonal off the rolled reach.
            if (step < BlinkNear - 1.5f)
                faults.Add($"a blink stepped {step:F1} blocks, under its own ring of {BlinkNear}");
            if (step > BlinkFar + 1.5f)
                faults.Add($"a blink stepped {step:F1} blocks, past its own ring of {BlinkFar}");
            if (MathF.Abs(blink.To.Y - 64f) > 0.01f)
                faults.Add($"a blink landed at y {blink.To.Y:F2} rather than on the floor");
        }

        if (Vector3.Distance(struck.Position, stood) < 1.5f)
            faults.Add("a struck farwalker is still where the blow found it");

        // ⛔ The control: a struck wolf, same blow, same window, must still be where the flinch
        // and half a second of legs put it — under a block, not a ring away.
        var wolves = new CreatureHerd(61);
        wolves.Spawn(Flat, [new SpawnKind("wolf", new Vector3(0.9f, 1.1f, 1.4f))], new Vector3(0f, 64f, 0f), 1);
        if (wolves.Count == 1)
        {
            var wolf = wolves.All[0];
            wolf.Position = new Vector3(0.5f, 64f, 0.5f);
            var was = wolf.Position;

            wolves.Hurt(wolf, 3, player);
            for (var i = 0; i < 30; i++) wolves.Update(1f / 60f, Flat);

            if (Vector3.Distance(wolf.Position, was) > 1.2f)
                faults.Add("a struck wolf crossed more ground in half a second than legs allow");
            if (wolves.TakeBlinks().Count != 0) faults.Add("a wolf teleported");
        }

        // And the crowd step: stood over, an unprovoked one leaves on its own within a second.
        var shy = new CreatureHerd(67);
        shy.Spawn(Flat, [walker], new Vector3(0f, 64f, 0f), 1);
        if (shy.Count == 1)
        {
            var crowded = shy.All[0];
            crowded.Position = new Vector3(0.5f, 64f, 0.5f);
            var near = new Vector3(0.5f, 64f, 2.5f);

            for (var i = 0; i < 60; i++) shy.Update(1f / 60f, Flat, near);

            // The record again, for the walk-back reason above: the claim is that crowding
            // produced a step with no blow anywhere in it.
            if (shy.TakeBlinks().Count == 0)
                faults.Add("a crowded farwalker stood its ground rather than stepping away");
        }

        return faults;
    }

    /// <summary>
    /// Checks an exploder lights at arm's length, blasts on schedule, never swings, and that its
    /// fuse goes out when real distance is put in — and that nothing else in the game blasts.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Three controls, one per false pass.</b> A zombie at the same range must swing and
    /// never blast, or "it blasted" is true of every hostile; a crawler out at ten blocks must
    /// chase and never blast, or range means nothing; and a player who RUNS mid-fuse must be
    /// answered with silence, or the abort is a comment.
    /// </remarks>
    private static List<string> ValidateFusing()
    {
        var faults = new List<string>();

        static bool Flat(int x, int y, int z) => y < 64;

        var crawler = new SpawnKind("crawler", new Vector3(0.5f, 1.63f, 0.5f), Hostile: true);
        var player = new Vector3(0.5f, 64f, 0.5f);

        var herd = new CreatureHerd(41);
        herd.Spawn(Flat, [crawler], new Vector3(0f, 64f, 0f), 1);
        if (herd.Count != 1) return ["no crawler stood up for the fuse check"];

        var one = herd.All[0];
        one.Position = new Vector3(0.5f, 64f, 2.5f);

        // Two blocks away: it lights, and one and a half seconds later there is a blast, the
        // crawler is gone, and at no point did it swing.
        var lit = false;
        for (var i = 0; i < 150; i++)
        {
            herd.Update(1f / 60f, Flat, player);
            lit |= one.FuseLit;
        }

        if (!lit) faults.Add("a crawler two blocks from somebody never lit its fuse");

        var blasts = herd.TakeBlasts();
        if (blasts.Count != 1) faults.Add($"{blasts.Count} blasts came off one fuse");
        if (herd.TakeAttacks().Count != 0) faults.Add("an exploder landed an ordinary swing");
        if (herd.All.Any(c => c.Alive)) faults.Add("a crawler survived its own blast");

        // ⛔ Control one: a zombie at the same range swings and never blasts.
        var swinger = new CreatureHerd(43);
        swinger.Spawn(Flat, [new SpawnKind("zombie", new Vector3(0.63f, 2f, 0.63f), Hostile: true)],
            new Vector3(0f, 64f, 0f), 1);
        if (swinger.Count == 1)
        {
            swinger.All[0].Position = new Vector3(0.5f, 64f, 2.5f);
            for (var i = 0; i < 150; i++) swinger.Update(1f / 60f, Flat, player);

            if (swinger.TakeBlasts().Count != 0) faults.Add("a zombie exploded");
            if (swinger.TakeAttacks().Count == 0) faults.Add("a zombie two blocks off landed nothing");
        }

        // ⛔ Control two: ten blocks off it closes and holds its fire.
        var far = new CreatureHerd(47);
        far.Spawn(Flat, [crawler], new Vector3(0f, 64f, 0f), 1);
        if (far.Count == 1)
        {
            var distant = far.All[0];
            distant.Position = new Vector3(0.5f, 64f, 10.5f);
            var was = Vector3.Distance(distant.Position, player);

            for (var i = 0; i < 60; i++) far.Update(1f / 60f, Flat, player);

            if (far.TakeBlasts().Count != 0) faults.Add("a crawler ten blocks away blasted");
            if (Vector3.Distance(distant.Position, player) > was - 1f)
                faults.Add("a far crawler did not close on its target");
        }

        // ⛔ Control three: the fuse catches, the player runs past the abort range, and the world
        // stays whole. Half a second lit, then the target is twelve blocks away.
        var flee = new CreatureHerd(53);
        flee.Spawn(Flat, [crawler], new Vector3(0f, 64f, 0f), 1);
        if (flee.Count == 1)
        {
            var stalker = flee.All[0];
            stalker.Position = new Vector3(0.5f, 64f, 2.5f);

            for (var i = 0; i < 30; i++) flee.Update(1f / 60f, Flat, player);
            if (stalker.FuseFor <= 0f) faults.Add("half a second at arm's length lit nothing");

            var fled = new Vector3(0.5f, 64f, 14.5f);
            for (var i = 0; i < 120; i++)
            {
                // ⚠ Held still, so only the DISTANCE is being tested — left to walk, it chases
                // the fled player back into fuse range and relights, honestly, which is not the
                // claim this arm makes.
                stalker.Position = new Vector3(0.5f, 64f, 2.5f);
                flee.Update(1f / 60f, Flat, fled);
            }

            if (flee.TakeBlasts().Count != 0) faults.Add("a fuse blew after its target ran clear");
            if (stalker.FuseFor > 0f) faults.Add("a fuse kept burning twelve blocks from anybody");
        }

        return faults;
    }

    /// <summary>
    /// Checks a hopping kind actually hops: it leaves the ground, comes back to it, and gets
    /// somewhere doing it.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The walkers of every other arm are this check's control.</b> They already assert a
    /// flat-ground herd never leaves y 64 ("walked off its own floor"), so air on a walker is
    /// caught there; what no other arm can see is a hop that never happens — a slime demoted to a
    /// glide passes every walking claim there is, which is exactly why the peak height is asserted.
    /// </remarks>
    private static List<string> ValidateHopping()
    {
        var faults = new List<string>();

        static bool Flat(int x, int y, int z) => y < 64;

        var slime = new SpawnKind("slime", new Vector3(1f, 1f, 1f), Hostile: true, Move: CreatureMove.Hop);

        var herd = new CreatureHerd(37);
        herd.Spawn(Flat, [slime], new Vector3(0f, 64f, 0f), 3);
        if (herd.Count != 3) return ["the hop check's slimes found no room on an open plain"];

        var start = herd.All.Select(c => c.Position).ToList();
        var peak = 0f;

        for (var i = 0; i < 900; i++)
        {
            herd.Update(1f / 60f, Flat);
            foreach (var one in herd.All)
                peak = MathF.Max(peak, one.Position.Y - 64f);
        }

        // Fifteen seconds of a kind whose walk is hops. Half a block of air is under half its own
        // arc, so a working hop clears it easily and a glide never does.
        if (peak < 0.5f)
            faults.Add($"a hopping kind peaked {peak:F2} blocks off the floor in fifteen seconds");

        // ⚠ Against the arc's own figure as well: a launch that never comes down again would pass
        // "it left the ground" while drifting into the sky.
        if (peak > 2.5f)
            faults.Add($"a hop reached {peak:F2} blocks, which is not the arc its launch speed buys");

        foreach (var one in herd.All)
            if (one.Position.Y - 64f > 2f)
                faults.Add($"a hopper ended the run {one.Position.Y - 64f:F1} blocks up in the air");

        var moved = 0;
        for (var i = 0; i < herd.Count; i++)
            if (Vector3.Distance(herd.All[i].Position, start[i]) > 2f) moved++;

        if (moved == 0)
            faults.Add("not one of three hoppers went anywhere in fifteen seconds of hopping");

        // And it hurts nothing doing it: a hop's arc must never charge fall damage.
        foreach (var one in herd.All)
            if (one.Health < one.MaxHealth)
                faults.Add("a hopper hurt itself hopping on flat ground");

        return faults;
    }

    /// <summary>
    /// Checks a struck wolf turns on the striker, brings the pack near it, and bites when it
    /// arrives — and that a cow under the same blow still runs.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The far wolf and the cow are both controls.</b> "The pack joined" is also true of a
    /// rule that provokes every wolf in the world, so one stands outside the aggro range and must
    /// stay calm; and "it turned on the striker" is also true of a rule that made every beast
    /// fight back, so the same blow lands on a cow and must still be a flight.
    /// </remarks>
    private static List<string> ValidateRetaliation()
    {
        var faults = new List<string>();

        static bool Flat(int x, int y, int z) => y < 64;

        var wolves = new SpawnKind("wolf", new Vector3(0.9f, 1.1f, 1.4f));
        var herd = new CreatureHerd(23);
        herd.Spawn(Flat, [wolves], new Vector3(0f, 64f, 0f), 3);
        if (herd.Count != 3) return ["the pack for the retaliation check found no room to stand"];

        var struck = herd.All[0];
        var near = herd.All[1];
        var far = herd.All[2];
        struck.Position = new Vector3(0.5f, 64f, 0.5f);
        near.Position = new Vector3(4.5f, 64f, 0.5f);
        far.Position = new Vector3(40.5f, 64f, 0.5f);

        var player = new Vector3(0.5f, 64f, -6.5f);
        herd.Hurt(struck, 3, player);

        if (!struck.Provoked) faults.Add("a struck wolf did not take it personally");
        if (struck.FleeFor > 0f) faults.Add("a struck wolf ran like a cow");
        if (!near.Provoked) faults.Add("a packmate four blocks away did not join");
        if (far.Provoked) faults.Add("a wolf forty blocks away joined a fight it could not have seen");

        // It closes rather than wandering: the distance falls over a second of stepping.
        var before = Vector3.Distance(struck.Position, player);
        for (var i = 0; i < 60; i++) herd.Update(1f / 60f, Flat, player);
        var after = Vector3.Distance(struck.Position, player);

        if (after >= before - 0.5f)
            faults.Add($"a provoked wolf stood at {after:F1} blocks after a second, from {before:F1}");

        // And it bites when it gets there, for its own number.
        for (var i = 0; i < 300; i++) herd.Update(1f / 60f, Flat, player);
        var bites = herd.TakeAttacks();
        if (bites.Count == 0) faults.Add("a provoked wolf that reached the player never bit");

        foreach (var bite in bites)
            if (bite.HalfHearts != CreatureVitals.DamageFor("wolf"))
                faults.Add($"a wolf bit for {bite.HalfHearts} rather than its own {CreatureVitals.DamageFor("wolf")}");

        var cowHerd = new CreatureHerd(29);
        cowHerd.Spawn(Flat, [new SpawnKind("cow", new Vector3(0.75f, 1.56f, 1.50f))], new Vector3(0f, 64f, 0f), 1);
        if (cowHerd.Count == 1)
        {
            var cow = cowHerd.All[0];
            cowHerd.Hurt(cow, 3, player);
            if (cow.Provoked) faults.Add("a cow retaliated, which would make the wolf nothing special");
            if (cow.FleeFor <= 0f) faults.Add("a struck cow no longer runs");
        }

        return faults;
    }

    /// <summary>
    /// Checks a hostile comes at somebody, hits them when it arrives, and burns in the sun.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Every arm has a beast beside it, and that pairing is the whole check.</b> "It moved
    /// toward the player" is also true of an animal that happened to wander that way, and "it landed
    /// a blow" is also true of a table that hits regardless of range — so the same run puts a cow at
    /// the same distance and asserts it does neither, and the range gate is asked from too far away
    /// as well as from close to.
    /// </remarks>
    private static List<string> ValidateHunting()
    {
        var faults = new List<string>();

        static bool Flat(int x, int y, int z) => y < 64;

        var hunter = new SpawnKind("zombie", new Vector3(0.63f, 2.0f, 0.63f), Hostile: true);
        var grazer = new SpawnKind("cow", new Vector3(0.75f, 1.56f, 1.50f));

        var herd = new CreatureHerd(23);
        herd.Spawn(Flat, [hunter], new Vector3(0f, 64f, 0f), 1);
        herd.Spawn(Flat, [grazer], new Vector3(0f, 64f, 0f), 1);
        if (herd.Count != 2) return ["two creatures would not stand up for the hunting check"];

        var zombie = herd.All[0];
        var cow = herd.All[1];

        var player = new Vector3(0.5f, 64f, 0.5f);

        zombie.Position = new Vector3(0.5f, 64f, 10.5f);
        cow.Position = new Vector3(6.5f, 64f, 10.5f);
        zombie.Moving = cow.Moving = false;

        var zombieWas = Vector3.Distance(zombie.Position, player);
        var cowWas = Vector3.Distance(cow.Position, player);

        // Five seconds. It has ten blocks to cross at three a second, so a shorter run measures how
        // fast it walks rather than whether it swings — the first pass asked for two blows in three
        // seconds and got one, because the creature spent most of them still on its way over.
        for (var i = 0; i < 300; i++) herd.Update(1f / 60f, Flat, player);

        var zombieNow = Vector3.Distance(zombie.Position, player);
        if (zombieNow > zombieWas - 4f)
            faults.Add($"a hostile ten blocks off closed only {zombieWas - zombieNow:F1} blocks in three seconds");

        // ⚠ The beast arm. A herd that walked everything toward the player would pass the arm above.
        var cowNow = Vector3.Distance(cow.Position, player);
        if (cowNow < cowWas - 5f)
            faults.Add($"a cow closed {cowWas - cowNow:F1} blocks on the player, so beasts are hunting too");

        // It arrived, so it must have swung. Three seconds is at least two blows at 1.1 s apart.
        var landed = herd.TakeAttacks();
        if (landed.Count < 2)
            faults.Add($"a hostile that reached the player landed {landed.Count} blows in five seconds");

        if (landed.Count > 0 && landed[0].HalfHearts != CreatureVitals.DamageFor("zombie"))
            faults.Add($"a zombie's blow was worth {landed[0].HalfHearts}");

        if (landed.Any(a => a.Kind == "cow")) faults.Add("a cow attacked the player");

        // ⛔ THE RANGE CONTROL. Held at the far edge of its sight, it must chase and never connect —
        // without this arm a table that swung every step whatever the distance passes everything.
        var far = new CreatureHerd(29);
        far.Spawn(Flat, [hunter], new Vector3(0f, 64f, 0f), 1);
        var distant = far.All[0];
        distant.Position = new Vector3(0.5f, 64f, 14.5f);
        distant.Moving = false;

        for (var i = 0; i < 60; i++)
        {
            // Held where it is, so only the distance is being tested rather than how fast it walks.
            distant.Position = new Vector3(0.5f, 64f, 14.5f);
            far.Update(1f / 60f, Flat, player);
        }

        if (far.TakeAttacks().Count != 0)
            faults.Add("a hostile fourteen blocks away landed a blow");

        // And the sun. ⛔ Paired with the same creature under cover, or "it lost health" is equally
        // true of one that is simply taking damage from something else entirely.
        var noon = new CreatureHerd(31);
        noon.Spawn(Flat, [hunter, grazer], new Vector3(0f, 64f, 0f), 2);
        if (noon.Count != 2) return [.. faults, "nothing stood up for the daylight check"];

        var scorched = noon.All[0];
        var sheltered = noon.All[1];
        var before = (scorched.Health, sheltered.Health);

        // ⛔ THE WHOLE WORLD IS IN THE SUN, AND THE FIRST VERSION OF THIS LIT ONLY THE ZOMBIE'S
        // COLUMN — which meant the cow was safe because it was standing somewhere shaded rather than
        // because cows do not burn. Control-tested: with the kind filter deliberately deleted the
        // check went GREEN, because the case it claims to catch could not happen in the world it
        // built. Lighting everything is what makes the cow's survival a claim about the table.
        var scorchClock = 0f;
        for (var i = 0; i < 300; i++)
        {
            noon.Update(
                1f / 60f, Flat, null, (_, _, _) => true,
                scorch: creature =>
                {
                    scorchClock += 1f / 60f;
                    var due = (int)(scorchClock * ScorchRate);
                    if (due <= 0) return;
                    scorchClock -= due / ScorchRate;
                    noon.Hurt(creature, due, creature.Middle + new Vector3(0f, 4f, 0f));
                });
        }

        if (scorched.Health >= before.Item1)
            faults.Add("a zombie stood in five seconds of daylight and took no damage");

        if (sheltered.Health < before.Item2)
            faults.Add("a cow standing in the same sun was burned by it");

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
