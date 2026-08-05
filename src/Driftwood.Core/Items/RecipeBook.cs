namespace Driftwood.Core.Items;

/// <summary>
/// Every recipe, and the two questions worth asking about them: does this grid make anything, and
/// what could I make with what I am carrying.
/// </summary>
/// <remarks>
/// <para>The two questions have different answers and both are needed. A grid match is what a bench
/// screen asks once per change; "what can I make" is what a recipe list asks over the whole book,
/// and it does not care about arrangement at all — a recipe's shape is a way of entering it, not a
/// second cost.</para>
/// <para>Paying for a recipe is an assignment problem the moment two ingredients could take the
/// same item. It is solved by backtracking rather than by taking the cheapest match first, because
/// a greedy pass spends the one plank a tag would have accepted on the slot that could have taken
/// a log. Nine slots and a handful of members each is small enough that exact is free.</para>
/// </remarks>
public sealed class RecipeBook
{
    private readonly List<Recipe> _recipes = [];
    private readonly List<SmeltRecipe> _smelting = [];
    private readonly ItemRegistry _items;

    public RecipeBook(ItemRegistry items) => _items = items;

    public IReadOnlyList<Recipe> Recipes => _recipes;

    public IReadOnlyList<SmeltRecipe> Smelting => _smelting;

    public RecipeBook Add(Recipe recipe)
    {
        _recipes.Add(recipe);
        return this;
    }

    public RecipeBook Add(SmeltRecipe recipe)
    {
        _smelting.Add(recipe);
        return this;
    }

    /// <summary>What a furnace would turn this into, if anything.</summary>
    public SmeltRecipe? SmeltFor(ItemId input)
    {
        foreach (var recipe in _smelting) if (recipe.Input.Matches(input)) return recipe;
        return null;
    }

    /// <summary>Seconds of burn this is worth. Zero when it is not fuel.</summary>
    public float BurnSeconds(ItemId fuel) => fuel.IsNone ? 0f : _items[fuel].BurnSeconds;

    /// <summary>
    /// What this arrangement makes, if anything.
    /// </summary>
    /// <param name="grid">Row-major, <paramref name="width"/> by <paramref name="height"/>.</param>
    /// <remarks>
    /// The grid is trimmed to what is actually in it before anything is compared, which is what lets
    /// a two-by-two recipe be laid anywhere in a three-by-three. Without it a player has to guess
    /// which corner the game wanted, and every wrong guess looks like the recipe not existing.
    /// </remarks>
    public bool TryMatch(
        ReadOnlySpan<ItemStack> grid, int width, int height, CraftStation station, out Recipe? made)
    {
        made = null;

        // A station that offers a list has no single answer to "what does this make" — one rock at
        // a stonecutter is three different things — so it is asked with Offers instead.
        if (CraftStations.Chooses(station)) return false;

        int minX = width, minY = height, maxX = -1, maxY = -1, filled = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (grid[y * width + x].IsEmpty) continue;
            filled++;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        if (filled == 0) return false;

        var w = maxX - minX + 1;
        var h = maxY - minY + 1;

        foreach (var recipe in _recipes)
        {
            // ⚠ Where it is worked comes first, before any shape is compared. A stonecutter recipe
            // is one slot wide, so a single stone laid in a bench would match one without this —
            // and the gate would be no gate at all for exactly the recipes it was built to hold.
            if (!recipe.WorkedAt(station, Math.Max(width, height))) continue;

            if (recipe.Shapeless)
            {
                if (recipe.SlotsUsed != filled) continue;
                if (!Assignable(recipe, grid, width, height)) continue;
            }
            else
            {
                if (recipe.Width != w || recipe.Height != h) continue;
                if (!Aligns(recipe, grid, width, minX, minY, mirror: false)
                    && !(recipe.Mirrored && Aligns(recipe, grid, width, minX, minY, mirror: true)))
                    continue;
            }

            made = recipe;
            return true;
        }

        return false;
    }

    /// <summary>True when what is being carried covers this recipe's cost.</summary>
    public bool CanPay(Inventory carrying, Recipe recipe) => Plan(carrying, recipe) is not null;

    /// <summary>
    /// Pays for a recipe out of an inventory and hands back what it made.
    /// </summary>
    /// <remarks>
    /// Nothing is spent until the whole cost is known to be payable, so a craft that cannot be
    /// afforded takes nothing rather than half-eating the ingredients and reporting failure.
    /// </remarks>
    public bool Craft(Inventory carrying, Recipe recipe, out ItemStack made)
    {
        made = ItemStack.Empty;

        if (Plan(carrying, recipe) is not { } plan) return false;

        foreach (var (item, count) in plan)
        {
            var taken = carrying.Take(item, count);
            if (taken == count) continue;

            // Cannot happen — the plan was checked against these same counts a line ago — but a
            // silent half-payment is the one failure here nobody would ever notice, so it says so.
            throw new InvalidOperationException(
                $"crafting '{recipe.Name}' took {taken} of {count} {_items[item].Name}");
        }

        made = recipe.Result;
        return true;
    }

    /// <summary>Everything that could be made right now, in the order the book lists them.</summary>
    public IEnumerable<Recipe> CraftableFrom(Inventory carrying, CraftStation station, int grid)
    {
        foreach (var recipe in _recipes)
        {
            if (!recipe.WorkedAt(station, grid)) continue;
            if (CanPay(carrying, recipe)) yield return recipe;
        }
    }

    /// <summary>Every recipe this station works, whatever is being carried.</summary>
    public IEnumerable<Recipe> WorkedAt(CraftStation station, int grid)
    {
        foreach (var recipe in _recipes)
            if (recipe.WorkedAt(station, grid)) yield return recipe;
    }

    /// <summary>
    /// Everything a choosing station would make out of one thing, in the order the book lists them.
    /// </summary>
    /// <remarks>
    /// The other half of <see cref="TryMatch"/>, for the stations that answer with several things
    /// rather than one. Every recipe here is a single slot by construction — a station you feed one
    /// thing to has nothing to arrange — and the check insists on that rather than assuming it.
    /// </remarks>
    public IEnumerable<Recipe> Offers(CraftStation station, ItemId input)
    {
        if (input.IsNone || CraftStations.IsGrid(station)) yield break;

        foreach (var recipe in _recipes)
        {
            if (recipe.Station != station) continue;
            if (recipe.SlotsUsed != 1) continue;

            foreach (var slot in recipe.Ingredients)
                if (slot.Matches(input)) { yield return recipe; break; }
        }
    }

    /// <summary>
    /// Which concrete items pay for which slots, or null when the cost cannot be met.
    /// </summary>
    /// <remarks>
    /// Ingredients are tried most-constrained first, which is not a heuristic standing in for
    /// correctness — the search backtracks either way — but it is what keeps the tree shallow when
    /// several slots take a tag with the same members.
    /// </remarks>
    private List<(ItemId Item, int Count)>? Plan(Inventory carrying, Recipe recipe)
    {
        var slots = new List<Ingredient>(recipe.SlotsUsed);
        foreach (var cell in recipe.Cells) if (cell is not null) slots.Add(cell);
        if (slots.Count == 0) return null;

        slots.Sort((a, b) => a.Members.Length.CompareTo(b.Members.Length));

        var spent = new Dictionary<ushort, int>();
        return Assign(carrying, slots, 0, spent)
            ? [.. spent.Select(pair => (new ItemId(pair.Key), pair.Value))]
            : null;
    }

    private bool Assign(
        Inventory carrying, List<Ingredient> slots, int at, Dictionary<ushort, int> spent)
    {
        if (at >= slots.Count) return true;

        foreach (var candidate in slots[at].Members)
        {
            var already = spent.GetValueOrDefault(candidate.Value);
            if (carrying.CountOf(candidate) <= already) continue;

            spent[candidate.Value] = already + 1;
            if (Assign(carrying, slots, at + 1, spent)) return true;

            if (already == 0) spent.Remove(candidate.Value);
            else spent[candidate.Value] = already;
        }

        return false;
    }

    /// <summary>Whether a shaped recipe sits over the filled part of the grid, cell for cell.</summary>
    private static bool Aligns(
        Recipe recipe, ReadOnlySpan<ItemStack> grid, int gridWidth, int atX, int atY, bool mirror)
    {
        for (var y = 0; y < recipe.Height; y++)
        for (var x = 0; x < recipe.Width; x++)
        {
            var want = recipe.At(mirror ? recipe.Width - 1 - x : x, y);
            var have = grid[(atY + y) * gridWidth + atX + x];

            if (want is null)
            {
                if (!have.IsEmpty) return false;
                continue;
            }

            if (have.IsEmpty || !want.Matches(have.Item)) return false;
        }

        return true;
    }

    /// <summary>Whether every slot of a shapeless recipe can be paired with a distinct stack.</summary>
    private static bool Assignable(Recipe recipe, ReadOnlySpan<ItemStack> grid, int width, int height)
    {
        var loose = new List<ItemId>();
        for (var i = 0; i < width * height; i++)
            if (!grid[i].IsEmpty) loose.Add(grid[i].Item);

        var slots = new List<Ingredient>();
        foreach (var cell in recipe.Cells) if (cell is not null) slots.Add(cell);
        if (slots.Count != loose.Count) return false;

        slots.Sort((a, b) => a.Members.Length.CompareTo(b.Members.Length));

        var used = new bool[loose.Count];
        return Pair(slots, loose, used, 0);
    }

    private static bool Pair(List<Ingredient> slots, List<ItemId> loose, bool[] used, int at)
    {
        if (at >= slots.Count) return true;

        for (var i = 0; i < loose.Count; i++)
        {
            if (used[i] || !slots[at].Matches(loose[i])) continue;

            used[i] = true;
            if (Pair(slots, loose, used, at + 1)) return true;
            used[i] = false;
        }

        return false;
    }
}
