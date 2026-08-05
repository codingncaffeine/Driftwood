using System.Numerics;
using Driftwood.Core.Blocks;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>
/// The dark frame drawn around whatever block the player is looking at.
/// </summary>
/// <remarks>
/// <para>Twelve thin boxes rather than twelve lines. A line in core-profile OpenGL is guaranteed at
/// exactly one width and no more, which on any modern display is a hairline that disappears against
/// half the world — and the selection frame is the single most-looked-at thing on screen, the one
/// piece of feedback that says what a click is about to take. Boxes cost 288 vertices rebuilt per
/// frame and can be any thickness.</para>
/// <para>Its own tiny shader rather than a special case inside the chunk pass. The chunk shader
/// takes packed integer vertices, a palette and baked light; nothing about a frame wants any of
/// that, and threading one through would put a branch in the hottest shader in the game.</para>
/// <para>Swelled a fraction off the surface it describes, because geometry exactly coplanar with
/// the face it outlines is a z-fighting shimmer.</para>
/// </remarks>
public sealed class BlockOutline : IDisposable
{
    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        uniform mat4 uViewProj;
        uniform vec3 uOrigin;
        void main() { gl_Position = uViewProj * vec4(uOrigin + aPos, 1.0); }
        """;

    private const string FragmentSource = """
        #version 330 core
        uniform vec4 uColor;
        out vec4 FragColor;
        void main() { FragColor = uColor; }
        """;

    /// <summary>How thick each edge is, in blocks.</summary>
    /// <remarks>
    /// Fixed in world units rather than in pixels, so the frame thins with distance the way an
    /// edge painted on the block would. A constant pixel width reads as an overlay drawn on the
    /// screen instead of as something in the world.
    /// </remarks>
    private const float Thickness = 0.0125f;

    /// <summary>How far the frame floats off the surface.</summary>
    private const float Swell = 0.003f;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;

    private readonly float[] _vertices = new float[12 * 24 * 3];
    private int _at;

    public unsafe BlockOutline(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_vertices.Length * sizeof(float)), null, BufferUsageARB.StreamDraw);

        // Twelve boxes, six faces each, and the pattern never changes.
        var indices = new uint[12 * 36];
        for (var box = 0; box < 12; box++)
        for (var face = 0; face < 6; face++)
        {
            var v = (uint)(box * 24 + face * 4);
            var i = (box * 6 + face) * 6;
            indices[i] = v;
            indices[i + 1] = v + 1;
            indices[i + 2] = v + 2;
            indices[i + 3] = v;
            indices[i + 4] = v + 2;
            indices[i + 5] = v + 3;
        }

        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* p = indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);

        _gl.BindVertexArray(0);
    }

    /// <param name="min">The shape's own lower corner within the cell, in block units.</param>
    /// <param name="max">Its upper corner. A full cube is 0 to 1.</param>
    public unsafe void Draw(Matrix4x4 viewProj, Vector3 blockOrigin, Vector3 min, Vector3 max)
    {
        var a = min - new Vector3(Swell);
        var b = max + new Vector3(Swell);

        _at = 0;

        // The four edges running along each axis, each grown by the thickness on the two axes it
        // does not run along and by the same again on the ends, so the corners close up solid.
        for (var axis = 0; axis < 3; axis++)
        for (var corner = 0; corner < 4; corner++)
        {
            var low = a;
            var high = b;

            var first = axis == 0 ? 1 : 0;
            var second = axis == 2 ? 1 : 2;

            Pin(ref low, ref high, first, (corner & 1) == 0);
            Pin(ref low, ref high, second, (corner & 2) == 0);

            Box(low - new Vector3(Thickness), high + new Vector3(Thickness));
        }

        _shader.Use();
        _shader.SetMatrix4("uViewProj", viewProj);
        _shader.SetVec3("uOrigin", blockOrigin);
        _shader.SetVec4("uColor", new Vector4(0.03f, 0.03f, 0.04f, 1f));

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = _vertices)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(_at * sizeof(float)), p);

        _gl.DrawElements(PrimitiveType.Triangles, 12 * 36, DrawElementsType.UnsignedInt, (void*)0);
        _gl.BindVertexArray(0);

        // Collapses the box onto one side of an axis, which is what turns a cube into an edge.
        static void Pin(ref Vector3 low, ref Vector3 high, int axis, bool atLow)
        {
            switch (axis)
            {
                case 0: if (atLow) high.X = low.X; else low.X = high.X; break;
                case 1: if (atLow) high.Y = low.Y; else low.Y = high.Y; break;
                default: if (atLow) high.Z = low.Z; else low.Z = high.Z; break;
            }
        }
    }

    /// <summary>Writes one box, wound outward, using the corner table the mesher already checks.</summary>
    private void Box(Vector3 low, Vector3 high)
    {
        for (var face = 0; face < Faces.Count; face++)
        foreach (var corner in Faces.Corners[face])
        {
            _vertices[_at++] = corner.X == 0 ? low.X : high.X;
            _vertices[_at++] = corner.Y == 0 ? low.Y : high.Y;
            _vertices[_at++] = corner.Z == 0 ? low.Z : high.Z;
        }
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }
}
