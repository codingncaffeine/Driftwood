using System.Numerics;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>
/// The cast line and its float: a thin dark line hung from the rod's tip, and the reference's
/// red-over-white float sitting on the water.
/// </summary>
/// <remarks>
/// ⛳ Fishing, 2026-10-16. Plain coloured geometry rather than a texture: the reference draws its
/// float from an entity sheet no pack in the measured set even ships, and two colours are what make
/// it read at thirty blocks. The line turns to face the eye so it never thins to nothing edge-on,
/// and sags in the middle by how much slack there is, which is what makes it read as a line rather
/// than a laser.
/// </remarks>
public sealed class FishingRenderer : IDisposable
{
    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aColor;
        uniform mat4 uViewProj;
        uniform vec3 uLight;
        out vec3 vColor;
        void main()
        {
            vColor = aColor * uLight;
            gl_Position = uViewProj * vec4(aPos, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        in vec3 vColor;
        out vec4 FragColor;
        void main() { FragColor = vec4(vColor, 1.0); }
        """;

    private const int Segments = 16;
    private const float LineWidth = 0.014f;
    private const float FloatSize = 0.14f;

    private static readonly Vector3 LineColour = new(0.10f, 0.10f, 0.11f);
    private static readonly Vector3 FloatTop = new(0.80f, 0.12f, 0.10f);
    private static readonly Vector3 FloatBottom = new(0.92f, 0.92f, 0.90f);

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly float[] _vertices = new float[(Segments * 6 + 12 * 6) * 6];
    private int _at;

    public unsafe FishingRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);
        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
        _gl.BindVertexArray(0);
    }

    public unsafe void Draw(Matrix4x4 viewProj, Vector3 eye, Vector3 rodTip, Vector3 bob, bool slack, Vector3 light)
    {
        _at = 0;

        // The line: a curve dropping below the straight by how slack it is, as a ribbon facing the eye.
        var length = Vector3.Distance(rodTip, bob);
        var sag = slack ? MathF.Min(length * 0.12f, 1.5f) : length * 0.02f;
        var previous = rodTip;
        for (var i = 1; i <= Segments; i++)
        {
            var t = i / (float)Segments;
            var point = Vector3.Lerp(rodTip, bob + new Vector3(0f, FloatSize * 0.5f, 0f), t)
                        - new Vector3(0f, sag * 4f * t * (1f - t), 0f);
            var along = point - previous;
            var side = Vector3.Cross(along, eye - point);
            if (side.LengthSquared() > 1e-10f) side = Vector3.Normalize(side) * (LineWidth * 0.5f);
            Quad(previous - side, previous + side, point + side, point - side, LineColour);
            previous = point;
        }

        // The float: white below, red above, a small box riding on the water.
        var half = FloatSize * 0.5f;
        Box(bob + new Vector3(-half, 0f, -half), bob + new Vector3(half, FloatSize * 0.5f, half), FloatBottom);
        Box(bob + new Vector3(-half, FloatSize * 0.5f, -half), bob + new Vector3(half, FloatSize, half), FloatTop);

        var cull = _gl.IsEnabled(EnableCap.CullFace);
        _gl.Disable(EnableCap.CullFace);
        _shader.Use();
        _shader.SetMatrix4("uViewProj", viewProj);
        _shader.SetVec3("uLight", light);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = _vertices)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_at * sizeof(float)), p, BufferUsageARB.StreamDraw);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(_at / 6));
        _gl.BindVertexArray(0);
        if (cull) _gl.Enable(EnableCap.CullFace);
    }

    private void Box(Vector3 low, Vector3 high, Vector3 colour)
    {
        Vector3 C(int x, int y, int z) => new(x == 0 ? low.X : high.X, y == 0 ? low.Y : high.Y, z == 0 ? low.Z : high.Z);
        Quad(C(0, 0, 0), C(1, 0, 0), C(1, 1, 0), C(0, 1, 0), colour * 0.85f);
        Quad(C(0, 0, 1), C(1, 0, 1), C(1, 1, 1), C(0, 1, 1), colour * 0.85f);
        Quad(C(0, 0, 0), C(0, 0, 1), C(0, 1, 1), C(0, 1, 0), colour * 0.75f);
        Quad(C(1, 0, 0), C(1, 0, 1), C(1, 1, 1), C(1, 1, 0), colour * 0.75f);
        Quad(C(0, 1, 0), C(1, 1, 0), C(1, 1, 1), C(0, 1, 1), colour);
        Quad(C(0, 0, 0), C(1, 0, 0), C(1, 0, 1), C(0, 0, 1), colour * 0.6f);
    }

    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 colour)
    {
        foreach (var v in (ReadOnlySpan<Vector3>)[a, b, c, a, c, d])
        {
            _vertices[_at++] = v.X;
            _vertices[_at++] = v.Y;
            _vertices[_at++] = v.Z;
            _vertices[_at++] = colour.X;
            _vertices[_at++] = colour.Y;
            _vertices[_at++] = colour.Z;
        }
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }
}
