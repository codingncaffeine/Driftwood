namespace Driftwood.Core.Items;

/// <summary>
/// One brewing stand: three bottles, the ingredient over them, the cinder dust that fires it, and
/// how far along the brew is.
/// </summary>
public sealed class BrewingStand
{
    public const int Bottles = 3;

    /// <summary>The reference's twenty seconds a brew.</summary>
    public const float BrewSeconds = 20f;

    /// <summary>Brews one pinch of cinder dust pays for — the reference's twenty to a blaze powder.</summary>
    public const int BrewsPerDust = 20;

    public readonly ItemStack[] Bottle = new ItemStack[Bottles];
    public ItemStack Ingredient;
    public ItemStack Fuel;

    /// <summary>Brews left in the dust already burned.</summary>
    public int FuelLeft;

    /// <summary>Seconds into the brew under way; zero when none is.</summary>
    public float Progress;

    public float Fraction => Math.Clamp(Progress / BrewSeconds, 0f, 1f);

    public bool IsEmpty =>
        Ingredient.IsEmpty && Fuel.IsEmpty && Bottle.All(one => one.IsEmpty);
}

/// <summary>
/// Every brewing stand in the world, by cell, and the brewing itself — the furnace bank's shape.
/// </summary>
public sealed class BrewingBank
{
    private readonly Dictionary<(int X, int Y, int Z), BrewingStand> _at = [];
    private ItemRegistry? _items;

    public void Bind(ItemRegistry items) => _items = items;

    public int Count => _at.Count;

    public IEnumerable<((int X, int Y, int Z) At, BrewingStand What)> All =>
        _at.Select(pair => (pair.Key, pair.Value));

    public BrewingStand Open(int x, int y, int z)
    {
        if (!_at.TryGetValue((x, y, z), out var stand)) _at[(x, y, z)] = stand = new BrewingStand();
        return stand;
    }

    public bool TryGet(int x, int y, int z, out BrewingStand stand) => _at.TryGetValue((x, y, z), out stand!);

    /// <summary>Takes a stand out of the world and hands back what was in it.</summary>
    public IEnumerable<ItemStack> Remove(int x, int y, int z)
    {
        if (!_at.Remove((x, y, z), out var stand)) yield break;
        foreach (var bottle in stand.Bottle) if (!bottle.IsEmpty) yield return bottle;
        if (!stand.Ingredient.IsEmpty) yield return stand.Ingredient;
        if (!stand.Fuel.IsEmpty) yield return stand.Fuel;
    }

    /// <summary>What a bottle in this slot becomes with this ingredient, or null.</summary>
    public string? Result(ItemStack bottle, ItemStack ingredient) =>
        _items is null || bottle.IsEmpty || ingredient.IsEmpty ? null
        : Potions.Brew(_items[bottle.Item].Name, _items[ingredient.Item].Name);

    /// <summary>True when the ingredient over the stand would change at least one bottle under it.</summary>
    public bool CanBrew(BrewingStand stand) =>
        stand.Bottle.Any(bottle => Result(bottle, stand.Ingredient) is not null);

    /// <summary>Whether an item may go in a stand's bottle slot, its ingredient slot or its fuel slot.</summary>
    public static bool FitsBottle(ItemType item) => item.Name == "glass_bottle" || Potions.TryGet(item.Name, out _);
    public static bool FitsIngredient(ItemType item) => Potions.Ingredients.Contains(item.Name);
    public static bool FitsFuel(ItemType item) => item.Name == "cinder_dust";

    /// <summary>
    /// Brews every stand on for <paramref name="dt"/>, and says how many brews finished — the host
    /// plays the sound and credits the achievement.
    /// </summary>
    public int Update(float dt, List<(int X, int Y, int Z)>? finished = null)
    {
        if (_items is null) return 0;
        var done = 0;

        foreach (var (at, stand) in _at)
        {
            if (!CanBrew(stand))
            {
                stand.Progress = 0f;
                continue;
            }

            // Dust is burned only when there is something to brew and nothing left in the last pinch.
            if (stand.FuelLeft <= 0)
            {
                if (stand.Fuel.IsEmpty) { stand.Progress = 0f; continue; }
                stand.Fuel = stand.Fuel.Count > 1 ? stand.Fuel with { Count = stand.Fuel.Count - 1 } : ItemStack.Empty;
                stand.FuelLeft = BrewingStand.BrewsPerDust;
            }

            stand.Progress += dt;
            if (stand.Progress < BrewingStand.BrewSeconds) continue;

            for (var i = 0; i < BrewingStand.Bottles; i++)
                if (Result(stand.Bottle[i], stand.Ingredient) is { } made)
                    stand.Bottle[i] = new ItemStack(_items.ByName(made).Id, 1);

            stand.Ingredient = stand.Ingredient.Count > 1
                ? stand.Ingredient with { Count = stand.Ingredient.Count - 1 }
                : ItemStack.Empty;
            stand.FuelLeft--;
            stand.Progress = 0f;
            finished?.Add(at);
            done++;
        }

        return done;
    }

    public void Clear() => _at.Clear();
}
