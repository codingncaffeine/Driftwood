namespace Driftwood.Core.Items;

/// <summary>The shelves a player can narrow the recipe book to.</summary>
public enum RecipeCategory
{
    All,
    Building,
    Tools,
    Materials,
    Light,
    Machines,
}

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
    public SmeltRecipe? SmeltFor(ItemId input) => SmeltFor(input, FurnaceKind.Furnace);

    /// <summary>
    /// What a smelter of this kind would turn it into, or null when that one will not take it.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The kind is asked here rather than at every call site.</b> A blast furnace refusing a
    /// log is the same question as a furnace having nothing to do with an empty slot, and both have
    /// to give the same answer to the tick, to the screen's "will this slot take this" and to the
    /// check — three places that would otherwise each decide it.
    /// </remarks>
    public SmeltRecipe? SmeltFor(ItemId input, FurnaceKind kind)
    {
        foreach (var recipe in _smelting)
            if (recipe.Input.Matches(input))
                return FurnaceKinds.Takes(kind, recipe.Work) ? recipe : null;

        return null;
    }

    /// <summary>
    /// Everything a smelter of this kind will work, in the order it was written down.
    /// </summary>
    /// <remarks>
    /// <para>⛔⛔ <b>Reported by the user: <i>"I'm not seeing any recipes for food when i look in the
    /// furnace."</i> There was no list. At all.</b> A furnace opened three slots and nothing else, so
    /// the only way to learn that a fire cooks meat was to already know. Every one of the smelts
    /// worked perfectly and not one was ever named anywhere a player could look — which is exactly
    /// the fault this project already fixed for the bench, whose own note says it outright: <i>a thing
    /// that is absent and a thing that does not exist look identical.</i></para>
    /// <para>⛳ <b>Filtered by the kind, so each fire's book is its own.</b> A smoker lists what a
    /// smoker cooks and a campfire lists what a campfire cooks, which is also the clearest statement
    /// the game makes anywhere about what the specialised fires are for.</para>
    /// </remarks>
    public IEnumerable<SmeltRecipe> SmeltsAt(FurnaceKind kind)
    {
        foreach (var recipe in _smelting)
            if (FurnaceKinds.Takes(kind, recipe.Work))
                yield return recipe;
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

    /// <summary>Which shelf a recipe belongs on, derived from the thing it makes.</summary>
    public RecipeCategory CategoryOf(Recipe recipe)
    {
        var type = _items[recipe.Result.Item];
        var name = type.Name;

        if (type.IsTool || type.Wears is not null || type.ShieldShare > 0f)
            return RecipeCategory.Tools;

        if (type.Glow.LengthSquared() > 0f) return RecipeCategory.Light;

        // These are useful blocks rather than building fabric. Named here because ItemType's
        // placement rule deliberately says how something lands, not what right-clicking it does.
        if (name.Contains("bench", StringComparison.Ordinal)
            || name.Contains("furnace", StringComparison.Ordinal)
            || name.Contains("smoker", StringComparison.Ordinal)
            || name.Contains("chest", StringComparison.Ordinal)
            || name.Contains("barrel", StringComparison.Ordinal)
            || name.Contains("anvil", StringComparison.Ordinal)
            || name.Contains("loom", StringComparison.Ordinal)
            || name.Contains("stonecutter", StringComparison.Ordinal)
            || name.Contains("composter", StringComparison.Ordinal)
            || name.Contains("campfire", StringComparison.Ordinal))
            return RecipeCategory.Machines;

        return type.Places is not null || type.PlacesEntity
            ? RecipeCategory.Building
            : RecipeCategory.Materials;
    }

    /// <summary>True when a recipe matches both the active shelf and the words in the search box.</summary>
    public bool Matches(Recipe recipe, RecipeCategory category, string search)
    {
        if (category != RecipeCategory.All && CategoryOf(recipe) != category) return false;
        if (string.IsNullOrWhiteSpace(search)) return true;

        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var word in words)
        {
            var found = recipe.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
                || _items[recipe.Result.Item].Label.Contains(word, StringComparison.OrdinalIgnoreCase)
                || recipe.MadeAt.Contains(word, StringComparison.OrdinalIgnoreCase);

            if (!found)
                foreach (var ingredient in recipe.Ingredients)
                    if (ingredient.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }

            if (!found) return false;
        }

        return true;
    }

    /// <summary>
    /// The immediate cost and the raw materials underneath its craftable components.
    /// </summary>
    /// <remarks>
    /// The second line is deliberately recursive: a pickaxe does not merely cost sticks and
    /// planks, it ultimately costs logs. Cycles stop at the first repeated item and alternative
    /// tag members use the first member written by the recipe set, making the answer stable.
    /// </remarks>
    public string[] CostLines(Recipe recipe, Inventory? carrying = null)
    {
        var immediate = CountIngredients(recipe, 1);
        var leaves = new Dictionary<ushort, int>();
        var visiting = new HashSet<ushort>();

        foreach (var (ingredient, count) in immediate)
            Expand(ingredient, count, leaves, visiting, 0);

        var first = "needs " + Join(immediate.Select(pair =>
        {
            var have = carrying is null ? -1 : pair.Ingredient.Members.Sum(carrying.CountOf);
            return (pair.Ingredient.Name, pair.Count, have);
        }));
        var raw = leaves
            .OrderBy(pair => _items[pair.Key].Label, StringComparer.OrdinalIgnoreCase)
            .Select(pair => (
                _items[pair.Key].Label, pair.Value,
                carrying is null ? -1 : carrying.CountOf(new ItemId(pair.Key))));
        var second = "from " + Join(raw);

        return first == second.Replace("from ", "needs ", StringComparison.Ordinal)
            ? [first]
            : [first, second];
    }

    private List<(Ingredient Ingredient, int Count)> CountIngredients(Recipe recipe, int crafts)
    {
        var counted = new Dictionary<string, (Ingredient Ingredient, int Count)>(StringComparer.Ordinal);
        foreach (var ingredient in recipe.Ingredients)
        {
            if (counted.TryGetValue(ingredient.Name, out var was))
                counted[ingredient.Name] = (was.Ingredient, was.Count + crafts);
            else
                counted.Add(ingredient.Name, (ingredient, crafts));
        }

        return [.. counted.Values];
    }

    private void Expand(
        Ingredient ingredient, int count, Dictionary<ushort, int> leaves,
        HashSet<ushort> visiting, int depth)
    {
        var item = ingredient.Members[0];
        if (depth >= 8 || !visiting.Add(item.Value))
        {
            leaves[item.Value] = leaves.GetValueOrDefault(item.Value) + count;
            return;
        }

        Recipe? madeBy = null;
        foreach (var candidate in _recipes)
            if (candidate.Result.Item == item)
            {
                madeBy = candidate;
                break;
            }

        if (madeBy is null)
        {
            leaves[item.Value] = leaves.GetValueOrDefault(item.Value) + count;
        }
        else
        {
            var crafts = (count + madeBy.Result.Count - 1) / madeBy.Result.Count;
            foreach (var (part, amount) in CountIngredients(madeBy, crafts))
                Expand(part, amount, leaves, visiting, depth + 1);
        }

        visiting.Remove(item.Value);
    }

    private static string Join(IEnumerable<(string Name, int Count, int Have)> costs) =>
        string.Join(", ", costs.Select(cost =>
            cost.Have < 0 ? $"{cost.Count} {cost.Name}" : $"{cost.Count} {cost.Name} (have {cost.Have})"));

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
