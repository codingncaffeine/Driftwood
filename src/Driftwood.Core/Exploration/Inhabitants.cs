using System.Numerics;
using Driftwood.Core.Entities;

namespace Driftwood.Core.Exploration;

public enum Profession : byte { Shorewright, Forager, Waykeeper, Lorekeeper }

public sealed class Inhabitant
{
    public required string Id { get; init; }
    public required string SettlementId { get; init; }
    public required string Name { get; init; }
    public Profession Profession { get; init; }
    public Vector3 Position { get; set; }
    public float Yaw { get; set; }
    public (int X, int Y, int Z) Home { get; init; }
    public (int X, int Y, int Z) Work { get; init; }
    public (int X, int Y, int Z) Commons { get; init; }
    internal readonly Queue<Vector3> Path = new();
    internal float Repath;

    public string Kind => Profession switch
    {
        Profession.Shorewright => "shorewright",
        Profession.Forager => "forager",
        Profession.Waykeeper => "waykeeper",
        _ => "lorekeeper",
    };
}

public readonly record struct SavedInhabitant(
    string Id,
    string SettlementId,
    string Name,
    Profession Profession,
    Vector3 Position,
    float Yaw,
    int HomeX, int HomeY, int HomeZ,
    int WorkX, int WorkY, int WorkZ,
    int CommonsX, int CommonsY, int CommonsZ);

/// <summary>Settlement residents with ownership, schedules and bounded A* ground navigation.</summary>
public sealed class InhabitantSystem
{
    private const float WalkSpeed = 1.35f;
    private const int SearchRadius = 28;
    private readonly List<Inhabitant> _all = [];

    public IReadOnlyList<Inhabitant> All => _all;
    public bool Dirty { get; private set; }

    public void EnsureSettlement(ExplorationGenerator generator, StructureSite settlement)
    {
        if (settlement.Kind != StructureKind.Driftstead
            || _all.Any(one => one.SettlementId == settlement.Id)) return;

        var professions = Enum.GetValues<Profession>();
        var names = new[] { "Mara", "Tovin", "Elow", "Sable" };
        var residents = generator.Residents(settlement);
        for (var i = 0; i < residents.Count; i++)
        {
            var one = residents[i];
            _all.Add(new Inhabitant
            {
                Id = $"{settlement.Id}/resident/{i}",
                SettlementId = settlement.Id,
                Name = names[i % names.Length],
                Profession = professions[i % professions.Length],
                Position = new Vector3(one.X + 0.5f, one.Y, one.Z + 0.5f),
                Yaw = i * 120f,
                Home = (one.HomeX, one.Y, one.HomeZ),
                Work = (one.WorkX, one.Y, one.WorkZ),
                Commons = (settlement.X, one.Y, settlement.Z),
            });
        }
        Dirty = true;
    }

    public void Update(
        float dt,
        float dayTime,
        Func<int, int, int, bool> solid,
        Func<int, int, int, bool>? known = null,
        Func<int, int, int, bool>? spawnSupport = null)
    {
        spawnSupport ??= solid;
        foreach (var one in _all)
        {
            var cellX = (int)MathF.Floor(one.Position.X);
            var cellY = (int)MathF.Floor(one.Position.Y);
            var cellZ = (int)MathF.Floor(one.Position.Z);
            if (known is not null && !known(cellX, cellY, cellZ)) continue;

            // Generated and restored residents are resolved against the decorated live world, not
            // merely the terrain height their settlement was authored from. This prevents a tree
            // canopy from becoming their first floor while retaining the exact saved pose whenever
            // it is still a legal stand.
            if (!CanStand(cellX, cellY, cellZ, solid, spawnSupport, known))
            {
                if (!TryStandNear(cellX, cellY, cellZ, solid, spawnSupport, known, out var safe))
                    continue;
                one.Position = safe;
                one.Path.Clear();
                one.Repath = 0f;
                cellX = (int)MathF.Floor(safe.X);
                cellY = (int)MathF.Floor(safe.Y);
                cellZ = (int)MathF.Floor(safe.Z);
                Dirty = true;
            }

            one.Repath -= dt;
            var target = Scheduled(one, dayTime);
            if (one.Repath <= 0f && (one.Path.Count == 0 || Vector3.DistanceSquared(one.Path.Last(), target) > 3f))
            {
                one.Path.Clear();
                foreach (var step in FindPath(one.Position, target, solid, spawnSupport)) one.Path.Enqueue(step);
                one.Repath = 2.5f;
            }

            if (one.Path.Count == 0) continue;
            var next = one.Path.Peek();
            var delta = next - one.Position;
            delta.Y = 0f;
            if (delta.LengthSquared() < 0.08f)
            {
                one.Position = new Vector3(next.X, next.Y, next.Z);
                one.Path.Dequeue();
                Dirty = true;
                continue;
            }

            var heading = Vector3.Normalize(delta);
            var move = MathF.Min(WalkSpeed * dt, delta.Length());
            one.Position += heading * move;
            one.Position = new Vector3(one.Position.X, next.Y, one.Position.Z);
            one.Yaw = float.RadiansToDegrees(MathF.Atan2(heading.Z, heading.X));
            Dirty = true;
        }
    }

    public Inhabitant? Pick(Vector3 origin, Vector3 direction, float reach, out float distance)
    {
        Inhabitant? nearest = null;
        distance = reach;
        foreach (var one in _all)
        {
            var min = one.Position + new Vector3(-0.33f, 0f, -0.33f);
            var max = one.Position + new Vector3(0.33f, 1.9f, 0.33f);
            if (!CreatureHerd.RayBox(origin, direction, min, max, out var hit) || hit >= distance) continue;
            nearest = one;
            distance = hit;
        }
        return nearest;
    }

    public List<SavedInhabitant> Capture() =>
        [.. _all.Select(one => new SavedInhabitant(
            one.Id, one.SettlementId, one.Name, one.Profession, one.Position, one.Yaw,
            one.Home.X, one.Home.Y, one.Home.Z,
            one.Work.X, one.Work.Y, one.Work.Z,
            one.Commons.X, one.Commons.Y, one.Commons.Z))];

    public void Reload(IEnumerable<SavedInhabitant> saved)
    {
        _all.Clear();
        foreach (var one in saved)
            _all.Add(new Inhabitant
            {
                Id = one.Id,
                SettlementId = one.SettlementId,
                Name = one.Name,
                Profession = one.Profession,
                Position = one.Position,
                Yaw = one.Yaw,
                Home = (one.HomeX, one.HomeY, one.HomeZ),
                Work = (one.WorkX, one.WorkY, one.WorkZ),
                Commons = (one.CommonsX, one.CommonsY, one.CommonsZ),
            });
        Dirty = false;
    }

    public void Settled() => Dirty = false;

    private static Vector3 Scheduled(Inhabitant one, float dayTime)
    {
        var point = dayTime switch
        {
            < 0.22f => one.Home,
            < 0.50f => one.Work,
            < 0.70f => one.Commons,
            _ => one.Home,
        };
        return new Vector3(point.X + 0.5f, point.Y, point.Z + 0.5f);
    }

    /// <summary>A* over a bounded horizontal neighbourhood, allowing one-block steps.</summary>
    private static IReadOnlyList<Vector3> FindPath(
        Vector3 from,
        Vector3 to,
        Func<int, int, int, bool> solid,
        Func<int, int, int, bool> spawnSupport)
    {
        var start = (X: (int)MathF.Floor(from.X), Y: (int)MathF.Floor(from.Y), Z: (int)MathF.Floor(from.Z));
        var goal = (X: (int)MathF.Floor(to.X), Y: (int)MathF.Floor(to.Y), Z: (int)MathF.Floor(to.Z));
        var frontier = new PriorityQueue<(int X, int Y, int Z), int>();
        var came = new Dictionary<(int X, int Y, int Z), (int X, int Y, int Z)>();
        var cost = new Dictionary<(int X, int Y, int Z), int> { [start] = 0 };
        frontier.Enqueue(start, 0);
        var reached = start;
        var nearest = Manhattan(start, goal);

        while (frontier.TryDequeue(out var at, out _) && cost.Count <= 4096)
        {
            var distance = Manhattan(at, goal);
            if (distance < nearest) { nearest = distance; reached = at; }
            if (distance <= 1) { reached = at; break; }

            ReadOnlySpan<(int X, int Z)> directions = [(1, 0), (-1, 0), (0, 1), (0, -1)];
            foreach (var direction in directions)
            {
                var nx = at.X + direction.X;
                var nz = at.Z + direction.Z;
                if (Math.Abs(nx - start.Item1) > SearchRadius || Math.Abs(nz - start.Item3) > SearchRadius)
                    continue;

                var ny = WalkY(solid, spawnSupport, nx, at.Y, nz);
                if (ny is null) continue;
                var next = (nx, ny.Value, nz);
                var nextCost = cost[at] + 10 + Math.Abs(next.Item2 - at.Y) * 4;
                if (cost.TryGetValue(next, out var old) && old <= nextCost) continue;
                cost[next] = nextCost;
                came[next] = at;
                frontier.Enqueue(next, nextCost + Manhattan(next, goal) * 10);
            }
        }

        if (reached == start) return [];
        var reverse = new List<Vector3>();
        for (var at = reached; at != start && came.TryGetValue(at, out var previous); at = previous)
            reverse.Add(new Vector3(at.X + 0.5f, at.Y, at.Z + 0.5f));
        reverse.Reverse();
        return reverse;
    }

    private static int? WalkY(
        Func<int, int, int, bool> solid,
        Func<int, int, int, bool> spawnSupport,
        int x,
        int aroundY,
        int z)
    {
        for (var offset = 1; offset >= -1; offset--)
        {
            var feet = aroundY + offset;
            if (!solid(x, feet - 1, z) || !spawnSupport(x, feet - 1, z)
                || solid(x, feet, z) || solid(x, feet + 1, z)) continue;
            return feet;
        }
        return null;
    }

    private static bool CanStand(
        int x,
        int feet,
        int z,
        Func<int, int, int, bool> solid,
        Func<int, int, int, bool> spawnSupport,
        Func<int, int, int, bool>? known) =>
        (known is null || known(x, feet - 1, z) && known(x, feet + 1, z))
        && solid(x, feet - 1, z)
        && spawnSupport(x, feet - 1, z)
        && !solid(x, feet, z)
        && !solid(x, feet + 1, z);

    private static bool TryStandNear(
        int centreX,
        int centreY,
        int centreZ,
        Func<int, int, int, bool> solid,
        Func<int, int, int, bool> spawnSupport,
        Func<int, int, int, bool>? known,
        out Vector3 at)
    {
        for (var radius = 0; radius <= 12; radius++)
        for (var dz = -radius; dz <= radius; dz++)
        for (var dx = -radius; dx <= radius; dx++)
        {
            if (radius > 0 && Math.Max(Math.Abs(dx), Math.Abs(dz)) != radius) continue;
            var x = centreX + dx;
            var z = centreZ + dz;
            if (!CreatureHerd.TryGround(solid, spawnSupport, x, z, centreY + 8, out var y)) continue;
            var feet = (int)y;
            if (!CanStand(x, feet, z, solid, spawnSupport, known)) continue;
            at = new Vector3(x + 0.5f, y, z + 0.5f);
            return true;
        }

        at = default;
        return false;
    }

    private static int Manhattan((int X, int Y, int Z) a, (int X, int Y, int Z) b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Z - b.Z) + Math.Abs(a.Y - b.Y);
}
