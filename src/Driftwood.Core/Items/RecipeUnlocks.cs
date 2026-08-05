namespace Driftwood.Core.Items;

/// <summary>
/// Notices when something becomes makeable that was not makeable before.
/// </summary>
/// <remarks>
/// <para>A diff rather than an event, and that is the whole design. Nothing in the game has to know
/// that coal is what a torch is missing: picking up the first coal changes what the pockets can
/// pay for, and this notices. The day a recipe is added or an ingredient is renamed, it keeps
/// working, because it never knew anything in the first place.</para>
/// <para>⚠ <b>Once per world — not once a session, and not once per installation.</b> These are
/// achievements, and the whole value of one is that it fires the first time you do the thing. A
/// player told about planks again on every launch learns to stop reading the corner, and then the
/// one that matters goes past unread. So what has been said outlives the process, and it does it
/// <em>in the save</em>: a brand new world months later is somebody who has forgotten what makes a
/// torch and should be told again, while the same world reloaded should not be. This lived in a
/// file beside the settings for as long as there was one world and no saves, which made "ever" and
/// "this world" the same sentence; saves are what stopped them being the same sentence.</para>
/// <para>A recipe added to the game since a world was last opened announces itself on the next
/// load, which is the behaviour anybody would want and comes free from the same diff.</para>
/// <para>It is deliberately blind to where the player is standing. Learning that a stone pickaxe
/// has become possible is worth knowing at the moment the rubble is picked up, not later when
/// somebody happens to walk past a bench.</para>
/// </remarks>
public sealed class RecipeUnlocks
{
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private int _lastVersion = -1;

    /// <summary>How many recipes have been announced in this world, for the check to read back.</summary>
    public int Announced => _announced.Count;

    /// <summary>Which ones, so a save carries them with the world they belong to.</summary>
    public IReadOnlyCollection<string> Names => _announced;

    /// <summary>Puts back what a save remembered, without announcing any of it.</summary>
    public void Reload(IEnumerable<string> names)
    {
        _announced.Clear();
        foreach (var name in names) _announced.Add(name);

        _lastVersion = -1;
        Dirty = false;
    }

    /// <summary>
    /// True when something has been announced that the world on disk does not know about yet.
    /// </summary>
    /// <remarks>
    /// The one signal a periodic save has that the world itself does not: picking something up can
    /// announce a recipe without changing a single block, so <see cref="World.VoxelWorld.Changed"/>
    /// would say there was nothing to write.
    /// </remarks>
    public bool Dirty { get; private set; }

    /// <summary>Forgets that anything is unwritten. What a save does once it is safely down.</summary>
    public void Settled() => Dirty = false;

    /// <summary>Forgets everything, so the notices start again. What the settings screen offers.</summary>
    public void Forget()
    {
        if (_announced.Count == 0) return;
        _announced.Clear();
        _lastVersion = -1;
        Dirty = true;
    }

    /// <summary>
    /// Fills <paramref name="into"/> with whatever has just become makeable. Usually nothing.
    /// </summary>
    /// <returns>False when the pockets have not changed since last time, so nothing was looked at.</returns>
    public bool Poll(RecipeBook book, Inventory carrying, List<Recipe> into)
    {
        into.Clear();

        if (_lastVersion == carrying.Version) return false;
        _lastVersion = carrying.Version;

        foreach (var recipe in book.Recipes)
        {
            if (_announced.Contains(recipe.Name)) continue;
            if (!book.CanPay(carrying, recipe)) continue;

            _announced.Add(recipe.Name);
            into.Add(recipe);
            Dirty = true;
        }

        return true;
    }

    /// <summary>
    /// Marks everything currently makeable as already known, without announcing any of it.
    /// </summary>
    /// <remarks>
    /// For a world that starts with something in its pockets. Without it, a player who loads into a
    /// full inventory is told about forty recipes at once, which is every one of them and therefore
    /// none of them.
    /// </remarks>
    public void Prime(RecipeBook book, Inventory carrying)
    {
        _lastVersion = carrying.Version;

        foreach (var recipe in book.Recipes)
            if (book.CanPay(carrying, recipe) && _announced.Add(recipe.Name)) Dirty = true;
    }
}
