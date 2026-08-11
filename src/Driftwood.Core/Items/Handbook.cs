using Driftwood.Core.Blocks;

namespace Driftwood.Core.Items;

/// <summary>Builds the player-facing facts behind an item handbook entry.</summary>
public sealed class Handbook(
    ItemRegistry items, BlockRegistry blocks, RecipeBook recipes, BlockDrops drops)
{
    public string Describe(ItemType item)
    {
        var facts = new List<string>();

        var made = recipes.Recipes.Where(recipe => recipe.Result.Item == item.Id).ToArray();
        if (made.Length > 0)
            facts.Add("made by " + string.Join(" or ", made.Select(recipe =>
                $"{recipe.Name} {recipe.MadeAt}")));

        var used = recipes.Recipes
            .Where(recipe => recipe.Ingredients.Any(part => part.Matches(item.Id)))
            .Select(recipe => recipe.Name).Distinct().ToArray();
        if (used.Length > 0) facts.Add("used to make " + string.Join(", ", used));

        var sources = drops.Sources(item.Id)
            .Select(id => blocks[id].Name.Replace('_', ' ')).Distinct().ToArray();
        if (sources.Length > 0) facts.Add("found from " + string.Join(", ", sources));

        var smeltsFrom = recipes.Smelting
            .Where(recipe => recipe.Result.Item == item.Id)
            .Select(recipe => recipe.Input.Name).Distinct().ToArray();
        if (smeltsFrom.Length > 0) facts.Add("smelted from " + string.Join(" or ", smeltsFrom));

        var smeltsInto = recipes.Smelting
            .Where(recipe => recipe.Input.Matches(item.Id))
            .Select(recipe => items[recipe.Result.Item].Label).Distinct().ToArray();
        if (smeltsInto.Length > 0) facts.Add("smelts into " + string.Join(" or ", smeltsInto));

        if (item.Places is { Variants.Length: > 0 } place)
        {
            var block = blocks[place.Variants[0]];
            var times = items.All
                .Where(tool => tool.IsTool && tool.Tool == block.HarvestClass)
                .Select(tool => $"{tool.Label} {MiningRules.SecondsToBreak(block, tool):0.0}s")
                .ToArray();
            facts.Add($"places {block.Name.Replace('_', ' ')}; {MiningRules.SecondsToBreak(block, null):0.0}s by hand"
                + (times.Length > 0 ? ", " + string.Join(", ", times) : ""));
        }
        else if (item.PlacesEntity) facts.Add("places an entity in the world");

        if (item.IsTool)
            facts.Add($"{item.Tool.ToString().ToLowerInvariant()} tier {item.Tier}, {item.MiningSpeed:0.#}x, "
                + $"{item.Durability} uses, +{item.AttackDamage} attack");
        if (item.Wears is { } worn) facts.Add($"worn as {worn.ToString().ToLowerInvariant()}, {item.ArmourPoints} armour");
        if (item.ShieldShare > 0) facts.Add($"blocks {item.ShieldShare:P0} of a raised hit");
        if (item.Use == ItemUse.Bow) facts.Add("fires arrows from your pockets");
        if (item.Use == ItemUse.BowAmmunition) facts.Add("ammunition for a bow");
        if (item.Use == ItemUse.ThrownFarstep) facts.Add("throw to farstep where it lands");
        if (item.IsFood) facts.Add($"restores {item.Feeds / 2f:0.#} hearts");
        if (item.BurnSeconds > 0) facts.Add($"burns for {item.BurnSeconds:0.#} seconds");
        facts.Add(item.MaxStack > 1 ? $"stacks to {item.MaxStack}" : "one per slot");

        return string.Join(". ", facts) + ".";
    }
}
