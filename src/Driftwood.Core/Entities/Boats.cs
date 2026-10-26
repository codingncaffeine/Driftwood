using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.World;

namespace Driftwood.Core.Entities;

/// <summary>Where a boat finds itself this tick, the reference's five states.</summary>
public enum BoatStatus
{
    InWater,
    UnderWater,
    UnderFlowingWater,
    OnLand,
    InAir,
}

/// <summary>What the rower is pressing this tick.</summary>
public readonly record struct Oars(bool Forward, bool Back, bool Left, bool Right)
{
    public bool Any => Forward || Back || Left || Right;
}

/// <summary>
/// One boat: a free body on the water, not a cell on a track like the cart.
/// </summary>
/// <remarks>
/// <para>Velocity and spin are kept in the reference's own units — blocks and degrees per tick of
/// twenty a second — because every constant in <see cref="BoatSystem"/> is the reference's, and a
/// port that converts each one is a port with a dozen places to be wrong. The renderer and the save
/// never need anything but <see cref="Position"/> and <see cref="Yaw"/>.</para>
/// <para>Facing follows the reference: forward is (−sin yaw, 0, cos yaw).</para>
/// </remarks>
public sealed class Boat
{
    /// <summary>The middle of the hull's underside, in world blocks.</summary>
    public Vector3 Position;

    /// <summary>Blocks per tick.</summary>
    public Vector3 Velocity;

    /// <summary>Degrees, the reference's: 0 faces +z.</summary>
    public float Yaw;

    /// <summary>Degrees per tick the hull is turning.</summary>
    public float Spin;

    /// <summary>Cherry planks rather than driftoak — the hull's wood, its item and its sheet.</summary>
    public bool Cherry;

    /// <summary>The oars' phases in radians, for the renderer; each turns only while it pulls.</summary>
    public float LeftOar, RightOar;

    /// <summary>What the rower pressed, set by whoever is aboard before each step.</summary>
    public Oars Oars;

    /// <summary>Whether someone is at the oars. A boat nobody rows only drifts.</summary>
    public bool Rowed;

    /// <summary>The creature in the second seat, or null.</summary>
    public Creature? Passenger;

    public BoatStatus Status = BoatStatus.InAir;

    /// <summary>How hard it has been hit lately; three good knocks break it, the reference's way.</summary>
    public float Damage;

    /// <summary>A rock to the side when struck, decaying.</summary>
    public float Hurt;

    /// <summary>Blocks rowed since the rower climbed in — the long-haul achievement's count.</summary>
    public float RowedDistance;

    /// <summary>The way it points, flat.</summary>
    public Vector3 Forward
    {
        get
        {
            var r = float.DegreesToRadians(Yaw);
            return new Vector3(-MathF.Sin(r), 0f, MathF.Cos(r));
        }
    }

    /// <summary>Where the rower sits: a little ahead of the middle when there are two aboard.</summary>
    public Vector3 RowerSeat => Position + new Vector3(0f, BoatSystem.SeatHeight, 0f)
                                + (Passenger is null ? Vector3.Zero : Forward * 0.2f);

    /// <summary>Where a passenger sits: behind the rower.</summary>
    public Vector3 PassengerSeat => Position + new Vector3(0f, BoatSystem.SeatHeight, 0f) - Forward * 0.6f;

    /// <summary>Speed over the water, blocks per second.</summary>
    public float Speed => new Vector2(Velocity.X, Velocity.Z).Length() * BoatSystem.TicksPerSecond;
}

/// <summary>
/// Floats, rows and grounds every boat: the reference's boat, tick for tick.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Ported rather than invented</b>, as the driftwings were, so a boat handles the way
/// anyone who has rowed one there expects: forward pulls 0.04 a tick against water's 0.9 keep, so
/// it tops out near eight blocks a second; A and D swing the bow a degree a tick and a turn on the
/// spot creeps forward; the hull sits about a third of a block under the waterline; on land it
/// drags to a halt, and on ice — the reference's own famous quirk — it barely slows at all.</para>
/// <para>Collision is against the same block shapes the player's body uses, axis by axis, and a
/// boat never steps up: it beaches against a one-block shore, which is the point of a shore.</para>
/// </remarks>
public sealed class BoatSystem
{
    public const float TicksPerSecond = 20f;
    private const float Tick = 1f / TicksPerSecond;

    /// <summary>The hull, the reference's 1.375 across and 0.5625 tall.</summary>
    public const float Width = 1.375f;
    public const float Height = 0.5625f;

    /// <summary>Where a rider's feet go, over the hull's underside.</summary>
    public const float SeatHeight = -0.1f;

    private const float Gravity = -0.04f;
    private const float WaterKeep = 0.9f;
    private const float AirKeep = 0.9f;
    private const float UnderwaterKeep = 0.45f;
    private const float Pull = 0.04f;
    private const float Backwater = 0.005f;
    private const float TurnCreep = 0.005f;
    private const float Buoyancy = 0.06153846f;

    /// <summary>Ground's own grip, and ice's lack of it — the reference's 0.6 and 0.98.</summary>
    private const float GroundSlip = 0.6f;
    private const float IceSlip = 0.98f;

    private const float Touch = 1e-4f;

    private readonly (Vector3 Min, Vector3 Max)[][] _boxes;
    private readonly int _cellsBelow;
    private readonly FluidKind[] _fluid;
    private readonly int[] _level;
    private readonly bool[] _falling;
    private readonly bool[] _icy;
    private float _clock;

    public BoatSystem(BlockRegistry registry, StarterBlocks.Ids ids)
    {
        _boxes = registry.BuildCollisionTable(out _cellsBelow);
        _fluid = new FluidKind[registry.Count];
        _level = new int[registry.Count];
        _falling = new bool[registry.Count];
        _icy = new bool[registry.Count];
        for (var id = 1; id < registry.Count; id++)
        {
            var type = registry[(ushort)id];
            _fluid[id] = type.Fluid;
            _level[id] = type.FluidLevel;
            _falling[id] = type.FluidFalling;
        }

        _icy[ids.Ice.Value] = true;
        if (registry.TryByName("packed_ice", out var packed)) _icy[packed.Id.Value] = true;
    }

    public List<Boat> All { get; } = [];

    /// <summary>Puts a boat down, its bow the way the placer faces, as the reference does.</summary>
    public Boat Place(Vector3 at, float yaw, bool cherry)
    {
        var boat = new Boat { Position = at, Yaw = yaw, Cherry = cherry };
        All.Add(boat);
        return boat;
    }

    /// <summary>
    /// Advances every boat by fixed ticks. Seated passengers are carried along to their seats.
    /// </summary>
    public void Step(VoxelWorld world, float dt)
    {
        _clock += MathF.Min(dt, 0.25f);
        while (_clock >= Tick)
        {
            _clock -= Tick;
            foreach (var boat in All) TickOne(world, boat);
        }

        foreach (var boat in All)
        {
            boat.Hurt = MathF.Max(0f, boat.Hurt - dt * 10f);
            boat.Damage = MathF.Max(0f, boat.Damage - dt * 2f);

            if (boat.Passenger is { } rider)
            {
                if (!rider.Alive) { rider.Seated = false; boat.Passenger = null; continue; }
                rider.Position = boat.PassengerSeat;
                var f = boat.Forward;
                rider.Yaw = float.RadiansToDegrees(MathF.Atan2(f.Z, f.X));
            }
        }
    }

    private void TickOne(VoxelWorld world, Boat boat)
    {
        var was = boat.Status;
        var (status, surface, slip) = Survey(world, boat);

        // ⛳ The reference's landing: a hull falling out of the air into water is lifted straight to
        // the surface it fell through, rather than left to sink — under water the buoyancy is a
        // token one and a dropped boat would otherwise settle on the bottom.
        if (was == BoatStatus.InAir && status is BoatStatus.InWater or BoatStatus.UnderWater or BoatStatus.UnderFlowingWater)
        {
            boat.Position.Y = SurfaceAbove(world, boat) - Height + 0.101f;
            boat.Velocity.Y = 0f;
            (status, surface, slip) = Survey(world, boat);
        }

        boat.Status = status;

        // floatBoat: what the water, the ground or the air does to it.
        var fall = Gravity;
        var lift = 0f;
        var keep = 0.05f;
        switch (status)
        {
            case BoatStatus.InWater:
                lift = (surface - boat.Position.Y) / Height;
                keep = WaterKeep;
                break;
            case BoatStatus.UnderFlowingWater:
                fall = -7.0E-4f;
                keep = WaterKeep;
                break;
            case BoatStatus.UnderWater:
                lift = 0.01f;
                keep = UnderwaterKeep;
                break;
            case BoatStatus.InAir:
                keep = AirKeep;
                break;
            case BoatStatus.OnLand:
                keep = boat.Rowed ? slip / 2f : slip;
                break;
        }

        var v = boat.Velocity;
        v = new Vector3(v.X * keep, v.Y + fall, v.Z * keep);
        boat.Spin *= keep;
        if (lift > 0f) v.Y = (v.Y + lift * Buoyancy) * 0.75f;

        // controlBoat: the oars.
        if (boat.Rowed)
        {
            var o = boat.Oars;
            var push = 0f;
            if (o.Left) boat.Spin -= 1f;
            if (o.Right) boat.Spin += 1f;
            if (o.Right != o.Left && !o.Forward && !o.Back) push += TurnCreep;
            boat.Yaw += boat.Spin;
            if (o.Forward) push += Pull;
            if (o.Back) push -= Backwater;

            var r = float.DegreesToRadians(boat.Yaw);
            v += new Vector3(MathF.Sin(-r) * push, 0f, MathF.Cos(r) * push);

            // The oars turn while they pull: the left one for forward or a turn to the right.
            const float Stroke = MathF.PI / 8f;
            if ((o.Right && !o.Left) || o.Forward) boat.LeftOar = (boat.LeftOar + Stroke) % MathF.Tau;
            if ((o.Left && !o.Right) || o.Forward) boat.RightOar = (boat.RightOar + Stroke) % MathF.Tau;
        }
        else
        {
            boat.Yaw += boat.Spin;
        }

        boat.Yaw = ((boat.Yaw % 360f) + 360f) % 360f;

        var before = boat.Position;
        boat.Velocity = Move(world, boat, v);
        if (boat.Rowed)
        {
            var moved = boat.Position - before;
            boat.RowedDistance += new Vector2(moved.X, moved.Z).Length();
        }
    }

    /// <summary>
    /// The reference's getStatus: under water, in it, on the ground, or in the air — with the water's
    /// surface and the ground's slip found on the way.
    /// </summary>
    private (BoatStatus Status, float Surface, float Slip) Survey(VoxelWorld world, Boat boat)
    {
        var half = Width * 0.5f;
        var lo = boat.Position - new Vector3(half, 0f, half);
        var hi = boat.Position + new Vector3(half, Height, half);
        int x0 = (int)MathF.Floor(lo.X), x1 = (int)MathF.Ceiling(hi.X) - 1;
        int z0 = (int)MathF.Floor(lo.Z), z1 = (int)MathF.Ceiling(hi.Z) - 1;

        // isUnderwater: water closing over the top of the hull.
        var top = (int)MathF.Floor(hi.Y);
        var under = false;
        var flowing = false;
        for (var z = z0; z <= z1; z++)
        for (var x = x0; x <= x1; x++)
        {
            var id = world.GetBlock(x, top, z).Value;
            if (_fluid[id] != FluidKind.Water) continue;
            if (hi.Y < top + FluidHeight(world, x, top, z, id) - Touch)
            {
                under = true;
                if (_level[id] < FluidTable.MaxLevel || _falling[id]) flowing = true;
            }
        }

        if (under) return (flowing ? BoatStatus.UnderFlowingWater : BoatStatus.UnderWater, 0f, 0f);

        // checkInWater: the highest surface under the hull's bottom slice.
        var surface = float.MinValue;
        var wet = false;
        int y0 = (int)MathF.Floor(lo.Y), y1 = (int)MathF.Ceiling(lo.Y + 0.001f);
        for (var y = y0; y < y1; y++)
        for (var z = z0; z <= z1; z++)
        for (var x = x0; x <= x1; x++)
        {
            var id = world.GetBlock(x, y, z).Value;
            if (_fluid[id] != FluidKind.Water) continue;
            var level = y + FluidHeight(world, x, y, z, id);
            surface = MathF.Max(surface, level);
            wet |= lo.Y < level;
        }

        if (wet) return (BoatStatus.InWater, surface, 0f);

        // getGroundFriction: the grip of whatever the hull rests on, averaged over what it touches.
        var below = (int)MathF.Floor(lo.Y - 0.001f);
        float sum = 0f;
        var count = 0;
        for (var z = z0; z <= z1; z++)
        for (var x = x0; x <= x1; x++)
        {
            var id = world.GetBlock(x, below, z).Value;
            if (!Touches(id, x, below, z, lo.X, hi.X, lo.Z, hi.Z, lo.Y - 0.001f)) continue;
            sum += _icy[id] ? IceSlip : GroundSlip;
            count++;
        }

        return count > 0 ? (BoatStatus.OnLand, 0f, sum / count) : (BoatStatus.InAir, 0f, 0f);
    }

    /// <summary>The top of the water the hull is in: up from its rim while any of its footprint is wet.</summary>
    private float SurfaceAbove(VoxelWorld world, Boat boat)
    {
        var half = Width * 0.5f;
        int x0 = (int)MathF.Floor(boat.Position.X - half), x1 = (int)MathF.Ceiling(boat.Position.X + half) - 1;
        int z0 = (int)MathF.Floor(boat.Position.Z - half), z1 = (int)MathF.Ceiling(boat.Position.Z + half) - 1;

        var top = boat.Position.Y + Height;
        for (var y = (int)MathF.Floor(boat.Position.Y); y < (int)MathF.Floor(boat.Position.Y) + 64; y++)
        {
            var wet = false;
            for (var z = z0; z <= z1; z++)
            for (var x = x0; x <= x1; x++)
            {
                var id = world.GetBlock(x, y, z).Value;
                if (_fluid[id] != FluidKind.Water) continue;
                wet = true;
                top = MathF.Max(top, y + FluidHeight(world, x, y, z, id));
            }

            if (!wet) break;
        }

        return top;
    }

    /// <summary>A water cell's surface over its floor: a full one under more water is the whole cell.</summary>
    private float FluidHeight(VoxelWorld world, int x, int y, int z, ushort id)
    {
        if (_fluid[world.GetBlock(x, y + 1, z).Value] == FluidKind.Water) return 1f;
        return Math.Clamp(_level[id], 1, FluidTable.MaxLevel) / (FluidTable.MaxLevel + 1f);
    }

    private bool Touches(ushort id, int x, int y, int z, float lx, float hx, float lz, float hz, float probe)
    {
        foreach (var (min, max) in _boxes[id])
        {
            if (lx < x + max.X - Touch && hx > x + min.X + Touch
                && lz < z + max.Z - Touch && hz > z + min.Z + Touch
                && probe < y + max.Y && probe >= y + min.Y - Touch)
                return true;
        }

        return false;
    }

    /// <summary>Moves the hull by a tick's velocity, up and down first, then across, stopping at blocks.</summary>
    private Vector3 Move(VoxelWorld world, Boat boat, Vector3 v)
    {
        var p = boat.Position;

        if (v.Y != 0f)
        {
            var target = p + new Vector3(0f, v.Y, 0f);
            if (Collides(world, target))
            {
                // Settle onto what it hit rather than hovering a tick's fall above it.
                var step = v.Y / 8f;
                for (var i = 0; i < 8 && !Collides(world, p + new Vector3(0f, step, 0f)); i++) p.Y += step;
                v.Y = 0f;
            }
            else p = target;
        }

        if (v.X != 0f)
        {
            var target = p + new Vector3(v.X, 0f, 0f);
            if (Collides(world, target)) v.X = 0f; else p = target;
        }

        if (v.Z != 0f)
        {
            var target = p + new Vector3(0f, 0f, v.Z);
            if (Collides(world, target)) v.Z = 0f; else p = target;
        }

        boat.Position = p;
        return v;
    }

    /// <summary>True when the hull at this position overlaps any block's shape.</summary>
    public bool Collides(VoxelWorld world, Vector3 at)
    {
        var half = Width * 0.5f;
        var lo = new Vector3(at.X - half, at.Y, at.Z - half);
        var hi = new Vector3(at.X + half, at.Y + Height, at.Z + half);

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
            foreach (var (min, max) in _boxes[world.GetBlock(x, y, z).Value])
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
    /// Seats a creature that bumps into a moving boat with a free seat — the reference's rule, trap
    /// and all: a boat rowed into a zombie carries it off, harmless while it sits.
    /// </summary>
    public bool TryBoard(Boat boat, Creature creature)
    {
        if (boat.Passenger is not null || creature.Seated || !creature.Alive) return false;
        if (creature.Size.X > Width * 0.9f || creature.Size.Y > 2.2f) return false;
        if (boat.Speed < 1f) return false;

        var d = creature.Position - boat.Position;
        if (new Vector2(d.X, d.Z).Length() > Width * 0.5f + creature.Size.X * 0.5f + 0.05f) return false;
        if (MathF.Abs(d.Y) > 1f) return false;

        boat.Passenger = creature;
        creature.Seated = true;
        return true;
    }

    /// <summary>Lets the passenger out beside the hull.</summary>
    public void Unseat(Boat boat)
    {
        if (boat.Passenger is not { } rider) return;
        rider.Seated = false;
        var side = Vector3.Cross(boat.Forward, Vector3.UnitY);
        rider.Position = boat.Position + side * 1.2f + new Vector3(0f, 0.3f, 0f);
        boat.Passenger = null;
    }

    /// <summary>The nearest boat the look passes through, within reach.</summary>
    public Boat? Pick(Vector3 eye, Vector3 forward, float reach)
    {
        Boat? best = null;
        var bestAlong = float.MaxValue;
        var half = Width * 0.5f;

        foreach (var boat in All)
        {
            var lo = boat.Position - new Vector3(half, 0f, half);
            var hi = boat.Position + new Vector3(half, Height, half);
            if (!RayBox(eye, forward, lo, hi, out var along) || along > reach || along >= bestAlong) continue;
            best = boat;
            bestAlong = along;
        }

        return best;
    }

    private static bool RayBox(Vector3 o, Vector3 d, Vector3 lo, Vector3 hi, out float t)
    {
        var tMin = 0f;
        var tMax = float.MaxValue;
        t = 0f;
        for (var a = 0; a < 3; a++)
        {
            var oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z;
            var da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
            var la = a == 0 ? lo.X : a == 1 ? lo.Y : lo.Z;
            var ha = a == 0 ? hi.X : a == 1 ? hi.Y : hi.Z;
            if (MathF.Abs(da) < 1e-6f)
            {
                if (oa < la || oa > ha) return false;
                continue;
            }

            var t1 = (la - oa) / da;
            var t2 = (ha - oa) / da;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tMin = MathF.Max(tMin, t1);
            tMax = MathF.Min(tMax, t2);
            if (tMin > tMax) return false;
        }

        t = tMin;
        return true;
    }
}
