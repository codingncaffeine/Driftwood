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

    /// <summary>Blocks a second up a ladder, and the speed a slide down one settles at.</summary>
    /// <remarks>
    /// Deliberately slower than walking. A ladder is a way up rather than a shortcut, and one that
    /// is quicker than the ground makes every staircase in the game pointless.
    /// </remarks>
    public const float ClimbSpeed = 2.8f;
    public const float SlideSpeed = 3.4f;

    /// <summary>
    /// The boxes each block is made of, keyed by raw id. Empty for anything with no collision.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>This replaced a <c>bool[]</c>, and that was the whole of #57.</b> Every slab, stair,
    /// fence, chest, campfire and shut trapdoor in the game was a full cube to a body — walking into
    /// a slab was walking into a wall, and standing on one put the feet a whole block up. An open
    /// door had to be registered not solid at all just so a doorway could be walked through.
    /// </remarks>
    private readonly (Vector3 Min, Vector3 Max)[][] _boxes;

    /// <summary>How many rows of cells below the body's own box still have to be looked at.</summary>
    /// <remarks>
    /// A fence reaches half a block above its own cell so it cannot be hopped. Nothing else does,
    /// and on a registry where nothing does this is zero and the scan is the size it always was.
    /// </remarks>
    private readonly int _cellsBelow;

    /// <summary>
    /// Slack on a face-to-face touch, in blocks.
    /// </summary>
    /// <remarks>
    /// A body resting exactly on a surface has its foot plane and the surface at the same number, and
    /// an overlap test with no slack would call that a collision — after which it could never sit
    /// flush against anything and would be pushed out of every floor it landed on.
    /// </remarks>
    private const float Touch = 1e-4f;

    /// <summary>Which way the wall is behind each climbable block, or -1. Indexed by raw block id.</summary>
    private readonly int[] _climbTo;

    /// <summary>True while the body is in something it can climb, for the caller and the checks.</summary>
    public bool OnLadder { get; private set; }

    /// <summary>True while the feet are in water. Drives buoyancy, drag and the swim stroke.</summary>
    public bool InWater { get; private set; }

    /// <summary>True while the feet are in lava, which is water with the numbers made cruel.</summary>
    public bool InLava { get; private set; }

    /// <summary>How much of walking speed a fluid leaves. Lava is a wall you can wade into.</summary>
    private const float WaterDrag = 0.55f;

    private const float LavaDrag = 0.22f;

    /// <summary>How fast a body settles through a fluid with nothing pressed.</summary>
    private const float WaterSink = 1.2f;

    private const float LavaSink = 0.55f;

    /// <summary>How fast a stroke carries it up. Above the sink rate, or you cannot get out.</summary>
    private const float WaterStroke = 3.2f;

    /// <summary>
    /// Lava is climbed out of far more slowly than water, and it is meant to be.
    /// </summary>
    /// <remarks>
    /// Just fast enough to escape a one-block spill and far too slow to cross a lake. What kills a
    /// player in the deep should be a decision about how far in they went, not a reflex test.
    /// </remarks>
    private const float LavaStroke = 1.1f;

    /// <summary>Nothing falls fast in a fluid, which is what makes a lake break a plunge.</summary>
    private const float SwimTerminal = 6f;

    /// <summary>How quickly the body reaches whatever a fluid is doing to it.</summary>
    private const float SwimAcceleration = 8f;

    /// <summary>Centre of the feet.</summary>
    public Vector3 Position;
    public Vector3 Velocity;

    public bool OnGround { get; private set; }
    public bool Sneaking { get; private set; }

    /// <summary>Fall distance since last touching the ground, for fall damage at P3-7.</summary>
    public float FallDistance { get; private set; }

    /// <summary>Which fluid each block is, for the buoyancy test. Indexed by raw block id.</summary>
    private readonly Blocks.FluidKind[] _fluid;

    public PlayerBody(BlockRegistry registry)
    {
        _boxes = registry.BuildCollisionTable(out _cellsBelow);

        _fluid = new Blocks.FluidKind[registry.Count];
        for (var id = 1; id < registry.Count; id++) _fluid[id] = registry[(ushort)id].Fluid;

        _climbTo = new int[registry.Count];
        Array.Fill(_climbTo, -1);
        for (var id = 1; id < registry.Count; id++)
        {
            var type = registry[(ushort)id];

            // A ladder's support face is the wall it is fixed to, which is also the direction a
            // player has to press to hold on to it. One statement, read twice.
            if (type.Climbable) _climbTo[id] = type.SupportFace;
        }
    }

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

        // ⛳ Which fluid the body is standing in, before anything else uses it. Water was a hole you
        // fell through and drowned in until fluids landed — there was no swimming at all — and it is
        // asked here rather than inside each rule so that one traversal answers gravity, speed and
        // whether a jump is a jump or a stroke.
        InWater = Steeped(world, Blocks.FluidKind.Water);
        InLava = Steeped(world, Blocks.FluidKind.Lava);

        var wishLength = new Vector2(wish.X, wish.Z).Length();
        if (wishLength > 1f) wish /= wishLength;

        var target = sneak ? SneakSpeed : sprint ? SprintSpeed : WalkSpeed;
        if (InLava) target *= LavaDrag;
        else if (InWater) target *= WaterDrag;

        var accel = OnGround ? GroundAcceleration : AirAcceleration;
        if (InWater || InLava) accel = SwimAcceleration;

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

        if (jump && OnGround && !InWater && !InLava)
        {
            Velocity.Y = JumpSpeed;
            OnGround = false;
        }

        if (InWater || InLava)
        {
            // ⛳ Swimming: a slow sink, a stroke that beats it, and a terminal speed low enough that
            // falling into a lake is survivable. Buoyancy is written as a target velocity rather than
            // as an upward force because a force has to be balanced against gravity to hold still and
            // a target does not — the failure mode of the other way is a body that bobs.
            var sink = InLava ? LavaSink : WaterSink;
            var rise = jump ? (InLava ? LavaStroke : WaterStroke) : sneak ? -sink * 3f : -sink;

            Velocity.Y += (rise - Velocity.Y) * MathF.Min(1f, SwimAcceleration * dt);

            // Nothing falls fast in a fluid, and this is also what stops a plunge from a cliff into
            // a lake killing you: the fall damage is worked out from how far you fell, so the water
            // has to arrest you before the landing rather than cushioning it afterwards.
            Velocity.Y = Math.Clamp(Velocity.Y, -SwimTerminal, SwimTerminal);
            FallDistance = 0f;
        }
        else
        {
            Velocity.Y = MathF.Max(Velocity.Y - Gravity * dt, -TerminalSpeed);
        }

        // A ladder replaces gravity rather than fighting it, which is why this comes after the fall
        // and not before: pressing into one holds you against it and a fall becomes a slide.
        Climb(world, wish, sneak);

        MoveWithCollisions(world, Velocity * dt);
    }

    /// <summary>
    /// True when the body's lower half is in the named fluid.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The lower half, not the head and not the whole box.</b> Wading through a stream is not
    /// swimming and standing waist-deep should not lift you off the bottom, so a test on the head
    /// is too high; a test on the whole box means a body with one toe in the water starts floating,
    /// which is too low. The lower half is where a person's buoyancy actually comes from.
    /// </remarks>
    private bool Steeped(VoxelWorld world, Blocks.FluidKind kind)
    {
        var half = Width * 0.5f;
        var top = Position.Y + CurrentHeight * 0.5f;

        var minX = (int)MathF.Floor(Position.X - half);
        var maxX = (int)MathF.Floor(Position.X + half);
        var minZ = (int)MathF.Floor(Position.Z - half);
        var maxZ = (int)MathF.Floor(Position.Z + half);
        var minY = (int)MathF.Floor(Position.Y);
        var maxY = (int)MathF.Floor(top);

        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        for (var x = minX; x <= maxX; x++)
            if (_fluid[world.GetBlock(x, y, z).Value] == kind) return true;

        return false;
    }

    /// <summary>
    /// Holds the body on a ladder it is pressing into, and turns falling past one into sliding.
    /// </summary>
    /// <remarks>
    /// <para>Pressing into the wall is what holds you, rather than merely standing in the cell. A
    /// ladder you stick to by walking past it is a ladder that catches anybody who builds a
    /// corridor beside one, and the direction to press is a thing the block already knows: it is
    /// the wall the ladder is fixed to.</para>
    /// <para>Fall distance is cleared while climbing, so stepping off at the top of a shaft is not
    /// a fall from the bottom of it. ⚠ <see cref="FallDistance"/> is cleared the instant the body
    /// lands, so anything wanting to know how far it fell has to keep the number itself — that is
    /// why this clears it here rather than trusting the landing to.</para>
    /// </remarks>
    private void Climb(VoxelWorld world, Vector3 wish, bool sneak)
    {
        OnLadder = false;

        var half = Width * 0.5f;
        var minX = (int)MathF.Floor(Position.X - half);
        var maxX = (int)MathF.Ceiling(Position.X + half) - 1;
        var minY = (int)MathF.Floor(Position.Y);
        var maxY = (int)MathF.Ceiling(Position.Y + CurrentHeight) - 1;
        var minZ = (int)MathF.Floor(Position.Z - half);
        var maxZ = (int)MathF.Ceiling(Position.Z + half) - 1;

        var pressing = false;

        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        for (var x = minX; x <= maxX; x++)
        {
            var toWall = _climbTo[world.GetBlock(x, y, z).Value];
            if (toWall < 0) continue;

            OnLadder = true;

            var (nx, ny, nz) = Faces.Normals[toWall];
            if (wish.X * nx + wish.Y * ny + wish.Z * nz > 0.1f) pressing = true;
        }

        if (!OnLadder) return;

        // Pressing into it goes up; standing in it comes down slowly; crouching holds. Three
        // states rather than two, because a ladder you can only ever climb is a ladder you have to
        // jump off the top of to get back down.
        if (sneak) Velocity.Y = 0f;
        else if (pressing) Velocity.Y = ClimbSpeed;
        else Velocity.Y = MathF.Max(Velocity.Y, -SlideSpeed);

        FallDistance = 0f;
    }

    /// <summary>Places the body somewhere without any collision resolution.</summary>
    public void Teleport(Vector3 position)
    {
        Position = position;
        Velocity = Vector3.Zero;
        OnGround = false;
        FallDistance = 0f;
    }

    /// <summary>
    /// Ground actually covered since this was last read, in blocks. Reading it clears it.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>What hunger is spent on, and it is MEASURED rather than inferred from the keys.</b>
    /// A player holding forward against a wall is going nowhere; one carried by a current or sliding
    /// down a slope is holding nothing and covering ground. Distance is the honest question — and it
    /// makes sprinting cost more with no multiplier anywhere, because a sprint covers more blocks in
    /// the same second and so spends more of the bar by arithmetic. A constant would be one more
    /// number to keep in step with the movement speeds.</para>
    /// <para>⚠ <b>Horizontal only.</b> Vertical movement is falling, climbing and being shoved by
    /// water, none of which is walking; counting it would make treading water the hungriest thing in
    /// the game.</para>
    /// <para>⚠ <b>Reading it clears it</b>, so it cannot be counted twice and cannot grow without
    /// bound across a session. There is exactly one reader.</para>
    /// </remarks>
    public float TakeDistanceWalked()
    {
        var walked = _walked;
        _walked = 0f;
        return walked;
    }

    private float _walked;

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
        var started = Position;

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
                //
                // ⛔ The surface, not the cell boundary. A slab's top is half way up its cell and a
                // stair's lower step is too; snapping to floor(y)+1 put the feet inside them.
                var target = Position.Y + motion.Y;

                if (motion.Y < 0f)
                {
                    var top = FloorCrossed(world, Position, Position.Y, target);
                    Position.Y = float.IsNaN(top) ? MathF.Floor(target) + 1f : top;

                    OnGround = true;
                    FallDistance = 0f;
                }
                else
                {
                    var head = Position.Y + CurrentHeight;
                    var under = CeilingCrossed(world, Position, head, target + CurrentHeight);
                    Position.Y = float.IsNaN(under)
                        ? MathF.Ceiling(target + CurrentHeight) - 1f - CurrentHeight
                        : under - CurrentHeight;
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

        // Whatever ground this slice actually covered, after every collision has had its say — so a
        // step into a wall adds nothing and a step along it adds only the part that happened.
        var dx = Position.X - started.X;
        var dz = Position.Z - started.Z;
        _walked += MathF.Sqrt(dx * dx + dz * dz);
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

    /// <summary>True when any block's shape overlaps the body's box at the given feet position.</summary>
    /// <remarks>
    /// Half-open on every edge: a body standing exactly on a block boundary must not be considered
    /// inside the block it is touching, or it can never be flush against anything.
    /// </remarks>
    public bool Collides(VoxelWorld world, Vector3 feet)
    {
        var half = Width * 0.5f;
        var lo = new Vector3(feet.X - half, feet.Y, feet.Z - half);
        var hi = new Vector3(feet.X + half, feet.Y + CurrentHeight, feet.Z + half);

        return Overlaps(world, lo, hi);
    }

    /// <summary>True when something is directly under the body's footprint, close enough to stand on.</summary>
    /// <remarks>
    /// A thin slice under the feet rather than "is the block below solid". That is what makes a slab
    /// something to stand on top of at half height, and what stops the cell under a chest counting
    /// as floor when the chest itself is what is being stood on.
    /// </remarks>
    public bool StandingOnGround(VoxelWorld world, Vector3 feet)
    {
        const float Probe = 0.02f;
        var half = Width * 0.5f;

        var lo = new Vector3(feet.X - half, feet.Y - Probe, feet.Z - half);
        var hi = new Vector3(feet.X + half, feet.Y, feet.Z + half);

        return Overlaps(world, lo, hi);
    }

    /// <summary>True when any block's shape overlaps the given box.</summary>
    private bool Overlaps(VoxelWorld world, Vector3 lo, Vector3 hi)
    {
        var minX = (int)MathF.Floor(lo.X);
        var maxX = (int)MathF.Ceiling(hi.X) - 1;
        var minY = (int)MathF.Floor(lo.Y) - _cellsBelow;
        var maxY = (int)MathF.Ceiling(hi.Y) - 1;
        var minZ = (int)MathF.Floor(lo.Z);
        var maxZ = (int)MathF.Ceiling(hi.Z) - 1;

        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        for (var x = minX; x <= maxX; x++)
        {
            var boxes = _boxes[world.GetBlock(x, y, z).Value];
            if (boxes.Length == 0) continue;

            foreach (var (min, max) in boxes)
            {
                if (lo.X < x + max.X - Touch && hi.X > x + min.X + Touch &&
                    lo.Y < y + max.Y - Touch && hi.Y > y + min.Y + Touch &&
                    lo.Z < z + max.Z - Touch && hi.Z > z + min.Z + Touch)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The highest surface the feet would pass through on the way down, or <see cref="float.NaN"/>.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The half of #57 that is not the overlap test.</b> Landing used to be
    /// <c>floor(y) + 1</c> — the top of the cell, which is only the top of the block when the block
    /// fills its cell. On a slab it put the feet half a block inside the slab, on a stair a whole
    /// step out. What is wanted is the highest box top that the fall crossed, which is a real number
    /// and not a cell boundary.
    /// </remarks>
    private float FloorCrossed(VoxelWorld world, Vector3 feet, float from, float to)
    {
        var half = Width * 0.5f;
        var loX = feet.X - half;
        var hiX = feet.X + half;
        var loZ = feet.Z - half;
        var hiZ = feet.Z + half;

        var minX = (int)MathF.Floor(loX);
        var maxX = (int)MathF.Ceiling(hiX) - 1;
        var minZ = (int)MathF.Floor(loZ);
        var maxZ = (int)MathF.Ceiling(hiZ) - 1;
        var minY = (int)MathF.Floor(to) - _cellsBelow;
        var maxY = (int)MathF.Ceiling(from);

        var best = float.NaN;

        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        for (var x = minX; x <= maxX; x++)
        {
            var boxes = _boxes[world.GetBlock(x, y, z).Value];
            if (boxes.Length == 0) continue;

            foreach (var (min, max) in boxes)
            {
                if (loX >= x + max.X - Touch || hiX <= x + min.X + Touch) continue;
                if (loZ >= z + max.Z - Touch || hiZ <= z + min.Z + Touch) continue;

                // Only surfaces the feet actually crossed. One already above where they started is
                // something the body was inside before this step and is not what stopped it.
                var top = y + max.Y;
                if (top > from + Touch || top <= to) continue;
                if (float.IsNaN(best) || top > best) best = top;
            }
        }

        return best;
    }

    /// <summary>The lowest surface the head would pass through on the way up, or NaN.</summary>
    private float CeilingCrossed(VoxelWorld world, Vector3 feet, float from, float to)
    {
        var half = Width * 0.5f;
        var loX = feet.X - half;
        var hiX = feet.X + half;
        var loZ = feet.Z - half;
        var hiZ = feet.Z + half;

        var minX = (int)MathF.Floor(loX);
        var maxX = (int)MathF.Ceiling(hiX) - 1;
        var minZ = (int)MathF.Floor(loZ);
        var maxZ = (int)MathF.Ceiling(hiZ) - 1;
        var minY = (int)MathF.Floor(from) - _cellsBelow;
        var maxY = (int)MathF.Ceiling(to);

        var best = float.NaN;

        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        for (var x = minX; x <= maxX; x++)
        {
            var boxes = _boxes[world.GetBlock(x, y, z).Value];
            if (boxes.Length == 0) continue;

            foreach (var (min, max) in boxes)
            {
                if (loX >= x + max.X - Touch || hiX <= x + min.X + Touch) continue;
                if (loZ >= z + max.Z - Touch || hiZ <= z + min.Z + Touch) continue;

                var under = y + min.Y;
                if (under < from - Touch || under >= to) continue;
                if (float.IsNaN(best) || under < best) best = under;
            }
        }

        return best;
    }
}
