namespace Driftwood.Core.Items;

/// <summary>
/// The squares a player arranges things in, and what that arrangement currently makes.
/// </summary>
/// <remarks>
/// <para>In Core rather than in the screen that draws it, for the same reason placement rules are:
/// every combination has to be walkable without a window. <see cref="RecipeBook.TryMatch"/> has been
/// written, mirrored, trimmed and gated since the recipes landed and until now the only thing that
/// ever called it was the audit — this is the thing that calls it in the game.</para>
/// <para>Two by two in the hands and three by three at a bench are one type with a different size.
/// The matcher already trims a grid to its filled bounding box before comparing, so a two-by-two
/// recipe laid in the corner of a bench matches without the grid knowing anything about it.</para>
/// <para>The result is a value the grid keeps rather than a question asked at draw time. Matching
/// walks every recipe, and a screen that asked "what does this make" once per frame per slot would
/// pay for the whole book sixty times a second to be told nothing changed.</para>
/// </remarks>
public sealed class CraftingGrid
{
    private readonly ItemStack[] _cells;
    private readonly RecipeBook _book;
    private readonly ItemRegistry _items;

    public CraftingGrid(RecipeBook book, ItemRegistry items, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), $"a {width}x{height} grid is not a grid");

        _book = book;
        _items = items;
        Width = width;
        Height = height;
        _cells = new ItemStack[width * height];
    }

    public int Width { get; }

    public int Height { get; }

    public int Cells => _cells.Length;

    public ItemStack this[int cell] => (uint)cell < (uint)_cells.Length ? _cells[cell] : ItemStack.Empty;

    public ReadOnlySpan<ItemStack> All => _cells;

    /// <summary>What the arrangement makes right now. Empty when it makes nothing.</summary>
    public ItemStack Result { get; private set; }

    /// <summary>Which recipe that was, for a screen that wants to name it.</summary>
    public Recipe? Match { get; private set; }

    /// <summary>True when nothing at all is laid out.</summary>
    public bool IsEmpty
    {
        get
        {
            foreach (var cell in _cells) if (!cell.IsEmpty) return false;
            return true;
        }
    }

    /// <summary>Puts a carried stack into a cell, and hands back what was displaced or would not fit.</summary>
    public ItemStack Put(int cell, ItemStack carried)
    {
        if ((uint)cell >= (uint)_cells.Length || carried.IsEmpty) return carried;

        var there = _cells[cell];

        if (there.IsEmpty || there.Matches(carried))
        {
            _cells[cell] = there.Merge(carried, _items[carried.Item].MaxStack, out var over);
            Rematch();
            return over;
        }

        _cells[cell] = carried;
        Rematch();
        return there;
    }

    /// <summary>Drops one off the cursor into a cell, for laying a recipe out one square at a time.</summary>
    public ItemStack PutOne(int cell, ItemStack carried)
    {
        if ((uint)cell >= (uint)_cells.Length || carried.IsEmpty) return carried;

        var there = _cells[cell];
        if (!there.IsEmpty && !there.Matches(carried)) return carried;
        if (!there.IsEmpty && there.Count >= _items[carried.Item].MaxStack) return carried;

        _cells[cell] = there.IsEmpty ? carried with { Count = 1 } : there with { Count = there.Count + 1 };
        Rematch();
        return carried.MinusOne();
    }

    /// <summary>Lifts a whole cell onto the cursor.</summary>
    public ItemStack TakeAll(int cell)
    {
        if ((uint)cell >= (uint)_cells.Length || _cells[cell].IsEmpty) return ItemStack.Empty;

        var lifted = _cells[cell];
        _cells[cell] = ItemStack.Empty;
        Rematch();
        return lifted;
    }

    /// <summary>Lifts half a cell onto the cursor, rounded up.</summary>
    public ItemStack TakeHalf(int cell)
    {
        if ((uint)cell >= (uint)_cells.Length || _cells[cell].IsEmpty) return ItemStack.Empty;

        var there = _cells[cell];
        var half = (there.Count + 1) / 2;

        _cells[cell] = there.Minus(half);
        Rematch();
        return there with { Count = half };
    }

    /// <summary>
    /// Spends one of every filled square and hands back what that made.
    /// </summary>
    /// <remarks>
    /// One off each square, not a payment out of the pockets — the arrangement <em>is</em> the
    /// payment, and it is why a stack of sixty logs in one square makes sixty batches of planks
    /// rather than one. Empty when the grid makes nothing, so a caller can spin this until it stops.
    /// </remarks>
    public ItemStack TakeResult()
    {
        if (Result.IsEmpty) return ItemStack.Empty;

        var made = Result;
        for (var i = 0; i < _cells.Length; i++)
            if (!_cells[i].IsEmpty) _cells[i] = _cells[i].MinusOne();

        Rematch();
        return made;
    }

    /// <summary>
    /// Empties the grid back into an inventory, and hands back whatever would not fit.
    /// </summary>
    /// <remarks>
    /// What closing the screen does. A grid that quietly keeps its contents is a grid a player has
    /// lost three logs in, and the whole rule around this inventory is that nothing is ever
    /// swallowed — so what will not fit comes back to be dropped on the floor.
    /// </remarks>
    public List<ItemStack> Empty(Inventory into)
    {
        var spilled = new List<ItemStack>();

        for (var i = 0; i < _cells.Length; i++)
        {
            if (_cells[i].IsEmpty) continue;

            var left = into.Add(_cells[i]);
            _cells[i] = ItemStack.Empty;
            if (!left.IsEmpty) spilled.Add(left);
        }

        Rematch();
        return spilled;
    }

    /// <summary>Puts a stack in wherever it will go, the way clicking a recipe in the book does.</summary>
    /// <remarks>Used by the checks; the screen fills squares one at a time by hand.</remarks>
    public ItemStack AddAnywhere(ItemStack stack)
    {
        for (var i = 0; i < _cells.Length && !stack.IsEmpty; i++)
        {
            if (!_cells[i].IsEmpty) continue;
            stack = Put(i, stack);
        }

        return stack;
    }

    private void Rematch()
    {
        Match = _book.TryMatch(_cells, Width, Height, out var made) ? made : null;
        Result = Match?.Result ?? ItemStack.Empty;
    }
}
