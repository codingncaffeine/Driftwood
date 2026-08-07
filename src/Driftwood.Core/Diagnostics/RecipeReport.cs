using System.Text;
using Driftwood.Core.Blocks;
using Driftwood.Core.Items;

namespace Driftwood.Core.Diagnostics;

/// <summary>
/// Every recipe in the game, where it can actually be made, and what it actually costs.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Built from a user report that reads as a bug and is not one.</b> They tried to make a
/// blast furnace, were told it wanted five iron, three smooth stone <em>and a furnace</em>, and
/// concluded it could not be made at a bench — because a furnace has two slots and neither of them
/// is for building things in. The mechanics were right the whole time: it is a bench recipe with a
/// furnace as an <em>ingredient</em>, which means breaking the one already built and putting it in
/// the grid. Nothing anywhere said so.</para>
/// <para>⛔ <b>So the interesting output is not the list, it is the FINDINGS.</b> A dump of 195
/// recipes is a thing nobody reads. What is worth printing is the handful of rows where what a
/// player would reasonably expect and what the game does come apart — a workstation used as an
/// ingredient, a recipe whose declared station is not the station it is really made at, an item
/// nothing consumes, an item nothing produces.</para>
/// <para>Headless, in Core, and takes only the registries: the whole point is that "can this be
/// made, and where" is answerable without a window, a world or somebody standing at a bench.</para>
/// </remarks>
public static class RecipeReport
{
    /// <summary>One thing worth saying about one recipe.</summary>
    /// <param name="Kind">A short tag, so a reader can skim for the class rather than the row.</param>
    public readonly record struct Finding(string Kind, string Recipe, string What);

    /// <summary>
    /// Blocks a player builds, places, and then uses — as opposed to ones they stack up.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The set that makes a recipe surprising.</b> Nine planks in a grid is unremarkable; a
    /// <em>furnace</em> in a grid means going back to the one you built, breaking it, and carrying
    /// it to a bench — and a player who has one standing three paces away, lit, with ore in it, will
    /// never guess that is what is being asked. It is the genre's own convention and it is still the
    /// single least discoverable thing in the tree.
    /// </remarks>
    public static readonly string[] Workstations =
        ["bench", "furnace", "blast_furnace", "smoker", "barrel", "chest", "stonecutter", "smithing_table"];

    /// <summary>What a recipe costs, counted rather than listed slot by slot.</summary>
    public static string Cost(Recipe recipe)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var slot in recipe.Ingredients)
            counts[slot.Name] = counts.GetValueOrDefault(slot.Name) + 1;

        return string.Join(" + ", counts.Select(c => c.Value == 1 ? c.Key : $"{c.Value} {c.Key}"));
    }

    /// <summary>
    /// The whole report, and the findings under it.
    /// </summary>
    /// <param name="findings">Everything worth acting on, most surprising first.</param>
    public static string Build(
        BlockRegistry blocks, ItemRegistry items, RecipeBook book,
        BlockDrops drops, CreatureDrops creatures, out List<Finding> findings)
    {
        findings = [];
        var text = new StringBuilder();

        var stations = new HashSet<ItemId>();
        foreach (var name in Workstations)
            if (items.TryByName(name, out var type)) stations.Add(type.Id);

        // ── The list, grouped by where a player would actually go to make each thing ────────────
        var byPlace = book.Recipes
            .GroupBy(r => r.MadeAt)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var place in byPlace)
        {
            text.AppendLine($"  {place.Key}  ({place.Count()})");

            foreach (var recipe in place)
            {
                var result = items[recipe.Result.Item];
                var made = recipe.Result.Count > 1 ? $"{recipe.Result.Count}x {result.Name}" : result.Name;

                text.AppendLine(
                    $"    {made,-28} {recipe.Width}x{recipe.Height}"
                    + $"{(recipe.Shapeless ? " loose" : "      ")}  {Cost(recipe)}");
            }
        }

        text.AppendLine($"  smelting  ({book.Smelting.Count})");
        foreach (var smelt in book.Smelting)
            text.AppendLine(
                $"    {items[smelt.Result.Item].Name,-28} {smelt.Work.ToString().ToLowerInvariant(),-6}"
                + $"      {smelt.Input.Name}");

        // ── ⛔⛔ THE ONE THAT MATTERS: HAND IT THE INGREDIENTS AND SEE IF IT CRAFTS ──────────────
        // Reported by the user as "it never actually became available to make", and everything else
        // in this file would have gone on agreeing that it was fine. The audit's own recipe check
        // lays a recipe out and asks whether the book FINDS it, which is a question about matching;
        // the reachability walk asks whether the ingredients can be OBTAINED, which is a question
        // about the tree. Neither one puts the exact ingredients in a real inventory and asks the
        // book to pay for them, and that is the question a player is actually asking.
        foreach (var recipe in book.Recipes)
        {
            var pockets = new Inventory(items);
            var missing = false;

            foreach (var slot in recipe.Ingredients)
            {
                if (slot.Members.Length == 0) { missing = true; break; }
                pockets.Add(new ItemStack(slot.Members[0], 1));
            }

            var made = items[recipe.Result.Item].Name;

            if (missing)
            {
                findings.Add(new Finding("ingredient-has-no-item", made, "an ingredient resolves to nothing"));
                continue;
            }

            if (!book.CanPay(pockets, recipe))
                findings.Add(new Finding(
                    "cannot-be-paid-for", made,
                    $"handed exactly {Cost(recipe)} in a real inventory, the book still will not pay for it"));

            var station = recipe.Station != CraftStation.Hand
                ? recipe.Station
                : recipe.TooBigForHands ? CraftStation.Bench : CraftStation.Hand;

            // ⛔ A CHOOSING STATION IS ASKED A DIFFERENT QUESTION, and asking it the grid one is how
            // this check's first run produced thirty-two faults that were all its own. A stonecutter
            // has many answers for one input — that is the entire point of it — so TryMatch refuses
            // it by design and Offers is the way in.
            if (CraftStations.Chooses(station))
            {
                var input = recipe.Ingredients.First().Members[0];

                if (!book.Offers(station, input).Contains(recipe))
                    findings.Add(new Finding(
                        "not-offered", made,
                        $"{items[input].Name} put into a {station.ToString().ToLowerInvariant()} "
                        + "is not offered this"));

                continue;
            }

            // And the same arrangement laid into a grid of its own size: the half a player performs
            // rather than the half the book performs for them.
            var side = Math.Max(recipe.Width, recipe.Height);
            var grid = new ItemStack[side * side];

            for (var y = 0; y < recipe.Height; y++)
            for (var x = 0; x < recipe.Width; x++)
                if (recipe.Cells[y * recipe.Width + x] is { } cell)
                    grid[y * side + x] = new ItemStack(cell.Members[0], 1);

            if (!book.TryMatch(grid, side, side, station, out var found) || found != recipe)
                findings.Add(new Finding(
                    "laid-out-and-not-found", made,
                    $"laid out {recipe.Width}x{recipe.Height} {recipe.MadeAt} it comes back as "
                    + $"'{(found is null ? "nothing" : items[found.Result.Item].Name)}'"));
        }

        // ── ⛔ A WORKSTATION USED AS AN INGREDIENT ──────────────────────────────────────────────
        // The user's own report, generalised. Every one of these asks a player to break something
        // they built and are standing next to, and not one of them says so anywhere.
        foreach (var recipe in book.Recipes)
        {
            foreach (var slot in recipe.Ingredients)
            {
                if (!slot.Members.Any(stations.Contains)) continue;

                findings.Add(new Finding(
                    "station-as-ingredient",
                    items[recipe.Result.Item].Name,
                    $"wants a {slot.Name} in the grid, which means breaking one already built and "
                    + $"carrying it {recipe.MadeAt}"));
                break;
            }
        }

        // ── ⛔ THE DECLARED STATION IS NOT THE REAL ONE ─────────────────────────────────────────
        // Station defaults to Hand and most recipes never set it, so a three-wide recipe reads as
        // a hand recipe and is bench-only in fact. Harmless to the matcher and misleading to
        // everything that shows a player where to go.
        var mislabelled = book.Recipes.Count(r => r.Station == CraftStation.Hand && r.TooBigForHands);
        if (mislabelled > 0)
            findings.Add(new Finding(
                "station-not-declared",
                $"{mislabelled} recipes",
                "say Station=Hand and do not fit in two hands, so the gate holding them is their "
                + "SHAPE. Correct today; a hand recipe that grew to three wide would move stations "
                + "without anybody editing a station"));

        // ── ⚠ ITEMS NOTHING CONSUMES ───────────────────────────────────────────────────────────
        var consumed = new HashSet<ItemId>();
        foreach (var recipe in book.Recipes)
        foreach (var slot in recipe.Ingredients)
        foreach (var member in slot.Members) consumed.Add(member);

        foreach (var smelt in book.Smelting)
        foreach (var member in smelt.Input.Members) consumed.Add(member);

        // ⛔ EVERY EXCLUSION IS NAMED, because the first run of this listed thirty-four items and
        // thirty-two of them were fine — twenty-four tools, a pair of shears, three buckets. A
        // findings list that is mostly noise is read once and then never again, which is the same
        // outcome as not having one. "Consumed" is the wrong question for anything whose purpose is
        // to be USED: a tool wears out, fuel burns, food is eaten, armour is worn, a bucket carries.
        var tools = 0;
        foreach (var type in items.All)
        {
            if (type.Id.IsNone) continue;
            if (consumed.Contains(type.Id)) continue;
            if (type.IsFuel || type.IsFood || type.Wears is not null) continue;
            if (type.Places is not null) continue;   // a block you put down is its own purpose

            // ⚠ Counted rather than listed, and it is a real gap in a way the others are not: the
            // smithing table is meant to take a tool and a material and hand back the tool a tier
            // up, which is the one recipe in the plan that consumes one. See #58.
            if (type.IsTool) { tools++; continue; }

            findings.Add(new Finding("nothing-consumes-it", type.Name, "no recipe or smelt takes it"));
        }

        if (tools > 0)
            findings.Add(new Finding(
                "nothing-consumes-it", $"{tools} tools",
                "used rather than consumed, which is right — but the smithing table is supposed to "
                + "take one and hand back the next tier, and it is the last of #58's group A"));

        // ── ⚠ ITEMS NOTHING PRODUCES ───────────────────────────────────────────────────────────
        var produced = new HashSet<ItemId>();
        foreach (var recipe in book.Recipes) produced.Add(recipe.Result.Item);
        foreach (var smelt in book.Smelting) produced.Add(smelt.Result.Item);
        foreach (var (_, item, _, _) in creatures.Walk()) produced.Add(item);

        // ⚠ Asked of every item rather than walked off the block table, because Sources is the only
        // way in and it answers per item. A block that leaves nothing simply answers nothing.
        foreach (var type in items.All)
            if (drops.Sources(type.Id).Any()) produced.Add(type.Id);

        // ⚠ A bucket with something in it is FILLED rather than made — it comes out of a world the
        // player dipped it in, which is not a recipe, a smelt or a drop. Named here so the finding
        // stays about things that genuinely cannot be got.
        var filled = new HashSet<string>(StringComparer.Ordinal) { "water_bucket", "lava_bucket" };

        foreach (var type in items.All)
        {
            if (type.Id.IsNone || produced.Contains(type.Id) || filled.Contains(type.Name)) continue;

            findings.Add(new Finding(
                "nothing-produces-it", type.Name, "no recipe, smelt, block drop or creature leaves it"));
        }

        return text.ToString();
    }
}
