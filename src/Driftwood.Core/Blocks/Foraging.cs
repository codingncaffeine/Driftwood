namespace Driftwood.Core.Blocks;

using Driftwood.Core.Items;

/// <summary>
/// Picking food off a standing plant — the berry bush's rulebook.
/// </summary>
/// <remarks>
/// <para>⛳ <b>In Core, away from the renderer, for the sowing rule's reason:</b> everything here
/// runs under <c>--audit</c> with no window, and the client's part is one switch case that asks
/// this and plays the sound.</para>
/// <para>⛳ <b>Picking beats breaking, and the numbers say so.</b> A pick pays two to three and
/// leaves the bush standing to bear again; breaking the ripe bush pays its drop once and costs the
/// plant. Both routes exist on purpose — breaking is how the FIRST wild berry reaches a pocket,
/// picking is how a kept bush becomes a supply.</para>
/// </remarks>
public static class Foraging
{
    /// <summary>The fewest berries a pick pays.</summary>
    public const int PickLeast = 2;

    /// <summary>And the most, on a kind roll.</summary>
    public const int PickMost = 3;

    /// <summary>
    /// Picks the fruit off a block, when it has any: what it pays, and what the block becomes.
    /// </summary>
    /// <param name="roll">A number in [0,1) from whoever owns the randomness.</param>
    public static bool TryPick(
        BlockRegistry registry, BlockId block, double roll, out BlockId becomes, out int berries)
    {
        becomes = block;
        berries = 0;

        if (registry[block].Name != StarterBlocks.BerryBushRipeName) return false;

        becomes = registry.ByName(StarterBlocks.BerryBushName).Id;
        berries = roll < 0.5 ? PickLeast : PickMost;
        return true;
    }

    /// <summary>Proves a pick pays, resets the bush, and refuses everything else — headlessly.</summary>
    public static List<string> SelfTest(BlockRegistry registry, ItemRegistry items, BlockDrops drops)
    {
        var faults = new List<string>();

        if (!items.TryByName("berries", out _))
            faults.Add("the pick pays 'berries', which is not an item");

        var ripe = registry.ByName(StarterBlocks.BerryBushRipeName).Id;
        var young = registry.ByName(StarterBlocks.BerryBushName).Id;

        if (!TryPick(registry, ripe, 0.0, out var after, out var low) || after != young)
            faults.Add("picking a ripe bush did not leave the young bush standing");
        if (low != PickLeast) faults.Add($"a low roll paid {low} berries, wanted {PickLeast}");

        TryPick(registry, ripe, 0.99, out _, out var high);
        if (high != PickMost) faults.Add($"a high roll paid {high} berries, wanted {PickMost}");

        if (TryPick(registry, young, 0.0, out _, out _))
            faults.Add("a bush with no fruit on it was picked");
        if (TryPick(registry, registry.ByName("stone").Id, 0.0, out _, out _))
            faults.Add("a rock was picked like a bush");

        // ⚠ The economy pin: the best pick must beat breaking the plant, or the mechanic teaches
        // nothing and every bush ends under a fist. Read off the drops table rather than restated,
        // so a retune of the drop moves this with it.
        var broken = drops.Of(ripe);
        if (PickMost <= broken.Count)
            faults.Add(
                $"breaking a ripe bush pays {broken.Count} against a best pick of {PickMost}, "
                + "so keeping a bush teaches nothing");

        return faults;
    }
}
