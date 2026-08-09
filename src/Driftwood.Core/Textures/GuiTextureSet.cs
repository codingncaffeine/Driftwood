namespace Driftwood.Core.Textures;

/// <summary>The small, explicit subset of a resource pack's interface Driftwood can wear.</summary>
public static class GuiTextureSet
{
    public const int Size = 256;

    public enum Layer
    {
        Inventory,
        CraftingTable,
        Furnace,
        Chest,
        Stonecutter,
        Hotbar,
        HotbarSelection,
        OffhandLeft,
        OffhandRight,
        AttackBackground,
        AttackProgress,
        AttackFull,
        HeartContainer,
        HeartFull,
        HeartHalf,
        FoodEmpty,
        FoodFull,
        FoodHalf,
        Air,
        ArmourEmpty,
        ArmourFull,
        ArmourHalf,
        RecipeTab,
        RecipeTabSelected,
        RecipeFilterOff,
        RecipeFilterOn,
        RecipeButton,
        RecipeButtonHighlighted,
        RecipeOverlay,
        WidgetButton,
        WidgetButtonHighlighted,
        Scroller,
        ScrollerBackground,
    }

    public readonly record struct Entry(Layer Layer, string Path, string Alternate = "");

    public static readonly Entry[] Entries =
    [
        new(Layer.Inventory, "textures/gui/container/inventory.png"),
        new(Layer.CraftingTable, "textures/gui/container/crafting_table.png"),
        new(Layer.Furnace, "textures/gui/container/furnace.png"),
        new(Layer.Chest, "textures/gui/container/shulker_box.png"),
        new(Layer.Stonecutter, "textures/gui/container/stonecutter.png"),
        new(Layer.Hotbar, "textures/gui/sprites/hud/hotbar.png"),
        new(Layer.HotbarSelection, "textures/gui/sprites/hud/hotbar_selection.png"),
        new(Layer.OffhandLeft, "textures/gui/sprites/hud/hotbar_offhand_left.png"),
        new(Layer.OffhandRight, "textures/gui/sprites/hud/hotbar_offhand_right.png"),
        new(Layer.AttackBackground, "textures/gui/sprites/hud/crosshair_attack_indicator_background.png"),
        new(Layer.AttackProgress, "textures/gui/sprites/hud/crosshair_attack_indicator_progress.png"),
        new(Layer.AttackFull, "textures/gui/sprites/hud/crosshair_attack_indicator_full.png"),
        new(Layer.HeartContainer, "textures/gui/sprites/hud/heart/container.png"),
        new(Layer.HeartFull, "textures/gui/sprites/hud/heart/full.png"),
        new(Layer.HeartHalf, "textures/gui/sprites/hud/heart/half.png"),
        new(Layer.FoodEmpty, "textures/gui/sprites/hud/food_empty.png"),
        new(Layer.FoodFull, "textures/gui/sprites/hud/food_full.png"),
        new(Layer.FoodHalf, "textures/gui/sprites/hud/food_half.png"),
        new(Layer.Air, "textures/gui/sprites/hud/air.png"),
        new(Layer.ArmourEmpty, "textures/gui/sprites/hud/armor_empty.png"),
        new(Layer.ArmourFull, "textures/gui/sprites/hud/armor_full.png"),
        new(Layer.ArmourHalf, "textures/gui/sprites/hud/armor_half.png"),
        new(Layer.RecipeTab, "textures/gui/sprites/recipe_book/tab.png"),
        new(Layer.RecipeTabSelected, "textures/gui/sprites/recipe_book/tab_selected.png"),
        new(Layer.RecipeFilterOff, "textures/gui/sprites/recipe_book/filter_disabled.png"),
        new(Layer.RecipeFilterOn, "textures/gui/sprites/recipe_book/filter_enabled.png"),
        new(Layer.RecipeButton, "textures/gui/sprites/recipe_book/button.png"),
        new(Layer.RecipeButtonHighlighted, "textures/gui/sprites/recipe_book/button_highlighted.png"),
        new(Layer.RecipeOverlay, "textures/gui/sprites/recipe_book/overlay_recipe.png"),
        new(Layer.WidgetButton, "textures/gui/sprites/widget/button.png"),
        new(Layer.WidgetButtonHighlighted, "textures/gui/sprites/widget/button_highlighted.png"),
        new(Layer.Scroller, "textures/gui/sprites/widget/scroller.png"),
        new(Layer.ScrollerBackground, "textures/gui/sprites/widget/scroller_background.png"),
    ];

    public sealed record Result(byte[][] Tiles, bool[] Present, int Loaded)
    {
        public string Summary => $"{Loaded} of {Tiles.Length} GUI layers from the pack";
    }

    public static Result? Load(TexturePack? pack)
    {
        if (pack is null) return null;

        var count = Enum.GetValues<Layer>().Length;
        var tiles = new byte[count][];
        var present = new bool[count];
        var loaded = 0;

        for (var i = 0; i < count; i++) tiles[i] = new byte[Size * Size * 4];

        foreach (var entry in Entries)
        {
            var tile = pack.TryLoadTile(entry.Path, Size);
            if (tile is null && entry.Alternate.Length > 0)
                tile = pack.TryLoadTile(entry.Alternate, Size);
            if (tile is null) continue;

            var layer = (int)entry.Layer;
            tiles[layer] = tile;
            present[layer] = true;
            loaded++;
        }

        return loaded == 0 ? null : new Result(tiles, present, loaded);
    }
}
