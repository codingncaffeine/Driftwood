using Driftwood.Core.Entities;
using Driftwood.Core.Items;

namespace Driftwood.Core.Ui;

/// <summary>What a tooltip says: a name, and a dimmer line under it.</summary>
/// <param name="Title">What the thing is. Never empty when a tooltip is shown at all.</param>
/// <param name="Note">What is worth knowing about it, or empty when there is nothing.</param>
public readonly record struct TooltipText(string Title, string Note)
{
    public static readonly TooltipText None = new("", "");

    public bool IsEmpty => Title.Length == 0;
}

/// <summary>
/// Turns what the pointer is over into words.
/// </summary>
/// <remarks>
/// <para>⛳ <b>In Core, and away from the renderer, because it is a lookup rather than a drawing.</b>
/// Every fact it needs — an item's label, a tool's class and tier, what a recipe costs — is already
/// carried by the thing itself; nothing here is a second table. That also makes it checkable without
/// a window, which matters because the failure worth catching is not "the box is in the wrong place"
/// but "hovering a furnace's fuel slot says nothing at all".</para>
/// <para>⚠ <b>An empty POCKET says nothing, and an empty FUEL slot says "fuel".</b> There are
/// thirty-six pockets and a tooltip over every one of them is a box that follows the pointer round
/// an empty inventory saying "pocket" — noise. The special squares are the opposite case: an empty
/// boots slot that says nothing is a square nobody can work out the purpose of, which is exactly the
/// state the worn slots are in until armour lands.</para>
/// </remarks>
public static class Tooltip
{
    /// <summary>What the pointer is over, in words. Empty when there is nothing worth saying.</summary>
    public static TooltipText For(
        Zone zone, ItemStack stack, ItemRegistry items,
        Recipe? recipe = null, bool payable = true) => zone.Kind switch
    {
        ZoneKind.Slot => stack.IsEmpty ? Empty(zone.Role, zone.Index) : Of(stack, items),
        ZoneKind.Recipe when recipe is not null => OfRecipe(recipe, items, payable),
        ZoneKind.Button => OfButton((ScreenButton)zone.Index),
        _ => TooltipText.None,
    };

    /// <summary>One item, named, with whatever about it is worth a second line.</summary>
    public static TooltipText Of(ItemStack stack, ItemRegistry items)
    {
        if (stack.IsEmpty) return TooltipText.None;

        var type = items[stack.Item];
        var notes = new List<string>(3);

        // ⚠ Tier first, because it is the fact a player is actually asking about when they hover a
        // pickaxe: not how fast it digs but whether it will bring the thing up at all.
        if (type.IsTool)
        {
            notes.Add(type.Tool.ToString().ToLowerInvariant());
            if (type.Tier > 0) notes.Add($"tier {type.Tier}");
        }

        if (type.AttackDamage > 0) notes.Add($"{Hearts(Combat.DamageOf(type))} damage");

        if (type.IsFood) notes.Add($"restores {Hearts(type.Feeds)}");

        if (type.IsFuel) notes.Add($"burns {type.BurnSeconds:0.#}s");

        if (type.Wears is { } worn) notes.Add($"worn on the {worn.ToString().ToLowerInvariant()}");

        // ⚠ Wear last and only when there is some. A fresh tool saying "0 of 60 used" is a number
        // nobody needed; a tool at 58 of 60 is the one thing a player wants to see before a trip.
        if (type.Durability > 0 && stack.Damage > 0)
            notes.Add($"{type.Durability - stack.Damage} of {type.Durability} left");

        return new TooltipText(type.Label, string.Join(" · ", notes));
    }

    /// <summary>A recipe: what it makes, and what it costs.</summary>
    /// <remarks>
    /// ⛳ <b>The cost line is the half that makes this worth having in the book.</b> A recipe picture
    /// says what goes where and says nothing about the names of any of it — which is fine for planks
    /// and useless for the twelfth grey rock. It is also what #46 wanted, and it belongs here rather
    /// than in a second panel.
    /// </remarks>
    public static TooltipText OfRecipe(Recipe recipe, ItemRegistry items, bool payable)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var slot in recipe.Ingredients)
            counts[Named(slot, items)] = counts.GetValueOrDefault(Named(slot, items)) + 1;

        var parts = counts.Select(c => c.Value > 1 ? $"{c.Value} {c.Key}" : c.Key);
        var cost = string.Join(", ", parts);

        var made = items[recipe.Result.Item];
        var title = recipe.Result.Count > 1 ? $"{made.Label} x{recipe.Result.Count}" : made.Label;

        // ⛔ WHERE IT IS MADE COMES FIRST, AND IT IS THE HALF THAT WAS MISSING. A user read
        // "5 iron ingot, 3 smooth stone, furnace" off a blast furnace and concluded it could not be
        // made at a bench — because a furnace has two slots and neither is for building in. It is a
        // bench recipe with a furnace as an ingredient. One flat list was carrying both "made at"
        // and "made of", and a reader has no way to tell which any entry is.
        //
        // ⚠ Says so when it cannot be paid for. The book already dims those, and a dimmed picture is
        // a picture somebody squints at — the words are what actually answer "why is this grey".
        return new TooltipText(
            title, $"{recipe.MadeAt} · {(payable ? cost : $"needs {cost}")}");
    }

    /// <summary>What a button on a screen does.</summary>
    public static TooltipText OfButton(ScreenButton which) => which switch
    {
        ScreenButton.Book => new TooltipText("recipe book", "everything you can make"),
        ScreenButton.PageBack => new TooltipText("back a page", ""),
        ScreenButton.PageForward => new TooltipText("on a page", ""),
        _ => TooltipText.None,
    };

    /// <summary>
    /// What an empty square is <em>for</em>, or nothing when it is simply storage.
    /// </summary>
    /// <remarks>
    /// ⛔ The pockets and a chest's rows are deliberately absent. They are the two roles with
    /// dozens of squares each, and naming them turns an empty inventory into a box that chases the
    /// pointer about saying "pocket".
    /// </remarks>
    public static TooltipText Empty(SlotRole role, int index) => role switch
    {
        SlotRole.Equip => index == (int)EquipSlot.Offhand
            ? new TooltipText("the other hand", "anything at all")
            : new TooltipText(((EquipSlot)index).ToString().ToLowerInvariant(), "armour"),

        SlotRole.Craft => new TooltipText("crafting", "lay something out here"),
        SlotRole.Result => new TooltipText("what it makes", ""),
        SlotRole.Smelting => new TooltipText("to smelt", "ore, food, sand, clay"),
        SlotRole.Fuel => new TooltipText("fuel", "coal, charcoal, anything wooden"),
        SlotRole.Smelted => new TooltipText("what came out", ""),
        SlotRole.Cutting => new TooltipText("to cut", "any worked stone"),
        SlotRole.Cut => new TooltipText("what it cuts into", ""),
        _ => TooltipText.None,
    };

    /// <summary>An ingredient's name: the tag's where there is one, the item's otherwise.</summary>
    private static string Named(Ingredient slot, ItemRegistry items) =>
        slot.Members.Length == 1 ? items[slot.Members[0]].Label : slot.Name;

    /// <summary>Half-hearts as hearts, which is the unit anybody reading it counts in.</summary>
    /// <remarks>
    /// ⚠ The model is in half-hearts and the display is in hearts, and this is the one conversion.
    /// Written as a fraction rather than rounded — a wooden sword doing "2" damage when the bar it
    /// empties is ten hearts long is a number that means nothing.
    /// </remarks>
    private static string Hearts(int halfHearts) =>
        halfHearts % 2 == 0 ? $"{halfHearts / 2} hearts" : $"{halfHearts / 2f:0.#} hearts";

    /// <summary>
    /// Checks every square, button and recipe says something, and that the quiet ones stay quiet.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Both halves, or "a tooltip appeared" passes a build that shows one everywhere.</b> The
    /// pockets and a chest's rows have to answer with nothing when empty and with a name when full,
    /// which is the pair that separates "it reads the slot" from "it prints a constant".
    /// </remarks>
    public static List<string> Validate(ItemRegistry items, RecipeBook book)
    {
        var faults = new List<string>();

        static Zone Slot(SlotRole role, int index) => new(ZoneKind.Slot, role, index, 0f, 0f, 16f, 16f);

        // ⛔ THE QUIET ONES. An empty pocket and an empty chest slot say nothing at all.
        foreach (var role in new[] { SlotRole.Pocket, SlotRole.Stored })
            if (!For(Slot(role, 0), ItemStack.Empty, items).IsEmpty)
                faults.Add($"an empty {role} square offered a tooltip");

        // And every special square says what it is for, by name.
        foreach (var role in new[]
                 {
                     SlotRole.Equip, SlotRole.Craft, SlotRole.Result, SlotRole.Smelting,
                     SlotRole.Fuel, SlotRole.Smelted, SlotRole.Cutting, SlotRole.Cut,
                 })
        {
            if (!For(Slot(role, 0), ItemStack.Empty, items).IsEmpty) continue;
            faults.Add($"an empty {role} square said nothing about what it is for");
        }

        // ⛳ The user's own example: a pocket with something in it names it.
        var pickaxe = items.ByName("stone_pickaxe");
        var filled = For(Slot(SlotRole.Pocket, 0), new ItemStack(pickaxe.Id, 1), items);

        if (filled.Title != pickaxe.Label)
            faults.Add($"a pocket holding a stone pickaxe was titled '{filled.Title}'");

        if (!filled.Note.Contains("tier", StringComparison.Ordinal))
            faults.Add($"a pickaxe's note does not say its tier: '{filled.Note}'");

        // A worn tool says how much is left, and a fresh one does not clutter itself saying so.
        var worn = Of(new ItemStack(pickaxe.Id, 1) { Damage = 100 }, items);
        if (!worn.Note.Contains(" left", StringComparison.Ordinal))
            faults.Add($"a worn pickaxe does not say what is left of it: '{worn.Note}'");

        if (filled.Note.Contains(" left", StringComparison.Ordinal))
            faults.Add("a fresh pickaxe reports its wear");

        // Food and fuel each say the one thing they are for.
        var cooked = Of(new ItemStack(items.ByName("cooked_beef").Id, 1), items);
        if (!cooked.Note.Contains("restores", StringComparison.Ordinal))
            faults.Add($"cooked beef does not say what it restores: '{cooked.Note}'");

        var coal = Of(new ItemStack(items.ByName("coal").Id, 1), items);
        if (!coal.Note.Contains("burns", StringComparison.Ordinal))
            faults.Add($"coal does not say how long it burns: '{coal.Note}'");

        // ⛳ And a recipe says what it costs, in words, which is the half a picture cannot.
        var torch = book.Recipes.FirstOrDefault(r => r.Name == "torch");
        if (torch is null) faults.Add("the torch recipe is gone, so the cost line cannot be checked");
        else
        {
            var told = OfRecipe(torch, items, payable: true);

            if (!told.Title.StartsWith("torch", StringComparison.Ordinal))
                faults.Add($"the torch recipe is titled '{told.Title}'");

            if (!told.Note.Contains("stick", StringComparison.OrdinalIgnoreCase))
                faults.Add($"the torch recipe's cost does not mention a stick: '{told.Note}'");

            if (OfRecipe(torch, items, payable: false).Note is var cannot
                && !cannot.Contains("needs", StringComparison.Ordinal))
            {
                faults.Add($"a recipe that cannot be paid for reads '{cannot}'");
            }

            // ⛔⛔ AND WHERE IT IS MADE, which is the half that was missing and the half a user
            // report turned on. A cost line alone cannot distinguish "made at" from "made of": they
            // read a blast furnace as wanting five iron, three smooth stone AND A FURNACE, and every
            // reading of that sentence except the right one says it cannot be made at a bench.
            //
            // ⚠ Both arms, and the second is the one that matters. A build that printed "at a bench"
            // on everything would satisfy the first — so a torch, which two hands really do make,
            // has to say so instead.
            var bench = book.Recipes.FirstOrDefault(r => r.Name == "blast furnace");

            if (bench is null) faults.Add("the blast furnace recipe is gone");
            else if (!OfRecipe(bench, items, payable: true).Note.Contains("at a bench", StringComparison.Ordinal))
                faults.Add(
                    "a bench recipe does not say where it is made: "
                    + $"'{OfRecipe(bench, items, payable: true).Note}'");

            if (!told.Note.Contains("in your hands", StringComparison.Ordinal))
                faults.Add($"a hand recipe does not say it is made in the hands: '{told.Note}'");
        }

        // ⛔ THE NEGATIVE CONTROL. A zone that is not a thing must answer with nothing — without
        // this, a build that titled everything "pocket" would pass every arm above.
        foreach (var kind in new[] { ZoneKind.None, ZoneKind.Row, ZoneKind.Tab, ZoneKind.Scrollbar })
        {
            var quiet = For(new Zone(kind, SlotRole.None, 0, 0f, 0f, 8f, 8f), ItemStack.Empty, items);
            if (!quiet.IsEmpty) faults.Add($"a {kind} zone offered a tooltip: '{quiet.Title}'");
        }

        return faults;
    }
}
