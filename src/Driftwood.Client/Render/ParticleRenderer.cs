using System.Numerics;
using Driftwood.Core.Particles;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>
/// Draws the debris: one camera-facing quad per particle, cropped out of the block's own tile.
/// </summary>
/// <remarks>
/// <para>Cut out rather than blended. Every particle is a piece of a block texture, most of which is
/// opaque, so discarding the clear texels keeps them in the opaque pass — where they write depth,
/// need no sorting against each other, and are occluded by terrain for free. Blending a few hundred
/// unsorted quads is the more expensive way to get a worse answer.</para>
/// <para>The billboard is built on the processor. The camera's right and up are two vectors the
/// client already has, the corner offsets are four constants, and a few hundred particles a frame is
/// nothing next to sending the basis to the card and expanding there — which would need either
/// geometry shaders or an instanced path, both of which are more machinery than this earns.</para>
/// </remarks>
public sealed class ParticleRenderer : IDisposable
{
    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aUvw;
        layout(location = 2) in vec3 aLight;

        uniform mat4 uViewProj;
        uniform vec3 uCameraPos;
        uniform float uFogStart;
        uniform float uFogEnd;

        out vec3 vUvw;
        out vec3 vLight;
        out float vFog;

        void main()
        {
            gl_Position = uViewProj * vec4(aPos, 1.0);
            vUvw = aUvw;
            vLight = aLight;
            vFog = clamp((length(aPos - uCameraPos) - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        in vec3 vUvw;
        in vec3 vLight;
        in float vFog;

        uniform sampler2DArray uBlocks;
        uniform vec3 uFogColor;

        out vec4 FragColor;

        void main()
        {
            vec4 texel = texture(uBlocks, vUvw);
            if (texel.a < 0.5) discard;
            FragColor = vec4(mix(texel.rgb * vLight, uFogColor, vFog), 1.0);
        }
        """;

    /// <summary>Floats per vertex: position, texture coordinate with layer, and light.</summary>
    private const int Stride = 9;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;

    private readonly float[] _vertices = new float[ParticleSystem.Capacity * 4 * Stride];

    /// <summary>Particles the last <see cref="Draw"/> put on screen.</summary>
    public int DrawnParticles { get; private set; }

    public unsafe ParticleRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(
            BufferTargetARB.ArrayBuffer,
            (nuint)(_vertices.Length * sizeof(float)),
            null,
            BufferUsageARB.StreamDraw);

        // The index pattern never changes — quad n is always vertices 4n..4n+3 — so it is uploaded
        // once and only the positions are restreamed.
        var indices = new uint[ParticleSystem.Capacity * 6];
        for (var q = 0; q < ParticleSystem.Capacity; q++)
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

        var stride = Stride * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)(6 * sizeof(float)));

        _gl.BindVertexArray(0);
    }

    /// <param name="lightAt">The colour a particle standing at a point should be multiplied by.</param>
    public unsafe void Draw(
        ParticleSystem particles,
        Matrix4x4 viewProj,
        Vector3 cameraPos,
        Vector3 cameraForward,
        Func<Vector3, Vector3> lightAt,
        Vector3 fogColor,
        float fogStart,
        float fogEnd)
    {
        DrawnParticles = 0;

        var live = particles.Live;
        if (live.Length == 0) return;

        var right = Vector3.Normalize(Vector3.Cross(cameraForward, Vector3.UnitY));
        var up = Vector3.Cross(right, cameraForward);

        const float crop = 1f / ParticleSystem.CropsPerAxis;
        var at = 0;

        foreach (var p in live)
        {
            var light = lightAt(p.Position);
            var u = p.CropX * crop;
            var v = p.CropY * crop;

            // Corners counter-clockwise seen from the camera, with the texture crop laid over them.
            Write(p.Position - right * p.Size - up * p.Size, u, v + crop, p.Layer, light);
            Write(p.Position + right * p.Size - up * p.Size, u + crop, v + crop, p.Layer, light);
            Write(p.Position + right * p.Size + up * p.Size, u + crop, v, p.Layer, light);
            Write(p.Position - right * p.Size + up * p.Size, u, v, p.Layer, light);

            DrawnParticles++;
        }

        _shader.Use();
        _shader.SetMatrix4("uViewProj", viewProj);
        _shader.SetVec3("uCameraPos", cameraPos);
        _shader.SetVec3("uFogColor", fogColor);
        _shader.SetFloat("uFogStart", fogStart);
        _shader.SetFloat("uFogEnd", fogEnd);
        _shader.SetInt("uBlocks", 0);

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = _vertices)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(at * sizeof(float)), p);

        _gl.DrawElements(
            PrimitiveType.Triangles, (uint)(DrawnParticles * 6), DrawElementsType.UnsignedInt, (void*)0);
        _gl.BindVertexArray(0);

        void Write(Vector3 position, float u, float v, ushort layer, Vector3 light)
        {
            _vertices[at++] = position.X;
            _vertices[at++] = position.Y;
            _vertices[at++] = position.Z;
            _vertices[at++] = u;
            _vertices[at++] = v;
            _vertices[at++] = layer;
            _vertices[at++] = light.X;
            _vertices[at++] = light.Y;
            _vertices[at++] = light.Z;
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
