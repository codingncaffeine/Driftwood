namespace Driftwood.Core.Items;

/// <summary>
/// One furnace: what is in it, what is burning, and how far through the burn it is.
/// </summary>
/// <remarks>
/// <para>The first block in the game that is more than an id in a cell. A cell holds sixteen bits
/// and this needs three stacks and two clocks, so the state lives beside the world keyed on where
/// it is — which is the block-entity idea the plan puts at P6, arriving with the first block that
/// actually needs it.</para>
/// <para>Fuel is spent only when there is something to smelt. A furnace that burns its coal down
/// while empty is the one behaviour here everybody notices and nobody forgives.</para>
/// </remarks>
public sealed class Furnace
{
    public ItemStack Input;
    public ItemStack Fuel;
    public ItemStack Output;

    /// <summary>Seconds of burn left in what is currently alight.</summary>
    public float BurnLeft;

    /// <summary>What that was worth when it was lit, so the flame can be drawn as a gauge.</summary>
    public float BurnTotal;

    /// <summary>Seconds into the thing currently being smelted.</summary>
    public float Progress;

    /// <summary>How long the current smelt takes end to end. Zero when nothing is smelting.</summary>
    public float Takes;

    public bool Lit => BurnLeft > 0f;

    /// <summary>0 to 1 through the current smelt.</summary>
    public float Fraction => Takes > 0f ? Math.Clamp(Progress / Takes, 0f, 1f) : 0f;

    /// <summary>0 to 1 of the current fuel left, for the flame.</summary>
    public float FuelLeft => BurnTotal > 0f ? Math.Clamp(BurnLeft / BurnTotal, 0f, 1f) : 0f;

    public bool IsEmpty => Input.IsEmpty && Fuel.IsEmpty && Output.IsEmpty;
}

/// <summary>
/// Every furnace in the world, and the tick that advances them.
/// </summary>
/// <remarks>
/// <para>Keyed on position rather than held by the chunk, because a chunk is a flat array of ids
/// that streaming creates and destroys freely and this has to outlive that. It also means the whole
/// thing is headless and can be run at any rate the checks like.</para>
/// <para>Nothing here is saved. There is no save format yet, so a furnace's contents live for one
/// session — which is fine while nothing else persists either, and is the first thing P5 has to
/// pick up.</para>
/// </remarks>
public sealed class FurnaceBank
{
    private readonly Dictionary<(int X, int Y, int Z), Furnace> _at = [];
    private readonly ItemRegistry _items;
    private readonly RecipeBook _book;

    public FurnaceBank(ItemRegistry items, RecipeBook book)
    {
        _items = items;
        _book = book;
    }

    public int Count => _at.Count;

    /// <summary>The furnace at a cell, made if this is the first time anybody opened it.</summary>
    public Furnace Open(int x, int y, int z)
    {
        if (_at.TryGetValue((x, y, z), out var furnace)) return furnace;

        furnace = new Furnace();
        _at[(x, y, z)] = furnace;
        return furnace;
    }

    public bool TryGet(int x, int y, int z, out Furnace furnace) => _at.TryGetValue((x, y, z), out furnace!);

    /// <summary>Takes a furnace out of the world and hands back what was inside it.</summary>
    public IEnumerable<ItemStack> Remove(int x, int y, int z)
    {
        if (!_at.Remove((x, y, z), out var furnace)) yield break;

        if (!furnace.Input.IsEmpty) yield return furnace.Input;
        if (!furnace.Fuel.IsEmpty) yield return furnace.Fuel;
        if (!furnace.Output.IsEmpty) yield return furnace.Output;
    }

    /// <summary>
    /// Advances every furnace, and reports the ones whose flame went in or out.
    /// </summary>
    /// <param name="relit">
    /// Filled with the cells whose lit state changed, so the caller can swap the block that is drawn.
    /// </param>
    public void Update(float dt, List<(int X, int Y, int Z, bool Lit)> relit)
    {
        relit.Clear();

        foreach (var (cell, furnace) in _at)
        {
            var wasLit = furnace.Lit;
            Step(furnace, dt);
            if (furnace.Lit != wasLit) relit.Add((cell.X, cell.Y, cell.Z, furnace.Lit));
        }
    }

    private void Step(Furnace furnace, float dt)
    {
        var recipe = Smeltable(furnace);
        furnace.Takes = recipe?.Seconds ?? 0f;

        // Light a new piece of fuel only if there is work for it. This is the whole reason the
        // recipe is looked up before the fuel is touched rather than after.
        if (furnace.BurnLeft <= 0f && recipe is not null && !furnace.Fuel.IsEmpty)
        {
            var worth = _items[furnace.Fuel.Item].BurnSeconds;
            if (worth > 0f)
            {
                furnace.Fuel = furnace.Fuel.MinusOne();
                furnace.BurnLeft = worth;
                furnace.BurnTotal = worth;
            }
        }

        // Anything already alight burns down whether or not there is still something in the top,
        // which is what makes taking the ore out mid-smelt cost you the coal.
        if (furnace.BurnLeft > 0f) furnace.BurnLeft = MathF.Max(0f, furnace.BurnLeft - dt);

        if (recipe is null || !furnace.Lit)
        {
            // Half-cooked work slides back rather than being thrown away, so a furnace that runs out
            // of fuel for a moment does not start from nothing.
            furnace.Progress = MathF.Max(0f, furnace.Progress - dt);
            return;
        }

        furnace.Progress += dt;
        if (furnace.Progress < recipe.Seconds) return;

        furnace.Progress = 0f;
        furnace.Input = furnace.Input.MinusOne();
        furnace.Output = furnace.Output.Merge(
            recipe.Result, _items[recipe.Result.Item].MaxStack, out var spilled);

        // Cannot happen: Smeltable refuses when the result will not fit. Said out loud because a
        // furnace that silently eats what it made is invisible until somebody counts.
        if (!spilled.IsEmpty)
            throw new InvalidOperationException($"a furnace lost {spilled.Count} {_items[spilled.Item].Name}");
    }

    /// <summary>What this furnace would make next, or null when it has nothing to do.</summary>
    private SmeltRecipe? Smeltable(Furnace furnace)
    {
        if (furnace.Input.IsEmpty) return null;
        if (_book.SmeltFor(furnace.Input.Item) is not { } recipe) return null;

        if (furnace.Output.IsEmpty) return recipe;
        if (furnace.Output.Item.Value != recipe.Result.Item.Value) return null;

        var cap = _items[recipe.Result.Item].MaxStack;
        return furnace.Output.Count + recipe.Result.Count <= cap ? recipe : null;
    }
}
