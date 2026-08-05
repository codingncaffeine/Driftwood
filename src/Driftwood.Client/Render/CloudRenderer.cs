using System.Numerics;
using Driftwood.Core.Sky;
using Driftwood.Core.Spatial;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>
/// The cloud layer: one extruded sheet, drawn again beside itself so the sky never ends.
/// </summary>
/// <remarks>
/// <para>The geometry is built once and never rebuilt. The sheet wraps, so drifting it is a
/// translation and following the player is a translation, and both come out of a uniform — there is
/// nothing about a cloud moving that a vertex buffer needs to know.</para>
/// <para>Four copies, laid out around whichever cell of the infinite grid the player is standing in.
/// A single copy is wider than any view distance, but the player is not at its centre, so one copy
/// leaves an empty quarter of the sky behind them. Four is what makes it endless in every
/// direction, and each is one draw call over a static buffer.</para>
/// <para>Drawn last, blended, with depth writes off. They are the only translucent thing in the
/// frame and everything opaque has already written its depth, so they need no sorting beyond back
/// face culling — which leaves exactly one layer of cloud between the eye and whatever is past it.</para>
/// </remarks>
public sealed class CloudRenderer : IDisposable
{
    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in float aShade;

        uniform mat4 uViewProj;
        uniform vec3 uOrigin;
        uniform vec3 uCameraPos;
        uniform float uFogStart;
        uniform float uFogEnd;

        out float vShade;
        out float vFog;

        void main()
        {
            vec3 world = uOrigin + aPos;
            gl_Position = uViewProj * vec4(world, 1.0);
            vShade = aShade;

            // Distance measured flat. A cloud layer is a hundred blocks over your head and the
            // height difference is the same for all of it, so folding it in would fade the sky
            // directly above as hard as the sky at the edge of sight.
            float d = length(world.xz - uCameraPos.xz);
            vFog = clamp((d - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        in float vShade;
        in float vFog;

        uniform vec3 uColor;
        uniform vec3 uFogColor;
        uniform float uAlpha;

        out vec4 FragColor;

        void main()
        {
            // Faded into the horizon rather than out to nothing: a cloud that goes transparent at
            // distance shows the sky through its middle, and the layer reads as tattered.
            vec3 lit = mix(uColor * vShade, uFogColor, vFog);
            FragColor = vec4(lit, uAlpha);
        }
        """;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private readonly int _indexCount;

    /// <summary>Quads in the sheet, for the line the client prints at startup.</summary>
    public int QuadCount { get; }

    /// <summary>Copies of the sheet the last frame actually drew, after culling.</summary>
    public int DrawnSheets { get; private set; }

    public unsafe CloudRenderer(GL gl, CloudMesh mesh)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _indexCount = mesh.Indices.Length;
        QuadCount = mesh.QuadCount;

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (CloudVertex* p = mesh.Vertices)
        {
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                (nuint)(mesh.Vertices.Length * CloudVertex.SizeInBytes),
                p,
                BufferUsageARB.StaticDraw);
        }

        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* p = mesh.Indices)
        {
            _gl.BufferData(
                BufferTargetARB.ElementArrayBuffer,
                (nuint)(mesh.Indices.Length * sizeof(uint)),
                p,
                BufferUsageARB.StaticDraw);
        }

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, CloudVertex.SizeInBytes, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, CloudVertex.SizeInBytes, (void*)12);

        _gl.BindVertexArray(0);
    }

    /// <param name="elapsedSeconds">Wall clock since the world opened, which is what makes them drift.</param>
    public unsafe void Draw(
        Matrix4x4 viewProj,
        Frustum frustum,
        Vector3 cameraPos,
        Vector3 color,
        Vector3 fogColor,
        float fogStart,
        float fogEnd,
        float elapsedSeconds)
    {
        if (_indexCount == 0) return;

        var drift = elapsedSeconds * CloudField.DriftBlocksPerSecond;

        // The corner of the sheet copy the camera is standing on, once the drift is taken out.
        var baseX = MathF.Floor((cameraPos.X - drift) / CloudField.Period) * CloudField.Period + drift;
        var baseZ = MathF.Floor(cameraPos.Z / CloudField.Period) * CloudField.Period;

        _shader.Use();
        _shader.SetMatrix4("uViewProj", viewProj);
        _shader.SetVec3("uCameraPos", cameraPos);
        _shader.SetVec3("uColor", color);
        _shader.SetVec3("uFogColor", fogColor);
        _shader.SetFloat("uFogStart", fogStart);
        _shader.SetFloat("uFogEnd", fogEnd);
        _shader.SetFloat("uAlpha", 0.82f);

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);

        _gl.BindVertexArray(_vao);

        // Nine candidates around the camera's own cell, so the sheet reaches past the far plane
        // whichever corner of a cell the player happens to be standing in. Almost all of them are
        // behind the eye or past the horizon; the frustum decides, exactly as it does for chunks.
        var drawn = 0;
        for (var dz = -1; dz <= 1; dz++)
        for (var dx = -1; dx <= 1; dx++)
        {
            var origin = new Vector3(
                baseX + dx * CloudField.Period,
                CloudField.Altitude,
                baseZ + dz * CloudField.Period);

            var max = origin + new Vector3(CloudField.Period, CloudField.Thickness, CloudField.Period);
            if (!frustum.IntersectsBox(origin, max)) continue;

            _shader.SetVec3("uOrigin", origin);
            _gl.DrawElements(PrimitiveType.Triangles, (uint)_indexCount, DrawElementsType.UnsignedInt, (void*)0);
            drawn++;
        }

        DrawnSheets = drawn;
        _gl.BindVertexArray(0);

        // Put the pipeline back exactly as it was found.
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }
}
