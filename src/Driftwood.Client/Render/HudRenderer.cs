using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;
using Driftwood.Core.Items;
using Driftwood.Core.Textures;
using Driftwood.Core.Ui;
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
}

/// <summary>The tabs of the game screen.</summary>
public enum GameTab
{
    Controls,
    Video,
    Audio,
    World,
    Saves,
}

/// <summary>
/// One line of a settings tab: what it is, and what it is currently set to.
/// </summary>
/// <param name="Heading">True for a group title, which has no value and cannot be selected.</param>
/// <param name="Note">A second, dimmer line under it — what a setting costs, or when it applies.</param>
/// <param name="Edits">
/// A line of typed text this row is the box for, drawn where the value would be.
/// </param>
/// <remarks>
/// ⛳ <b>A text field is a row, which is why it cost almost nothing.</b> Scrolling, hit testing, the
/// note strip and the way the keyboard walks the list are all already here and all already right; a
/// field of its own would have been a second one of each. It also means every screen that has rows
/// can have a box to type in without learning anything new.
/// </remarks>
public readonly record struct MenuRow(
    string Label, string Value = "", bool Heading = false, string Note = "", TextField? Edits = null);

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

    /// <summary>What the pointer is over, refreshed each frame from the layout.</summary>
    public Zone? Hovered;

    public bool IsOpen => Kind != HudScreenKind.None;

    /// <summary>
    /// True for the screens drawn on the pack's own container panel.
    /// </summary>
    /// <remarks>
    /// Those three carry the player's own pockets in their bottom half, which is why the bar along
    /// the bottom of the world is not drawn under them — it would be the same nine slots twice.
    /// </remarks>
    public bool IsContainer =>
        Kind is HudScreenKind.Player or HudScreenKind.Bench or HudScreenKind.Furnace
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
    private readonly BlockTextureArray _font;
    private readonly int[] _advance;

    private readonly List<float> _plain = new(4096);
    private readonly List<float> _blocks = new(2048);
    private readonly List<float> _iconQuads = new(2048);
    private readonly List<float> _text = new(8192);
    private readonly List<float> _skinQuads = new(256);

    /// <summary>The player's own sheet, as a single-layer array so the batcher can sample it.</summary>
    private BlockTextureArray? _skin;

    /// <summary>The model the sheet dresses, so the figure follows it rather than a copy of it.</summary>
    private ModelBox[] _dollBoxes = [];

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

    public unsafe HudRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);

        var icons = new List<byte[]> { TileGen.Heart(), TileGen.Bubble() };
        icons.AddRange(TileGen.Digits());
        icons.Add(TileGen.Cursor());
        icons.AddRange(TileGen.EquipGhosts());
        icons.Add(TileGen.Bloom());
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
        _plain.Clear();
        _blocks.Clear();
        _iconQuads.Clear();
        _text.Clear();
        _skinQuads.Clear();
        layout.Clear();

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
        }

        // A container panel carries the player's own pockets in its bottom half, so the bar along
        // the bottom of the world would be the same nine slots drawn twice, in two different sizes.
        // ⚠ And nobody is carrying anything before they have started: an empty bar under the menu
        // reads as a game somebody is already losing at.
        if (!screen.IsContainer && screen.Kind != HudScreenKind.Start)
            Hotbar(catalogue, inventory, w, h);

        if (!screen.IsOpen)
        {
            Hearts(vitals, w, h);
            Bubbles(vitals, w, h);
        }

        Toasts(toasts, w);

        // Last, over everything, because a pointer that goes behind a panel is a pointer somebody
        // is about to lose. What is on the cursor rides under it, offset so the hotspot still reads.
        if (screen.IsOpen) Pointer(catalogue, screen, layout);

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

        Flush(_plain, textured: false, null);
        Flush(_skinQuads, textured: true, _skin);
        Flush(_blocks, textured: true, blocks);
        Flush(_iconQuads, textured: true, _icons);
        Flush(_text, textured: true, _font);

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

    /// <summary>The bar, one slot per pocket, with each block's own tile and its count.</summary>
    private void Hotbar(ItemRegistry catalogue, Inventory inventory, float w, float h)
    {
        const float Slot = 22f;
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
        Rect(_plain, 0f, 0f, w, h, new Vector4(0.04f, 0.04f, 0.04f, 0.72f));

        // The three container screens are drawn on the pack's own panel, share every square below
        // the halfway line, and carry the recipe book beside them. Everything else is settings.
        if (screen.IsContainer)
        {
            Container(catalogue, inventory, equipment, screen, layout, w, h);
            Footer(screen, w, h);
            return;
        }

        const float Panel = MenuPanel;
        var left = MathF.Round((w - Panel) / 2f);

        // Sat where it is tall rather than always a fixed way down the screen.
        var tall = 22f + Math.Min(screen.Rows.Count, ScreenLayout.MenuLines(h)) * ScreenLayout.MenuLine + 12f;
        var top = MathF.Round((h - tall) * 0.42f);

        // The name, over the panel. ⛳ It belongs on the start screen and the start screen does not
        // exist yet, so it lives here meanwhile — which is also somewhere it is worth having: a
        // paused game is the other place a title reads as a title rather than as decoration.
        var cell = TitleCell(w);
        var titleTop = MathF.Max(6f, top - TitleArt.LetterHeight * cell - 26f);
        Title(w * 0.5f, titleTop, cell, screen.Drift);

        Tabs(screen, layout, left, top, Panel);
        Rows(screen, layout, left, top + 22f, Panel, h);

        Footer(screen, w, h);
    }

    /// <summary>The tabs, with the open one lit and underlined.</summary>
    private void Tabs(HudScreen screen, ScreenLayout layout, float left, float top, float panel)
    {
        var names = screen.TabNames;
        var pen = left;

        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];
            var width = MathF.Round(TextWidth(name, 8f)) + 10f;
            var open = screen.Tab == i;

            // The open one stands out of the screen and the shut ones are pressed into it, which is
            // the oldest way of drawing a tab and still the one that needs no explaining.
            Bevel(pen, top, width, 16f, open, open ? PanelFill : new Vector4(0.22f, 0.22f, 0.22f, 0.95f));

            Text(name, pen + 5f, top + 4f, 8f,
                open ? Highlight : InkFaint);

            layout.Add(ZoneKind.Tab, i, pen, top, width, 16f);
            pen += width + 2f;
        }

        // The rule the open tab is standing on. Drawn after, so its two pixels meet the panel below
        // rather than the tab above.
        Rect(_plain, left, top + 16f, panel, 2f, PanelLight);
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

        Frame(left - 4f, top - 4f, panel + 8f, shown * Line + 12f);

        if (total > lines) Scrollbar(screen, layout, left + panel + 6f, top - 2f, shown * Line + 8f, first, lines, total);

        for (var i = first; i < first + shown; i++)
        {
            var row = screen.Rows[i];
            var y = top + (i - first) * Line + 2f;

            if (row.Heading)
            {
                Text(row.Label, left, y, 8f, Highlight);
                continue;
            }

            var lit = i == screen.Selected;
            var hot = screen.Hovered is { Kind: ZoneKind.Row } over && over.Index == i;

            if (lit)
                Bevel(left - 2f, y - 2f, panel + 4f, Line, raised: false, new Vector4(0.50f, 0.50f, 0.50f, 0.97f));
            else if (hot)
                Rect(_plain, left - 2f, y - 2f, panel + 4f, Line, new Vector4(1f, 1f, 1f, 0.10f));

            Text(row.Label, left + 6f, y, 8f, lit ? Vector4.One : InkDim);

            var boxWidth = 0f;
            if (row.Edits is { } field) boxWidth = Box(screen, field, left, y, panel);
            else if (row.Value.Length > 0)
            {
                var width = TextWidth(row.Value, 8f);
                Text(row.Value, left + panel - width - 4f, y, 8f,
                    lit ? Ink : InkDim);
            }

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

        Bevel(x, y - 2f, width, ScreenLayout.MenuLine, raised: false,
            new Vector4(0.13f, 0.13f, 0.14f, 0.98f));

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
            Text(field.Placeholder, x + 4f, y, Glyph, InkFaint);
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

        Bevel(left - 4f, y, panel + 8f, lines.Count * 9f + 7f, raised: false,
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

        Bevel(x, y, Width, height, raised: false, new Vector4(0.17f, 0.17f, 0.17f, 0.97f));

        var span = MathF.Max(1f, total - lines);
        var thumb = MathF.Max(10f, MathF.Round(height * lines / total));
        var travel = height - thumb - 4f;
        var at = MathF.Round(y + 2f + travel * (first / span));

        var held = screen.Hovered is { Kind: ZoneKind.Scrollbar };
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

        TextCentred(
            screen.Grid is { Width: > 2 } ? "at a bench" : "in your hands",
            layout.BookX + layout.Size(ScreenLayout.BookWidth * 0.5f), layout.Y(14f), 8f, InkDim);

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

        // The panel. Its border is two of the pack's own pixels, so it thickens with the panel
        // rather than staying two layout units while everything around it grows.
        PanelBevel(layout, 0f, 0f, ScreenLayout.PanelWidth, ScreenLayout.PanelHeight, raised: true, PanelFill);

        // The player screen's tabs sit on top of the panel. A station has none — a furnace is not a
        // place you look up what you have unlocked. There is one tab until the progress, handbook
        // and map ones land, so the strip draws nothing today.
        if (screen.TabNames.Length > 1)
            Tabs(screen, layout, layout.X(0f), layout.Y(0f) - 18f, layout.Size(ScreenLayout.PanelWidth));

        // The button that folds the book out, on whichever panel has one.
        foreach (var zone in layout.Zones)
        {
            if (zone.Kind != ZoneKind.Button || zone.Index != (int)ScreenButton.Book) continue;

            var hot = screen.Hovered is { Kind: ZoneKind.Button } over && over.Index == zone.Index;
            Bevel(zone.X, zone.Y, zone.W, zone.H, raised: !screen.BookOut,
                hot ? PanelLight : screen.BookOut ? SlotFill : PanelFill);

            // An open book on the face of it, which is the one picture that needs no label.
            var ink = screen.BookOut ? Highlight : Ink;
            var pad = MathF.Max(1f, MathF.Round(z * 1.5f));
            Rect(_plain, zone.X + pad, zone.Y + pad, zone.W - pad * 2f, zone.H - pad * 2f,
                new Vector4(0.10f, 0.10f, 0.10f, 0.9f));
            Rect(_plain, zone.X + pad + z, zone.Y + pad + z, (zone.W - pad * 2f) / 2f - z, zone.H - pad * 2f - z * 2f, ink);
            Rect(_plain, zone.X + zone.W / 2f + z * 0.5f, zone.Y + pad + z, (zone.W - pad * 2f) / 2f - z, zone.H - pad * 2f - z * 2f, ink);
        }

        // A rule where the player's own pockets begin, which is where the pack's sheet puts one too.
        Rect(_plain, layout.X(7f), layout.Y(78f), layout.Size(162f), z, PanelDark);
        Rect(_plain, layout.X(7f), layout.Y(79f), layout.Size(162f), z, PanelLight);

        switch (kind)
        {
            case PanelKind.Player:
                Figure(layout, screen);
                Arrow(layout, ScreenLayout.PlayerArrow, 1f);
                break;

            case PanelKind.Bench:
                Arrow(layout, ScreenLayout.BenchArrow, 1f);
                break;

            case PanelKind.Furnace:
                Hearth(layout, screen);
                break;
        }

        // At a bench and at a furnace the square that gives wears a wider frame, which is how the
        // pack's own sheets draw it — twenty six across rather than eighteen. The two-by-two in a
        // player's hands does not: its result is a plain square, and that difference was measured
        // rather than assumed.
        var giving = kind == PanelKind.Bench
            ? layout.Find(SlotRole.Result, 0)
            : kind == PanelKind.Furnace ? layout.Find(SlotRole.Smelted, 0) : null;

        if (giving is { } wide)
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

            // Whatever already has the wider frame keeps it; every other square is a well pressed in.
            if (giving != zone) Well(layout, zone);

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

            if (stack.IsEmpty) continue;

            var type = catalogue[stack.Item];
            var inset = MathF.Round(z);
            Bloom(type, zone.X, zone.Y, zone.W, zone.H);
            SlotIcon(catalogue, stack, zone.X + inset, zone.Y + inset, zone.W - inset * 2f, Vector4.One);

            if (type.Durability > 0 && stack.Damage > 0)
            {
                var life = 1f - stack.Damage / (float)type.Durability;
                var bar = MathF.Max(1f, MathF.Round(z));
                Rect(_plain, zone.X + inset, zone.Y + zone.H - bar * 2f, zone.W - inset * 2f, bar,
                    new Vector4(0f, 0f, 0f, 0.8f));
                Rect(_plain, zone.X + inset, zone.Y + zone.H - bar * 2f, (zone.W - inset * 2f) * life, bar,
                    new Vector4(1f - life, 0.25f + life * 0.65f, 0.2f, 1f));
            }

            if (stack.Count > 1) Number(stack.Count, zone.X + zone.W, zone.Y + zone.H - digits - 1f, digits);
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

    /// <summary>
    /// The window the player's own figure stands in, and the figure.
    /// </summary>
    /// <remarks>
    /// <para>A flat front view cut out of the skin sheet rather than the model rendered into the
    /// panel: the overlay is a quad batcher in screen space, and standing up a second camera, a
    /// depth buffer and a lighting rig to show a player who is standing still and facing forward
    /// would be a renderer to keep working forever for a picture that has no depth in it anyway.
    /// </para>
    /// <para><b>Built from <see cref="PlayerModel.Build"/> and <see cref="PlayerModel.FaceRect"/>,
    /// not from a copy of their numbers.</b> Each box already knows where it sits in model space and
    /// where its front face lands on the sheet, so the figure is those two things read out — a slim
    /// arm is narrower here because it is narrower there, a legacy sheet points both arms at one
    /// patch here because it does there, and an overlay stands proud by the same quarter unit. The
    /// alternative is a second table of skin coordinates, and the day the two disagree the figure is
    /// wearing somebody's shoe on its head.</para>
    /// <para>The model's own right appears on the viewer's left, which is why screen x runs against
    /// model x — and it is the same reason a skin's face looks back at you.</para>
    /// </remarks>
    private void Figure(ScreenLayout layout, HudScreen screen)
    {
        _ = screen;
        var box = ScreenLayout.Figure;

        PanelBevel(
            layout, box.X, box.Y, box.W, box.H,
            raised: false, new Vector4(0.13f, 0.14f, 0.16f, 0.98f));

        if (_skin is null || _dollBoxes.Length == 0) return;

        // The model is thirty two units tall and sixteen across at the shoulders. Two panel pixels
        // per unit fits both inside the window with a margin, and two is a whole number, which is
        // the only kind this interface uses.
        const float Units = PlayerModel.UnitsTall;
        const float Across = 16f;
        const float PerUnit = 2f;

        var left = box.X + MathF.Round((box.W - Across * PerUnit) * 0.5f);
        var top = box.Y + MathF.Round((box.H - Units * PerUnit) * 0.5f);

        foreach (var part in _dollBoxes)
        {
            var grow = part.Inflate;
            var minX = part.Pivot.X + part.Offset.X - grow;
            var maxX = minX + part.Width + grow * 2f;
            var minY = part.Pivot.Y + part.Offset.Y - grow;
            var maxY = minY + part.Height + grow * 2f;

            var (fx, fy, fw, fh) = PlayerModel.FaceRect(part, 0);

            // Normalised against sixty four whatever the sheet is stored at — a 128 or 512 pixel
            // skin is the same layout at a different resolution, which is the whole reason the
            // loader squares every sheet up on the way in.
            var u0 = fx / (float)PlayerModel.SheetSize;
            var u1 = (fx + fw) / (float)PlayerModel.SheetSize;
            var v0 = fy / (float)PlayerModel.SheetSize;
            var v1 = (fy + fh) / (float)PlayerModel.SheetSize;

            // A mirrored net is applied left-for-right, so its u runs the other way across the face.
            if (part.Mirror) (u0, u1) = (u1, u0);

            RectUv(
                _skinQuads,
                layout.X(left + (Across * 0.5f - maxX) * PerUnit),
                layout.Y(top + (Units - maxY) * PerUnit),
                layout.Size((maxX - minX) * PerUnit),
                layout.Size((maxY - minY) * PerUnit),
                u0, v0, u1, v1,
                Vector4.One);
        }
    }

    /// <summary>
    /// Hands the overlay the skin the player is wearing, so the figure can be cut out of it.
    /// </summary>
    /// <remarks>
    /// After construction rather than in it, because the window and its GL context exist before
    /// anybody has decided which skin is being worn — and because a skin can change without the
    /// overlay needing to be built again.
    /// </remarks>
    public void SetSkin(PlayerSkinData skin)
    {
        _skin?.Dispose();
        _skin = new BlockTextureArray(_gl, [skin.Pixels], skin.Size);
        _dollBoxes = PlayerModel.Build(skin.Arms, skin.Legacy);
    }

    /// <summary>A furnace's flame burning down, and the work filling toward the output.</summary>
    private void Hearth(ScreenLayout layout, HudScreen screen)
    {
        var z = layout.Zoom;
        var fuel = screen.Burning?.FuelLeft ?? 0f;
        var work = screen.Burning?.Fraction ?? 0f;

        var flame = ScreenLayout.FurnaceFlame;
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
        Arrow(layout, ScreenLayout.FurnaceArrow, 0.35f);
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
    private void Pointer(ItemRegistry catalogue, HudScreen screen, ScreenLayout layout)
    {
        var size = MathF.Max(12f, MathF.Round(layout.Zoom * 8f));
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

        Rect(_iconQuads, at.X, at.Y, size, size, Vector4.One, IconCursor);
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

    private void Frame(float x, float y, float w, float h) => Bevel(x, y, w, h, raised: true, PanelFill);

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
            var glyph = TileGen.GlyphOf(c);
            if (glyph < 0) glyph = TileGen.GlyphOf('?');

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
            var glyph = TileGen.GlyphOf(c);
            width += Advance(glyph < 0 ? TileGen.GlyphOf('?') : glyph, height);
        }

        return width;
    }

    /// <summary>The sizes this interface writes at: a row's label and value, and a note under them.</summary>
    public const float RowGlyph = 8f;

    public const float NoteGlyph = 7f;

    /// <summary>How wide the settings panel is, so anything checking what fits can ask.</summary>
    public const float MenuPanel = 232f;

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
        MathF.Max(1f, MathF.Round(_advance[glyph] * (height / TileGen.Size)));

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

    /// <summary>Ten hearts, each worth two of the model's units.</summary>
    private void Hearts(PlayerVitals vitals, float w, float h)
    {
        const float Icon = 9f;
        const int Count = PlayerVitals.MaxHealth / 2;

        var left = MathF.Round(w / 2f) - Count * Icon;
        var top = h - 44f;

        var empty = new Vector4(0.10f, 0.05f, 0.06f, 0.85f);
        var full = new Vector4(0.86f, 0.16f, 0.20f, 1f);

        for (var i = 0; i < Count; i++)
        {
            var x = left + i * Icon;
            Rect(_iconQuads, x, top, Icon - 1f, Icon - 1f, empty, IconHeart);

            var filled = Math.Clamp(vitals.Health - i * 2, 0, 2);
            if (filled == 0) continue;

            // A half heart is the left half of the same shape, uv and all, which is what keeps the
            // two states looking like one heart in two conditions.
            var portion = filled == 2 ? 1f : 0.5f;
            Rect(_iconQuads, x, top, (Icon - 1f) * portion, Icon - 1f, full, IconHeart, uWidth: portion);
        }
    }

    /// <summary>Breath, shown only while it is worth knowing about.</summary>
    private void Bubbles(PlayerVitals vitals, float w, float h)
    {
        if (!vitals.Submerged && vitals.Breath >= PlayerVitals.MaxBreath) return;

        const float Icon = 9f;
        const int Count = 10;

        var left = MathF.Round(w / 2f);
        var top = h - 44f;
        var colour = new Vector4(0.72f, 0.88f, 1f, 0.95f);

        var remaining = vitals.Breath * Count / (float)PlayerVitals.MaxBreath;
        for (var i = 0; i < Count; i++)
        {
            if (remaining <= i) continue;
            Rect(_iconQuads, left + i * Icon, top, Icon - 1f, Icon - 1f, colour, IconBubble);
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
    private void SlotIcon(
        ItemRegistry catalogue, ItemStack stack, float x, float y, float size, Vector4 tint,
        float spin = 0f)
    {
        if (stack.IsEmpty) return;

        var type = catalogue[stack.Item];

        if (!type.DrawsAsBlock || type.IconModel is not { Icon.Length: > 0 } model)
        {
            if (spin != 0f) TurningCard(type.IconLayer, x, y, size, tint, spin);
            else Rect(_blocks, x, y, size, size, tint, type.IconLayer);
            return;
        }

        if (spin != 0f) TurningIcon(model, x, y, size, tint, spin);
        else foreach (var box in model.Icon) IconBox(box, x, y, size, tint);
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
                : (MathF.Abs(normal.Y) + MathF.Abs(normal.Z) * 0.80f + MathF.Abs(normal.X) * 0.62f) / weight;

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

        Quad(_blocks, box.Left, tint * new Vector4(0.80f, 0.80f, 0.80f, 1f),
            lTop.X, lTop.Y, lo.X, 1f - hi.Y,
            lTopIn.X, lTopIn.Y, hi.X, 1f - hi.Y,
            lBottomIn.X, lBottomIn.Y, hi.X, 1f - lo.Y,
            lBottom.X, lBottom.Y, lo.X, 1f - lo.Y);

        // The +x face, to the lower right. Texture runs across z the other way.
        var rTopIn = At(hi.X, hi.Y, hi.Z);
        var rTop = At(hi.X, hi.Y, lo.Z);
        var rBottom = At(hi.X, lo.Y, lo.Z);
        var rBottomIn = At(hi.X, lo.Y, hi.Z);

        Quad(_blocks, box.Right, tint * new Vector4(0.62f, 0.62f, 0.62f, 1f),
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

    private void Title(float centreX, float top, float cell, float drift)
    {
        var depth = MathF.Max(1f, MathF.Round(cell * 0.9f));
        var width = TitleArt.Cells * cell;
        var left = centreX - width * 0.5f;
        var middle = TitleArt.Cells * 0.5f;

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
                if (!TitleArt.Filled(letter, x, y)) continue;

                var cx = column + x;
                var px = left + cx * cell + lean;
                var py = top + y * cell + bob;

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
        float u0, float v0, float u1, float v1, Vector4 colour)
    {
        Vertex(into, x, y, u0, v0, 0f, colour);
        Vertex(into, x + w, y, u1, v0, 0f, colour);
        Vertex(into, x + w, y + h, u1, v1, 0f, colour);
        Vertex(into, x, y + h, u0, v1, 0f, colour);
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
        _shader.Dispose();
    }
}
