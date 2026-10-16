namespace Driftwood.Core.Textures;

/// <summary>The small, explicit subset of a resource pack's interface Driftwood can wear.</summary>
public static class GuiTextureSet
{
    public const int Size = 256;

    // SetSkin is also used by the live player preview. The authored defaults never change, so build
    // their pixels once and clone only the two small outer arrays before sparse pack replacements.
    private static readonly Lazy<DefaultGuiTheme.Result> Defaults = new(() =>
        DefaultGuiTheme.Build(Enum.GetValues<Layer>().Length));

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
        MenuBackground,
        MenuListBackground,
        OptionsBackground,
        WidgetButton,
        WidgetButtonHighlighted,
        WidgetButtonDisabled,
        TextField,
        TextFieldHighlighted,
        Tab,
        TabHighlighted,
        TabSelected,
        TabSelectedHighlighted,
        TooltipBackground,
        ToastBackground,
        ToastSlot,
        Slider,
        SliderHighlighted,
        SliderHandle,
        SliderHandleHighlighted,
        Checkbox,
        CheckboxHighlighted,
        CheckboxSelected,
        CheckboxSelectedHighlighted,
        Scroller,
        ScrollerHighlighted,
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
        new(Layer.MenuBackground, "textures/gui/menu_background.png"),
        new(Layer.MenuListBackground, "textures/gui/menu_list_background.png"),
        new(Layer.OptionsBackground, "textures/gui/options_background.png"),
        new(Layer.WidgetButton, "textures/gui/sprites/widget/button.png"),
        new(Layer.WidgetButtonHighlighted, "textures/gui/sprites/widget/button_highlighted.png"),
        new(Layer.WidgetButtonDisabled, "textures/gui/sprites/widget/button_disabled.png"),
        new(Layer.TextField, "textures/gui/sprites/widget/text_field.png"),
        new(Layer.TextFieldHighlighted, "textures/gui/sprites/widget/text_field_highlighted.png"),
        new(Layer.Tab, "textures/gui/sprites/widget/tab.png"),
        new(Layer.TabHighlighted, "textures/gui/sprites/widget/tab_highlighted.png"),
        new(Layer.TabSelected, "textures/gui/sprites/widget/tab_selected.png"),
        new(Layer.TabSelectedHighlighted, "textures/gui/sprites/widget/tab_selected_highlighted.png"),
        new(Layer.TooltipBackground, "textures/gui/sprites/tooltip/background.png"),
        new(Layer.ToastBackground, "textures/gui/sprites/toast/system.png",
            "textures/gui/sprites/toast/advancement.png"),
        new(Layer.ToastSlot, "textures/gui/sprites/toast/recipe.png"),
        new(Layer.Slider, "textures/gui/sprites/widget/slider.png"),
        new(Layer.SliderHighlighted, "textures/gui/sprites/widget/slider_highlighted.png"),
        new(Layer.SliderHandle, "textures/gui/sprites/widget/slider_handle.png"),
        new(Layer.SliderHandleHighlighted, "textures/gui/sprites/widget/slider_handle_highlighted.png"),
        new(Layer.Checkbox, "textures/gui/sprites/widget/checkbox.png"),
        new(Layer.CheckboxHighlighted, "textures/gui/sprites/widget/checkbox_highlighted.png"),
        new(Layer.CheckboxSelected, "textures/gui/sprites/widget/checkbox_selected.png"),
        new(Layer.CheckboxSelectedHighlighted, "textures/gui/sprites/widget/checkbox_selected_highlighted.png"),
        new(Layer.Scroller, "textures/gui/sprites/widget/scroller.png"),
        new(Layer.ScrollerHighlighted, "textures/gui/sprites/widget/scroller_highlighted.png"),
        new(Layer.ScrollerBackground, "textures/gui/sprites/widget/scroller_background.png"),
    ];

    /// <summary>
    /// How a pack asks for one of its interface textures to be stretched, from the texture's own
    /// <c>.mcmeta</c>: nine-sliced at a declared size with fixed borders, or not at all.
    /// </summary>
    /// <remarks>
    /// ⛳ The borders are the pack's, not a guess. A button is drawn at any width by keeping its
    /// corners and edges at their authored size and stretching only the middle; a guessed border
    /// squashes whatever ornament the author put at the ends.
    /// </remarks>
    public readonly record struct Slice(float Width, float Height, float Left, float Top, float Right, float Bottom);

    public sealed record Result(byte[][] Tiles, bool[] Present, bool[] FromPack, int Loaded, int Defaults, Slice?[]? Slices = null)
    {
        public string Summary => Loaded == 0
            ? $"Driftwood's {Defaults}-layer graphite pixel theme"
            : $"{Loaded} of {Tiles.Length} GUI layers from the pack over {Defaults} Driftwood fallbacks";
    }

    public static Result Load(TexturePack? pack)
    {
        var count = Enum.GetValues<Layer>().Length;
        var defaults = Defaults.Value;
        var tiles = (byte[][])defaults.Tiles.Clone();
        var present = (bool[])defaults.Present.Clone();
        var fromPack = new bool[count];
        var loaded = 0;
        var slices = new Slice?[count];

        if (pack is null) return new Result(tiles, present, fromPack, 0, defaults.Painted);

        foreach (var entry in Entries)
        {
            var tile = pack.TryLoadTile(entry.Path, Size);
            if (tile is null && entry.Alternate.Length > 0)
                tile = pack.TryLoadTile(entry.Alternate, Size);
            if (tile is null) continue;

            var layer = (int)entry.Layer;
            tiles[layer] = tile;
            present[layer] = true;
            fromPack[layer] = true;
            slices[layer] = ReadSlice(pack, entry.Path)
                ?? (entry.Alternate.Length > 0 ? ReadSlice(pack, entry.Alternate) : null);
            loaded++;
        }

        return new Result(tiles, present, fromPack, loaded, defaults.Painted, slices);
    }

    /// <summary>
    /// Reads a texture's <c>gui.scaling</c> from its <c>.mcmeta</c>. Only nine-slicing changes how
    /// it is drawn; "stretch", "tile", no file and an unreadable file all leave the default.
    /// </summary>
    private static Slice? ReadSlice(TexturePack pack, string path)
    {
        var bytes = pack.TryReadAssetBytes(path + ".mcmeta", 64 * 1024, out _);
        if (bytes is null) return null;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("gui", out var gui)
                || !gui.TryGetProperty("scaling", out var scaling)
                || !scaling.TryGetProperty("type", out var type) || type.GetString() != "nine_slice")
                return null;

            var width = Number(scaling, "width", 0f);
            var height = Number(scaling, "height", 0f);
            if (width <= 0f || height <= 0f) return null;

            // A border is one number for all four sides, or one per side.
            float left, top, right, bottom;
            if (!scaling.TryGetProperty("border", out var border)) return null;
            if (border.ValueKind == System.Text.Json.JsonValueKind.Number)
                left = top = right = bottom = border.GetSingle();
            else
            {
                left = Number(border, "left", 0f);
                top = Number(border, "top", 0f);
                right = Number(border, "right", 0f);
                bottom = Number(border, "bottom", 0f);
            }

            left = Math.Clamp(left, 0f, width / 2f);
            right = Math.Clamp(right, 0f, width / 2f);
            top = Math.Clamp(top, 0f, height / 2f);
            bottom = Math.Clamp(bottom, 0f, height / 2f);
            return new Slice(width, height, left, top, right, bottom);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        static float Number(System.Text.Json.JsonElement element, string name, float otherwise) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number
                ? value.GetSingle() : otherwise;
    }
}
