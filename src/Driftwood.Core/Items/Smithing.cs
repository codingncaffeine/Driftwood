namespace Driftwood.Core.Items;

/// <summary>
/// The smithing table's rulebook: which rung a tool stands on, what the next one costs, and the
/// upgrade itself — the same tool a tier up at the same wear fraction.
/// </summary>
/// <remarks>
/// <para>⛳ <b>USER DECISION 2026-08-09: WEAR CARRIES.</b> A smithing table that handed back a
/// fresh tool would also be a repair bench, and the anvil already owns repair — upgrading
/// changes the metal, not the miles on it. What carries is the FRACTION, because durabilities
/// differ sevenfold across the ladder and carrying the raw count would mend a tool, or nearly
/// kill one, by accident of arithmetic.</para>
/// <para>⛳ <b>The ladder is the tier table minus gold</b>, which that table's own remark puts
/// beside the ladder rather than on it — a choice for a particular afternoon, not a rung on the
/// way up. A gold tool is refused with its own word, and nothing upgrades INTO gold.</para>
/// <para>No screen, on the anvil's argument: one tool in hand and one material from the pockets
/// is nothing to arrange.</para>
/// </remarks>
public static class Smithing
{
    /// <summary>The block and the item, named once for the three files that mention it.</summary>
    public const string Table = "smithing_table";

    /// <summary>
    /// The ladder, in upgrade order. Strictly rising harvest tiers by construction once gold
    /// steps aside: wood, stone, copper, iron, stormglass, diamond.
    /// </summary>
    public static IEnumerable<StarterItems.ToolTier> Ladder =>
        StarterItems.Tiers.Where(t => t.Name != "gold");

    /// <summary>The rung and head a tool stands on, or null for anything that is not a tool.</summary>
    /// <remarks>
    /// Matched against the same two tables the tools were generated from, so a new rung or a
    /// fifth head is known here the day it is added — never a second list.
    /// </remarks>
    public static (StarterItems.ToolTier Rung, string Head)? RungOf(ItemType type)
    {
        foreach (var tier in StarterItems.Tiers)
        foreach (var (head, _) in StarterItems.Heads)
            if (type.Name == $"{tier.Name}_{head}")
                return (tier, head);

        return null;
    }

    /// <summary>True of a gold tool: beside the ladder by design, refused with its own word.</summary>
    public static bool BesideLadder(ItemType type) => RungOf(type)?.Rung.Name == "gold";

    /// <summary>The next rung up for this tool — null off the ladder, at its top, or for gold.</summary>
    public static StarterItems.ToolTier? NextOf(ItemType type)
    {
        var rung = RungOf(type);
        if (rung is null || rung.Value.Rung.Name == "gold") return null;

        StarterItems.ToolTier? previous = null;
        foreach (var step in Ladder)
        {
            if (previous?.Name == rung.Value.Rung.Name) return step;
            previous = step;
        }

        return null;   // the top of the ladder
    }

    /// <summary>
    /// The upgrade: the same head a tier up, wear carried as a fraction of durability.
    /// </summary>
    /// <remarks>
    /// ⛔ A tool never arrives dead: the carried fraction is clamped one use short, so the worst
    /// a paid upgrade can hand back is a tool with one swing left — never an empty hand.
    /// </remarks>
    public static ItemStack Upgraded(ItemRegistry items, ItemStack held)
    {
        var type = items[held.Item];
        var rung = RungOf(type);
        var next = NextOf(type);
        if (rung is null || next is null) return ItemStack.Empty;

        var to = items.ByName($"{next.Value.Name}_{rung.Value.Head}");

        var fraction = type.Durability <= 0 ? 0f : held.Damage / (float)type.Durability;
        var damage = Math.Clamp((int)MathF.Round(fraction * to.Durability), 0, to.Durability - 1);

        return new ItemStack(to.Id, 1, damage);
    }
}
