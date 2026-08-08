namespace Driftwood.Core.Blocks;

using Driftwood.Core.Items;

/// <summary>
/// What rots, how readily, and what the bin gives back — the composter's whole rulebook.
/// </summary>
/// <remarks>
/// <para>⛳ <b>In Core, away from the renderer, because the sowing rule already paid for the
/// alternative.</b> <c>PlantSeed</c> once lived in the client comparing against a string, and three
/// crops would have grown while being unplantable. Everything here runs under <c>--audit</c> with
/// no window: the table, the roll, the ladder from empty to ready, and the payout.</para>
/// <para>⛳ <b>It pays in bone meal, not a new item.</b> The game already has the "hurry a crop
/// along" item and it already comes from bones — this is the daylight route to the same thing, and
/// a farm that feeds its scraps back into itself needs no skeleton hunt. Two sources, one
/// economy.</para>
/// </remarks>
public static class Composting
{
    /// <summary>
    /// What a farm can spare, against the chance a helping actually raises the level.
    /// </summary>
    /// <remarks>
    /// ⚠ The chances rank by how much a thing cost to grow — seeds are nearly free and mostly
    /// air, a baked potato is a grown crop and a fire's time. Spending real food on compost is
    /// legal and wasteful, which is the right way round for a bin.
    /// </remarks>
    private static readonly Dictionary<string, float> Compostables = new(StringComparer.Ordinal)
    {
        ["seeds"] = 0.3f,
        ["wheat"] = 0.65f,
        ["carrot"] = 0.65f,
        ["potato"] = 0.65f,
        ["beetroot"] = 0.65f,
        ["baked_potato"] = 0.85f,
        ["bread"] = 0.85f,
    };

    /// <summary>Bone meal handed back per emptied bin.</summary>
    public const int Yield = 2;

    /// <summary>True when this item can be thrown in at all.</summary>
    public static bool Takes(string item) => Compostables.ContainsKey(item);

    /// <summary>Every name the table accepts, for the check that they all exist.</summary>
    public static IEnumerable<string> Accepted => Compostables.Keys;

    /// <summary>
    /// Throws one helping in: consumes it always, raises the level sometimes.
    /// </summary>
    /// <param name="stage">The bin's current fill, 0 through 7. Ready bins take nothing.</param>
    /// <param name="roll">A number in [0,1) from whoever owns the randomness.</param>
    /// <returns>The stage afterwards, or null when the item is not compostable or the bin is done.</returns>
    public static int? Fill(string item, int stage, double roll)
    {
        if (stage is < 0 or >= StarterBlocks.ComposterStages) return null;
        if (!Compostables.TryGetValue(item, out var chance)) return null;

        return roll < chance ? stage + 1 : stage;
    }

    /// <summary>
    /// Proves the ladder climbs, pays out, and refuses what it should — headlessly.
    /// </summary>
    public static List<string> SelfTest(ItemRegistry items)
    {
        var faults = new List<string>();

        // Every accepted name is a real item, or the table is pointing at nothing.
        foreach (var name in Accepted)
            if (!items.TryByName(name, out _))
                faults.Add($"the composter accepts '{name}', which is not an item");

        // A roll under the chance climbs one rung; over it, the helping is spent for nothing.
        if (Fill("seeds", 0, 0.0) != 1) faults.Add("a lucky helping of seeds did not raise the level");
        if (Fill("seeds", 0, 0.99) != 0) faults.Add("an unlucky helping did not leave the level alone");

        // The whole ladder, with certain rolls: exactly eight helpings from empty to ready.
        var stage = 0;
        var helpings = 0;
        while (stage < StarterBlocks.ComposterStages && helpings < 20)
        {
            stage = Fill("wheat", stage, 0.0) ?? stage;
            helpings++;
        }
        if (helpings != StarterBlocks.ComposterStages)
            faults.Add($"{helpings} certain helpings reached ready, wanted {StarterBlocks.ComposterStages}");

        // A full bin takes nothing more, and a rock was never food for it.
        if (Fill("wheat", StarterBlocks.ComposterStages, 0.0) is not null)
            faults.Add("a ready bin still took a helping");
        if (Fill("stone", 0, 0.0) is not null)
            faults.Add("the bin composted a rock");

        // ⚠ And the payout stays under the bone route: a bone is 3 meal for one drop, a bin of
        // the cheapest filler is Yield meal for ~27 helpings, so meal-per-thing-spent must stay
        // lower on the bin or skeletons stop being worth fighting. Read off the tables rather
        // than restated, so a retune of either side moves this with it.
        var seedHelpings = StarterBlocks.ComposterStages / Compostables["seeds"];
        if (Yield / seedHelpings >= 3.0)
            faults.Add(
                $"a bin of seeds pays {Yield} meal over ~{seedHelpings:F0} helpings, out-earning the bone recipe");

        return faults;
    }
}
