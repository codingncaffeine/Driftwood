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

    public IReadOnlyList<BlockType> All => _byId;
}
