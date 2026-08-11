using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;
using Driftwood.Core.Physics;
using Driftwood.Core.World;

namespace Driftwood.Core.Projectiles;

/// <summary>The ordinary things the shared flight pool currently carries.</summary>
public enum ProjectileKind : byte
{
    Arrow,
    Farpearl,
}

/// <summary>Who launched a projectile. Spells and creature shots can extend this without a new pool.</summary>
public enum ProjectileOwner : byte
{
    Player,
    Creature,
}

/// <summary>One live projectile as presentation needs to see it.</summary>
public readonly record struct ProjectileSnapshot(
    ProjectileKind Kind, ProjectileOwner Owner, Vector3 Position, Vector3 Velocity, float Age);

/// <summary>One projectile reaching a block or creature during the last update.</summary>
public readonly record struct ProjectileImpact(
    ProjectileKind Kind,
    ProjectileOwner Owner,
    Vector3 Position,
    Vector3 Normal,
    Vector3 Velocity,
    int Damage,
    Creature? Creature,
    int X,
    int Y,
    int Z,
    int Face);

/// <summary>
/// A fixed flight pool, swept against the world's real block shapes and every living creature.
/// </summary>
/// <remarks>
/// <para>Fixed means exactly that: spawning and stepping allocate nothing. A full pool refuses the
/// next shot rather than growing during a fight, and impacts are handed out through a span over a
/// second fixed array. The later magic projectiles use this same machinery; bow shots and the
/// farpearl are its first consumers rather than special physics in the client.</para>
/// <para>A path that reaches an unloaded chunk is retired with no impact. Unloaded space reads as
/// air, and letting a shot continue through it would make the result depend on whether streaming
/// happened to finish before the next frame.</para>
/// </remarks>
public sealed class ProjectileSystem
{
    public const int Capacity = 128;
    public const float ArrowSpeed = 30f;
    public const float ArrowGravity = 12f;
    public const int ArrowDamage = 5;
    public const float FarpearlSpeed = 22f;
    public const float FarpearlGravity = 10f;
    public const float Lifetime = 20f;

    private struct Slot
    {
        public bool Active;
        public ProjectileKind Kind;
        public ProjectileOwner Owner;
        public Vector3 Position;
        public Vector3 Velocity;
        public float Gravity;
        public float Age;
        public int Damage;
    }

    private readonly Slot[] _slots = new Slot[Capacity];
    private readonly ProjectileImpact[] _impacts = new ProjectileImpact[Capacity];
    private readonly (Vector3 Min, Vector3 Max)[][] _shapes;
    private int _impactCount;

    public ProjectileSystem(BlockRegistry registry) => _shapes = registry.BuildCollisionTable(out _);

    public int Count { get; private set; }

    /// <summary>Impacts made by the most recent <see cref="Update"/>; valid until the next one.</summary>
    public ReadOnlySpan<ProjectileImpact> Impacts => _impacts.AsSpan(0, _impactCount);

    public bool ActiveAt(int index) => (uint)index < Capacity && _slots[index].Active;

    public ProjectileSnapshot SnapshotAt(int index)
    {
        ref readonly var shot = ref _slots[index];
        return new ProjectileSnapshot(shot.Kind, shot.Owner, shot.Position, shot.Velocity, shot.Age);
    }

    public bool ShootArrow(Vector3 origin, Vector3 direction, ProjectileOwner owner = ProjectileOwner.Player) =>
        Spawn(ProjectileKind.Arrow, owner, origin, direction, ArrowSpeed, ArrowGravity, ArrowDamage);

    public bool ThrowFarpearl(Vector3 origin, Vector3 direction, ProjectileOwner owner = ProjectileOwner.Player) =>
        Spawn(ProjectileKind.Farpearl, owner, origin, direction, FarpearlSpeed, FarpearlGravity, 0);

    /// <summary>Adds one flight with explicit numbers, which later spell data can call unchanged.</summary>
    public bool Spawn(
        ProjectileKind kind,
        ProjectileOwner owner,
        Vector3 origin,
        Vector3 direction,
        float speed,
        float gravity,
        int damage)
    {
        var lengthSquared = direction.LengthSquared();
        if (lengthSquared < 1e-8f || speed <= 0f) return false;
        direction /= MathF.Sqrt(lengthSquared);

        for (var i = 0; i < Capacity; i++)
        {
            if (_slots[i].Active) continue;

            _slots[i] = new Slot
            {
                Active = true,
                Kind = kind,
                Owner = owner,
                Position = origin,
                Velocity = direction * speed,
                Gravity = MathF.Max(0f, gravity),
                Damage = Math.Max(0, damage),
            };
            Count++;
            return true;
        }

        return false;
    }

    public void Update(VoxelWorld world, CreatureHerd? herd, float dt)
    {
        _impactCount = 0;
        if (dt <= 0f || Count == 0) return;

        for (var i = 0; i < Capacity; i++)
        {
            ref var shot = ref _slots[i];
            if (!shot.Active) continue;

            shot.Age += dt;
            if (shot.Age >= Lifetime)
            {
                Retire(ref shot);
                continue;
            }

            var from = shot.Position;
            shot.Velocity.Y -= shot.Gravity * dt;
            var to = from + shot.Velocity * dt;
            var travel = to - from;
            var distance = travel.Length();
            if (distance < 1e-7f) continue;
            var direction = travel / distance;

            var hasBlock = BlockShapes.SweepPoint(_shapes, world, from, to, out var block);
            var blockAt = hasBlock ? block.Fraction * distance : float.PositiveInfinity;

            Creature? creature = null;
            var creatureAt = float.PositiveInfinity;
            if (herd is not null)
            {
                foreach (var candidate in herd.All)
                {
                    if (!candidate.Alive) continue;
                    var (min, max) = candidate.Bounds();
                    if (!CreatureHerd.RayBox(from, direction, min, max, out var at)
                        || at > distance || at >= creatureAt)
                        continue;

                    creature = candidate;
                    creatureAt = at;
                }
            }

            var hitCreature = creature is not null && creatureAt < blockAt;
            var end = hitCreature
                ? from + direction * creatureAt
                : hasBlock ? block.Position : to;

            if (!PathIsLoaded(world, from, end))
            {
                Retire(ref shot);
                continue;
            }

            if (hitCreature)
            {
                Impact(
                    shot,
                    end,
                    -direction,
                    creature,
                    (int)MathF.Floor(end.X),
                    (int)MathF.Floor(end.Y),
                    (int)MathF.Floor(end.Z),
                    -1);
                Retire(ref shot);
                continue;
            }

            if (hasBlock)
            {
                Impact(shot, block.Position, block.Normal, null, block.X, block.Y, block.Z, block.Face);
                Retire(ref shot);
                continue;
            }

            shot.Position = to;
        }
    }

    private void Impact(
        in Slot shot,
        Vector3 position,
        Vector3 normal,
        Creature? creature,
        int x,
        int y,
        int z,
        int face)
    {
        _impacts[_impactCount++] = new ProjectileImpact(
            shot.Kind,
            shot.Owner,
            position,
            normal,
            shot.Velocity,
            shot.Damage,
            creature,
            x,
            y,
            z,
            face);
    }

    private void Retire(ref Slot shot)
    {
        shot.Active = false;
        Count--;
    }

    /// <summary>
    /// Samples at half-cell intervals. This is not collision detection—the exact sweep above is—
    /// it only proves no <see cref="Chunk.Size"/>-cell chunk boundary was crossed while absent.
    /// </summary>
    private static bool PathIsLoaded(VoxelWorld world, Vector3 from, Vector3 to)
    {
        var distance = Vector3.Distance(from, to);
        var steps = Math.Max(1, (int)MathF.Ceiling(distance * 2f));

        for (var i = 0; i <= steps; i++)
        {
            var at = Vector3.Lerp(from, to, i / (float)steps);
            if (!world.TryGetChunk(
                    ChunkPos.FromWorld(
                        (int)MathF.Floor(at.X),
                        (int)MathF.Floor(at.Y),
                        (int)MathF.Floor(at.Z)),
                    out _))
                return false;
        }

        return true;
    }
}
