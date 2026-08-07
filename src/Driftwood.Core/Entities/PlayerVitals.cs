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

    private readonly bool[] _drownsIn;

    private float _sinceHurt;
    private float _regenerating;
    private float _drowningFor;
    private bool _wasOnGround = true;
    private float _fallInAir;

    /// <summary>Half-hearts remaining, 0 to <see cref="MaxHealth"/>.</summary>
    public int Health { get; private set; } = MaxHealth;

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
        Breath = MaxBreath;
        _sinceHurt = 0f;
        _regenerating = 0f;
        _drowningFor = 0f;
        _fallInAir = 0f;
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
    public void Restore(int health, int breath)
    {
        Restore();
        Health = Math.Clamp(health, 0, MaxHealth);
        Breath = Math.Clamp(breath, 0, MaxBreath);
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
            halfHearts = Armour.Survive(halfHearts, ArmourPoints, ShieldRaised);
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

        var hurt = before - Health;
        if (hurt > 0) return new VitalsEvent(hurt, !Alive, Submerged && Breath == 0);

        // Nothing hurt this frame, so rest counts toward getting some of it back.
        _sinceHurt += dt;
        if (Health < MaxHealth && _sinceHurt >= RegenerationDelay)
        {
            _regenerating += dt;
            while (_regenerating >= RegenerationPeriod && Health < MaxHealth)
            {
                _regenerating -= RegenerationPeriod;
                Health++;
            }
        }

        return new VitalsEvent(0, false, Submerged && Breath == 0);
    }
}
