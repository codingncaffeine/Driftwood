using Driftwood.Core.Blocks;

namespace Driftwood.Core.Items;

/// <summary>
/// Driftwood's own recipe set.
/// </summary>
/// <remarks>
/// <para>The grammar is the genre's and the list is ours. Timber into planks into sticks, a bench
/// before anything three wide, a tier ladder where each rung digs up the next — those are how a
/// game of this kind teaches itself and nobody owns them. Which materials, in what numbers, under
/// what names is content, and every line below is written against our own block set rather than
/// transcribed.</para>
/// <para>Tools come off the same two tables the items did, so the whole ladder is one nested loop
/// rather than twenty-four recipes. A seventh tier would be a row in <see cref="StarterItems.Tiers"/>
/// and nothing here at all.</para>
/// </remarks>
public static class StarterRecipes
{
    /// <summary>The heads, as patterns over a material and a stick.</summary>
    /// <remarks>
    /// <c>M</c> the tier's material, <c>S</c> a stick, a space nothing. Written as pictures because
    /// that is what they are — a recipe laid out as three rows of characters can be checked against
    /// the screen by eye, and one written as a list of coordinates cannot.
    /// </remarks>
    private static readonly (string Head, string[] Rows)[] ToolPatterns =
    [
        ("pickaxe", ["MMM", " S ", " S "]),
        ("axe",     ["MM ", "MS ", " S "]),
        ("shovel",  ["M", "S", "S"]),
        ("sword",   ["M", "M", "S"]),
    ];

    /// <summary>What each shaped material is cut from.</summary>
    private static readonly (string Material, string From)[] ShapedFrom =
    [
        ("driftoak", "#planks"),
        ("stone", "stone"),
        ("rubble", "rubble"),
        ("bricks", "bricks"),
        ("sandstone", "sandstone"),
        ("stone_bricks", "stone_bricks"),
        ("smooth_stone", "smooth_stone"),
        ("polished_deepstone", "polished_deepstone"),
        ("deepstone_bricks", "deepstone_bricks"),
        ("polished_coralstone", "polished_coralstone"),
        ("polished_driftstone", "polished_driftstone"),
        ("polished_saltstone", "polished_saltstone"),
        ("cut_sandstone", "cut_sandstone"),
    ];

    /// <summary>
    /// Four of a rock laid in a square, worked into four of something else.
    /// </summary>
    /// <remarks>
    /// <para>The genre's own gesture and the reason a two-by-two in the hands matters: a whole
    /// building vocabulary opens without a bench, out of stone the player is already carrying. Four
    /// in and four out, so working a rock costs nothing but the doing of it — which is what makes
    /// it a decision about what a wall should look like rather than about whether it is affordable.
    /// </para>
    /// <para>Each row is also its own slab, stair and often wall through the tables above, so the
    /// nine rows here are most of the buildable set.</para>
    /// </remarks>
    private static readonly (string From, string Into)[] CutFrom =
    [
        ("stone", "stone_bricks"),
        ("deepstone", "polished_deepstone"),
        ("polished_deepstone", "deepstone_bricks"),
        ("coralstone", "polished_coralstone"),
        ("driftstone", "polished_driftstone"),
        ("saltstone", "polished_saltstone"),
        ("sandstone", "cut_sandstone"),
    ];

    /// <summary>The walls, and the rock each is stacked from. Six across two rows makes six.</summary>
    private static readonly (string Wall, string From)[] WallsFrom =
    [
        ("rubble_wall", "rubble"),
        ("stone_brick_wall", "stone_bricks"),
        ("deepstone_brick_wall", "deepstone_bricks"),
        ("sandstone_wall", "sandstone"),
        ("brick_wall", "bricks"),
    ];

    public static RecipeBook Build(ItemRegistry items)
    {
        var tags = Tags(items);
        var book = new RecipeBook(items);

        Ingredient Of(string name) => tags.TryGetValue(name, out var tag)
            ? tag
            : new Ingredient { Name = name, Members = [items.ByName(name).Id] };

        // One builder for every shaped recipe in the file. 'M' is whatever the caller is making the
        // thing out of, which is what lets the tool ladder and the shaped materials both be loops.
        //
        // Patterns are trimmed to what is actually in them before being stored. They are written as
        // rectangles because that is how they read on the page — an axe is a picture of a bit and a
        // haft, and squaring it off with a blank column is what makes the picture legible — but a
        // stored recipe has to be exactly its own bounding box, since that is what the matcher
        // compares a trimmed grid against. Authored one way, stored the other, and the conversion
        // written once here rather than remembered at every call site.
        void Shaped(
            string name, string result, int count, string[] rows,
            string? material = null, bool mirrored = true)
        {
            var wide = rows[0].Length;
            foreach (var row in rows)
                if (row.Length != wide)
                    throw new InvalidOperationException($"recipe '{name}' has ragged rows");

            int minX = wide, minY = rows.Length, maxX = -1, maxY = -1;
            for (var y = 0; y < rows.Length; y++)
            for (var x = 0; x < wide; x++)
            {
                if (rows[y][x] == ' ') continue;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            if (maxX < 0) throw new InvalidOperationException($"recipe '{name}' is empty");

            var width = maxX - minX + 1;
            var height = maxY - minY + 1;
            var cells = new Ingredient?[width * height];

            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var key = rows[minY + y][minX + x];
                cells[y * width + x] = key == ' ' ? null : Of(KeyOf(key, name, material));
            }

            book.Add(new Recipe
            {
                Name = name,
                Result = items.Stack(result, count),
                Width = width,
                Height = height,
                Cells = cells,
                Mirrored = mirrored,
            });
        }

        void Loose(string name, string result, int count, params string[] parts)
        {
            var cells = new Ingredient?[parts.Length];
            for (var i = 0; i < parts.Length; i++) cells[i] = Of(parts[i]);

            book.Add(new Recipe
            {
                Name = name,
                Result = items.Stack(result, count),
                Width = parts.Length,
                Height = 1,
                Cells = cells,
                Shapeless = true,
            });
        }

        void Smelt(string input, string result, int count = 1) => book.Add(new SmeltRecipe
        {
            Name = $"smelt {input}",
            Input = Of(input),
            Result = items.Stack(result, count),
        });

        // The first two moves of the game. A log opens into four planks with nothing but hands, and
        // two planks make the sticks everything else is hafted on.
        Loose("planks from a log", "driftoak_planks", 4, "#logs");
        Shaped("sticks", "stick", 4, ["P", "P"]);

        // The bench, which is what three-wide costs. Everything below this line that needs one says
        // so by being bigger than a player's two hands, not by a flag.
        Shaped("bench", "bench", 1, ["PP", "PP"]);
        Shaped("furnace", "furnace", 1, ["RRR", "R R", "RRR"]);

        // Light. Either coal will do — the one that comes out of the ground and the one that comes
        // out of a furnace are the same thing on the end of a stick.
        Shaped("torch", "torch", 4, ["C", "S"]);

        // The rest of it, and the reason a torch stops being the only answer. A lantern is iron
        // shut round a flame, so it is brighter, whiter and can be hung; a campfire is timber and
        // kindling with something to light it, laid out as a picture of the thing it makes; and
        // smokeglass is glass with the light taken out of it.
        Shaped("lantern", "lantern", 1, [" I ", "ITI", " I "]);
        Shaped("campfire", "campfire", 1, [" S ", "SCS", "WWW"]);
        Shaped("smokeglass", "smokeglass", 2, [" C ", "CGC", " C "]);
        Shaped("smokeglass pane", "smokeglass_pane", 16, ["MMM", "MMM"], "smokeglass");

        // ⚠ The first thing azurite has ever been for. Six ores come up out of the ground and this
        // was the one with no recipe anywhere in the tree — a mineral a player mines four of and
        // can do nothing whatever with, which is a hole rather than a decision. Set round a
        // stormglass it is the brightest and the coldest light there is.
        Shaped("stormglass lamp", "stormglass_lamp", 1, [" A ", "AZA", " A "]);

        // Building shapes, three across for six of the flat kind and a staircase for four steps.
        foreach (var (material, from) in ShapedFrom)
        {
            Shaped($"{material} slab", $"{material}_slab", 6, ["MMM"], from, mirrored: false);
            Shaped($"{material} stairs", $"{material}_stairs", 4, ["M  ", "MM ", "MMM"], from);
        }

        // Working a rock: four in a square, four out. In the hands, without a bench, because the
        // whole building vocabulary opening out of stone somebody is already carrying is the point
        // — and four for four means the choice is what a wall should look like, never whether it
        // can be afforded.
        foreach (var (from, into) in CutFrom)
            Shaped($"{into.Replace('_', ' ')}", into, 4, ["MM", "MM"], from);

        // The one that is not four for four, because it is not a cut but a carving: two worked
        // slabs stacked, which is the genre's own gesture for putting a face on something.
        Shaped("chiseled sandstone", "chiseled_sandstone", 1, ["M", "M"], "sandstone_slab");

        // Things that join up with what is beside them. A run of six across two rows is the genre's
        // own grammar for anything wall-shaped, and the count is what says how far it goes: six
        // rubble is six wall, six glass is sixteen panes because a pane is a sixteenth of a wall.
        Shaped("fence", "driftoak_fence", 3, ["MSM", "MSM"], "#planks");
        Shaped("pane", "glass_pane", 16, ["MMM", "MMM"], "glass");

        foreach (var (wall, from) in WallsFrom)
            Shaped($"{wall.Replace('_', ' ')}", wall, 6, ["MMM", "MMM"], from);

        // Packing four of something loose back into a block, which is how the genre's own storage
        // of a powder or a lump works and is what gives brick and clay somewhere to go.
        Shaped("brick block", "bricks", 1, ["BB", "BB"]);
        Shaped("clay block", "clay", 1, ["LL", "LL"]);

        // Every tool, off the two tables. The material is a tag for the tiers that have more than
        // one source — any plank, any rough stone — and a plain item for the metals.
        foreach (var tier in StarterItems.Tiers)
        foreach (var (head, rows) in ToolPatterns)
            Shaped($"{tier.Name} {head}", $"{tier.Name}_{head}", 1, rows, tier.Material);

        // The furnace's whole job. Rubble back into stone is the loop that makes one worth building
        // before there is any metal to melt, and charcoal is what keeps it burning where there is
        // no coal — a forest is fuel, which is the answer to spawning somewhere with no cave.
        Smelt("rubble", "stone");

        // Stone taken past stone. A furnace giving back something that is not the thing it was
        // given is the one place the smelter is a shaping tool rather than a refiner, and it is
        // what makes a second pass through the fire worth doing.
        Smelt("stone", "smooth_stone");
        Smelt("sandstone", "cut_sandstone");

        Smelt("sand", "glass");
        Smelt("clay_lump", "brick");
        Smelt("#logs", "charcoal");
        Smelt("raw_copper", "copper_ingot");
        Smelt("raw_iron", "iron_ingot");
        Smelt("raw_gold", "gold_ingot");

        return book;
    }

    /// <summary>
    /// The named sets recipes are written against.
    /// </summary>
    /// <remarks>
    /// This is the whole reason tags exist rather than recipes naming items. One entry here is every
    /// recipe that mentions it: the day a second wood species is registered, <c>#planks</c> gains a
    /// member and planks, sticks, the bench, every wooden shape and the whole first rung of the tool
    /// ladder accept it without a line changing.
    /// </remarks>
    private static Dictionary<string, Ingredient> Tags(ItemRegistry items)
    {
        Ingredient Tag(string name, params string[] members) => new()
        {
            Name = name,
            Members = [.. members.Select(m => items.ByName(m).Id)],
        };

        return new Dictionary<string, Ingredient>(StringComparer.Ordinal)
        {
            ["#logs"] = Tag("any log", "driftoak_log"),
            ["#planks"] = Tag("any plank", "driftoak_planks"),

            // Either fire-starter. They burn the same and light the same, and a torch recipe that
            // insisted on the mined one would be a trap for anyone who spawned above ground.
            ["#coals"] = Tag("coal or charcoal", "coal", "charcoal"),

            // Rough rock, which is what a tool bites. Smelted stone is deliberately not in it: it is
            // the smooth finished block, and a ladder where the refined material also makes the
            // second rung would let a player skip nothing and gain nothing.
            ["#rough_stone"] = Tag(
                "any rough stone", "rubble", "deepstone", "coralstone", "driftstone", "saltstone"),
        };
    }

    /// <summary>Which ingredient a letter in a pattern stands for.</summary>
    private static string KeyOf(char key, string recipe, string? material) => key switch
    {
        'M' => material ?? throw new InvalidOperationException($"recipe '{recipe}' uses M with no material"),
        'P' => "#planks",
        'S' => "stick",
        'R' => "#rough_stone",
        'C' => "#coals",
        'B' => "brick",
        'L' => "clay_lump",
        'W' => "#logs",
        'T' => "torch",
        'I' => "iron_ingot",
        'G' => "glass",
        'A' => "azurite",
        'Z' => "stormglass",
        _ => throw new InvalidOperationException($"recipe '{recipe}' uses unknown key '{key}'"),
    };
}
