namespace Driftwood.Core.Blocks;

/// <summary>
/// Pairs every block that can stand in the sea with the form of it that does, and answers what a
/// cell keeps when the block is taken out of it.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Built from the registry by the one naming rule, so anything can construct it without
/// being handed it.</b> A wet form is its dry name plus <see cref="Suffix"/>; the mesher, the drops
/// table, the item map and the client each build their own copy off the sealed registry, exactly as
/// <see cref="FluidTable"/> is built, and there is no second table anywhere to fall out of step.
/// </para>
/// <para>⛳ <b>Seagrass is the one wet form with no dry one</b> — its dry form is air, which is the
/// genre's own answer read off the mechanics: scooping the water out from under seagrass removes
/// the plant, and there is nothing it could stand there as. Anything else always-wet lands the same
/// way by declaring <see cref="BlockType.Waterlogged"/> with no dry partner registered.</para>
/// <para>⛔ <b><see cref="Remains"/> is the one answer for "what does an emptied cell hold".</b>
/// Mining, the support shed and the crawler's blast each empty cells, and each writing bare air
/// would quietly delete the sea from around whatever was standing in it. One rule, asked by all
/// three, is what keeps a broken wet fence a hole in the water rather than a hole in the world.
/// </para>
/// </remarks>
public sealed class Waterlogging
{
    /// <summary>What a wet form's name adds to its dry one's.</summary>
    public const string Suffix = "_waterlogged";

    /// <summary>dry id → wet id, or 0 where there is no wet form. Indexed by raw block id.</summary>
    private readonly ushort[] _wetOf;

    /// <summary>wet id → dry id (air for the always-wet), identity elsewhere. Indexed by raw id.</summary>
    private readonly ushort[] _dryOf;

    private readonly bool[] _wet;

    private readonly ushort _water;

    /// <summary>How many dry/wet pairs exist, for the check that says this table is not empty.</summary>
    public int Pairs { get; }

    public Waterlogging(BlockRegistry registry)
    {
        _wetOf = new ushort[registry.Count];
        _dryOf = new ushort[registry.Count];
        _wet = new bool[registry.Count];
        _water = registry.ByName("water").Id.Value;

        for (var id = 0; id < registry.Count; id++)
        {
            var type = registry[(ushort)id];
            _dryOf[id] = (ushort)id;
            if (!type.Waterlogged) continue;

            _wet[id] = true;

            if (type.Name.EndsWith(Suffix, StringComparison.Ordinal)
                && registry.TryByName(type.Name[..^Suffix.Length], out var dry))
            {
                _wetOf[dry.Id.Value] = (ushort)id;
                _dryOf[id] = dry.Id.Value;
                Pairs++;
            }
            else
            {
                // Always wet, never dry: taking the water takes the thing.
                _dryOf[id] = BlockId.Air.Value;
            }
        }
    }

    /// <summary>True for a block sharing its cell with water.</summary>
    public bool IsWet(BlockId id) => id.Value < _wet.Length && _wet[id.Value];

    /// <summary>The waterlogged form of this block, if it has one.</summary>
    public bool TryWet(BlockId dry, out BlockId wet)
    {
        var value = dry.Value < _wetOf.Length ? _wetOf[dry.Value] : (ushort)0;
        wet = new BlockId(value);
        return value != 0;
    }

    /// <summary>
    /// The dry form of a wet block — air for the always-wet — and any other block itself.
    /// </summary>
    /// <remarks>
    /// The identity default is what lets a caller normalise unconditionally: a cull test, a label,
    /// a drop lookup or a toggle-sound test can ask for the dry name of whatever it holds without
    /// first asking whether the question applies.
    /// </remarks>
    public BlockId DryOf(BlockId id) => id.Value < _dryOf.Length ? new BlockId(_dryOf[id.Value]) : id;

    /// <summary>
    /// What a cell keeps when the block in it is taken: the water a wet block stood in, or nothing.
    /// </summary>
    public BlockId Remains(BlockId taken) =>
        taken.Value < _wet.Length && _wet[taken.Value] ? new BlockId(_water) : BlockId.Air;
}
