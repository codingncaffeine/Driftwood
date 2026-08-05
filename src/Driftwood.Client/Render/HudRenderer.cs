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
    Crafting,
    Furnace,
}

/// <summary>
/// Everything the overlay needs to know about the screen the player has open.
/// </summary>
/// <param name="Recipes">What this station can make, craftable or not.</param>
/// <param name="Payable">Whether each of those can be paid for right now, in the same order.</param>
/// <remarks>
/// Uncraftable recipes are listed rather than hidden, greyed out. A book that shows only what you
/// can already afford answers "what now" and never answers "what for" — and what a player wants
/// from a recipe screen in this genre is mostly the second question.
/// </remarks>
public readonly record struct HudScreen(
    HudScreenKind Kind,
    IReadOnlyList<Recipe> Recipes,
    IReadOnlyList<bool> Payable,
    int Selected,
    Furnace? Burning,
    int Slot)
{
    public static readonly HudScreen None = new(HudScreenKind.None, [], [], 0, null, 0);

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

    private readonly List<float> _plain = new(4096);
    private readonly List<float> _blocks = new(2048);
    private readonly List<float> _iconQuads = new(2048);

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
        in HudScreen screen,
        int screenWidth,
        int screenHeight)
    {
        _plain.Clear();
        _blocks.Clear();
        _iconQuads.Clear();

        var scale = MathF.Max(1f, MathF.Floor(screenHeight / DesignHeight * 2f) / 2f);
        var w = screenWidth / scale;
        var h = screenHeight / scale;

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

        var width = Inventory.Slots * Slot;
        var left = MathF.Round((w - width) / 2f);
        var top = h - Slot - 6f;

        Rect(_plain, left - 2f, top - 2f, width + 4f, Slot + 4f, new Vector4(0.05f, 0.05f, 0.07f, 0.55f));

        for (var i = 0; i < Inventory.Slots; i++)
        {
            var x = left + i * Slot;

            Rect(_plain, x, top, Slot, Slot, new Vector4(0.12f, 0.12f, 0.15f, 0.55f));

            // The selected slot gets a frame rather than a fill, so the icon in it is not tinted.
            if (i == inventory.Selected)
            {
                var frame = new Vector4(1f, 1f, 1f, 0.9f);
                Rect(_plain, x - 1f, top - 1f, Slot + 2f, 1.5f, frame);
                Rect(_plain, x - 1f, top + Slot - 0.5f, Slot + 2f, 1.5f, frame);
                Rect(_plain, x - 1f, top - 1f, 1.5f, Slot + 2f, frame);
                Rect(_plain, x + Slot - 0.5f, top - 1f, 1.5f, Slot + 2f, frame);
            }

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
    private void Screen(ItemRegistry catalogue, in HudScreen screen, float w, float h)
    {
        Rect(_plain, 0f, 0f, w, h, new Vector4(0.02f, 0.02f, 0.04f, 0.62f));

        const float Panel = 232f;
        var left = MathF.Round((w - Panel) / 2f);
        var top = MathF.Round(h * 0.16f);

        if (screen.Kind == HudScreenKind.Furnace)
        {
            Hearth(catalogue, screen, left, top, Panel);
            return;
        }

        Book(catalogue, screen, left, top, Panel);
    }

    /// <summary>The recipe list, and the selected recipe laid out as it would be in the grid.</summary>
    private void Book(ItemRegistry catalogue, in HudScreen screen, float left, float top, float panel)
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

            Rect(_plain, x, y, Cell - 1f, Cell - 1f,
                payable ? new Vector4(0.16f, 0.18f, 0.20f, 0.85f) : new Vector4(0.09f, 0.09f, 0.10f, 0.85f));

            var result = screen.Recipes[i].Result;
            var shade = payable ? Vector4.One : new Vector4(0.42f, 0.42f, 0.46f, 0.75f);
            Rect(_blocks, x + 3f, y + 3f, Cell - 7f, Cell - 7f, shade, catalogue[result.Item].IconLayer);

            if (i == screen.Selected) Select(x - 1f, y - 1f, Cell + 1f, Cell + 1f);
        }

        // The selected recipe, drawn as its own picture: the grid on the left, the result on the
        // right, at the size a player is about to reproduce.
        var chosen = screen.Selected >= 0 && screen.Selected < screen.Recipes.Count
            ? screen.Recipes[screen.Selected]
            : null;

        var detailTop = top + listHeight + 14f;
        Frame(left - 4f, detailTop - 4f, panel + 8f, 3f * Cell + 8f);
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

            Rect(_plain, px, py, Cell - 1f, Cell - 1f,
                slot is null ? new Vector4(0.07f, 0.07f, 0.08f, 0.8f) : new Vector4(0.16f, 0.18f, 0.20f, 0.9f));

            if (slot is null) continue;
            Rect(_blocks, px + 3f, py + 3f, Cell - 7f, Cell - 7f, Vector4.One,
                catalogue[slot.Members[0]].IconLayer);

            // A slot that will take more than one thing wears a corner mark, so a tag does not read
            // as "exactly this plank".
            if (slot.Members.Length > 1)
                Rect(_plain, px + Cell - 6f, py + 2f, 3f, 3f, new Vector4(0.95f, 0.82f, 0.35f, 0.95f));
        }

        // The result, out to the right of the grid with a bar pointing at it.
        var arrowY = detailTop + Cell * 1.5f - 1.5f;
        Rect(_plain, left + 3f * Cell + 6f, arrowY, 20f, 3f, new Vector4(0.8f, 0.8f, 0.85f, 0.9f));

        var resultX = left + 3f * Cell + 34f;
        var resultY = detailTop + Cell - 4f;
        Rect(_plain, resultX - 3f, resultY - 3f, 34f, 34f, new Vector4(0.16f, 0.18f, 0.20f, 0.9f));
        Rect(_blocks, resultX, resultY, 28f, 28f, Vector4.One, catalogue[chosen.Result.Item].IconLayer);
        if (chosen.Result.Count > 1) Number(chosen.Result.Count, resultX + 29f, resultY + 20f);
    }

    /// <summary>The nth filled slot of a shapeless recipe, or null past the end.</summary>
    private static Ingredient? Nth(Recipe recipe, int index)
    {
        foreach (var slot in recipe.Ingredients)
            if (index-- == 0) return slot;
        return null;
    }

    /// <summary>A furnace: what is in it, what is burning, and how far through it is.</summary>
    private void Hearth(ItemRegistry catalogue, in HudScreen screen, float left, float top, float panel)
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
        var flameH = 26f * furnace.FuelLeft;
        Rect(_plain, left + 52f, inputY + 34f, 8f, 26f, new Vector4(0.10f, 0.09f, 0.09f, 0.9f));
        Rect(_plain, left + 52f, inputY + 34f + (26f - flameH), 8f, flameH,
            new Vector4(1f, 0.55f + furnace.FuelLeft * 0.3f, 0.18f, 1f));

        // And the work, filling toward the output.
        Rect(_plain, left + 70f, top + 40f, 56f, 6f, new Vector4(0.10f, 0.10f, 0.12f, 0.9f));
        Rect(_plain, left + 70f, top + 40f, 56f * furnace.Fraction, 6f,
            new Vector4(0.75f, 0.80f, 0.86f, 1f));

        void Cell(float x, float y, ItemStack stack, int index)
        {
            Rect(_plain, x, y, Slot, Slot, new Vector4(0.16f, 0.18f, 0.20f, 0.9f));
            if (index == chosen) Select(x - 1f, y - 1f, Slot + 2f, Slot + 2f);

            if (stack.IsEmpty) return;
            Rect(_blocks, x + 4f, y + 4f, Slot - 8f, Slot - 8f, Vector4.One,
                catalogue[stack.Item].IconLayer);
            if (stack.Count > 1) Number(stack.Count, x + Slot - 2f, y + Slot - 9f);
        }
    }

    private void Frame(float x, float y, float w, float h)
    {
        Rect(_plain, x, y, w, h, new Vector4(0.06f, 0.07f, 0.09f, 0.94f));
        var edge = new Vector4(0.32f, 0.34f, 0.38f, 0.95f);
        Rect(_plain, x, y, w, 1f, edge);
        Rect(_plain, x, y + h - 1f, w, 1f, edge);
        Rect(_plain, x, y, 1f, h, edge);
        Rect(_plain, x + w - 1f, y, 1f, h, edge);
    }

    /// <summary>Four thin bars round a slot. The same frame the held hotbar slot wears.</summary>
    private void Select(float x, float y, float w, float h)
    {
        var frame = new Vector4(1f, 1f, 1f, 0.92f);
        Rect(_plain, x, y, w, 1.5f, frame);
        Rect(_plain, x, y + h - 1.5f, w, 1.5f, frame);
        Rect(_plain, x, y, 1.5f, h, frame);
        Rect(_plain, x + w - 1.5f, y, 1.5f, h, frame);
    }

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
        _shader.Dispose();
    }
}
