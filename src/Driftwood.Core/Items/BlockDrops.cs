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
/// <para>⛳ <b>Three shapes of row, 2026-08-22.</b> The certain one (<see cref="Rule"/>: stone leaves
/// rubble); the <em>maybe</em> (<see cref="Chance"/>: a leaf leaves a sapling one time in twenty,
/// and a stick or an apple on their own separate rolls — each row is its own coin, so one leaf can
/// pay two things); and the <em>instead</em> (<see cref="ToolRule"/>: shears take the leaf block
/// itself, and nothing else is rolled). The chance rows are what the user's leaf report asked for,
/// and they took the ripe wheat's second item out of the client, where it had been living as the
/// one special case — and had quietly started paying wheat seed for every ripe carrot.</para>
/// </remarks>
public sealed class BlockDrops
{
    /// <summary>One row: what a block leaves. A null item means it leaves nothing.</summary>
    public readonly record struct Rule(string Block, string? Item, int Count = 1);

    /// <summary>
    /// One maybe: what a block leaves <paramref name="Probability"/> of the time, on top of its
    /// certain drop, <paramref name="Min"/> to <paramref name="Max"/> of it when it does.
    /// </summary>
    public readonly record struct Chance(string Block, string Item, float Probability, int Min = 1, int Max = 1);

    /// <summary>
    /// One instead: what a block leaves when taken with that class of tool — the whole answer, with
    /// no certain drop and no chance rows beside it.
    /// </summary>
    public readonly record struct ToolRule(string Block, ToolClass Tool, string Item, int Count = 1);

    /// <summary>
    /// How much each tier of Fortune raises every chance row, as a share of the base chance.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>The user's rule, 2026-08-22:</b> "each fortune is considered a tier or level, so as a whole
    /// increase the rates 5–10% per tier." The top of their range, applied uniformly — a 5% sapling
    /// is 5.5 / 6.0 / 6.5% at tiers one to three. Deliberately modest: Fortune's large effect in the
    /// genre is on the COUNT of an ore or a crop, which is a separate rule when enchanting exists.
    /// Nothing passes a tier yet; the parameter is plumbed so P17 is a caller and not a rewrite.
    /// </remarks>
    public const float FortuneStepPerTier = 0.10f;

    private readonly ItemId[] _item;
    private readonly int[] _count;
    private readonly (ItemId Item, float Probability, int Min, int Max)[][] _chances;
    private readonly (ToolClass Tool, ItemId Item, int Count)[][] _instead;
    private readonly ItemRegistry _items;

    public BlockDrops(BlockRegistry blocks, ItemRegistry items, params Rule[] rules)
        : this(blocks, items, rules, [], [])
    {
    }

    public BlockDrops(
        BlockRegistry blocks, ItemRegistry items, Rule[] rules, Chance[] chances, ToolRule[] instead)
    {
        _items = items;
        _item = new ItemId[blocks.Count];
        _count = new int[blocks.Count];
        _chances = new (ItemId, float, int, int)[blocks.Count][];
        _instead = new (ToolClass, ItemId, int)[blocks.Count][];

        // Anything that can be put back down leaves the thing that puts it down. That covers most
        // of the world and every orientation of every shape at once.
        for (ushort id = 1; id < blocks.Count; id++)
        {
            // ⛳ A fluid leaves nothing, and it is said once here rather than as seventeen rows in
            // the table below. Every level of every fluid is its own registered block, so writing
            // them out would be a list somebody has to extend by hand the day a third fluid lands —
            // and a forgotten row would not fail loudly, it would hand out an item that never existed.
            //
            // ⛔ A WATERLOGGED BLOCK DECLARES A FLUID AND IS NOT ONE. Without its exception, every
            // wet fence and slab in the game would silently drop nothing — the exact quiet failure
            // this guard warns about, arriving through the guard itself. A wet form leaves its dry
            // item, which ItemRegistry.Seal already answers through ForBlock.
            if (blocks[id].Fluid != Blocks.FluidKind.None && !blocks[id].Waterlogged)
            {
                _item[id] = ItemId.None;
                _count[id] = 0;
                continue;
            }

            _item[id] = items.ForBlock(new BlockId(id));
            _count[id] = 1;
        }

        foreach (var rule in rules)
        {
            var block = blocks.ByName(rule.Block).Id;
            _item[block.Value] = rule.Item is null ? ItemId.None : items.ByName(rule.Item).Id;
            _count[block.Value] = rule.Item is null ? 0 : rule.Count;
        }

        foreach (var chance in chances)
        {
            if (chance.Probability is <= 0f or > 1f)
                throw new ArgumentOutOfRangeException(
                    nameof(chances), $"'{chance.Block}' leaves '{chance.Item}' at {chance.Probability}, which is not a chance");
            if (chance.Min < 1 || chance.Max < chance.Min)
                throw new ArgumentOutOfRangeException(
                    nameof(chances), $"'{chance.Block}' leaves {chance.Min}-{chance.Max} '{chance.Item}', which is not a count");

            var block = blocks.ByName(chance.Block).Id.Value;
            var row = (items.ByName(chance.Item).Id, chance.Probability, chance.Min, chance.Max);
            _chances[block] = _chances[block] is { } had ? [.. had, row] : [row];
        }

        foreach (var rule in instead)
        {
            if (rule.Tool == ToolClass.None)
                throw new ArgumentException($"'{rule.Block}' has an instead-row for no tool at all", nameof(instead));

            var block = blocks.ByName(rule.Block).Id.Value;
            var row = (rule.Tool, items.ByName(rule.Item).Id, rule.Count);
            _instead[block] = _instead[block] is { } had ? [.. had, row] : [row];
        }
    }

    /// <summary>What this block leaves for certain, ignoring what is in hand. Empty when it leaves nothing.</summary>
    public ItemStack Of(BlockId block) =>
        block.Value >= _item.Length || _item[block.Value].IsNone
            ? ItemStack.Empty
            : new ItemStack(_item[block.Value], _count[block.Value]);

    /// <summary>
    /// What this block leaves for certain for a player holding that. Empty when the tool was not
    /// good enough; the tool's own instead-row when it has one.
    /// </summary>
    public ItemStack Harvest(BlockType block, ItemType? held)
    {
        if (Instead(block, held) is { } instead) return instead;
        return MiningRules.CanHarvest(block, held) ? Of(block.Id) : ItemStack.Empty;
    }

    /// <summary>
    /// Everything this block leaves for a player holding that: the certain drop and every chance
    /// row that came up, or the tool's instead-row alone. Appends to <paramref name="into"/>.
    /// </summary>
    /// <param name="fortune">The Fortune tier in hand, zero for none. Raises every chance row by
    /// <see cref="FortuneStepPerTier"/> per tier.</param>
    /// <remarks>
    /// ⛔ Nothing is rolled below the tier line. A leaf under a bare hand is above it (leaves are
    /// soft), but an ore that refuses a wooden pick refuses its maybes too — a chance row is a bonus
    /// on a harvest, never a way round the tool rule.
    /// </remarks>
    public void Harvest(BlockType block, ItemType? held, Random random, List<ItemStack> into, int fortune = 0)
    {
        if (Instead(block, held) is { } instead)
        {
            into.Add(instead);
            return;
        }

        if (!MiningRules.CanHarvest(block, held)) return;

        var certain = Of(block.Id);
        if (!certain.IsEmpty) into.Add(certain);

        if (block.Id.Value >= _chances.Length || _chances[block.Id.Value] is not { } rows) return;

        var boost = 1f + FortuneStepPerTier * Math.Max(0, fortune);
        foreach (var (item, probability, min, max) in rows)
        {
            if (random.NextDouble() >= Math.Min(1f, probability * boost)) continue;
            into.Add(new ItemStack(item, min + random.Next(max - min + 1)));
        }
    }

    /// <summary>The chance rows of a block, for a census or a report.</summary>
    public IEnumerable<(ItemId Item, float Probability, int Min, int Max)> ChancesOf(BlockId block) =>
        block.Value < _chances.Length && _chances[block.Value] is { } rows ? rows : [];

    /// <summary>What a tool class takes from this block instead of the ordinary drop, or empty.</summary>
    public ItemStack InsteadWith(BlockId block, ToolClass tool)
    {
        if (block.Value >= _instead.Length || _instead[block.Value] is not { } rows) return ItemStack.Empty;
        foreach (var (rowTool, item, count) in rows)
            if (rowTool == tool) return new ItemStack(item, count);
        return ItemStack.Empty;
    }

    private ItemStack? Instead(BlockType block, ItemType? held)
    {
        if (held is null || held.Tool == ToolClass.None) return null;
        var stack = InsteadWith(block.Id, held.Tool);
        return stack.IsEmpty ? null : stack;
    }

    /// <summary>Every block that leaves this item, for certain or by chance, for the reachability walk.</summary>
    public IEnumerable<BlockId> Sources(ItemId item)
    {
        for (ushort id = 1; id < _item.Length; id++)
        {
            if (_item[id].Value == item.Value)
            {
                yield return new BlockId(id);
                continue;
            }

            var listed = false;
            if (_chances[id] is { } rows)
                foreach (var row in rows)
                    if (row.Item.Value == item.Value) { listed = true; break; }

            if (!listed && _instead[id] is { } instead)
                foreach (var row in instead)
                    if (row.Item.Value == item.Value) { listed = true; break; }

            if (listed) yield return new BlockId(id);
        }
    }

    /// <summary>How many blocks leave nothing at all, for the audit to weigh.</summary>
    public int BlocksLeavingNothing
    {
        get
        {
            var none = 0;
            for (ushort id = 1; id < _item.Length; id++)
                if (_item[id].IsNone && _chances[id] is null && _instead[id] is null) none++;
            return none;
        }
    }

    /// <summary>The name of what a block leaves, for a report.</summary>
    public string Describe(BlockId block)
    {
        var stack = Of(block);
        var certain = stack.IsEmpty ? "nothing" : $"{stack.Count}x {_items[stack.Item].Name}";

        if (block.Value >= _chances.Length || _chances[block.Value] is not { } rows) return certain;

        var maybes = string.Join(", ", rows.Select(r =>
            $"{r.Probability * 100f:0.##}% {(r.Min == r.Max ? $"{r.Min}" : $"{r.Min}-{r.Max}")}x {_items[r.Item].Name}"));
        return $"{certain} ({maybes})";
    }
}
