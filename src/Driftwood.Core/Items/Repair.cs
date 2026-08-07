using Driftwood.Core.Blocks;

namespace Driftwood.Core.Items;

/// <summary>
/// Mending a worn thing on an anvil, and wearing the anvil out doing it.
/// </summary>
/// <remarks>
/// <para>⛳ <b>The first path in the game that gives durability BACK.</b> <see cref="ItemStack"/> has
/// carried wear since tools landed and nothing anywhere reduced it, so every tool and every piece of
/// armour has been strictly consumable — you do not repair a pickaxe, you make another one. That is
/// the actual hole; the anvil is the shape of the fix.</para>
/// <para>⛔ <b>An anvil is not a crafting station and must not be one.</b> A grid station has one
/// answer per arrangement and returns a NEW stack; this returns the SAME stack, carrying whatever
/// wear the material could not pay off, plus its own enchantments and name the day those exist.
/// Every one of those is a bespoke result, which is why it is a <see cref="BlockUse"/> and not a
/// <see cref="CraftStation"/>.</para>
/// </remarks>
public static class Repair
{
    /// <summary>
    /// How much of a thing's total durability one unit of its material puts back.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>A share of the whole rather than a fixed number of uses.</b> A stormglass pickaxe has
    /// six times the life of a wooden one, and an ingot that returned fifty uses would mend a wooden
    /// tool outright and barely dent a stormglass one — the good tool would be the expensive one to
    /// keep. A quarter each means four of the right material always mends anything from nothing.
    /// </remarks>
    public const float PerMaterial = 0.25f;

    /// <summary>How many of the material one repair will spend at most.</summary>
    public const int MostAtOnce = 4;

    /// <summary>
    /// What a thing is mended with: the material it was made of.
    /// </summary>
    /// <remarks>
    /// ⛳ Read off the recipe tree rather than stated a second time. A tool's name is
    /// <c>&lt;tier&gt;_&lt;head&gt;</c> and a piece of armour's is <c>&lt;material&gt;_&lt;piece&gt;</c>,
    /// and both tables already say what that tier is beaten out of — so the mending material is a
    /// lookup, and a tier added to either table is mendable without an edit here.
    /// </remarks>
    public static ItemId MaterialFor(ItemRegistry items, ItemType worn)
    {
        foreach (var material in Armour.Materials)
        foreach (var piece in Armour.Pieces)
            if (string.Equals(Armour.ItemName(material, piece), worn.Name, StringComparison.Ordinal))
                return items.TryByName(material.Made, out var plate) ? plate.Id : ItemId.None;

        foreach (var tier in StarterItems.Tiers)
        foreach (var head in StarterItems.Heads)
            if (string.Equals($"{tier.Name}_{head.Name}", worn.Name, StringComparison.Ordinal))
                // ⚠ A TAG rather than an item for the first two rungs — wood is "#planks" and stone
                // is "#rough_stone" — so those two answer nothing and are not mendable. That is the
                // honest result rather than a special case: you do not repair a wooden pickaxe, you
                // make another one, and the anvil is a thing you build after those two rungs anyway.
                return items.TryByName(tier.Material, out var made) ? made.Id : ItemId.None;

        return ItemId.None;
    }

    /// <summary>
    /// What comes off an anvil, given a worn thing and some of its material.
    /// </summary>
    /// <param name="spent">How many of the material the repair actually used.</param>
    /// <returns>The mended stack, or the worn one unchanged when nothing could be done.</returns>
    /// <remarks>
    /// ⛔ <b>Spends only what it uses.</b> A tool one use short of full must not eat four ingots to
    /// get there — the count is worked back from the damage rather than taken because it was
    /// offered, which is the difference between a repair and a toll.
    /// </remarks>
    public static ItemStack Mend(
        ItemRegistry items, ItemStack worn, ItemStack material, out int spent)
    {
        spent = 0;

        if (worn.IsEmpty || material.IsEmpty) return worn;

        var type = items[worn.Item];
        if (type.Durability <= 0 || worn.Damage <= 0) return worn;
        if (MaterialFor(items, type) != material.Item) return worn;

        var perUnit = Math.Max(1, (int)MathF.Round(type.Durability * PerMaterial));

        // Enough to cover the damage, no more, and never more than the anvil takes in one go or
        // than is actually on the bench.
        var wanted = (worn.Damage + perUnit - 1) / perUnit;
        spent = Math.Clamp(wanted, 0, Math.Min(MostAtOnce, material.Count));

        if (spent <= 0) return worn;

        return worn with { Damage = Math.Max(0, worn.Damage - spent * perUnit) };
    }

    /// <summary>
    /// Checks a repair gives back what it should and costs what it should.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Every arm has its opposite here, because "it repaired something" is true of a build that
    /// hands back a fresh item for free.</b> So: the wrong material does nothing, an undamaged thing
    /// does nothing and costs nothing, a barely-damaged one costs ONE, and a ruined one is capped
    /// rather than mended outright. The cap is the arm that fails a build which simply zeroes the
    /// damage, which is the obvious wrong implementation.
    /// </remarks>
    public static List<string> Validate(ItemRegistry items)
    {
        var faults = new List<string>();

        var pick = items.ByName("iron_pickaxe");
        var iron = items.ByName("iron_ingot");
        var gold = items.ByName("gold_ingot");

        var perUnit = Math.Max(1, (int)MathF.Round(pick.Durability * PerMaterial));

        // Ruined, and four ingots on the bench: capped at four, so it comes back short of new.
        var ruined = new ItemStack(pick.Id, 1) with { Damage = pick.Durability - 1 };
        var mended = Mend(items, ruined, new ItemStack(iron.Id, 8), out var spent);

        if (spent != MostAtOnce)
            faults.Add($"mending a ruined pickaxe spent {spent} iron rather than {MostAtOnce}");

        if (mended.Damage != Math.Max(0, ruined.Damage - MostAtOnce * perUnit))
            faults.Add($"a ruined pickaxe came back at {mended.Damage} damage, which is not four ingots' worth");

        // ⛔ THE ARM THAT CATCHES "just set damage to zero".
        if (mended.Damage == 0 && ruined.Damage > MostAtOnce * perUnit)
            faults.Add("four ingots mended a ruined pickaxe outright, so the material is not being counted");

        // One use short: costs exactly one.
        var nearly = new ItemStack(pick.Id, 1) with { Damage = 1 };
        Mend(items, nearly, new ItemStack(iron.Id, 8), out var little);
        if (little != 1) faults.Add($"mending one point of damage spent {little} iron");

        // Undamaged: nothing happens and nothing is spent.
        var fresh = new ItemStack(pick.Id, 1);
        var untouched = Mend(items, fresh, new ItemStack(iron.Id, 8), out var wasted);
        if (wasted != 0 || untouched != fresh)
            faults.Add($"mending an undamaged pickaxe spent {wasted} iron");

        // The wrong metal does nothing.
        Mend(items, ruined, new ItemStack(gold.Id, 8), out var wrong);
        if (wrong != 0) faults.Add($"gold mended an iron pickaxe, spending {wrong}");

        // And armour goes through the same door, or the user's own ask is only half built.
        var helmet = items.ByName("iron_helmet");
        if (MaterialFor(items, helmet) != iron.Id)
            faults.Add("an iron helmet is not mended with iron");

        return faults;
    }
}
