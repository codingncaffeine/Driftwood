using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.World;

namespace Driftwood.Core.Entities;

/// <summary>One block of a ship, at its cell relative to the helm.</summary>
public readonly record struct ShipCell(int X, int Y, int Z, BlockId Id);

/// <summary>
/// A ship under way (2026-10-25): blocks lifted off the world as one body, sailed, and set back down.
/// </summary>
/// <remarks>
/// <para>Local space is cells relative to the helm's own cell. A local point <c>p</c> is in the world
/// at <c>Position + Pivot + Turn(Yaw)(p − Pivot)</c>, the pivot being the middle of the helm cell's
/// floor — so a ship swings about the wheel, which is where the person steering it stands.</para>
/// <para>Yaw grows the camera's way: a positive turn takes +x toward +z, which is a turn to the right.</para>
/// </remarks>
public sealed class Ship
{
    public static readonly Vector3 Pivot = new(0.5f, 0f, 0.5f);

    public required ShipCell[] Cells { get; init; }

    /// <summary>The cells a hull's edge runs through — the only ones that can meet anything.</summary>
    public required ShipCell[] Rim { get; init; }

    /// <summary>Air the hull held below the waterline — its hold — kept so docking can bail it out.</summary>
    public required (int X, int Y, int Z)[] Hold { get; init; }

    /// <summary>The helm cell's corner in the world: where local (0,0,0) is.</summary>
    public Vector3 Position;

    /// <summary>Degrees turned since it was lifted.</summary>
    public float Yaw;

    /// <summary>Blocks per second along <see cref="Forward"/>, and degrees per second of swing.</summary>
    public float Speed, Spin;

    /// <summary>The way the bow pointed when it was lifted: the way the helm's steersman faced.</summary>
    public required Vector3 Bow { get; init; }

    /// <summary>Where the water stood round the hull when it was lifted.</summary>
    public required int Waterline { get; init; }

    /// <summary>Bumps into something hard since the last ask — the client's cue for a thud.</summary>
    public int Bumped;

    /// <summary>Bumped up by the owner whenever the cells change, so a mesh knows to rebuild.</summary>
    public int Version;

    /// <summary>What the client carries in it — chests, furnaces, signs — keyed by local cell.</summary>
    public Dictionary<(int X, int Y, int Z), object> Cargo { get; } = [];

    /// <summary>Creatures standing on deck, by where on it they stand.</summary>
    public List<(Creature Who, Vector3 Local)> Crew { get; } = [];

    public (int X, int Y, int Z) Min { get; init; }
    public (int X, int Y, int Z) Max { get; init; }

    public Vector3 Forward => Turn(Bow, Yaw);

    /// <summary>A local point in the world, by this yaw and position.</summary>
    public Vector3 ToWorld(Vector3 local) => ToWorld(local, Position, Yaw);

    public static Vector3 ToWorld(Vector3 local, Vector3 position, float yaw) =>
        position + Pivot + Turn(local - Pivot, yaw);

    public static Vector3 Turn(Vector3 v, float degrees)
    {
        var a = float.DegreesToRadians(degrees);
        var (s, c) = MathF.SinCos(a);
        return new Vector3(v.X * c - v.Z * s, v.Y, v.X * s + v.Z * c);
    }

    /// <summary>Where the steersman stands: on the deck a pace behind the wheel.</summary>
    public Vector3 HelmStand => ToWorld(Pivot - Bow * 1.1f + new Vector3(0f, 0f, 0f));
}

/// <summary>What the oars are, for a ship: the wheel and the wind.</summary>
public readonly record struct ShipOrders(bool Ahead, bool Astern, bool Port, bool Starboard);

/// <summary>
/// Lifts ships off the world, sails them, and sets them back down.
/// </summary>
/// <remarks>
/// <para>⛳ <b>The world only ever holds a ship's blocks while it is moored.</b> Under way they live
/// in the <see cref="Ship"/> alone and are drawn from there, so nothing else in the game — light,
/// fluids, saving, creatures pathing — has to know a block can move. The cost is that a ship can
/// only be boarded at the helm while it sails, and that is the rule: leaving the helm moors it.</para>
/// <para>A ship is found by flooding out from the helm through every block that is not air or a
/// fluid. Land is blocks too, so a helm on a jetty would try to sail the coast: past
/// <see cref="MaxCells"/> the flood gives up and says the ship is fast to the land.</para>
/// <para>It holds the height it was lifted at — a ship rides its own waterline — and handles by
/// its size: a raft turns on a coin and a galleon wants room, from one rule rather than a table.</para>
/// </remarks>
public sealed class ShipSystem
{
    public const int MaxCells = 4096;

    private readonly BlockRegistry _registry;
    private readonly BlockTurns _turns;
    private readonly bool[] _solid;
    private readonly bool[] _water;
    private readonly bool[] _fluid;
    private readonly BlockId _waterId;

    public ShipSystem(BlockRegistry registry, BlockTurns turns, StarterBlocks.Ids ids)
    {
        _registry = registry;
        _turns = turns;
        _waterId = ids.Water;
        _solid = new bool[registry.Count];
        _water = new bool[registry.Count];
        _fluid = new bool[registry.Count];
        for (var id = 1; id < registry.Count; id++)
        {
            var type = registry[(ushort)id];
            _solid[id] = type.Solid;
            _water[id] = type.Fluid == FluidKind.Water;
            _fluid[id] = type.Fluid != FluidKind.None;
        }
    }

    public List<Ship> All { get; } = [];

    /// <summary>Why a helm would not lift, for the player to read.</summary>
    public string? Refusal { get; private set; }

    /// <summary>
    /// Floods out from the helm and lifts what it finds, or refuses and says why. The world's own
    /// cells are cleared through <paramref name="write"/>, and the water closes over where the hull was.
    /// </summary>
    public Ship? Lift(VoxelWorld world, int hx, int hy, int hz, Vector3 bow, Action<int, int, int, BlockId> write)
    {
        Refusal = null;
        var found = new Dictionary<(int, int, int), BlockId>();
        var queue = new Queue<(int X, int Y, int Z)>();
        queue.Enqueue((hx, hy, hz));
        found[(hx, hy, hz)] = world.GetBlock(hx, hy, hz);

        var wet = false;
        while (queue.Count > 0)
        {
            var (x, y, z) = queue.Dequeue();
            foreach (var (dx, dy, dz) in Faces.Normals)
            {
                var n = (x + dx, y + dy, z + dz);
                if (found.ContainsKey(n)) continue;
                var id = world.GetBlock(n.Item1, n.Item2, n.Item3);
                if (_water[id.Value]) wet = true;
                if (id.IsAir || _fluid[id.Value]) continue;

                found[n] = id;
                if (found.Count > MaxCells)
                {
                    Refusal = "the ship is fast to the land";
                    return null;
                }

                queue.Enqueue(n);
            }
        }

        if (!wet)
        {
            Refusal = "there is no water under the ship";
            return null;
        }

        // ⛳ Afloat, not merely beside the sea: most of what the hull's underside rests on must be
        // water. A jetty's helm floods into the coast and finds the coast's underside on rock.
        int bottoms = 0, floating = 0;
        foreach (var (x, y, z) in found.Keys)
        {
            if (found.ContainsKey((x, y - 1, z))) continue;
            bottoms++;
            if (_water[world.GetBlock(x, y - 1, z).Value]) floating++;
        }

        if (floating * 2 < bottoms)
        {
            Refusal = "the ship is fast to the land";
            return null;
        }

        // The waterline: the highest water cell beside the hull.
        var waterline = int.MinValue;
        foreach (var (x, y, z) in found.Keys)
        foreach (var (dx, dy, dz) in Faces.Normals)
        {
            if (dy > 0) continue;
            var c = (x + dx, y + dy, z + dz);
            if (found.ContainsKey(c)) continue;
            if (_water[world.GetBlock(c.Item1, c.Item2, c.Item3).Value]) waterline = Math.Max(waterline, c.Item2);
        }

        var cells = found.Select(kv => new ShipCell(kv.Key.Item1 - hx, kv.Key.Item2 - hy, kv.Key.Item3 - hz, kv.Value))
            .ToArray();
        var set = new HashSet<(int, int, int)>(found.Keys);
        var rim = cells.Where(c =>
            !set.Contains((c.X + hx + 1, c.Y + hy, c.Z + hz)) || !set.Contains((c.X + hx - 1, c.Y + hy, c.Z + hz))
            || !set.Contains((c.X + hx, c.Y + hy, c.Z + hz + 1)) || !set.Contains((c.X + hx, c.Y + hy, c.Z + hz - 1))
            || !set.Contains((c.X + hx, c.Y + hy - 1, c.Z + hz))).ToArray();

        var min = (cells.Min(c => c.X), cells.Min(c => c.Y), cells.Min(c => c.Z));
        var max = (cells.Max(c => c.X), cells.Max(c => c.Y), cells.Max(c => c.Z));

        // The hold: air inside the hull's box at or under the waterline. The sea will be let into
        // the space the hull leaves, and docking has to bail exactly these back out.
        var hold = new List<(int, int, int)>();
        for (var y = min.Item2; y <= Math.Min(max.Item2, waterline - hy); y++)
        for (var z = min.Item3; z <= max.Item3; z++)
        for (var x = min.Item1; x <= max.Item1; x++)
            if (world.GetBlock(x + hx, y + hy, z + hz).IsAir) hold.Add((x, y, z));

        var ship = new Ship
        {
            Cells = cells, Rim = rim, Hold = [.. hold], Bow = Vector3.Normalize(new Vector3(bow.X, 0f, bow.Z)),
            Waterline = waterline, Position = new Vector3(hx, hy, hz), Min = min, Max = max,
        };

        foreach (var c in cells) write(c.X + hx, c.Y + hy, c.Z + hz, BlockId.Air);
        Flood(world, ship, hx, hy, hz, write);

        All.Add(ship);
        return ship;
    }

    /// <summary>Lets the sea into the hull's box, up to the waterline, wherever there is now air.</summary>
    private void Flood(VoxelWorld world, Ship ship, int ox, int oy, int oz, Action<int, int, int, BlockId> write)
    {
        for (var y = ship.Min.Y; y <= Math.Min(ship.Max.Y, ship.Waterline - oy); y++)
        for (var z = ship.Min.Z; z <= ship.Max.Z; z++)
        for (var x = ship.Min.X; x <= ship.Max.X; x++)
            if (world.GetBlock(x + ox, y + oy, z + oz).IsAir) write(x + ox, y + oy, z + oz, _waterId);
    }

    /// <summary>How the ship handles by its size: top speed, how fast it gets there, how fast it swings.</summary>
    public static (float Top, float Gain, float Swing) Handling(int cells)
    {
        var heft = cells / 200f;
        return (Math.Clamp(7f - heft * 0.6f, 3f, 7f), 2.4f / (1f + heft), 50f / (1f + heft * 0.8f));
    }

    /// <summary>Sails a ship one step: the orders, the water's drag, and whatever it runs into.</summary>
    public void Step(VoxelWorld world, Ship ship, ShipOrders orders, float dt)
    {
        var (top, gain, swing) = Handling(ship.Cells.Length);

        if (orders.Ahead) ship.Speed = MathF.Min(top, ship.Speed + gain * dt);
        else if (orders.Astern) ship.Speed = MathF.Max(-top * 0.35f, ship.Speed - gain * dt);
        else ship.Speed *= MathF.Exp(-0.35f * dt);

        // The wheel bites only with way on: a ship dead in the water turns slowly, if at all.
        var bite = 0.25f + 0.75f * Math.Clamp(MathF.Abs(ship.Speed) / 2f, 0f, 1f);
        var want = (orders.Starboard ? 1f : 0f) - (orders.Port ? 1f : 0f);
        ship.Spin += (want * swing * bite - ship.Spin) * Math.Clamp(dt * 2.5f, 0f, 1f);

        var yaw = ship.Yaw + ship.Spin * dt;
        if (!Blocked(world, ship, ship.Position, yaw)) ship.Yaw = yaw;
        else { ship.Spin = 0f; ship.Bumped++; }

        var moved = ship.Position + Ship.Turn(ship.Bow, ship.Yaw) * (ship.Speed * dt);
        if (!Blocked(world, ship, moved, ship.Yaw)) ship.Position = moved;
        else if (MathF.Abs(ship.Speed) > 0.05f) { ship.Speed = 0f; ship.Bumped++; }

        foreach (var (who, local) in ship.Crew)
        {
            who.Position = ship.ToWorld(local);
            who.Yaw += ship.Spin * dt;
        }
    }

    /// <summary>True when any of the rim, at this place and heading, would stand in something solid.</summary>
    public bool Blocked(VoxelWorld world, Ship ship, Vector3 position, float yaw)
    {
        foreach (var c in ship.Rim)
        {
            // The cell's middle and its four corners a little in: enough to catch a corner of
            // rock while letting a hull slide past a wall it only grazes.
            for (var k = 0; k < 5; k++)
            {
                var (ox, oz) = k switch { 0 => (0.5f, 0.5f), 1 => (0.1f, 0.1f), 2 => (0.9f, 0.1f), 3 => (0.1f, 0.9f), _ => (0.9f, 0.9f) };
                var p = Ship.ToWorld(new Vector3(c.X + ox, c.Y + 0.5f, c.Z + oz), position, yaw);
                var id = world.GetBlock((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), (int)MathF.Floor(p.Z)).Value;
                if (_solid[id]) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Where a ship would moor from here: its heading snapped to a quarter turn and its corner to a
    /// cell, with every block's place found. Null when something solid stands in the way.
    /// </summary>
    public (int Quarters, (int X, int Y, int Z) Corner)? Mooring(VoxelWorld world, Ship ship)
    {
        var quarters = (int)MathF.Round(ship.Yaw / 90f);

        // The snapped yaw moves every cell; the corner is chosen so the wheel stays where it stood.
        var corner = ((int)MathF.Round(ship.Position.X), (int)MathF.Round(ship.Position.Y), (int)MathF.Round(ship.Position.Z));
        foreach (var (dx, dz) in (ReadOnlySpan<(int, int)>)[(0, 0), (1, 0), (-1, 0), (0, 1), (0, -1)])
        {
            var at = (corner.Item1 + dx, corner.Item2, corner.Item3 + dz);
            var clear = true;
            foreach (var c in ship.Cells)
            {
                var (x, y, z) = Place(c.X, c.Y, c.Z, quarters, at);
                var id = world.GetBlock(x, y, z).Value;
                if (_solid[id]) { clear = false; break; }
            }

            if (clear) return (quarters, at);
        }

        return null;
    }

    /// <summary>Where a local cell lands, turned by quarters about the helm cell, from a corner.</summary>
    public static (int X, int Y, int Z) Place(int x, int y, int z, int quarters, (int X, int Y, int Z) corner)
    {
        var q = ((quarters % 4) + 4) % 4;
        var (tx, tz) = q switch { 0 => (x, z), 1 => (-z, x), 2 => (-x, -z), _ => (z, -x) };
        return (corner.X + tx, corner.Y + y, corner.Z + tz);
    }

    /// <summary>
    /// Sets the ship back into the world as blocks, turned to its snapped heading, and bails out
    /// its hold. Returns false and leaves it sailing when nothing fits — unless <paramref name="force"/>,
    /// the world being saved and closed with the ship still out, when it moors where it is and any
    /// cell that would land in rock is lost rather than the whole ship.
    /// </summary>
    public bool Moor(VoxelWorld world, Ship ship, Action<int, int, int, BlockId> write,
        Action<(int X, int Y, int Z), (int X, int Y, int Z), object>? cargo = null, bool force = false)
    {
        var mooring = Mooring(world, ship);
        if (mooring is null && !force) return false;
        var (quarters, corner) = mooring ?? ((int)MathF.Round(ship.Yaw / 90f),
            ((int)MathF.Round(ship.Position.X), (int)MathF.Round(ship.Position.Y), (int)MathF.Round(ship.Position.Z)));

        foreach (var c in ship.Cells)
        {
            var (x, y, z) = Place(c.X, c.Y, c.Z, quarters, corner);
            if (_solid[world.GetBlock(x, y, z).Value]) continue;
            write(x, y, z, _turns.Turn(c.Id, quarters));
        }

        foreach (var (hx, hy, hz) in ship.Hold)
        {
            var (x, y, z) = Place(hx, hy, hz, quarters, corner);
            if (_fluid[world.GetBlock(x, y, z).Value]) write(x, y, z, BlockId.Air);
        }

        if (cargo is not null)
            foreach (var (local, what) in ship.Cargo)
                cargo(local, Place(local.X, local.Y, local.Z, quarters, corner), what);

        foreach (var (who, _) in ship.Crew) who.Seated = false;

        All.Remove(ship);
        return true;
    }

    /// <summary>Takes on everything standing on the deck: it rides where it stands until the ship moors.</summary>
    public void Muster(Ship ship, IEnumerable<Creature> creatures)
    {
        foreach (var who in creatures)
        {
            if (!who.Alive || who.Seated) continue;
            var local = Ship.Turn(who.Position - ship.Position - Ship.Pivot, -ship.Yaw) + Ship.Pivot;
            if (local.X < ship.Min.X - 0.5f || local.X > ship.Max.X + 1.5f
                || local.Z < ship.Min.Z - 0.5f || local.Z > ship.Max.Z + 1.5f
                || local.Y < ship.Min.Y || local.Y > ship.Max.Y + 3f) continue;

            // On the deck: a ship cell under its feet.
            var (cx, cy, cz) = ((int)MathF.Floor(local.X), (int)MathF.Floor(local.Y - 0.1f), (int)MathF.Floor(local.Z));
            if (!ship.Cells.Any(c => c.X == cx && c.Z == cz && c.Y <= cy && c.Y >= cy - 1)) continue;

            who.Seated = true;
            ship.Crew.Add((who, local));
        }
    }
}
