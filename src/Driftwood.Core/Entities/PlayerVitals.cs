using Driftwood.Core.Blocks;
using Driftwood.Core.Physics;
using Driftwood.Core.World;

namespace Driftwood.Core.Entities;

/// <summary>What happened to the player this frame, for whatever wants to react to it.</summary>
/// <param name="Hurt">Health lost, in half-hearts.</param>
/// <param name="Died">True on the one frame health reached nothing.</param>
/// <param name="Drowned">True on the frames breath is being spent rather than recovered.</param>
public readonly record struct VitalsEvent(int Hurt, bool Died, bool Drowned);

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

    /// <param name="registry">
    /// Read for which blocks a head cannot breathe in: anything that stops neither movement nor
    /// light but is not air. Water today, and whatever else fills a space later.
    /// </param>
    public PlayerVitals(BlockRegistry registry)
    {
        _drownsIn = new bool[registry.Count];
        for (var id = 1; id < registry.Count; id++)
        {
            var type = registry[(ushort)id];
            _drownsIn[id] = !type.Solid && !type.Opaque && type.LightAttenuation > 0;
        }
    }

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

    /// <summary>Takes damage directly, for anything that is not a fall or a lungful of water.</summary>
    public void Hurt(int halfHearts)
    {
        if (halfHearts <= 0 || !Alive) return;
        Health = Math.Max(0, Health - halfHearts);
        _sinceHurt = 0f;
        _regenerating = 0f;
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

        if (Submerged)
        {
            Breath = Math.Max(0, Breath - (int)MathF.Ceiling(dt * 60f));
            if (Breath == 0)
            {
                _drowningFor += dt;
                while (_drowningFor >= 1f / DrownRate)
                {
                    _drowningFor -= 1f / DrownRate;
                    Hurt(1);
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
