using Driftwood.Core.Blocks;

namespace Driftwood.Core.Items;

/// <summary>
/// What each block leaves on the floor when it comes apart, and whether the tool was good enough
/// to leave anything at all.
/// </summary>
/// <remarks>
/// <para>One table rather than a field on each block, and that is a trade made knowingly. Locality
/// was worth having while a drop was "itself, or one other block" — but a drop is now an item that
/// need not exist as a block (an ore leaves a lump), a count that need not be one (clay leaves
/// four), and an answer that depends on what was in hand. None of those can be written in a block
/// table that is built before items exist, and splitting the rule across two files is how the two
/// halves stop agreeing.</para>
/// <para>The default is the item that puts the block back down, so the table below only says the
/// interesting cases. A block nothing places and nothing lists leaves nothing — which is correct
/// for bedrock and for water, and is caught by the audit for anything else.</para>
/// </remarks>
public sealed class BlockDrops
{
    /// <summary>One row: what a block leaves. A null item means it leaves nothing.</summary>
    public readonly record struct Rule(string Block, string? Item, int Count = 1);

    private readonly ItemId[] _item;
    private readonly int[] _count;
    private readonly ItemRegistry _items;

    public BlockDrops(BlockRegistry blocks, ItemRegistry items, params Rule[] rules)
    {
        _items = items;
        _item = new ItemId[blocks.Count];
        _count = new int[blocks.Count];

        // Anything that can be put back down leaves the thing that puts it down. That covers most
        // of the world and every orientation of every shape at once.
        for (ushort id = 1; id < blocks.Count; id++)
        {
            _item[id] = items.ForBlock(new BlockId(id));
            _count[id] = 1;
        }

        foreach (var rule in rules)
        {
            var block = blocks.ByName(rule.Block).Id;
            _item[block.Value] = rule.Item is null ? ItemId.None : items.ByName(rule.Item).Id;
            _count[block.Value] = rule.Item is null ? 0 : rule.Count;
        }
    }

    /// <summary>What this block leaves, ignoring what is in hand. Empty when it leaves nothing.</summary>
    public ItemStack Of(BlockId block) =>
        block.Value >= _item.Length || _item[block.Value].IsNone
            ? ItemStack.Empty
            : new ItemStack(_item[block.Value], _count[block.Value]);

    /// <summary>
    /// What this block leaves for a player holding that. Empty when the tool was not good enough.
    /// </summary>
    public ItemStack Harvest(BlockType block, ItemType? held) =>
        MiningRules.CanHarvest(block, held) ? Of(block.Id) : ItemStack.Empty;

    /// <summary>Every block that leaves this item, for the reachability walk.</summary>
    public IEnumerable<BlockId> Sources(ItemId item)
    {
        for (ushort id = 1; id < _item.Length; id++)
            if (_item[id].Value == item.Value) yield return new BlockId(id);
    }

    /// <summary>How many blocks leave nothing at all, for the audit to weigh.</summary>
    public int BlocksLeavingNothing
    {
        get
        {
            var none = 0;
            for (ushort id = 1; id < _item.Length; id++) if (_item[id].IsNone) none++;
            return none;
        }
    }

    /// <summary>The name of what a block leaves, for a report.</summary>
    public string Describe(BlockId block)
    {
        var stack = Of(block);
        return stack.IsEmpty ? "nothing" : $"{stack.Count}x {_items[stack.Item].Name}";
    }
}
