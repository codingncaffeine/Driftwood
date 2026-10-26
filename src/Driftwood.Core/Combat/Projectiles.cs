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
    FireBolt,

    /// <summary>A witch's flask: thrown, lobbed, and splashing where it lands.</summary>
    Flask,

    /// <summary>A cinderwisp's small fireball: straight and quick, and it sets alight.</summary>
    Ember,

    /// <summary>An ashwail's bolt: slow, bursting where it lands, and turned back by a blow.</summary>
    WailBolt,

    /// <summary>A seeker's eye, flying toward the Sanctum for a moment and then coming down.</summary>
    Seeker,

    /// <summary>The Ashwraith's skull (2026-10-25): quick, bursting where it lands, and withering.</summary>
    SkullBolt,
}

/// <summary>Who launched a projectile. Spells and creature shots can extend this without a new pool.</summary>
public enum ProjectileOwner : byte
{
    Player,
    Creature,
}

/// <summary>One live projectile as presentation needs to see it.</summary>
public readonly record struct ProjectileSnapshot(
    ProjectileKind Kind, ProjectileOwner Owner, Vector3 Position, Vector3 Velocity, float Age,
    Entities.ShotTag Tag = Entities.ShotTag.None, int Damage = 0);

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
    int Face,
    bool Player = false,
    Entities.ShotTag Tag = Entities.ShotTag.None);

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
    public const float FireBoltSpeed = 20f;

    /// <summary>A thrown flask's speed and fall: the reference's, a slow lob that drops hard.</summary>
    public const float FlaskSpeed = 15f;
    public const float FlaskGravity = 20f;

    /// <summary>The Emberdeep's fire: the reference's small fireball is quick, the ghast's slow.</summary>
    public const float EmberSpeed = 16f;
    public const float WailBoltSpeed = 9f;
    public const float SkullBoltSpeed = 12f;

    /// <summary>How near a bolt has to pass the point of a swing for the swing to send it back.</summary>
    public const float DeflectRadius = 1.1f;

    /// <summary>How long a seeker's eye flies before it comes down: the reference's two seconds.</summary>
    public const float SeekerLife = 2.2f;
    public const float SeekerSpeed = 8f;
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
        public Entities.ShotTag Tag;
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
        return new ProjectileSnapshot(shot.Kind, shot.Owner, shot.Position, shot.Velocity, shot.Age, shot.Tag, shot.Damage);
    }

    public bool ShootArrow(Vector3 origin, Vector3 direction, ProjectileOwner owner = ProjectileOwner.Player) =>
        Spawn(ProjectileKind.Arrow, owner, origin, direction, ArrowSpeed, ArrowGravity, ArrowDamage);

    public bool ThrowFarpearl(Vector3 origin, Vector3 direction, ProjectileOwner owner = ProjectileOwner.Player) =>
        Spawn(ProjectileKind.Farpearl, owner, origin, direction, FarpearlSpeed, FarpearlGravity, 0);

    public bool ShootFireBolt(Vector3 origin, Vector3 direction, int damage) =>
        Spawn(ProjectileKind.FireBolt, ProjectileOwner.Player, origin, direction,
            FireBoltSpeed, 0f, damage);

    /// <summary>Adds one flight with explicit numbers, which later spell data can call unchanged.</summary>
    public bool Spawn(
        ProjectileKind kind,
        ProjectileOwner owner,
        Vector3 origin,
        Vector3 direction,
        float speed,
        float gravity,
        int damage,
        Entities.ShotTag tag = Entities.ShotTag.None)
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
                Tag = tag,
            };
            Count++;
            return true;
        }

        return false;
    }

    /// <param name="player">
    /// The player's box, for shots a creature loosed. ⛔ A creature's arrow never tests the herd: the
    /// pool has no idea who fired it, and the first skeleton to shoot would have put the arrow in its
    /// own skull at the muzzle.
    /// </param>
    public void Update(VoxelWorld world, CreatureHerd? herd, float dt, (Vector3 Min, Vector3 Max)? player = null)
    {
        _impactCount = 0;
        if (dt <= 0f || Count == 0) return;

        for (var i = 0; i < Capacity; i++)
        {
            ref var shot = ref _slots[i];
            if (!shot.Active) continue;

            shot.Age += dt;

            // A seeker's eye ends its flight where it is, and says so: the host decides whether it
            // comes down whole or shatters.
            if (shot.Kind == ProjectileKind.Seeker && shot.Age >= SeekerLife)
            {
                Impact(shot, shot.Position, Vector3.UnitY, null,
                    (int)MathF.Floor(shot.Position.X), (int)MathF.Floor(shot.Position.Y), (int)MathF.Floor(shot.Position.Z), -1);
                Retire(ref shot);
                continue;
            }

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
            if (herd is not null && shot.Owner != ProjectileOwner.Creature)
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

            var playerAt = float.PositiveInfinity;
            if (shot.Owner == ProjectileOwner.Creature && player is { } box
                && CreatureHerd.RayBox(from, direction, box.Min, box.Max, out var into) && into <= distance)
                playerAt = into;

            if (playerAt < blockAt)
            {
                var hit = from + direction * playerAt;
                Impact(shot, hit, -direction, null, (int)MathF.Floor(hit.X), (int)MathF.Floor(hit.Y),
                    (int)MathF.Floor(hit.Z), -1, player: true);
                Retire(ref shot);
                continue;
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
        int face,
        bool player = false)
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
            face,
            player,
            shot.Tag);
    }

    /// <summary>
    /// Turns a creature's bolt back along the line of a swing, as the reference's ghast fireball is
    /// returned by a punch. True when one was close enough to turn.
    /// </summary>
    /// <remarks>
    /// Owned by the player from then on, so it tests the herd instead of the player — which is the
    /// whole of what sends it back to its maker.
    /// </remarks>
    public bool TryDeflect(Vector3 eye, Vector3 forward, float reach)
    {
        forward = Vector3.Normalize(forward);
        for (var i = 0; i < Capacity; i++)
        {
            ref var shot = ref _slots[i];
            if (!shot.Active || shot.Kind != ProjectileKind.WailBolt || shot.Owner != ProjectileOwner.Creature) continue;

            var to = shot.Position - eye;
            var along = Vector3.Dot(to, forward);
            if (along < 0f || along > reach) continue;
            if ((to - forward * along).Length() > DeflectRadius) continue;

            shot.Owner = ProjectileOwner.Player;
            shot.Velocity = forward * MathF.Max(shot.Velocity.Length(), WailBoltSpeed) * 1.4f;
            shot.Age = 0f;
            return true;
        }

        return false;
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
