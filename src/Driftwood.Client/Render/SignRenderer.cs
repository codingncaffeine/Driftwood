using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.World;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>
/// Writes each sign's words on its board, in the interface's own font.
/// </summary>
/// <remarks>
/// <para>⛳ Signs, 2026-10-16. Every letter is a square of the font array laid flat on the board's
/// front face, the pen moving by the font's own advance — the HUD's way of writing, turned into the
/// world. So a pack that brings a font writes its signs in it too, with nothing to keep in step.</para>
/// <para>Drawn with the depth test on and depth writes off, after the terrain, so a wall in front
/// of a sign hides its words and the words never hide the board they are on. A polygon offset and
/// a hair's lift off the face keep the letters from flickering into the wood.</para>
/// </remarks>
public sealed class SignRenderer : IDisposable
{
    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aUvw;
        uniform mat4 uViewProj;
        out vec3 vUvw;
        void main()
        {
            vUvw = aUvw;
            gl_Position = uViewProj * vec4(aPos, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        in vec3 vUvw;
        uniform sampler2DArray uFont;
        uniform vec4 uInk;
        out vec4 FragColor;
        void main()
        {
            if (texture(uFont, vUvw).a < 0.5) discard;
            FragColor = uInk;
        }
        """;

    /// <summary>A glyph cell's height on the board, in blocks; four lines fill the half-block board.</summary>
    private const float GlyphSize = 0.1f;

    private const float LineHeight = 0.115f;

    /// <summary>Signs further than this are not written; their words are a smudge by then anyway.</summary>
    private const float Reach = 32f;

    private const int FloatsPerVertex = 6;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private float[] _vertices = new float[6 * FloatsPerVertex * 256];
    private int _at;

    public unsafe SignRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);
        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, FloatsPerVertex * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, FloatsPerVertex * sizeof(float),
            (void*)(3 * sizeof(float)));
        _gl.BindVertexArray(0);
    }

    /// <summary>How many letters went down last frame, for the checks to read.</summary>
    public int Glyphs { get; private set; }

    public unsafe void Draw(
        SignBank signs, VoxelWorld world, BlockRegistry registry, HudRenderer font,
        Matrix4x4 viewProj, Vector3 eye)
    {
        _at = 0;
        Glyphs = 0;

        foreach (var ((x, y, z), lines) in signs.All)
        {
            var cell = new Vector3(x, y, z);
            if (Vector3.DistanceSquared(cell + new Vector3(0.5f), eye) > Reach * Reach) continue;
            if (StarterBlocks.SignOf(registry[world.GetBlock(x, y, z)].Name) is not var (facing, wall)) continue;

            var (centre, normal, _) = BlockModel.SignFace(facing, wall);

            // Only the side the words are on: from behind, a board has nothing written on it.
            if (Vector3.Dot(eye - (cell + centre), normal) <= 0f) continue;

            var right = Vector3.Cross(Vector3.UnitY, normal);
            var face = cell + centre + normal * 0.004f;

            for (var line = 0; line < lines.Length; line++)
            {
                var text = lines[line];
                if (text.Length == 0) continue;

                var width = 0f;
                foreach (var c in text) width += font.Glyph(c).Advance / 16f * GlyphSize;

                var top = (lines.Length / 2f - line) * LineHeight - (LineHeight - GlyphSize) / 2f;
                var pen = -width / 2f;

                foreach (var c in text)
                {
                    var (layer, advance) = font.Glyph(c);
                    var from = face + right * pen + Vector3.UnitY * top;
                    Quad(from, right * GlyphSize, -Vector3.UnitY * GlyphSize, layer);
                    pen += advance / 16f * GlyphSize;
                    Glyphs++;
                }
            }
        }

        if (_at == 0) return;

        var cull = _gl.IsEnabled(EnableCap.CullFace);
        _gl.Disable(EnableCap.CullFace);
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(-1f, -2f);

        _shader.Use();
        _shader.SetMatrix4("uViewProj", viewProj);
        _shader.SetInt("uFont", 0);
        _shader.SetVec4("uInk", new Vector4(0.06f, 0.045f, 0.03f, 1f));
        font.FontTiles.Bind(TextureUnit.Texture0);

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = _vertices)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_at * sizeof(float)), p, BufferUsageARB.StreamDraw);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(_at / FloatsPerVertex));
        _gl.BindVertexArray(0);

        _gl.Disable(EnableCap.PolygonOffsetFill);
        _gl.DepthMask(true);
        if (cull) _gl.Enable(EnableCap.CullFace);
    }

    /// <summary>Two triangles from a top-left corner and the two edges out of it.</summary>
    private void Quad(Vector3 corner, Vector3 across, Vector3 down, int layer)
    {
        if (_at + 6 * FloatsPerVertex > _vertices.Length) Array.Resize(ref _vertices, _vertices.Length * 2);

        Vertex(corner, 0f, 0f, layer);
        Vertex(corner + down, 0f, 1f, layer);
        Vertex(corner + across + down, 1f, 1f, layer);
        Vertex(corner, 0f, 0f, layer);
        Vertex(corner + across + down, 1f, 1f, layer);
        Vertex(corner + across, 1f, 0f, layer);
    }

    private void Vertex(Vector3 at, float u, float v, int layer)
    {
        _vertices[_at++] = at.X;
        _vertices[_at++] = at.Y;
        _vertices[_at++] = at.Z;
        _vertices[_at++] = u;
        _vertices[_at++] = v;
        _vertices[_at++] = layer;
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }
}
