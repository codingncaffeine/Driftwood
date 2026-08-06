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
/// Where a thing is worked.
/// </summary>
/// <remarks>
/// <para>⛔ <b>The gate the recipe set was missing.</b> Until this existed the only thing standing
/// between a player and a recipe was its <em>shape</em> — anything fitting a two-by-two could be
/// made in bare hands — which put fourteen recipes, seven of them worked stone, in the pockets of
/// somebody who had not built anything at all. A bench was a grid size rather than a place.</para>
/// <para>Shape is still a constraint and still checked: a recipe worked in the hands that does not
/// fit in them is a contradiction the audit refuses. This says <em>where</em>, the grid says
/// <em>whether it fits</em>, and the two are different questions.</para>
/// <para>Smelting is not here. It has its own type, because one-in-one-out over time with a fuel
/// cost is a different shape of thing — the furnaces name their own station on
/// <see cref="SmeltRecipe"/>.</para>
/// </remarks>
public enum CraftStation
{
    /// <summary>Anywhere it fits: the two-by-two in a player's hands, or any grid at least as big.</summary>
    Hand,

    /// <summary>A bench. Three by three, and the only place most things are made.</summary>
    Bench,

    /// <summary>A stonecutter: one block of rock in, one worked form out, chosen from a list.</summary>
    Stonecutter,

    /// <summary>A smithing table: a tool and a material in, the same tool a tier up.</summary>
    Smithing,

    /// <summary>A loom: cloth and a dye.</summary>
    Loom,
}

/// <summary>What kind of station each one is.</summary>
public static class CraftStations
{
    /// <summary>
    /// True when a station is worked by <em>arranging</em> things, false when it offers a list.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The distinction the stonecutter forces, and it goes deeper than the screen.</b> A grid
    /// station has one answer for any arrangement, so <see cref="RecipeBook.TryMatch"/> can return
    /// it and two recipes matching the same grid are a fault. A choosing station has <em>many</em>
    /// answers for one input — that is the entire point of it, one rock offering its slab, its stair
    /// and its worked form — so a matcher would have to pick arbitrarily between them and the
    /// duplicate-signature check would call the whole station a mistake.
    /// </remarks>
    public static bool IsGrid(CraftStation station) =>
        station is CraftStation.Hand or CraftStation.Bench;

    /// <summary>True when this station is worked by picking one of the things it offers.</summary>
    public static bool Chooses(CraftStation station) => !IsGrid(station);
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

    /// <summary>Where this is worked. <see cref="CraftStation.Hand"/> means wherever it fits.</summary>
    public CraftStation Station { get; init; } = CraftStation.Hand;

    /// <summary>True when this does not fit in the two-by-two a player carries in their hands.</summary>
    public bool TooBigForHands => Width > 2 || Height > 2;

    /// <summary>
    /// True when a bench, or something at least as big, is needed.
    /// </summary>
    /// <remarks>
    /// Two reasons, and they are genuinely different: it does not fit in two hands, or it is worked
    /// at a bench whatever its size. A recipe can be small and still want somewhere to be made.
    /// </remarks>
    public bool NeedsBench => TooBigForHands || Station == CraftStation.Bench;

    /// <summary>True when this can be laid out in a square grid of this size at this station.</summary>
    /// <remarks>
    /// ⚠ Both halves matter. A recipe worked at one named station is made <em>only</em> there — but
    /// a hand recipe is not made everywhere either: a stonecutter has one slot and no arrangement,
    /// so laying a two-by-two out at one is not a thing that can happen. Without the second test a
    /// single stone in a stonecutter would match every one-slot recipe in the book.
    /// </remarks>
    public bool WorkedAt(CraftStation station, int grid)
    {
        if (Station != CraftStation.Hand) return Station == station && grid >= Width && grid >= Height;
        if (station is not (CraftStation.Hand or CraftStation.Bench)) return false;
        return grid >= Width && grid >= Height;
    }

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

    /// <summary>
    /// What sort of job this is, which is what says whether a specialised smelter will take it.
    /// </summary>
    /// <remarks>
    /// On the recipe rather than on the item, because it is a property of the <em>job</em>: the same
    /// rock is ore when it is being reduced to metal and is not when it is being fired into a
    /// building block. A table of "which items are ore" would have to be kept in step with this one.
    /// </remarks>
    public SmeltWork Work { get; init; } = SmeltWork.Other;
}

/// <summary>What kind of job a smelt is.</summary>
public enum SmeltWork
{
    /// <summary>Firing, baking, melting sand — anything only a plain furnace will do.</summary>
    Other,

    /// <summary>Reducing a raw lump to metal, which is what a blast furnace is for.</summary>
    Ore,
}

/// <summary>Which sort of smelting block this is.</summary>
public enum FurnaceKind
{
    /// <summary>A furnace. Takes everything, at the time each recipe says.</summary>
    Furnace,

    /// <summary>A blast furnace. Ore only, in half the time.</summary>
    Blast,
}

/// <summary>What each kind of smelter will take, and how fast.</summary>
public static class FurnaceKinds
{
    /// <summary>How long a smelt takes here, against what the recipe says on its own.</summary>
    /// <remarks>
    /// ⚠ <b>Half, and that is the whole of what a blast furnace is for.</b> It costs five iron and
    /// a furnace and it takes nothing but ore, so what it gives back has to be worth walking to —
    /// and the genre's answer, which is the right one, is that it is simply twice as quick.
    /// </remarks>
    public static float SpeedOf(FurnaceKind kind) => kind == FurnaceKind.Blast ? 0.5f : 1f;

    /// <summary>True when a smelter of this kind will do this job at all.</summary>
    public static bool Takes(FurnaceKind kind, SmeltWork work) =>
        kind != FurnaceKind.Blast || work == SmeltWork.Ore;
}
