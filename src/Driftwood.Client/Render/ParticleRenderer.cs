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
        layout(location = 2) in vec4 aLight;   // rgb, and how much of it is left

        uniform mat4 uViewProj;
        uniform vec3 uCameraPos;
        uniform float uFogStart;
        uniform float uFogEnd;

        out vec3 vUvw;
        out vec4 vLight;
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
        in vec4 vLight;
        in float vFog;

        uniform sampler2DArray uBlocks;
        uniform vec3 uFogColor;
        uniform float uCutout;
        uniform float uAdditive;

        out vec4 FragColor;

        void main()
        {
            vec4 texel = texture(uBlocks, vUvw);
            // ⛳ The cutout is what keeps a chip in the opaque pass, where it writes depth and needs
            // no sorting. A flame has no edge to cut out — its own tile is already a shape, and what
            // varies is how much of it is left — so the blended pass turns the discard off and lets
            // the alpha carry it instead. One shader, one uniform, two behaviours.
            if (uCutout > 0.5 && texel.a < 0.5) discard;
            if (texel.a < 0.02) discard;

            vec3 coloured = texel.rgb * vLight.rgb;
            // Additive light disappears into distant fog; adding the fog colour itself once per
            // particle would make a portal turn into a white square at the horizon.
            vec3 fogged = uAdditive > 0.5
                ? coloured * (1.0 - vFog)
                : mix(coloured, uFogColor, vFog);
            FragColor = vec4(fogged, vLight.a * texel.a);
        }
        """;

    /// <summary>Floats per vertex: position, texture coordinate with layer, light, and alpha.</summary>
    private const int Stride = 10;

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
        _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, (uint)stride, (void*)(6 * sizeof(float)));

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

        var at = 0;

        // ⛳ THREE PASSES OUT OF ONE BUFFER: solid debris, ordinary alpha, then scarce additive
        // glints. A chip still writes depth, smoke layers correctly, and magic can make light
        // without turning every interaction puff into neon.
        var solid = 0;
        foreach (var p in live)
        {
            if (p.Look != ParticleLook.Debris) continue;
            Quad(p);
            solid++;
        }

        var blended = 0;
        foreach (var p in live)
        {
            if (p.Look is ParticleLook.Debris or ParticleLook.Glow) continue;
            Quad(p);
            blended++;
        }

        var additive = 0;
        foreach (var p in live)
        {
            if (p.Look != ParticleLook.Glow) continue;
            Quad(p);
            additive++;
        }

        DrawnParticles = solid + blended + additive;

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

        if (solid > 0)
        {
            _shader.SetFloat("uCutout", 1f);
            _shader.SetFloat("uAdditive", 0f);
            _gl.DrawElements(
                PrimitiveType.Triangles, (uint)(solid * 6), DrawElementsType.UnsignedInt, (void*)0);
        }

        if (blended > 0)
        {
            // ⛔ Depth-tested and not depth-written. A hundred smoke quads in a plume all sit at
            // nearly the same depth, so writing it means the first one drawn hides the rest and a
            // column of smoke reads as a single flat card. And the state goes back afterwards —
            // everything else in this frame is drawn expecting depth writes on.
            _shader.SetFloat("uCutout", 0f);
            _shader.SetFloat("uAdditive", 0f);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.DepthMask(false);

            _gl.DrawElements(
                PrimitiveType.Triangles, (uint)(blended * 6), DrawElementsType.UnsignedInt,
                (void*)(nint)(solid * 6 * sizeof(uint)));

            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
        }

        if (additive > 0)
        {
            _shader.SetFloat("uCutout", 0f);
            _shader.SetFloat("uAdditive", 1f);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
            _gl.DepthMask(false);

            _gl.DrawElements(
                PrimitiveType.Triangles, (uint)(additive * 6), DrawElementsType.UnsignedInt,
                (void*)(nint)((solid + blended) * 6 * sizeof(uint)));

            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
        }

        _gl.BindVertexArray(0);

        void Quad(in Particle p)
        {
            // ⛳ Fire makes its own light and must not be dimmed by the cave it is in — it is the
            // thing lighting the cave. Smoke and debris take the light where they stand.
            var light = p.Look is ParticleLook.Flame or ParticleLook.Glow
                ? Vector3.One
                : lightAt(p.Position);
            light *= new Vector3(p.Tint.X, p.Tint.Y, p.Tint.Z);

            // Thinning rather than switching off, which is the whole difference between smoke
            // dissipating and smoke being deleted. Flame goes faster than linear so a tongue is
            // bright for most of its life and then gone, rather than fading the whole way.
            var life = p.Life <= 0f ? 1f : Math.Clamp(1f - p.Age / p.Life, 0f, 1f);
            var alpha = p.Look switch
            {
                ParticleLook.Flame => MathF.Sqrt(life),
                ParticleLook.Smoke => life * life * 0.75f,
                ParticleLook.Soft => MathF.Min(1f, p.Age * 8f) * life * life,
                ParticleLook.Glow => MathF.Min(1f, p.Age * 10f) * MathF.Sqrt(life),
                _ => 1f,
            } * p.Tint.W;

            var crop = p.FullTile ? 1f : 1f / ParticleSystem.CropsPerAxis;
            var u = p.FullTile ? 0f : p.CropX * crop;
            var v = p.FullTile ? 0f : p.CropY * crop;

            var cosine = MathF.Cos(p.Rotation);
            var sine = MathF.Sin(p.Rotation);
            var particleRight = right * cosine + up * sine;
            var particleUp = -right * sine + up * cosine;

            // Corners counter-clockwise seen from the camera, with the texture crop laid over them.
            Write(p.Position - particleRight * p.Size - particleUp * p.Size, u, v + crop, p.Layer, light, alpha);
            Write(p.Position + particleRight * p.Size - particleUp * p.Size, u + crop, v + crop, p.Layer, light, alpha);
            Write(p.Position + particleRight * p.Size + particleUp * p.Size, u + crop, v, p.Layer, light, alpha);
            Write(p.Position - particleRight * p.Size + particleUp * p.Size, u, v, p.Layer, light, alpha);
        }

        void Write(Vector3 position, float u, float v, ushort layer, Vector3 light, float alpha)
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
            _vertices[at++] = alpha;
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
