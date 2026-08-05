using Driftwood.Core.Blocks;

namespace Driftwood.Core.Items;

/// <summary>
/// The authoritative id-to-<see cref="ItemType"/> table, and the block-to-item map that goes with it.
/// </summary>
/// <remarks>
/// <para>Built once at startup after the blocks, because an item that places a block needs the
/// block's id and a block never needs an item's. The one-way dependency is why the two registries
/// are separate objects rather than one table with two kinds of row in it.</para>
/// <para>The reverse map is not simply an inverse. Twenty stair orientations all point back at one
/// stair item, so <see cref="ForBlock"/> is many-to-one — which is the whole reason a stair picked
/// up off the floor is the same thing however it was facing when it was broken.</para>
/// </remarks>
public sealed class ItemRegistry
{
    private readonly List<ItemType> _byId = [];
    private readonly Dictionary<string, ItemType> _byName = new(StringComparer.Ordinal);
    private ItemId[] _forBlock = [];
    private bool _sealed;

    public ItemRegistry()
    {
        // Id 0 is nothing, so a zeroed slot reads as empty without a null check. It places nothing
        // and is never handed out, but it has to occupy the slot or every id would be off by one.
        Register(new ItemType { Name = "nothing", Label = "nothing", IconLayer = 0, MaxStack = 0 });
    }

    public int Count => _byId.Count;

    public IReadOnlyList<ItemType> All => _byId;

    public ItemId Register(ItemType type)
    {
        if (_sealed)
            throw new InvalidOperationException("item registry is sealed; register during startup only");
        if (_byName.ContainsKey(type.Name))
            throw new InvalidOperationException($"duplicate item name '{type.Name}'");
        if (_byId.Count > ushort.MaxValue)
            throw new InvalidOperationException("item registry exhausted (65536 ids)");

        // A thing that carries wear cannot also stack: two half-worn pickaxes in one slot have one
        // count and two amounts of damage, and there is nowhere to keep the second.
        if (type.Durability > 0 && type.MaxStack != 1)
            throw new InvalidOperationException($"item '{type.Name}' wears out but stacks to {type.MaxStack}");

        type.Id = new ItemId((ushort)_byId.Count);
        _byId.Add(type);
        _byName[type.Name] = type;
        return type.Id;
    }

    /// <summary>
    /// Closes the registry and builds the block-to-item map. Call before handing it to anything.
    /// </summary>
    public ItemRegistry Seal(BlockRegistry blocks)
    {
        _forBlock = new ItemId[blocks.Count];

        foreach (var type in _byId)
        {
            if (type.Places is not { } places) continue;

            // What this puts down, so a slot can draw it as the block it is rather than as one of
            // its faces. The first variant is the one an item is thought of as.
            if (places.Variants.Length > 0) type.IconModel = blocks[places.Variants[0]].Model;

            foreach (var variant in places.Variants)
            {
                if (!_forBlock[variant.Value].IsNone)
                    throw new InvalidOperationException(
                        $"block '{blocks[variant].Name}' is placed by both "
                        + $"'{_byId[_forBlock[variant.Value].Value].Name}' and '{type.Name}'");

                _forBlock[variant.Value] = type.Id;
            }
        }

        _sealed = true;
        return this;
    }

    public ItemType this[ItemId id] => _byId[id.Value];

    public ItemType this[ushort id] => _byId[id];

    public ItemType ByName(string name) => _byName.TryGetValue(name, out var t)
        ? t
        : throw new KeyNotFoundException($"no item named '{name}'");

    public bool TryByName(string name, out ItemType type) => _byName.TryGetValue(name, out type!);

    /// <summary>The item that puts this block down, or none when nothing does.</summary>
    public ItemId ForBlock(BlockId block) =>
        block.Value < _forBlock.Length ? _forBlock[block.Value] : ItemId.None;

    /// <summary>A stack of one thing by name, for the recipe tables and the checks.</summary>
    public ItemStack Stack(string name, int count = 1) => new(ByName(name).Id, count);
}
