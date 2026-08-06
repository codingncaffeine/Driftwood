using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.World;

namespace Driftwood.Core.Particles;

/// <summary>
/// Finds everything burning near the player and keeps it burning.
/// </summary>
/// <remarks>
/// <para>⛳ <b>A found list refreshed slowly, and emission every frame from the list.</b> Fire has to
/// be emitted continuously — a flame lasts a third of a second, so anything less than every frame
/// reads as a stutter — but finding it means sweeping the cells round the player, and doing that
/// sixty times a second to place four particles is the wrong shape entirely. Sweeping twice a second
/// and emitting from what the sweep found is the same picture for a fortieth of the cost.</para>
/// <para>⛔ <b>The sweep walks CHUNKS, not cells.</b> A radius of sixteen is 35,937 cells, and asking
/// <see cref="VoxelWorld.GetBlock"/> for each is 35,937 dictionary lookups — twice a second, forever,
/// for a handful of torches. Walking the chunks that overlap the box and indexing their arrays
/// directly is the same answer with one lookup per chunk.</para>
/// <para>⚠ <b>What it finds is a position and two numbers, not a block.</b> By the time a flame is
/// being placed the block it came off may have been broken; carrying the sizes means the emitter
/// never reads the world between sweeps, and the worst a stale entry can do is put one more tongue
/// of fire in the air half a second after a torch came down.</para>
/// </remarks>
public sealed class Fires
{
    /// <summary>One thing that is burning: where, how much fire, how much smoke.</summary>
    public readonly record struct Fire(
        Vector3 Flame, float FlameScale, Vector3 Smoke, float SmokeScale);

    /// <summary>How far out to look. Beyond this a torch is a light, not a fire you can see.</summary>
    public const int Reach = 16;

    /// <summary>Seconds between sweeps.</summary>
    public const float SweepEvery = 0.5f;

    /// <summary>
    /// The most fires kept at once.
    /// </summary>
    /// <remarks>
    /// ⚠ A cap rather than a list that grows. A player who tiles a room with a hundred torches gets
    /// the nearest sixty-four burning and the rest lit but still, which is a far better failure than
    /// a particle pool emptied by scenery — the pool is shared with every chip, every leaf and every
    /// puff of smoke in the game.
    /// </remarks>
    public const int Capacity = 64;

    private readonly float[] _flameScale;
    private readonly float[] _flameHeight;
    private readonly float[] _smokeScale;
    private readonly float[] _smokeHeight;
    private readonly bool[] _burns;

    private readonly Fire[] _found = new Fire[Capacity];
    private float _sweepIn;

    public Fires(BlockRegistry registry)
    {
        _flameScale = new float[registry.Count];
        _flameHeight = new float[registry.Count];
        _smokeScale = new float[registry.Count];
        _smokeHeight = new float[registry.Count];
        _burns = new bool[registry.Count];

        for (var id = 1; id < registry.Count; id++)
        {
            var type = registry[(ushort)id];

            _flameScale[id] = type.FlameScale;
            _flameHeight[id] = type.FlameHeight;
            _smokeScale[id] = type.SmokeScale;
            _smokeHeight[id] = type.SmokeHeight;
            _burns[id] = type.Smoulders;
        }
    }

    /// <summary>How many fires the last sweep found.</summary>
    public int Count { get; private set; }

    /// <summary>Fires found but not kept, because the list was full.</summary>
    public int Refused { get; private set; }

    public ReadOnlySpan<Fire> Found => _found.AsSpan(0, Count);

    /// <summary>True for any block that puts something in the air.</summary>
    public bool Burns(BlockId block) => _burns[block.Value];

    /// <summary>Sweeps again if it is time, and says whether it did.</summary>
    public bool Update(VoxelWorld world, Vector3 viewer, float dt)
    {
        _sweepIn -= dt;
        if (_sweepIn > 0f) return false;

        _sweepIn = SweepEvery;
        Sweep(world, viewer);
        return true;
    }

    /// <summary>Walks the chunks overlapping the box round the viewer and collects what burns.</summary>
    public void Sweep(VoxelWorld world, Vector3 viewer)
    {
        Count = 0;
        Refused = 0;

        var cx = (int)MathF.Floor(viewer.X);
        var cy = (int)MathF.Floor(viewer.Y);
        var cz = (int)MathF.Floor(viewer.Z);

        var min = ChunkPos.FromWorld(cx - Reach, cy - Reach, cz - Reach);
        var max = ChunkPos.FromWorld(cx + Reach, cy + Reach, cz + Reach);

        for (var chunkY = min.Y; chunkY <= max.Y; chunkY++)
        for (var chunkZ = min.Z; chunkZ <= max.Z; chunkZ++)
        for (var chunkX = min.X; chunkX <= max.X; chunkX++)
        {
            if (!world.TryGetChunk(new ChunkPos(chunkX, chunkY, chunkZ), out var chunk)) continue;
            if (chunk.IsEmpty) continue;

            var (ox, oy, oz) = chunk.Position.Origin;
            var raw = chunk.Raw;

            // Only the part of the chunk inside the box, so a chunk clipped by the edge of the
            // reach does not walk the whole 32³ to find nothing.
            var x0 = Math.Max(0, cx - Reach - ox);
            var x1 = Math.Min(Chunk.SizeMask, cx + Reach - ox);
            var y0 = Math.Max(0, cy - Reach - oy);
            var y1 = Math.Min(Chunk.SizeMask, cy + Reach - oy);
            var z0 = Math.Max(0, cz - Reach - oz);
            var z1 = Math.Min(Chunk.SizeMask, cz + Reach - oz);

            for (var y = y0; y <= y1; y++)
            for (var z = z0; z <= z1; z++)
            for (var x = x0; x <= x1; x++)
            {
                var id = raw[Chunk.Index(x, y, z)];
                if (!_burns[id]) continue;

                if (Count >= Capacity)
                {
                    Refused++;
                    continue;
                }

                var at = new Vector3(ox + x + 0.5f, oy + y, oz + z + 0.5f);

                _found[Count++] = new Fire(
                    at with { Y = at.Y + _flameHeight[id] }, _flameScale[id],
                    at with { Y = at.Y + _smokeHeight[id] }, _smokeScale[id]);
            }
        }
    }

    /// <summary>
    /// Puts this frame's fire and smoke into the pool.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>Rate times dt, carried between frames</b>, so the fire is the same fire at thirty
    /// frames a second and at two hundred. A count per frame is a fire whose size is the machine's.
    /// </para>
    /// <para>⚠ Smoke is emitted at a fraction of the flame's rate. A plume needs far fewer particles
    /// than a fire because each one lasts five times as long and grows the whole way — matched rates
    /// give a column of smoke thick enough to hide the fire that made it.</para>
    /// </remarks>
    public void Emit(ParticleSystem into, ushort flameLayer, ushort smokeLayer, float dt)
    {
        // Per fire, per second, at scale 1.
        const float FlamesASecond = 26f;
        const float SmokesASecond = 5f;

        _flameDebt += dt * FlamesASecond;
        _smokeDebt += dt * SmokesASecond;

        var flames = (int)_flameDebt;
        var smokes = (int)_smokeDebt;
        _flameDebt -= flames;
        _smokeDebt -= smokes;

        if (flames <= 0 && smokes <= 0) return;

        for (var i = 0; i < Count; i++)
        {
            var fire = _found[i];

            if (flames > 0 && fire.FlameScale > 0f)
                into.Flame(fire.Flame, fire.FlameScale, flameLayer, flames);

            if (smokes > 0 && fire.SmokeScale > 0f)
                into.Smoke(fire.Smoke, fire.SmokeScale, smokeLayer, smokes);
        }
    }

    private float _flameDebt;
    private float _smokeDebt;
}
