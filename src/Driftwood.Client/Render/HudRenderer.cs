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

    /// <summary>A station rather than a menu, so it has no tabs.</summary>
    Furnace,

    /// <summary>The other station: three by three, and the pockets under it.</summary>
    Bench,
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
/// <remarks>Progress, handbook and map join this; the renderer is told names, not values.</remarks>
public enum PlayerTab
{
    /// <summary>What is carried, what is worn, and the two-by-two a player always has.</summary>
    Items,

    Craft,
}

/// <summary>The tabs of the game screen.</summary>
public enum GameTab
{
    Controls,
    Video,
    Audio,
    World,
}

/// <summary>
/// One line of a settings tab: what it is, and what it is currently set to.
/// </summary>
/// <param name="Heading">True for a group title, which has no value and cannot be selected.</param>
/// <param name="Note">A second, dimmer line under it — what a setting costs, or when it applies.</param>
public readonly record struct MenuRow(string Label, string Value = "", bool Heading = false, string Note = "");

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

    /// <summary>The hint along the bottom — what the keys do here, right now.</summary>
    public string Footer = "";

    public Furnace? Burning;

    /// <summary>The squares in front of the player, when the open screen has any.</summary>
    public CraftingGrid? Grid;

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
        Kind is HudScreenKind.Player or HudScreenKind.Bench or HudScreenKind.Furnace;
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

    private float[] _upload = new float[8192];

    /// <summary>Icon array layers. Digits run from <see cref="IconDigit"/> upward.</summary>
    private const int IconHeart = 0;
    private const int IconBubble = 1;
    private const int IconDigit = 2;
    private const int IconCursor = IconDigit + 10;

    /// <summary>The five worn-slot silhouettes, in <see cref="EquipSlot"/> order.</summary>
    private const int IconEquip = IconCursor + 1;

    public unsafe HudRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);

        var icons = new List<byte[]> { TileGen.Heart(), TileGen.Bubble() };
        icons.AddRange(TileGen.Digits());
        icons.Add(TileGen.Cursor());
        icons.AddRange(TileGen.EquipGhosts());
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
        if (!screen.IsContainer) Hotbar(catalogue, inventory, w, h);

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
            Rect(_blocks, x + Pad, top + Pad, Slot - Pad * 2f, Slot - Pad * 2f, Vector4.One, type.IconLayer);

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

        // The three container screens are drawn on the pack's own panel and share every square
        // below the halfway line. Everything else is the tabbed settings layout.
        if (screen.IsContainer && screen.Tab == (int)PlayerTab.Items)
        {
            Container(catalogue, inventory, equipment, screen, layout, w, h);
            Footer(screen, w, h);
            return;
        }

        const float Panel = 232f;
        var left = MathF.Round((w - Panel) / 2f);
        var top = MathF.Round(h * 0.12f);

        Tabs(screen, layout, left, top, Panel);
        var body = top + 22f;

        // A tab either lists recipes or lists rows. Which one is decided by whoever filled the
        // screen in, not here — Recipes being non-empty is the signal, so a tab that wants to draw
        // something else entirely adds a list rather than a case in this method.
        if (screen.Recipes.Count > 0) Book(catalogue, screen, layout, left, body, Panel);
        else Rows(screen, layout, left, body, Panel);

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

    /// <summary>A settings tab: a label on the left and what it is set to on the right.</summary>
    private void Rows(HudScreen screen, ScreenLayout layout, float left, float top, float panel)
    {
        const float Line = 13f;

        Frame(left - 4f, top - 4f, panel + 8f, screen.Rows.Count * Line + 12f);

        for (var i = 0; i < screen.Rows.Count; i++)
        {
            var row = screen.Rows[i];
            var y = top + i * Line + 2f;

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

            if (row.Value.Length > 0)
            {
                var width = TextWidth(row.Value, 8f);
                Text(row.Value, left + panel - width - 4f, y, 8f,
                    lit ? Ink : InkDim);
            }

            if (row.Note.Length > 0 && lit)
                Text(row.Note, left + 6f, y + 9f, 7f, InkFaint);

            // A heading has no zone at all, so the pointer cannot land on something the keyboard
            // deliberately skips over. The row's whole width is clickable, not just its words.
            layout.Add(ZoneKind.Row, i, left - 2f, y - 2f, panel + 4f, Line);
        }
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

    /// <summary>The recipe list, and the selected recipe laid out as it would be in the grid.</summary>
    private void Book(
        ItemRegistry catalogue, HudScreen screen, ScreenLayout layout, float left, float top, float panel)
    {
        const float Cell = 22f;
        const int Columns = 10;

        var rows = Math.Max(1, (screen.Recipes.Count + Columns - 1) / Columns);
        var listHeight = rows * Cell;

        Frame(left - 4f, top - 4f, panel + 8f, listHeight + 8f);

        for (var i = 0; i < screen.Recipes.Count; i++)
        {
            var x = left + i % Columns * Cell;
            var y = top + i / Columns * Cell;
            var payable = i < screen.Payable.Count && screen.Payable[i];

            Bevel(x, y, Cell - 2f, Cell - 2f, raised: false,
                payable ? SlotFill : new Vector4(0.19f, 0.19f, 0.19f, 0.95f));

            var result = screen.Recipes[i].Result;
            var shade = payable ? Vector4.One : new Vector4(0.45f, 0.45f, 0.45f, 0.85f);
            Rect(_blocks, x + 3f, y + 3f, Cell - 8f, Cell - 8f, shade, catalogue[result.Item].IconLayer);

            if (screen.Hovered is { Kind: ZoneKind.Recipe } hot && hot.Index == i)
                Rect(_plain, x, y, Cell - 2f, Cell - 2f, new Vector4(1f, 1f, 1f, 0.18f));

            if (i == screen.Selected) Select(x, y, Cell - 2f, Cell - 2f);

            layout.Add(ZoneKind.Recipe, i, x, y, Cell - 2f, Cell - 2f);
        }

        // The selected recipe, drawn as its own picture: the grid on the left, the result on the
        // right, at the size a player is about to reproduce.
        var chosen = screen.Selected >= 0 && screen.Selected < screen.Recipes.Count
            ? screen.Recipes[screen.Selected]
            : null;

        var detailTop = top + listHeight + 14f;
        Frame(left - 4f, detailTop - 4f, panel + 8f, 3f * Cell + 30f);
        if (chosen is null) return;

        for (var y = 0; y < 3; y++)
        for (var x = 0; x < 3; x++)
        {
            var px = left + x * Cell;
            var py = detailTop + y * Cell;

            // Shapeless recipes have no arrangement, so they are laid out in reading order rather
            // than pretended into a shape the grid would not accept.
            var slot = chosen.Shapeless
                ? Nth(chosen, y * 3 + x)
                : x < chosen.Width && y < chosen.Height ? chosen.At(x, y) : null;

            Bevel(px, py, Cell - 2f, Cell - 2f, raised: false,
                slot is null ? new Vector4(0.19f, 0.19f, 0.19f, 0.95f) : SlotFill);

            if (slot is null) continue;
            Rect(_blocks, px + 3f, py + 3f, Cell - 8f, Cell - 8f, Vector4.One,
                catalogue[slot.Members[0]].IconLayer);

            // A slot that will take more than one thing wears a corner mark, so a tag does not read
            // as "exactly this plank".
            if (slot.Members.Length > 1)
                Rect(_plain, px + Cell - 6f, py + 2f, 3f, 3f, new Vector4(0.95f, 0.82f, 0.35f, 0.95f));
        }

        // The result, out to the right of the grid with a bar pointing at it.
        var arrowY = detailTop + Cell * 1.5f - 1.5f;
        Rect(_plain, left + 3f * Cell + 6f, arrowY, 20f, 3f, PanelLight);

        var resultX = left + 3f * Cell + 34f;
        var resultY = detailTop + Cell - 4f;
        Bevel(resultX - 4f, resultY - 4f, 36f, 36f, raised: false, SlotFill);
        Rect(_blocks, resultX, resultY, 28f, 28f, Vector4.One, catalogue[chosen.Result.Item].IconLayer);
        if (chosen.Result.Count > 1) Number(chosen.Result.Count, resultX + 29f, resultY + 20f);

        // What it is, in words. The pictures say what it costs; only a name says what it is for,
        // and a bench full of grey squares is a puzzle rather than a menu.
        var nameY = detailTop + 3f * Cell + 8f;
        var payableNow = screen.Selected < screen.Payable.Count && screen.Payable[screen.Selected];

        Text(chosen.Name, left, nameY, 9f,
            payableNow ? Vector4.One : InkDim);

        Text(chosen.NeedsBench ? "at a bench" : "in hand", left, nameY + 12f, 7f,
            InkFaint);
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
            _ => PanelKind.Player,
        };

        layout.BuildPanel(kind, screen.Grid?.Width ?? 0, w, h);
        var z = layout.Zoom;

        // The panel. Its border is two of the pack's own pixels, so it thickens with the panel
        // rather than staying two layout units while everything around it grows.
        PanelBevel(layout, 0f, 0f, ScreenLayout.PanelWidth, ScreenLayout.PanelHeight, raised: true, PanelFill);

        // The player screen's tabs sit on top of the panel. A station has none — a furnace is not a
        // place you look up what you have unlocked.
        if (screen.TabNames.Length > 1)
            Tabs(screen, layout, layout.X(0f), layout.Y(0f) - 18f, layout.Size(ScreenLayout.PanelWidth));

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
            Rect(_blocks, zone.X + inset, zone.Y + inset, zone.W - inset * 2f, zone.H - inset * 2f,
                Vector4.One, type.IconLayer);

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

        // What the arrangement makes, named, under the panel. The picture says what it costs and
        // only a name says what it is for.
        if (screen.Grid?.Match is { } made)
            TextCentred(made.Name, w / 2f, layout.Y(ScreenLayout.PanelHeight) + 6f, 9f, Ink);
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
        _ => ItemStack.Empty,
    };

    /// <summary>A square pressed into the panel: eighteen across with sixteen inside it.</summary>
    private void Well(ScreenLayout layout, Zone zone)
    {
        var z = layout.Zoom;
        Rect(_plain, zone.X - z, zone.Y - z, zone.W + z * 2f, zone.H + z * 2f, SlotFill);
        Rect(_plain, zone.X - z, zone.Y - z, zone.W + z * 2f, z, PanelDark);
        Rect(_plain, zone.X - z, zone.Y - z, z, zone.H + z * 2f, PanelDark);
        Rect(_plain, zone.X - z, zone.Y + zone.H, zone.W + z * 2f, z, PanelLight);
        Rect(_plain, zone.X + zone.W, zone.Y - z, z, zone.H + z * 2f, PanelLight);
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

    /// <summary>The window the player's own figure stands in.</summary>
    private void Figure(ScreenLayout layout, HudScreen screen)
    {
        _ = screen;
        PanelBevel(
            layout, ScreenLayout.Figure.X, ScreenLayout.Figure.Y,
            ScreenLayout.Figure.W, ScreenLayout.Figure.H,
            raised: false, new Vector4(0.13f, 0.14f, 0.16f, 0.98f));
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

            Rect(_blocks, hx, hy, held, held, Vector4.One, catalogue[screen.Carried.Item].IconLayer);
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
    private static readonly Vector4 SlotFill = new(0.24f, 0.24f, 0.24f, 0.97f);
    private static readonly Vector4 Highlight = new(0.96f, 0.96f, 0.96f, 1f);

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

    /// <summary>What is picked out: pressed into the panel, with a lit edge round it.</summary>
    private void Select(float x, float y, float w, float h)
    {
        x = MathF.Round(x);
        y = MathF.Round(y);
        w = MathF.Round(w);
        h = MathF.Round(h);

        Rect(_plain, x - 1f, y - 1f, w + 2f, 1f, Highlight);
        Rect(_plain, x - 1f, y + h, w + 2f, 1f, Highlight);
        Rect(_plain, x - 1f, y - 1f, 1f, h + 2f, Highlight);
        Rect(_plain, x + w, y - 1f, 1f, h + 2f, Highlight);
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
    private float Text(string line, float x, float y, float height, Vector4 colour, bool shadow = true)
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
    private float TextWidth(string line, float height)
    {
        var width = 0f;

        foreach (var c in line)
        {
            var glyph = TileGen.GlyphOf(c);
            width += Advance(glyph < 0 ? TileGen.GlyphOf('?') : glyph, height);
        }

        return width;
    }

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
        _shader.Dispose();
    }
}
