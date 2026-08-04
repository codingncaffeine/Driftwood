using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.World;

namespace Driftwood.Core.Physics;

/// <summary>
/// A player-sized box that falls, walks, jumps and refuses to go through blocks.
/// </summary>
/// <remarks>
/// <para>Collision is resolved one axis at a time rather than by a true swept test. It is the
/// standard approach for a voxel world and it is standard for a reason: sliding along a wall falls
/// out of it for free, and a corner never wedges you the way a single earliest-time-of-impact
/// solution does. The cost is that it will tunnel through a block if a step is long enough to clear
/// it, which is why a frame's movement is cut into substeps no longer than a third of a block.</para>
/// <para>Lives in Core, with no reference to input or to a camera, so the audit can drop a body off
/// a cliff and check where it lands without opening a window. Physics that can only be tested by
/// playing the game is physics that gets tested once.</para>
/// </remarks>
public sealed class PlayerBody
{
    /// <summary>Footprint. Narrower than a block, so a one-block gap is passable.</summary>
    public const float Width = 0.6f;

    public const float Height = 1.8f;

    /// <summary>Eye offset from the feet. Slightly below the top of the head, as a head is.</summary>
    public const float EyeHeight = 1.62f;

    /// <summary>Crouching lowers the eye and the box with it.</summary>
    public const float SneakHeight = 1.5f;
    public const float SneakEyeHeight = 1.32f;

    /// <summary>
    /// Blocks per second squared. Chosen with the jump so that a jump clears exactly one block
    /// and no more: the whole world is built out of one-block steps, and being able to hop two
    /// would make terrain stop meaning anything.
    /// </summary>
    public const float Gravity = 32f;

    public const float JumpSpeed = 8.6f;
    public const float TerminalSpeed = 78f;

    public const float WalkSpeed = 4.3f;
    public const float SprintSpeed = 5.7f;
    public const float SneakSpeed = 1.3f;

    /// <summary>Ground acceleration. High enough that walking feels immediate, not like skating.</summary>
    public const float GroundAcceleration = 60f;
    public const float AirAcceleration = 12f;
    public const float GroundFriction = 14f;

    /// <summary>
    /// How high a step the body climbs without jumping. Two thirds of a block: enough for the
    /// half-height blocks that arrive with building, not enough to walk up terrain, which stays
    /// something you have to jump.
    /// </summary>
    public const float StepHeight = 0.6f;

    /// <summary>Movement per substep. Shorter than the thinnest thing that can be collided with.</summary>
    private const float MaxSubstep = 0.3f;

    private readonly bool[] _solid;

    /// <summary>Centre of the feet.</summary>
    public Vector3 Position;
    public Vector3 Velocity;

    public bool OnGround { get; private set; }
    public bool Sneaking { get; private set; }

    /// <summary>Fall distance since last touching the ground, for fall damage at P3-7.</summary>
    public float FallDistance { get; private set; }

    public PlayerBody(BlockRegistry registry) => _solid = registry.BuildSolidTable();

    public float CurrentHeight => Sneaking ? SneakHeight : Height;
    public float CurrentEyeHeight => Sneaking ? SneakEyeHeight : EyeHeight;
    public Vector3 EyePosition => Position + new Vector3(0f, CurrentEyeHeight, 0f);

    /// <summary>
    /// Advances one frame. <paramref name="wish"/> is the desired horizontal direction in world
    /// space; its length is treated as the throttle and clamped to one.
    /// </summary>
    public void Step(VoxelWorld world, float dt, Vector3 wish, bool jump, bool sneak, bool sprint)
    {
        if (dt <= 0f) return;
        dt = MathF.Min(dt, 0.1f);   // a stalled frame must not teleport the player through a wall

        Sneaking = sneak;

        var wishLength = new Vector2(wish.X, wish.Z).Length();
        if (wishLength > 1f) wish /= wishLength;

        var target = sneak ? SneakSpeed : sprint ? SprintSpeed : WalkSpeed;
        var accel = OnGround ? GroundAcceleration : AirAcceleration;

        var desired = new Vector3(wish.X, 0f, wish.Z) * target;
        var horizontal = new Vector3(Velocity.X, 0f, Velocity.Z);

        // Accelerate toward the desired velocity rather than assigning it, so stopping and turning
        // have weight. Friction only applies on the ground; in the air you keep what you had, which
        // is what makes a jump commit you to its arc.
        var delta = desired - horizontal;
        var deltaLength = delta.Length();
        if (deltaLength > 1e-5f)
        {
            var change = MathF.Min(deltaLength, accel * dt);
            horizontal += delta / deltaLength * change;
        }

        if (OnGround && wishLength < 1e-3f)
        {
            var speed = horizontal.Length();
            var drop = GroundFriction * dt;
            horizontal = speed <= drop ? Vector3.Zero : horizontal * ((speed - drop) / speed);
        }

        Velocity = new Vector3(horizontal.X, Velocity.Y, horizontal.Z);

        if (jump && OnGround)
        {
            Velocity.Y = JumpSpeed;
            OnGround = false;
        }

        Velocity.Y = MathF.Max(Velocity.Y - Gravity * dt, -TerminalSpeed);

        MoveWithCollisions(world, Velocity * dt);
    }

    /// <summary>Places the body somewhere without any collision resolution.</summary>
    public void Teleport(Vector3 position)
    {
        Position = position;
        Velocity = Vector3.Zero;
        OnGround = false;
        FallDistance = 0f;
    }

    private void MoveWithCollisions(VoxelWorld world, Vector3 motion)
    {
        var steps = 1 + (int)(motion.Length() / MaxSubstep);
        var slice = motion / steps;

        for (var i = 0; i < steps; i++) MoveSlice(world, slice);
    }

    private void MoveSlice(VoxelWorld world, Vector3 motion)
    {
        var wasOnGround = OnGround;
        var before = Position;

        // X and Z before Y. Resolving the vertical first would let a body that is falling past a
        // ledge land on top of it when it should have slid down the face.
        if (motion.X != 0f && !TryAxis(world, new Vector3(motion.X, 0f, 0f), wasOnGround))
            Velocity.X = 0f;

        if (motion.Z != 0f && !TryAxis(world, new Vector3(0f, 0f, motion.Z), wasOnGround))
            Velocity.Z = 0f;

        // Crouching at a ledge stops you walking off it. Checked here, between the horizontal move
        // and the vertical one, because by the time gravity has been applied the body is already
        // falling and no longer reports standing on anything — which is how the first version of
        // this silently never fired at all.
        if (Sneaking && wasOnGround) HoldTheLedge(world, before);

        if (motion.Y != 0f)
        {
            if (Collides(world, Position + new Vector3(0f, motion.Y, 0f)))
            {
                // Landed or hit the ceiling. Sit exactly against the surface rather than a hair
                // away, or the next frame starts fractionally inside it.
                Position.Y = motion.Y < 0f
                    ? MathF.Floor(Position.Y + motion.Y) + 1f
                    : MathF.Ceiling(Position.Y + motion.Y + CurrentHeight) - 1f - CurrentHeight;

                if (motion.Y < 0f)
                {
                    OnGround = true;
                    FallDistance = 0f;
                }

                Velocity.Y = 0f;
            }
            else
            {
                Position.Y += motion.Y;
                OnGround = false;
                if (motion.Y < 0f) FallDistance -= motion.Y;
            }
        }
        else if (!OnGround)
        {
            OnGround = StandingOnGround(world, Position);
        }
    }

    /// <summary>
    /// Backs a crouching body out of a step that would leave it standing over nothing, one axis at
    /// a time so it can still slide along the edge.
    /// </summary>
    private void HoldTheLedge(VoxelWorld world, Vector3 before)
    {
        var moved = new Vector3(Position.X, before.Y, Position.Z);
        if (StandingOnGround(world, moved)) return;

        // Keep whichever single axis still has floor under it — walking along the lip of a drop is
        // the whole point of crouching there, not being frozen the moment you approach it.
        var alongX = new Vector3(Position.X, before.Y, before.Z);
        if (StandingOnGround(world, alongX))
        {
            Position = alongX;
            Velocity.Z = 0f;
            return;
        }

        var alongZ = new Vector3(before.X, before.Y, Position.Z);
        if (StandingOnGround(world, alongZ))
        {
            Position = alongZ;
            Velocity.X = 0f;
            return;
        }

        Position = before;
        Velocity.X = 0f;
        Velocity.Z = 0f;
    }

    /// <summary>
    /// Tries one horizontal axis, climbing a low step if that is what is in the way.
    /// </summary>
    private bool TryAxis(VoxelWorld world, Vector3 motion, bool wasOnGround)
    {
        var target = Position + motion;
        if (!Collides(world, target))
        {
            Position = target;
            return true;
        }

        // Only step up from the ground. Doing it mid-air would let a player climb a wall by
        // jumping into it, one ledge at a time.
        if (!wasOnGround) return false;

        for (var lift = 0.1f; lift <= StepHeight + 1e-3f; lift += 0.1f)
        {
            var lifted = target + new Vector3(0f, lift, 0f);
            if (Collides(world, lifted)) continue;

            Position = lifted;
            return true;
        }

        return false;
    }

    /// <summary>True when any solid block overlaps the body's box at the given feet position.</summary>
    public bool Collides(VoxelWorld world, Vector3 feet)
    {
        var half = Width * 0.5f;
        var height = CurrentHeight;

        // Half-open on the maximum edge: a body standing exactly on a block boundary must not be
        // considered inside the block it is touching, or it can never be flush against anything.
        var minX = (int)MathF.Floor(feet.X - half);
        var maxX = (int)MathF.Ceiling(feet.X + half) - 1;
        var minY = (int)MathF.Floor(feet.Y);
        var maxY = (int)MathF.Ceiling(feet.Y + height) - 1;
        var minZ = (int)MathF.Floor(feet.Z - half);
        var maxZ = (int)MathF.Ceiling(feet.Z + half) - 1;

        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        for (var x = minX; x <= maxX; x++)
        {
            if (_solid[world.GetBlock(x, y, z).Value]) return true;
        }

        return false;
    }

    /// <summary>True when something solid is directly under the body's footprint.</summary>
    public bool StandingOnGround(VoxelWorld world, Vector3 feet)
    {
        var probe = feet - new Vector3(0f, 0.02f, 0f);
        var half = Width * 0.5f;

        var y = (int)MathF.Floor(probe.Y);
        var minX = (int)MathF.Floor(probe.X - half);
        var maxX = (int)MathF.Ceiling(probe.X + half) - 1;
        var minZ = (int)MathF.Floor(probe.Z - half);
        var maxZ = (int)MathF.Ceiling(probe.Z + half) - 1;

        for (var z = minZ; z <= maxZ; z++)
        for (var x = minX; x <= maxX; x++)
        {
            if (_solid[world.GetBlock(x, y, z).Value]) return true;
        }

        return false;
    }
}
