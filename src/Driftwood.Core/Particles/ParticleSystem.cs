using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Physics;
using Driftwood.Core.World;

namespace Driftwood.Core.Particles;

/// <summary>How a particle is drawn, which is a different question from what it is doing.</summary>
/// <remarks>
/// ⛳ <b>Three looks rather than a flag per effect.</b> Every particle in the game until now was a
/// crop of a block's own tile, discarded where it was clear and lit by the cell it stood in — which
/// is exactly right for a chip of stone and cannot express a flame at all. Fire makes its own light
/// and has no edges to cut out; smoke has no colour of its own and has to thin rather than vanish.
/// Naming the three lets one pool hold all of them and the renderer sort them into two passes.
/// </remarks>
public enum ParticleLook
{
    /// <summary>A chip of something: cut out, lit by the world, gone the instant its time is up.</summary>
    Debris = 0,

    /// <summary>Fire: blended, full bright whatever the hour, thinning as it rises.</summary>
    Flame,

    /// <summary>Smoke: blended, lit by the world, thinning and spreading as it goes.</summary>
    Smoke,

    /// <summary>A tinted translucent mote: alpha blended and lit by the world.</summary>
    Soft,

    /// <summary>A tinted magical glint: additive and self-lit, used sparingly for legibility.</summary>
    Glow,
}

/// <summary>
/// One allocation-free particle recipe. Semantic effects compose these recipes into rings, trails,
/// sprays and columns; the renderer never needs to know which spell or interaction asked for them.
/// </summary>
public readonly record struct ParticleRecipe(
    ushort Layer,
    ParticleLook Look,
    Vector4 Tint,
    float Size,
    float Life,
    float SizeJitter = 0f,
    float LifeJitter = 0f,
    float Fall = 0f,
    float Drag = 1.4f,
    float Grow = 0f,
    float Sway = 0f,
    float Spin = 0f,
    bool FullTile = true);

/// <summary>One live particle: a crop of a block's texture, thrown and falling.</summary>
public struct Particle
{
    public Vector3 Position;
    public Vector3 Velocity;

    /// <summary>How it is drawn. Not what it is doing — a flame and a chip move by the same code.</summary>
    public ParticleLook Look;

    /// <summary>Blocks a second the half-width grows by. Negative shrinks; zero holds.</summary>
    /// <remarks>
    /// ⚠ <b>What makes smoke read as smoke.</b> A puff that keeps its size and fades out reads as a
    /// picture being turned down; one that spreads while it thins reads as air taking it. It is one
    /// float and it is most of the difference.
    /// </remarks>
    public float Grow;

    /// <summary>Seconds since it was spawned.</summary>
    public float Age;

    /// <summary>Seconds it lasts.</summary>
    public float Life;

    /// <summary>Half-width on screen, in blocks.</summary>
    public float Size;

    /// <summary>Texture array layer it reads.</summary>
    public ushort Layer;

    /// <summary>Which crop of that tile, on a grid of <see cref="ParticleSystem.CropsPerAxis"/>.</summary>
    public byte CropX;
    public byte CropY;

    /// <summary>
    /// True for authored spark/rune/heart/bubble silhouettes; false for random material crops.
    /// </summary>
    public bool FullTile;

    /// <summary>
    /// Share of gravity this feels. One for anything thrown, a fraction for anything that drifts.
    /// </summary>
    /// <remarks>
    /// Per particle rather than per kind, because the same pool has to hold a chip of stone falling
    /// like a chip of stone and a leaf taking its time about it — and rain and snow after that.
    /// </remarks>
    public float Fall;

    /// <summary>How far it wanders sideways as it falls, in blocks a second.</summary>
    public float Sway;

    /// <summary>Colour and opacity multiplied over the pack/fallback particle shape.</summary>
    public Vector4 Tint;

    /// <summary>Share of velocity shed per second. Per particle so a streak need not move like dust.</summary>
    public float Drag;

    /// <summary>Billboard rotation and angular velocity, in radians.</summary>
    public float Rotation;
    public float Spin;

    /// <summary>True on the frames it is resting on something.</summary>
    public bool Grounded;
}

/// <summary>
/// The debris: chips off a block being mined, the burst when it gives, dust under a landing.
/// </summary>
/// <remarks>
/// <para>The whole simulation lives away from the renderer so it can be run without a window. What a
/// particle system gets wrong is never how it looks in one screenshot — it is a burst that falls
/// through the floor, a pool that leaks until the frame rate sags an hour into play, or a spawn that
/// allocates. All three are questions with numeric answers and none of them needs a screen.</para>
/// <para>Fixed pool, no allocation, and a spawn past the end is refused and counted rather than
/// growing the array. A particle system that quietly grows is one that has already lost the argument
/// about how many particles there should be.</para>
/// <para>Each particle shows a small crop of the block's own tile rather than a flat colour. That is
/// the genre's own trick and it is most of the effect: the debris off stone is grey speckled stone
/// and the debris off a plank is a piece of plank, without anything anywhere naming a colour.</para>
/// </remarks>
public sealed class ParticleSystem
{
    /// <summary>Particles alive at once before new ones are refused.</summary>
    public const int Capacity = 4096;

    /// <summary>Crops per axis of a block tile. Four gives sixteen distinct chips of any block.</summary>
    public const int CropsPerAxis = 4;

    /// <summary>
    /// Debris falls more slowly than a player does.
    /// </summary>
    /// <remarks>
    /// Deliberately not the body's 32. A chip under real gravity is on the floor within a fifth of a
    /// second and the burst reads as a flicker; slowing it lets the spray hang long enough to be
    /// seen, which is the entire purpose of it.
    /// </remarks>
    private const float Gravity = 17f;

    /// <summary>Air resistance, as a share of speed shed per second.</summary>
    private const float DefaultDrag = 1.4f;

    /// <summary>How much of its downward speed a chip keeps after hitting something.</summary>
    private const float Bounce = 0.22f;

    private readonly Particle[] _particles = new Particle[Capacity];
    private readonly (Vector3 Min, Vector3 Max)[][] _shapes;
    private uint _rng;

    /// <summary>Particles currently alive.</summary>
    public int Count { get; private set; }

    /// <summary>Spawns refused because the pool was full, since the world opened.</summary>
    public int Refused { get; private set; }

    /// <summary>Everything alive, packed at the front of the pool.</summary>
    public ReadOnlySpan<Particle> Live => _particles.AsSpan(0, Count);

    public ParticleSystem(BlockRegistry registry, uint seed = 0x9E3779B9)
    {
        _shapes = registry.BuildCollisionTable(out _);
        _rng = seed | 1u;
    }

    /// <summary>Throws everything away. Used when the world is torn down.</summary>
    public void Clear() => Count = 0;

    /// <summary>
    /// Advances every particle, and retires the ones whose time is up.
    /// </summary>
    /// <remarks>
    /// The dead are removed by swapping the last live one into the gap, so the array stays packed
    /// and the walk stays linear. Order is not meaningful — nothing about a chip depends on which
    /// other chip it was spawned beside.
    /// </remarks>
    public void Update(VoxelWorld world, float dt)
    {
        var i = 0;
        while (i < Count)
        {
            ref var p = ref _particles[i];

            p.Age += dt;
            if (p.Age >= p.Life)
            {
                _particles[i] = _particles[--Count];
                continue;
            }

            p.Velocity.Y -= Gravity * p.Fall * dt;
            p.Velocity -= p.Velocity * MathF.Min(p.Drag * dt, 1f);

            if (p.Grow != 0f) p.Size = MathF.Max(0.004f, p.Size + p.Grow * dt);
            if (p.Spin != 0f) p.Rotation += p.Spin * dt;

            Move(world, ref p, dt);
            i++;
        }
    }

    /// <summary>A block coming apart: its own texture sprayed out of the cell it filled.</summary>
    public void Burst(BlockType type, int x, int y, int z, int count = 26)
    {
        for (var i = 0; i < count; i++)
        {
            var position = new Vector3(x + Unit(), y + Unit(), z + Unit());
            var velocity = new Vector3(Signed() * 2.6f, Unit() * 3.4f + 0.7f, Signed() * 2.6f);
            Spawn(type, position, velocity, 0.09f + Unit() * 0.045f, 0.55f + Unit() * 0.55f);
        }
    }

    /// <summary>Chips off the face being struck, thrown back the way the blow came from.</summary>
    public void Chip(BlockType type, int x, int y, int z, int face, int count = 4)
    {
        var n = Faces.Normals[face];
        var normal = new Vector3(n.X, n.Y, n.Z);

        for (var i = 0; i < count; i++)
        {
            // On the struck face, jittered across it rather than out of its middle.
            var spread = new Vector3(Signed(), Signed(), Signed()) * 0.34f;
            spread -= normal * Vector3.Dot(spread, normal);

            var position = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) + normal * 0.53f + spread;
            var velocity = normal * (1.1f + Unit() * 1.4f)
                         + new Vector3(Signed(), Signed() * 0.6f + 0.7f, Signed()) * 1.0f;

            Spawn(type, position, velocity, 0.055f + Unit() * 0.03f, 0.35f + Unit() * 0.35f);
        }
    }

    /// <summary>Dust kicked up by a foot, or knocked out of the ground by a landing.</summary>
    public void Puff(BlockType type, Vector3 at, int count = 6, float strength = 1f)
    {
        for (var i = 0; i < count; i++)
        {
            var position = at + new Vector3(Signed() * 0.30f, 0.06f, Signed() * 0.30f);
            var velocity = new Vector3(Signed() * 1.5f, Unit() * 1.1f + 0.2f, Signed() * 1.5f) * strength;
            Spawn(type, position, velocity, 0.06f + Unit() * 0.04f, 0.35f + Unit() * 0.4f);
        }
    }

    /// <summary>
    /// A leaf letting go: falls slowly, wanders as it comes, and lasts long enough to arrive.
    /// </summary>
    /// <remarks>
    /// The first emitter here that is not a reaction to something the player did. It is also the
    /// shape rain and snow want — a slow fall with a sideways wander — so the two fields it needed
    /// are on the particle rather than in this method.
    /// </remarks>
    public void Leaf(BlockType type, Vector3 at)
    {
        Spawn(
            type,
            at + new Vector3(Signed() * 0.42f, -0.05f, Signed() * 0.42f),
            new Vector3(Signed() * 0.25f, -0.25f, Signed() * 0.25f),
            0.055f + Unit() * 0.03f,
            5f + Unit() * 4f,
            fall: 0.045f,
            sway: 0.55f + Unit() * 0.5f);
    }

    /// <summary>
    /// A live fire: tongues rising out of a cell, sized to whatever is burning.
    /// </summary>
    /// <param name="scale">
    /// How big a fire this is. A torch is about 0.35, a campfire 1, a furnace mouth 0.5 — so one
    /// emitter serves everything that burns and the block says how much it burns by.
    /// </param>
    /// <remarks>
    /// <para>⛳ <b>Rising, shrinking, and short-lived.</b> A flame is not debris that happens to be
    /// orange: it goes up rather than falling, it narrows as it goes rather than holding its size,
    /// and it lasts under a second — a long-lived flame particle reads as a floating ember. Negative
    /// <see cref="Particle.Fall"/> is what carries it up, so the existing integrator needs nothing
    /// added to it.</para>
    /// <para>⚠ The spread is a <em>disc</em> rather than a cube: fire comes off the top of a thing,
    /// and scattering it through the volume of the cell puts half the tongues inside the log.</para>
    /// </remarks>
    public void Flame(Vector3 at, float scale, ushort layer, int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            var spread = 0.16f * scale;
            var position = at + new Vector3(Signed() * spread, Unit() * 0.06f * scale, Signed() * spread);
            var velocity = new Vector3(Signed() * 0.12f, 0.5f + Unit() * 0.5f, Signed() * 0.12f) * scale;

            Spawn(
                layer, position, velocity,
                size: (0.055f + Unit() * 0.045f) * scale,
                life: 0.32f + Unit() * 0.30f,
                fall: -0.09f,
                look: ParticleLook.Flame,
                grow: -0.10f * scale);
        }
    }

    /// <summary>
    /// Smoke: goes up, spreads, thins out, and is gone.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>It lasts several times as long as a flame and grows the whole time.</b> That is the
    /// difference between a puff dissipating and a puff being switched off — air takes smoke apart
    /// rather than turning it down, and the eye reads spreading as the thing that happened.
    /// </remarks>
    public void Smoke(Vector3 at, float scale, ushort layer, int count = 1, float rise = 1f)
    {
        for (var i = 0; i < count; i++)
        {
            var spread = 0.20f * scale;
            var position = at + new Vector3(Signed() * spread, Unit() * 0.1f * scale, Signed() * spread);
            var velocity = new Vector3(Signed() * 0.18f, (0.35f + Unit() * 0.35f) * rise, Signed() * 0.18f);

            Spawn(
                layer, position, velocity,
                size: (0.05f + Unit() * 0.05f) * scale,
                life: 1.4f + Unit() * 1.6f,
                fall: -0.02f,
                sway: 0.12f + Unit() * 0.16f,
                look: ParticleLook.Smoke,
                grow: 0.09f * scale);
        }
    }

    /// <summary>
    /// The puff something leaves when it dies: a burst of smoke that spreads and is gone.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Thrown outward as well as up</b>, unlike a chimney's. A column rising from a corpse
    /// reads as the corpse being on fire; a ball that expands and lifts reads as the thing having
    /// gone. Sized off the creature so a chicken and a cow do not leave the same cloud.
    /// </remarks>
    public void DeathPuff(Vector3 at, float scale, ushort layer, int count = 14)
    {
        for (var i = 0; i < count; i++)
        {
            var direction = new Vector3(Signed(), Unit() * 0.7f, Signed());
            var position = at + direction * 0.28f * scale;

            Spawn(
                layer,
                position,
                direction * (0.8f + Unit() * 0.8f) + new Vector3(0f, 0.5f, 0f),
                size: (0.07f + Unit() * 0.06f) * scale,
                life: 0.75f + Unit() * 0.7f,
                fall: -0.03f,
                look: ParticleLook.Smoke,
                grow: 0.16f * scale);
        }
    }

    /// <summary>Emits one particle from a reusable recipe.</summary>
    public void Emit(in ParticleRecipe recipe, Vector3 position, Vector3 velocity)
    {
        var size = MathF.Max(0.004f, recipe.Size + Signed() * recipe.SizeJitter);
        var life = MathF.Max(0.03f, recipe.Life + Signed() * recipe.LifeJitter);
        var spin = recipe.Spin == 0f ? 0f : Signed() * MathF.Abs(recipe.Spin);

        Spawn(
            recipe.Layer, position, velocity, size, life,
            recipe.Fall, recipe.Sway, recipe.Look, recipe.Grow,
            recipe.Tint, MathF.Max(0f, recipe.Drag), spin, recipe.FullTile);
    }

    /// <summary>A directed cone used for impacts, carving, smithing and material reactions.</summary>
    public void Spray(
        in ParticleRecipe recipe, Vector3 at, Vector3 normal, int count,
        float speed, float spread = 0.65f, float radius = 0.05f)
    {
        normal = SafeNormal(normal, Vector3.UnitY);
        Basis(normal, out var across, out var up);

        for (var i = 0; i < count; i++)
        {
            var side = across * Signed() + up * Signed();
            var direction = SafeNormal(normal + side * spread, normal);
            Emit(
                recipe,
                at + across * Signed() * radius + up * Signed() * radius,
                direction * speed * (0.55f + Unit() * 0.75f));
        }
    }

    /// <summary>An omnidirectional burst used for hits, ends and compact spell impacts.</summary>
    public void Sphere(
        in ParticleRecipe recipe, Vector3 at, int count, float speed, float radius = 0f,
        float upward = 0f)
    {
        for (var i = 0; i < count; i++)
        {
            var direction = SafeNormal(
                new Vector3(Signed(), Signed() + upward, Signed()), Vector3.UnitY);
            Emit(recipe, at + direction * radius * Unit(), direction * speed * (0.55f + Unit() * 0.75f));
        }
    }

    /// <summary>
    /// A world-space ring. Positive speed travels outward; negative speed collapses inward. Rings
    /// make shields, roots, summons and portal ownership readable without a dedicated mesh.
    /// </summary>
    public void Ring(
        in ParticleRecipe recipe, Vector3 at, Vector3 normal, int count,
        float radius, float speed = 0f)
    {
        normal = SafeNormal(normal, Vector3.UnitY);
        Basis(normal, out var across, out var up);
        var phase = Unit() * MathF.Tau;

        for (var i = 0; i < count; i++)
        {
            var angle = phase + MathF.Tau * i / Math.Max(count, 1);
            var radial = across * MathF.Cos(angle) + up * MathF.Sin(angle);
            Emit(recipe, at + radial * radius, radial * speed);
        }
    }

    /// <summary>A sampled line for direct streaks, drains and projectile trails.</summary>
    public void Trail(
        in ParticleRecipe recipe, Vector3 from, Vector3 to, int count,
        float jitter = 0.03f, float drift = 0f)
    {
        if (count <= 0) return;
        var line = to - from;
        var direction = SafeNormal(line, Vector3.UnitY);
        Basis(direction, out var across, out var up);

        for (var i = 0; i < count; i++)
        {
            var t = count == 1 ? 0.5f : i / (float)(count - 1);
            var offset = across * Signed() * jitter + up * Signed() * jitter;
            Emit(recipe, from + line * t + offset, direction * drift);
        }
    }

    /// <summary>Rising motes distributed through a bounded cylinder.</summary>
    public void Column(
        in ParticleRecipe recipe, Vector3 at, int count, float radius, float height, float rise)
    {
        for (var i = 0; i < count; i++)
        {
            var angle = Unit() * MathF.Tau;
            var distance = MathF.Sqrt(Unit()) * radius;
            var position = at + new Vector3(
                MathF.Cos(angle) * distance, Unit() * height, MathF.Sin(angle) * distance);
            var velocity = new Vector3(Signed() * 0.08f, rise * (0.65f + Unit() * 0.7f), Signed() * 0.08f);
            Emit(recipe, position, velocity);
        }
    }

    private void Spawn(
        BlockType type, Vector3 position, Vector3 velocity, float size, float life,
        float fall = 1f, float sway = 0f) =>
        Spawn(
            type.Model.ParticleLayer, position, velocity, size, life, fall, sway,
            ParticleLook.Debris, 0f);

    private void Spawn(
        ushort layer, Vector3 position, Vector3 velocity, float size, float life,
        float fall = 1f, float sway = 0f,
        ParticleLook look = ParticleLook.Debris, float grow = 0f,
        Vector4 tint = default, float drag = DefaultDrag, float spin = 0f,
        bool fullTile = false)
    {
        if (Count >= Capacity)
        {
            Refused++;
            return;
        }

        if (tint == default) tint = Vector4.One;

        _particles[Count++] = new Particle
        {
            Position = position,
            Velocity = velocity,
            Age = 0f,
            Life = life,
            Size = size,
            Layer = layer,
            CropX = (byte)(NextBits() % CropsPerAxis),
            CropY = (byte)(NextBits() % CropsPerAxis),
            FullTile = fullTile,
            Fall = fall,
            Sway = sway,
            Look = look,
            Grow = grow,
            Tint = tint,
            Drag = drag,
            Rotation = Unit() * MathF.Tau,
            Spin = spin,
        };
    }

    /// <summary>
    /// Moves one particle, one axis at a time, stopping at anything solid.
    /// </summary>
    /// <remarks>
    /// Axis at a time rather than as one swept step, exactly as the player's body does it. A single
    /// combined test cannot say which wall was hit, so a chip that clips a corner either passes
    /// through it or loses all its speed at once; separating the axes lets it slide along the
    /// surface it actually met, which is what makes debris settle into corners instead of on them.
    /// </remarks>
    private void Move(VoxelWorld world, ref Particle p, float dt)
    {
        var step = p.Velocity * dt;
        p.Grounded = false;

        // ⛔ Fire and smoke pass through everything, and they have to. A campfire's collision box is
        // the whole cell — it has to be, or a body would stand in the fire — so a flame born in the
        // middle of one starts inside a solid, and a collider would pin it there for its whole life.
        // Nothing that is not a solid object should be asking a solidity table anything.
        if (p.Look != ParticleLook.Debris)
        {
            if (p.Sway > 0f)
            {
                var drift = (p.Age + p.Position.X * 1.7f + p.Position.Z * 2.3f) * 2.2f;
                step.X += MathF.Sin(drift) * p.Sway * dt;
                step.Z += MathF.Cos(drift * 0.8f) * p.Sway * dt;
            }

            p.Position += step;
            return;
        }

        // The wander, for anything that has one. Phase comes off the spawn position rather than
        // from a stored angle, so a hundred leaves coming off one canopy are not all leaning the
        // same way at the same moment — and it costs no field to say so.
        if (p.Sway > 0f)
        {
            var phase = (p.Age + p.Position.X * 1.7f + p.Position.Z * 2.3f) * 2.2f;
            step.X += MathF.Sin(phase) * p.Sway * dt;
            step.Z += MathF.Cos(phase * 0.8f) * p.Sway * dt;
        }

        var next = p.Position with { X = p.Position.X + step.X };
        if (Blocked(world, next)) p.Velocity.X = 0f;
        else p.Position = next;

        next = p.Position with { Y = p.Position.Y + step.Y };
        if (Blocked(world, next))
        {
            if (step.Y < 0f) p.Grounded = true;
            p.Velocity.Y = -p.Velocity.Y * Bounce;
            if (MathF.Abs(p.Velocity.Y) < 0.45f) p.Velocity.Y = 0f;
        }
        else
        {
            p.Position = next;
        }

        next = p.Position with { Z = p.Position.Z + step.Z };
        if (Blocked(world, next)) p.Velocity.Z = 0f;
        else p.Position = next;

        // Ground friction, so a burst comes to rest rather than skating away down a hillside.
        if (!p.Grounded) return;
        p.Velocity.X *= 0.55f;
        p.Velocity.Z *= 0.55f;
    }

    private bool Blocked(VoxelWorld world, Vector3 at) => BlockShapes.Inside(_shapes, world, at);

    private static Vector3 SafeNormal(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : fallback;

    private static void Basis(Vector3 normal, out Vector3 across, out Vector3 up)
    {
        var helper = MathF.Abs(normal.Y) < 0.92f ? Vector3.UnitY : Vector3.UnitX;
        across = Vector3.Normalize(Vector3.Cross(normal, helper));
        up = Vector3.Normalize(Vector3.Cross(across, normal));
    }

    /// <summary>0 to 1.</summary>
    private float Unit() => (NextBits() & 0xFFFFFF) / (float)0x1000000;

    /// <summary>-1 to 1.</summary>
    private float Signed() => Unit() * 2f - 1f;

    private uint NextBits()
    {
        // xorshift32. Deterministic from the seed, so a headless run of the same spawns is the
        // same spawns — which is what lets a burst be measured rather than eyeballed.
        _rng ^= _rng << 13;
        _rng ^= _rng >> 17;
        _rng ^= _rng << 5;
        return _rng;
    }
}
