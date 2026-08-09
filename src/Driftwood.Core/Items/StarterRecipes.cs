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
            string? material = null, bool mirrored = true,
            CraftStation station = CraftStation.Hand)
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
                Station = station,
            });
        }

        /// <summary>One block in, one worked form out. What a stonecutter does.</summary>
        void Cut(string name, string from, string result, int count = 1) =>
            Shaped(name, result, count, ["M"], from, station: CraftStation.Stonecutter);

        void Loose(string name, string result, int count, params string[] parts) =>
            LooseAt(name, result, count, CraftStation.Hand, parts);

        void LooseAt(
            string name, string result, int count, CraftStation station, params string[] parts)
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
                Station = station,
            });
        }

        void Smelt(string input, string result, int count = 1, SmeltWork work = SmeltWork.Other) =>
            book.Add(new SmeltRecipe
            {
                Name = $"smelt {input}",
                Input = Of(input),
                Result = items.Stack(result, count),
                Work = work,
            });

        // The first two moves of the game. A log opens into four planks with nothing but hands, and
        // two planks make the sticks everything else is hafted on.
        Loose("planks from a log", "driftoak_planks", 4, "#logs");
        Shaped("sticks", "stick", 4, ["P", "P"]);

        // The bench, which is what three-wide costs. Everything below this line that needs one says
        // so by being bigger than a player's two hands, not by a flag.
        Shaped("bench", "bench", 1, ["PP", "PP"]);
        Shaped("furnace", "furnace", 1, ["RRR", "R R", "RRR"]);

        // The same gesture as the furnace in a different material, which is what makes it legible
        // without being told: eight of something round a hole is a box made of that something.
        Shaped("chest", "chest", 1, ["PPP", "P P", "PPP"]);

        // A blade over a stone bed. Iron is the gate on every worked stone in the game, which is
        // the whole point: a decorative vocabulary should cost a trip underground, not nothing.
        Shaped("stonecutter", "stonecutter", 1, [" I ", "RRR"]);

        // ⛳ A furnace clad in iron over a bed of smooth stone. It costs five ingots and a furnace
        // you already built, which puts it a full trip underground past the furnace — and what it
        // gives back is that every ingot after it comes twice as fast. The smooth stone is what
        // makes it a second, deliberate visit to the fire rather than a thing found in a wall.
        Shaped("blast furnace", "blast_furnace", 1, ["III", "IFI", "MMM"], "smooth_stone");

        // ⛳ A furnace boxed in timber. It costs four logs and the furnace you already built, which
        // puts it a long way below the blast furnace on purpose — one is a trip underground and the
        // other is a walk to a tree, and what separates them is which half of the game they serve.
        Shaped("smoker", "smoker", 1, [" W ", "WFW", " W "]);

        // ⛳ A barrel: six planks round a hole with a lid of slabs. It holds what a chest holds and
        // opens upward, so it is the container for a cellar with a low ceiling — and it is cheaper
        // than a chest because being able to reach it is a condition rather than a given.
        Shaped("barrel", "barrel", 1, ["PSP", "P P", "PSP"]);

        // ⛳ A composter: an open-topped bin of planks. Scraps and spare seeds go in, bone meal
        // comes out, which gives a farm the daylight route to the thing skeletons otherwise
        // guard — feeding a field with the field's own leavings.
        Shaped("composter", "composter", 1, ["P P", "P P", "PPP"]);

        // ⛳ Paper is PULPED WOOD, which is both the real answer and the one that uses a material
        // every player has by their first afternoon. The reference presses it out of a swamp reed;
        // we have no reed and are not adding a plant to the world to serve a single recipe.
        //
        // ⚠ Three from three, not one from three. A spell page, a book, a map and a sign all want
        // paper, and one-for-three would make the first book cost most of a tree.
        //
        // ⛔ STACKED, NOT IN A ROW, AND THE DUPLICATE-SIGNATURE CHECK IS WHY. Three planks laid in a
        // row is already the slab, and this went in shapeless first — which is worse than colliding
        // with one recipe, because a shapeless plank recipe matches EVERY arrangement of that many
        // planks: two of them is the stick, four is the bench. Planks are the most reused ingredient
        // in the game, so anything made of nothing but planks has to name its shape. A stack of
        // sheets is at least the right picture for a ream.
        Shaped("paper", "paper", 3, ["P", "P", "P"], mirrored: false);

        // ⛳ THE ANVIL, and the count is the user's own: three blocks of iron is twenty-seven ingots
        // and the three across the waist are three, which is thirty. It is the most expensive single
        // thing in the game by a wide margin, and it should be — it is what stops every tool being
        // disposable.
        Shaped("anvil", "anvil", 1, ["NNN", " I ", "III"]);

        // ⛳ A hoe: a blade over a haft. Two iron rather than a ladder of seven — a hoe turns ground
        // over and turning it over faster is not a thing anybody wants.
        Shaped("hoe", "hoe", 1, ["II", " S", " S"]);

        // ⛳ Bread, which is what a field is FOR. Three wheat in a row, the genre's own, and the one
        // recipe in the game whose ingredient has to be grown rather than found or killed.
        Shaped("bread", "bread", 1, ["MMM"], "wheat", station: CraftStation.Bench);

        // ⛔ BONE MEAL REPLACES BONE-TO-DYE RATHER THAN SITTING BESIDE IT, and the duplicate-signature
        // check is what settled that. Both were one bone loose at a bench, which is one arrangement
        // with two answers — a fault by construction on a grid station. So the chain is the
        // reference's own and it is better: a bone grinds to meal, and the meal is BOTH the white dye
        // and the thing that makes a crop jump a stage. One grind, two uses, no ambiguity.
        LooseAt("bone meal", "bonemeal", 3, CraftStation.Bench, "bone");

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

        // Things that open. A ladder is rungs between two rails and comes out of a bench three at a
        // time; a door is six planks two wide, which is the shape of a door; a trapdoor is the same
        // arrangement lying down. All three are the genre's own grammar and none of them is a
        // transcription — the counts are ours and a door here is one door rather than three.
        Shaped("ladder", "ladder", 3, ["S S", "SSS", "S S"]);
        Shaped("door", "door", 1, ["PP", "PP", "PP"], mirrored: false);
        Shaped("trapdoor", "trapdoor", 2, ["PPP", "PPP"], mirrored: false);

        // ── The signal kit (#27), and the first thing copper is FOR. ─────────────────────────────
        // Wire is drawn from the ingot; the hands are a stick on stone and a worked stone; the
        // tidelamp is the stormglass lamp taught to listen. Each gate is its two inputs over a
        // stone base with its own heart between them: nothing for AND, copper for OR, copper
        // between the inputs for XOR, one input alone for NOT, and iron — the thing that holds —
        // for the latch that remembers.
        LooseAt("tidewire", "tidewire", 4, CraftStation.Bench, "copper_ingot");
        Shaped("lever", "lever", 1, ["S", "R"], station: CraftStation.Bench);

        // The button is one stone worked into a form, which is a stonecutter's sentence — and a
        // one-stone bench recipe would collide with every worked stone the cutter already offers.
        Cut("button", "stone", "button");
        Shaped("pressure plate", "pressure_plate", 1, ["OO"], station: CraftStation.Bench);
        LooseAt("tidelamp", "tidelamp", 1, CraftStation.Bench,
            "stormglass_lamp", "tidewire", "tidewire");
        Shaped("and gate", "gate_and", 1, ["D D", "OOO"], station: CraftStation.Bench);
        Shaped("or gate", "gate_or", 1, ["DUD", "OOO"], station: CraftStation.Bench);
        Shaped("xor gate", "gate_xor", 1, ["D D", "OUO"], station: CraftStation.Bench);
        Shaped("not gate", "gate_not", 1, [" D ", "OOO"], station: CraftStation.Bench);
        Shaped("latch gate", "gate_latch", 1, ["D D", "OIO"], station: CraftStation.Bench);

        // ── The track (#28). Iron rails on a stick of ties; the booster is gold with a wire's
        // heart; the cart is a tub of iron. The genre's own grammar at our own counts.
        Shaped("rail", "rail", 16, ["I I", "ISI", "I I"], mirrored: false, station: CraftStation.Bench);
        Shaped("powered rail", "powered_rail", 6, ["Y Y", "YSY", "YDY"],
            mirrored: false, station: CraftStation.Bench);
        Shaped("cart", "cart", 1, ["I I", "III"], mirrored: false, station: CraftStation.Bench);

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

        // ⛳ WORKING A ROCK IS DONE AT A STONECUTTER, and that is a deliberate change. These were
        // four-in-a-square in bare hands, which put seven of the fourteen hand recipes in the game
        // into the pockets of somebody who had not built a single thing — a whole decorative
        // vocabulary before the first bench. A rock worked into another rock is what a stonecutter
        // is for, and gating it there is what makes building one worth doing.
        //
        // One in, one out, because a saw does not multiply stone. The bench's four-for-four is gone
        // with the hand recipe it belonged to.
        foreach (var (from, into) in CutFrom)
            Cut($"{into.Replace('_', ' ')}", from, into);

        Cut("chiseled sandstone", "cut_sandstone", "chiseled_sandstone");

        // And every stone shape, straight off the block rather than three-across at a bench. This is
        // what gives the station a list to choose from: one rock in and the slab, the stair and the
        // worked form are all offered together, which is the whole gesture of a stonecutter.
        foreach (var (material, from) in ShapedFrom)
        {
            if (material == "driftoak") continue;      // timber is sawn at a bench, not on stone

            Cut($"cut {material} slab", from, $"{material}_slab", 2);
            Cut($"cut {material} stairs", from, $"{material}_stairs", 1);
        }

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

        // ═══ THE DYE TREE ═══════════════════════════════════════════════════════════════════════
        //
        // ⛳ Seven the world gives and nine it mixes, which is what makes sixteen colours a tree
        // rather than sixteen errands. Every primary is something a player already walks past:
        // two flowers, a mineral, a mineral, a rock, a weed and the block under a torch.
        //
        // ⚠ The mixes are OURS. The reference's brown comes off a jungle tree we do not have, so
        // ours is red and green — which is what brown is on a palette, and is the sort of decision
        // that has to be written down because nothing about it is derivable.
        LooseAt("white dye", "dye_white", 1, CraftStation.Bench, "marshlily");
        LooseAt("light blue dye", "dye_light_blue", 1, CraftStation.Bench, "seaflax");
        LooseAt("red dye", "dye_red", 1, CraftStation.Bench, "emberbloom");
        LooseAt("yellow dye", "dye_yellow", 1, CraftStation.Bench, "sunwort");
        LooseAt("blue dye", "dye_blue", 2, CraftStation.Bench, "azurite");
        LooseAt("black dye", "dye_black", 1, CraftStation.Bench, "#coals");
        LooseAt("black dye from ink", "dye_black", 2, CraftStation.Bench, "ink_sac");

        // ⛳ A second white, ground out of a bone. Two sources for one colour is not redundancy: a
        // marshlily is a summer afternoon and a bone is a night that went badly, and white is the
        // colour half the palette is mixed with — so the branch a player can reach matters.
        LooseAt("white dye from bone meal", "dye_white", 1, CraftStation.Bench, "bonemeal");

        // ⛳ Green is MIXED rather than found, and that is a deliberate difference from the
        // reference — which smelts a cactus, in a desert we do not have. Blue and yellow is what
        // green is, so the tree stays a tree and the world does not have to grow a seventh source.
        LooseAt("green dye", "dye_green", 2, CraftStation.Bench, "dye_blue", "dye_yellow");

        LooseAt("orange dye", "dye_orange", 2, CraftStation.Bench, "dye_red", "dye_yellow");
        LooseAt("lime dye", "dye_lime", 2, CraftStation.Bench, "dye_green", "dye_white");
        LooseAt("pink dye", "dye_pink", 2, CraftStation.Bench, "dye_red", "dye_white");
        LooseAt("grey dye", "dye_grey", 2, CraftStation.Bench, "dye_black", "dye_white");
        LooseAt("light grey dye", "dye_light_grey", 2, CraftStation.Bench, "dye_grey", "dye_white");
        LooseAt("cyan dye", "dye_cyan", 2, CraftStation.Bench, "dye_blue", "dye_green");
        LooseAt("purple dye", "dye_purple", 2, CraftStation.Bench, "dye_blue", "dye_red");
        LooseAt("magenta dye", "dye_magenta", 2, CraftStation.Bench, "dye_purple", "dye_pink");
        LooseAt("brown dye", "dye_brown", 2, CraftStation.Bench, "dye_red", "dye_green");

        // ⛳ Every colour of wool from any other colour of wool, and every carpet from its own wool.
        // Thirty-two rows out of one loop — and the re-dye takes the #wool tag rather than white, so
        // a bad choice is a dye away from being fixed instead of a trip back to the sheep.
        // ⚠ All of it at a bench, and the gate is what said so. Every one of these would fit in a
        // player's two hands, and moving forty-one recipes there took the bare-hand set from six to
        // fifty-four — which the audit calls "most of a game before anything is built", and it is
        // right. Dyeing is an industry rather than a first move: the hand makes planks, sticks and
        // the bench, and the bench makes everything that is a choice about how a thing should look.
        foreach (var dye in StarterBlocks.Colours)
        {
            LooseAt(
                $"{dye.Name.Replace('_', ' ')} wool", $"wool_{dye.Name}", 1, CraftStation.Bench,
                "#wool", $"dye_{dye.Name}");

            Shaped(
                $"{dye.Name.Replace('_', ' ')} carpet", $"carpet_{dye.Name}", 3, ["MM"],
                $"wool_{dye.Name}", mirrored: false, station: CraftStation.Bench);

            // ⛳ And the same colour in glass, which is the second axis the dye tree pays for and the
            // cheapest thirty-two blocks in the game — sand is everywhere and the dyes already exist.
            // ⚠ ONE pane per pane, not the reference's eight-for-eight. Ours follows the WOOL rule
            // above rather than the reference's: a colour costs a dye, every time, so choosing wrong
            // costs one dye instead of eight. That is the same argument as re-dyeing off #wool.
            LooseAt(
                $"{dye.Name.Replace('_', ' ')} glass", $"stained_glass_{dye.Name}", 1,
                CraftStation.Bench, "glass", $"dye_{dye.Name}");

            Shaped(
                $"{dye.Name.Replace('_', ' ')} glass pane", $"stained_glass_pane_{dye.Name}", 16,
                ["MMM", "MMM"], $"stained_glass_{dye.Name}", station: CraftStation.Bench);
        }

        // ⛳ Four threads woven back into a fleece — the other way a player gets wool, and the reason
        // a cellar full of spiders is worth clearing rather than avoiding.
        Shaped("wool from string", "wool_white", 1, ["MM", "MM"], "string", station: CraftStation.Bench);

        // ⛳ Two blades on a pivot, drawn as a diagonal because that is what a pair of shears is.
        // ⚠ At a bench rather than in the hands, though it would fit in a player's two-by-two: every
        // other piece of metalwork in the game is made at one, and an iron tool that could be run up
        // on the walk back from the mine would be the one exception with nothing to justify it.
        Shaped("shears", "shears", 1, [" I", "I "], station: CraftStation.Bench);

        // ⛳ A pail: three plates beaten into a V, which is the shape the genre uses and the shape a
        // bucket actually is. It is the gate on every fluid a player moves, and it sits behind iron
        // deliberately — carrying water down a shaft should be something you earn, and it is a large
        // part of what a first trip underground is now FOR.
        Shaped("bucket", "bucket", 1, ["I I", " I "], station: CraftStation.Bench);

        // Nine into one and back again, the same storage gesture bricks and clay already use.
        Shaped("coal block", "coal_block", 1, ["CCC", "CCC", "CCC"], "coal", station: CraftStation.Bench);
        Loose("coal from a block", "coal", 9, "coal_block");

        // ⛳ The three metals, packed and unpacked. ⚠ BOTH DIRECTIONS, always: a player who tidies
        // their iron into blocks and then cannot spend it has been punished for being organised,
        // and the pack-away recipe is the one everybody finds first.
        foreach (var (name, label, ingot, _, _, _) in StarterBlocks.MetalBlocks)
        {
            Shaped(label, name, 1, ["MMM", "MMM", "MMM"], ingot, station: CraftStation.Bench);
            Loose($"{ingot} from a block", ingot, 9, name);
        }

        // ⛳ The slimeball packed away the same two directions (#97) — and the block is the point:
        // the first floor that returns a landing. A sneak absorbs the bounce, and a fall onto it
        // is never billed.
        Shaped("slime block", "slime_block", 1, ["MMM", "MMM", "MMM"], "slimeball",
            station: CraftStation.Bench);
        Loose("slimeballs from a block", "slimeball", 9, "slime_block");

        // Every tool, off the two tables. The material is a tag for the tiers that have more than
        // one source — any plank, any rough stone — and a plain item for the metals.
        foreach (var tier in StarterItems.Tiers)
        foreach (var (head, rows) in ToolPatterns)
            Shaped($"{tier.Name} {head}", $"{tier.Name}_{head}", 1, rows, tier.Material);

        // ⛳ And every piece of armour, off its own table, at a bench. Three of the four are three
        // wide and would need one anyway; the boots are not, and putting them at a bench with the
        // rest is the station gate doing what it was added for — a set of boots run up in the hands
        // on the walk home would be the one exception with nothing to justify it.
        //
        // ⛳ THE LEATHER SET IS THE POINT. It is the only armour reachable without a pickaxe, it
        // comes off an animal, and it is what finally consumes the leather that has been dropping
        // since the herd arrived.
        foreach (var material in Armour.Materials)
        foreach (var piece in Armour.Pieces)
        {
            Shaped(
                $"{material.Name} {piece.Name}", Armour.ItemName(material, piece), 1,
                piece.Rows, material.Made, mirrored: false, station: CraftStation.Bench);
        }

        // ⛳ A board round a metal boss, drawn as the shape of a shield. It is the only thing in the
        // game worth having in the other hand, which is what the other hand was waiting for — and
        // the facing is the ladder, so the pattern is one and the metal is the parameter.
        foreach (var shield in Armour.Shields)
        {
            Shaped(shield.Name.Replace('_', ' '), shield.Name, 1, ["PMP", "PPP", " P "],
                   shield.Made, mirrored: false, station: CraftStation.Bench);
        }

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
        // ⛳ The three that are ORE, which is what a blast furnace will take and the only thing it
        // will. Everything above is firing, baking or melting — jobs a specialised smelter has no
        // business doing, and the reason the two stations are worth having side by side.
        Smelt("raw_copper", "copper_ingot", work: SmeltWork.Ore);
        Smelt("raw_iron", "iron_ingot", work: SmeltWork.Ore);
        Smelt("raw_gold", "gold_ingot", work: SmeltWork.Ore);

        // ⛳ Cooking, which is the first thing the fire does that is not about rocks. Every meat
        // triples what it puts back once it has been over a flame, and that is the whole argument
        // for carrying it home rather than eating it where the animal fell.
        foreach (var meat in StarterItems.Meats)
            Smelt($"raw_{meat.Name}", $"cooked_{meat.Name}", work: SmeltWork.Food);

        // The rabbit's, standing outside the meat table for the layer-numbering reason its items
        // do. And its hide: four in a square make a leather, so a warren is a slow tannery and
        // the first armour no longer strictly needs a cow.
        Smelt("raw_rabbit", "cooked_rabbit", work: SmeltWork.Food);
        Shaped("leather from hides", "leather", 1, ["MM", "MM"], "rabbit_hide");

        // ⛳ And the one crop worth cooking, which finally gives the smoker something to do that does
        // not involve an animal — it has been a station for meat alone since the day it landed.
        Smelt("potato", "baked_potato", work: SmeltWork.Food);

        // ⛳ Either mushroom roasts to the same meal — the cave's answer to the baked potato, and
        // the first thing a player can cook before owning a farm, since the ingredient is found on
        // the way down rather than grown.
        Smelt("mushroom_brown", "roasted_mushroom", work: SmeltWork.Food);
        Smelt("mushroom_red", "roasted_mushroom", work: SmeltWork.Food);

        // The egg's one consumer (#97): the hen keeps laying whether or not anybody wants an
        // omelette, so the surplus needed somewhere honest to go.
        Smelt("egg", "fried_egg", work: SmeltWork.Food);

        // The desert's dye: a cactus cooks down to green, the second door into a colour that
        // otherwise only comes out of mixing. The genre's own recipe, worth keeping real.
        Smelt("cactus", "dye_green");

        // And the deep's: a glowcap grinds to cyan at the bench.
        LooseAt("cyan dye from a glowcap", "dye_cyan", 1, CraftStation.Bench, "glowcap");

        // The wetland's door into paper — the planks recipe's own gesture in the reed's material,
        // beside M0's path and replacing nothing.
        Shaped("paper from reeds", "paper", 3, ["M", "M", "M"], "marsh_reed");

        // Grown, not cut: moss pressed against rubble is the first worked stone whose look came
        // off a cave floor, and the decor vocabulary's door into "old".
        LooseAt("mossy rubble", "mossy_rubble", 1, CraftStation.Hand, "moss", "rubble");

        // ⛳ A torch shut inside a carved pumpkin. The carve itself is the shears' act on the
        // standing block, not a recipe — so this is the one bench step in the pumpkin's whole run.
        LooseAt("jack o'lantern", "jack_o_lantern", 1, CraftStation.Bench, "carved_pumpkin", "torch");

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

            // ⛳ Every colour, so a dye recolours wool a player already has rather than only white.
            // A bad choice is then one dye away from being fixed instead of a trip back to the
            // sheep, and it is the whole reason the re-dye is a tag rather than sixteen rows naming
            // white — this is the tag doing the job the tag system was added for.
            ["#wool"] = Tag(
                "any wool",
                [.. StarterBlocks.Colours.Select(c => $"wool_{c.Name}")]),
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
        'F' => "furnace",
        'G' => "glass",
        'A' => "azurite",
        'Z' => "stormglass",
        'N' => "iron_block",
        'D' => "tidewire",
        'U' => "copper_ingot",
        'O' => "stone",
        'Y' => "gold_ingot",
        _ => throw new InvalidOperationException($"recipe '{recipe}' uses unknown key '{key}'"),
    };
}
