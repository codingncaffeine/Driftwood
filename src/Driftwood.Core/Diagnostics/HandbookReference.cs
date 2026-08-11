using System.Text;
using System.Text.Json;
using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;
using Driftwood.Core.Exploration;
using Driftwood.Core.Items;
using Driftwood.Core.Magic;
using Driftwood.Core.Settings;

namespace Driftwood.Core.Diagnostics;

/// <summary>
/// Deterministic machine-readable receipt behind the player handbook. The prose explains play;
/// this export keeps stable registry names and authored numbers from being copied into a second
/// hand-maintained catalogue.
/// </summary>
public static class HandbookReference
{
    public const string PageName = "Live-Registry-Reference.md";
    public const string JsonName = "game-reference.json";

    public static IReadOnlyList<string> Write(string folder, string productVersion)
    {
        Directory.CreateDirectory(folder);
        var (blocks, items, blockDrops, creatureDrops, recipes) = Registries();
        var keys = Bindings.Defaults();
        var pad = ControllerBindings.Defaults();
        var settings = new GameSettings();

        var blockRows = blocks.All.Skip(1).OrderBy(one => one.Name, StringComparer.Ordinal).Select(block => new
        {
            name = block.Name,
            block.Solid,
            block.Opaque,
            block.Translucent,
            hardness = block.Hardness,
            harvestClass = block.HarvestClass.ToString(),
            harvestTier = block.HarvestTier,
            use = block.Use.ToString(),
            fluid = block.Fluid.ToString(),
            fluidLevel = block.FluidLevel,
            block.Waterlogged,
            block.Climbable,
            block.Snares,
            block.Bouncy,
            block.Hurts,
            tint = block.Tint.ToString(),
            soundMaterial = block.Sounds.ToString(),
            lightEmission = block.LightEmission,
            block.Crafted,
            block.Derived,
            drop = blockDrops.Describe(block.Id),
        }).ToArray();

        var itemRows = items.All.Skip(1).OrderBy(one => one.Name, StringComparer.Ordinal).Select(item => new
        {
            name = item.Name,
            label = item.Label,
            maxStack = item.MaxStack,
            places = item.Places is { Variants.Length: > 0 } ? blocks[item.PlainBlock].Name : null,
            placesEntity = item.PlacesEntity,
            use = item.Use.ToString(),
            tool = item.Tool.ToString(),
            tier = item.Tier,
            miningSpeed = item.MiningSpeed,
            attackDamage = item.AttackDamage,
            durability = item.Durability,
            burnSeconds = item.BurnSeconds,
            feeds = item.Feeds,
            equipSlot = item.Wears?.ToString(),
            armourPoints = item.ArmourPoints,
            shieldShare = item.ShieldShare,
        }).ToArray();

        var recipeRows = recipes.Recipes.OrderBy(one => one.Name, StringComparer.Ordinal).Select(recipe => new
        {
            name = recipe.Name,
            result = items[recipe.Result.Item].Name,
            resultCount = recipe.Result.Count,
            station = recipe.Station.ToString(),
            madeAt = recipe.MadeAt,
            recipe.Width,
            recipe.Height,
            recipe.Shapeless,
            recipe.Mirrored,
            cells = recipe.Cells.Select(cell => cell is null ? null : new
            {
                name = cell.Name,
                members = cell.Members.Select(member => items[member].Name).OrderBy(one => one, StringComparer.Ordinal),
            }).ToArray(),
        }).ToArray();

        var smeltRows = recipes.Smelting.OrderBy(one => one.Name, StringComparer.Ordinal).Select(recipe => new
        {
            name = recipe.Name,
            input = recipe.Input.Name,
            inputMembers = recipe.Input.Members.Select(member => items[member].Name)
                .OrderBy(one => one, StringComparer.Ordinal),
            result = items[recipe.Result.Item].Name,
            resultCount = recipe.Result.Count,
            seconds = recipe.Seconds,
            work = recipe.Work.ToString(),
        }).ToArray();

        var creatureRows = CreatureSet.All.OrderBy(one => one.Name, StringComparer.Ordinal).Select(creature => new
        {
            name = creature.Name,
            label = creature.Label,
            family = creature.Family.ToString(),
            movement = CreatureSet.MoveFor(creature.Name).ToString(),
            health = CreatureVitals.HealthFor(creature.Name),
            damage = CreatureVitals.DamageFor(creature.Name),
            burnsInDaylight = CreatureVitals.BurnsInDaylight(creature.Name),
            retaliates = CreatureVitals.Retaliates(creature.Name),
            timid = CreatureVitals.Timid(creature.Name),
            drops = creatureDrops.Rules.Where(rule => rule.Kind == creature.Name).Select(rule => new
            {
                trigger = rule.Trigger.ToString(),
                item = rule.Item,
                min = rule.Min,
                max = rule.Max,
                tool = rule.Tool.ToString(),
                chance = rule.Chance,
                needsFleece = rule.NeedsFleece,
            }),
        }).ToArray();

        var structures = Enum.GetValues<StructureKind>().Select(kind => new
        {
            name = kind.ToString(),
            grid = ExplorationGenerator.GridFor(kind),
            discoveryExperience = CharacterRewards.Discovery(kind).Experience,
            discoveryCoins = CharacterRewards.Discovery(kind).Coins,
        }).ToArray();

        var professions = Enum.GetValues<Profession>().Select(profession => new
        {
            name = profession.ToString(),
            offers = Trading.For(profession).Select(offer => new
            {
                label = offer.Label,
                cost = offer.Cost,
                costCount = offer.CostCount,
                result = offer.Result,
                resultCount = offer.ResultCount,
            }),
        }).ToArray();

        var keyboard = GameActions.All.Select(action => new
        {
            action = action.ToString(),
            label = GameActions.Label(action),
            primary = keys.Primary(action),
            secondary = keys.Secondary(action),
        }).ToArray();
        var controller = ControllerActions.All.Select(action => new
        {
            action = action.ToString(),
            label = ControllerActions.Label(action),
            control = pad.Control(action).ToString(),
        }).ToArray();

        var document = new
        {
            schema = 1,
            gameVersion = productVersion,
            generatedFrom = "Driftwood Core registries",
            counts = new
            {
                blocks = blockRows.Length,
                items = itemRows.Length,
                recipes = recipeRows.Length,
                smelts = smeltRows.Length,
                creatures = creatureRows.Length,
                creatureDropRules = creatureDrops.Rules.Count,
                structures = structures.Length,
                tradeOffers = Trading.All.Count(),
                keyboardActions = keyboard.Length,
                controllerActions = controller.Length,
                spells = SpellCatalogue.All.Count,
            },
            defaults = new
            {
                settings.ViewDistance,
                settings.FieldOfView,
                settings.FrameCap,
                settings.Volume,
                settings.MouseSensitivity,
                settings.ControllerDeadzone,
                settings.ControllerLookSpeed,
                settings.ControllerTargetAssist,
                settings.ControllerRumble,
                settings.HdrIntensity,
                settings.Shadows,
                settings.AmbientOcclusion,
                settings.Materials,
                settings.WaterEffects,
                settings.Weather,
                settings.GodRays,
                settings.Bloom,
                settings.TemporalAntialiasing,
                settings.CompanionWindowLocked,
                settings.SpellbookWindowLocked,
                settings.SpellBarWindowLocked,
            },
            controls = new { keyboard, controller },
            blocks = blockRows,
            items = itemRows,
            recipes = recipeRows,
            smelting = smeltRows,
            creatures = creatureRows,
            structures,
            professions,
            magicReference = "magic-reference.json",
        };

        var json = Path.Combine(folder, JsonName);
        File.WriteAllText(
            json,
            JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));

        var page = Path.Combine(folder, PageName);
        var markdown = $$"""
            <!-- Generated from Driftwood Core {{productVersion}}; regenerate with --magic-reference. -->
            > **Development reference — Driftwood {{productVersion}}.** Counts below come from the live game registries.

            # Live registry reference

            This is the build receipt behind the handbook's prose. Stable block, item, recipe, smelt,
            creature/drop, control, setting, structure, trade and magic identifiers are exported to
            [game-reference.json](game-reference.json). Spell ranks and companion values are in
            [magic-reference.json](magic-reference.json).

            | Registry | Rows |
            | --- | ---: |
            | Block states | {{blockRows.Length}} |
            | Gameplay items | {{itemRows.Length}} |
            | Crafting recipes | {{recipeRows.Length}} |
            | Smelting recipes | {{smeltRows.Length}} |
            | Creature kinds | {{creatureRows.Length}} |
            | Creature drop rules | {{creatureDrops.Rules.Count}} |
            | Authored site kinds | {{structures.Length}} |
            | Resident trade offers | {{Trading.All.Count()}} |
            | Keyboard actions | {{keyboard.Length}} |
            | Controller actions | {{controller.Length}} |
            | Initial spells | {{SpellCatalogue.All.Count}} |

            Numeric runtime registration ids are deliberately absent. Saves, docs and integrations use
            the stable names in these exports, so registration-order changes cannot rename player state.
            """;
        File.WriteAllText(page, markdown.TrimEnd() + Environment.NewLine, new UTF8Encoding(false));
        return [page, json];
    }

    public static List<string> Faults(string folder, string productVersion)
    {
        var faults = new List<string>();
        var page = Path.Combine(folder, PageName);
        var jsonPath = Path.Combine(folder, JsonName);
        if (!File.Exists(page)) faults.Add($"missing generated wiki page {PageName}");
        if (!File.Exists(jsonPath)) faults.Add($"missing {JsonName}");
        if (faults.Count > 0) return faults;

        var (blocks, items, _, creatureDrops, recipes) = Registries();
        var expectedBlocks = blocks.All.Skip(1).Select(one => one.Name).OrderBy(one => one, StringComparer.Ordinal);
        var expectedItems = items.All.Skip(1).Select(one => one.Name).OrderBy(one => one, StringComparer.Ordinal);
        var expectedRecipes = recipes.Recipes.Select(one => one.Name).OrderBy(one => one, StringComparer.Ordinal);
        var expectedCreatures = CreatureSet.All.Select(one => one.Name).OrderBy(one => one, StringComparer.Ordinal);

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = json.RootElement;
            if (root.GetProperty("schema").GetInt32() != 1
                || root.GetProperty("gameVersion").GetString() != productVersion)
                faults.Add("game-reference.json has a stale schema or build version");
            Compare("blocks", expectedBlocks, root.GetProperty("blocks"), faults);
            Compare("items", expectedItems, root.GetProperty("items"), faults);
            Compare("recipes", expectedRecipes, root.GetProperty("recipes"), faults);
            Compare("creatures", expectedCreatures, root.GetProperty("creatures"), faults);
            if (root.GetProperty("smelting").GetArrayLength() != recipes.Smelting.Count
                || root.GetProperty("structures").GetArrayLength() != Enum.GetValues<StructureKind>().Length
                || root.GetProperty("professions").GetArrayLength() != Enum.GetValues<Profession>().Length
                || root.GetProperty("counts").GetProperty("creatureDropRules").GetInt32() != creatureDrops.Rules.Count)
                faults.Add("game-reference.json has a stale smelt, structure, profession or drop count");
            var controls = root.GetProperty("controls");
            if (controls.GetProperty("keyboard").GetArrayLength() != GameActions.All.Length
                || controls.GetProperty("controller").GetArrayLength() != ControllerActions.All.Length)
                faults.Add("game-reference.json has incomplete default controls");
        }
        catch (Exception fault)
        {
            faults.Add($"{JsonName} does not parse: {fault.Message}");
        }

        var text = File.ReadAllText(page);
        if (!text.Contains($"Driftwood Core {productVersion}", StringComparison.Ordinal)
            || !text.Contains($"Driftwood {productVersion}", StringComparison.Ordinal)
            || !text.Contains("game-reference.json", StringComparison.Ordinal)
            || !text.Contains("magic-reference.json", StringComparison.Ordinal))
            faults.Add("the live registry page has a stale banner or missing export link");
        if (text.Contains("TODO", StringComparison.OrdinalIgnoreCase)
            || text.Contains("TBD", StringComparison.OrdinalIgnoreCase)
            || text.Contains("placeholder", StringComparison.OrdinalIgnoreCase))
            faults.Add("the live registry page contains placeholder writing");
        return faults;
    }

    private static void Compare(
        string what,
        IEnumerable<string> expected,
        JsonElement exported,
        List<string> faults)
    {
        var wanted = expected.ToArray();
        var actual = exported.EnumerateArray().Select(one => one.GetProperty("name").GetString() ?? "").ToArray();
        if (!wanted.SequenceEqual(actual, StringComparer.Ordinal))
            faults.Add($"game-reference.json has stale or unordered {what}");
        if (actual.Distinct(StringComparer.Ordinal).Count() != actual.Length)
            faults.Add($"game-reference.json has duplicate {what} names");
    }

    private static (BlockRegistry Blocks, ItemRegistry Items, BlockDrops BlockDrops,
        CreatureDrops CreatureDrops, RecipeBook Recipes) Registries()
    {
        var blocks = new BlockRegistry();
        StarterBlocks.Register(blocks);
        blocks.Seal();
        var items = StarterItems.Register(blocks);
        return (blocks, items, StarterItems.Drops(blocks, items), StarterItems.Creatures(items),
            StarterRecipes.Build(items));
    }
}
