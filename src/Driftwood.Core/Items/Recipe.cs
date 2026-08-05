namespace Driftwood.Core.Items;

/// <summary>
/// One slot's worth of a recipe: any of a named set of things will do.
/// </summary>
/// <remarks>
/// <para>Tags and single items are the same thing here, and deliberately. A tag is an ingredient
/// with several members and a plain item is an ingredient with one, so matching never asks which
/// kind it is holding — and the day a second wood species exists, <c>#planks</c> gains a member and
/// every recipe written against it covers the new wood without being touched.</para>
/// <para>Named, because the name is what a player is shown and what a fault message says. "any
/// plank" reads; a list of ids does not.</para>
/// </remarks>
public sealed class Ingredient
{
    public required string Name { get; init; }

    /// <summary>Everything that satisfies this slot.</summary>
    public required ItemId[] Members { get; init; }

    public bool Matches(ItemId item)
    {
        foreach (var member in Members) if (member.Value == item.Value) return true;
        return false;
    }

    public override string ToString() => Name;
}

/// <summary>
/// One thing that can be made from others, and the arrangement it wants them in.
/// </summary>
/// <remarks>
/// <para>Shaped and shapeless share one type rather than being two. Both are a small grid of
/// ingredient slots and a result; the only difference is whether the arrangement is read, and that
/// is one flag rather than two matchers and two tables to keep in step.</para>
/// <para>A recipe that will not fit in a two-by-two says so by being bigger than one, rather than
/// by carrying a "needs a bench" flag somebody has to remember to set. The rule is the shape.</para>
/// </remarks>
public sealed class Recipe
{
    public required string Name { get; init; }

    public required ItemStack Result { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Row-major, <see cref="Width"/> by <see cref="Height"/>. Null means the slot is empty.</summary>
    public required Ingredient?[] Cells { get; init; }

    /// <summary>True when the arrangement does not matter, only what is in the grid.</summary>
    public bool Shapeless { get; init; }

    /// <summary>
    /// True when the mirror image also makes this. On by default, which is what a player expects.
    /// </summary>
    /// <remarks>
    /// Nobody laying out a recipe thinks about handedness, and a tool that only works built
    /// left-handed reads as the recipe being wrong rather than as a rule. It is still a flag,
    /// because the day something genuinely has a handedness there is somewhere to say so.
    /// </remarks>
    public bool Mirrored { get; init; } = true;

    /// <summary>True when this will not fit in the two-by-two a player carries in their hands.</summary>
    public bool NeedsBench => Width > 2 || Height > 2;

    /// <summary>What it costs, as a flat list with repeats.</summary>
    public IEnumerable<Ingredient> Ingredients
    {
        get { foreach (var cell in Cells) if (cell is not null) yield return cell; }
    }

    /// <summary>How many slots this actually uses.</summary>
    public int SlotsUsed
    {
        get
        {
            var used = 0;
            foreach (var cell in Cells) if (cell is not null) used++;
            return used;
        }
    }

    public Ingredient? At(int x, int y) => Cells[y * Width + x];

    /// <summary>
    /// A canonical description of what this matches, for finding two recipes that are one recipe.
    /// </summary>
    /// <remarks>
    /// Two recipes with the same signature can never both be reached: whichever the matcher happens
    /// to walk into first wins, and the other is a row in a table that does nothing. Shapeless
    /// signatures sort their ingredients, because the order they were written in is not part of what
    /// they match.
    /// </remarks>
    public string Signature()
    {
        if (Shapeless)
        {
            var names = new List<string>();
            foreach (var cell in Cells) if (cell is not null) names.Add(cell.Name);
            names.Sort(StringComparer.Ordinal);
            return "shapeless:" + string.Join(",", names);
        }

        var forward = Layout(mirror: false);
        if (!Mirrored) return "shaped:" + forward;

        // The mirror pair is one signature, so a recipe and its reflection do not read as two
        // different things that happen to match the same grid.
        var back = Layout(mirror: true);
        return "shaped:" + (StringComparer.Ordinal.Compare(forward, back) <= 0 ? forward : back);
    }

    private string Layout(bool mirror)
    {
        var rows = new List<string>(Height);
        for (var y = 0; y < Height; y++)
        {
            var cells = new List<string>(Width);
            for (var x = 0; x < Width; x++)
                cells.Add(At(mirror ? Width - 1 - x : x, y)?.Name ?? "-");
            rows.Add(string.Join("|", cells));
        }

        return string.Join("/", rows);
    }
}

/// <summary>One thing a furnace turns into another, and how long it takes.</summary>
/// <remarks>
/// Separate from <see cref="Recipe"/> because it is a different question with a different answer
/// shape: one slot in, one out, and a second cost in fuel that no bench recipe has. Folding the two
/// together would put a "is this smelting" branch through every line of the matcher.
/// </remarks>
public sealed class SmeltRecipe
{
    public required string Name { get; init; }

    public required Ingredient Input { get; init; }

    public required ItemStack Result { get; init; }

    /// <summary>Seconds of burn one of these takes. The genre's own ten.</summary>
    public float Seconds { get; init; } = 10f;
}
