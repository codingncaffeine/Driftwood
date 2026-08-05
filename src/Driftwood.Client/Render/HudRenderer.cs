using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;
using Driftwood.Core.Items;
using Driftwood.Core.Textures;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

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
        BlockRegistry registry,
        Inventory inventory,
        PlayerVitals vitals,
        int screenWidth,
        int screenHeight)
    {
        _plain.Clear();
        _blocks.Clear();
        _iconQuads.Clear();

        var scale = MathF.Max(1f, MathF.Floor(screenHeight / DesignHeight * 2f) / 2f);
        var w = screenWidth / scale;
        var h = screenHeight / scale;

        Crosshair(w, h);
        Hotbar(registry, inventory, w, h);
        Hearts(vitals, w, h);
        Bubbles(vitals, w, h);

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
    private void Hotbar(BlockRegistry registry, Inventory inventory, float w, float h)
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

            var layer = registry[stack.Block].Model.ParticleLayer;
            Rect(_blocks, x + Pad, top + Pad, Slot - Pad * 2f, Slot - Pad * 2f, Vector4.One, layer);

            // Counts sit in the bottom right of the slot, right-aligned, one digit at a time. A
            // single item shows no number: nine slots each labelled "1" is noise, not information.
            if (stack.Count <= 1) continue;
            Number(stack.Count, x + Slot - 1.5f, top + Slot - 8.5f);
        }
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
