using System.Numerics;
using Driftwood.Core.Lighting;

namespace Driftwood.Core.Blocks;

/// <summary>
/// The authoritative id-to-<see cref="BlockType"/> table. Built once during startup, then
/// read-only for the rest of the process so meshing and worldgen threads can share it.
/// </summary>
/// <remarks>
/// P0 registers a starter set in code (see <see cref="StarterBlocks"/>). At P6 this same
/// registry is populated from content files instead — the registry API is what the rest of
/// the engine binds to, so that swap does not ripple outward.
/// </remarks>
public sealed class BlockRegistry
{
    private readonly List<BlockType> _byId = [];
    private readonly Dictionary<string, BlockType> _byName = new(StringComparer.Ordinal);
    private bool _sealed;

    public int Count => _byId.Count;

    public BlockId Register(BlockType type)
    {
        if (_sealed)
            throw new InvalidOperationException("block registry is sealed; register during startup only");
        if (_byName.ContainsKey(type.Name))
            throw new InvalidOperationException($"duplicate block name '{type.Name}'");
        if (_byId.Count > ushort.MaxValue)
            throw new InvalidOperationException("block registry exhausted (65536 ids)");

        // Shorthand blocks are cubes. Building the model here rather than lazily means the mesher
        // never sees a null shape and never has to decide what a block looks like on a worker thread.
        type.Model ??= BlockModel.Cube(
            type.TopLayer, type.SideLayer, type.BottomLayer, type.Tint != TintSource.None);

        // A shape with gaps in it cannot hide what is behind it. Left to run, a non-cube opaque
        // block culls its neighbours' faces and opens a hole straight through the terrain, which
        // reads as a rendering bug a long way from the table entry that caused it.
        if (type.Opaque && !type.Model.IsFullCube)
            throw new InvalidOperationException($"block '{type.Name}' is opaque but its model is not a full cube");

        // A filled cell has no room left for water. A full-cube waterlogged form would also draw
        // its water shell inside its own faces and read the flow's questions ambiguously, so the
        // combination is refused here by name rather than surfacing as either of those.
        if (type.Waterlogged && (type.Opaque || type.Model.IsFullCube))
            throw new InvalidOperationException($"block '{type.Name}' is waterlogged but fills its cell");

        type.Id = new BlockId((ushort)_byId.Count);
        _byId.Add(type);
        _byName[type.Name] = type;
        return type.Id;
    }

    /// <summary>Closes the registry to further writes. Call before handing it to worker threads.</summary>
    public BlockRegistry Seal()
    {
        _sealed = true;
        return this;
    }

    public BlockType this[BlockId id] => _byId[id.Value];

    public BlockType this[ushort id] => _byId[id];

    public BlockType ByName(string name) => _byName.TryGetValue(name, out var t)
        ? t
        : throw new KeyNotFoundException($"no block named '{name}'");

    public bool TryByName(string name, out BlockType type) => _byName.TryGetValue(name, out type!);

    /// <summary>
    /// Flat lookup of <see cref="BlockType.Opaque"/> keyed by raw id. The mesher hits this once
    /// per face test per block; going through the object graph for each one shows up in a profile.
    /// </summary>
    public bool[] BuildOpacityTable()
    {
        var table = new bool[_byId.Count];
        for (var i = 0; i < _byId.Count; i++) table[i] = _byId[i].Opaque;
        return table;
    }

    /// <summary>
    /// Flat lookup of <see cref="BlockType.Solid"/> keyed by raw id, for collision. Separate from
    /// the opacity table on purpose: leaves stop a player and do not stop light, water stops
    /// neither, and a table that conflated them would put a wall around every pond.
    /// </summary>
    public bool[] BuildSolidTable()
    {
        var table = new bool[_byId.Count];
        for (var i = 0; i < _byId.Count; i++) table[i] = _byId[i].Solid;
        return table;
    }

    /// <summary>
    /// The boxes each block is actually made of, keyed by raw id, for collision that follows a
    /// shape rather than a cell.
    /// </summary>
    /// <param name="cellsBelow">
    /// ⛔ <b>How many extra rows of cells a scan has to look <em>down</em> through, and it is not
    /// optional.</b> Every box is inside its own cell but for one deliberate exception: a fence is
    /// drawn a block high and collided with a block and a half high, so a body standing in the cell
    /// above one would never see it if the scan only covered the cells its own box overlaps. This is
    /// how far past a cell the tallest box in the whole registry reaches, rounded up — 0 when nothing
    /// overhangs, and the scan costs nothing extra then.
    /// </param>
    /// <remarks>
    /// A block that is not <see cref="BlockType.Solid"/> gets no boxes at all rather than an empty
    /// shape, so the hot loop skips it on a length check and never asks a second question.
    /// </remarks>
    public (Vector3 Min, Vector3 Max)[][] BuildCollisionTable(out int cellsBelow)
    {
        var table = new (Vector3 Min, Vector3 Max)[_byId.Count][];
        var over = 0f;

        for (var i = 0; i < _byId.Count; i++)
        {
            var type = _byId[i];
            table[i] = type.Solid ? type.Model.Collision : [];

            foreach (var (_, max) in table[i]) over = MathF.Max(over, max.Y - 1f);
        }

        cellsBelow = over <= 0f ? 0 : (int)MathF.Ceiling(over);
        return table;
    }

    /// <summary>
    /// Light lost per step into each block id. Opaque blocks get <see cref="LightValue.Max"/>,
    /// which drives light to zero in one step without the propagator needing a separate branch
    /// for "blocked" and "dimmed".
    /// </summary>
    public byte[] BuildLightAttenuationTable()
    {
        var table = new byte[_byId.Count];
        for (var i = 0; i < _byId.Count; i++)
        {
            var type = _byId[i];
            table[i] = type.Opaque
                ? (byte)LightValue.Max
                : (byte)Math.Clamp(type.LightAttenuation, 0, LightValue.Max);
        }
        return table;
    }

    /// <summary>Packed emission keyed by raw id.</summary>
    public ushort[] BuildLightEmissionTable()
    {
        var table = new ushort[_byId.Count];
        for (var i = 0; i < _byId.Count; i++) table[i] = _byId[i].LightEmission;
        return table;
    }

    /// <summary>Shapes keyed by raw id, for the mesher's per-block path.</summary>
    public BlockModel[] BuildModelTable()
    {
        var table = new BlockModel[_byId.Count];
        for (var i = 0; i < _byId.Count; i++) table[i] = _byId[i].Model;
        return table;
    }

    /// <summary>
    /// Everything the greedy path needs about one block, flattened: whether it can merge at all,
    /// how many coplanar passes it draws, and the texture and tint of each face of each pass.
    /// </summary>
    /// <remarks>
    /// Indexed <c>id * MaxPasses * 6 + pass * 6 + face</c>. The mesher reads this once per cell per
    /// face per pass — several hundred million times over a loaded world — so it is worth the
    /// flattening. Going through the model object graph for each one is measurable.
    /// </remarks>
    public sealed record GreedyTables(bool[] FullCube, int[] PassCount, ushort[] Layer, bool[] Tinted)
    {
        public const int Stride = BlockModel.MaxPasses * Faces.Count;

        public ushort LayerFor(int id, int pass, int face) => Layer[id * Stride + pass * Faces.Count + face];

        public bool TintedFor(int id, int pass, int face) => Tinted[id * Stride + pass * Faces.Count + face];
    }

    public GreedyTables BuildGreedyTables()
    {
        var fullCube = new bool[_byId.Count];
        var passCount = new int[_byId.Count];
        var layer = new ushort[_byId.Count * GreedyTables.Stride];
        var tinted = new bool[_byId.Count * GreedyTables.Stride];
        Array.Fill(layer, BlockModel.NoLayer);

        for (var id = 0; id < _byId.Count; id++)
        {
            var model = _byId[id].Model;
            fullCube[id] = model.IsFullCube;
            passCount[id] = model.PassCount;
            if (!model.IsFullCube) continue;

            for (var pass = 0; pass < model.PassCount; pass++)
            for (var face = 0; face < Faces.Count; face++)
            {
                var i = id * GreedyTables.Stride + pass * Faces.Count + face;
                layer[i] = model.PassLayer(pass, face);
                tinted[i] = model.PassTinted(pass, face);
            }
        }

        return new GreedyTables(fullCube, passCount, layer, tinted);
    }

    public IReadOnlyList<BlockType> All => _byId;
}
