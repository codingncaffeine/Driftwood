using Driftwood.Core.World;

namespace Driftwood.Core.Blocks;

/// <summary>
/// One set of blocks that join up with their neighbours: sixteen variants keyed on which sides
/// are connected.
/// </summary>
public sealed class ConnectionFamily
{
    /// <summary>Sides a thing can join, so sixteen combinations of them.</summary>
    public const int Masks = 16;

    public required string Name { get; init; }

    /// <summary>Indexed by mask, one bit per entry of <see cref="Placeable.Facings"/>.</summary>
    public required BlockId[] ByMask { get; init; }

    /// <summary>The form with nothing joined to it — what an item puts down before the pass runs.</summary>
    public BlockId Bare => ByMask[0];
}

/// <summary>
/// Works out what a connecting block should look like given what is around it.
/// </summary>
/// <remarks>
/// <para>The machinery three separate features are queued behind: fences and walls want it now,
/// signals and rails will want the same pass, and so will fluid flow and falling sand. It is worth
/// building once rather than badly three times.</para>
/// <para><b>One ring is enough, and that is a property rather than an optimisation.</b> A variant
/// swap only ever changes the <em>shape</em> of a block, never whether its neighbours would connect
/// to it — every member of a family connects the same way — so re-evaluating the six cells around
/// an edit can never make a seventh want to change. Anything that breaks that (a family whose
/// variants differ in solidity, say) turns this into a cascade needing a work queue and a visited
/// set, and the audit checks the property directly so it fails rather than quietly under-updating.
/// </para>
/// </remarks>
public sealed class ConnectionTable
{
    private readonly ConnectionFamily[] _families;

    /// <summary>Which family a block belongs to, or -1. Indexed by raw block id.</summary>
    private readonly int[] _family;

    /// <summary>Which mask a block currently wears. Indexed by raw block id.</summary>
    private readonly int[] _mask;

    /// <summary>Blocks a connecting thing joins on to even though they are not in its family.</summary>
    /// <remarks>
    /// A full solid cube — a wall of stone, a plank, a pane of glass. Not opaque: glass is what a
    /// pane most wants to meet, and testing opacity would leave every window with a gap in it.
    /// </remarks>
    private readonly bool[] _anchor;

    /// <summary>True where a member is the waterlogged twin of its family's dry form.</summary>
    private readonly bool[] _waterlogged;

    private readonly Waterlogging _wet;

    public ConnectionTable(BlockRegistry registry, params ConnectionFamily[] families)
    {
        _families = families;
        _family = new int[registry.Count];
        _mask = new int[registry.Count];
        _anchor = new bool[registry.Count];
        _waterlogged = new bool[registry.Count];
        _wet = new Waterlogging(registry);

        Array.Fill(_family, -1);

        for (var id = 1; id < registry.Count; id++)
        {
            var type = registry[(ushort)id];
            _anchor[id] = type.Solid && type.Model.IsFullCube;
        }

        for (var f = 0; f < families.Length; f++)
        for (var mask = 0; mask < ConnectionFamily.Masks; mask++)
        {
            var id = families[f].ByMask[mask].Value;
            if (_family[id] >= 0)
                throw new InvalidOperationException(
                    $"block {id} is in both '{_families[_family[id]].Name}' and '{families[f].Name}'");

            _family[id] = f;
            _mask[id] = mask;

            // ⛳ The wet twin sits in the SAME family at the same mask, so a fence post in the sea
            // and one on the beach join across the waterline as one fence — membership is about
            // what a thing is, and wet is about what its cell also holds. The rewire keeps
            // wetness through TryRewire, so the one-ring property still stands: a swap changes
            // shape, never whether a neighbour would join.
            if (!_wet.TryWet(families[f].ByMask[mask], out var twin)) continue;

            _family[twin.Value] = f;
            _mask[twin.Value] = mask;
            _waterlogged[twin.Value] = true;
        }
    }

    public int FamilyCount => _families.Length;

    public bool Connects(BlockId id) => id.Value < _family.Length && _family[id.Value] >= 0;

    /// <summary>
    /// Works out what the block at a cell should be, given what is around it now.
    /// </summary>
    /// <returns>False when it is not a connecting block, or is already the right one.</returns>
    public bool TryRewire(VoxelWorld world, int x, int y, int z, out BlockId become)
    {
        become = BlockId.Air;

        var here = world.GetBlock(x, y, z);
        if (here.Value >= _family.Length) return false;

        var family = _family[here.Value];
        if (family < 0) return false;

        var wanted = 0;
        for (var i = 0; i < Placeable.Facings.Length; i++)
        {
            var (dx, dy, dz) = Faces.Normals[Placeable.Facings[i]];
            var neighbour = world.GetBlock(x + dx, y + dy, z + dz).Value;

            if (neighbour >= _family.Length) continue;
            if (_family[neighbour] != family && !_anchor[neighbour]) continue;

            wanted |= 1 << i;
        }

        if (wanted == _mask[here.Value]) return false;

        become = _families[family].ByMask[wanted];

        // A wet fence re-picks its shape between wet forms; the water in the cell is not the
        // rewire's to spill.
        if (_waterlogged[here.Value] && _wet.TryWet(become, out var wetForm)) become = wetForm;

        return true;
    }

    /// <summary>The mask a block is wearing, for the checks that read it back.</summary>
    public int MaskOf(BlockId id) => id.Value < _mask.Length ? _mask[id.Value] : 0;
}
