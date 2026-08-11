using System.Numerics;
using Driftwood.Core.Magic;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>
/// A world-space, double-sided Gateway Rift. The shader continuously turns several differently
/// paced polar bands inside a feathered ellipse, giving the flat doorway visible depth without a
/// flipbook texture or a screen-space effect.
/// </summary>
public sealed class GatewayRiftRenderer : IDisposable
{
    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec2 aPos;

        uniform mat4 uViewProj;
        uniform vec3 uOrigin;
        uniform float uYaw;
        uniform vec2 uSize;

        out vec2 vUv;

        void main()
        {
            float yaw = radians(uYaw);
            vec3 across = vec3(cos(yaw), 0.0, sin(yaw));
            vec3 world = uOrigin
                + across * (aPos.x * uSize.x)
                + vec3(0.0, (aPos.y + 0.5) * uSize.y, 0.0);
            vUv = aPos + vec2(0.5);
            gl_Position = uViewProj * vec4(world, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        in vec2 vUv;
        uniform float uTime;
        uniform float uRank;
        out vec4 FragColor;

        void main()
        {
            vec2 p = vUv * 2.0 - 1.0;
            float radius = length(p);
            if (radius > 1.0) discard;

            float angle = atan(p.y, p.x);
            float inward = 1.0 - radius;
            float twist = angle * 5.0
                - uTime * (1.25 + inward * 2.15)
                + sin(radius * 17.0 - uTime * 1.8) * 0.42;
            float ribbon = 0.5 + 0.5 * sin(twist + radius * 13.0);
            float counter = 0.5 + 0.5 * sin(angle * 3.0 + uTime * 0.9 - radius * 22.0);
            float sparks = pow(max(0.0, sin(angle * 11.0 - radius * 31.0 + uTime * 2.4)), 18.0);

            vec3 deep = vec3(0.055, 0.025, 0.12);
            vec3 violet = vec3(0.42, 0.20, 0.72);
            vec3 blue = vec3(0.12, 0.48, 0.82);
            vec3 colour = mix(deep, violet, ribbon * (0.45 + inward * 0.45));
            colour = mix(colour, blue, counter * inward * 0.58);
            colour += sparks * vec3(0.72, 0.82, 1.0) * (0.22 + 0.05 * uRank);

            float core = smoothstep(0.0, 0.32, radius);
            colour *= mix(0.38, 1.0, core);
            float rim = smoothstep(0.72, 0.97, radius);
            colour += rim * vec3(0.34, 0.18, 0.58);
            float alpha = smoothstep(1.0, 0.91, radius) * (0.72 + rim * 0.24);
            FragColor = vec4(colour, alpha);
        }
        """;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;

    public unsafe GatewayRiftRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        float[] vertices = [-0.5f, -0.5f, 0.5f, -0.5f, 0.5f, 0.5f, -0.5f, 0.5f];
        uint[] indices = [0, 1, 2, 0, 2, 3];

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = vertices)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), p,
                BufferUsageARB.StaticDraw);
        _ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* p = indices)
            gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), p,
                BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        gl.BindVertexArray(0);
    }

    public unsafe void Draw(IReadOnlyCollection<GatewayRift> rifts, Matrix4x4 viewProj, float time)
    {
        if (rifts.Count == 0) return;
        _shader.Use();
        _shader.SetMatrix4("uViewProj", viewProj);
        _shader.SetFloat("uTime", time);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.CullFace);
        _gl.BindVertexArray(_vao);

        foreach (var rift in rifts)
        {
            _shader.SetVec3("uOrigin", rift.Position);
            _shader.SetFloat("uYaw", rift.Yaw);
            _shader.SetVec2("uSize", new Vector2(rift.Width, rift.Height));
            _shader.SetFloat("uRank", rift.Rank);
            _gl.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, (void*)0);
        }

        _gl.BindVertexArray(0);
        _gl.Enable(EnableCap.CullFace);
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
