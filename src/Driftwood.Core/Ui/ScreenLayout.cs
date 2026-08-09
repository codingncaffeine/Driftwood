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

    /// <summary>A chest: three rows of nine over the pockets, and nothing else at all.</summary>
    Chest,

    /// <summary>A stonecutter: one rock in, everything it cuts into offered, one taken out.</summary>
    Stonecutter,
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

    /// <summary>A slot of whatever container is open. The index is a slot of a <see cref="Chest"/>.</summary>
    Stored,

    /// <summary>The rock a stonecutter is working on.</summary>
    Cutting,

    /// <summary>What the chosen cut would make. Gives only.</summary>
    Cut,
}

/// <summary>What kind of thing the pointer is over.</summary>
public enum ZoneKind
{
    None,
    Slot,
    Tab,
    Row,
    Recipe,

    /// <summary>The bar down the side of a list too long to show at once.</summary>
    Scrollbar,

    /// <summary>Something that does one thing when pressed. The index says which.</summary>
    Button,

    /// <summary>The explored map canvas; dragging it pans without stealing clicks from its tabs.</summary>
    Map,

    /// <summary>
    /// A box to type into, sitting on a row. The index is that row's.
    /// </summary>
    /// <remarks>
    /// Its own zone rather than leaving the whole row to answer, because clicking a box is how
    /// everybody expects to start typing in one — and because it gives the check a rectangle built
    /// from what was actually drawn rather than one worked out again from the same constants.
    /// ⚠ Added after its row, since the later zone is the one on top.
    /// </remarks>
    Field,
}

/// <summary>Which button. The index of a <see cref="ZoneKind.Button"/> zone.</summary>
public enum ScreenButton
{
    /// <summary>Folds the recipe book out beside the panel, and away again.</summary>
    Book,

    PageBack,
    PageForward,

    /// <summary>The six recipe shelves, kept consecutive so the category is the offset.</summary>
    RecipeCategoryAll,
    RecipeCategoryBuilding,
    RecipeCategoryTools,
    RecipeCategoryMaterials,
    RecipeCategoryLight,
    RecipeCategoryMachines,

    /// <summary>Shows every matching recipe, or only those craftable here and now.</summary>
    CraftableOnly,
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

    // The bottom half, shared by every panel: three rows of nine, then the bar.
    private const int PocketsLeft = 8;
    private const int PocketsTop = 84;
    private const int BarTop = 142;

    /// <summary>
    /// Where a container's own rows begin, and the pitch they run at.
    /// </summary>
    /// <remarks>
    /// ⚠ Measured out of the pack's own <c>shulker_box.png</c> by sampling pixels, not remembered —
    /// three rows of nine at 8,18 with the pockets still at 84 and the bar still at 142, which is to
    /// say a twenty-seven-slot container is <em>exactly</em> the panel we already draw. That is worth
    /// knowing rather than assuming: a chest could have needed a taller panel and a second set of
    /// numbers, and it does not. (A double chest does — six rows at 18, pockets at 140, bar at 198,
    /// 176 by 222 — measured off <c>generic_54.png</c> at the same time, for when it lands.)
    /// </remarks>
    private const int StoredTop = 18;

    /// <summary>Where a stonecutter lists what it would make, on the same 18 pitch as everything.</summary>
    /// <remarks>
    /// Between the rock and the result, which is where the sheet a pack ships puts its own list.
    /// Four across and three down is twelve, comfortably more than any one rock offers — the most
    /// today is three, and a well with room to spare reads as a list rather than as a full grid.
    /// </remarks>
    public static readonly (int X, int Y) CutList = (52, 15);

    public const int CutColumns = 4;

    public const int CutRows = 3;

    /// <summary>How many cuts are shown at once.</summary>
    public const int CutOffers = CutColumns * CutRows;

    private readonly List<Zone> _zones = [];

    public IReadOnlyList<Zone> Zones => _zones;

    /// <summary>
    /// The recipe book, which folds out beside the panel rather than living on a tab of its own.
    /// </summary>
    /// <remarks>
    /// <b>147 by 166 — the same height as the container panel, which is the tell.</b> Two panels
    /// painted to exactly one height are two panels meant to stand side by side, and that is what a
    /// pack's <c>recipe_book.png</c> is: not a screen you go to, a leaf you unfold next to the one
    /// you are on. Which matters for more than looks — a book on its own tab crafts into pockets
    /// that are not on screen, so what you just made goes somewhere you cannot see.
    /// </remarks>
    public const int BookWidth = 147;

    public const int BookHeight = 166;

    /// <summary>Between the book and the panel it hangs off.</summary>
    public const int BookGap = 4;

    /// <summary>How far the 35x27 shelf tabs protrude past the book's left edge.</summary>
    public const int BookTabReach = 28;

    public const int BookTabWidth = 35;

    public const int BookTabHeight = 27;

    /// <summary>A recipe in the book. Twenty five, which is what the pack's own slot sprite is.</summary>
    public const int BookCell = 25;

    public const int BookColumns = 5;

    public const int BookRows = 4;

    /// <summary>How many recipes one page of the book holds.</summary>
    public const int BookPage = BookColumns * BookRows;

    // Inside the book's own frame: the well is 132x151 at 8,8, and this is what goes in it.
    private const int BookGridX = 11;
    private const int BookGridY = 34;

    /// <summary>Where the panel's top left corner sits, in layout units.</summary>
    public float OriginX { get; private set; }

    public float OriginY { get; private set; }

    /// <summary>Where the book's top left corner sits, when it is out.</summary>
    public float BookX { get; private set; }

    /// <summary>True when the last panel was laid out with the book beside it.</summary>
    public bool BookOut { get; private set; }

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
    public static float ZoomFor(float width, float height, bool bookOut = false)
    {
        var across = bookOut ? PanelWidth + BookWidth + BookGap + BookTabReach : PanelWidth;

        var want = MathF.Round(height * 0.62f / PanelHeight);
        var fitsTall = MathF.Floor((height - 40f) / PanelHeight);
        var fitsWide = MathF.Floor(width / across);

        return MathF.Max(1f, MathF.Min(want, MathF.Min(fitsTall, fitsWide)));
    }

    /// <summary>
    /// Lays a container panel out, centred, and fills in every square it holds.
    /// </summary>
    /// <param name="craftCells">
    /// How wide the crafting grid is — two for the player's own hands, three at a bench. Ignored by
    /// the furnace, which has no grid.
    /// </param>
    public void BuildPanel(
        PanelKind kind, int craftCells, float screenWidth, float screenHeight,
        bool bookOut = false, int bookPage = 0, int bookCount = 0)
    {
        _zones.Clear();
        Kind = kind;

        // ⚠ Named by what it belongs to rather than by what it does not. It was written as "anything
        // but a furnace", which was true when a furnace was the only other panel — and the day a
        // stonecutter arrived with a list of its own on the same zone kind, the book drew straight
        // over it out of an empty recipe list, and threw.
        //
        // ⛳⛳ AND THE FURNACE HAS ONE NOW, which is the user's own instruction: "just re-use the
        // recipe book icon that we use for the workbench". It had none, so a fire was three squares
        // and no indication anywhere in the game that one of them cooks meat — reported as "I'm not
        // seeing any recipes for food when i look in the furnace". A stonecutter still has none
        // because its list IS its panel, built from the rock on its bed.
        BookOut = bookOut && kind is PanelKind.Player or PanelKind.Bench or PanelKind.Furnace;

        Zoom = ZoomFor(screenWidth, screenHeight, BookOut);

        // The pair is centred together when the book is out, so folding it away does not leave the
        // panel sitting off to one side of the screen.
        // Centre the book and panel as the pair they have always been; the category tabs protrude
        // from that pair like tabs do. ZoomFor still budgets their reach so none are clipped.
        var spread = BookOut ? (BookWidth + BookGap) * Zoom : 0f;
        OriginX = MathF.Round((screenWidth - PanelWidth * Zoom + spread) * 0.5f);
        OriginY = MathF.Round((screenHeight - PanelHeight * Zoom) * 0.5f);
        BookX = OriginX - (BookWidth + BookGap) * Zoom;

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
                Button(ScreenButton.Book, BookToggle.X, BookToggle.Y, BookToggle.W, BookToggle.H);
                break;

            case PanelKind.Bench:
                for (var y = 0; y < craftCells; y++)
                for (var x = 0; x < craftCells; x++)
                    Square16(SlotRole.Craft, y * craftCells + x, 30 + x * Pitch, 17 + y * Pitch);

                Square16(SlotRole.Result, 0, 124, 35);
                Button(ScreenButton.Book, BenchBookToggle.X, BenchBookToggle.Y, BenchBookToggle.W, BenchBookToggle.H);
                break;

            case PanelKind.Furnace:
                Square16(SlotRole.Smelting, 0, 56, 17);
                Square16(SlotRole.Fuel, 0, 56, 53);
                Square16(SlotRole.Smelted, 0, 116, 35);

                // ⛔ The button was MISSING here while Player and Bench both had one — so at a
                // fire, the book only ever appeared if some other screen had left BookOut set,
                // and there was nothing on screen to open it with. Reported by the user as the
                // fires "not showing recipes" a session after the book itself was built. Same
                // spot as the bench's, which is clear on this sheet too.
                Button(ScreenButton.Book, BenchBookToggle.X, BenchBookToggle.Y, BenchBookToggle.W, BenchBookToggle.H);
                break;

            // ⚠ Not a grid. A stonecutter takes one thing and offers several, so what sits between
            // the input and the output is a LIST rather than an arrangement — the same zone kind the
            // recipe book uses, because it is the same gesture: look at what is on offer, pick one.
            case PanelKind.Stonecutter:
                Square16(SlotRole.Cutting, 0, 20, 33);
                Square16(SlotRole.Cut, 0, 143, 33);

                for (var i = 0; i < CutOffers; i++)
                    _zones.Add(new Zone(
                        ZoneKind.Recipe, SlotRole.None, i,
                        X(CutList.X + i % CutColumns * Pitch),
                        Y(CutList.Y + i / CutColumns * Pitch),
                        Size(Square), Size(Square)));
                break;

            case PanelKind.Chest:
                for (var row = 0; row < Chest.Slots / Inventory.HotbarSlots; row++)
                for (var column = 0; column < Inventory.HotbarSlots; column++)
                    Square16(
                        SlotRole.Stored,
                        row * Inventory.HotbarSlots + column,
                        PocketsLeft + column * Pitch,
                        StoredTop + row * Pitch);
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

        if (BookOut) BuildBook(bookPage, bookCount);
    }

    /// <summary>The recipes on the open page, and the arrows to the pages either side.</summary>
    private void BuildBook(int page, int count)
    {
        var pages = Math.Max(1, (count + BookPage - 1) / BookPage);
        page = Math.Clamp(page, 0, pages - 1);

        var from = page * BookPage;
        var to = Math.Min(count, from + BookPage);

        // A real field and two real buttons, in the header that used to hold only a caption.
        // They are zones from the same layout the renderer reads, so filtering does not grow a
        // second set of almost-the-same hit-test constants.
        _zones.Add(new Zone(
            ZoneKind.Field, SlotRole.None, -1,
            BookX + 8f * Zoom, Y(12), Size(101), Size(15)));

        var shelves = Enum.GetValues<RecipeCategory>().Length;
        for (var shelf = 0; shelf < shelves; shelf++)
            _zones.Add(new Zone(
                ZoneKind.Button, SlotRole.None,
                (int)ScreenButton.RecipeCategoryAll + shelf,
                BookX - BookTabReach * Zoom,
                Y(2 + shelf * BookTabHeight),
                Size(BookTabWidth), Size(BookTabHeight)));

        _zones.Add(new Zone(
            ZoneKind.Button, SlotRole.None, (int)ScreenButton.CraftableOnly,
            BookX + 113f * Zoom, Y(11), Size(26), Size(16)));

        for (var i = from; i < to; i++)
        {
            var at = i - from;
            _zones.Add(new Zone(
                ZoneKind.Recipe, SlotRole.None, i,
                BookX + (BookGridX + at % BookColumns * BookCell) * Zoom,
                Y(BookGridY + at / BookColumns * BookCell),
                Size(BookCell - 2), Size(BookCell - 2)));
        }

        // Both arrows exist whatever page it is on; whoever draws them dims the one that would do
        // nothing. A button that vanishes is a button somebody hunts for.
        _zones.Add(new Zone(
            ZoneKind.Button, SlotRole.None, (int)ScreenButton.PageBack,
            BookX + 12f * Zoom, Y(140), Size(12), Size(17)));

        _zones.Add(new Zone(
            ZoneKind.Button, SlotRole.None, (int)ScreenButton.PageForward,
            BookX + 123f * Zoom, Y(140), Size(12), Size(17)));
    }

    /// <summary>
    /// Where the button that folds the book out sits, on each panel that has one.
    /// </summary>
    /// <remarks>
    /// Eighteen across rather than the pack's twenty, so it stands on the same pitch as the squares
    /// and leaves the same two pixels of panel beside them. Twenty put it flush against the
    /// two-by-two, which the audit caught by name: everything else on this panel has a gutter, and
    /// a button touching a square is the one place a click could be one pixel from meaning
    /// something completely different.
    /// </remarks>
    public static readonly (int X, int Y, int W, int H) BookToggle = (78, 18, 18, 18);

    public static readonly (int X, int Y, int W, int H) BenchBookToggle = (5, 17, 18, 18);

    /// <summary>The book's own frame and the well inside it, in the book's own pixels.</summary>
    public static readonly (int X, int Y, int W, int H) BookWell = (8, 8, 132, 151);

    /// <summary>Where a page's grid starts inside the book.</summary>
    public static readonly (int X, int Y) BookGrid = (BookGridX, BookGridY);

    private void Button(ScreenButton which, int panelX, int panelY, int w, int h) =>
        _zones.Add(new Zone(
            ZoneKind.Button, SlotRole.None, (int)which,
            X(panelX), Y(panelY), Size(w), Size(h)));

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

    /// <summary>One line of a settings list, in layout units.</summary>
    public const float MenuLine = 13f;

    /// <summary>How wide the bar down the side of a long list is.</summary>
    public const float ScrollbarWidth = 7f;

    /// <summary>
    /// How many lines of a settings list are shown at once.
    /// </summary>
    /// <remarks>
    /// <b>Capped, and the cap is the point.</b> The controls tab has twenty eight rows in it and
    /// every new binding adds one, so a list drawn at its full length is a panel that grows until it
    /// runs off the bottom of the window — and then keeps growing, invisibly. Twelve lines is a
    /// panel that reads at a glance, and what does not fit is scrolled to. Held further down on a
    /// window too short even for that, so the answer is always a shorter list rather than a clipped
    /// one.
    /// </remarks>
    public static int MenuLines(float height) =>
        Math.Clamp((int)MathF.Floor((height - 150f) / MenuLine), 5, 12);
}
