using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Items;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>
/// Draws what is lying on the ground: a small cube of the block, or a card of the icon, bobbing
/// and turning.
/// </summary>
/// <remarks>
/// <para>A block draws as a cube, because a block reads as a block from every angle and it means
/// the thing on the floor is visibly the thing that was broken. Everything else — a stick, an
/// ingot, a pickaxe — draws as the same cube squashed almost flat with its icon on every face,
/// which is a card with thickness. It has to have <em>some</em> thickness: a plane turning on its
/// axis vanishes once a revolution, and an item that blinks looks like a rendering fault.</para>
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
            // own faces, so this exists only to keep the cube from reading as a flat hexagon.
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

    /// <summary>How thick a flat item is, as a share of its width.</summary>
    private const float CardThickness = 0.14f;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;

    /// <summary>Items the last <see cref="Draw"/> put on screen.</summary>
    public int DrawnItems { get; private set; }

    public unsafe ItemRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);

        // A unit cube from the same corner table the mesher uses, so its winding is the winding
        // that has already been checked rather than a second transcription of it.
        var vertices = new List<float>(24 * 6);
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

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        var data = vertices.ToArray();
        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        var order = indices.ToArray();
        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* p = order)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(order.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);

        const uint stride = 6 * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(5 * sizeof(float)));

        _gl.BindVertexArray(0);
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

        _gl.BindVertexArray(_vao);

        foreach (var item in ground.Live)
        {
            var type = catalogue[item.Stack.Item];

            // Shrinking as it is drawn in is the whole tell that a pickup is happening.
            var scale = Size * (1f - item.Collecting * 0.8f);
            var bob = MathF.Sin(item.Age * 2.6f) * 0.045f;

            var thickness = type.DrawsAsCube ? 1f : CardThickness;

            var model = Matrix4x4.CreateScale(scale, scale, scale * thickness)
                      * Matrix4x4.CreateRotationY(item.Age * 1.5f)
                      * Matrix4x4.CreateTranslation(item.Position + new Vector3(0f, scale + bob, 0f));

            _shader.SetMatrix4("uModel", model);
            SetLayers(type, registry);

            _shader.SetVec3("uLight", lightAt(item.Position));
            _gl.DrawElements(PrimitiveType.Triangles, 36, DrawElementsType.UnsignedInt, (void*)0);
            DrawnItems++;
        }

        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Draws what the player is holding, in the camera's own space, in the fist of the view model.
    /// </summary>
    /// <remarks>
    /// <para>The same cube as everything on the floor, at the transform the arm hands over. That
    /// sharing is the point: a pickaxe animated from its own copy of the arm's numbers leaves the
    /// fist the first time either is dialled, and it only ever shows mid-swing.</para>
    /// <para>Depth is on but the view-model pass has already cleared the buffer, so this sits over
    /// the world with the arm and tests only against the arm.</para>
    /// </remarks>
    public unsafe void DrawInHand(
        Matrix4x4 projection, Matrix4x4 model, ItemType type, BlockRegistry registry, Vector3 light)
    {
        _shader.Use();
        _shader.SetMatrix4("uViewProj", projection);
        _shader.SetMatrix4("uModel", model);
        _shader.SetInt("uBlocks", 0);
        _shader.SetVec3("uLight", light);
        SetLayers(type, registry);

        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, 36, DrawElementsType.UnsignedInt, (void*)0);
        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// A block wears its own three faces; anything else wears its icon on all six, which is what
    /// makes a card read the same whichever way round the spin has it.
    /// </summary>
    private void SetLayers(ItemType type, BlockRegistry registry)
    {
        if (!type.DrawsAsCube)
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
        _shader.Dispose();
    }
}
