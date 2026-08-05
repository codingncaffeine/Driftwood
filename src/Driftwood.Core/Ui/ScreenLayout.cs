using Driftwood.Core.Items;

namespace Driftwood.Core.Ui;

/// <summary>Which of the three container panels is being laid out.</summary>
public enum PanelKind
{
    /// <summary>What this character carries: worn slots, a figure, the two-by-two, the pockets.</summary>
    Player,

    /// <summary>A bench: three by three, and the pockets under it.</summary>
    Bench,

    /// <summary>A furnace: what goes in, what burns, what comes out, and the pockets.</summary>
    Furnace,
}

/// <summary>What a square is for. The index means something different for each.</summary>
public enum SlotRole
{
    None,

    /// <summary>A pocket. The index is a slot in <see cref="Inventory"/>, the bar included.</summary>
    Pocket,

    /// <summary>A square of the crafting grid. The index is a cell of <see cref="CraftingGrid"/>.</summary>
    Craft,

    /// <summary>What the grid makes. Gives only; never takes.</summary>
    Result,

    /// <summary>Worn, or in the other hand. The index is an <see cref="EquipSlot"/>.</summary>
    Equip,

    /// <summary>What a furnace is working on.</summary>
    Smelting,

    /// <summary>What a furnace is burning.</summary>
    Fuel,

    /// <summary>What a furnace has finished. Gives only.</summary>
    Smelted,
}

/// <summary>What kind of thing the pointer is over.</summary>
public enum ZoneKind
{
    None,
    Slot,
    Tab,
    Row,
    Recipe,
}

/// <summary>
/// One rectangle on screen that means something, in layout units.
/// </summary>
/// <remarks>
/// Half-open on the far edges, so two zones laid edge to edge cannot both claim the pixel between
/// them. A slot grid with an eighteen-unit pitch and sixteen-unit squares has gaps rather than
/// overlaps anyway, but the rule is what makes that true by construction rather than by luck.
/// </remarks>
public readonly record struct Zone(ZoneKind Kind, SlotRole Role, int Index, float X, float Y, float W, float H)
{
    public bool Contains(float x, float y) => x >= X && y >= Y && x < X + W && y < Y + H;

    public float CentreX => X + W * 0.5f;

    public float CentreY => Y + H * 0.5f;
}

/// <summary>
/// The one layout: what the overlay draws, and what the pointer is tested against.
/// </summary>
/// <remarks>
/// <para><b>Computed once and read by both sides.</b> A screen whose hit test is written from the
/// same constants as its renderer drifts the first time one of them is edited, and the symptom —
/// clicks landing half a square away from the pictures — is one a player notices long before anyone
/// finds it in the source. So the rectangles are built here, the renderer draws the list, and the
/// pointer is tested against the list.</para>
/// <para><b>The panel is authored in the pack's own pixel grid, not in ours.</b> A hundred and
/// seventy six by a hundred and sixty six, squares on an eighteen pitch with sixteen inside them —
/// which is not an arbitrary choice, it is what every <c>inventory.png</c>, <c>crafting_table.png</c>
/// and <c>furnace.png</c> in every resource pack is painted on. Those three files were measured
/// rather than remembered. Laying our own chrome out on that grid means the day a pack skins the
/// interface, its panel blits in at a whole number of units per texel with no layout to redo.</para>
/// <para><see cref="Zoom"/> is how many layout units one of those panel pixels is worth, and it is
/// always a whole number for the reason everything here is: half a unit puts a one-pixel slot border
/// on one and a half pixels and the sampler resolves that by blurring it.</para>
/// </remarks>
public sealed class ScreenLayout
{
    /// <summary>The panel every container screen in this genre is drawn on, in its own pixels.</summary>
    public const int PanelWidth = 176;

    public const int PanelHeight = 166;

    /// <summary>One square, border included. Measured off three of the pack's own container sheets.</summary>
    public const int Pitch = 18;

    /// <summary>What is inside one square — where the icon goes, and what is hit-tested.</summary>
    public const int Square = 16;

    /// <summary>A result square wears a wider frame. The square inside it is still sixteen.</summary>
    public const int ResultFrame = 26;

    // The bottom half, shared by all three panels: three rows of nine, then the bar.
    private const int PocketsLeft = 8;
    private const int PocketsTop = 84;
    private const int BarTop = 142;

    private readonly List<Zone> _zones = [];

    public IReadOnlyList<Zone> Zones => _zones;

    /// <summary>Where the panel's top left corner sits, in layout units.</summary>
    public float OriginX { get; private set; }

    public float OriginY { get; private set; }

    /// <summary>Layout units per panel pixel. A whole number, never less than one.</summary>
    public float Zoom { get; private set; } = 1f;

    /// <summary>Which panel was last built, so a renderer can draw the parts that are not squares.</summary>
    public PanelKind Kind { get; private set; }

    public void Clear() => _zones.Clear();

    public void Add(Zone zone) => _zones.Add(zone);

    public void Add(ZoneKind kind, int index, float x, float y, float w, float h) =>
        _zones.Add(new Zone(kind, SlotRole.None, index, x, y, w, h));

    /// <summary>What the pointer is over, or null over nothing.</summary>
    /// <remarks>Last one wins, so anything added later is on top — which is how it draws.</remarks>
    public Zone? At(float x, float y)
    {
        for (var i = _zones.Count - 1; i >= 0; i--)
            if (_zones[i].Contains(x, y)) return _zones[i];

        return null;
    }

    /// <summary>The one zone with this role and index, or null when the panel has no such square.</summary>
    public Zone? Find(SlotRole role, int index)
    {
        foreach (var zone in _zones)
            if (zone.Role == role && zone.Index == index) return zone;

        return null;
    }

    /// <summary>A panel-pixel x, in layout units.</summary>
    public float X(float panelX) => OriginX + panelX * Zoom;

    /// <summary>A panel-pixel y, in layout units.</summary>
    public float Y(float panelY) => OriginY + panelY * Zoom;

    /// <summary>A panel-pixel length, in layout units.</summary>
    public float Size(float panelLength) => panelLength * Zoom;

    /// <summary>
    /// How many layout units one panel pixel is worth on a screen this size.
    /// </summary>
    /// <remarks>
    /// Sized against the height so the panel fills about three fifths of it — the share the genre
    /// settled on and the one that leaves the footer hint somewhere to go — then held down to
    /// whatever actually fits, so a short window gets a smaller panel rather than a clipped one.
    /// </remarks>
    public static float ZoomFor(float width, float height)
    {
        var want = MathF.Round(height * 0.62f / PanelHeight);
        var fitsTall = MathF.Floor((height - 40f) / PanelHeight);
        var fitsWide = MathF.Floor(width / PanelWidth);

        return MathF.Max(1f, MathF.Min(want, MathF.Min(fitsTall, fitsWide)));
    }

    /// <summary>
    /// Lays a container panel out, centred, and fills in every square it holds.
    /// </summary>
    /// <param name="craftCells">
    /// How wide the crafting grid is — two for the player's own hands, three at a bench. Ignored by
    /// the furnace, which has no grid.
    /// </param>
    public void BuildPanel(PanelKind kind, int craftCells, float screenWidth, float screenHeight)
    {
        _zones.Clear();
        Kind = kind;

        Zoom = ZoomFor(screenWidth, screenHeight);
        OriginX = MathF.Round((screenWidth - PanelWidth * Zoom) * 0.5f);
        OriginY = MathF.Round((screenHeight - PanelHeight * Zoom) * 0.5f);

        switch (kind)
        {
            case PanelKind.Player:
                // Down the left, head to feet, then the other hand out beside them — the order and
                // the places the pack's own sheet paints them.
                for (var i = 0; i < 4; i++) Square16(SlotRole.Equip, i, PocketsLeft, 8 + i * Pitch);
                Square16(SlotRole.Equip, (int)EquipSlot.Offhand, 77, 62);

                for (var y = 0; y < craftCells; y++)
                for (var x = 0; x < craftCells; x++)
                    Square16(SlotRole.Craft, y * craftCells + x, 98 + x * Pitch, 18 + y * Pitch);

                Square16(SlotRole.Result, 0, 154, 28);
                break;

            case PanelKind.Bench:
                for (var y = 0; y < craftCells; y++)
                for (var x = 0; x < craftCells; x++)
                    Square16(SlotRole.Craft, y * craftCells + x, 30 + x * Pitch, 17 + y * Pitch);

                Square16(SlotRole.Result, 0, 124, 35);
                break;

            case PanelKind.Furnace:
                Square16(SlotRole.Smelting, 0, 56, 17);
                Square16(SlotRole.Fuel, 0, 56, 53);
                Square16(SlotRole.Smelted, 0, 116, 35);
                break;
        }

        // The pockets, on every panel. Every container screen in this genre shows what you are
        // carrying under whatever it is a screen of, because otherwise there is no way to put
        // anything into it.
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < Inventory.HotbarSlots; column++)
            Square16(
                SlotRole.Pocket,
                Inventory.HotbarSlots + row * Inventory.HotbarSlots + column,
                PocketsLeft + column * Pitch,
                PocketsTop + row * Pitch);

        for (var column = 0; column < Inventory.HotbarSlots; column++)
            Square16(SlotRole.Pocket, column, PocketsLeft + column * Pitch, BarTop);
    }

    private void Square16(SlotRole role, int index, int panelX, int panelY) =>
        _zones.Add(new Zone(
            ZoneKind.Slot, role, index,
            X(panelX), Y(panelY), Size(Square), Size(Square)));

    // Where the parts that are not squares go, in panel pixels. Read off the pack's own sheets
    // rather than remembered — the arrow on the player panel is genuinely a different size from the
    // one on the bench, which is not something anybody would guess.
    public static readonly (int X, int Y, int W, int H) PlayerArrow = (134, 29, 19, 13);
    public static readonly (int X, int Y, int W, int H) BenchArrow = (90, 35, 22, 15);
    public static readonly (int X, int Y, int W, int H) FurnaceArrow = (80, 35, 22, 15);
    public static readonly (int X, int Y, int W, int H) FurnaceFlame = (56, 36, 14, 14);

    /// <summary>The window the player's own figure is shown in.</summary>
    public static readonly (int X, int Y, int W, int H) Figure = (25, 7, 50, 71);
}
