using System.Numerics;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>
/// The wireframe box drawn around whatever block the player is looking at.
/// </summary>
/// <remarks>
/// Its own tiny shader and buffer rather than a special case inside the chunk pass. The chunk
/// shader takes packed integer vertices, a palette and baked light; nothing about a debug-coloured
/// line wants any of that, and threading one through would put a branch in the hottest shader in
/// the game to serve twelve lines.
/// <para>Drawn with a depth bias toward the camera so it sits on the surface rather than fighting
/// it. A wireframe exactly coplanar with the face it outlines is a z-fighting shimmer, and the
/// selection box is the one piece of UI the player stares at constantly.</para>
/// </remarks>
public sealed class BlockOutline : IDisposable
{
    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        uniform mat4 uViewProj;
        uniform vec3 uOrigin;
        uniform float uSwell;
        void main()
        {
            vec3 p = uOrigin + (aPos - 0.5) * (1.0 + uSwell) + 0.5;
            gl_Position = uViewProj * vec4(p, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        uniform vec4 uColor;
        out vec4 FragColor;
        void main() { FragColor = uColor; }
        """;

    private static readonly float[] Corners =
    [
        0, 0, 0,  1, 0, 0,  1, 0, 1,  0, 0, 1,
        0, 1, 0,  1, 1, 0,  1, 1, 1,  0, 1, 1,
    ];

    private static readonly uint[] Edges =
    [
        0, 1, 1, 2, 2, 3, 3, 0,
        4, 5, 5, 6, 6, 7, 7, 4,
        0, 4, 1, 5, 2, 6, 3, 7,
    ];

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;

    public unsafe BlockOutline(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = Corners)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(Corners.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* p = Edges)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(Edges.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);

        _gl.BindVertexArray(0);
    }

    public unsafe void Draw(Matrix4x4 viewProj, Vector3 blockOrigin)
    {
        _shader.Use();
        _shader.SetMatrix4("uViewProj", viewProj);
        _shader.SetVec3("uOrigin", blockOrigin);
        _shader.SetFloat("uSwell", 0.004f);
        _shader.SetVec4("uColor", new Vector4(0.05f, 0.05f, 0.07f, 1f));

        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Lines, (uint)Edges.Length, DrawElementsType.UnsignedInt, (void*)0);
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }
}
