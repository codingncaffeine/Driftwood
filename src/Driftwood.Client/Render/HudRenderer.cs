using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;
using Driftwood.Core.Items;
using Driftwood.Core.Textures;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>Which screen, if any, is over the world.</summary>
public enum HudScreenKind
{
    None,

    /// <summary>This character in this world: what is carried, and what can be made.</summary>
    Player,

    /// <summary>This installation: keys, picture, sound, and the testing dials.</summary>
    Game,

    /// <summary>A station rather than a menu, so it has no tabs.</summary>
    Furnace,
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
/// <remarks>Items, progress, handbook and map join this; the renderer is told names, not values.</remarks>
public enum PlayerTab
{
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
    public int Slot;

    public bool IsOpen => Kind != HudScreenKind.None;
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

    private const int Floats = 9;

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

    public unsafe HudRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);

        var icons = new List<byte[]> { TileGen.Heart(), TileGen.Bubble() };
        icons.AddRange(TileGen.Digits());
        _icons = new BlockTextureArray(gl, [.. icons], TileGen.Size);

        _font = new BlockTextureArray(gl, TileGen.Font(), TileGen.Size);
        _advance = TileGen.FontAdvance();

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_upload.Length * sizeof(float)), null, BufferUsageARB.StreamDraw);

        // One quad is four corners and six indices, and the pattern never varies.
        const int MaxQuads = 2048;
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

    /// <summary>Lays the whole overlay out and draws it.</summary>
    public void Draw(
        BlockTextureArray blocks,
        ItemRegistry catalogue,
        Inventory inventory,
        PlayerVitals vitals,
        HudScreen screen,
        IReadOnlyList<Toast> toasts,
        int screenWidth,
        int screenHeight)
    {
        _plain.Clear();
        _blocks.Clear();
        _iconQuads.Clear();
        _text.Clear();

        // A whole number of screen pixels per layout unit, never a half. Everything here is pixel
        // art — a font drawn at twice its authored size, two-pixel bevels, hard edges — and all of
        // that depends on one layout unit landing on exactly one grid of pixels. At a half step the
        // bevels come out one and a half pixels wide, which the sampler resolves by blurring them,
        // and the whole interface goes soft in a way that reads as a low resolution rather than as
        // a deliberate one.
        var scale = MathF.Max(1f, MathF.Floor(screenHeight / DesignHeight));
        var w = MathF.Floor(screenWidth / scale);
        var h = MathF.Floor(screenHeight / scale);

        // A screen covers the world and the crosshair with it: a reticle over an inventory is
        // aiming at nothing, and it sits exactly where the eye is trying to read.
        if (screen.IsOpen) Screen(catalogue, screen, w, h);
        else Crosshair(w, h);

        Hotbar(catalogue, inventory, w, h);

        if (!screen.IsOpen)
        {
            Hearts(vitals, w, h);
            Bubbles(vitals, w, h);
        }

        Toasts(toasts, w);

        _shader.Use();
        _shader.SetVec2("uScreen", new Vector2(w, h));
        _shader.SetInt("uAtlas", 0);

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
    }

    private unsafe void Flush(List<float> batch, bool textured, BlockTextureArray? atlas)
    {
        if (batch.Count == 0) return;

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
    private void Screen(ItemRegistry catalogue, HudScreen screen, float w, float h)
    {
        Rect(_plain, 0f, 0f, w, h, new Vector4(0.04f, 0.04f, 0.04f, 0.72f));

        const float Panel = 232f;
        var left = MathF.Round((w - Panel) / 2f);
        var top = MathF.Round(h * 0.12f);

        if (screen.Kind == HudScreenKind.Furnace)
        {
            Hearth(catalogue, screen, left, top, Panel);
            Footer(screen, w, h);
            return;
        }

        Tabs(screen, left, top, Panel);
        var body = top + 22f;

        // A tab either lists recipes or lists rows. Which one is decided by whoever filled the
        // screen in, not here — Recipes being non-empty is the signal, so a tab that wants to draw
        // something else entirely adds a list rather than a case in this method.
        if (screen.Recipes.Count > 0) Book(catalogue, screen, left, body, Panel);
        else Rows(screen, left, body, Panel);

        Footer(screen, w, h);
    }

    /// <summary>The tabs, with the open one lit and underlined.</summary>
    private void Tabs(HudScreen screen, float left, float top, float panel)
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

            pen += width + 2f;
        }

        // The rule the open tab is standing on. Drawn after, so its two pixels meet the panel below
        // rather than the tab above.
        Rect(_plain, left, top + 16f, panel, 2f, PanelLight);
    }

    /// <summary>A settings tab: a label on the left and what it is set to on the right.</summary>
    private void Rows(HudScreen screen, float left, float top, float panel)
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

            if (i == screen.Selected)
                Bevel(left - 2f, y - 2f, panel + 4f, Line, raised: false, new Vector4(0.50f, 0.50f, 0.50f, 0.97f));

            var lit = i == screen.Selected;
            Text(row.Label, left + 6f, y, 8f, lit ? Vector4.One : InkDim);

            if (row.Value.Length > 0)
            {
                var width = TextWidth(row.Value, 8f);
                Text(row.Value, left + panel - width - 4f, y, 8f,
                    lit ? Ink : InkDim);
            }

            if (row.Note.Length > 0 && lit)
                Text(row.Note, left + 6f, y + 9f, 7f, InkFaint);
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
    private void Book(ItemRegistry catalogue, HudScreen screen, float left, float top, float panel)
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

            if (i == screen.Selected) Select(x, y, Cell - 2f, Cell - 2f);
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

    /// <summary>A furnace: what is in it, what is burning, and how far through it is.</summary>
    private void Hearth(ItemRegistry catalogue, HudScreen screen, float left, float top, float panel)
    {
        const float Slot = 30f;

        Frame(left - 4f, top - 4f, panel + 8f, 92f);
        if (screen.Burning is not { } furnace) return;

        var inputY = top + 6f;
        var fuelY = top + 52f;
        var outX = left + 132f;
        var chosen = screen.Slot;

        Cell(left + 8f, inputY, furnace.Input, 0);
        Cell(left + 8f, fuelY, furnace.Fuel, 1);
        Cell(outX, top + 29f, furnace.Output, 2);

        // The flame between the two, burning down. Drawn as a bar rather than a picture because
        // what it has to say is how much is left, and a flickering icon says nothing about that.
        // Quantised to whole pixels so it steps rather than slides, which is the whole aesthetic.
        var flameH = MathF.Round(26f * furnace.FuelLeft);
        Bevel(left + 52f, inputY + 34f, 10f, 30f, raised: false, new Vector4(0.17f, 0.17f, 0.17f, 0.95f));
        Rect(_plain, left + 54f, inputY + 36f + (26f - flameH), 6f, flameH,
            new Vector4(1f, 0.55f + furnace.FuelLeft * 0.3f, 0.18f, 1f));

        // And the work, filling toward the output.
        Bevel(left + 70f, top + 38f, 58f, 10f, raised: false, new Vector4(0.17f, 0.17f, 0.17f, 0.95f));
        Rect(_plain, left + 72f, top + 40f, MathF.Round(54f * furnace.Fraction), 6f,
            PanelLight);

        void Cell(float x, float y, ItemStack stack, int index)
        {
            Bevel(x, y, Slot, Slot, raised: false, SlotFill);
            if (index == chosen) Select(x, y, Slot, Slot);

            if (stack.IsEmpty) return;
            Rect(_blocks, x + 4f, y + 4f, Slot - 8f, Slot - 8f, Vector4.One,
                catalogue[stack.Item].IconLayer);
            if (stack.Count > 1) Number(stack.Count, x + Slot - 2f, y + Slot - 9f);
        }
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
    private void Number(int value, float right, float top)
    {
        const float Glyph = 6f;
        var shadow = new Vector4(0f, 0f, 0f, 0.75f);
        var bright = Vector4.One;

        var at = right;
        do
        {
            var digit = value % 10;
            value /= 10;
            at -= Glyph;

            Rect(_iconQuads, at + 0.75f, top + 0.75f, Glyph, Glyph, shadow, IconDigit + digit);
            Rect(_iconQuads, at, top, Glyph, Glyph, bright, IconDigit + digit);
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
