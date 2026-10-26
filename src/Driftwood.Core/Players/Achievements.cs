using Driftwood.Core.Entities;
using Driftwood.Core.Gen;
using Driftwood.Core.Items;

namespace Driftwood.Core.Players;

/// <summary>The pages of the achievement book, in the order their tabs are shown.</summary>
public enum AchievementTab : byte
{
    /// <summary>The story: log to bench to iron to diamond, and down.</summary>
    Driftwood,

    /// <summary>Exploration, combat, magic and the people of the world.</summary>
    Adventure,

    /// <summary>Food, farming, animals and fishing.</summary>
    Husbandry,

    /// <summary>Everything below the hollows.</summary>
    Emberdeep,
}

/// <summary>The reference's three frames: an ordinary step, a goal, and a challenge.</summary>
public enum AchievementFrame : byte { Task, Goal, Challenge }

/// <summary>
/// One thing the game says has happened. <see cref="Subject"/> is what it happened to — an item's
/// name, a creature kind, a biome — and <c>"*"</c> in a criterion matches any subject.
/// </summary>
public readonly record struct AchievementCriterion(string Event, string Subject)
{
    public bool Matches(string @event, string subject) =>
        Event == @event && (Subject == "*" || Subject == subject);

    public string Key => Event + ":" + Subject;
}

/// <summary>
/// One achievement: where it hangs in the tree, what it says, and what earns it.
/// </summary>
/// <param name="Parent">The achievement it hangs off, or empty for a tab's root.</param>
/// <param name="Icon">The item drawn in its frame.</param>
/// <param name="All">
/// True when every criterion must be met (and the tree view shows a count); false when any one does.
/// </param>
public sealed record Achievement(
    string Id, AchievementTab Tab, string Parent, string Title, string Description, string Icon,
    AchievementFrame Frame, AchievementCriterion[] Criteria, bool All = false);

/// <summary>The events the game reports. Kept as names so a save stays readable by eye.</summary>
public static class AchievementEvents
{
    /// <summary>An item is in the pockets — however it got there. Subject: the item.</summary>
    public const string Obtain = "obtain";

    /// <summary>An item is worn. Subject: the item.</summary>
    public const string Wear = "wear";

    /// <summary>The player defeated a creature. Subject: its kind.</summary>
    public const string Kill = "kill";

    /// <summary>The player ate. Subject: the food.</summary>
    public const string Eat = "eat";

    /// <summary>The player slept in a bed. Subject: <c>bed</c>.</summary>
    public const string Sleep = "sleep";

    /// <summary>A trade went through. Subject: the resident's profession.</summary>
    public const string Trade = "trade";

    /// <summary>A site came into view. Subject: its <see cref="Exploration.StructureKind"/>.</summary>
    public const string Discover = "discover";

    /// <summary>An encounter was cleared. Subject: its <see cref="Exploration.EncounterKind"/>.</summary>
    public const string Clear = "clear";

    /// <summary>The player stood at a depth. Subject: one of the <see cref="Depths"/> names.</summary>
    public const string Reach = "reach";

    /// <summary>The player stood in a biome. Subject: the <see cref="Biome"/>.</summary>
    public const string Enter = "enter";

    /// <summary>Two animals made a third. Subject: the kind.</summary>
    public const string Breed = "breed";

    /// <summary>A live animal was shorn. Subject: the kind.</summary>
    public const string Shear = "shear";

    /// <summary>Something was planted. Subject: what was put in the ground.</summary>
    public const string Plant = "plant";

    /// <summary>Bonemeal took. Subject: <c>bonemeal</c>.</summary>
    public const string Grow = "grow";

    /// <summary>The player got into a cart. Subject: the cart's item.</summary>
    public const string Ride = "ride";

    /// <summary>A spell went off. Subject: its stable name.</summary>
    public const string Cast = "cast";

    /// <summary>The character is at least this level. Subject: the level, as digits.</summary>
    public const string Level = "level";

    /// <summary>The character is at least this rank. Subject: the rank, as digits.</summary>
    public const string Rank = "rank";

    /// <summary>The spellbook holds at least this many spells. Subject: the count, as digits.</summary>
    public const string Learned = "learned";

    /// <summary>A creature killed by its own bolt, struck back. Subject: its kind.</summary>
    public const string Returned = "returned";

    /// <summary>
    /// The depth bands a <see cref="Reach"/> names, deepest last. A player at a depth has reached
    /// every band above it too, so a fall straight to the floor still earns the steps on the way.
    /// </summary>
    public static readonly (string Name, int Below)[] Depths =
    [
        ("underground", 0),
        ("hollows", TerrainGenerator.HollowsTop),
        ("emberdeep", TerrainGenerator.EmberdeepTop),
        ("core", TerrainGenerator.WorldBottom + 36),
    ];

    /// <summary>Every depth band the player is inside at this height.</summary>
    public static IEnumerable<string> DepthsAt(float y)
    {
        foreach (var (name, below) in Depths)
            if (y < below) yield return name;
    }
}

/// <summary>
/// The achievement tree: the reference's advancement layout, with criteria for this game.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Shaped like the reference's, judged by ours.</b> The user's ask: "we've got a great
/// reference for those, we'll just change what it takes to get them". So the tabs, the frames and
/// the familiar names are there, and what earns each is what this world actually has — stormglass
/// before diamond, the Emberdeep instead of a portal, Driftsteads instead of villages, and magic.</para>
/// <para>⚠ <b>The "one of every" rows are built from the registries, not listed.</b> Monsters
/// Hunted reads the hostiles out of <see cref="CreatureSet"/>, A Balanced Diet reads every food out
/// of the items, Two by Two reads <see cref="Breeding"/>. A creature added next month joins the
/// list without anybody remembering to add it here — which is what the reference does too.</para>
/// </remarks>
public sealed class AchievementBook
{
    private readonly Dictionary<string, Achievement> _byId = new(StringComparer.Ordinal);

    public IReadOnlyList<Achievement> All { get; }

    public AchievementBook(ItemRegistry items)
    {
        All = Build(items);
        foreach (var achievement in All) _byId[achievement.Id] = achievement;
    }

    public Achievement? ById(string id) => _byId.GetValueOrDefault(id);

    public IEnumerable<Achievement> On(AchievementTab tab) => All.Where(one => one.Tab == tab);

    /// <summary>Tabs that have anything on them, in order.</summary>
    public IEnumerable<AchievementTab> Tabs =>
        Enum.GetValues<AchievementTab>().Where(tab => All.Any(one => one.Tab == tab));

    public static string TabName(AchievementTab tab) => tab switch
    {
        AchievementTab.Driftwood => "Driftwood",
        AchievementTab.Adventure => "Adventure",
        AchievementTab.Husbandry => "Husbandry",
        AchievementTab.Emberdeep => "Emberdeep",
        _ => tab.ToString(),
    };

    /// <summary>What a toast says over the achievement's name, by its frame.</summary>
    public static string Heading(AchievementFrame frame) => frame switch
    {
        AchievementFrame.Goal => "goal reached!",
        AchievementFrame.Challenge => "challenge complete!",
        _ => "achievement get!",
    };

    private static AchievementCriterion C(string @event, string subject) => new(@event, subject);

    private static AchievementCriterion[] Any(string @event, params string[] subjects) =>
        [.. subjects.Select(subject => C(@event, subject))];

    private static List<Achievement> Build(ItemRegistry items)
    {
        const string O = AchievementEvents.Obtain;
        const string W = AchievementEvents.Wear;
        var story = AchievementTab.Driftwood;
        var adventure = AchievementTab.Adventure;
        var husbandry = AchievementTab.Husbandry;
        var deep = AchievementTab.Emberdeep;
        var task = AchievementFrame.Task;
        var goal = AchievementFrame.Goal;
        var challenge = AchievementFrame.Challenge;

        var hostiles = CreatureSet.All
            .Where(kind => kind.Family == CreatureFamily.Hostile)
            .Select(kind => kind.Name).ToArray();
        var foods = items.All.Skip(1).Where(item => item.IsFood).Select(item => item.Name).ToArray();
        var breeders = Breeding.Fed.Order(StringComparer.Ordinal).ToArray();
        var biomes = Enum.GetNames<Biome>();

        return
        [
            // ── The story ──
            new("driftwood", story, "", "Driftwood", "Break a log with your bare hands and keep it",
                "driftoak_log", task, Any(O, "driftoak_log", "cherry_log")),
            new("bench_work", story, "driftwood", "Bench Work", "Make a bench for the full crafting grid",
                "bench", task, Any(O, "bench")),
            new("stone_age", story, "bench_work", "Stone Age", "Mine rubble with your new pickaxe",
                "rubble", task, Any(O, "rubble")),
            new("getting_an_upgrade", story, "stone_age", "Getting an Upgrade", "Make a better pickaxe",
                "stone_pickaxe", task, Any(O, "stone_pickaxe")),
            new("hot_topic", story, "stone_age", "Hot Topic", "Make a furnace",
                "furnace", task, Any(O, "furnace")),
            new("copper_age", story, "hot_topic", "Copper Age", "Smelt a copper ingot",
                "copper_ingot", task, Any(O, "copper_ingot")),
            new("acquire_hardware", story, "getting_an_upgrade", "Acquire Hardware", "Smelt an iron ingot",
                "iron_ingot", task, Any(O, "iron_ingot")),
            new("suit_up", story, "acquire_hardware", "Suit Up", "Protect yourself with a piece of iron armour",
                "iron_chestplate", task, Any(W, "iron_helmet", "iron_chestplate", "iron_leggings", "iron_boots")),
            new("hot_stuff", story, "acquire_hardware", "Hot Stuff", "Fill a bucket with lava",
                "lava_bucket", task, Any(O, "lava_bucket")),
            new("iron_pick", story, "acquire_hardware", "Isn't It Iron Pick", "Upgrade your pickaxe",
                "iron_pickaxe", task, Any(O, "iron_pickaxe")),
            new("into_the_deep", story, "getting_an_upgrade", "Into the Deep",
                "Go below the hollows' roof, where the deepstone starts",
                "glowcap", task, Any(AchievementEvents.Reach, "hollows")),
            new("storm_caught", story, "iron_pick", "Storm Caught", "Mine a seam of stormglass",
                "stormglass", task, Any(O, "stormglass")),
            new("diamonds", story, "storm_caught", "Diamonds!", "Bring up a diamond with a stormglass pickaxe",
                "diamond", task, Any(O, "diamond")),
            new("cover_me", story, "diamonds", "Cover Me with Diamonds", "Wear diamond armour from head to foot",
                "diamond_chestplate", goal,
                Any(W, "diamond_helmet", "diamond_chestplate", "diamond_leggings", "diamond_boots"), All: true),
            new("deeper", story, "diamonds", "We Need to Go Deeper", "Reach the Emberdeep",
                "emberstone", goal, Any(AchievementEvents.Reach, "emberdeep")),

            // ── The Emberdeep ──
            new("emberdeep", deep, "", "Emberdeep", "Below the hollows, where the rock runs molten",
                "emberstone", task, Any(AchievementEvents.Reach, "emberdeep")),
            new("bottom_of_the_world", deep, "emberdeep", "Bottom of the World",
                "Stand over the molten sea at the floor of the world",
                "lava_bucket", goal, Any(AchievementEvents.Reach, "core")),
            new("terrible_fortress", deep, "emberdeep", "A Terrible Fortress", "Find your way into an Emberhold",
                "ember_bricks", task, Any(AchievementEvents.Discover, "Emberhold")),
            new("ashcap_grower", deep, "terrible_fortress", "Ashes to Ashes", "Plant an ashcap in ashsand",
                "ashcap", task, Any(AchievementEvents.Plant, "ashcap")),
            new("into_fire", deep, "terrible_fortress", "Into Fire", "Take a cinder rod from a cinderwisp",
                "cinder_rod", task, Any(O, "cinder_rod")),
            new("return_to_sender", deep, "emberdeep", "Return to Sender",
                "Bring down an ashwail with its own bolt, struck back",
                "fire_charge", challenge, Any(AchievementEvents.Returned, "ashwail")),
            new("tear_stained", deep, "emberdeep", "Tear-Stained", "Collect the tear an ashwail cries",
                "wail_tear", task, Any(O, "wail_tear")),
            new("spooky", deep, "emberdeep", "Charred Remains", "Take a charred skeleton's skull",
                "charred_skull", goal, Any(O, "charred_skull")),
            new("melted", deep, "emberdeep", "Hot to the Touch", "Make ember cream",
                "ember_cream", task, Any(O, "ember_cream")),

            // ── Adventure ──
            new("adventure", adventure, "", "Adventure", "Adventure, exploration and combat",
                "relic_chart", task, [C(AchievementEvents.Kill, "*"), C(AchievementEvents.Discover, "*")]),
            new("monster_hunter", adventure, "adventure", "Monster Hunter", "Defeat any monster",
                "iron_sword", task, Any(AchievementEvents.Kill, hostiles)),
            new("monsters_hunted", adventure, "monster_hunter", "Monsters Hunted", "Defeat one of every monster",
                "diamond_sword", challenge, Any(AchievementEvents.Kill, hostiles), All: true),
            new("sweet_dreams", adventure, "adventure", "Sweet Dreams", "Sleep in a bed to change where you wake",
                "bed", task, Any(AchievementEvents.Sleep, "*")),
            new("adventuring_time", adventure, "sweet_dreams", "Adventuring Time", "Set foot in every biome",
                "leather_boots", challenge, Any(AchievementEvents.Enter, biomes), All: true),
            new("what_a_deal", adventure, "adventure", "What a Deal!", "Trade with a resident of a Driftstead",
                "trade_token", task, Any(AchievementEvents.Trade, "*")),
            new("off_the_map", adventure, "adventure", "Off the Map", "Find a place the world was hiding",
                "relic_chart", task, Any(AchievementEvents.Discover, "*")),
            new("careful_work", adventure, "off_the_map", "Careful Work", "Brush a relic shard out of a buried gallery",
                "brush", task, Any(O, "relic_shard")),
            new("storm_breaker", adventure, "off_the_map", "Storm Breaker", "Clear a storm vault",
                "trial_key", goal, Any(AchievementEvents.Clear, "Trial")),
            new("starfall", adventure, "storm_breaker", "Starfall", "Clear the Starfall Crown",
                "starheart", challenge, Any(AchievementEvents.Clear, "Crown")),
            new("on_rails", adventure, "adventure", "On Rails", "Ride a cart",
                "cart", task, Any(AchievementEvents.Ride, "*")),
            new("first_words", adventure, "adventure", "First Words", "Cast a spell",
                "book", task, Any(AchievementEvents.Cast, "*")),
            new("not_alone", adventure, "first_words", "Not Alone", "Call up a companion to fight beside you",
                "bone", task, Any(AchievementEvents.Cast, "summon_bones", "animate_zombie", "spirit_wolf", "earth_elemental")),
            new("well_read", adventure, "first_words", "Well Read", "Hold five spells in your spellbook",
                "bookshelf", task, Any(AchievementEvents.Learned, "5")),
            new("rank_two", adventure, "well_read", "Rising Power", "Reach Rank II",
                "gold_ingot", task, Any(AchievementEvents.Rank, "2")),
            new("rank_four", adventure, "rank_two", "Mastery", "Reach Rank IV",
                "starheart", goal, Any(AchievementEvents.Rank, "4")),
            new("the_long_road", adventure, "rank_four", "The Long Road", "Reach level twenty",
                "diamond", challenge, Any(AchievementEvents.Level, "20")),

            // ── Husbandry ──
            new("husbandry", husbandry, "", "Husbandry", "The world is full of friends and food",
                "wheat", task, Any(AchievementEvents.Eat, "*")),
            new("seedy_place", husbandry, "husbandry", "A Seedy Place", "Plant a seed and watch it grow",
                "seeds", task, Any(AchievementEvents.Plant, "*")),
            new("daily_bread", husbandry, "seedy_place", "Daily Bread", "Bake bread from your own wheat",
                "bread", task, Any(O, "bread")),
            new("green_thumb", husbandry, "seedy_place", "Green Thumb", "Hurry something along with bonemeal",
                "bonemeal", task, Any(AchievementEvents.Grow, "*")),
            new("two_of_a_kind", husbandry, "husbandry", "Two of a Kind", "Breed two animals together",
                "wheat", task, Any(AchievementEvents.Breed, "*")),
            new("two_by_two", husbandry, "two_of_a_kind", "Two by Two", "Breed every animal that will breed",
                "egg", goal, Any(AchievementEvents.Breed, breeders), All: true),
            new("shear_luck", husbandry, "husbandry", "Shear Luck", "Shear a sheep",
                "shears", task, Any(AchievementEvents.Shear, "sheep")),
            new("fishy_business", husbandry, "husbandry", "Fishy Business", "Catch a fish",
                "fishing_rod", task, Any(O, "raw_cod", "raw_salmon")),
            new("balanced_diet", husbandry, "husbandry", "A Balanced Diet", "Eat everything that can be eaten",
                "apple", challenge, Any(AchievementEvents.Eat, foods), All: true),
        ];
    }

    /// <summary>
    /// Where each achievement sits on its tab: a column for its depth in the tree, a row worked out
    /// so no two overlap. Children run to the right of their parent, as the reference lays them.
    /// </summary>
    /// <remarks>
    /// A leaf takes the next free row; a parent sits level with the middle of its children. That is
    /// the tidy-tree rule in its simplest form, and with trees this size it never needs more.
    /// </remarks>
    public Dictionary<string, (int Column, float Row)> Layout(AchievementTab tab)
    {
        var placed = new Dictionary<string, (int, float)>(StringComparer.Ordinal);
        var children = On(tab).ToLookup(one => one.Parent, StringComparer.Ordinal);
        var next = 0f;

        float Place(Achievement node, int column)
        {
            var kids = children[node.Id].ToList();
            float row;
            if (kids.Count == 0) row = next++;
            else
            {
                var rows = kids.Select(kid => Place(kid, column + 1)).ToList();
                row = (rows[0] + rows[^1]) * 0.5f;
            }

            placed[node.Id] = (column, row);
            return row;
        }

        foreach (var root in children[""]) Place(root, 0);
        return placed;
    }

    /// <summary>
    /// The tree's own checks: every name it mentions exists, every parent is on the same tab, and
    /// each tab has exactly one root.
    /// </summary>
    public List<string> Validate(ItemRegistry items)
    {
        var faults = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var kinds = CreatureSet.All.Select(kind => kind.Name).ToHashSet(StringComparer.Ordinal);
        var biomes = Enum.GetNames<Biome>().ToHashSet(StringComparer.Ordinal);
        var depths = AchievementEvents.Depths.Select(depth => depth.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var one in All)
        {
            if (!ids.Add(one.Id)) faults.Add($"achievement {one.Id} is listed twice");
            if (!items.TryByName(one.Icon, out _)) faults.Add($"achievement {one.Id} wears an icon that is no item: {one.Icon}");
            if (one.Criteria.Length == 0) faults.Add($"achievement {one.Id} can never be earned");

            if (one.Parent.Length > 0)
            {
                if (ById(one.Parent) is not { } parent) faults.Add($"achievement {one.Id} hangs off a missing {one.Parent}");
                else if (parent.Tab != one.Tab) faults.Add($"achievement {one.Id} hangs off {one.Parent} on another tab");
            }

            foreach (var criterion in one.Criteria)
            {
                if (criterion.Subject == "*") continue;
                var known = criterion.Event switch
                {
                    AchievementEvents.Obtain or AchievementEvents.Wear or AchievementEvents.Eat =>
                        items.TryByName(criterion.Subject, out _),
                    AchievementEvents.Kill or AchievementEvents.Breed or AchievementEvents.Shear =>
                        kinds.Contains(criterion.Subject),
                    AchievementEvents.Enter => biomes.Contains(criterion.Subject),
                    AchievementEvents.Reach => depths.Contains(criterion.Subject),
                    AchievementEvents.Cast => Magic.SpellCatalogue.TryByStableName(criterion.Subject, out _),
                    _ => true,
                };
                if (!known) faults.Add($"achievement {one.Id} waits on {criterion.Key}, which nothing can be");
            }
        }

        foreach (var tab in Tabs)
        {
            var roots = On(tab).Count(one => one.Parent.Length == 0);
            if (roots != 1) faults.Add($"the {TabName(tab)} tab has {roots} roots rather than one");
            if (Layout(tab).Count != On(tab).Count()) faults.Add($"the {TabName(tab)} tab has a loop or an orphan");
        }

        return faults;
    }
}

/// <summary>
/// What one player has earned in one world, and how far along the many-part ones are.
/// </summary>
/// <remarks>
/// ⚠ <b>Once per world, saved with it</b> — the same rule <see cref="RecipeUnlocks"/> lives by,
/// for the same reason: a new world is a new journey, and a reloaded one is not.
/// </remarks>
public sealed class AchievementLog
{
    private readonly List<string> _earned = [];
    private readonly HashSet<string> _earnedSet = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _met = new(StringComparer.Ordinal);

    /// <summary>Earned ids, oldest first.</summary>
    public IReadOnlyList<string> Earned => _earned;

    public bool Has(string id) => _earnedSet.Contains(id);

    public bool Dirty { get; private set; }

    public void Settled() => Dirty = false;

    /// <summary>How many of an achievement's criteria are met.</summary>
    public int MetCount(Achievement achievement) =>
        Has(achievement.Id) ? achievement.Criteria.Length
            : _met.TryGetValue(achievement.Id, out var met) ? met.Count : 0;

    /// <summary>Whether one criterion of an achievement has been met.</summary>
    public bool IsMet(Achievement achievement, AchievementCriterion criterion) =>
        Has(achievement.Id) || (_met.TryGetValue(achievement.Id, out var met) && met.Contains(criterion.Key));

    /// <summary>
    /// Says something happened, and adds to <paramref name="justEarned"/> whatever it finished.
    /// </summary>
    /// <returns>True when anything changed, earned or part-met.</returns>
    public bool Record(AchievementBook book, string @event, string subject, List<Achievement> justEarned)
    {
        var changed = false;

        foreach (var achievement in book.All)
        {
            if (Has(achievement.Id)) continue;

            foreach (var criterion in achievement.Criteria)
            {
                if (!criterion.Matches(@event, subject)) continue;

                if (!achievement.All)
                {
                    Earn(achievement, justEarned);
                    changed = true;
                    break;
                }

                if (!_met.TryGetValue(achievement.Id, out var met))
                    _met[achievement.Id] = met = new HashSet<string>(StringComparer.Ordinal);
                if (!met.Add(criterion.Key)) continue;

                changed = true;
                Dirty = true;
                if (met.Count >= achievement.Criteria.Length) Earn(achievement, justEarned);
            }
        }

        return changed;
    }

    private void Earn(Achievement achievement, List<Achievement> justEarned)
    {
        if (!_earnedSet.Add(achievement.Id)) return;
        _earned.Add(achievement.Id);
        _met.Remove(achievement.Id);
        justEarned.Add(achievement);
        Dirty = true;
    }

    /// <summary>Forgets everything, as resetting progress does.</summary>
    public void Forget()
    {
        if (_earned.Count == 0 && _met.Count == 0) return;
        _earned.Clear();
        _earnedSet.Clear();
        _met.Clear();
        Dirty = true;
    }

    public void Write(BinaryWriter into)
    {
        into.Write(1); // payload version
        into.Write(_earned.Count);
        foreach (var id in _earned) into.Write(id);

        into.Write(_met.Count);
        foreach (var (id, met) in _met.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            into.Write(id);
            into.Write(met.Count);
            foreach (var key in met.Order(StringComparer.Ordinal)) into.Write(key);
        }
    }

    /// <summary>Reads what <see cref="Write"/> wrote; an unknown id is kept, so an older build loses nothing.</summary>
    public string? Read(BinaryReader from)
    {
        _earned.Clear();
        _earnedSet.Clear();
        _met.Clear();

        var version = from.ReadInt32();
        if (version != 1) return $"this world's achievements use unknown version {version}";

        var earned = from.ReadInt32();
        if (earned is < 0 or > 65_536) return $"this world says it has {earned} achievements";
        for (var i = 0; i < earned; i++)
        {
            var id = from.ReadString();
            if (_earnedSet.Add(id)) _earned.Add(id);
        }

        var partial = from.ReadInt32();
        if (partial is < 0 or > 65_536) return $"this world says {partial} achievements are part-way";
        for (var i = 0; i < partial; i++)
        {
            var id = from.ReadString();
            var count = from.ReadInt32();
            if (count is < 0 or > 65_536) return $"achievement {id} says {count} parts are met";
            var met = new HashSet<string>(StringComparer.Ordinal);
            for (var j = 0; j < count; j++) met.Add(from.ReadString());
            _met[id] = met;
        }

        Dirty = false;
        return null;
    }
}
