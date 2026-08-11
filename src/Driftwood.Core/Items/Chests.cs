namespace Driftwood.Core.Items;

/// <summary>
/// One chest: a row of slots and nothing else.
/// </summary>
/// <remarks>
/// The second block entity, and much the simpler of the two. A furnace needed three stacks and two
/// clocks because it does something over time; a chest does nothing at all, which is the point of
/// it — it is the first place in the game a player can put something down and expect to find it
/// again.
/// </remarks>
public sealed class Chest
{
    /// <summary>Three rows of nine, which is the shape the panel a pack ships is painted for.</summary>
    public const int Slots = 27;

    public readonly ItemStack[] Contents = new ItemStack[Slots];

    public bool IsEmpty
    {
        get
        {
            foreach (var stack in Contents) if (!stack.IsEmpty) return false;
            return true;
        }
    }

    /// <summary>How many slots have something in them, for a report.</summary>
    public int Used
    {
        get
        {
            var used = 0;
            foreach (var stack in Contents) if (!stack.IsEmpty) used++;
            return used;
        }
    }
}

/// <summary>
/// Every chest in the world, keyed on where it is.
/// </summary>
/// <remarks>
/// <para>The same shape as <see cref="FurnaceBank"/> and for the same reason: a cell holds an id
/// and nothing beside it, and a chunk is a flat array that streaming makes and destroys freely.
/// State that has to outlive both lives here.</para>
/// <para>⛔ <b>Nothing here is saved.</b> A chest is the first thing in the game whose whole purpose
/// is that what you put in it is still there later, and closing the window still takes the lot. It
/// makes the case for P5 rather than weakening it.</para>
/// </remarks>
public sealed class ChestBank
{
    private readonly Dictionary<(int X, int Y, int Z), Chest> _at = [];
    private readonly ItemRegistry _items;

    public ChestBank(ItemRegistry items) => _items = items;

    public int Count => _at.Count;

    /// <summary>Every chest and where it is, for a save to write down.</summary>
    public IEnumerable<((int X, int Y, int Z) At, Chest What)> All
    {
        get { foreach (var (cell, chest) in _at) yield return (cell, chest); }
    }

    /// <summary>The chest at a cell, made if this is the first time anybody opened it.</summary>
    public Chest Open(int x, int y, int z)
    {
        if (_at.TryGetValue((x, y, z), out var chest)) return chest;

        chest = new Chest();
        _at[(x, y, z)] = chest;
        return chest;
    }

    public bool TryGet(int x, int y, int z, out Chest chest) => _at.TryGetValue((x, y, z), out chest!);

    /// <summary>Takes a chest out of the world and hands back everything that was in it.</summary>
    public IEnumerable<ItemStack> Remove(int x, int y, int z)
    {
        if (!_at.Remove((x, y, z), out var chest)) yield break;

        foreach (var stack in chest.Contents)
            if (!stack.IsEmpty) yield return stack;
    }

    /// <summary>
    /// Hands back every stack but retains the empty record. Generated loot uses that record as its
    /// one-time receipt, so breaking and replacing an authored chest cannot roll the table again.
    /// </summary>
    public IEnumerable<ItemStack> Drain(int x, int y, int z)
    {
        if (!_at.TryGetValue((x, y, z), out var chest)) yield break;

        for (var slot = 0; slot < chest.Contents.Length; slot++)
        {
            var stack = chest.Contents[slot];
            chest.Contents[slot] = ItemStack.Empty;
            if (!stack.IsEmpty) yield return stack;
        }
    }

    /// <summary>Puts a stack into the first slot that will take it, and returns what would not fit.</summary>
    /// <remarks>
    /// Merges before it fills, the way the pockets do: shift-clicking forty planks into a chest that
    /// already holds twenty must top that stack up rather than starting a second one beside it.
    /// </remarks>
    public ItemStack Add(Chest chest, ItemStack stack)
    {
        if (stack.IsEmpty) return ItemStack.Empty;

        var cap = _items[stack.Item].MaxStack;

        for (var i = 0; i < Chest.Slots && !stack.IsEmpty; i++)
        {
            if (chest.Contents[i].IsEmpty || !chest.Contents[i].Matches(stack)) continue;
            chest.Contents[i] = chest.Contents[i].Merge(stack, cap, out stack);
        }

        for (var i = 0; i < Chest.Slots && !stack.IsEmpty; i++)
        {
            if (!chest.Contents[i].IsEmpty) continue;
            chest.Contents[i] = stack;
            stack = ItemStack.Empty;
        }

        return stack;
    }
}
