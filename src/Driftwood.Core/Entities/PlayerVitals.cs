using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Items;
using Driftwood.Core.Physics;
using Driftwood.Core.World;

namespace Driftwood.Core.Entities;

/// <summary>What happened to the player this frame, for whatever wants to react to it.</summary>
/// <param name="Hurt">Health lost, in half-hearts.</param>
/// <param name="Died">True on the one frame health reached nothing.</param>
/// <param name="Drowned">True on the frames breath is being spent rather than recovered.</param>
/// <param name="Armoured">
/// Half-hearts of armour-protected damage thrown at the player this frame, <em>before</em> the
/// armour took its share. ⚠ Deliberately the blow rather than what landed: a full set turns most of
/// it aside, and wear paid out of what got through would make the best armour also the
/// longest-lasting by a wide margin — which is backwards. What wears a plate is being hit.
/// </param>
public readonly record struct VitalsEvent(int Hurt, bool Died, bool Drowned, int Armoured = 0);

/// <summary>
/// Health, the fall that takes it, the water that takes it, and the rest that gives it back.
/// </summary>
/// <remarks>
/// <para>Half-hearts rather than a float, because that is the unit the whole genre's feedback is
/// built in — a bar of ten hearts, each worth two. Keeping the model in the same unit the display
/// uses means there is never a rounding argument between what the player is told and what is true.
/// </para>
/// <para>Headless, and takes the world only to look at what the head is in. Every rule here is a
/// number a check can walk: how far a fall has to be before it costs anything, how much each block
/// past that costs, how long a breath lasts, how fast it comes back. None of that can be tuned by
/// eye and all of it is wrong in a way nobody notices until they die to it.</para>
/// </remarks>
public sealed class PlayerVitals
{
    /// <summary>Half-hearts at full health. Ten hearts, the genre's own.</summary>
    public const int MaxHealth = 20;

    /// <summary>Ticks of breath a full lungful is worth, at sixty a second.</summary>
    public const int MaxBreath = 300;

    /// <summary>
    /// Half-drumsticks at full. Ten of them, counted in the same unit the bar draws.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>The same shape as health on purpose</b> — twenty units drawn as ten icons — so the two
    /// rows either side of the crosshair are read the same way, and so the torn-fill the health bar
    /// uses works on this one without a second idea about what a partly-full icon means.
    /// </remarks>
    public const int MaxFood = 20;

    /// <summary>
    /// How fed a player has to be before health comes back on its own.
    /// </summary>
    /// <remarks>
    /// ⛳⛳ <b>THIS IS THE WHOLE POINT OF HUNGER, and without it the bar is a nuisance.</b> Eating no
    /// longer heals directly — <see cref="ItemType.Feeds"/> fills this instead — so the loop is: eat
    /// to stay fed, stay fed to mend. A hunger bar that only threatens starvation is a chore with a
    /// timer; one that gates healing is the reason to keep a farm.
    /// ⛔ <b>Fourteen of twenty, and eighteen was WRONG — the existing rest check caught it.</b>
    /// Mending spends food, so the gate and the cost together decide how much a full belly is worth:
    /// at eighteen there were two units of room, mending stopped after a single heart, and half a
    /// minute of rest left a hurt player at 16 of 20 with no way to finish. The reference gets away
    /// with a tight gate because it burns a hidden saturation buffer first; we have no such buffer,
    /// so the gate has to leave the room itself. Fourteen buys twelve half-hearts on a full bar.
    /// </remarks>
    public const int WellFed = 14;

    /// <summary>
    /// Below this, health drains rather than returns.
    /// </summary>
    /// <remarks>
    /// ⚠ Zero, so being merely hungry costs nothing but the healing. Starving is a state a player
    /// arrives at, not a slope they are on from the first minute.
    /// </remarks>
    public const int Starving = 0;

    /// <summary>
    /// Health starvation will not take a player past.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Two half-hearts, and it does NOT kill.</b> Hunger is the one thing in this game that can
    /// run a player down while they are doing nothing wrong and looking at something else — a bar
    /// they have never been taught to watch. Starving to death the first time is a lesson delivered
    /// by taking the session away. It leaves them at one heart, which is unmistakable.
    /// </remarks>
    public const int StarvationFloor = 2;

    /// <summary>
    /// Blocks of falling that cost nothing.
    /// </summary>
    /// <remarks>
    /// Three, which is what makes a two-block drop free and a four-block drop a decision. It is also
    /// exactly the height a player can build up to and step off without thinking, and the whole feel
    /// of moving around a voxel world rests on that being generous enough to not punish ordinary
    /// climbing.
    /// </remarks>
    public const float SafeFall = 3f;

    /// <summary>Half-hearts per block fallen past <see cref="SafeFall"/>.</summary>
    public const float FallDamagePerBlock = 1f;

    /// <summary>Half-hearts lost per second of drowning, once breath is gone.</summary>
    private const float DrownRate = 2f;

    /// <summary>Seconds unhurt before health starts coming back.</summary>
    private const float RegenerationDelay = 5f;

    /// <summary>Seconds a half-heart takes to return.</summary>
    private const float RegenerationPeriod = 1.6f;

    /// <summary>
    /// Effort a half-drumstick is worth.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Hunger is spent by DOING THINGS, not by the clock.</b> A timer makes eating a chore that
    /// arrives whatever the player was up to; effort makes a long dig or a fight cost something, and
    /// makes standing still in a shelter nearly free. It is also the only version a check can state:
    /// walking a hundred blocks costs this much, and that is a number rather than a feeling.
    /// ⚠ Four, against the costs below, puts a quiet hour at roughly half a bar.
    /// </remarks>
    private const float EffortPerFood = 4f;

    /// <summary>
    /// Effort one block of ground covered costs.
    /// </summary>
    /// <remarks>
    /// ⚠ One hundredth, so four hundred blocks of walking is one half-drumstick and a full bar is
    /// eight thousand blocks of it. Walking is deliberately the cheapest thing a player does —
    /// mending and fighting are what actually empty the bar.
    /// </remarks>
    private const float EffortPerBlockWalked = 0.01f;

    /// <summary>Effort one block broken costs, and one blow taken.</summary>
    public const float EffortPerBlockMined = 0.025f;
    public const float EffortPerWound = 0.1f;

    /// <summary>Effort a half-heart of regeneration costs, on top of everything else.</summary>
    /// <remarks>
    /// ⚠ <b>Mending is by far the expensive thing</b> — two hundred blocks of walking buys one
    /// half-heart of it — which is what makes food the resource a fight costs rather than the fight
    /// costing only health. ⛔ But not so expensive that a belly cannot finish the job: at six, one
    /// heart of healing dropped a full player under <see cref="WellFed"/> and mending stopped
    /// mid-way. Two spends five of the bar to heal from half dead to full.
    /// </remarks>
    private const float EffortPerHeal = 2f;

    /// <summary>Seconds a half-heart of starvation takes.</summary>
    private const float StarvationPeriod = 4f;

    private readonly bool[] _drownsIn;

    private float _sinceHurt;
    private float _regenerating;
    private float _drowningFor;
    private bool _wasOnGround = true;
    private float _fallInAir;
    /// <summary>
    /// Effort banked toward the next half-drumstick.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>A double, and the check is what forced it.</b> Effort arrives sixty times a second for
    /// hours, and in single precision the same total paid in sixty pieces comes to very slightly
    /// LESS than the same total paid at once — so <c>_effort >= EffortPerFood</c> never trips and the
    /// bar does not move. It read as hunger simply not existing, which is exactly what a rate that
    /// rounds to nothing looks like from the outside. The same drift is why an accumulator summed
    /// across a session should not be a float in the first place.
    /// </remarks>
    private double _effort;

    private float _starvingFor;

    /// <summary>Half-hearts remaining, 0 to <see cref="MaxHealth"/>.</summary>
    public int Health { get; private set; } = MaxHealth;

    /// <summary>Half-drumsticks remaining, 0 to <see cref="MaxFood"/>.</summary>
    public int Food { get; private set; } = MaxFood;

    /// <summary>True when health is coming back on its own rather than draining.</summary>
    public bool Mending => Food >= WellFed && Health < MaxHealth;

    /// <summary>True when there is nothing left to burn and health is going instead.</summary>
    public bool StarvingNow => Food <= Starving && Health > StarvationFloor;

    /// <summary>Ticks of breath remaining, 0 to <see cref="MaxBreath"/>.</summary>
    public int Breath { get; private set; } = MaxBreath;

    /// <summary>True while the head is inside something that has to be breathed.</summary>
    public bool Submerged { get; private set; }

    public bool Alive => Health > 0;

    /// <summary>What a head is inside, per block id, for the two things that can happen to it.</summary>
    private readonly bool[] _burnsIn;

    /// <param name="registry">
    /// Read for which blocks a head cannot breathe in, and which ones burn it.
    /// </param>
    /// <remarks>
    /// ⛔ <b>Asked of the block outright, and it used to be derived — <c>!Solid &amp;&amp; !Opaque
    /// &amp;&amp; LightAttenuation &gt; 0</c>.</b> That picked out water exactly, for as long as water
    /// was the only fluid in the game. <b>Lava satisfies all three of them</b>, so the day it was
    /// registered a player would have held their breath in molten rock and drowned in it after five
    /// seconds, and there is not a check anywhere that would have looked wrong. It is
    /// <see cref="BlockType.Fluid"/> now, which is a fact a block states rather than one three other
    /// facts happen to imply.
    /// </remarks>
    public PlayerVitals(BlockRegistry registry)
    {
        _drownsIn = new bool[registry.Count];
        _burnsIn = new bool[registry.Count];

        for (var id = 1; id < registry.Count; id++)
        {
            var type = registry[(ushort)id];
            _drownsIn[id] = type.Fluid == FluidKind.Water;
            _burnsIn[id] = type.Fluid == FluidKind.Lava;
        }
    }

    /// <summary>True when the given block would burn something standing in it.</summary>
    public bool Burns(BlockId block) => _burnsIn[block.Value];

    /// <summary>Half-hearts a second while actually in it. Four seconds to kill from full.</summary>
    private const float LavaRate = 2f;

    private const int LavaDamage = 2;

    /// <summary>Half-hearts a second while still alight after getting out.</summary>
    private const float BurnRate = 2f;

    /// <summary>How long the burning lasts once you are clear of it.</summary>
    /// <remarks>
    /// Long enough that a dash across a flow costs something, short enough that a dash is survivable
    /// — which is the whole design of the block. Water puts it out at once, which is what makes
    /// carrying a bucket into the deep a plan rather than an errand.
    /// </remarks>
    private const float BurnAfter = 4f;

    private float _lavaFor;
    private float _burnTick;
    private float _burningFor;

    /// <summary>True while still alight after leaving the fire.</summary>
    public bool Burning => _burningFor > 0f;

    /// <summary>True when any part of the body is inside something that burns.</summary>
    private bool TouchesBurning(VoxelWorld world, PlayerBody body)
    {
        var feet = body.Position;
        var half = PlayerBody.Width * 0.5f;
        var top = feet.Y + body.CurrentHeight;

        var minX = (int)MathF.Floor(feet.X - half);
        var maxX = (int)MathF.Floor(feet.X + half);
        var minZ = (int)MathF.Floor(feet.Z - half);
        var maxZ = (int)MathF.Floor(feet.Z + half);
        var minY = (int)MathF.Floor(feet.Y);
        var maxY = (int)MathF.Floor(top);

        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        for (var x = minX; x <= maxX; x++)
            if (_burnsIn[world.GetBlock(x, y, z).Value]) return true;

        return false;
    }

    private static BlockId FeetBlock(VoxelWorld world, PlayerBody body) => world.GetBlock(
        (int)MathF.Floor(body.Position.X),
        (int)MathF.Floor(body.Position.Y),
        (int)MathF.Floor(body.Position.Z));

    /// <summary>Full health, full breath, nothing pending. What a respawn does.</summary>
    public void Restore()
    {
        Health = MaxHealth;
        Food = MaxFood;
        Breath = MaxBreath;
        _sinceHurt = 0f;
        _regenerating = 0f;
        _drowningFor = 0f;
        _fallInAir = 0f;
        _effort = 0f;
        _starvingFor = 0f;
        _wasOnGround = true;
    }

    /// <summary>
    /// Puts health and breath back where a save left them, and nothing else pending.
    /// </summary>
    /// <remarks>
    /// Clamped rather than trusted. The numbers come off a file, and a health of minus one or of
    /// four hundred is the kind of thing a corrupted save says — neither should be a state the
    /// game can be put into by opening one.
    /// </remarks>
    public void Restore(int health, int breath, int food = MaxFood)
    {
        Restore();
        Health = Math.Clamp(health, 0, MaxHealth);
        Breath = Math.Clamp(breath, 0, MaxBreath);

        // ⚠ Defaulted to full rather than to zero, because a save written before hunger existed
        // carries no food at all — and reading that as "starving" would open every existing world
        // with the health bar already draining.
        Food = Math.Clamp(food, 0, MaxFood);
    }

    /// <summary>
    /// Points of armour currently worn, 0 to <see cref="Armour.MaxPoints"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Handed in rather than worked out.</b> Vitals knows about a body and the blocks round it
    /// and nothing about pockets, and giving it an <see cref="Equipment"/> to read would make every
    /// check that hurts a player have to build an inventory first. The host sets it when what is
    /// worn changes, which is a few times a session.
    /// </remarks>
    public int ArmourPoints { get; set; }

    /// <summary>
    /// True while a shield is up: something is in the other hand and the player is holding it there.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>The cost of holding it is paid elsewhere and is deliberately not a number here.</b> A
    /// raised shield stops the player mining and stops them swinging, which is a cost a player feels
    /// immediately and a check can state outright — where a movement penalty would be a multiplier
    /// nobody notices and every check would have to be calibrated against.
    /// </remarks>
    public bool ShieldRaised { get; set; }

    /// <summary>
    /// What the thing in the other hand turns aside, when it is up. Zero for anything but a shield.
    /// </summary>
    /// <remarks>
    /// ⚠ Handed in beside <see cref="ArmourPoints"/> and for the same reason: vitals knows about a
    /// body and the blocks round it, and giving it an <see cref="Equipment"/> would make every check
    /// that hurts a player build an inventory first.
    /// </remarks>
    public float ShieldShare { get; set; }

    /// <summary>Armour-protected damage thrown this frame, before the armour's share came off.</summary>
    private int _armoured;

    /// <summary>And how much of that arrived while the shield was up, which is what wears one.</summary>
    private int _shielded;

    /// <summary>
    /// Takes damage directly, for anything that is not a fall or a lungful of water.
    /// </summary>
    /// <param name="armoured">
    /// False for the handful of things a plate cannot help with. ⛔ Drowning is the one that
    /// matters: armour that made a lungful of water survivable would turn the single hazard with no
    /// counterplay into one solved by wearing more of something, and diving in a full set would be
    /// safer than diving in a shirt.
    /// </param>
    public void Hurt(int halfHearts, bool armoured = true)
    {
        if (halfHearts <= 0 || !Alive) return;

        if (armoured)
        {
            _armoured += halfHearts;
            if (ShieldRaised) _shielded += halfHearts;
            halfHearts = Armour.Survive(halfHearts, ArmourPoints, ShieldRaised ? ShieldShare : 0f);
        }

        Health = Math.Max(0, Health - halfHearts);
        _sinceHurt = 0f;
        _regenerating = 0f;
    }

    /// <summary>
    /// Reads and clears what the armour and the shield have been asked to stand up to.
    /// </summary>
    /// <remarks>
    /// ⚠ Cleared by reading it, because the blow that starts a frame and the blow that arrives
    /// mid-frame — a creature's swing goes through <see cref="Hurt"/> directly — must both be paid
    /// for exactly once. A field the host reads and forgets to reset is a set of armour that wears
    /// through in a second.
    /// </remarks>
    public (int Armour, int Shield) TakeWear()
    {
        var was = (_armoured, _shielded);
        _armoured = 0;
        _shielded = 0;
        return was;
    }

    /// <summary>
    /// Puts health back directly, and says how much actually landed.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>It answers with what it took, not with what it was offered.</b> Eating at full health has
    /// to be refusable, and the caller cannot work that out for itself without a second copy of the
    /// clamp — which is how a player ends up spending a cooked steak on nothing. A dead one heals
    /// nothing: coming back is what a respawn is for.
    /// </remarks>
    public int Heal(int halfHearts)
    {
        if (halfHearts <= 0 || !Alive) return 0;

        var before = Health;
        Health = Math.Min(MaxHealth, Health + halfHearts);
        return Health - before;
    }

    /// <summary>
    /// Advances one frame against a body and the world its head is in.
    /// </summary>
    /// <remarks>
    /// The fall has to be caught while the body is still in the air. <see cref="PlayerBody"/> clears
    /// its own distance the instant it lands, so a reader that waits for the landing frame to ask
    /// how far it fell is told zero, every time, for every fall.
    /// </remarks>
    public VitalsEvent Update(VoxelWorld world, PlayerBody body, float dt)
    {
        if (!Alive) return new VitalsEvent(0, false, false);

        var before = Health;

        var onGround = body.OnGround;
        if (!onGround) _fallInAir = MathF.Max(_fallInAir, body.FallDistance);

        if (onGround && !_wasOnGround && _fallInAir > SafeFall)
            Hurt((int)MathF.Round((_fallInAir - SafeFall) * FallDamagePerBlock));

        if (onGround) _fallInAir = 0f;
        _wasOnGround = onGround;

        // The head, not the feet. Standing chest-deep is not drowning, and the difference is the
        // whole reason wading across a river is a thing a player can choose to do.
        var eye = body.EyePosition;
        var head = world.GetBlock(
            (int)MathF.Floor(eye.X), (int)MathF.Floor(eye.Y), (int)MathF.Floor(eye.Z));

        Submerged = _drownsIn[head.Value];

        // ⛳ Contact damage, which did not exist at all: falling and drowning were the only two ways
        // to be hurt by the world, and lava with neither is scenery. Asked of the whole body rather
        // than of the head — stepping into it is what kills you, not swimming in it — and it keeps
        // burning for a few seconds after you get out, which is what makes a dash across a flow a
        // decision rather than a free move. Water puts it out immediately.
        var touchingLava = TouchesBurning(world, body);

        if (touchingLava) _burningFor = BurnAfter;
        else if (Submerged || _drownsIn[FeetBlock(world, body).Value]) _burningFor = 0f;
        else _burningFor = MathF.Max(0f, _burningFor - dt);

        if (touchingLava)
        {
            _lavaFor += dt;
            while (_lavaFor >= 1f / LavaRate)
            {
                _lavaFor -= 1f / LavaRate;
                Hurt(LavaDamage);
            }
        }
        else
        {
            _lavaFor = 0f;

            if (_burningFor > 0f)
            {
                _burnTick += dt;
                while (_burnTick >= 1f / BurnRate)
                {
                    _burnTick -= 1f / BurnRate;
                    Hurt(1);
                }
            }
            else
            {
                _burnTick = 0f;
            }
        }

        if (Submerged)
        {
            Breath = Math.Max(0, Breath - (int)MathF.Ceiling(dt * 60f));
            if (Breath == 0)
            {
                _drowningFor += dt;
                while (_drowningFor >= 1f / DrownRate)
                {
                    _drowningFor -= 1f / DrownRate;

                    // ⛔ The one thing armour must not help with. See Hurt: making a lungful of
                    // water survivable by wearing more of something turns the single hazard with no
                    // counterplay into one solved by shopping, and would make diving in a full set
                    // safer than diving in a shirt.
                    Hurt(1, armoured: false);
                }
            }
        }
        else
        {
            // Breath comes back several times faster than it goes. Surfacing has to feel like
            // relief rather than like the start of another timer.
            Breath = Math.Min(MaxBreath, Breath + (int)MathF.Ceiling(dt * 60f * 4f));
            _drowningFor = 0f;
        }

        // ⛳ Moving costs, taken from how far the body actually went rather than from which keys are
        // down — so walking into a wall costs nothing, and sprinting costs more without a multiplier
        // because it covers more ground in the same second.
        Spend(body.TakeDistanceWalked() * EffortPerBlockWalked);

        var hurt = before - Health;
        if (hurt > 0)
        {
            Spend(hurt * EffortPerWound);
            return new VitalsEvent(hurt, !Alive, Submerged && Breath == 0);
        }

        // ── Being fed, or not ───────────────────────────────────────────────────────────────────
        //
        // ⛔ REGENERATION IS GATED ON FOOD NOW, and that is the change that makes hunger a system
        // rather than a second bar. Eating used to put health back directly; it fills the food bar,
        // and the food bar is what mends. ⚠ Mending SPENDS food, so a player who heals from half is
        // hungry afterwards — which is the cost of a fight actually being paid.
        _sinceHurt += dt;

        if (Food <= Starving)
        {
            // Nothing left to burn. Health goes instead, down to a floor rather than to death.
            _regenerating = 0f;

            if (Health > StarvationFloor)
            {
                _starvingFor += dt;
                while (_starvingFor >= StarvationPeriod && Health > StarvationFloor)
                {
                    _starvingFor -= StarvationPeriod;
                    Health--;
                }
            }

            return new VitalsEvent(0, false, Submerged && Breath == 0);
        }

        _starvingFor = 0f;

        if (Food >= WellFed && Health < MaxHealth && _sinceHurt >= RegenerationDelay)
        {
            _regenerating += dt;
            while (_regenerating >= RegenerationPeriod && Health < MaxHealth)
            {
                _regenerating -= RegenerationPeriod;
                Health++;
                Spend(EffortPerHeal);
            }
        }

        return new VitalsEvent(0, false, Submerged && Breath == 0);
    }

    /// <summary>
    /// Puts effort in, and takes a half-drumstick off for every <see cref="EffortPerFood"/> of it.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The remainder is KEPT rather than dropped.</b> Effort arrives a fraction at a time, sixty
    /// times a second; rounding each frame's worth to a whole unit would either round everything to
    /// zero and make hunger free, or round everything up and empty the bar in seconds.
    /// </remarks>
    public void Spend(float effort)
    {
        if (effort <= 0f || Food <= 0) return;

        _effort += effort;

        while (_effort >= EffortPerFood && Food > 0)
        {
            _effort -= EffortPerFood;
            Food--;
        }
    }

    /// <summary>
    /// Eats something worth this many half-drumsticks, and answers how many it actually took.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Answers zero when the bar is already full, and the caller is what refuses the meal.</b>
    /// Eating a whole roast at nineteen of twenty and getting one unit of it is how a player loses
    /// food without noticing; refusing outright means it is still in their pocket for later.
    /// ⚠ It also clears the part-spent effort, so a meal is a clean start rather than something that
    /// is a little short because of the last few steps before it.
    /// </remarks>
    public int Eat(int halfDrumsticks)
    {
        if (halfDrumsticks <= 0 || Food >= MaxFood) return 0;

        var before = Food;
        Food = Math.Min(MaxFood, Food + halfDrumsticks);
        _effort = 0f;

        return Food - before;
    }

    /// <summary>
    /// Hunger, walked end to end: it drains, it gates mending, it starves, and it stops.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>Every arm here has a control beside it, because hunger fails SILENTLY.</b> A drain
    /// rate that rounds to nothing gives a bar that never moves and looks exactly like a bar that is
    /// working; one that gates healing on the wrong side of a comparison gives a player who mends
    /// while starving and starves while fed. Neither shows up in a screenshot, and both are the sort
    /// of thing a player discovers by dying to it three hours in.</para>
    /// <para>⛳ <b>The starvation floor is the one to gate hardest.</b> It is the only rule here whose
    /// failure is losing somebody's session, and "it stopped at the floor" is not the same claim as
    /// "it went down" — a build that never starves at all satisfies the second.</para>
    /// </remarks>
    public static List<string> Validate(BlockRegistry registry, out string detail)
    {
        var faults = new List<string>();

        // ── Effort drains the bar, and by a stated amount ───────────────────────────────────────
        var walker = new PlayerVitals(registry);
        walker.Spend(EffortPerFood * 4f);

        if (walker.Food != MaxFood - 4)
            faults.Add($"four drumsticks of effort took {MaxFood - walker.Food} half-drumsticks");

        // ⛔ THE CONTROL: a rate that rounds to nothing looks identical from the bar. A quarter of a
        // unit of effort, sixty times, has to move it exactly one — not zero and not sixty.
        var trickle = new PlayerVitals(registry);
        for (var i = 0; i < 60; i++) trickle.Spend(EffortPerFood / 60f);

        if (trickle.Food != MaxFood - 1)
            faults.Add($"a drumstick of effort paid in sixty pieces took {MaxFood - trickle.Food}, "
                     + "so the remainder between frames is being dropped or double-counted");

        // ── Eating fills, and is refused rather than wasted at a full bar ───────────────────────
        var eater = new PlayerVitals(registry);
        if (eater.Eat(6) != 0) faults.Add("a full player ate something and it went nowhere");

        eater.Spend(EffortPerFood * 10f);
        if (eater.Eat(6) != 6) faults.Add("a half-empty player could not eat a whole meal");

        // ⚠ A meal bigger than the room left has to report only what it actually put in, or the
        // caller spends a roast and is told it landed whole.
        var room = MaxFood - eater.Food;
        var took = eater.Eat(999);

        if (took != room) faults.Add($"a huge meal reported {took} into {room} of room");
        if (eater.Food != MaxFood) faults.Add($"eating past full left {eater.Food} of {MaxFood}");

        // ── Being fed is what mends, and being hungry is what does not ──────────────────────────
        var fed = new PlayerVitals(registry);
        fed.Hurt(10);
        var mendedFed = RunQuiet(fed, 20f);

        if (mendedFed <= 0)
            faults.Add("a well-fed player at half health did not mend at all in twenty seconds");

        // ⛔ THE CONTROL, and without it the arm above passes a build that mends unconditionally.
        var starved = new PlayerVitals(registry);
        starved.Hurt(10);
        starved.Spend(EffortPerFood * MaxFood);

        if (starved.Food != 0) faults.Add("a player cannot be emptied of food at all");

        var mendedStarving = RunQuiet(starved, 20f);
        if (mendedStarving > 0)
            faults.Add($"a starving player mended {mendedStarving} half-hearts, so food does not "
                     + "gate healing and the bar is decoration");

        // ── Starving costs health, and stops at the floor rather than killing ───────────────────
        if (starved.Health >= MaxHealth - 10)
            faults.Add("twenty seconds of starving cost no health at all");

        // Long enough to kill several times over if there were no floor.
        var dying = new PlayerVitals(registry);
        dying.Spend(EffortPerFood * MaxFood);
        RunQuiet(dying, StarvationPeriod * (MaxHealth + 10));

        if (dying.Health != StarvationFloor)
            faults.Add($"starving ran a player to {dying.Health} half-hearts rather than stopping "
                     + $"at {StarvationFloor}");

        if (!dying.Alive)
            faults.Add("starving killed a player, and it is the one thing in the game that must not");

        detail = $"{EffortPerFood} effort a half-drumstick: a block walked costs "
               + $"{EffortPerBlockWalked}, a block mined {EffortPerBlockMined}, a half-heart mended "
               + $"{EffortPerHeal}. Mending needs {WellFed} of {MaxFood}; at 0 health falls to "
               + $"{StarvationFloor} and stops there";

        return faults;

        // ⛔ THE REAL Update, AGAINST A REAL BODY ON REAL GROUND — not a copy of the rule.
        //
        // Eight fluid checks in this project were green while the feature did nothing in the game,
        // because every one of them drove the engine in a box it had filled itself. A hunger check
        // that re-implemented the drain and the gate here would pass whatever Update actually did,
        // which is the entire thing worth knowing. Standing still on a floor of stone costs no
        // walking, so what runs is exactly the food clock and nothing else.
        int RunQuiet(PlayerVitals vitals, float seconds)
        {
            var world = new VoxelWorld(registry);
            var stone = registry.ByName("stone").Id;

            for (var z = -2; z <= 2; z++)
            for (var x = -2; x <= 2; x++)
                world.SetBlock(x, 59, z, stone);

            var body = new PlayerBody(registry);
            body.Teleport(new Vector3(0.5f, 60f, 0.5f));

            var before = vitals.Health;
            var step = 1f / 60f;

            for (var t = 0f; t < seconds; t += step)
            {
                body.Step(world, step, Vector3.Zero, false, false, false);
                vitals.Update(world, body, step);
            }

            return vitals.Health - before;
        }
    }
}
