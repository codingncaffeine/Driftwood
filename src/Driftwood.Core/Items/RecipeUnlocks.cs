namespace Driftwood.Core.Items;

/// <summary>
/// Notices when something becomes makeable that was not makeable before.
/// </summary>
/// <remarks>
/// <para>A diff rather than an event, and that is the whole design. Nothing in the game has to know
/// that coal is what a torch is missing: picking up the first coal changes what the pockets can
/// pay for, and this notices. The day a recipe is added or an ingredient is renamed, it keeps
/// working, because it never knew anything in the first place.</para>
/// <para><b>Once ever, not once a session.</b> These are achievements, and the whole value of one
/// is that it fires the first time you do the thing. A player who is told about planks again every
/// time they launch the game learns to stop reading the corner, and then the one that matters goes
/// past unread — so what has already been said outlives the process, in a file beside the settings.
/// </para>
/// <para>It is deliberately blind to where the player is standing. Learning that a stone pickaxe
/// has become possible is worth knowing at the moment the rubble is picked up, not later when
/// somebody happens to walk past a bench.</para>
/// </remarks>
public sealed class RecipeUnlocks
{
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private int _lastVersion = -1;

    /// <summary>Where the record of what has already been said lives.</summary>
    public static string Path =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Driftwood",
            "unlocked.txt");

    /// <summary>How many recipes have ever been announced, for the check to read back.</summary>
    public int Announced => _announced.Count;

    /// <summary>Which ones, so a save can carry them with the world they belong to.</summary>
    /// <remarks>
    /// ⚠ <b>Per world, not per installation.</b> This began as a file beside the settings, on the
    /// reading that an achievement fires once ever — but that was decided when there was one world
    /// and no saves, so "ever" meant "this world" without anybody having to say it. A brand new
    /// world months later is a new player who has forgotten what makes a torch, and it should tell
    /// them again; the same world reloaded should not. So the set travels in the save.
    /// </remarks>
    public IReadOnlyCollection<string> Names => _announced;

    /// <summary>Puts back what a save remembered, without announcing any of it.</summary>
    public void Reload(IEnumerable<string> names)
    {
        _announced.Clear();
        foreach (var name in names) _announced.Add(name);

        _lastVersion = -1;
        Dirty = false;
    }

    /// <summary>True when something has been added since it was last written out.</summary>
    public bool Dirty { get; private set; }

    /// <summary>
    /// Reads back what earlier sessions already said.
    /// </summary>
    /// <remarks>
    /// A missing or unreadable file means a new player, which is the right answer to every way this
    /// can fail: the cost of being wrong is a few notices somebody has seen before, and the cost of
    /// refusing to start over a text file is the whole game.
    /// </remarks>
    public void Restore(string? path = null)
    {
        try
        {
            path ??= Path;
            if (!File.Exists(path)) return;

            foreach (var line in File.ReadAllLines(path))
            {
                var name = line.Trim();
                if (name.Length > 0 && name[0] != '#') _announced.Add(name);
            }
        }
        catch (Exception)
        {
            // A new player, then.
        }

        Dirty = false;
    }

    /// <summary>Writes what has been said, so the next session does not say it again.</summary>
    public bool Persist(string? path = null)
    {
        if (!Dirty) return true;

        try
        {
            path ??= Path;
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var lines = new List<string> { "# recipes Driftwood has already told you about" };
            lines.AddRange(_announced.Order(StringComparer.Ordinal));

            var temporary = path + ".new";
            File.WriteAllLines(temporary, lines);
            File.Move(temporary, path, overwrite: true);

            Dirty = false;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

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
