using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;
using Driftwood.Core.Gen;
using Driftwood.Core.Items;
using Driftwood.Core.Physics;
using Driftwood.Core.Textures;
using Driftwood.Core.Ui;
using Driftwood.Core.World;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>Which screen, if any, is over the world.</summary>
public enum HudScreenKind
{
    None,

    /// <summary>This character in this world: what is carried, worn, and what can be made.</summary>
    Player,

    /// <summary>This installation: keys, picture, sound, and the testing dials.</summary>
    Game,

    /// <summary>Dead, and told what by. One row out: waking where the bed says (#100).</summary>
    Death,

    /// <summary>
    /// Before anybody is playing: the world flies past underneath and a short menu sits on it.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Drawn by exactly the same code as <see cref="Game"/>, and that is why it was cheap.</b>
    /// A menu is a list of rows where up and down pick and enter acts — which is what a settings tab
    /// already is, down to the mouse hit-testing and the scrollbar. It has no tabs, so the tab strip
    /// draws nothing, and the title was already being drawn over that panel waiting for this.
    /// </remarks>
    Start,

    /// <summary>A station rather than a menu, so it has no tabs.</summary>
    Furnace,

    /// <summary>The other station: three by three, and the pockets under it.</summary>
    Bench,

    /// <summary>A chest: twenty seven slots over the pockets, and nothing else.</summary>
    Chest,

    /// <summary>A stonecutter: a rock, everything it cuts into, and one of them taken out.</summary>
    Stonecutter,
}

/// <summary>
/// One thing the game has to say, sitting in the corner until it fades.
/// </summary>
/// <param name="Icon">A texture layer to draw beside it, or -1 for none.</param>
public sealed class Toast(string title, string line, int icon, float life)
{
    public const float FadeSeconds = 0.6f;

    public string Title { get; } = title;
    public string Line { get; } = line;
    public int Icon { get; } = icon;
    public float Life { get; } = life;
    public float Age { get; set; }

    public bool Gone => Age >= Life;

    /// <summary>Full for most of its life, then out. Nothing appears part-way in.</summary>
    public float Alpha => Math.Clamp((Life - Age) / FadeSeconds, 0f, 1f);
}

/// <summary>The tabs of the player screen, in the order they are shown.</summary>
/// <remarks>
/// <para>Progress, handbook and map join this; the renderer is told names, not values.</para>
/// <para><b>Crafting is not one of them and never should have been.</b> A recipe book on its own
/// tab crafts into pockets that are not on screen, so what you just made goes somewhere you cannot
/// see — and every pack in the genre answers this for us by painting <c>recipe_book.png</c> at
/// exactly the height of <c>inventory.png</c>. Two panels drawn to one height are two panels meant
/// to stand side by side. The book folds out beside the pockets now, and what it makes lands in a
/// square that is already in view.</para>
/// </remarks>
public enum PlayerTab
{
    /// <summary>What is carried, what is worn, the two-by-two, and the book beside them.</summary>
    Items,

    /// <summary>What this player has done in this world, and what the recipe tree has revealed.</summary>
    Progress,

    /// <summary>Every item and the ways it is made, found, used, worn, burned, or placed.</summary>
    Handbook,

    /// <summary>The parts of this world the player has personally visited.</summary>
    Map,
}

/// <summary>The tabs of the game screen.</summary>
public enum GameTab
{
    Controls,
    Controller,
    Video,
    Audio,
    World,
    Saves,

    /// <summary>The shelf of texture packs, and the box that puts one on it.</summary>
    Packs,

    /// <summary>The local skin shelf, player lookup, and recent-public community feed.</summary>
    Skins,
}

/// <summary>
/// One line of a settings tab: what it is, and what it is currently set to.
/// </summary>
/// <param name="Heading">True for a group title, which has no value and cannot be selected.</param>
/// <param name="Note">A second, dimmer line under it — what a setting costs, or when it applies.</param>
/// <param name="Edits">
/// A line of typed text this row is the box for, drawn where the value would be.
/// </param>
/// <param name="Progress">
/// Zero through one draws a slim determinate bar along the row's foot; a negative value draws none.
/// </param>
/// <remarks>
/// ⛳ <b>A text field is a row, which is why it cost almost nothing.</b> Scrolling, hit testing, the
/// note strip and the way the keyboard walks the list are all already here and all already right; a
/// field of its own would have been a second one of each. It also means every screen that has rows
/// can have a box to type in without learning anything new.
/// </remarks>
public readonly record struct MenuRow(
    string Label, string Value = "", bool Heading = false, string Note = "", TextField? Edits = null,
    float Progress = -1f);

/// <summary>
/// Everything the overlay needs to know about the screen the player has open.
/// </summary>
/// <remarks>
/// <para>A class the host owns and fills in rather than a value passed by copy, because it holds
/// three lists that are rebuilt every frame and copying them around would be the only allocation
/// on the overlay path.</para>
/// <para>The rows are built by whoever knows what the settings mean, and this side only draws a
/// label and a value. That split is what keeps the renderer from growing a switch over every
/// setting in the game — and it is why the world tab, which is mostly read-outs, costs nothing
/// here at all.</para>
/// </remarks>
public sealed class HudScreen
{
    public HudScreenKind Kind;

    /// <summary>Which tab, as an index into <see cref="TabNames"/>.</summary>
    /// <remarks>
    /// An index and a list of names rather than an enum, so the renderer never learns what any
    /// particular tab means and a new one is a row in the host rather than a case in here.
    /// </remarks>
    public int Tab;

    public string[] TabNames = [];

    /// <summary>What this station can make, craftable or not.</summary>
    /// <remarks>
    /// Uncraftable recipes are listed rather than hidden, greyed out. A book that shows only what
    /// you can already afford answers "what now" and never answers "what for", and what a player
    /// wants from a recipe screen in this genre is mostly the second question.
    /// </remarks>
    public readonly List<Recipe> Recipes = [];

    /// <summary>Whether each of those can be paid for right now, in the same order.</summary>
    public readonly List<bool> Payable = [];

    /// <summary>The recipe book's live search, shelf, and craftable-now switch.</summary>
    public readonly TextField RecipeSearch = new(36) { Placeholder = "search" };

    public RecipeCategory RecipeCategory;

    public bool CraftableOnly;

    /// <summary>Immediate and recursively expanded cost of the selected recipe.</summary>
    public string[] RecipeCosts = [];

    /// <summary>Exploration map state. Pan is in chunks; zoom is layout units per tile.</summary>
    public WorldMap? Map;

    public Vector2 MapPlayer;

    public float MapFacing;

    public float MapZoom = 0.5f;

    public Vector2 MapPan;

    /// <summary>The rotatable skin preview on the SKINS tab, in degrees around the model.</summary>
    public float SkinPreviewYaw;

    /// <summary>Published geometry for the UI check; cleared whenever the preview is not drawn.</summary>
    public int SkinPreviewQuads;

    public Vector4 SkinPreviewBox;

    public Vector4 SkinPreviewBounds;

    /// <summary>The inventory figure's user-controlled turn, with a tiny idle turn added on draw.</summary>
    public float FigureYaw = -12f;

    /// <summary>Published shared-preview geometry for the inventory branch of --ui-check.</summary>
    public int FigureSkinFaces;

    public int FigureOuterFaces;

    public int FigureArmourFaces;

    public int FigureHeldItems;

    public float FigureArmWidth;

    public Vector4 FigureBox;

    public Vector4 FigureBounds;

    /// <summary>The current swing cooldown, so the crosshair can show when the next strike lands.</summary>
    public bool AttackCooling;

    public float AttackReady = 1f;

    /// <summary>Whether the controller's hold-to-pick nine-way hotbar is over the world.</summary>
    public bool RadialHotbar;

    /// <summary>The highlighted radial slot, or -1 while the stick is centred.</summary>
    public int RadialSlot = -1;

    /// <summary>Published geometry for the framebuffer check.</summary>
    public int RadialDrawnSlots;

    public Vector4 RadialBounds;

    /// <summary>The lines of whichever settings tab is open.</summary>
    public readonly List<MenuRow> Rows = [];

    /// <summary>Which recipe or row is picked out.</summary>
    public int Selected;

    /// <summary>The first row of a settings list actually on screen.</summary>
    /// <remarks>
    /// The list is capped at a readable number of lines and what does not fit is scrolled to, so
    /// this is a window onto <see cref="Rows"/> rather than a property of it.
    /// </remarks>
    public int Scroll;

    /// <summary>The hint along the bottom — what the keys do here, right now.</summary>
    public string Footer = "";

    /// <summary>
    /// The field the keyboard is currently going into, or null when it is driving the screen.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Typing has to be a mode, and there is no way around it.</b> Every letter on the keyboard
    /// already means something on a screen — E closes the pockets, W would walk — so a screen with a
    /// box on it cannot have both at once. While this is set, characters go into the box and the
    /// only keys the screen still hears are escape and enter, which are how somebody gets out.
    /// </remarks>
    public TextField? Typing;

    /// <summary>Seconds the screen has been up, for a caret that blinks.</summary>
    public float Clock;

    public Furnace? Burning;

    /// <summary>The squares in front of the player, when the open screen has any.</summary>
    public CraftingGrid? Grid;

    /// <summary>Whether the recipe book is folded out beside the panel.</summary>
    /// <remarks>Remembered across openings — a player who wants it open wants it open.</remarks>
    public bool BookOut;

    /// <summary>Which page of it.</summary>
    public int BookPage;

    /// <summary>
    /// What is on the cursor, between being picked up and being put down.
    /// </summary>
    /// <remarks>
    /// Its own state rather than a slot, because it is genuinely nowhere: not in the pockets, not in
    /// the grid, not on the floor. Which is also why closing a screen has to put it somewhere — see
    /// the note on the host's own close.
    /// </remarks>
    public ItemStack Carried;

    /// <summary>Where the pointer is, in layout units. The hotspot is the top left of the tile.</summary>
    public Vector2 Pointer;

    /// <summary>Where the code-authored cursor landed this frame, in layout units.</summary>
    /// <remarks>
    /// Zero-width while no screen is open. Published so the framebuffer check can read the pointer
    /// that was actually drawn instead of duplicating its scale and hotspot arithmetic.
    /// </remarks>
    public Vector4 CursorBox;

    /// <summary>What the pointer is over, refreshed each frame from the layout.</summary>
    public Zone? Hovered;

    /// <summary>
    /// Where the tooltip was drawn last frame, in layout units. Zero-width when there was none.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Published for the same reason <see cref="ScreenLayout"/> is: so a check reads where the
    /// box IS rather than where the constants say it should be.</b> A tooltip flips to the other side
    /// of the pointer near an edge, and a check sampling "twelve units down and right" read bare
    /// panel every time for a slot on the bottom row — it passed, on a two-count difference that was
    /// two panels rather than a box. This is one field and it turns a colour comparison into
    /// "was a box laid out at all, and what is in it".
    /// </remarks>
    public Vector4 TipBox;

    /// <summary>
    /// The middle of the first cell of the title the word FILLS, and of the first it leaves empty,
    /// in layout units. Negative when no title was drawn this frame.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Published because the title MOVES, and a check that works out where it should be lands
    /// on the backdrop.</b> Every letter of the word bobs and leans on its own phase — up to a
    /// little over half a cell — so the grid position of a filled cell says nothing whatever about
    /// which pixel it was drawn at. The check that reads the timber recomputed the grid from the
    /// same constants the renderer uses, ignored the bob, and read a cell's worth away: it passed
    /// for months on whatever happened to be there, and went red the day the backdrop behind that
    /// point was black rather than brown. Same fault and same fix as <see cref="TipBox"/>.
    /// </remarks>
    public Vector2 TitleInk = new(-1f, -1f), TitleGap = new(-1f, -1f);

    public bool IsOpen => Kind != HudScreenKind.None;

    /// <summary>
    /// True for the screens drawn on the pack's own container panel.
    /// </summary>
    /// <remarks>
    /// Those three carry the player's own pockets in their bottom half, which is why the bar along
    /// the bottom of the world is not drawn under them — it would be the same nine slots twice.
    /// </remarks>
    public bool IsContainer =>
        (Kind == HudScreenKind.Player && Tab == (int)PlayerTab.Items)
        || Kind is HudScreenKind.Bench or HudScreenKind.Furnace
             or HudScreenKind.Chest or HudScreenKind.Stonecutter;

    /// <summary>The rock on a stonecutter's bed, what it could become, and which was picked.</summary>
    /// <remarks>
    /// Held on the screen rather than beside the world, unlike a furnace or a chest — a stonecutter
    /// keeps nothing. What is on its bed goes back into the pockets when the screen shuts, exactly
    /// as a bench's grid does, because it belongs to the player rather than to the station.
    /// </remarks>
    /// <summary>Seconds, for anything on a screen that moves. Wound on by the client.</summary>
    /// <remarks>
    /// On the screen rather than passed into the draw, because it is a property of what is being
    /// shown rather than of one call — and because the same number twice draws the same picture,
    /// which is what lets a check read a moving thing back off the framebuffer.
    /// </remarks>
    public float Drift;

    public ItemStack Cutting;

    public readonly List<Recipe> Cuts = [];

    public int Cut;

    /// <summary>
    /// Which choosing station the chooser screen is asking for — the stonecutter's or the
    /// loom's. One screen, one field, two stations; the panel and every slot are shared.
    /// </summary>
    public CraftStation Choosing = CraftStation.Stonecutter;

    /// <summary>What is in the chest this screen is a screen of, or null when it is not one.</summary>
    /// <remarks>
    /// Held on the screen rather than looked up from the world every frame, for the same reason the
    /// crafting grid is: the renderer draws what the screen says it is showing, and a screen that
    /// went and asked the world would need to know where the chest was and what happens when it is
    /// no longer there.
    /// </remarks>
    public Chest? Stored;
}

/// <summary>
/// The screen-space layer: a crosshair, the hand, the hearts and the breath.
/// </summary>
/// <remarks>
/// <para>Built as a general quad batcher rather than as a crosshair with some extras bolted on,
/// because the next thing that needs it is an inventory screen and the one after that is a recipe
/// book. Everything on screen that is not the world goes through here: a rectangle, a colour, and
/// optionally a layer of one of two texture arrays.</para>
/// <para>Three batches, one per texture binding, because switching a texture inside a batch means
/// splitting it anyway. Untextured first — panels and frames sit under everything — then the block
/// array for hotbar contents, then the icons.</para>
/// <para>Sized in fixed units and scaled up to the window, the way the genre does it. A crosshair
/// specified in pixels is a speck on a tall display and a slab on a short one, and hearts drawn at
/// a fraction of the height jitter by a pixel every time the window is dragged.</para>
/// </remarks>
public sealed class HudRenderer : IDisposable
{
    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec3 aUvw;
        layout(location = 2) in vec4 aColor;

        uniform vec2 uScreen;

        out vec3 vUvw;
        out vec4 vColor;

        void main()
        {
            // Pixels with the origin at the top left, which is how a layout is written.
            vec2 ndc = vec2(aPos.x / uScreen.x * 2.0 - 1.0, 1.0 - aPos.y / uScreen.y * 2.0);
            gl_Position = vec4(ndc, 0.0, 1.0);
            vUvw = aUvw;
            vColor = aColor;
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        in vec3 vUvw;
        in vec4 vColor;

        uniform sampler2DArray uAtlas;
        uniform int uTextured;

        out vec4 FragColor;

        void main()
        {
            vec4 c = vColor;
            if (uTextured != 0) c *= texture(uAtlas, vUvw);
            if (c.a < 0.004) discard;
            FragColor = c;
        }
        """;

    /// <summary>The height the layout is written against. Everything scales off this.</summary>
    private const float DesignHeight = 480f;

    private float _lastScale;

    /// <summary>What the screen last threw, so the same fault is reported once and not every frame.</summary>
    private string _screenFault = "";

    /// <summary>Which screen kind the batch sizes were last reported for.</summary>
    private HudScreenKind _lastReported = HudScreenKind.None;

    /// <summary>
    /// How many screen pixels one layout unit is worth. A whole number, and never less than two
    /// unless the display genuinely cannot afford it.
    /// </summary>
    /// <remarks>
    /// <para>Rounded rather than floored, and that distinction is a bug this already had: at the
    /// default 1600x900 the window is 1.875 design heights tall, and flooring gave a scale of one —
    /// so the whole interface came out at half the size it was drawn for, a 232-unit panel adrift
    /// in a 1600-unit space. Rounding gives two, which is what 1.875 obviously means.</para>
    /// <para>Whole numbers because everything here is pixel art and a half step puts a two-pixel
    /// bevel on one and a half pixels, which the sampler resolves by blurring. One is allowed only
    /// on a display too short for two, where a blurry interface beats one that does not fit.</para>
    /// </remarks>
    public static float ScaleFor(int screenHeight) =>
        MathF.Max(1f, MathF.Round(screenHeight / DesignHeight));

    private const int Floats = 9;

    /// <summary>How many quads one batch can hold. The index buffer is built for exactly this many.</summary>
    private const int MaxQuads = 8192;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private readonly BlockTextureArray _icons;
    private BlockTextureArray _font;
    private int[] _advance;

    private readonly List<float> _backdrop = new(64);
    private readonly List<float> _plain = new(4096);
    private readonly List<float> _blocks = new(2048);
    private readonly List<float> _iconQuads = new(2048);
    private readonly List<float> _cursorQuads = new(64);
    private readonly List<float> _text = new(8192);
    private readonly List<float> _skinQuads = new(256);
    private readonly List<float> _previewQuads = new(512);

    private readonly List<float> _armourQuads = new(256);
    private readonly List<float> _guiUnder = new(512);
    private readonly List<float> _guiOver = new(512);

    /// <summary>The player's own sheet, as a single-layer array so the batcher can sample it.</summary>
    private BlockTextureArray? _skin;

    /// <summary>A candidate may be inspected without becoming the skin worn by the world model.</summary>
    private BlockTextureArray? _previewSkin;

    private ProjectedPlayerPreview? _wornPreview;
    private ProjectedPlayerPreview? _candidatePreview;
    private readonly List<ProjectedPlayerFace> _projectedSkinFaces = new(48);
    private readonly List<ProjectedPlayerFace> _projectedArmourFaces = new(48);

    private BlockTextureArray? _gui;
    private bool[] _guiPresent = [];

    /// <summary>
    /// Every armour sheet in one array, material-major, two layers each.
    /// </summary>
    /// <remarks>
    /// ⛳ Built once and not per material, because the figure has to draw a helmet of one metal over
    /// a chestplate of another in the same frame and a texture bound per piece would be nine binds
    /// inside one small window. It is the same array the world renderer holds as ten separate
    /// textures — this one is layered so the overlay's single batch can sample all of them.
    /// </remarks>
    private BlockTextureArray? _armour;

    private float[] _upload = new float[8192];

    /// <summary>Icon array layers. Digits run from <see cref="IconDigit"/> upward.</summary>
    private const int IconHeart = 0;
    private const int IconBubble = 1;
    private const int IconDigit = 2;
    private const int IconCursor = IconDigit + 10;

    /// <summary>The five worn-slot silhouettes, in <see cref="EquipSlot"/> order.</summary>
    private const int IconEquip = IconCursor + 1;

    /// <summary>A soft round bloom, drawn behind anything that gives off light.</summary>
    private const int IconBloom = IconEquip + 5;

    /// <summary>
    /// The plate the armour bar is counted in.
    /// </summary>
    /// <remarks>
    /// ⛔ Appended past the bloom rather than filed beside the heart, for exactly the reason the
    /// dyes cost this project a session: <b>the order these are added to the list IS the numbering
    /// these constants name</b>, so a tile slipped in among them moves every layer after it while
    /// nothing anywhere fails — the cursor would simply become a bubble.
    /// </remarks>
    private const int IconPlate = IconBloom + 1;

    /// <summary>
    /// The hunger bar's two drumsticks: the hollow socket, then the painted one over it.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Two tiles here where the health bar has one, and the art is the reason.</b> The heart
    /// arrived as an outline alone, so its middle is flooded and one tile serves both states under
    /// two tints. The drumstick arrived as a PAIR — a finished full-colour one and a hollow one — so
    /// there is nothing to derive and nothing to tint: each state is simply drawn.
    /// </remarks>
    private const int IconFoodSocket = IconPlate + 1;
    private const int IconFoodFull = IconFoodSocket + 1;

    public unsafe HudRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);

        var icons = new List<byte[]> { TileGen.Heart(), TileGen.BubbleTile() };
        icons.AddRange(TileGen.Digits());
        icons.Add(TileGen.Cursor());
        icons.AddRange(TileGen.EquipGhosts());
        icons.Add(TileGen.Bloom());
        icons.Add(TileGen.Plate());
        icons.Add(TileGen.DrumstickSocket());
        icons.Add(TileGen.DrumstickFull());
        _icons = new BlockTextureArray(gl, [.. icons], TileGen.Size);

        _font = new BlockTextureArray(gl, TileGen.Font(), TileGen.Size);
        _advance = TileGen.FontAdvance();

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_upload.Length * sizeof(float)), null, BufferUsageARB.StreamDraw);

        // One quad is four corners and six indices, and the pattern never varies.
        //
        // Eight thousand rather than two, and the old number was already close enough to be a bug
        // waiting for a long screen: the controls tab draws about thirty rows of a label and a value
        // and each glyph is TWO quads because of its shadow, which came to roughly two thousand on
        // its own. A batch past the end of this buffer draws indices that were never written.
        // Flush guards it as well, so the failure is a message rather than a wall of nothing.
        var indices = new uint[MaxQuads * 6];
        for (var q = 0; q < MaxQuads; q++)
        {
            var v = (uint)(q * 4);
            indices[q * 6] = v;
            indices[q * 6 + 1] = v + 1;
            indices[q * 6 + 2] = v + 2;
            indices[q * 6 + 3] = v;
            indices[q * 6 + 4] = v + 2;
            indices[q * 6 + 5] = v + 3;
        }

        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* p = indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);

        var stride = (uint)(Floats * sizeof(float));
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)(5 * sizeof(float)));

        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Lays the whole overlay out and draws it.
    /// </summary>
    /// <param name="layout">
    /// Filled in as the screen is laid out, and read afterwards by whoever is tracking the pointer.
    /// <b>One layout, built once, drawn and hit-tested from.</b> Writing the hit test from the same
    /// constants as the renderer is how a screen ends up with clicks landing half a square from its
    /// own pictures the first time either side is edited.
    /// </param>
    public void Draw(
        BlockTextureArray blocks,
        ItemRegistry catalogue,
        Inventory inventory,
        Equipment equipment,
        PlayerVitals vitals,
        HudScreen screen,
        ScreenLayout layout,
        IReadOnlyList<Toast> toasts,
        int screenWidth,
        int screenHeight)
    {
        _backdrop.Clear();
        _plain.Clear();
        _blocks.Clear();
        _iconQuads.Clear();
        _cursorQuads.Clear();
        _text.Clear();
        _skinQuads.Clear();
        _previewQuads.Clear();
        _armourQuads.Clear();
        _guiUnder.Clear();
        _guiOver.Clear();
        layout.Clear();

        screen.SkinPreviewQuads = 0;
        screen.SkinPreviewBox = Vector4.Zero;
        screen.SkinPreviewBounds = Vector4.Zero;
        screen.FigureSkinFaces = 0;
        screen.FigureOuterFaces = 0;
        screen.FigureArmourFaces = 0;
        screen.FigureHeldItems = 0;
        screen.FigureArmWidth = 0f;
        screen.FigureBox = Vector4.Zero;
        screen.FigureBounds = Vector4.Zero;
        screen.CursorBox = Vector4.Zero;
        screen.RadialDrawnSlots = 0;
        screen.RadialBounds = Vector4.Zero;

        // A whole number of screen pixels per layout unit, never a half. Everything here is pixel
        // art — a font drawn at twice its authored size, two-pixel bevels, hard edges — and all of
        // that depends on one layout unit landing on exactly one grid of pixels. At a half step the
        // bevels come out one and a half pixels wide, which the sampler resolves by blurring them,
        // and the whole interface goes soft in a way that reads as a low resolution rather than as
        // a deliberate one.
        var scale = ScaleFor(screenHeight);
        var w = MathF.Floor(screenWidth / scale);
        var h = MathF.Floor(screenHeight / scale);

        // Said once, and only when it changes. A layout that comes out the wrong size is invisible
        // to every check in the project — it draws, it just draws somewhere nobody is looking — so
        // the numbers that decide it are the ones worth being able to read back.
        if (Math.Abs(scale - _lastScale) > 0.01f)
        {
            _lastScale = scale;
            Console.WriteLine(
                $"overlay     {screenWidth}x{screenHeight} at {scale:F0}x, laid out in {w:F0}x{h:F0} units");
            Console.Out.Flush();
        }

        // A screen covers the world and the crosshair with it: a reticle over an inventory is
        // aiming at nothing, and it sits exactly where the eye is trying to read.
        //
        // Caught and reported rather than allowed to escape, because an exception thrown part way
        // through laying a screen out abandons every batch built so far and draws NOTHING AT ALL —
        // not the panel, not the backdrop, not the bar. From outside that is indistinguishable
        // from the screen never having opened, and the window keeps running, so nothing anywhere
        // says what happened. It is said once, with the kind, so the next frame is not a wall of it.
        if (screen.IsOpen)
        {
            try
            {
                Screen(catalogue, inventory, equipment, screen, layout, w, h);
            }
            catch (Exception ex) when (_screenFault != ex.GetType().Name + screen.Kind)
            {
                _screenFault = ex.GetType().Name + screen.Kind;
                Console.Error.WriteLine(
                    $"driftwood: the {screen.Kind} screen threw while drawing — {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine($"           {ex.StackTrace}");
            }
        }
        else
        {
            Crosshair(w, h);
            AttackIndicator(screen, w, h);
        }

        // A container panel carries the player's own pockets in its bottom half, so the bar along
        // the bottom of the world would be the same nine slots drawn twice, in two different sizes.
        // ⚠ And nobody is carrying anything before they have started: an empty bar under the menu
        // reads as a game somebody is already losing at.
        if (!screen.IsContainer && screen.Kind != HudScreenKind.Start)
        {
            Hotbar(catalogue, inventory, w, h);
            Offhand(catalogue, equipment, vitals, w, h);
        }

        if (!screen.IsOpen && screen.RadialHotbar)
            RadialPicker(catalogue, inventory, screen, w, h);

        if (!screen.IsOpen)
        {
            Hearts(vitals, screen.Drift, w, h);
            Food(vitals, screen.Drift, w, h);
            ArmourBar(vitals, w, h);
            Bubbles(vitals, screen.Drift, w, h);
        }

        Toasts(toasts, w);

        // Last, over everything, because a pointer that goes behind a panel is a pointer somebody
        // is about to lose. What is on the cursor rides under it, offset so the hotspot still reads.
        // ⚠ The tooltip goes UNDER the cursor and OVER everything else, in that order.
        if (screen.IsOpen)
        {
            Tip(catalogue, inventory, equipment, screen, layout, w, h);
            Pointer(catalogue, screen, layout);
        }

        // Said once per screen, on the frame it opens. "It is not appearing" and "it is appearing
        // somewhere I am not looking" are different faults with the same symptom, and the only
        // thing that tells them apart is whether any geometry was built at all.
        if (screen.Kind != _lastReported)
        {
            _lastReported = screen.Kind;
            if (screen.IsOpen)
            {
                Console.WriteLine(
                    $"overlay     {screen.Kind} screen: {_plain.Count / (Floats * 4)} panels, "
                    + $"{_text.Count / (Floats * 4)} glyphs, {_blocks.Count / (Floats * 4)} icons, "
                    + $"{screen.Recipes.Count} recipes, {screen.Rows.Count} rows, "
                    + $"{screen.TabNames.Length} tabs");
                Console.Out.Flush();
            }
        }

        _shader.Use();
        _shader.SetVec2("uScreen", new Vector2(w, h));
        _shader.SetInt("uAtlas", 0);

        // Culling OFF, and this is the line the whole overlay was missing.
        //
        // The world pass turns on back-face culling with counter-clockwise fronts and leaves it on.
        // Rect lays its corners out top-left, top-right, bottom-right, bottom-left — clockwise on a
        // screen whose y grows downward — and the vertex shader flips y to reach NDC, which leaves
        // every quad wound clockwise there. Clockwise is the back face, so the driver threw away
        // every panel, every glyph, the crosshair and the whole bar, reported no error, and drew a
        // window with nothing in it. Every count on our side read correct throughout.
        //
        // Disabled rather than re-wound, for the same reason BlockCracks disables it: a
        // two-dimensional overlay has no facing to get right, and leaving it to a corner order that
        // has to survive a y-flip is how this happened in the first place.
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.BindVertexArray(_vao);

        Flush(_backdrop, textured: false, null);
        Flush(_guiUnder, textured: true, _gui);
        Flush(_plain, textured: false, null);
        Flush(_skinQuads, textured: true, _skin);
        Flush(_previewQuads, textured: true, _previewSkin);

        // ⚠ After the skin and before the items, because these three are one picture in painter's
        // order: a plate goes over the body it is worn on and under the thing the hand is holding.
        Flush(_armourQuads, textured: true, _armour);
        Flush(_blocks, textured: true, blocks);
        Flush(_iconQuads, textured: true, _icons);
        Flush(_guiOver, textured: true, _gui);
        Flush(_text, textured: true, _font);

        // ⛔ THE POINTER OWNS A PASS, NOT A PLACE IN THE METHOD. Pointer() is called after every
        // other builder, but painter's order is the order these BATCHES FLUSH — while it shared the
        // icon batch, a packed GUI overlay and every glyph were still painted over it. This is the
        // final flush on purpose: carried items, their counts, tooltips and all pack chrome stay
        // beneath the one thing somebody must never lose against the screen.
        Flush(_cursorQuads, textured: true, _icons);

        _gl.BindVertexArray(0);
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);

        // Said once. A silent GL error is the one failure mode where every count on our side reads
        // correct and nothing arrives on the screen, which is exactly the shape of fault that sends
        // somebody hunting through layout arithmetic for an afternoon.
        var error = _gl.GetError();
        if (error == GLEnum.NoError || _reportedError == error) return;

        _reportedError = error;
        Console.Error.WriteLine($"driftwood: the overlay's draw reported {error}");
    }

    private GLEnum _reportedError = GLEnum.NoError;

    /// <summary>Whether a batch has ever wanted more quads than the index buffer holds.</summary>
    private bool _overflowed;

    private unsafe void Flush(List<float> batch, bool textured, BlockTextureArray? atlas)
    {
        if (batch.Count == 0) return;

        // Held to what the index buffer was built for. Drawing past it reads indices nobody wrote,
        // which on some drivers is a black screen and on others is a crash — and either way the
        // counts on our side all read correct. Said once, with the number, so it is findable.
        if (batch.Count > MaxQuads * Floats * 4)
        {
            if (!_overflowed)
            {
                _overflowed = true;
                Console.Error.WriteLine(
                    $"driftwood: the overlay wanted {batch.Count / (Floats * 4)} quads in one batch "
                    + $"and can draw {MaxQuads}; the rest of it is not on screen");
            }

            batch.RemoveRange(MaxQuads * Floats * 4, batch.Count - MaxQuads * Floats * 4);
        }

        if (_upload.Length < batch.Count) _upload = new float[batch.Count * 2];
        batch.CopyTo(_upload);

        _shader.SetInt("uTextured", textured ? 1 : 0);
        atlas?.Bind();

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_upload.Length * sizeof(float)), null, BufferUsageARB.StreamDraw);
        fixed (float* p = _upload)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(batch.Count * sizeof(float)), p);

        var quads = batch.Count / (Floats * 4);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)(quads * 6), DrawElementsType.UnsignedInt, (void*)0);
    }

    /// <summary>
    /// A white cross over a dark one.
    /// </summary>
    /// <remarks>
    /// Two colours rather than one, because a plain white crosshair vanishes against snow and a
    /// plain dark one vanishes down a cave, and those are the two places a player spends the most
    /// time aiming carefully. An outline costs four more quads and works against both.
    /// </remarks>
    private void Crosshair(float w, float h)
    {
        var cx = MathF.Round(w / 2f);
        var cy = MathF.Round(h / 2f);
        var shadow = new Vector4(0f, 0f, 0f, 0.55f);
        var bright = new Vector4(1f, 1f, 1f, 0.85f);

        Rect(_plain, cx - 6f, cy - 1f, 12f, 2f, shadow);
        Rect(_plain, cx - 1f, cy - 6f, 2f, 12f, shadow);
        Rect(_plain, cx - 5f, cy - 0.5f, 10f, 1f, bright);
        Rect(_plain, cx - 0.5f, cy - 5f, 1f, 10f, bright);
    }

    private void AttackIndicator(HudScreen screen, float w, float h)
    {
        if (!screen.AttackCooling) return;

        var ready = Math.Clamp(screen.AttackReady, 0f, 1f);
        var x = MathF.Round(w * 0.5f - 8f);
        var y = MathF.Round(h * 0.5f + 9f);

        if (HasGui(GuiTextureSet.Layer.AttackBackground))
            Rect(_guiOver, x, y, 16f, 4f, Vector4.One, (int)GuiTextureSet.Layer.AttackBackground);
        else
            Rect(_plain, x, y, 16f, 3f, new Vector4(0f, 0f, 0f, 0.75f));

        if (ready <= 0f) return;
        if (HasGui(GuiTextureSet.Layer.AttackProgress))
            Rect(_guiOver, x, y, 16f * ready, 4f, Vector4.One,
                (int)GuiTextureSet.Layer.AttackProgress, ready);
        else
            Rect(_plain, x + 1f, y + 1f, 14f * ready, 1f,
                new Vector4(0.92f, 0.92f, 0.84f, 1f));
    }

    /// <summary>The bar, one slot per pocket, with each block's own tile and its count.</summary>
    private void Hotbar(ItemRegistry catalogue, Inventory inventory, float w, float h)
    {
        if (HasGui(GuiTextureSet.Layer.Hotbar))
        {
            SkinnedHotbar(catalogue, inventory, w, h);
            return;
        }

        // ⛳ The same number the bars over it hang off — see BarSpan. One source, so the rack and the
        // rows above it cannot drift apart.
        const float Slot = HotbarSlot;
        const float Pad = 2f;

        var width = Inventory.HotbarSlots * Slot;
        var left = MathF.Round((w - width) / 2f);
        var top = MathF.Round(h - Slot - 8f);

        Bevel(left - 3f, top - 3f, width + 6f, Slot + 6f, raised: true, PanelFill);

        // The bar is the first nine of the inventory, not a container of its own — so this draws a
        // window onto the same array the backpack lives in, and dragging between the two is a move
        // between indices rather than a transfer.
        for (var i = 0; i < Inventory.HotbarSlots; i++)
        {
            var x = left + i * Slot;

            // Each pocket pressed into the bar, so the bar reads as a rack of them rather than as
            // nine rectangles drawn on one.
            Bevel(x + 1f, top + 1f, Slot - 2f, Slot - 2f, raised: false, SlotFill);

            // The one in hand gets a lit edge rather than a fill, so the icon in it is not tinted.
            if (i == inventory.Selected) Select(x + 1f, top + 1f, Slot - 2f, Slot - 2f);

            var stack = inventory[i];
            if (stack.IsEmpty) continue;

            var type = catalogue[stack.Item];
            SlotIcon(catalogue, stack, x + Pad, top + Pad, Slot - Pad * 2f, Vector4.One);

            // How much life a tool has left, as a bar across the bottom of its slot. A count would
            // say nothing — a tool is always one — and wear is the only thing about it that changes.
            if (type.Durability > 0 && stack.Damage > 0)
            {
                var life = 1f - stack.Damage / (float)type.Durability;
                Rect(_plain, x + Pad, top + Slot - 4f, Slot - Pad * 2f, 2f, new Vector4(0f, 0f, 0f, 0.8f));
                Rect(_plain, x + Pad, top + Slot - 4f, (Slot - Pad * 2f) * life, 2f,
                    new Vector4(1f - life, 0.25f + life * 0.65f, 0.2f, 1f));
            }

            // Counts sit in the bottom right of the slot, right-aligned, one digit at a time. A
            // single item shows no number: nine slots each labelled "1" is noise, not information.
            if (stack.Count <= 1) continue;
            Number(stack.Count, x + Slot - 1.5f, top + Slot - 8.5f);
        }
    }

    private void SkinnedHotbar(ItemRegistry catalogue, Inventory inventory, float w, float h)
    {
        const float width = 182f;
        const float height = 22f;
        const float pitch = 20f;
        const float icon = 16f;

        var left = MathF.Round((w - width) * 0.5f);
        var top = MathF.Round(h - height - 8f);
        Rect(_guiUnder, left, top, width, height, Vector4.One, (int)GuiTextureSet.Layer.Hotbar);

        if (HasGui(GuiTextureSet.Layer.HotbarSelection))
            Rect(_guiOver, left - 1f + inventory.Selected * pitch, top - 1f, 24f, 23f,
                Vector4.One, (int)GuiTextureSet.Layer.HotbarSelection);

        for (var i = 0; i < Inventory.HotbarSlots; i++)
        {
            var stack = inventory[i];
            if (stack.IsEmpty) continue;

            var x = left + 3f + i * pitch;
            var y = top + 3f;
            var type = catalogue[stack.Item];
            SlotIcon(catalogue, stack, x, y, icon, Vector4.One);

            if (type.Durability > 0 && stack.Damage > 0)
            {
                var life = 1f - stack.Damage / (float)type.Durability;
                Rect(_plain, x, top + 19f, icon, 2f, new Vector4(0f, 0f, 0f, 0.8f));
                Rect(_plain, x, top + 19f, icon * life, 2f,
                    new Vector4(1f - life, 0.25f + life * 0.65f, 0.2f, 1f));
            }

            if (stack.Count > 1) Number(stack.Count, x + icon + 1f, top + 14f);
        }
    }

    /// <summary>Nine real hotbar slots around the right stick's nine deterministic wedges.</summary>
    private void RadialPicker(
        ItemRegistry catalogue, Inventory inventory, HudScreen screen, float w, float h)
    {
        const float slot = 22f;
        const float radius = 48f;
        var centre = new Vector2(MathF.Round(w * 0.5f), MathF.Round(h * 0.52f));
        var shade = new Vector4(0.02f, 0.025f, 0.03f, 0.86f);

        // A quiet centre makes the ring legible over snow and caves alike without turning it into a
        // rectangular menu. The nine pockets themselves carry the shape.
        Bevel(centre.X - 26f, centre.Y - 10f, 52f, 20f, raised: true, shade);

        for (var i = 0; i < Inventory.HotbarSlots; i++)
        {
            var angle = -MathF.PI * 0.5f + MathF.Tau * i / Inventory.HotbarSlots;
            var x = MathF.Round(centre.X + MathF.Cos(angle) * radius - slot * 0.5f);
            var y = MathF.Round(centre.Y + MathF.Sin(angle) * radius - slot * 0.5f);
            Bevel(x, y, slot, slot, raised: false, SlotFill with { W = 0.96f });
            if (i == screen.RadialSlot) Select(x, y, slot, slot);

            var stack = inventory[i];
            if (!stack.IsEmpty)
            {
                SlotIcon(catalogue, stack, x + 3f, y + 3f, slot - 6f, Vector4.One);
                if (stack.Count > 1) Number(stack.Count, x + slot - 1f, y + slot - 7f, 5f);
            }

            screen.RadialDrawnSlots++;
        }

        var selected = Math.Clamp(screen.RadialSlot, 0, Inventory.HotbarSlots - 1);
        var held = inventory[selected];
        var words = held.IsEmpty ? $"slot {selected + 1}" : catalogue[held.Item].Label;
        if (words.Length > 17) words = words[..16] + "…";
        TextCentred(words, centre.X, centre.Y - 4f, 7f, Highlight);
        screen.RadialBounds = new Vector4(
            centre.X - radius - slot * 0.5f, centre.Y - radius - slot * 0.5f,
            radius * 2f + slot, radius * 2f + slot);
    }

    /// <summary>
    /// A screen over the world: what can be made here, and what the selected one costs.
    /// </summary>
    /// <remarks>
    /// <para>There is no text renderer yet, so nothing here is labelled and nothing needs to be.
    /// A recipe is a picture of its ingredients and its result — which is what a recipe is anyway —
    /// and the arrangement in the panel is the arrangement in the grid, so the screen teaches the
    /// bench rather than describing it.</para>
    /// <para>Uncraftable recipes are drawn dark rather than left out. Half the value of the screen
    /// is seeing that a stormglass pickaxe exists long before there is any stormglass.</para>
    /// </remarks>
    private void Screen(
        ItemRegistry catalogue,
        Inventory inventory,
        Equipment equipment,
        HudScreen screen,
        ScreenLayout layout,
        float w,
        float h)
    {
        Rect(_backdrop, 0f, 0f, w, h, new Vector4(0.04f, 0.04f, 0.04f, 0.72f));

        // The three container screens are drawn on the pack's own panel, share every square below
        // the halfway line, and carry the recipe book beside them. Everything else is settings.
        if (screen.IsContainer)
        {
            Container(catalogue, inventory, equipment, screen, layout, w, h);
            Footer(screen, w, h);
            return;
        }

        if (screen.Kind == HudScreenKind.Player && screen.Tab == (int)PlayerTab.Map)
        {
            MapScreen(screen, layout, w, h);
            Footer(screen, w, h);
            return;
        }

        if (screen.Kind == HudScreenKind.Game && screen.Tab == (int)GameTab.Skins)
        {
            SkinScreen(screen, layout, w, h);
            Footer(screen, w, h);
            return;
        }

        var panel = screen.Kind == HudScreenKind.Game ? GameMenuPanel : MenuPanel;
        var left = MathF.Round((w - panel) / 2f);

        // Sat where it is tall rather than always a fixed way down the screen.
        var tall = 22f + Math.Min(screen.Rows.Count, ScreenLayout.MenuLines(h)) * ScreenLayout.MenuLine + 12f;
        var top = MathF.Round((h - tall) * 0.42f);

        // The name, over the panel. ⛳ It belongs on the start screen and the start screen does not
        // exist yet, so it lives here meanwhile — which is also somewhere it is worth having: a
        // paused game is the other place a title reads as a title rather than as decoration.
        var cell = TitleCell(w);
        var titleTop = MathF.Max(6f, top - TitleArt.LetterHeight * cell - 26f);
        Title(screen, w * 0.5f, titleTop, cell, screen.Drift);

        Tabs(screen, layout, left, top, panel);
        Rows(screen, layout, left, top + 22f, panel, h);

        Footer(screen, w, h);
    }

    /// <summary>The skin shelf's rows beside a real, rotatable projection of the player model.</summary>
    private void SkinScreen(HudScreen screen, ScreenLayout layout, float w, float h)
    {
        const float rowsWide = MenuPanel;
        const float gap = 14f;
        const float previewWide = 132f;
        const float totalWide = GameMenuPanel;

        var shown = Math.Min(screen.Rows.Count, ScreenLayout.MenuLines(h));
        var tall = 22f + shown * ScreenLayout.MenuLine + 12f;
        var left = MathF.Round((w - totalWide) * 0.5f);
        var top = MathF.Round((h - tall) * 0.42f);

        var cell = TitleCell(w);
        var titleTop = MathF.Max(6f, top - TitleArt.LetterHeight * cell - 26f);
        Title(screen, left + totalWide * 0.5f, titleTop, cell, screen.Drift);

        Tabs(screen, layout, left, top, totalWide);
        Rows(screen, layout, left, top + 22f, rowsWide, h);

        var px = left + rowsWide + gap;
        var py = top + 20f;
        var ph = MathF.Max(126f, shown * ScreenLayout.MenuLine + 10f);
        Frame(px - 4f, py - 4f, previewWide + 8f, ph + 8f);
        Rect(_plain, px, py, previewWide, ph, new Vector4(0.075f, 0.08f, 0.09f, 1f));
        layout.Add(ZoneKind.SkinPreview, 0, px, py, previewWide, ph);

        screen.SkinPreviewBox = new Vector4(px, py, previewWide, ph);
        DrawSkinPreview(screen, px, py, previewWide, ph);

        TextCentred(PreviewFacing(screen.SkinPreviewYaw), px + previewWide * 0.5f, py + ph - 10f, 8f, InkDim);
    }

    private static string PreviewFacing(float degrees)
    {
        var turn = ((degrees % 360f) + 360f) % 360f;
        return turn switch
        {
            < 45f or >= 315f => "front",
            < 135f => "side",
            < 225f => "back",
            _ => "side",
        };
    }

    /// <summary>Projects the candidate through the same camera the inventory figure now uses.</summary>
    private void DrawSkinPreview(HudScreen screen, float x, float y, float w, float h)
    {
        if (_previewSkin is null || _candidatePreview is null) return;

        var measure = DrawPlayerPreview(
            _candidatePreview, _previewQuads,
            x, y, w, h, screen.SkinPreviewYaw, screen.Drift, bottomInset: 16f,
            ReadOnlySpan<int>.Empty);

        screen.SkinPreviewQuads = measure.SkinFaces;
        screen.SkinPreviewBounds = measure.Bounds;
    }

    /// <summary>
    /// The one screen-space player renderer. SKINS supplies a candidate with no equipment; inventory
    /// supplies the worn model and material indices. Geometry, pose, camera, culling and lighting are
    /// otherwise identical.
    /// </summary>
    private ProjectedPlayerMeasure DrawPlayerPreview(
        ProjectedPlayerPreview model,
        List<float> skinBatch,
        float x,
        float y,
        float w,
        float h,
        float yaw,
        float drift,
        float bottomInset,
        ReadOnlySpan<int> armourMaterials)
    {
        var measure = model.Project(
            x, y, w, h, yaw, drift, bottomInset, armourMaterials,
            _projectedSkinFaces, _projectedArmourFaces);

        foreach (var face in _projectedSkinFaces)
            DrawProjected(skinBatch, face, armour: false);

        foreach (var face in _projectedArmourFaces)
            DrawProjected(_armourQuads, face, armour: true);

        return measure;
    }

    private void DrawProjected(List<float> into, in ProjectedPlayerFace face, bool armour)
    {
        var va = armour ? face.Ua.Y * _armourV : face.Ua.Y;
        var vb = armour ? face.Ub.Y * _armourV : face.Ub.Y;
        var vc = armour ? face.Uc.Y * _armourV : face.Uc.Y;
        var vd = armour ? face.Ud.Y * _armourV : face.Ud.Y;

        Quad(into, face.Layer, face.Tint,
            face.A.X, face.A.Y, face.Ua.X, va,
            face.B.X, face.B.Y, face.Ub.X, vb,
            face.C.X, face.C.Y, face.Uc.X, vc,
            face.D.X, face.D.Y, face.Ud.X, vd);
    }

    /// <summary>The visited surface, centred on the player unless it has been panned away.</summary>
    private void MapScreen(HudScreen screen, ScreenLayout layout, float w, float h)
    {
        var size = MathF.Floor(MathF.Min(300f, MathF.Min(w - 36f, h - 105f)));
        var left = MathF.Round((w - size) * 0.5f);
        var top = MathF.Round((h - size) * 0.44f);

        Tabs(screen, layout, left, top - 22f, size);
        Bevel(left - 4f, top - 4f, size + 8f, size + 8f, raised: true, PanelFill);
        Rect(_plain, left, top, size, size, new Vector4(0.06f, 0.07f, 0.07f, 1f));
        layout.Add(ZoneKind.Map, 0, left, top, size, size);

        var zoom = Math.Clamp(screen.MapZoom, 0.25f, 4f);
        var centre = screen.MapPlayer + screen.MapPan * Chunk.Size;
        var stride = Math.Max(1, (int)MathF.Ceiling(4f / zoom));
        var pitch = stride * zoom;

        if (screen.Map is { } map)
        {
            foreach (var tile in map.Tiles)
            {
                if (Mod(tile.X, stride) != 0 || Mod(tile.Z, stride) != 0) continue;
                var x = MathF.Round(left + size * 0.5f + (tile.X + stride * 0.5f - centre.X) * zoom);
                var y = MathF.Round(top + size * 0.5f + (tile.Z + stride * 0.5f - centre.Y) * zoom);
                if (x + pitch < left || y + pitch < top || x >= left + size || y >= top + size) continue;

                var shade = Math.Clamp(0.84f + (tile.Height - TerrainGenerator.SeaLevel) * 0.008f, 0.68f, 1.12f);
                var colour = MapColour(tile.Top, tile.Biome) * new Vector4(shade, shade, shade, 1f);
                colour.W = 1f;
                Rect(_plain, x, y, MathF.Max(2f, pitch - 1f), MathF.Max(2f, pitch - 1f), colour);
            }

            Text($"{map.Tiles.Count:N0} explored", left + 4f, top + size + 10f, 8f, InkDim);
        }

        // A bright square and a short nose: position and facing, both legible over every biome.
        var px = left + size * 0.5f - screen.MapPan.X * Chunk.Size * zoom;
        var py = top + size * 0.5f - screen.MapPan.Y * Chunk.Size * zoom;
        Rect(_plain, px - 3f, py - 3f, 6f, 6f, new Vector4(1f, 0.92f, 0.38f, 1f));
        var yaw = float.DegreesToRadians(screen.MapFacing);
        var dx = MathF.Cos(yaw) * 9f;
        var dy = MathF.Sin(yaw) * 9f;
        MapNeedle(px, py, dx, dy);

        TextCentred(
            $"x {screen.MapPlayer.X:F0}  z {screen.MapPlayer.Y:F0}  {zoom:0.##} px/block",
            left + size * 0.5f, top + size + 10f, 8f, InkDim);
    }

    private void MapNeedle(float x, float y, float dx, float dy)
    {
        var steps = (int)MathF.Max(MathF.Abs(dx), MathF.Abs(dy));
        for (var i = 1; i <= steps; i++)
            Rect(_plain, MathF.Round(x + dx * i / steps), MathF.Round(y + dy * i / steps), 2f, 2f,
                new Vector4(1f, 0.92f, 0.38f, 1f));
    }

    private static int Mod(int value, int by) => ((value % by) + by) % by;

    private static Vector4 MapColour(WorldMap.Surface surface, Biome biome) => surface switch
    {
        WorldMap.Surface.Water => biome == Biome.FrozenSea
            ? new Vector4(0.57f, 0.75f, 0.82f, 1f)
            : new Vector4(0.15f, 0.36f, 0.62f, 1f),
        WorldMap.Surface.Snow => new Vector4(0.86f, 0.90f, 0.91f, 1f),
        WorldMap.Surface.Sand => new Vector4(0.79f, 0.68f, 0.40f, 1f),
        WorldMap.Surface.Stone => new Vector4(0.43f, 0.45f, 0.43f, 1f),
        WorldMap.Surface.Wood => new Vector4(0.40f, 0.29f, 0.17f, 1f),
        WorldMap.Surface.Soil => new Vector4(0.36f, 0.28f, 0.17f, 1f),
        WorldMap.Surface.Other => MapBiomeColour(biome),
        _ => MapBiomeColour(biome),
    };

    private static Vector4 MapBiomeColour(Biome biome) => biome switch
    {
        Biome.Sea => new Vector4(0.15f, 0.36f, 0.62f, 1f),
        Biome.FrozenSea => new Vector4(0.57f, 0.75f, 0.82f, 1f),
        Biome.Shore => new Vector4(0.76f, 0.69f, 0.43f, 1f),
        Biome.Dunes => new Vector4(0.82f, 0.67f, 0.34f, 1f),
        Biome.Marsh => new Vector4(0.30f, 0.45f, 0.28f, 1f),
        Biome.Snowfield => new Vector4(0.86f, 0.90f, 0.91f, 1f),
        Biome.Tundra => new Vector4(0.61f, 0.70f, 0.60f, 1f),
        Biome.CherryGrove => new Vector4(0.68f, 0.50f, 0.55f, 1f),
        Biome.Woods => new Vector4(0.16f, 0.36f, 0.18f, 1f),
        Biome.Drylands => new Vector4(0.61f, 0.52f, 0.27f, 1f),
        Biome.Highlands => new Vector4(0.42f, 0.48f, 0.36f, 1f),
        Biome.Meadow => new Vector4(0.40f, 0.64f, 0.31f, 1f),
        _ => new Vector4(0.28f, 0.51f, 0.25f, 1f),
    };

    /// <summary>A single joined tab rail, with the open section carried by the mint state colour.</summary>
    private void Tabs(HudScreen screen, ScreenLayout layout, float left, float top, float panel)
    {
        var names = screen.TabNames;
        if (names.Length == 0) return;

        var widths = new float[names.Length];
        var natural = 0f;
        for (var i = 0; i < names.Length; i++)
        {
            widths[i] = MathF.Round(TextWidth(names[i], 8f)) + 10f;
            natural += widths[i];
        }

        // Fill a wider rail rather than leaving an orphaned rule to its right. On an unusually
        // narrow surface the same calculation contracts every tab proportionally and FitText below
        // is the final guard; no tab is ever allowed to leave the panel it belongs to.
        if (natural <= panel)
        {
            var extra = (panel - natural) / names.Length;
            for (var i = 0; i < widths.Length; i++) widths[i] += extra;
        }
        else
        {
            var scale = panel / natural;
            for (var i = 0; i < widths.Length; i++) widths[i] *= scale;
        }

        var pen = left;

        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];
            var width = i == names.Length - 1
                ? left + panel - pen
                : MathF.Round(widths[i]);
            var open = screen.Tab == i;
            var hot = screen.Hovered is { Kind: ZoneKind.Tab } over && over.Index == i;
            var layer = open
                ? hot ? GuiTextureSet.Layer.TabSelectedHighlighted : GuiTextureSet.Layer.TabSelected
                : hot ? GuiTextureSet.Layer.TabHighlighted : GuiTextureSet.Layer.Tab;

            if (HasGui(layer))
                NineSlice(_guiUnder, pen, top, width, 18f, layer, 130f, 24f, 4f);
            else
            {
                var fill = open ? PanelFill : new Vector4(0.22f, 0.22f, 0.22f, 0.97f);
                Rect(_plain, pen, top, width, 18f, fill);
                Rect(_plain, pen, top, width, 2f, open ? PanelLight : PanelDark);
                Rect(_plain, pen, top, 1f, 18f, i == 0 ? PanelLight : PanelDark);
                if (i == names.Length - 1)
                    Rect(_plain, pen + width - 1f, top, 1f, 18f, PanelDark);
            }
            Rect(_plain, pen, top + 16f, width, 2f, open ? Picked : PanelDark);

            var shown = FitText(name, MathF.Max(1f, width - 8f), 8f);
            TextCentred(shown, pen + width * 0.5f, top + 4f, 8f,
                open ? Highlight : InkFaint);

            layout.Add(ZoneKind.Tab, i, pen, top, width, 18f);
            pen += width;
        }
    }

    /// <summary>
    /// A settings tab: a label on the left and what it is set to on the right.
    /// </summary>
    /// <remarks>
    /// <para>A window onto the list rather than the whole of it. The controls tab is twenty eight
    /// rows and grows by one with every binding added, so drawn at its full length it is a panel
    /// that eventually runs off the bottom of the window — and a panel that has run off the bottom
    /// looks exactly like a panel that is fine, from everywhere except the row nobody can reach.
    /// </para>
    /// <para>Only the rows actually on screen get a zone, so a click can never land on one that has
    /// been scrolled past — the same property that makes the layout worth having in the first place,
    /// falling out of building it from what was drawn.</para>
    /// </remarks>
    private void Rows(HudScreen screen, ScreenLayout layout, float left, float top, float panel, float h)
    {
        const float Line = ScreenLayout.MenuLine;

        var total = screen.Rows.Count;
        var lines = ScreenLayout.MenuLines(h);
        var shown = Math.Min(lines, total);
        var first = Math.Clamp(screen.Scroll, 0, Math.Max(0, total - lines));

        var surface = screen.Kind == HudScreenKind.Start
            ? GuiTextureSet.Layer.MenuListBackground
            : GuiTextureSet.Layer.OptionsBackground;
        SurfaceFrame(left - 4f, top - 4f, panel + 8f, shown * Line + 12f, surface);

        if (total > lines) Scrollbar(screen, layout, left + panel + 6f, top - 2f, shown * Line + 8f, first, lines, total);

        for (var i = first; i < first + shown; i++)
        {
            var row = screen.Rows[i];
            var y = top + (i - first) * Line + 2f;

            if (row.Heading)
            {
                Rect(_plain, left - 2f, y - 1f, panel + 4f, Line - 1f,
                    new Vector4(0.17f, 0.17f, 0.17f, 0.72f));
                Rect(_plain, left - 2f, y - 1f, 2f, Line - 1f, Picked with { W = 0.70f });
                Text(FitText(row.Label, panel - 8f, 8f), left + 4f, y, 8f, Highlight);
                continue;
            }

            var lit = i == screen.Selected;
            var hot = screen.Hovered is { Kind: ZoneKind.Row } over && over.Index == i;

            var button = lit || hot
                ? GuiTextureSet.Layer.WidgetButtonHighlighted
                : GuiTextureSet.Layer.WidgetButton;
            if (HasGui(button))
                NineSlice(_guiUnder, left - 2f, y - 2f, panel + 4f, Line,
                    button, 200f, 20f, 4f,
                    new Vector4(1f, 1f, 1f, lit ? 1f : hot ? 0.94f : 0.72f));
            else if (lit)
                Bevel(left - 2f, y - 2f, panel + 4f, Line, raised: false,
                    new Vector4(0.27f, 0.27f, 0.27f, 0.98f));
            else if (hot)
                Rect(_plain, left - 2f, y - 2f, panel + 4f, Line, new Vector4(1f, 1f, 1f, 0.10f));

            if (lit)
            {
                Rect(_plain, left - 2f, y - 2f, 2f, Line, Picked);
                Rect(_plain, left, y + Line - 3f, panel + 2f, 1f, Picked with { W = 0.50f });
            }

            if (row.Progress >= 0f)
            {
                var progress = Math.Clamp(row.Progress, 0f, 1f);
                Rect(_plain, left + 2f, y + Line - 5f, panel - 4f, 2f,
                    new Vector4(0.08f, 0.08f, 0.08f, 0.80f));
                if (progress > 0f)
                    Rect(_plain, left + 2f, y + Line - 5f, (panel - 4f) * progress, 2f,
                        new Vector4(0.82f, 0.92f, 0.58f, 0.95f));
            }

            var boxWidth = 0f;
            if (row.Edits is { } field) boxWidth = Box(screen, field, left, y, panel);
            var valueText = "";
            var valueWidth = 0f;
            if (row.Edits is null && row.Value.Length > 0)
            {
                valueText = FitText(row.Value, MathF.Min(154f, panel * 0.43f), 8f);
                valueWidth = TextWidth(valueText, 8f);
                Text(valueText, left + panel - valueWidth - 4f, y, 8f,
                    lit ? Ink : InkDim);
            }

            var reserved = boxWidth > 0f ? boxWidth + 8f : valueWidth > 0f ? valueWidth + 12f : 6f;
            var labelWidth = MathF.Max(8f, panel - reserved - 8f);
            Text(FitText(row.Label, labelWidth, 8f), left + 6f, y, 8f,
                lit ? Vector4.One : InkDim);

            // A heading has no zone at all, so the pointer cannot land on something the keyboard
            // deliberately skips over. The row's whole width is clickable, not just its words.
            layout.Add(ZoneKind.Row, i, left - 2f, y - 2f, panel + 4f, Line);

            // ⚠ After the row, never before: the later zone is the one on top, and a box that its
            // own row covered would be a box a click can never land in.
            if (boxWidth > 0f)
                layout.Add(ZoneKind.Field, i, left + panel - boxWidth, y - 2f, boxWidth, Line);
        }

        Note(screen, left, top - 4f + shown * Line + 16f, panel, first, shown);
    }

    /// <summary>
    /// A sunken box on the right of a row, holding what has been typed into it.
    /// </summary>
    /// <remarks>
    /// <para><b>Sunken, where every other control here is raised.</b> That is the whole of what says
    /// a box is somewhere to put something rather than something to press, and it is the same
    /// <see cref="Bevel"/> with the lit pair swapped that a pressed button already uses.</para>
    /// <para>⚠ <b>The window scrolls with the caret rather than the text being clipped.</b> A field
    /// is a fixed width and a name can be longer than it, so what is drawn is the run of characters
    /// around the caret — otherwise typing past the end writes over the panel beside it, which is
    /// exactly the fault the note strip was moved out of a row for.</para>
    /// <para>The caret blinks off the screen's own clock, not off a frame count, so it is the same
    /// speed on every machine. Drawn only while this is the field being typed into: two boxes with
    /// carets in them is two places to believe a letter is about to land.</para>
    /// </remarks>
    /// <returns>How wide it came out, so the row can give it a zone of its own.</returns>
    private float Box(HudScreen screen, TextField field, float left, float y, float panel)
    {
        const float Glyph = 8f;

        var width = MathF.Min(panel * 0.55f, 132f);
        var x = left + panel - width;
        var focused = ReferenceEquals(screen.Typing, field);
        var layer = focused ? GuiTextureSet.Layer.TextFieldHighlighted : GuiTextureSet.Layer.TextField;

        if (HasGui(layer))
            NineSlice(_guiOver, x, y - 2f, width, ScreenLayout.MenuLine,
                layer, 200f, 20f, 4f);
        else
            Bevel(x, y - 2f, width, ScreenLayout.MenuLine, raised: false,
                new Vector4(0.13f, 0.13f, 0.13f, 0.98f));

        var inside = width - 8f;
        var text = field.Text.AsSpan();

        // The first character drawn, chosen so the caret is always inside the box. Walked forward
        // until what is left of the line fits, which is one loop and no measuring backwards. Spans
        // throughout: this runs every frame a box is on screen, and slicing a string allocates.
        var from = 0;
        while (from < field.Caret && TextWidth(text[from..field.Caret], Glyph) > inside) from++;

        var shown = text[from..];
        while (shown.Length > 0 && TextWidth(shown, Glyph) > inside) shown = shown[..^1];

        if (text.Length == 0 && !focused && field.Placeholder.Length > 0)
            Text(FitText(field.Placeholder, width - 8f, Glyph), x + 4f, y, Glyph, InkFaint);
        else
            Text(shown, x + 4f, y, Glyph, focused ? Vector4.One : Ink);

        // Half a second lit, half dark. On its own pen position rather than at the end of the line,
        // so it sits between the characters somebody is actually typing between.
        if (focused && screen.Clock % 1f < 0.5f)
        {
            var at = x + 4f + TextWidth(text[from..field.Caret], Glyph);
            Rect(_plain, at, y - 1f, 1f, Glyph + 2f, Highlight);
        }

        return width;
    }

    /// <summary>
    /// What the picked setting costs, or when it applies, in a strip below the list.
    /// </summary>
    /// <remarks>
    /// <para><b>Below the list, not inside the row.</b> It used to be drawn nine units under its own
    /// label on a thirteen unit line — so its bottom third landed on top of the next row's text, and
    /// picking "view distance" wrote "…next time the game opens; 8 loaded now" across "field of
    /// view". Reported as text that should not be there, which is exactly what it was.</para>
    /// <para>Wrapped, because the longest of them is wider than the panel and a line drawn past the
    /// edge simply keeps going over whatever is beside it. Wrapping is on words and falls back to
    /// cutting a word that is wider than the whole panel on its own, so there is no input that makes
    /// this loop forever.</para>
    /// <para>One fixed place, so the eye learns where to look, and drawn only when the picked row
    /// has something to say — nothing above it moves either way.</para>
    /// </remarks>
    private void Note(HudScreen screen, float left, float y, float panel, int first, int shown)
    {
        if (screen.Selected < first || screen.Selected >= first + shown) return;
        if (screen.Rows[screen.Selected].Note is not { Length: > 0 } note) return;

        const float Glyph = 7f;
        var lines = Wrap(note, panel - 8f, Glyph);

        var height = lines.Count * 9f + 7f;
        if (HasGui(GuiTextureSet.Layer.TooltipBackground))
            NineSlice(_guiOver, left - 4f, y, panel + 8f, height,
                GuiTextureSet.Layer.TooltipBackground, 100f, 100f, 5f,
                new Vector4(1f, 1f, 1f, 0.97f));
        else
            Bevel(left - 4f, y, panel + 8f, height, raised: false,
                new Vector4(0.20f, 0.20f, 0.20f, 0.96f));

        for (var i = 0; i < lines.Count; i++)
            Text(lines[i], left, y + 4f + i * 9f, Glyph, InkFaint);
    }

    /// <summary>Reused by the wrapper so a note costs no allocation per frame.</summary>
    private readonly List<string> _wrapped = [];

    /// <summary>Breaks a line on spaces so it fits a width, cutting a word only if it cannot.</summary>
    private List<string> Wrap(string line, float width, float height)
    {
        _wrapped.Clear();

        var start = 0;
        while (start < line.Length)
        {
            var take = 0;
            var lastSpace = -1;

            while (start + take < line.Length)
            {
                if (line[start + take] == ' ') lastSpace = take;
                if (TextWidth(line.AsSpan(start, take + 1), height) > width) break;
                take++;
            }

            if (start + take >= line.Length)
            {
                _wrapped.Add(line[start..]);
                break;
            }

            // Back up to the last space if there was one; otherwise cut the word, which only
            // happens when one word is wider than the whole panel.
            var end = lastSpace > 0 ? lastSpace : Math.Max(1, take);
            _wrapped.Add(line.Substring(start, end).TrimEnd());
            start += end;

            while (start < line.Length && line[start] == ' ') start++;
        }

        return _wrapped;
    }

    /// <summary>
    /// The bar down the side of a list too long to show at once.
    /// </summary>
    /// <remarks>
    /// The thumb's length is the share of the list on screen and its position is how far down that
    /// share sits, which between them are the only two things a scrollbar has ever said. It is one
    /// zone rather than three — no separate arrows at the ends — because the whole track is
    /// clickable and draggable, and a pair of one-line-at-a-time buttons on a twelve-line list is
    /// furniture nobody uses.
    /// </remarks>
    private void Scrollbar(
        HudScreen screen, ScreenLayout layout, float x, float y, float height, int first, int lines, int total)
    {
        const float Width = ScreenLayout.ScrollbarWidth;

        if (HasGui(GuiTextureSet.Layer.ScrollerBackground))
            NineSlice(_guiOver, x, y, Width, height,
                GuiTextureSet.Layer.ScrollerBackground, 6f, 32f, 3f);
        else
            Bevel(x, y, Width, height, raised: false, new Vector4(0.17f, 0.17f, 0.17f, 0.97f));

        var span = MathF.Max(1f, total - lines);
        var thumb = MathF.Max(10f, MathF.Round(height * lines / total));
        var travel = height - thumb - 4f;
        var at = MathF.Round(y + 2f + travel * (first / span));

        var held = screen.Hovered is { Kind: ZoneKind.Scrollbar };
        if (HasGui(GuiTextureSet.Layer.Scroller))
            NineSlice(_guiOver, x + 1f, at, Width - 2f, thumb,
                GuiTextureSet.Layer.Scroller, 6f, 32f, 3f,
                held ? new Vector4(1.12f, 1.12f, 1.12f, 1f) : Vector4.One);
        else
            Bevel(x + 1f, at, Width - 2f, thumb, raised: true, held ? PanelLight : PanelFill);

        layout.Add(ZoneKind.Scrollbar, 0, x, y, Width, height);
    }

    /// <summary>
    /// What the game has to say, stacked down from the top right corner.
    /// </summary>
    /// <remarks>
    /// <para>Top right, because that corner holds nothing else and because everything a toast says
    /// is optional — a player who is busy should be able to ignore it without it having covered
    /// anything. The crosshair, the bar, the hearts and the breath all live elsewhere.</para>
    /// <para>They fade rather than vanishing, and they fade out rather than in. Something appearing
    /// gradually is a thing you notice after it has finished saying itself; something leaving
    /// gradually is a thing you can still read while it goes.</para>
    /// </remarks>
    private void Toasts(IReadOnlyList<Toast> toasts, float w)
    {
        if (toasts.Count == 0) return;

        const float Width = 132f;
        const float Height = 30f;

        var left = MathF.Round(w - Width - 8f);
        var top = 8f;

        foreach (var toast in toasts)
        {
            var alpha = toast.Alpha;
            if (alpha <= 0f) continue;

            Bevel(left, top, Width, Height, raised: true, PanelFill with { W = PanelFill.W * alpha });

            var textLeft = left + 6f;
            if (toast.Icon >= 0)
            {
                Bevel(left + 5f, top + 5f, 20f, 20f, raised: false, SlotFill with { W = SlotFill.W * alpha });
                Rect(_blocks, left + 8f, top + 8f, 14f, 14f, new Vector4(1f, 1f, 1f, alpha), toast.Icon);
                textLeft = left + 30f;
            }

            Text(toast.Title, textLeft, top + 5f, 7f, Highlight with { W = alpha });
            Text(toast.Line, textLeft, top + 15f, 8f, new Vector4(1f, 1f, 1f, alpha));

            top += Height + 4f;
        }
    }

    /// <summary>The hint along the bottom, above the bar.</summary>
    private void Footer(HudScreen screen, float w, float h)
    {
        if (screen.Footer.Length == 0) return;
        TextCentred(screen.Footer, w / 2f, h - 46f, 8f, InkDim);
    }

    /// <summary>
    /// The recipe book, folded out beside the panel it belongs to.
    /// </summary>
    /// <remarks>
    /// <para>A page of what can be made here — craftable lit, everything else dark, because half
    /// the value of a book is seeing that a stormglass pickaxe exists long before there is any
    /// stormglass. Clicking one lays its ingredients into the grid on the panel beside it, which is
    /// the whole reason the two are on one screen: what it makes lands in a square already in view,
    /// a hand's width from the pockets it is going into.</para>
    /// <para>The picked recipe is drawn under the page as what it costs. There is a font now, so it
    /// is named as well — the pictures say what it takes and only a name says what it is for.</para>
    /// </remarks>
    private void Book(ItemRegistry catalogue, HudScreen screen, ScreenLayout layout)
    {
        var z = layout.Zoom;
        var cell = layout.Size(ScreenLayout.BookCell);

        // The book's own frame, in its own pixels, hanging to the left of the panel.
        BookBevel(layout, 0f, 0f, ScreenLayout.BookWidth, ScreenLayout.BookHeight, raised: true, PanelFill);
        BookBevel(
            layout, ScreenLayout.BookWell.X, ScreenLayout.BookWell.Y,
            ScreenLayout.BookWell.W, ScreenLayout.BookWell.H,
            raised: false, new Vector4(0.15f, 0.15f, 0.16f, 0.98f));

        var pages = Math.Max(1, (screen.Recipes.Count + ScreenLayout.BookPage - 1) / ScreenLayout.BookPage);
        var page = Math.Clamp(screen.BookPage, 0, pages - 1);

        // Search, shelf and craftable-now live in the book's header. Each is drawn from its actual
        // zone, so what lights under the pointer is exactly what a click will operate.
        foreach (var zone in layout.Zones)
        {
            if (zone.Kind == ZoneKind.Field && zone.Index < 0)
            {
                BookSearch(screen, zone);
                continue;
            }

            if (zone.Kind != ZoneKind.Button) continue;

            var shelf = zone.Index - (int)ScreenButton.RecipeCategoryAll;
            var isShelf = shelf >= 0 && shelf < Enum.GetValues<RecipeCategory>().Length;
            var craftable = zone.Index == (int)ScreenButton.CraftableOnly;
            if (!isShelf && !craftable) continue;

            var hot = screen.Hovered is { Kind: ZoneKind.Button } over && over.Index == zone.Index;
            var category = isShelf ? (RecipeCategory)shelf : RecipeCategory.All;
            var active = craftable ? screen.CraftableOnly : screen.RecipeCategory == category;
            var guiLayer = craftable
                ? active ? GuiTextureSet.Layer.RecipeFilterOn : GuiTextureSet.Layer.RecipeFilterOff
                : active ? GuiTextureSet.Layer.RecipeTabSelected : GuiTextureSet.Layer.RecipeTab;
            if (HasGui(guiLayer))
                Rect(_guiOver, zone.X, zone.Y, zone.W, zone.H, Vector4.One, (int)guiLayer);
            else
                Bevel(zone.X, zone.Y, zone.W, zone.H, raised: !active,
                    hot ? PanelLight : active ? SlotFill : PanelFill);

            var label = craftable ? "!" : category switch
            {
                RecipeCategory.Building => "build",
                RecipeCategory.Materials => "raw",
                RecipeCategory.Tools => "tools",
                RecipeCategory.Light => "light",
                RecipeCategory.Machines => "work",
                _ => "all",
            };
            if (!craftable)
                TextCentred(label, zone.CentreX, zone.Y + MathF.Max(2f, (zone.H - 7f) * 0.5f), 7f,
                    active ? Highlight : Ink);
        }

        foreach (var zone in layout.Zones)
        {
            if (zone.Kind != ZoneKind.Recipe) continue;

            var payable = zone.Index < screen.Payable.Count && screen.Payable[zone.Index];

            Bevel(zone.X, zone.Y, zone.W, zone.H, raised: false,
                payable ? SlotFill : new Vector4(0.19f, 0.19f, 0.19f, 0.95f));

            if (screen.Hovered is { Kind: ZoneKind.Recipe } hot && hot.Index == zone.Index)
                Rect(_plain, zone.X, zone.Y, zone.W, zone.H, new Vector4(1f, 1f, 1f, 0.20f));

            if (zone.Index == screen.Selected) Select(zone.X, zone.Y, zone.W, zone.H);

            var result = screen.Recipes[zone.Index].Result;
            var inset = MathF.Round(z * 2f);

            // ⛳ THE BOOK TURNS AND NOTHING ELSE DOES, and that is the point rather than a saving.
            // A page is a display case: it is what somebody is reading, and turning is what says a
            // thing has a shape rather than a picture. Thirty six pockets all turning at once is a
            // fruit machine, and the one place a shape most needs to be legible — the bar under the
            // crosshair, glanced at mid-swing — is the last place anything should be moving.
            //
            // Offset per entry so a page does not turn in lockstep, which reads as one object seen
            // several times rather than as several objects.
            SlotIcon(catalogue, result, zone.X + inset, zone.Y + inset, zone.W - inset * 2f,
                payable ? Vector4.One : new Vector4(0.45f, 0.45f, 0.45f, 0.85f),
                spin: screen.Drift * BookTurn + zone.Index * 0.7f);

            if (result.Count > 1)
                Number(result.Count, zone.X + zone.W, zone.Y + zone.H - cell * 0.32f, MathF.Max(5f, z * 4f));
        }

        // The pages either side. Both always drawn; the one that would do nothing is dim.
        foreach (var zone in layout.Zones)
        {
            if (zone.Kind != ZoneKind.Button) continue;
            if (zone.Index is not ((int)ScreenButton.PageBack or (int)ScreenButton.PageForward)) continue;

            var back = zone.Index == (int)ScreenButton.PageBack;
            var live = back ? page > 0 : page < pages - 1;
            var hot = screen.Hovered is { Kind: ZoneKind.Button } over && over.Index == zone.Index;

            var ink = !live ? new Vector4(0.35f, 0.35f, 0.35f, 0.8f) : hot ? Highlight : Ink;

            // A triangle out of stacked bars, the same way the crafting arrow is drawn.
            var rows = (int)MathF.Max(3f, MathF.Round(zone.H / (z * 2f)));
            for (var i = 0; i < rows; i++)
            {
                var reach = zone.W * (1f - MathF.Abs(i - (rows - 1) * 0.5f) / (rows * 0.5f));
                if (reach <= 0f) continue;

                Rect(_plain,
                    back ? zone.X + zone.W - reach : zone.X,
                    zone.Y + i * (zone.H / rows), reach, MathF.Max(1f, zone.H / rows), ink);
            }
        }

        if (pages > 1)
            TextCentred(
                $"{page + 1} of {pages}",
                layout.BookX + layout.Size(ScreenLayout.BookWidth * 0.5f),
                layout.Y(144f), 8f, InkDim);

        // What the picked one costs, under the page.
        var chosen = screen.Selected >= 0 && screen.Selected < screen.Recipes.Count
            ? screen.Recipes[screen.Selected]
            : null;

        if (chosen is null) return;

        var nameY = layout.Y(ScreenLayout.BookHeight) + 6f;
        var payableNow = screen.Selected < screen.Payable.Count && screen.Payable[screen.Selected];

        TextCentred(
            chosen.Name, layout.BookX + layout.Size(ScreenLayout.BookWidth * 0.5f), nameY, 9f,
            payableNow ? Vector4.One : InkDim);

        TextCentred(
            payableNow ? "click to lay it out" : "not enough for this yet",
            layout.BookX + layout.Size(ScreenLayout.BookWidth * 0.5f), nameY + 12f, 7f, InkFaint);

        for (var i = 0; i < Math.Min(2, screen.RecipeCosts.Length); i++)
        {
            var lines = Wrap(screen.RecipeCosts[i], layout.Size(ScreenLayout.BookWidth - 8f), 6f);
            for (var line = 0; line < Math.Min(2, lines.Count); line++)
                TextCentred(lines[line],
                    layout.BookX + layout.Size(ScreenLayout.BookWidth * 0.5f),
                    nameY + 23f + (i * 2 + line) * 8f, 6f, InkFaint);
        }
    }

    /// <summary>The compact text field in the recipe book's header.</summary>
    private void BookSearch(HudScreen screen, Zone zone)
    {
        const float glyph = 7f;
        var field = screen.RecipeSearch;
        var focused = ReferenceEquals(screen.Typing, field);
        var hot = screen.Hovered is { Kind: ZoneKind.Field } over && over.Index == zone.Index;

        Bevel(zone.X, zone.Y, zone.W, zone.H, raised: false,
            hot || focused ? new Vector4(0.18f, 0.18f, 0.19f, 0.98f)
                : new Vector4(0.13f, 0.13f, 0.14f, 0.98f));

        var shown = field.Empty && !focused ? field.Placeholder : field.Text;
        while (shown.Length > 0 && TextWidth(shown, glyph) > zone.W - 6f) shown = shown[1..];
        Text(shown, zone.X + 3f, zone.Y + 4f, glyph, field.Empty ? InkFaint : Ink);

        if (focused && screen.Clock % 1f < 0.5f)
        {
            var at = zone.X + 3f + TextWidth(shown, glyph);
            Rect(_plain, MathF.Min(at, zone.X + zone.W - 2f), zone.Y + 3f, 1f, glyph + 2f, Highlight);
        }
    }

    /// <summary>A bevel in the book's own pixels, which start at its own left edge.</summary>
    private void BookBevel(
        ScreenLayout layout, float px, float py, float pw, float ph, bool raised, Vector4 fill)
    {
        var z = layout.Zoom;
        var edge = MathF.Max(1f, MathF.Round(z));
        var x = layout.BookX + px * z;
        var y = layout.Y(py);
        var w = pw * z;
        var h = ph * z;

        var top = raised ? PanelLight : PanelDark;
        var bottom = raised ? PanelDark : PanelLight;

        Rect(_plain, x, y, w, h, fill);
        Rect(_plain, x, y, w, edge, top);
        Rect(_plain, x, y, edge, h, top);
        Rect(_plain, x, y + h - edge, w, edge, bottom);
        Rect(_plain, x + w - edge, y, edge, h, bottom);
        Rect(_plain, x, y + h - edge, edge, edge, fill);
        Rect(_plain, x + w - edge, y, edge, edge, fill);
    }

    /// <summary>The nth filled slot of a shapeless recipe, or null past the end.</summary>
    private static Ingredient? Nth(Recipe recipe, int index)
    {
        foreach (var slot in recipe.Ingredients)
            if (index-- == 0) return slot;
        return null;
    }

    /// <summary>
    /// A container screen, drawn on the panel every resource pack is painted for.
    /// </summary>
    /// <remarks>
    /// <para>A hundred and seventy six by a hundred and sixty six, squares on an eighteen pitch,
    /// sixteen inside each — every number here was measured out of a real pack's
    /// <c>inventory.png</c>, <c>crafting_table.png</c> and <c>furnace.png</c> rather than
    /// remembered, which is how the player panel's arrow turned out to be a different size from the
    /// bench's. Drawing our own greyscale chrome on that grid is what makes a skinned interface a
    /// blit rather than a second layout.</para>
    /// <para>Three panels and one method, because two thirds of all three is the same thing: the
    /// player's own pockets. That is not a saving, it is the point — a container you cannot reach
    /// your own pockets from is a container you cannot put anything into.</para>
    /// </remarks>
    private void Container(
        ItemRegistry catalogue,
        Inventory inventory,
        Equipment equipment,
        HudScreen screen,
        ScreenLayout layout,
        float w,
        float h)
    {
        var kind = screen.Kind switch
        {
            HudScreenKind.Bench => PanelKind.Bench,
            HudScreenKind.Furnace => PanelKind.Furnace,
            HudScreenKind.Chest => PanelKind.Chest,
            HudScreenKind.Stonecutter => PanelKind.Stonecutter,
            _ => PanelKind.Player,
        };

        layout.BuildPanel(
            kind, screen.Grid?.Width ?? 0, w, h,
            screen.BookOut, screen.BookPage, screen.Recipes.Count);

        var z = layout.Zoom;

        // The book first, so the panel's own frame overlaps it rather than the other way round.
        if (layout.BookOut) Book(catalogue, screen, layout);

        var panelLayer = kind switch
        {
            PanelKind.Player => GuiTextureSet.Layer.Inventory,
            PanelKind.Bench => GuiTextureSet.Layer.CraftingTable,
            PanelKind.Furnace => GuiTextureSet.Layer.Furnace,
            PanelKind.Chest => GuiTextureSet.Layer.Chest,
            PanelKind.Stonecutter => GuiTextureSet.Layer.Stonecutter,
            _ => GuiTextureSet.Layer.Inventory,
        };
        var skinned = HasGui(panelLayer);

        // A container sheet is a fixed 256 square whose live panel occupies 176x166 at its origin.
        // The layout was authored in those same pixels, so this is one quad and no remapping.
        if (skinned)
            RectUv(_guiUnder, layout.OriginX, layout.OriginY,
                layout.Size(ScreenLayout.PanelWidth), layout.Size(ScreenLayout.PanelHeight),
                0f, 0f,
                ScreenLayout.PanelWidth / (float)GuiTextureSet.Size,
                ScreenLayout.PanelHeight / (float)GuiTextureSet.Size,
                Vector4.One, (int)panelLayer);
        else
            PanelBevel(layout, 0f, 0f, ScreenLayout.PanelWidth, ScreenLayout.PanelHeight, raised: true, PanelFill);

        // The player screen's tabs sit on top of the panel. A station has none — a furnace is not a
        // place you look up what you have unlocked.
        if (screen.TabNames.Length > 1)
            Tabs(screen, layout, layout.X(0f), layout.Y(0f) - 18f, layout.Size(ScreenLayout.PanelWidth));

        // The button that folds the book out, on whichever panel has one.
        foreach (var zone in layout.Zones)
        {
            if (zone.Kind != ZoneKind.Button || zone.Index != (int)ScreenButton.Book) continue;

            var hot = screen.Hovered is { Kind: ZoneKind.Button } over && over.Index == zone.Index;
            var button = hot
                ? GuiTextureSet.Layer.RecipeButtonHighlighted
                : GuiTextureSet.Layer.RecipeButton;
            var packedButton = HasGui(button);
            if (packedButton)
                Rect(_guiOver, zone.X, zone.Y, zone.W, zone.H, Vector4.One, (int)button);
            else
                Bevel(zone.X, zone.Y, zone.W, zone.H, raised: !screen.BookOut,
                    hot ? PanelLight : screen.BookOut ? SlotFill : PanelFill);

            // ⛳ A DRAWING OF A BOOK, which was three flat rectangles until the user pointed out that
            // it looked like nothing. That is the honest limit of generated chrome: the world's
            // tiles gain from being procedural — grain, variation, a whole set from two tables —
            // and a single button-sized picture gains nothing from it at all. This one is painted,
            // carried in the assembly, and reskinnable by a pack like every other layer.
            //
            // ⚠ Inset by the bevel so the raised edge still reads as a button under it, and drawn
            // at full brightness when the book is out — the same "this is a state, not chrome" the
            // mint selection is, and the only thing on the button that says which way it is set.
            var pad = MathF.Max(1f, MathF.Round(z * 2f));
            var tint = screen.BookOut ? Vector4.One : new Vector4(0.82f, 0.82f, 0.82f, 1f);

            if (!packedButton)
                Rect(_blocks, zone.X + pad, zone.Y + pad, zone.W - pad * 2f, zone.H - pad * 2f,
                    tint, StarterBlocks.LayerRecipeBook);
        }

        // A rule where the player's own pockets begin, which is where the pack's sheet puts one too.
        if (!skinned)
        {
            Rect(_plain, layout.X(7f), layout.Y(78f), layout.Size(162f), z, PanelDark);
            Rect(_plain, layout.X(7f), layout.Y(79f), layout.Size(162f), z, PanelLight);
        }

        switch (kind)
        {
            case PanelKind.Player:
                Figure(layout, screen, catalogue, equipment, inventory.Held, frame: !skinned);
                if (!skinned) Arrow(layout, ScreenLayout.PlayerArrow, 1f);
                break;

            case PanelKind.Bench:
                if (!skinned) Arrow(layout, ScreenLayout.BenchArrow, 1f);
                break;

            case PanelKind.Furnace:
                Hearth(layout, screen, chrome: !skinned);
                break;
        }

        // At a bench and at a furnace the square that gives wears a wider frame, which is how the
        // pack's own sheets draw it — twenty six across rather than eighteen. The two-by-two in a
        // player's hands does not: its result is a plain square, and that difference was measured
        // rather than assumed.
        var giving = kind == PanelKind.Bench
            ? layout.Find(SlotRole.Result, 0)
            : kind == PanelKind.Furnace ? layout.Find(SlotRole.Smelted, 0) : null;

        if (!skinned && giving is { } wide)
            PanelBevel(
                layout,
                (wide.X - layout.OriginX) / z - 5f, (wide.Y - layout.OriginY) / z - 5f,
                ScreenLayout.ResultFrame, ScreenLayout.ResultFrame,
                raised: false, SlotFill);

        var digits = MathF.Max(5f, MathF.Round(z * 4f));

        foreach (var zone in layout.Zones)
        {
            if (zone.Kind != ZoneKind.Slot) continue;

            var stack = Contents(inventory, equipment, screen, zone);
            var ghost = stack.IsEmpty ? GhostAt(screen, zone) : null;

            if (ghost is not null && HasGui(GuiTextureSet.Layer.RecipeOverlay))
                Rect(_guiUnder, zone.X, zone.Y, zone.W, zone.H, Vector4.One,
                    (int)GuiTextureSet.Layer.RecipeOverlay);

            // Whatever already has the wider frame keeps it; every other square is a well pressed in.
            if (!skinned && giving != zone) Well(layout, zone);

            // A worn slot nobody can fill yet says what it is for rather than sitting blank.
            if (stack.IsEmpty && zone.Role == SlotRole.Equip)
                Rect(_iconQuads, zone.X, zone.Y, zone.W, zone.H,
                    new Vector4(1f, 1f, 1f, 0.16f), IconEquip + zone.Index);

            // Under the pointer, and the one being carried, both get a lift. They are different
            // states — one is "this is what you would click", the other is "this is in your hand" —
            // so the hand keeps the hard lit edge and the pointer gets a wash.
            if (screen.Hovered is { Kind: ZoneKind.Slot } hot && hot == zone)
                Rect(_plain, zone.X, zone.Y, zone.W, zone.H, new Vector4(1f, 1f, 1f, 0.22f));

            if (zone.Role == SlotRole.Pocket && zone.Index == inventory.Selected)
                Select(zone.X, zone.Y, zone.W, zone.H);

            if (stack.IsEmpty && ghost is null) continue;

            var drawn = stack.IsEmpty
                ? new ItemStack(ghost!.Members[0], 1)
                : stack;

            var type = catalogue[drawn.Item];
            var inset = MathF.Round(z);
            if (ghost is null) Bloom(type, zone.X, zone.Y, zone.W, zone.H);
            SlotIcon(catalogue, drawn, zone.X + inset, zone.Y + inset, zone.W - inset * 2f,
                ghost is null ? Vector4.One : new Vector4(1f, 1f, 1f, 0.28f));

            if (ghost is null && type.Durability > 0 && stack.Damage > 0)
            {
                var life = 1f - drawn.Damage / (float)type.Durability;
                var bar = MathF.Max(1f, MathF.Round(z));
                Rect(_plain, zone.X + inset, zone.Y + zone.H - bar * 2f, zone.W - inset * 2f, bar,
                    new Vector4(0f, 0f, 0f, 0.8f));
                Rect(_plain, zone.X + inset, zone.Y + zone.H - bar * 2f, (zone.W - inset * 2f) * life, bar,
                    new Vector4(1f - life, 0.25f + life * 0.65f, 0.2f, 1f));
            }

            if (ghost is null && drawn.Count > 1)
                Number(drawn.Count, zone.X + zone.W, zone.Y + zone.H - digits - 1f, digits);
        }

        // A stonecutter's list. Every offer is drawn as the thing it would make, which is the only
        // label a picture of a slab needs — and the one that is picked is lit, because a list with
        // no selection showing is a list where taking the result looks like it came from nowhere.
        if (kind == PanelKind.Stonecutter)
        {
            foreach (var zone in layout.Zones)
            {
                if (zone.Kind != ZoneKind.Recipe) continue;
                if (zone.Index >= screen.Cuts.Count) continue;

                Well(layout, zone);

                if (zone.Index == screen.Cut)
                    Rect(_plain, zone.X, zone.Y, zone.W, zone.H, new Vector4(1f, 0.92f, 0.55f, 0.30f));
                else if (screen.Hovered is { Kind: ZoneKind.Recipe } over && over.Index == zone.Index)
                    Rect(_plain, zone.X, zone.Y, zone.W, zone.H, new Vector4(1f, 1f, 1f, 0.22f));

                var offer = screen.Cuts[zone.Index].Result;
                var inset = MathF.Round(z);
                SlotIcon(catalogue, offer, zone.X + inset, zone.Y + inset, zone.W - inset * 2f, Vector4.One);

                if (offer.Count > 1)
                    Number(offer.Count, zone.X + zone.W, zone.Y + zone.H - digits - 1f, digits);
            }
        }

        // What the arrangement makes, named, under the panel. The picture says what it costs and
        // only a name says what it is for.
        var named = kind == PanelKind.Stonecutter
            ? screen.Cut >= 0 && screen.Cut < screen.Cuts.Count ? screen.Cuts[screen.Cut].Name : null
            : screen.Grid?.Match?.Name;

        if (named is not null)
            TextCentred(named, w / 2f, layout.Y(ScreenLayout.PanelHeight) + 6f, 9f, Ink);
    }

    /// <summary>What is actually in one of the panel's squares.</summary>
    private static ItemStack Contents(
        Inventory inventory, Equipment equipment, HudScreen screen, Zone zone) => zone.Role switch
    {
        SlotRole.Pocket => inventory[zone.Index],
        SlotRole.Equip => equipment.At(zone.Index),
        SlotRole.Craft => screen.Grid?[zone.Index] ?? ItemStack.Empty,
        SlotRole.Result => screen.Grid?.Result ?? ItemStack.Empty,
        SlotRole.Smelting => screen.Burning?.Input ?? ItemStack.Empty,
        SlotRole.Fuel => screen.Burning?.Fuel ?? ItemStack.Empty,
        SlotRole.Smelted => screen.Burning?.Output ?? ItemStack.Empty,
        SlotRole.Stored => screen.Stored?.Contents[zone.Index] ?? ItemStack.Empty,
        SlotRole.Cutting => screen.Cutting,
        SlotRole.Cut => screen.Cut >= 0 && screen.Cut < screen.Cuts.Count
            ? screen.Cuts[screen.Cut].Result
            : ItemStack.Empty,
        _ => ItemStack.Empty,
    };

    /// <summary>An ingredient shown faintly in an empty craft cell before the recipe is laid out.</summary>
    private static Ingredient? GhostAt(HudScreen screen, Zone zone)
    {
        if (zone.Role != SlotRole.Craft || screen.Grid is not { } grid) return null;
        if (screen.Selected < 0 || screen.Selected >= screen.Recipes.Count) return null;

        var recipe = screen.Recipes[screen.Selected];
        if (!recipe.WorkedAt(grid.Station, grid.Width)) return null;

        if (recipe.Shapeless) return Nth(recipe, zone.Index);

        var x = zone.Index % grid.Width;
        var y = zone.Index / grid.Width;
        return x < recipe.Width && y < recipe.Height ? recipe.At(x, y) : null;
    }

    /// <summary>
    /// A square pressed into the panel: eighteen across with sixteen inside it.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>A rim and a well, rather than a bevel.</b> Modelled on the user's own reference sheet
    /// for this screen, where every square is a near-black interior inside a lighter grey-blue frame
    /// with the corners picked out darker. A bevel says "pressed in"; a frame says "this is one of a
    /// set", which is what a grid of thirty-six squares actually needs to say. The two lit sides are
    /// kept as a hairline inside the rim, so it still catches the light from the top left the way
    /// every other panel in the game does.
    /// </remarks>
    private void Well(ScreenLayout layout, Zone zone)
    {
        var z = layout.Zoom;

        // The rim: the full 18, in one tone that is neither the panel nor the interior.
        Rect(_plain, zone.X - z, zone.Y - z, zone.W + z * 2f, zone.H + z * 2f, SlotRim);

        // The corners of the rim, darker, which is what stops a grid of them reading as one mesh.
        Rect(_plain, zone.X - z, zone.Y - z, z, z, PanelDark);
        Rect(_plain, zone.X + zone.W, zone.Y - z, z, z, PanelDark);
        Rect(_plain, zone.X - z, zone.Y + zone.H, z, z, PanelDark);
        Rect(_plain, zone.X + zone.W, zone.Y + zone.H, z, z, PanelDark);

        // The interior, and a hairline of shadow along the two sides the light does not reach.
        Rect(_plain, zone.X, zone.Y, zone.W, zone.H, SlotFill);
        Rect(_plain, zone.X, zone.Y, zone.W, MathF.Max(1f, z * 0.5f), PanelDark);
        Rect(_plain, zone.X, zone.Y, MathF.Max(1f, z * 0.5f), zone.H, PanelDark);
    }

    /// <summary>
    /// A pool of its own colour behind anything that gives off light.
    /// </summary>
    /// <remarks>
    /// ⛳ From the user's own reference sheet for this screen, where every light source sits in a
    /// bloom. It is the thing that tells a torch from a stick and a lamp from a rock at the size of
    /// a fingernail: they are the same shape in the same square with the same three shaded faces,
    /// and only one of them is worth carrying into a cave. Drawn <em>behind</em> the icon and spread
    /// wider than the square, so it reads as light coming off the thing rather than as a tint on it.
    /// </remarks>
    private void Bloom(ItemType type, float x, float y, float w, float h)
    {
        var glow = type.Glow;
        var peak = MathF.Max(glow.X, MathF.Max(glow.Y, glow.Z));
        if (peak <= 0f) return;

        // ⚠ Kept inside its own square, and kept faint. The first pass spread it nearly a whole
        // square either side at half opacity, and a hotbar with a torch in it had two neighbours
        // washed out and the torch itself hidden under its own light. A bloom is a hint that a thing
        // gives off light; it is not the light.
        var colour = new Vector4(glow.X / peak, glow.Y / peak, glow.Z / peak, 0.12f + peak * 0.26f);
        var spread = MathF.Round(w * 0.18f);

        Rect(_iconQuads, x - spread, y - spread, w + spread * 2f, h + spread * 2f, colour, IconBloom);
    }

    /// <summary>A bevel measured in the pack's pixels rather than in layout units.</summary>
    private void PanelBevel(
        ScreenLayout layout, float px, float py, float pw, float ph, bool raised, Vector4 fill)
    {
        var z = layout.Zoom;
        var edge = MathF.Max(1f, MathF.Round(z));
        var x = layout.X(px);
        var y = layout.Y(py);
        var w = layout.Size(pw);
        var h = layout.Size(ph);

        var top = raised ? PanelLight : PanelDark;
        var bottom = raised ? PanelDark : PanelLight;

        Rect(_plain, x, y, w, h, fill);
        Rect(_plain, x, y, w, edge, top);
        Rect(_plain, x, y, edge, h, top);
        Rect(_plain, x, y + h - edge, w, edge, bottom);
        Rect(_plain, x + w - edge, y, edge, h, bottom);
        Rect(_plain, x, y + h - edge, edge, edge, fill);
        Rect(_plain, x + w - edge, y, edge, edge, fill);
    }

    /// <summary>The arrow from the grid to what it makes, drawn in the pack's own pixels.</summary>
    private void Arrow(ScreenLayout layout, (int X, int Y, int W, int H) box, float alpha)
    {
        var z = layout.Zoom;
        var colour = new Vector4(0.78f, 0.78f, 0.78f, alpha);

        // A shaft along the middle and a head made of a stack of bars — a triangle in a batcher
        // that only draws rectangles is a triangle drawn a row at a time, and at this size that is
        // exactly what a pixel arrow is anyway.
        var midY = box.Y + box.H / 2f;
        var head = MathF.Round(box.H * 0.55f);
        var shaft = box.W - head;

        Rect(_plain, layout.X(box.X), layout.Y(midY - box.H * 0.16f),
            layout.Size(shaft), layout.Size(box.H * 0.32f), colour);

        var rows = (int)MathF.Max(1f, MathF.Round(box.H / 2f));
        for (var i = 0; i < rows; i++)
        {
            var reach = head * (1f - i / (float)rows);
            Rect(_plain,
                layout.X(box.X + shaft), layout.Y(midY - i - 1f),
                layout.Size(reach), z, colour);
            Rect(_plain,
                layout.X(box.X + shaft), layout.Y(midY + i),
                layout.Size(reach), z, colour);
        }
    }

    /// <summary>The projected player: worn skin, outer layers, armour and both held items.</summary>
    /// <remarks>
    /// The old flat cut-out and its separate front-face armour loop are gone. This calls the same
    /// <see cref="ProjectedPlayerPreview"/> path as SKINS, then puts the existing item sprites at the
    /// projected fists. A small automatic four-degree drift keeps it alive; dragging the window owns
    /// the larger angle in <see cref="HudScreen.FigureYaw"/>.
    /// </remarks>
    private void Figure(
        ScreenLayout layout, HudScreen screen, ItemRegistry catalogue, Equipment equipment,
        ItemStack held, bool frame = true)
    {
        var box = ScreenLayout.Figure;

        if (frame)
            PanelBevel(
                layout, box.X, box.Y, box.W, box.H,
                raised: false, new Vector4(0.13f, 0.14f, 0.16f, 0.98f));

        var x = layout.X(box.X);
        var y = layout.Y(box.Y);
        var w = layout.Size(box.W);
        var h = layout.Size(box.H);
        screen.FigureBox = new Vector4(x, y, w, h);
        layout.Add(ZoneKind.PlayerPreview, 0, x, y, w, h);

        if (_skin is null || _wornPreview is null) return;

        Span<int> materials = stackalloc int[Equipment.Slots];
        materials.Fill(-1);
        if (_armour is not null)
        {
            foreach (var piece in Armour.Pieces)
            {
                var worn = equipment[piece.Slot];
                if (worn.IsEmpty) continue;
                materials[(int)piece.Slot] = Armour.MaterialOf(catalogue[worn.Item]);
            }
        }

        var yaw = screen.FigureYaw + MathF.Sin(screen.Drift * 0.35f) * 4f;
        var measure = DrawPlayerPreview(
            _wornPreview, _skinQuads, x, y, w, h, yaw, screen.Drift, bottomInset: 0f, materials);

        screen.FigureSkinFaces = measure.SkinFaces;
        screen.FigureOuterFaces = measure.OuterFaces;
        screen.FigureArmourFaces = measure.ArmourFaces;
        screen.FigureArmWidth = measure.ArmWidth;
        screen.FigureBounds = measure.Bounds;
        screen.FigureHeldItems = InHand(
            layout, catalogue, held, equipment[EquipSlot.Offhand], measure);
    }

    /// <summary>What each hand is holding, centred on the shared projection's two fists.</summary>
    /// <remarks>
    /// These remain readable inventory sprites rather than pretending the HUD owns the world item
    /// mesh. Their anchors are nevertheless the posed, turned fists, so they travel with the model;
    /// a small outward separation keeps both visible in a side view.
    /// </remarks>
    private int InHand(
        ScreenLayout layout, ItemRegistry catalogue, ItemStack held, ItemStack other,
        in ProjectedPlayerMeasure measure)
    {
        var size = layout.Size(13f);
        var centre = measure.Bounds.X + measure.Bounds.Z * 0.5f;
        var count = 0;

        if (!held.IsEmpty)
        {
            var x = MathF.Min(measure.MainHand.X - size * 0.5f, centre - size * 1.05f);
            Rect(_blocks, x, measure.MainHand.Y - size * 0.55f,
                size, size, Vector4.One, catalogue[held.Item].IconLayer);
            count++;
        }

        if (!other.IsEmpty)
        {
            var x = MathF.Max(measure.OffHand.X - size * 0.5f, centre + size * 0.05f);
            Rect(_blocks, x, measure.OffHand.Y - size * 0.55f,
                size, size, Vector4.One, catalogue[other.Item].IconLayer);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Hands the overlay the skin the player is wearing, so the figure can be cut out of it.
    /// </summary>
    /// <remarks>
    /// After construction rather than in it, because the window and its GL context exist before
    /// anybody has decided which skin is being worn — and because a skin can change without the
    /// overlay needing to be built again.
    /// </remarks>
    public void SetSkin(PlayerSkinData skin, TexturePack? pack = null)
    {
        _skin?.Dispose();
        _skin = new BlockTextureArray(_gl, [skin.Pixels], skin.Size);
        _wornPreview = ProjectedPlayerPreview.For(skin.Arms, skin.Legacy);

        BuildArmourSheets(pack);
        BuildGui(pack);
        BuildFont(pack);
    }

    /// <summary>Uploads a candidate for the SKINS preview without changing the worn player.</summary>
    public void SetSkinPreview(PlayerSkinData skin)
    {
        _previewSkin?.Dispose();
        _previewSkin = new BlockTextureArray(_gl, [skin.Pixels], skin.Size);
        _candidatePreview = ProjectedPlayerPreview.For(skin.Arms, skin.Legacy);
    }

    private void BuildGui(TexturePack? pack)
    {
        _gui?.Dispose();
        _gui = null;
        _guiPresent = [];

        var gui = GuiTextureSet.Load(pack);
        _gui = new BlockTextureArray(_gl, gui.Tiles, GuiTextureSet.Size);
        _guiPresent = gui.Present;
        Console.WriteLine($"interface   {gui.Summary}");
    }

    /// <summary>Rebuilds the 95 safe UI glyph layers from the selected sparse resource pack.</summary>
    private void BuildFont(TexturePack? pack)
    {
        var font = FontTextureSet.Load(pack);
        _font.Dispose();
        _font = new BlockTextureArray(_gl, font.Tiles, TileGen.Size);
        _advance = font.Advances;

        if (pack is null || font.BitmapGlyphs == 0 && font.SpaceAdvances == 0) return;

        Console.WriteLine(
            $"font        {font.Summary}"
            + (font.Omissions.Count > 0
                ? $"; not used: {string.Join(", ", font.Omissions)}"
                : "")
            + (font.Faults.Count > 0 ? $"; {font.Faults.Count} faults" : ""));
    }

    private bool HasGui(GuiTextureSet.Layer layer) =>
        (int)layer < _guiPresent.Length && _guiPresent[(int)layer];

    /// <summary>
    /// Paints every material's two armour sheets into one array for the figure to cut from.
    /// </summary>
    /// <remarks>
    /// ⚠ Painted rather than imported: <c>ArmourArt</c> draws these the way <c>TileGen</c> draws a
    /// tile, so there is nothing to wait for and nothing to fall back to. Built beside the skin
    /// because both are the same question — what is this body wearing — and doing it once means the
    /// figure never binds a texture per piece.
    /// </remarks>
    private void BuildArmourSheets(TexturePack? pack)
    {
        _armour?.Dispose();

        var sheets = ArmourSheets.Load(pack);

        // ⛔ ONE SIZE FOR THE WHOLE ARRAY, and it has to be the widest. A pack's nets arrive at its
        // own resolution and ours are painted at 64, so a set that mixes them — a copper plate of
        // ours beside an iron one of theirs — has two shapes in it, and an array takes one. Taking
        // the widest upscales ours rather than throwing away the pack's detail, which is the trade
        // this project already makes everywhere else.
        var size = ArmourArt.Width;
        foreach (var sheet in sheets) size = Math.Max(size, sheet.Width);

        var square = new byte[sheets.Length][];
        for (var i = 0; i < sheets.Length; i++) square[i] = ArmourSheets.Square(sheets[i], size);

        _armour = new BlockTextureArray(_gl, square, size);

        // ⚠ The net occupies the top HALF of the square, so the figure's v coordinates run to this
        // rather than to one. Kept as a number rather than assumed, because a format that ever
        // ships a square net would make the assumption silently wrong.
        _armourV = ArmourArt.Height / (float)ArmourArt.Width;
    }

    /// <summary>How far down the square array's layer the armour net actually reaches.</summary>
    private float _armourV = ArmourArt.Height / (float)ArmourArt.Width;

    /// <summary>A furnace's flame burning down, and the work filling toward the output.</summary>
    private void Hearth(ScreenLayout layout, HudScreen screen, bool chrome = true)
    {
        var z = layout.Zoom;
        var fuel = screen.Burning?.FuelLeft ?? 0f;
        var work = screen.Burning?.Fraction ?? 0f;

        var flame = ScreenLayout.FurnaceFlame;
        if (chrome)
            PanelBevel(layout, flame.X, flame.Y, flame.W, flame.H, raised: false,
                new Vector4(0.15f, 0.15f, 0.15f, 0.95f));

        // Quantised to whole panel pixels so it steps rather than slides, which is the aesthetic.
        var lit = MathF.Round((flame.H - 2f) * fuel);
        if (lit > 0f)
            Rect(_plain,
                layout.X(flame.X + 1f), layout.Y(flame.Y + 1f + (flame.H - 2f - lit)),
                layout.Size(flame.W - 2f), layout.Size(lit),
                new Vector4(1f, 0.55f + fuel * 0.3f, 0.18f, 1f));

        // The work arrow: a dim one all the way across, and a bright one as far as it has got.
        if (chrome) Arrow(layout, ScreenLayout.FurnaceArrow, 0.35f);
        if (work <= 0f) return;

        var full = ScreenLayout.FurnaceArrow;
        var done = MathF.Round(full.W * work);
        _ = z;
        Arrow(layout, (full.X, full.Y, (int)MathF.Max(1f, done), full.H), 1f);
    }

    /// <summary>The pointer, and whatever is riding on it.</summary>
    /// <remarks>
    /// Ours rather than the desktop's, so it scales with the interface, lands on the same pixel grid
    /// and is the same pointer on every machine. Drawn last, over everything.
    /// </remarks>
    /// <summary>
    /// The box that names whatever the pointer is over.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>Drawn LAST, after the panel and before the cursor</b>, which is the whole of its
    /// stacking: under the panel it describes it would be invisible, and over the cursor it would
    /// hide the thing doing the pointing.</para>
    /// <para>⚠ <b>It flips rather than clamping.</b> A box held inside the window by clamping sits
    /// <em>on top of</em> the square it is describing the moment the pointer nears an edge — which is
    /// the one place a tooltip must not be. Flipping to the other side of the pointer keeps the
    /// square visible wherever it is.</para>
    /// <para>⚠ Nothing is carried while a tooltip shows. A box under a dragged stack is a box
    /// describing a square the stack is not going into, and it covers what the drag is aiming at.
    /// </para>
    /// </remarks>
    private void Tip(ItemRegistry catalogue, Inventory inventory, Equipment equipment,
                     HudScreen screen, ScreenLayout layout, float w, float h)
    {
        screen.TipBox = Vector4.Zero;

        if (!screen.Carried.IsEmpty) return;
        if (screen.Hovered is not { } zone) return;

        Recipe? recipe = null;
        var payable = true;

        if (zone.Kind == ZoneKind.Recipe)
        {
            // ⚠ Two lists answer to one zone kind: the book's recipes and a stonecutter's offers.
            // The panel says which, exactly as the click handler does.
            var list = layout.Kind == PanelKind.Stonecutter ? screen.Cuts : screen.Recipes;
            if (zone.Index < 0 || zone.Index >= list.Count) return;

            recipe = list[zone.Index];
            payable = layout.Kind == PanelKind.Stonecutter
                      || (zone.Index < screen.Payable.Count && screen.Payable[zone.Index]);
        }

        // ⛳ The pockets go in so an unaffordable recipe can say WHAT IS MISSING rather than
        // restating its whole cost — #46's own line, and the game already knew the answer.
        var told = Tooltip.For(
            zone, Contents(inventory, equipment, screen, zone), catalogue, recipe, payable,
            inventory);

        if (told.IsEmpty) return;

        const float Title = 8f;
        const float NoteSize = 7f;
        const float Pad = 5f;

        var width = TextWidth(told.Title, Title);
        var note = told.Note.Length > 0 ? Wrap(told.Note, 150f, NoteSize) : null;
        var lines = new List<string>(note ?? []);

        foreach (var line in lines) width = MathF.Max(width, TextWidth(line, NoteSize));

        var boxWidth = width + Pad * 2f;
        var boxHeight = Title + lines.Count * (NoteSize + 2f) + Pad * 2f;

        // Below and right of the point by default — the corner a cursor's own arrow leaves free —
        // and flipped to the other side of it rather than clamped when that would run off.
        var x = screen.Pointer.X + 12f;
        var y = screen.Pointer.Y + 10f;

        if (x + boxWidth > w - 2f) x = screen.Pointer.X - boxWidth - 4f;
        if (y + boxHeight > h - 2f) y = screen.Pointer.Y - boxHeight - 2f;

        x = MathF.Round(MathF.Max(2f, x));
        y = MathF.Round(MathF.Max(2f, y));

        screen.TipBox = new Vector4(x, y, boxWidth, boxHeight);
        if (HasGui(GuiTextureSet.Layer.TooltipBackground))
            NineSlice(_guiOver, x, y, boxWidth, boxHeight,
                GuiTextureSet.Layer.TooltipBackground, 100f, 100f, 5f);
        else
            Bevel(x, y, boxWidth, boxHeight, raised: false, TipFill);
        Text(told.Title, x + Pad, y + Pad, Title, Ink);

        for (var i = 0; i < lines.Count; i++)
            Text(lines[i], x + Pad, y + Pad + Title + 2f + i * (NoteSize + 2f), NoteSize, InkFaint);
    }

    private void Pointer(ItemRegistry catalogue, HudScreen screen, ScreenLayout layout)
    {
        // One authored cursor pixel always occupies a whole number of layout pixels. The old twelve-
        // unit minimum squeezed a 16px tile onto a 3:4 grid, so alternating columns came out one and
        // two pixels wide even though texture filtering was nearest. It now steps 16, 32, ... with
        // the panel zoom and remains crisp at every integer overlay scale.
        var size = TileGen.Size * MathF.Max(1f, MathF.Ceiling(layout.Zoom * 0.5f));
        var at = screen.Pointer;

        if (!screen.Carried.IsEmpty)
        {
            // Under and behind the point, so the arrow still reads against whatever is being held.
            var held = MathF.Max(14f, MathF.Round(layout.Zoom * 14f));
            var hx = at.X - held * 0.30f;
            var hy = at.Y - held * 0.30f;

            SlotIcon(catalogue, screen.Carried, hx, hy, held, Vector4.One);
            if (screen.Carried.Count > 1)
                Number(screen.Carried.Count, hx + held, hy + held - 6f, MathF.Max(5f, held * 0.42f));
        }

        screen.CursorBox = new Vector4(at.X, at.Y, size, size);
        Rect(_cursorQuads, at.X, at.Y, size, size, Vector4.One, IconCursor);
    }

    // The interface's own palette. Named rather than written out at each use, so the whole thing
    // can be re-toned in one place and so two panels cannot drift apart by a hex digit.
    //
    // Strictly greyscale, and two tones doing all the work: one light, one dark, either side of a
    // mid fill. That is the whole of the look — a bevel is the two tones on opposite corners, a
    // pressed slot is the same two swapped, and a selection is the light one at full brightness.
    // There is deliberately no accent colour anywhere in the chrome: the moment one exists, every
    // panel starts needing a decision about whether it gets one, and the only things left with any
    // colour in them should be the blocks and the items, which are the point.
    private static readonly Vector4 PanelFill = new(0.36f, 0.36f, 0.36f, 0.97f);
    private static readonly Vector4 PanelLight = new(0.63f, 0.63f, 0.63f, 1f);
    private static readonly Vector4 PanelDark = new(0.15f, 0.15f, 0.15f, 1f);

    /// <summary>
    /// A tooltip's own fill: darker than a panel, and strictly neutral.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>USER, 2026-08-06: "we go with a pixel art styling in grey tones throughout the game."</b>
    /// This started life as 0.13/0.13/0.15 — a blue cast of two hundredths, invisible written down
    /// and exactly the sort of drift that makes an interface stop reading as one thing. Named here
    /// with the rest of the palette so the whole of it re-tones in one place and no two panels can
    /// disagree by a hex digit. Darker than <see cref="PanelFill"/> because it sits ON a panel and
    /// has to be told apart from it; the same three channels because everything here is.
    /// </remarks>
    private static readonly Vector4 TipFill = new(0.13f, 0.13f, 0.13f, 0.97f);

    /// <summary>Inside a square: nearly black, so what is in it is the only thing with a tone.</summary>
    /// <remarks>
    /// ⛔ <b>Measured off the user's own reference sheet for this screen, and it was the single
    /// biggest difference.</b> There, a well reads rgb 9 8 13 against a panel of 30 17 11 — about a
    /// third of the panel's brightness. Ours read 61 against 92, which is two thirds, and at that
    /// contrast a grid of squares is grey on grey and every icon in it looks washed. The blocks are
    /// the only things on this screen with any colour in them and the furniture round them should be
    /// getting out of the way.
    /// </remarks>
    private static readonly Vector4 SlotFill = new(0.085f, 0.085f, 0.095f, 0.98f);

    /// <summary>
    /// The rim round a square. Lighter than the panel, which is what makes a grid read as a grid.
    /// </summary>
    /// <remarks>
    /// ⚠ The reference sheet's rim is three times its panel's brightness, and ours cannot be — its
    /// panel is a dark brown and this one is a mid grey, so three times is off the top of the range.
    /// What carries over is that the rim is <em>distinct</em>: three tones, panel then rim then a
    /// near-black interior. Matched to the panel it is not a frame at all, which is what the first
    /// pass at this looked like.
    /// </remarks>
    private static readonly Vector4 SlotRim = new(0.50f, 0.49f, 0.53f, 1f);

    private static readonly Vector4 Highlight = new(0.96f, 0.96f, 0.96f, 1f);

    /// <summary>
    /// What is picked out. The one colour in the whole interface, and it is not chrome.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The rule this bends was written down deliberately and is worth restating.</b> The chrome
    /// is strictly greyscale so that the blocks are the only things on screen with any colour, and
    /// so that no panel ever needs a decision about whether it gets an accent. A selection is not
    /// chrome — it is a <em>state</em>, the answer to "which one", and it is the one thing on the
    /// screen that has to be found at a glance without reading anything. The reference sheet marks
    /// it in mint over a glow, and it is instantly the thing your eye goes to. Everything else stays
    /// grey.
    /// </remarks>
    private static readonly Vector4 Picked = new(0.55f, 0.98f, 0.78f, 1f);

    /// <summary>Text on a panel, and text on a panel that is not the one selected.</summary>
    private static readonly Vector4 Ink = new(0.95f, 0.95f, 0.95f, 1f);
    private static readonly Vector4 InkDim = new(0.70f, 0.70f, 0.70f, 1f);
    private static readonly Vector4 InkFaint = new(0.55f, 0.55f, 0.55f, 1f);

    /// <summary>
    /// A panel with a two-pixel bevel: lit from the top left, in shadow at the bottom right.
    /// </summary>
    /// <param name="raised">
    /// True for something standing out of the screen, false for something pressed into it.
    /// </param>
    /// <remarks>
    /// Two pixels rather than one, and light on two sides rather than a border on four. A single
    /// hairline round a rectangle is what a vector interface does; a bevel is what a pixel one does,
    /// and the difference is entirely that the light has a direction. Swapping which pair of sides
    /// is lit is the whole of "pressed in" versus "standing out", which is why one function draws
    /// both and a selected row can simply ask for the other one.
    /// </remarks>
    private void Bevel(float x, float y, float w, float h, bool raised, Vector4 fill)
    {
        x = MathF.Round(x);
        y = MathF.Round(y);
        w = MathF.Round(w);
        h = MathF.Round(h);

        var top = raised ? PanelLight : PanelDark;
        var bottom = raised ? PanelDark : PanelLight;

        Rect(_plain, x, y, w, h, fill);
        Rect(_plain, x, y, w, 2f, top);
        Rect(_plain, x, y, 2f, h, top);
        Rect(_plain, x, y + h - 2f, w, 2f, bottom);
        Rect(_plain, x + w - 2f, y, 2f, h, bottom);

        // The two corners where the light meets the shadow are neither, and leaving them to
        // whichever bar was drawn last is what makes a bevel look mitred wrong.
        Rect(_plain, x, y + h - 2f, 2f, 2f, PanelFill);
        Rect(_plain, x + w - 2f, y, 2f, 2f, PanelFill);
    }

    private void Frame(float x, float y, float w, float h)
    {
        Bevel(x, y, w, h, raised: true, PanelFill);

        // Small clean-room corner brackets and directional etching give plain fallback panels a
        // material identity. Kept inside the rim and strictly greyscale; selection remains the only
        // mint state, and pack-provided surfaces take the separate path below.
        if (w < 18f || h < 18f) return;
        Rect(_plain, x + 4f, y + 4f, 8f, 1f, PanelLight with { W = 0.42f });
        Rect(_plain, x + 4f, y + 4f, 1f, 6f, PanelLight with { W = 0.42f });
        Rect(_plain, x + w - 12f, y + h - 5f, 8f, 1f, PanelDark with { W = 0.72f });
        Rect(_plain, x + w - 5f, y + h - 10f, 1f, 6f, PanelDark with { W = 0.72f });
    }

    /// <summary>A standard pack surface tiled under a directional Driftwood frame.</summary>
    private void SurfaceFrame(float x, float y, float w, float h, GuiTextureSet.Layer surface)
    {
        x = MathF.Round(x);
        y = MathF.Round(y);
        w = MathF.Round(w);
        h = MathF.Round(h);

        if (!HasGui(surface))
        {
            Frame(x, y, w, h);
            return;
        }

        TileGui(_guiUnder, x + 2f, y + 2f, MathF.Max(0f, w - 4f), MathF.Max(0f, h - 4f),
            surface, 16f);

        Rect(_plain, x, y, w, 2f, PanelLight);
        Rect(_plain, x, y, 2f, h, PanelLight);
        Rect(_plain, x, y + h - 2f, w, 2f, PanelDark);
        Rect(_plain, x + w - 2f, y, 2f, h, PanelDark);
        Rect(_plain, x + 4f, y + 4f, 8f, 1f, PanelLight with { W = 0.48f });
        Rect(_plain, x + 4f, y + 4f, 1f, 6f, PanelLight with { W = 0.48f });
        Rect(_plain, x + w - 12f, y + h - 5f, 8f, 1f, PanelDark with { W = 0.78f });
        Rect(_plain, x + w - 5f, y + h - 10f, 1f, 6f, PanelDark with { W = 0.78f });
    }

    /// <summary>
    /// What is picked out: a line round it, and a wash of the same colour outside that.
    /// </summary>
    /// <remarks>
    /// The wash is what makes it findable across a screen of thirty-six squares — a one-pixel line
    /// is invisible at a glance and reads as an artefact of the grid when it is not. Two rings at a
    /// quarter and an eighth rather than a real blur: the overlay draws rectangles and a gradient
    /// here would want a texture, a second batch and a pass over the whole panel to be worth it.
    /// </remarks>
    private void Select(float x, float y, float w, float h)
    {
        x = MathF.Round(x);
        y = MathF.Round(y);
        w = MathF.Round(w);
        h = MathF.Round(h);

        for (var ring = 3; ring >= 1; ring--)
        {
            var wash = Picked with { W = 0.10f / ring };
            Rect(_plain, x - ring, y - ring, w + ring * 2f, ring, wash);
            Rect(_plain, x - ring, y + h, w + ring * 2f, ring, wash);
            Rect(_plain, x - ring, y - ring, ring, h + ring * 2f, wash);
            Rect(_plain, x + w, y - ring, ring, h + ring * 2f, wash);
        }

        Rect(_plain, x - 1f, y - 1f, w + 2f, 1f, Picked);
        Rect(_plain, x - 1f, y + h, w + 2f, 1f, Picked);
        Rect(_plain, x - 1f, y - 1f, 1f, h + 2f, Picked);
        Rect(_plain, x + w, y - 1f, 1f, h + 2f, Picked);
    }

    /// <summary>
    /// Draws a line of text, and returns how wide it came out.
    /// </summary>
    /// <param name="height">The glyph cell's height in layout units. Eight is a comfortable line.</param>
    /// <remarks>
    /// Each glyph is drawn as a full square quad and the pen advances by less than that, so
    /// neighbouring letters overlap into each other's transparent margin. That is not a bodge — it
    /// is what lets a variable-width font come out of an array where every layer is the same size,
    /// with no texture coordinates and no second batch format.
    /// </remarks>
    private float Text(ReadOnlySpan<char> line, float x, float y, float height, Vector4 colour, bool shadow = true)
    {
        var pen = MathF.Round(x);
        var top = MathF.Round(y);

        foreach (var c in line)
        {
            var glyph = DisplayGlyphOf(c);

            // A dark copy one unit down and right, so text stays readable over snow and over a cave
            // mouth alike. The same reason the crosshair is two colours.
            if (shadow) Rect(_text, pen + 1f, top + 1f, height, height, new Vector4(0f, 0f, 0f, colour.W * 0.75f), glyph);

            Rect(_text, pen, top, height, height, colour, glyph);
            pen += Advance(glyph, height);
        }

        return pen - MathF.Round(x);
    }

    /// <summary>How wide a line of text would come out, without drawing it.</summary>
    public float TextWidth(ReadOnlySpan<char> line, float height)
    {
        var width = 0f;

        foreach (var c in line)
        {
            width += Advance(DisplayGlyphOf(c), height);
        }

        return width;
    }

    /// <summary>The sizes this interface writes at: a row's label and value, and a note under them.</summary>
    public const float RowGlyph = 8f;

    public const float NoteGlyph = 7f;

    /// <summary>How wide the settings panel is, so anything checking what fits can ask.</summary>
    public const float MenuPanel = 232f;

    /// <summary>
    /// The game settings shell. Eight joined tabs and two honest text columns fit inside it at every
    /// supported scale; it is also exactly the total width already proven by the skin preview.
    /// </summary>
    public const float GameMenuPanel = 378f;

    /// <summary>How many wrapped lines a note comes to at the width it is drawn in.</summary>
    public int NoteLines(string note) => Wrap(note, MenuPanel - 8f, NoteGlyph).Count;

    /// <summary>
    /// How far the pen moves after one glyph, in whole layout units.
    /// </summary>
    /// <remarks>
    /// Rounded, and rounded in one place so the measurer and the drawer cannot disagree. A pen that
    /// advances by half a unit lands every second letter between two pixels, and the sampler
    /// resolves that by blurring it — which at this size is the difference between a pixel font and
    /// a smudge. It also means the ceiling on how narrow a glyph can get is a whole unit, so a
    /// comma never collides with what follows it.
    /// </remarks>
    private float Advance(int glyph, float height) =>
        MathF.Max(0f, MathF.Round(_advance[glyph] * (height / TileGen.Size)));

    /// <summary>
    /// Fits one unbroken label into a measured column. The renderer has no scissor rectangle, so an
    /// ellipsis is the hard guarantee that a result name and its right-aligned state never paint
    /// through one another.
    /// </summary>
    private string FitText(string line, float width, float height)
    {
        if (line.Length == 0 || TextWidth(line, height) <= width) return line;

        const string tail = "...";
        var tailWidth = TextWidth(tail, height);
        if (tailWidth >= width) return ".";

        var length = 0;
        var used = 0f;
        foreach (var c in line)
        {
            var step = Advance(DisplayGlyphOf(c), height);
            if (used + step + tailWidth > width) break;
            used += step;
            length++;
        }

        return length <= 0 ? tail : line[..length].TrimEnd() + tail;
    }

    /// <summary>
    /// UI prose predates the ASCII-only renderer and contains typographic punctuation. Map those
    /// marks to deliberate ASCII shapes instead of the old question-mark boxes; pack fonts then
    /// replace the same safe layers without changing string or input semantics.
    /// </summary>
    private static int DisplayGlyphOf(char c)
    {
        var shown = c switch
        {
            '\u00a0' => ' ',
            '\u00b7' or '\u2022' => '|',
            '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2212' => '-',
            '\u2018' or '\u2019' or '\u2032' => '\'',
            '\u201c' or '\u201d' or '\u2033' => '"',
            '\u2026' => '.',
            '\u00d7' => 'x',
            '\u2190' => '<',
            '\u2192' => '>',
            _ => c,
        };

        var glyph = TileGen.GlyphOf(shown);
        return glyph >= 0 ? glyph : TileGen.GlyphOf('?');
    }

    /// <summary>Draws a line centred on a point.</summary>
    private void TextCentred(string line, float centreX, float y, float height, Vector4 colour) =>
        Text(line, MathF.Round(centreX - TextWidth(line, height) / 2f), y, height, colour);

    /// <summary>Draws a number right-aligned at a point, digit by digit.</summary>
    /// <param name="glyph">
    /// How big one digit is. Takes a size because a count in a hotbar slot and a count in a panel
    /// zoomed to twice the pack's grid are the same number at two different scales.
    /// </param>
    private void Number(int value, float right, float top, float glyph = 6f)
    {
        var shadow = new Vector4(0f, 0f, 0f, 0.75f);
        var bright = Vector4.One;
        var lift = MathF.Max(0.75f, MathF.Round(glyph / 8f));

        var at = right;
        do
        {
            var digit = value % 10;
            value /= 10;
            at -= glyph;

            Rect(_iconQuads, at + lift, top + lift, glyph, glyph, shadow, IconDigit + digit);
            Rect(_iconQuads, at, top, glyph, glyph, bright, IconDigit + digit);
        }
        while (value > 0);
    }

    /// <summary>
    /// How many bands a partly-filled heart is torn into.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Eight, against a sixteen-pixel tile, so a band is two texels tall.</b> Sixteen would put
    /// the tear on every row and read as a fringe rather than a rip; four is coarse enough that the
    /// steps look like a staircase somebody drew on purpose. Eight is the one that reads as torn.
    /// </remarks>
    private const int TearBands = 8;

    /// <summary>One notch of any of the bars over the hotbar.</summary>
    private const float BarIcon = 9f;

    /// <summary>One pocket of the hotbar, which is what the bars are measured against.</summary>
    private const float HotbarSlot = 22f;

    /// <summary>
    /// How far from the middle the OUTER end of a bar sits: half the hotbar's own width.
    /// </summary>
    /// <remarks>
    /// <para>⛳⛳ <b>The bars hang off the ends of the rack under them rather than off the crosshair.</b>
    /// Reported by the user — <i>"they're kinda close to each other"</i> — and they were in fact
    /// touching, because both were measured from the centre outward and both are exactly ninety wide.
    /// The hotbar is ninety-nine to a side, so hanging the outer ends there leaves eighteen pixels of
    /// clear air in the middle, two icons' worth, and the assembly reads as one block of interface
    /// rather than two rows that happen to be adjacent.</para>
    /// <para>⛔ <b>Derived from the hotbar's own numbers, not a constant that happens to match them
    /// today.</b> The rack is what the eye lines these up against; a hand-written ninety-nine is a
    /// gap that silently stops matching the day the hotbar gains a tenth pocket, and nothing anywhere
    /// would fail.</para>
    /// </remarks>
    private static float BarSpan => Inventory.HotbarSlots * HotbarSlot / 2f;

    /// <summary>The far left of the pair of bars, which the health side hangs off.</summary>
    private static float BarsLeft(float w) => MathF.Round(w / 2f) - BarSpan;

    /// <summary>And the far right, which the food side hangs off.</summary>
    private static float BarsRight(float w) => MathF.Round(w / 2f) + BarSpan;

    /// <summary>
    /// How far one icon of a nearly-empty bar is shifted this instant, in whole screen pixels.
    /// </summary>
    /// <remarks>
    /// ⛳ The rule itself is <see cref="BarShake"/>'s, in Core, so <c>--audit</c> can walk it: the way
    /// it fails — a comparison the wrong way round, so every bar shivers permanently — is invisible
    /// in a screenshot taken at one instant and needs no window to catch.
    /// ⚠ Driven by <see cref="HudScreen.Drift"/>, which the client winds on, so the same number twice
    /// draws the same frame.
    /// </remarks>
    private static float Tremble(float drift, int index, int filled) =>
        BarShake.Offset(drift, index, filled);

    /// <summary>Ten hearts, drained by however much of one a blow actually cost.</summary>
    /// <remarks>
    /// <para>⛳⛳ <b>Torn, not sliced, and the user asked for exactly this:</b> <i>"we need to be able
    /// to reduce those hearts by portions depending on how much damage is taken … like a half heart
    /// would have like a jagged half filled with red?"</i> A single quad cuts its texture on a
    /// straight vertical line whatever the art behind it is, so the old half-heart was a heart
    /// guillotined down the middle. The red is drawn as eight bands now, each stopping a little
    /// short of or past the true level, so the edge between full and empty is ragged.</para>
    /// <para>⛳ <b>And it takes ANY fraction, not just a half.</b> The level is the real proportion of
    /// the heart that is left. Today <see cref="PlayerVitals.Health"/> counts in whole half-hearts so
    /// the only partial state a player can reach is a half — but the bar no longer assumes that, so
    /// the day a blow costs a third of a heart it already shows one.</para>
    /// <para>⚠ <b>The tear is a function of which band and which heart, so it does not move.</b> A
    /// wobble rolled per frame is a heart that boils while a player stands still, and a wobble rolled
    /// per band alone makes all ten hearts tear along the same line, which reads as a printing fault
    /// rather than as damage.</para>
    /// </remarks>
    private void Hearts(PlayerVitals vitals, float drift, float w, float h)
    {
        if (HasGui(GuiTextureSet.Layer.HeartContainer)
            && HasGui(GuiTextureSet.Layer.HeartFull)
            && HasGui(GuiTextureSet.Layer.HeartHalf))
        {
            SkinnedHearts(vitals, drift, w, h);
            return;
        }

        const float Icon = BarIcon;
        const int Count = PlayerVitals.MaxHealth / 2;

        var start = BarsLeft(w);
        var top = h - 44f;

        var empty = new Vector4(0.10f, 0.05f, 0.06f, 0.85f);
        var full = new Vector4(0.86f, 0.16f, 0.20f, 1f);

        // Icons still holding any red at all. Rounded UP, so a last half-heart still counts as one
        // thing left rather than as none — which is the moment the shiver matters most.
        var filled = (vitals.Health + 1) / 2;

        for (var i = 0; i < Count; i++)
        {
            var x = start + i * Icon;
            var y = top + Tremble(drift, i, filled);
            var size = Icon - 1f;

            // ⚠ The socket shivers with its heart rather than staying put. A fill that moves out of
            // a stationary socket is a heart coming apart, not a heart shaking.
            Rect(_iconQuads, x, y, size, size, empty, IconHeart);

            var level = Math.Clamp((vitals.Health - i * 2) / 2f, 0f, 1f);
            if (level <= 0f) continue;

            // A whole heart has no edge to tear, so it stays one quad — which also keeps the common
            // case, ten untouched hearts, at ten quads rather than eighty.
            if (level >= 1f)
            {
                Rect(_iconQuads, x, y, size, size, full, IconHeart);
                continue;
            }

            TornFill(x, y, size, level, full, IconHeart, i);
        }
    }

    private void SkinnedHearts(PlayerVitals vitals, float drift, float w, float h)
    {
        const float icon = BarIcon;
        const int count = PlayerVitals.MaxHealth / 2;
        var start = BarsLeft(w);
        var top = h - 44f;
        var filled = (vitals.Health + 1) / 2;

        for (var i = 0; i < count; i++)
        {
            var x = start + i * icon;
            var y = top + Tremble(drift, i, filled);
            var size = icon - 1f;
            Rect(_guiOver, x, y, size, size, Vector4.One, (int)GuiTextureSet.Layer.HeartContainer);

            var left = vitals.Health - i * 2;
            if (left >= 2)
                Rect(_guiOver, x, y, size, size, Vector4.One, (int)GuiTextureSet.Layer.HeartFull);
            else if (left == 1)
                Rect(_guiOver, x, y, size, size, Vector4.One, (int)GuiTextureSet.Layer.HeartHalf);
        }
    }

    /// <summary>
    /// Ten drumsticks, opposite the hearts, draining right to left.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>It empties from the crosshair OUTWARD, which is the mirror of the hearts.</b> Both
    /// bars lose their last icon at the far edge and their first beside the middle, so the two rows
    /// drain toward each other rather than both running the same way — and a glance at the gap in the
    /// middle reads as "how am I doing" without counting either row.</para>
    /// <para>⚠ <b>Drawn even when full, unlike the armour bar.</b> Armour is hidden at zero because a
    /// row of empty plates advertises a system a player has not met; hunger is the opposite — it is a
    /// bar that will kill you if you never notice it, so it is on screen from the first frame.</para>
    /// </remarks>
    private void Food(PlayerVitals vitals, float drift, float w, float h)
    {
        if (HasGui(GuiTextureSet.Layer.FoodEmpty)
            && HasGui(GuiTextureSet.Layer.FoodFull)
            && HasGui(GuiTextureSet.Layer.FoodHalf))
        {
            SkinnedFood(vitals, drift, w, h);
            return;
        }

        const float Icon = BarIcon;
        const int Count = PlayerVitals.MaxFood / 2;

        var right = BarsRight(w);
        var top = h - 44f;
        var filled = (vitals.Food + 1) / 2;

        var empty = new Vector4(0.09f, 0.07f, 0.04f, 0.85f);

        // ⛔ WHITE, and it is not a colour choice. The drumstick is a finished painting — meat, fat
        // and a bone, thirty thousand shades of it — so the tint has to be the identity or the whole
        // drawing collapses into one flat silhouette of itself. Every other icon on this bar is a
        // white mask being coloured here; this one is the exception and says so.
        var full = Vector4.One;

        for (var i = 0; i < Count; i++)
        {
            var x = right - (i + 1) * Icon;
            var y = top + Tremble(drift, i, filled);
            var size = Icon - 1f;

            Rect(_iconQuads, x, y, size, size, empty, IconFoodSocket);

            var level = Math.Clamp((vitals.Food - i * 2) / 2f, 0f, 1f);
            if (level <= 0f) continue;

            if (level >= 1f)
            {
                Rect(_iconQuads, x, y, size, size, full, IconFoodFull);
                continue;
            }

            TornFill(x, y, size, level, full, IconFoodFull, i);
        }
    }

    private void SkinnedFood(PlayerVitals vitals, float drift, float w, float h)
    {
        const float icon = BarIcon;
        const int count = PlayerVitals.MaxFood / 2;
        var right = BarsRight(w);
        var top = h - 44f;
        var filled = (vitals.Food + 1) / 2;

        for (var i = 0; i < count; i++)
        {
            var x = right - (i + 1) * icon;
            var y = top + Tremble(drift, i, filled);
            var size = icon - 1f;
            Rect(_guiOver, x, y, size, size, Vector4.One, (int)GuiTextureSet.Layer.FoodEmpty);

            var left = vitals.Food - i * 2;
            if (left >= 2)
                Rect(_guiOver, x, y, size, size, Vector4.One, (int)GuiTextureSet.Layer.FoodFull);
            else if (left == 1)
                Rect(_guiOver, x, y, size, size, Vector4.One, (int)GuiTextureSet.Layer.FoodHalf);
        }
    }

    /// <summary>Fills part of an icon, left to right, with a ragged edge instead of a straight one.</summary>
    private void TornFill(float x, float y, float size, float level, Vector4 tint, int layer, int seed)
    {
        var band = size / TearBands;

        for (var i = 0; i < TearBands; i++)
        {
            // ⚠ Two bands in three are pulled off the true level and the third sits on it, so the
            // edge is uneven without any band being far enough out to read as the wrong amount.
            var jag = ((seed * 7 + i * 13) % 3 - 1) * (0.5f / TearBands);
            var cut = Math.Clamp(level + jag, 0f, 1f);
            if (cut <= 0f) continue;

            Band(
                _iconQuads, x, y + i * band, size * cut, band, tint, layer,
                uWidth: cut, vStart: i / (float)TearBands, vHeight: 1f / TearBands);
        }
    }

    /// <summary>
    /// The armour bar, above the hearts, shown only when there is any.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Hidden at zero rather than drawn empty.</b> Ten empty plates over the hearts for the
    /// entire first hour of a world is a row that says "you have none of this" every frame to
    /// somebody who has not been shown that it exists yet — and it costs the hearts a row of screen
    /// they are much more likely to want to read.
    /// </remarks>
    private void ArmourBar(PlayerVitals vitals, float w, float h)
    {
        if (vitals.ArmourPoints <= 0) return;

        const float Icon = BarIcon;
        var Count = VitalBars.Icons(VitalBar.Armour);

        // ⛔ TEN PLATES FOR TWENTY-FOUR POINTS, and it used to be one plate per two — which made this
        // bar twelve icons where every other one is ten. Anchored to the hearts' left edge that ran
        // it eleven pixels PAST the middle of the screen and straight into the air bubbles, and the
        // layout check is what said so. A bar is a proportion of its maximum, not a tally of it; the
        // torn fill already draws any fraction, so nothing was needed to make ten work.
        var left = BarsLeft(w);
        var top = h - VitalBars.FromBottom(VitalBar.Armour);

        // ⛳ Lit while it is up, so raising the shield is visible from the bar rather than only from
        // the damage numbers. The one thing a player has to be able to tell at a glance is whether
        // the key they are holding is doing anything.
        var colour = vitals.ShieldRaised
            ? new Vector4(0.92f, 0.94f, 0.70f, 1f)
            : new Vector4(0.72f, 0.76f, 0.82f, 1f);

        var worn = vitals.ArmourPoints / (float)Armour.MaxPoints * Count;

        if (HasGui(GuiTextureSet.Layer.ArmourFull) && HasGui(GuiTextureSet.Layer.ArmourHalf))
        {
            for (var i = 0; i < Count; i++)
            {
                var level = Math.Clamp(worn - i, 0f, 1f);
                if (level <= 0f) continue;
                var layer = level >= 0.75f
                    ? GuiTextureSet.Layer.ArmourFull
                    : GuiTextureSet.Layer.ArmourHalf;
                Rect(_guiOver, left + i * Icon, top, Icon - 1f, Icon - 1f,
                    vitals.ShieldRaised ? new Vector4(1f, 1f, 0.82f, 1f) : Vector4.One, (int)layer);
            }
            return;
        }

        for (var i = 0; i < Count; i++)
        {
            var level = Math.Clamp(worn - i, 0f, 1f);
            if (level <= 0f) continue;

            if (level >= 1f)
            {
                Rect(_iconQuads, left + i * Icon, top, Icon - 1f, Icon - 1f, colour, IconPlate);
                continue;
            }

            TornFill(left + i * Icon, top, Icon - 1f, level, colour, IconPlate, i);
        }
    }

    /// <summary>
    /// What is in the other hand, in its own pocket beside the bar.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The whole reason the offhand was worth doing.</b> It has been real storage since the
    /// player screen landed, it accepts anything, and nothing about it appeared anywhere outside
    /// that screen — which makes it a place to put a stack and then forget you own it. Drawn beside
    /// the bar it is a slot; not drawn, it is a hole.
    /// </remarks>
    private void Offhand(
        ItemRegistry catalogue, Equipment equipment, PlayerVitals vitals, float w, float h)
    {
        var stack = equipment[EquipSlot.Offhand];
        if (stack.IsEmpty) return;

        const float Slot = 22f;
        const float Pad = 2f;

        var barWidth = Inventory.HotbarSlots * Slot;
        var left = MathF.Round((w - barWidth) / 2f) - Slot - 8f;

        // Lifted a little while it is up, which is the smallest gesture that reads as raised.
        var top = MathF.Round(h - Slot - 8f) - (vitals.ShieldRaised ? 4f : 0f);

        if (HasGui(GuiTextureSet.Layer.OffhandLeft))
            Rect(_guiUnder, left - 3f, top - 1f, 29f, 24f, Vector4.One,
                (int)GuiTextureSet.Layer.OffhandLeft);
        else
        {
            Bevel(left - 3f, top - 3f, Slot + 6f, Slot + 6f, raised: true, PanelFill);
            Bevel(left + 1f, top + 1f, Slot - 2f, Slot - 2f, raised: false, SlotFill);
        }
        if (vitals.ShieldRaised) Select(left + 1f, top + 1f, Slot - 2f, Slot - 2f);

        SlotIcon(catalogue, stack, left + Pad, top + Pad, Slot - Pad * 2f, Vector4.One);

        var type = catalogue[stack.Item];
        if (type.Durability > 0 && stack.Damage > 0)
        {
            var life = 1f - stack.Damage / (float)type.Durability;
            Rect(_plain, left + Pad, top + Slot - 4f, Slot - Pad * 2f, 2f, new Vector4(0f, 0f, 0f, 0.8f));
            Rect(_plain, left + Pad, top + Slot - 4f, (Slot - Pad * 2f) * life, 2f,
                new Vector4(1f - life, 0.25f + life * 0.65f, 0.2f, 1f));
        }

        if (stack.Count > 1) Number(stack.Count, left + Slot - 1.5f, top + Slot - 8.5f);
    }

    /// <summary>Breath, shown only while it is worth knowing about.</summary>
    /// <remarks>
    /// ⛔ <b>MOVED UP AND RIGHT, because hunger took the space it was in.</b> Breath started at the
    /// middle and ran right, which was empty screen until the food bar landed there — and two rows of
    /// icons drawn over each other is not something either of them reports. It sits over the food
    /// now, sharing its right edge, which is also the genre's own arrangement: air above food, armour
    /// above hearts, and the two columns kept apart.
    /// ⚠ <b>Right-aligned, and it empties toward the middle</b> like the food under it, so the pair
    /// drain the same way rather than in opposite directions.
    /// </remarks>
    /// <summary>
    /// Bubbles actually drawn on the last frame, and where the first one was put.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Reported by the user: the bubbles were not showing up at all.</b> Every part of them
    /// checked out on its own — the sheet loads, the tile has a hundred and thirteen texels of ink,
    /// the layer number is right, the call is in the draw — and not one of those is the same claim as
    /// <em>quads reached the screen</em>. So the renderer publishes what it drew, which is the rule
    /// <c>ScreenLayout</c> and <c>HudScreen.TipBox</c> already follow, and the gate can ask instead of
    /// somebody having to dive into a lake and look.
    /// </remarks>
    public int LastBubbles { get; private set; }

    /// <summary>Where the first bubble was put, in layout units.</summary>
    public Vector2 LastBubbleAt { get; private set; }

    /// <summary>The whole strip the bubbles ever occupy, in layout units — published whether or
    /// not any drew, so a check can confine a frame diff to exactly the bar's own ground.</summary>
    public (float X0, float Y0, float X1, float Y1) LastBubbleRow { get; private set; }

    private void Bubbles(PlayerVitals vitals, float drift, float w, float h)
    {
        LastBubbles = 0;

        LastBubbleRow = (
            BarsRight(w) - VitalBars.Icons(VitalBar.Breath) * BarIcon,
            h - VitalBars.FromBottom(VitalBar.Breath),
            BarsRight(w),
            h - VitalBars.FromBottom(VitalBar.Breath) + BarIcon);

        if (!vitals.Submerged && vitals.Breath >= PlayerVitals.MaxBreath) return;

        const float Icon = BarIcon;
        var count = VitalBars.Icons(VitalBar.Breath);

        var right = BarsRight(w);
        var top = h - VitalBars.FromBottom(VitalBar.Breath);
        var colour = new Vector4(0.72f, 0.88f, 1f, 0.95f);

        // ⛳⛳ WHOLE BUBBLES, and this is the one bar that does NOT tear — the user's own call and the
        // right one. Half a heart is a heart with a bite out of it and half a drumstick is one half
        // eaten, but half a bubble is not a thing: a bubble pops. So air says how much is left purely
        // by how many are still there, which is also why it needs no socket under it — a burst bubble
        // leaves nothing behind, where an eaten drumstick leaves the bone.
        var remaining = vitals.Breath * count / (float)PlayerVitals.MaxBreath;
        var left = (int)MathF.Ceiling(remaining);

        if (HasGui(GuiTextureSet.Layer.Air))
        {
            for (var i = 0; i < count; i++)
            {
                if (remaining <= i) continue;
                var y = top + Tremble(drift, i, left);
                var x = right - (i + 1) * Icon;
                Rect(_guiOver, x, y, Icon - 1f, Icon - 1f, Vector4.One, (int)GuiTextureSet.Layer.Air);
                if (LastBubbles == 0) LastBubbleAt = new Vector2(x, y);
                LastBubbles++;
            }
            return;
        }

        for (var i = 0; i < count; i++)
        {
            if (remaining <= i) continue;

            // The last few shiver, exactly as the other three do. Running out of air is the most
            // urgent thing that happens to a player, so if any row earns the shake it is this one.
            var y = top + Tremble(drift, i, left);
            var x = right - (i + 1) * Icon;

            Rect(_iconQuads, x, y, Icon - 1f, Icon - 1f, colour, IconBubble);

            if (LastBubbles == 0) LastBubbleAt = new Vector2(x, y);
            LastBubbles++;
        }
    }

    /// <param name="uWidth">Share of the tile's width to read, for a half-drawn icon.</param>
    private static void Rect(
        List<float> into, float x, float y, float w, float h, Vector4 colour, float layer = 0f, float uWidth = 1f)
    {
        Vertex(into, x, y, 0f, 0f, layer, colour);
        Vertex(into, x + w, y, uWidth, 0f, layer, colour);
        Vertex(into, x + w, y + h, uWidth, 1f, layer, colour);
        Vertex(into, x, y + h, 0f, 1f, layer, colour);
    }

    /// <summary>
    /// A rectangle showing one horizontal band of its tile, rather than the whole of it.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>What the torn fill on the health bar is made of.</b> A quad can only ever cut its texture
    /// on a straight line, so a partly-filled heart drawn as one rectangle is a heart sliced by a
    /// razor however jagged the art behind it is. Bands let each row of the shape stop in a different
    /// place, and the tear is the difference between where they stop.
    /// </remarks>
    private static void Band(
        List<float> into, float x, float y, float w, float h, Vector4 colour, float layer,
        float uWidth, float vStart, float vHeight)
    {
        var v0 = vStart;
        var v1 = vStart + vHeight;

        Vertex(into, x, y, 0f, v0, layer, colour);
        Vertex(into, x + w, y, uWidth, v0, layer, colour);
        Vertex(into, x + w, y + h, uWidth, v1, layer, colour);
        Vertex(into, x, y + h, 0f, v1, layer, colour);
    }

    /// <summary>
    /// One thing in a slot: a flat sprite, or a block seen as a block.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>A block drawn as one of its faces is not recognisable and the packs cannot help.</b>
    /// This was reported from the game — "I couldn't even tell that was a bench in the crafting
    /// window" — and the reason is that a bench's icon was its <em>top</em> tile, which is scored
    /// planks, which at sixteen pixels in a small square is a plank. Every wooden block in the game
    /// looked like every other one.</para>
    /// <para>⚠ <b>And there is no texture to import for it.</b> Measured: a real pack ships 795 item
    /// textures and not one of them is a block — no crafting table, no furnace, no stone. The genre
    /// does not have block icons; it <em>renders the block</em> into the slot at a fixed isometric
    /// angle, which is why a crafting table is recognisable there: you see its top and two sides at
    /// once. So this draws three faces rather than one, and the difference between a bench and a
    /// plank becomes the front face with the tools on it.</para>
    /// <para>⛳ <b>Every box of the model, not only a full cube.</b> Drawing just the cube case left
    /// every shaped block — slabs, stairs, fences, chests, doors, lanterns, campfires — falling back
    /// to one flat tile, so a stone block, a stone slab and stone stairs were the same grey square.
    /// That is forty of the hundred and ten things in the game, and it is the same complaint the
    /// bench produced, unfixed for a third of it.</para>
    /// <para>Flat is still right for a few, and they say so by not setting
    /// <see cref="ItemType.DrawsAsBlock"/>: a torch is art on crossed planes, so a solid of it is a
    /// solid of black, and a tool is not a box at all. ⛳ <b>Those still turn</b> — as a card rather
    /// than as a solid. See <see cref="TurningCard"/>.</para>
    /// </remarks>
    /// <param name="spin">
    /// Radians about the block's own upright axis, or 0 for the fixed three-quarter view.
    /// </param>
    /// <summary>The dark rim drawn behind every slot icon — the user's own example sheets
    /// outline each icon like a sticker, and it is most of why theirs sit ON the well where
    /// ours floated in it. Multiplying the texture down to near-black keeps the cut-out
    /// silhouette, so the rim is the icon's own shape at one texel's offset.</summary>
    private static readonly Vector4 IconRim = new(0.055f, 0.045f, 0.04f, 1f);

    private void SlotIcon(
        ItemRegistry catalogue, ItemStack stack, float x, float y, float size, Vector4 tint,
        float spin = 0f)
    {
        if (stack.IsEmpty) return;

        var type = catalogue[stack.Item];
        var rim = new Vector4(IconRim.X, IconRim.Y, IconRim.Z, tint.W);
        var o = size / 16f;

        if (!type.DrawsAsBlock || type.IconModel is not { Icon.Length: > 0 } model)
        {
            if (spin != 0f)
            {
                TurningCard(type.IconLayer, x - o, y, size, rim, spin);
                TurningCard(type.IconLayer, x + o, y, size, rim, spin);
                TurningCard(type.IconLayer, x, y - o, size, rim, spin);
                TurningCard(type.IconLayer, x, y + o, size, rim, spin);
                TurningCard(type.IconLayer, x, y, size, tint, spin);
            }
            else
            {
                Rect(_blocks, x - o, y, size, size, rim, type.IconLayer);
                Rect(_blocks, x + o, y, size, size, rim, type.IconLayer);
                Rect(_blocks, x, y - o, size, size, rim, type.IconLayer);
                Rect(_blocks, x, y + o, size, size, rim, type.IconLayer);
                Rect(_blocks, x, y, size, size, tint, type.IconLayer);
            }
            return;
        }

        if (spin != 0f)
        {
            TurningIcon(model, x - o, y, size, rim, spin);
            TurningIcon(model, x + o, y, size, rim, spin);
            TurningIcon(model, x, y - o, size, rim, spin);
            TurningIcon(model, x, y + o, size, rim, spin);
            TurningIcon(model, x, y, size, tint, spin);
        }
        else
        {
            foreach (var box in model.Icon) IconBox(box, x - o, y, size, rim);
            foreach (var box in model.Icon) IconBox(box, x + o, y, size, rim);
            foreach (var box in model.Icon) IconBox(box, x, y - o, size, rim);
            foreach (var box in model.Icon) IconBox(box, x, y + o, size, rim);
            foreach (var box in model.Icon) IconBox(box, x, y, size, tint);
        }
    }

    /// <summary>How thick a flat thing is made so it can be turned, in sixteenths.</summary>
    /// <remarks>
    /// Two, which is what the format gives an extruded item sprite and what the dropped ones already
    /// use. ⚠ <b>Thickness is not decoration here</b> — a plane with none is invisible exactly
    /// edge-on, twice a turn, which on a page somebody is reading is a torch that blinks out rather
    /// than one that is turning.
    /// </remarks>
    private const float CardThick = 2f;

    /// <summary>
    /// A flat thing turning: its own picture on a slab thin enough to still read as a picture.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>From the user, who counted:</b> <i>"there's a couple of things like the torch and
    /// ladder aren't spinning"</i>. They had been left out on purpose — a torch is a cut-out on
    /// crossed planes and a ladder is one sheet, so drawing either as a <em>solid</em> is three
    /// shaded copies of a shape with holes in it — but <b>"left out on purpose" and "forgotten" look
    /// identical from the front</b>, and the call was wrong anyway.</para>
    /// <para>⛔ <b>The first answer was to squash the picture as it turned, and the check caught it
    /// doing almost nothing.</b> A torch's sprite is a narrow upright stick in the middle of a
    /// transparent tile, so narrowing it by a tenth moves no pixels at all — measured at 0% where a
    /// block moved 35%. Squashing is a fine impression of turning for something that fills its tile
    /// and no impression whatever for something that does not.</para>
    /// <para>So a card is a <b>real slab two sixteenths thick</b> wearing its picture on every face,
    /// put through exactly the same turn, sort and shading as a block. It leans and shifts rather
    /// than merely narrowing, the cut-out keeps the silhouette the artist drew on every face — so a
    /// torch is torch-shaped edge-on too — and there is one code path for turning rather than two.
    /// </para>
    /// </remarks>
    private void TurningCard(ushort layer, float x, float y, float size, Vector4 tint, float spin)
    {
        var cos = MathF.Cos(spin);
        var wide = MathF.Max(CardThick / 16f, MathF.Abs(cos)) * size;
        var left = x + (size - wide) * 0.5f;

        // Mirrored once it has turned past edge-on, so the far side of a card is its far side.
        var (u0, u1) = cos >= 0f ? (0f, 1f) : (1f, 0f);

        // Darker as it turns away, the way a face of a block is. Square-on is full brightness.
        var shade = 0.70f + 0.30f * MathF.Abs(cos);

        Quad(_blocks, layer, tint * new Vector4(shade, shade, shade, 1f),
            left, y, u0, 0f,
            left + wide, y, u1, 0f,
            left + wide, y + size, u1, 1f,
            left, y + size, u0, 1f);
    }

    /// <summary>
    /// Radians a second a block in the recipe book turns.
    /// </summary>
    /// <remarks>
    /// Slow on purpose: about eleven seconds a turn. Fast enough that a page is obviously alive and
    /// slow enough to read a shape off one without waiting for it — a spin quick enough to be
    /// noticed is a spin quick enough to be annoying on a screen somebody is browsing.
    /// </remarks>
    private const float BookTurn = 0.55f;

    /// <summary>How much of the square a turning block is allowed, so its corners stay inside it.</summary>
    /// <remarks>
    /// ⚠ <b>A cube is 41% wider corner-on than face-on</b>, so one drawn at the size the fixed view
    /// uses would swing outside its own square twice a turn and clip against the panel. This is the
    /// price of turning, not a preference — a turning icon reads a little smaller than a still one.
    /// </remarks>
    private const float TurnFit = 0.70f;

    /// <summary>
    /// A block turning on the spot, drawn as its own faces from whatever angle it is at.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>Sorted and culled here rather than left to a depth buffer.</b> The still view gets
    /// away with drawing three faces in a fixed order because the same three always face you; the
    /// moment it turns, both which faces are visible and which order they go in change every frame.
    /// Doing it on the processor keeps the whole thing inside the overlay's existing batch — no
    /// second pass, no depth buffer to clear in the middle of the interface, and no state to put
    /// back afterwards.</para>
    /// <para>A face pointing away is dropped outright, and what is left is drawn far to near. That
    /// is exact for a solid and near enough for everything we have; a shape with a hole in it may
    /// show its own far side through the hole, which is the same trade the dropped items make.</para>
    /// <para>⚠ <b>Ties are broken by the order the model baked them in</b>, so two faces at the same
    /// depth — a grass block's overlay lies exactly on the dirt under it — come out in the same
    /// order every frame instead of flickering between them.</para>
    /// </remarks>
    private void TurningIcon(
        BlockModel model, float x, float y, float size, Vector4 tint, float spin)
    {
        var h = size * 0.5f * TurnFit;
        var ox = x + size * 0.5f;
        var oy = y + size * 0.5f;

        var cos = MathF.Cos(spin);
        var sin = MathF.Sin(spin);

        // A direction, turned. Nothing is moved: a normal has no position to be moved about.
        Vector3 Spun(Vector3 d) => new(d.X * cos + d.Z * sin, d.Y, -d.X * sin + d.Z * cos);

        // A point of the block, turned about its own middle.
        Vector3 Turn(Vector3 p) => Spun(p - new Vector3(0.5f));

        // Flattened the same way the still view flattens it: sx = (x - z) * h and
        // sy = (x + z) * h/2 - y * h, about the middle of the square.
        (float X, float Y) Flatten(Vector3 t) =>
            (ox + (t.X - t.Z) * h, oy + (t.X + t.Z) * (h * 0.5f) - t.Y * h);

        _turning.Clear();

        for (var i = 0; i < model.Quads.Length; i++)
        {
            var quad = model.Quads[i];

            var (nx, ny, nz) = Faces.Normals[quad.Face];
            var normal = Spun(new Vector3(nx, ny, nz));

            // The eye is off the block's +x +y +z corner, so a face is seen when its own direction
            // leans that way at all.
            if (normal.X + normal.Y + normal.Z <= 0f) continue;

            var depth = 0f;
            foreach (var corner in quad.Corners)
            {
                var t = Turn(corner.Position);
                depth += t.X + t.Y + t.Z;
            }

            _turning.Add((depth, i, normal));
        }

        // Far first. The index breaks a tie so coplanar faces keep the order they were baked in.
        _turning.Sort((a, b) =>
            a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth) : a.Index.CompareTo(b.Index));

        foreach (var (_, index, normal) in _turning)
        {
            var quad = model.Quads[index];

            // The three shades the still view uses, blended by which way this face now points, so a
            // block turning through the angles between them shades smoothly rather than jumping.
            var weight = MathF.Abs(normal.X) + MathF.Abs(normal.Y) + MathF.Abs(normal.Z);
            var shade = weight <= 0f
                ? 1f
                : (MathF.Abs(normal.Y) + MathF.Abs(normal.Z) * 0.85f + MathF.Abs(normal.X) * 0.46f) / weight;

            var lit = tint * new Vector4(shade, shade, shade, 1f);

            var a = Flatten(Turn(quad.Corners[0].Position));
            var b = Flatten(Turn(quad.Corners[1].Position));
            var c = Flatten(Turn(quad.Corners[2].Position));
            var d = Flatten(Turn(quad.Corners[3].Position));

            Quad(_blocks, quad.Layer, lit,
                a.X, a.Y, quad.Corners[0].U, quad.Corners[0].V,
                b.X, b.Y, quad.Corners[1].U, quad.Corners[1].V,
                c.X, c.Y, quad.Corners[2].U, quad.Corners[2].V,
                d.X, d.Y, quad.Corners[3].U, quad.Corners[3].V);
        }
    }

    /// <summary>Reused so a page of turning blocks costs no allocation per frame.</summary>
    private readonly List<(float Depth, int Index, Vector3 Normal)> _turning = [];

    /// <summary>The three faces one box of a block shows, seen from off its +x +y +z corner.</summary>
    /// <remarks>
    /// <para>⛳ <b>One projection, applied to a box, rather than a cube written out as nine
    /// numbers.</b> A point of the block maps to the slot as
    /// <c>sx = ox + (bx - bz) * h</c> and <c>sy = oy - h + (bx + bz) * h/2 + (1 - by) * h</c>, with
    /// <c>h</c> half the square. It is the standard two-to-one isometric, and it is worth writing
    /// down because it is what makes a unit cube fill the square exactly — every derived number
    /// below used to be a separate literal, and a slab could not have been expressed at all.</para>
    /// <para>The shading is what tells the three faces apart. Without it they read as one flat
    /// hexagon, whatever is drawn on them.</para>
    /// </remarks>
    private void IconBox(BlockModel.IconBox box, float x, float y, float size, Vector4 tint)
    {
        var h = size * 0.5f;
        var ox = x + h;
        var oy = y + h;

        (float X, float Y) At(float bx, float by, float bz) =>
            (ox + (bx - bz) * h, oy - h + (bx + bz) * (h * 0.5f) + (1f - by) * h);

        var (lo, hi) = (box.Min, box.Max);

        // The top, seen as a diamond. Its four corners are the box's own footprint at its top.
        var tBack = At(lo.X, hi.Y, lo.Z);
        var tRight = At(hi.X, hi.Y, lo.Z);
        var tFront = At(hi.X, hi.Y, hi.Z);
        var tLeft = At(lo.X, hi.Y, hi.Z);

        Quad(_blocks, box.Top, tint,
            tLeft.X, tLeft.Y, lo.X, hi.Z,
            tBack.X, tBack.Y, lo.X, lo.Z,
            tRight.X, tRight.Y, hi.X, lo.Z,
            tFront.X, tFront.Y, hi.X, hi.Z);

        // The +z face, to the lower left. Texture runs across x and down from the top of the box.
        var lTop = At(lo.X, hi.Y, hi.Z);
        var lTopIn = At(hi.X, hi.Y, hi.Z);
        var lBottomIn = At(hi.X, lo.Y, hi.Z);
        var lBottom = At(lo.X, lo.Y, hi.Z);

        // 0.85 and 0.46: measured off the user's own example sheet (block examples.png,
        // 2026-08-09), not invented — its stone icon reads 135/114/62 across the three faces,
        // and that hard right-face drop is most of why those icons pop where ours sat flat.
        Quad(_blocks, box.Left, tint * new Vector4(0.85f, 0.85f, 0.85f, 1f),
            lTop.X, lTop.Y, lo.X, 1f - hi.Y,
            lTopIn.X, lTopIn.Y, hi.X, 1f - hi.Y,
            lBottomIn.X, lBottomIn.Y, hi.X, 1f - lo.Y,
            lBottom.X, lBottom.Y, lo.X, 1f - lo.Y);

        // The +x face, to the lower right. Texture runs across z the other way.
        var rTopIn = At(hi.X, hi.Y, hi.Z);
        var rTop = At(hi.X, hi.Y, lo.Z);
        var rBottom = At(hi.X, lo.Y, lo.Z);
        var rBottomIn = At(hi.X, lo.Y, hi.Z);

        Quad(_blocks, box.Right, tint * new Vector4(0.46f, 0.46f, 0.46f, 1f),
            rTopIn.X, rTopIn.Y, 1f - hi.Z, 1f - hi.Y,
            rTop.X, rTop.Y, 1f - lo.Z, 1f - hi.Y,
            rBottom.X, rBottom.Y, 1f - lo.Z, 1f - lo.Y,
            rBottomIn.X, rBottomIn.Y, 1f - hi.Z, 1f - lo.Y);
    }

    /// <summary>
    /// The game's name, cut out of timber, floating on a swell.
    /// </summary>
    /// <param name="drift">Seconds, for the float. The same number every frame draws it still.</param>
    /// <remarks>
    /// <para><b>The letters are carved from one plank, not painted with one.</b> Every cell reads
    /// its own patch of the plank layer at its own position in the word, so the grain runs
    /// unbroken across all nine letters as though they were cut from a single board — which is what
    /// makes it read as timber rather than as letters with a wood pattern on them. It also means
    /// that with a texture pack loaded the title is made of <em>that pack's</em> wood, for nothing.
    /// </para>
    /// <para><b>The depth converges.</b> Each cell is extruded toward the middle of the word rather
    /// than in one direction, so the sides of the left letters are seen on their right and the right
    /// letters on their left — one viewpoint, in front of the centre. A title extruded uniformly
    /// reads as a drop shadow; this reads as a solid thing being looked at.</para>
    /// <para><b>And it drifts.</b> Each letter rides a sine wave phased by how far along the word it
    /// is, so a slow swell travels through the name — which is the one animation the word itself
    /// asks for.</para>
    /// </remarks>
    /// <summary>
    /// How big one block of the title is, in layout units.
    /// </summary>
    /// <remarks>
    /// Measured against the width of the screen rather than the width of the panel under it. A
    /// title is the widest thing on a screen and a panel is not — sized to the panel it came out
    /// three units a block, which is a caption. Whole units, like everything else here.
    /// </remarks>
    public static float TitleCell(float width) =>
        MathF.Max(2f, MathF.Round(width * 0.58f / TitleArt.Cells));

    private void Title(HudScreen screen, float centreX, float top, float cell, float drift)
    {
        var depth = MathF.Max(1f, MathF.Round(cell * 0.9f));
        var width = TitleArt.Cells * cell;
        var left = centreX - width * 0.5f;
        var middle = TitleArt.Cells * 0.5f;

        // Cleared each frame, so "no title drawn" is a state rather than last frame's answer.
        screen.TitleInk = screen.TitleGap = new Vector2(-1f, -1f);

        // How far across one plank tile a single cell reaches. Eight cells to a board, so the grain
        // is coarse enough to read at this size and repeats slowly enough not to look tiled.
        const float Board = 8f;

        for (var index = 0; index < TitleArt.Word.Length; index++)
        {
            var letter = TitleArt.Word[index];
            var column = index * (TitleArt.LetterWidth + TitleArt.Gap);

            // Phased by position, so the swell travels rather than the word bobbing as one piece.
            var bob = MathF.Sin(drift * 1.5f - index * 0.55f) * cell * 0.55f;
            var lean = MathF.Cos(drift * 1.1f - index * 0.4f) * cell * 0.12f;

            for (var y = 0; y < TitleArt.LetterHeight; y++)
            for (var x = 0; x < TitleArt.LetterWidth; x++)
            {
                var cx = column + x;
                var px = left + cx * cell + lean;
                var py = top + y * cell + bob;
                var filled = TitleArt.Filled(letter, x, y);

                // ⛳ Where the first of each landed, with this letter's own bob and lean in it —
                // which is the whole point, since those are what a check working from the grid
                // cannot know. Recorded before the skip, because an empty cell is never drawn and
                // is half of what makes the pair a check.
                if (filled && screen.TitleInk.X < 0f)
                    screen.TitleInk = new Vector2(px + cell * 0.5f, py + cell * 0.5f);
                else if (!filled && screen.TitleGap.X < 0f)
                    screen.TitleGap = new Vector2(px + cell * 0.5f, py + cell * 0.5f);

                if (!filled) continue;

                // Toward the middle of the word, and a little down: one viewpoint in front of it.
                var toward = cx < middle ? 1f : -1f;

                var u0 = cx % Board / Board;
                var v0 = y % Board / Board;
                var u1 = u0 + 1f / Board;
                var v1 = v0 + 1f / Board;

                // The side, laid down first so the face sits on top of it. Drawn as the same timber
                // darkened rather than as a flat colour, so the grain carries round the edge.
                for (var step = (int)depth; step >= 1; step--)
                {
                    var shade = 0.30f + 0.16f * (1f - step / depth);
                    Quad(_blocks, StarterBlocks.LayerPlanks, new Vector4(shade, shade, shade, 1f),
                        px + step * toward, py + step * 0.55f, u0, v0,
                        px + step * toward + cell, py + step * 0.55f, u1, v0,
                        px + step * toward + cell, py + step * 0.55f + cell, u1, v1,
                        px + step * toward, py + step * 0.55f + cell, u0, v1);
                }

                Quad(_blocks, StarterBlocks.LayerPlanks, Vector4.One,
                    px, py, u0, v0,
                    px + cell, py, u1, v0,
                    px + cell, py + cell, u1, v1,
                    px, py + cell, u0, v1);
            }
        }
    }

    /// <summary>Four corners in any arrangement, for the shapes a rectangle cannot describe.</summary>
    private static void Quad(
        List<float> into, float layer, Vector4 colour,
        float x0, float y0, float u0, float v0,
        float x1, float y1, float u1, float v1,
        float x2, float y2, float u2, float v2,
        float x3, float y3, float u3, float v3)
    {
        Vertex(into, x0, y0, u0, v0, layer, colour);
        Vertex(into, x1, y1, u1, v1, layer, colour);
        Vertex(into, x2, y2, u2, v2, layer, colour);
        Vertex(into, x3, y3, u3, v3, layer, colour);
    }

    /// <summary>A rectangle reading an arbitrary patch of its texture, rather than the whole of it.</summary>
    /// <remarks>
    /// Everything else here draws one tile, whole, which is what a texture array is for. A skin
    /// sheet is the exception: it is one image holding thirty six patches, and a figure made of it
    /// needs to name the corners of each one.
    /// </remarks>
    private static void RectUv(
        List<float> into, float x, float y, float w, float h,
        float u0, float v0, float u1, float v1, Vector4 colour, int layer = 0)
    {
        Vertex(into, x, y, u0, v0, layer, colour);
        Vertex(into, x + w, y, u1, v0, layer, colour);
        Vertex(into, x + w, y + h, u1, v1, layer, colour);
        Vertex(into, x, y + h, u0, v1, layer, colour);
    }

    /// <summary>Repeats a standard 16px-style pack surface without stretching its grain.</summary>
    private static void TileGui(
        List<float> into, float x, float y, float w, float h,
        GuiTextureSet.Layer layer, float tile)
    {
        if (w <= 0f || h <= 0f || tile <= 0f) return;

        for (var py = 0f; py < h; py += tile)
        for (var px = 0f; px < w; px += tile)
        {
            var wide = MathF.Min(tile, w - px);
            var tall = MathF.Min(tile, h - py);
            RectUv(into, x + px, y + py, wide, tall,
                0f, 0f, wide / tile, tall / tile, Vector4.One, (int)layer);
        }
    }

    /// <summary>Stretches the middle of a pack sprite while leaving its corners at authored size.</summary>
    private static void NineSlice(
        List<float> into, float x, float y, float w, float h, GuiTextureSet.Layer layer,
        float sourceW, float sourceH, float edge, Vector4? tint = null)
    {
        var ex = MathF.Min(edge, w * 0.5f);
        var ey = MathF.Min(edge, h * 0.5f);
        var u = edge / sourceW;
        var v = edge / sourceH;
        var colour = tint ?? Vector4.One;

        var xs = new[] { x, x + ex, x + w - ex, x + w };
        var ys = new[] { y, y + ey, y + h - ey, y + h };
        var us = new[] { 0f, u, 1f - u, 1f };
        var vs = new[] { 0f, v, 1f - v, 1f };

        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
            RectUv(into,
                xs[column], ys[row], xs[column + 1] - xs[column], ys[row + 1] - ys[row],
                us[column], vs[row], us[column + 1], vs[row + 1], colour, (int)layer);
    }

    private static void Vertex(List<float> into, float x, float y, float u, float v, float layer, Vector4 colour)
    {
        into.Add(x);
        into.Add(y);
        into.Add(u);
        into.Add(v);
        into.Add(layer);
        into.Add(colour.X);
        into.Add(colour.Y);
        into.Add(colour.Z);
        into.Add(colour.W);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        _icons.Dispose();
        _font.Dispose();
        _skin?.Dispose();
        _previewSkin?.Dispose();
        _armour?.Dispose();
        _gui?.Dispose();
        _shader.Dispose();
    }
}
