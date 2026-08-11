using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Items;
using Driftwood.Core.Projectiles;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>
/// Draws a thing that is not part of the world: lying on the ground, or held in a fist.
/// </summary>
/// <remarks>
/// <para>A block draws as a cube, because a block reads as a block from every angle and it means the
/// thing on the floor is visibly the thing that was broken. Everything else — a stick, an ingot, a
/// pickaxe — draws as its own picture <em>extruded</em>: front, back, and a wall of its own colour
/// round the silhouette. See <see cref="ItemSprite"/> for why that is not a cube with a picture on
/// it, which is what this used to be and what the user saw as two pickaxes.</para>
/// <para>One draw call each. A stack of dropped items is tens of entities, not thousands, and an
/// instanced path would cost more code than the draw calls cost frames — the moment that stops
/// being true, the vertex buffer here is already shaped to take a per-instance stream.</para>
/// <para>The turn and the bob are computed from age rather than accumulated, so an item dropped
/// while the game was paused is exactly where the clock says it should be and two items dropped
/// together do not drift apart.</para>
/// </remarks>
public sealed class ItemRenderer : IDisposable
{
    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aUv;
        layout(location = 2) in float aFace;

        uniform mat4 uViewProj;
        uniform mat4 uModel;
        uniform vec3 uLayers;       // top, side, bottom

        out vec3 vUvw;
        out float vShade;

        void main()
        {
            vec4 world = uModel * vec4(aPos, 1.0);
            gl_Position = uViewProj * world;

            int face = int(aFace + 0.5);
            float layer = face == 2 ? uLayers.x : face == 3 ? uLayers.z : uLayers.y;
            vUvw = vec3(aUv, layer);

            // Fixed per direction. A dropped item is lit by the cell it is in rather than by its
            // own faces, so this exists only to keep the cube from reading as a flat hexagon — and,
            // on a sprite, to make the extruded wall darker than the picture it holds up.
            vShade = face == 2 ? 1.0 : face == 3 ? 0.62 : (face == 0 || face == 1 ? 0.78 : 0.88);
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        in vec3 vUvw;
        in float vShade;

        uniform sampler2DArray uBlocks;
        uniform vec3 uLight;

        out vec4 FragColor;

        void main()
        {
            vec4 texel = texture(uBlocks, vUvw);
            if (texel.a < 0.5) discard;
            FragColor = vec4(texel.rgb * vShade * uLight, 1.0);
        }
        """;

    /// <summary>How big a dropped block is, as a share of a full one.</summary>
    private const float Size = 0.26f;

    private const int FloatsPerVertex = 6;

    /// <summary>Where one icon layer's extruded sprite lives in the shared buffer, and its grip.</summary>
    private readonly record struct SpriteRange(int First, int Count, Vector3 Hold);

    /// <summary>Where a block is held: by its underside, so it rests on the fist.</summary>
    private static readonly Vector3 BlockHold = new(0f, -0.5f, 0f);

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;

    private uint _spriteVao;
    private uint _spriteVbo;
    private uint _spriteEbo;
    private readonly Dictionary<ushort, SpriteRange> _sprites = [];

    /// <summary>Items the last <see cref="Draw"/> put on screen.</summary>
    public int DrawnItems { get; private set; }

    /// <summary>Flights the last projectile pass put on screen.</summary>
    public int DrawnProjectiles { get; private set; }

    /// <summary>How many icon layers were extruded, for the startup line.</summary>
    public int SpriteCount => _sprites.Count;

    /// <summary>Quads across every extruded sprite, so the cost of the pass is a number.</summary>
    public int SpriteQuads { get; private set; }

    public unsafe ItemRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);

        // A unit cube from the same corner table the mesher uses, so its winding is the winding
        // that has already been checked rather than a second transcription of it.
        var vertices = new List<float>(24 * FloatsPerVertex);
        var indices = new List<uint>(36);

        for (var face = 0; face < Faces.Count; face++)
        {
            var start = (uint)(face * 4);
            for (var c = 0; c < 4; c++)
            {
                var corner = Faces.Corners[face][c];
                vertices.Add(corner.X - 0.5f);
                vertices.Add(corner.Y - 0.5f);
                vertices.Add(corner.Z - 0.5f);
                vertices.Add(c is 1 or 2 ? 1f : 0f);
                vertices.Add(c is 2 or 3 ? 1f : 0f);
                vertices.Add(face);
            }

            indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }

        (_vao, _vbo, _ebo) = Upload([.. vertices], [.. indices]);
    }

    /// <summary>Makes one vertex array out of a block of floats and its index list.</summary>
    private unsafe (uint Vao, uint Vbo, uint Ebo) Upload(float[] data, uint[] order)
    {
        var vao = _gl.GenVertexArray();
        _gl.BindVertexArray(vao);

        var vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* p = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        var ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        fixed (uint* p = order)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(order.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);

        const uint stride = FloatsPerVertex * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(5 * sizeof(float)));

        _gl.BindVertexArray(0);
        return (vao, vbo, ebo);
    }

    /// <summary>
    /// Extrudes every flat item's icon, once, into one buffer with a range per layer.
    /// </summary>
    /// <remarks>
    /// <para>After construction rather than in it, because the textures a pack may have replaced are
    /// not read until later and the silhouette has to come off the picture that will actually be
    /// drawn — import a pack whose axe is a different shape and the wall round it moves with it.</para>
    /// <para>Keyed on the layer rather than on the item: twenty stair orientations are one item and
    /// several items can share a picture, and the geometry only ever depends on the picture.</para>
    /// </remarks>
    public void BuildSprites(ItemRegistry catalogue, byte[][] tiles, int size)
    {
        var vertices = new List<SpriteVertex>();
        var indices = new List<uint>();
        var floats = new List<float>();

        foreach (var type in catalogue.All)
        {
            if (type.DrawsAsBlock) continue;
            if (_sprites.ContainsKey(type.IconLayer)) continue;
            if (type.IconLayer >= tiles.Length) continue;

            vertices.Clear();
            indices.Clear();
            var mask = ItemSprite.Mask(tiles[type.IconLayer], size);
            ItemSprite.Build(mask, vertices, indices);
            if (indices.Count == 0) continue;

            var first = floats.Count / FloatsPerVertex;

            foreach (var v in vertices)
            {
                floats.Add(v.X);
                floats.Add(v.Y);
                floats.Add(v.Z);
                floats.Add(v.U);
                floats.Add(v.V);
                floats.Add(v.Face);
            }

            _sprites[type.IconLayer] = new SpriteRange(first, indices.Count, ItemSprite.Hold(mask));
            SpriteQuads += vertices.Count / 4;
        }

        if (_sprites.Count == 0) return;

        // ⚠ Indices are rebuilt over the whole buffer rather than appended, because each sprite's
        // own indices count from its own first vertex. Every quad is the same four corners in the
        // same order, so the list is a function of the vertex count and nothing has to be offset by
        // hand — which is exactly the arithmetic that goes wrong when it is done by hand.
        var quads = floats.Count / FloatsPerVertex / 4;
        var order = new uint[quads * 6];
        for (var q = 0; q < quads; q++)
        {
            var v = (uint)(q * 4);
            order[q * 6] = v;
            order[q * 6 + 1] = v + 1;
            order[q * 6 + 2] = v + 2;
            order[q * 6 + 3] = v;
            order[q * 6 + 4] = v + 2;
            order[q * 6 + 5] = v + 3;
        }

        (_spriteVao, _spriteVbo, _spriteEbo) = Upload([.. floats], order);
    }

    public unsafe void Draw(
        DroppedItems ground,
        BlockRegistry registry,
        ItemRegistry catalogue,
        Matrix4x4 viewProj,
        Func<Vector3, Vector3> lightAt)
    {
        DrawnItems = 0;
        if (ground.Count == 0) return;

        _shader.Use();
        _shader.SetMatrix4("uViewProj", viewProj);
        _shader.SetInt("uBlocks", 0);

        foreach (var item in ground.Live)
        {
            var type = catalogue[item.Stack.Item];

            // Shrinking as it is drawn in is the whole tell that a pickup is happening.
            var scale = Size * (1f - item.Collecting * 0.8f);
            var bob = MathF.Sin(item.Age * 2.6f) * 0.045f;

            // A flat thing is drawn bigger than a block: it is a picture of a pickaxe rather than a
            // solid, and at a block's size it reads as a splinter.
            if (!type.DrawsAsBlock) scale *= 1.55f;

            var model = Matrix4x4.CreateScale(scale)
                      * Matrix4x4.CreateRotationY(item.Age * 1.5f)
                      * Matrix4x4.CreateTranslation(item.Position + new Vector3(0f, scale + bob, 0f));

            _shader.SetMatrix4("uModel", model);
            SetLayers(type, registry);
            _shader.SetVec3("uLight", lightAt(item.Position));

            DrawMesh(type);
            DrawnItems++;
        }

        _gl.BindVertexArray(0);
    }

    /// <summary>Draws the same real item meshes in flight rather than inventing projectile art.</summary>
    public void DrawProjectiles(
        ProjectileSystem flights,
        BlockRegistry registry,
        ItemRegistry catalogue,
        Matrix4x4 viewProj,
        Func<Vector3, Vector3> lightAt)
    {
        DrawnProjectiles = 0;
        if (flights.Count == 0) return;

        var arrow = catalogue.ByName("arrow");
        var farpearl = catalogue.ByName("farpearl");

        _shader.Use();
        _shader.SetMatrix4("uViewProj", viewProj);
        _shader.SetInt("uBlocks", 0);

        for (var i = 0; i < ProjectileSystem.Capacity; i++)
        {
            if (!flights.ActiveAt(i)) continue;
            var shot = flights.SnapshotAt(i);
            var type = shot.Kind == ProjectileKind.Arrow ? arrow : farpearl;

            Matrix4x4 model;
            if (shot.Kind == ProjectileKind.Arrow)
            {
                // The generated arrow points along item-space +Y. Rotate that axis onto its flight
                // vector, preserving the icon's whole recognisable silhouette rather than drawing a
                // generic line whose texture pack can never replace.
                var direction = Vector3.Normalize(shot.Velocity);
                var rotation = FromTo(Vector3.UnitY, direction);
                model = Matrix4x4.CreateScale(0.72f)
                      * Matrix4x4.CreateFromQuaternion(rotation)
                      * Matrix4x4.CreateTranslation(shot.Position);
            }
            else
            {
                model = Matrix4x4.CreateScale(0.28f)
                      * Matrix4x4.CreateRotationX(shot.Age * 2.1f)
                      * Matrix4x4.CreateRotationY(shot.Age * 4.7f)
                      * Matrix4x4.CreateTranslation(shot.Position);
            }

            _shader.SetMatrix4("uModel", model);
            _shader.SetVec3("uLight", lightAt(shot.Position));
            SetLayers(type, registry);
            DrawMesh(type);
            DrawnProjectiles++;
        }

        _gl.BindVertexArray(0);
    }

    private static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        var dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (dot > 0.99999f) return Quaternion.Identity;
        if (dot < -0.99999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);

        var axis = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(axis, 1f + dot));
    }

    /// <summary>
    /// Draws what somebody is holding, at the transform the arm hands over.
    /// </summary>
    /// <remarks>
    /// <para>The same mesh as everything on the floor. That sharing is the point: a pickaxe animated
    /// from its own copy of the arm's numbers leaves the fist the first time either is dialled, and
    /// it only ever shows mid-swing.</para>
    /// <para>Called twice over, from two spaces. In first person <paramref name="viewProj"/> is the
    /// projection alone and the transform is in the camera's own space, over a cleared depth buffer.
    /// In third person it is the full view-projection and the transform is in the world, so the item
    /// is occluded by whatever is between it and the eye like anything else.</para>
    /// </remarks>
    public void DrawInHand(
        Matrix4x4 viewProj, Matrix4x4 model, ItemType type, BlockRegistry registry, Vector3 light)
    {
        _shader.Use();
        _shader.SetMatrix4("uViewProj", viewProj);
        _shader.SetMatrix4("uModel", model);
        _shader.SetInt("uBlocks", 0);
        _shader.SetVec3("uLight", light);
        SetLayers(type, registry);

        DrawMesh(type);
        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// The point of this item a fist closes on, in the item's own space.
    /// </summary>
    /// <remarks>
    /// Measured off the ink for a flat thing — see <see cref="ItemSprite.Hold"/> for why it cannot
    /// be one constant — and the underside for a block, so a block rests on the hand rather than
    /// being skewered by it.
    /// </remarks>
    public Vector3 HoldPoint(ItemType type) =>
        !type.DrawsAsBlock && _sprites.TryGetValue(type.IconLayer, out var range) ? range.Hold : BlockHold;

    /// <summary>The cube, or this item's own extruded silhouette.</summary>
    private unsafe void DrawMesh(ItemType type)
    {
        if (!type.DrawsAsBlock && _sprites.TryGetValue(type.IconLayer, out var range))
        {
            _gl.BindVertexArray(_spriteVao);
            _gl.DrawElements(
                PrimitiveType.Triangles, (uint)range.Count, DrawElementsType.UnsignedInt,
                (void*)(range.First / 4 * 6 * sizeof(uint)));
            return;
        }

        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, 36, DrawElementsType.UnsignedInt, (void*)0);
    }

    /// <summary>
    /// A block wears its own three faces; anything else wears its icon on every one, which is what
    /// lets a sprite's extruded wall read the same picture as its front.
    /// </summary>
    private void SetLayers(ItemType type, BlockRegistry registry)
    {
        if (!type.DrawsAsBlock)
        {
            _shader.SetVec3("uLayers", new Vector3(type.IconLayer));
            return;
        }

        var shape = registry[type.PlainBlock].Model;
        _shader.SetVec3("uLayers", new Vector3(
            shape.PassLayer(0, Faces.PosY) is var top and not BlockModel.NoLayer ? top : shape.ParticleLayer,
            shape.ParticleLayer,
            shape.PassLayer(0, Faces.NegY) is var bottom and not BlockModel.NoLayer ? bottom : shape.ParticleLayer));
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);

        if (_spriteVao != 0)
        {
            _gl.DeleteBuffer(_spriteVbo);
            _gl.DeleteBuffer(_spriteEbo);
            _gl.DeleteVertexArray(_spriteVao);
        }

        _shader.Dispose();
    }
}
