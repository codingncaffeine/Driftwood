namespace Driftwood.Core.Items;

/// <summary>What made a creature give something up.</summary>
/// <remarks>
/// ⛔ <b>Three, and a table keyed only on death cannot express two of them.</b> That is the whole
/// reason this is not <c>BlockDrops</c> with animals in it. A cow is killed for its leather; a sheep
/// is <em>sheared</em>, which takes its fleece and leaves the sheep; a hen lays an egg while nobody
/// is looking. Written as one trigger column rather than three tables so that a kind that does two
/// of them — a sheep gives wool both ways — says so in two rows rather than in two files.
/// </remarks>
public enum DropTrigger
{
    /// <summary>It died.</summary>
    Killed,

    /// <summary>Something was taken off it with a tool, and it walked away.</summary>
    Harvested,

    /// <summary>It left something behind on its own.</summary>
    Shed,
}

/// <summary>
/// What each creature leaves, and what had to happen for it to leave it.
/// </summary>
/// <remarks>
/// <para>The mirror of <see cref="BlockDrops"/> on the other side of the divide, and the same trade
/// made for the same reason: one table rather than a field on each creature, because a drop is a
/// count that need not be one, an item that has nothing to do with the animal's name, and an answer
/// that depends on what was in hand.</para>
/// <para>⚠ <b>A creature with no rows leaves nothing at all, and that is a legitimate state.</b> A
/// bat drops nothing in any game that has one. The audit weighs how many of them there are rather
/// than refusing the case.</para>
/// </remarks>
public sealed class CreatureDrops
{
    /// <summary>One row: what a kind leaves, when, and how much.</summary>
    /// <param name="Item">The item's name. Resolved once, at construction.</param>
    /// <param name="Min">Fewest it leaves. Zero with a <paramref name="Chance"/> under one is a maybe.</param>
    /// <param name="Tool">
    /// The class of tool that takes it. <see cref="ToolClass.None"/> means anything will do — which
    /// is right for a kill and wrong for a shearing, and is why the field is on the row rather than
    /// on the trigger.
    /// </param>
    /// <param name="Chance">0..1, rolled once for the whole row.</param>
    /// <param name="NeedsFleece">
    /// ⛳ True for anything that comes off a coat. A sheep sheared an hour ago has no wool to give and
    /// no wool to drop when it dies, and both halves of that are this one flag — without it, shearing
    /// a sheep and then killing it hands out two fleeces for one animal.
    /// </param>
    public readonly record struct Rule(
        string Kind, DropTrigger Trigger, string Item, int Min, int Max,
        ToolClass Tool = ToolClass.None, float Chance = 1f, bool NeedsFleece = false);

    private readonly Rule[] _rules;
    private readonly ItemId[] _items;
    private readonly ItemRegistry _catalogue;

    public CreatureDrops(ItemRegistry items, params Rule[] rules)
    {
        _catalogue = items;
        _rules = rules;
        _items = new ItemId[rules.Length];

        // ⛔ Resolved here and never at the moment of a kill. A name that is not an item is a typo,
        // and a typo found on the frame a cow dies is one found by a player rather than by the gate.
        for (var i = 0; i < rules.Length; i++) _items[i] = items.ByName(rules[i].Item).Id;
    }

    public IReadOnlyList<Rule> Rules => _rules;

    /// <summary>
    /// What one creature gives up, for this reason, to whoever is holding that.
    /// </summary>
    /// <param name="shorn">True when its coat has already been taken and has not grown back.</param>
    public List<ItemStack> Roll(string kind, DropTrigger trigger, ItemType? held, bool shorn, Random random)
    {
        var dropped = new List<ItemStack>();

        for (var i = 0; i < _rules.Length; i++)
        {
            var rule = _rules[i];

            if (rule.Trigger != trigger || !string.Equals(rule.Kind, kind, StringComparison.Ordinal)) continue;
            if (rule.NeedsFleece && shorn) continue;
            if (rule.Tool != ToolClass.None && held?.Tool != rule.Tool) continue;
            if (rule.Chance < 1f && random.NextDouble() >= rule.Chance) continue;

            var count = rule.Min == rule.Max ? rule.Min : random.Next(rule.Min, rule.Max + 1);
            if (count > 0) dropped.Add(new ItemStack(_items[i], count));
        }

        return dropped;
    }

    /// <summary>True when this would take something off a live one, so a click can mean shearing.</summary>
    /// <remarks>
    /// ⚠ <b>Asked before the roll, not after it.</b> A shearing that comes up empty still has to
    /// consume the click, make its noise and mark the animal — otherwise a player who shears a sheep
    /// on the frame a chance roll fails is told nothing whatever happened.
    /// </remarks>
    public bool CanHarvest(string kind, ItemType? held, bool shorn)
    {
        foreach (var rule in _rules)
        {
            if (rule.Trigger != DropTrigger.Harvested) continue;
            if (!string.Equals(rule.Kind, kind, StringComparison.Ordinal)) continue;
            if (rule.NeedsFleece && shorn) continue;
            if (rule.Tool != ToolClass.None && held?.Tool != rule.Tool) continue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Every row as the walk wants it: what leaves it, what it is, and what it takes to get.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The tool comes with the row, and the reachability walk has to honour it.</b> Exactly the
    /// mistake the station gate made once: a walk that hands out every drop regardless of what is in
    /// hand reports shears as costing nothing, and then a recipe gated behind them is silently free.
    /// </remarks>
    public IEnumerable<(string Kind, ItemId Item, ToolClass Tool, int Max)> Walk()
    {
        for (var i = 0; i < _rules.Length; i++)
            yield return (_rules[i].Kind, _items[i], _rules[i].Tool, _rules[i].Max);
    }

    /// <summary>
    /// Checks the three mechanisms are actually three, and that each gate refuses what it should.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The reachability walk cannot test the shear gate and it is important to say why.</b> Wool
    /// comes off a dead sheep as well as a live one, so a walk that ignored the tool column entirely
    /// would still report wool as reachable and pass — the gate would be silently free and nothing
    /// would notice, which is exactly the fault the station gate had. The gate is therefore asked
    /// directly, and asked with three different things in hand: nothing, a tool of the wrong class,
    /// and the right one.
    /// </remarks>
    public static List<string> Validate(ItemRegistry items, CreatureDrops table)
    {
        var faults = new List<string>();
        var random = new Random(4931);

        var shears = items.ByName("shears");
        var pickaxe = items.ByName("stone_pickaxe");

        // ⛔ THE GATE, all three arms. Any one alone passes a table that has forgotten the column.
        if (table.CanHarvest("sheep", null, shorn: false))
            faults.Add("a sheep could be sheared with bare hands");

        if (table.CanHarvest("sheep", pickaxe, shorn: false))
            faults.Add("a sheep could be sheared with a pickaxe");

        if (!table.CanHarvest("sheep", shears, shorn: false))
            faults.Add("a sheep could not be sheared with shears");

        if (table.CanHarvest("sheep", shears, shorn: true))
            faults.Add("a sheep already shorn could be sheared again");

        if (table.CanHarvest("cow", shears, shorn: false))
            faults.Add("a cow could be sheared");

        // What a kill actually leaves, counted over enough rolls that a range reads as a range.
        var kills = new Dictionary<string, int>(StringComparer.Ordinal);
        var woolFromShorn = 0;
        var eggsFromKills = 0;
        var featherless = 0;

        for (var i = 0; i < 400; i++)
        {
            foreach (var stack in table.Roll("cow", DropTrigger.Killed, null, false, random))
                kills[items[stack.Item].Name] = kills.GetValueOrDefault(items[stack.Item].Name) + stack.Count;

            foreach (var stack in table.Roll("sheep", DropTrigger.Killed, null, true, random))
                if (items[stack.Item].Name == "wool") woolFromShorn++;

            var chicken = table.Roll("chicken", DropTrigger.Killed, null, false, random);
            foreach (var stack in chicken)
                if (items[stack.Item].Name == "egg") eggsFromKills++;

            if (chicken.All(s => items[s.Item].Name != "feather")) featherless++;
        }

        if (kills.GetValueOrDefault("leather") is < 400 or > 1200)
            faults.Add($"400 cows left {kills.GetValueOrDefault("leather")} leather, which is not 1-3 each");

        if (kills.GetValueOrDefault("raw_beef") is < 400 or > 1200)
            faults.Add($"400 cows left {kills.GetValueOrDefault("raw_beef")} beef, which is not 1-3 each");

        // ⛔ A shorn sheep gives no fleece — and the OTHER arm, or "no wool" is equally true of a
        // table that has no wool row in it at all.
        if (woolFromShorn != 0) faults.Add($"{woolFromShorn} shorn sheep still dropped their fleece");

        var woolFromWhole = 0;
        for (var i = 0; i < 400; i++)
            foreach (var stack in table.Roll("sheep", DropTrigger.Killed, null, false, random))
                if (items[stack.Item].Name == "wool") woolFromWhole++;

        if (woolFromWhole != 400)
            faults.Add($"{woolFromWhole} of 400 unshorn sheep dropped a fleece");

        // ⚠ A range that starts at zero has to do both. All 400 chickens leaving a feather means the
        // minimum is not being honoured; none of them means the roll never fires.
        if (featherless is 0 or 400)
            faults.Add($"{featherless} of 400 chickens left no feather, which is not a 0-2 range");

        if (eggsFromKills != 0) faults.Add($"{eggsFromKills} eggs came off killing a chicken");

        // And the shed trigger gives exactly the egg, which is the third mechanism proving it is not
        // simply the first one under another name.
        var shed = table.Roll("chicken", DropTrigger.Shed, null, false, random);
        if (shed.Count != 1 || items[shed[0].Item].Name != "egg")
            faults.Add($"a chicken shed {shed.Count} things, the first of which was not an egg");

        if (table.Roll("cow", DropTrigger.Shed, null, false, random).Count != 0)
            faults.Add("a cow shed something");

        return faults;
    }

    /// <summary>A line per kind, for the report.</summary>
    public IEnumerable<string> Describe()
    {
        foreach (var kind in _rules.Select(r => r.Kind).Distinct(StringComparer.Ordinal))
        {
            var parts = new List<string>();

            for (var i = 0; i < _rules.Length; i++)
            {
                var rule = _rules[i];
                if (!string.Equals(rule.Kind, kind, StringComparison.Ordinal)) continue;

                var count = rule.Min == rule.Max ? $"{rule.Min}" : $"{rule.Min}-{rule.Max}";
                var how = rule.Trigger switch
                {
                    DropTrigger.Killed => "killed",
                    DropTrigger.Harvested => $"{rule.Tool.ToString().ToLowerInvariant()}",
                    _ => "shed",
                };

                parts.Add($"{count}x {_catalogue[_items[i]].Name} ({how})");
            }

            yield return $"{kind}: {string.Join(", ", parts)}";
        }
    }
}
