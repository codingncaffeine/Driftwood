using System.Numerics;
using Driftwood.Core.Sky;
using Driftwood.Core.Textures;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>
/// The sky behind everything: a gradient, the sun, the moon and the stars.
/// </summary>
/// <remarks>
/// <para>One triangle covering the screen, and a direction worked out per pixel. There is no dome
/// and no geometry to tessellate — the sky is a function of which way you are looking, and asking
/// that question per pixel is both exact and cheaper than any mesh that approximates it. A dome
/// would also have to be big enough not to clip the far plane and small enough not to lose
/// precision, and it is a nuisance at both ends.</para>
/// <para>Drawn first, with depth off. Everything else in the frame is nearer than the sky by
/// definition, so it needs neither to be tested nor to write.</para>
/// <para>The ray is built from the camera's own basis rather than by inverting the view-projection.
/// Inverting a matrix to recover something the camera already knows is work, and it goes wrong
/// quietly at grazing angles; a forward, a right and an up cannot.</para>
/// </remarks>
public sealed class SkyRenderer : IDisposable
{
    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec2 aClip;
        out vec2 vClip;
        void main()
        {
            vClip = aClip;
            gl_Position = vec4(aClip, 1.0, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 330 core

        in vec2 vClip;

        uniform vec3 uForward;
        uniform vec3 uRight;
        uniform vec3 uUp;
        uniform float uTanHalfFov;
        uniform float uAspect;

        uniform vec3 uZenith;
        uniform vec3 uHorizon;
        uniform vec3 uSunDir;
        uniform vec3 uMoonDir;
        uniform vec3 uSunColor;
        uniform float uStarFade;
        uniform sampler2D uSunSprite;
        uniform sampler2D uMoonSprite;

        out vec4 FragColor;

        // Cheap 3D hash, only ever fed integers. Stars are the one place a pattern would be
        // obvious, so this has to be a real hash rather than a product of sines.
        float hash(vec3 p)
        {
            p = fract(p * 0.3183099 + vec3(0.71, 0.113, 0.419));
            p += dot(p, p.yzx + 19.19);
            return fract((p.x + p.y) * p.z);
        }

        vec4 celestial(sampler2D sprite, vec3 direction, vec3 ray, float radius)
        {
            // A planar sprite also projects to the same coordinates at the antipode. Reject the
            // back hemisphere explicitly or every pack-authored sun and moon appears twice.
            if (dot(ray, direction) <= 0.0) return vec4(0.0);
            vec3 reference = abs(direction.y) > 0.96 ? vec3(0.0, 0.0, 1.0) : vec3(0.0, 1.0, 0.0);
            vec3 right = normalize(cross(reference, direction));
            vec3 up = cross(direction, right);
            vec2 uv = vec2(dot(ray, right), dot(ray, up)) / radius * 0.5 + 0.5;
            if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) return vec4(0.0);
            return texture(sprite, vec2(uv.x, 1.0 - uv.y));
        }

        void main()
        {
            vec3 ray = normalize(
                uForward + uRight * (vClip.x * uTanHalfFov * uAspect) + uUp * (vClip.y * uTanHalfFov));

            // The gradient. Below the horizon it keeps going down rather than stopping flat: from a
            // hilltop a good deal of the screen is sky under eye level, and a hard line there reads
            // as a wall.
            float above = smoothstep(0.0, 0.42, ray.y);
            float below = smoothstep(-0.35, 0.0, ray.y);
            vec3 color = mix(uHorizon * 0.62, mix(uHorizon, uZenith, above), below);

            // Stars, quantised onto a grid of directions. Only a small share of cells hold one, and
            // each is dimmed by how far the ray is from its cell's own centre so they read as points
            // rather than as squares.
            if (uStarFade > 0.002)
            {
                vec3 cell = floor(ray * 190.0);
                float roll = hash(cell);
                if (roll > 0.9975)
                {
                    vec3 centre = (cell + vec3(hash(cell + 3.7), hash(cell + 7.1), hash(cell + 11.3))) / 190.0;
                    float near = 1.0 - smoothstep(0.0, 0.0035, distance(normalize(centre), ray));
                    float twinkle = 0.55 + 0.45 * hash(cell + 17.0);
                    color += vec3(0.95, 0.96, 1.0) * near * twinkle * uStarFade
                             * smoothstep(-0.05, 0.15, ray.y);
                }
            }

            // The moon first, so the sun paints over it on the rare mornings they share the sky.
            vec4 moon = celestial(uMoonSprite, uMoonDir, ray, 0.052);
            color = mix(color, moon.rgb, moon.a * uStarFade);

            // The sun: a broad glow that lights the whole quarter of the sky it is in, then the disc.
            float toSun = dot(ray, uSunDir);
            color += uSunColor * pow(max(toSun, 0.0), 220.0) * 1.4;
            color += uSunColor * pow(max(toSun, 0.0), 8.0) * 0.10;
            vec4 sun = celestial(uSunSprite, uSunDir, ray, 0.043);
            color = mix(color, sun.rgb, sun.a);

            FragColor = vec4(color, 1.0);
        }
        """;

    // One triangle rather than two, so nothing is shaded twice down the diagonal.
    private static readonly float[] Corners = [-1f, -1f, 3f, -1f, -1f, 3f];

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly Texture2D _sun;
    private readonly Texture2D _moon;

    public unsafe SkyRenderer(GL gl, EnvironmentTextureSet.Result? art = null)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        art ??= EnvironmentTextureSet.Build(null);
        _sun = new Texture2D(gl, art.Sun);
        _moon = new Texture2D(gl, art.Moon);

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = Corners)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(Corners.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);

        _gl.BindVertexArray(0);
    }

    public void Draw(in SkyState sky, Vector3 forward, float fovDegrees, float aspect)
    {
        // Right and up from forward, which is safe because pitch is clamped short of straight up.
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Cross(right, forward);

        _shader.Use();
        _shader.SetVec3("uForward", forward);
        _shader.SetVec3("uRight", right);
        _shader.SetVec3("uUp", up);
        _shader.SetFloat("uTanHalfFov", MathF.Tan(float.DegreesToRadians(fovDegrees) * 0.5f));
        _shader.SetFloat("uAspect", aspect);

        _shader.SetVec3("uZenith", sky.Zenith);
        _shader.SetVec3("uHorizon", sky.Horizon);
        _shader.SetVec3("uSunDir", sky.SunDirection);
        _shader.SetVec3("uMoonDir", sky.MoonDirection);

        // The disc keeps its colour after dusk has taken the light out of the sun term, or the sun
        // goes out several minutes before it reaches the horizon.
        _shader.SetVec3("uSunColor", Vector3.Max(sky.SunColor, new Vector3(0.45f, 0.30f, 0.18f)));
        _shader.SetFloat("uStarFade", sky.StarFade);
        _shader.SetInt("uSunSprite", 0);
        _shader.SetInt("uMoonSprite", 1);
        _sun.Bind(TextureUnit.Texture0);
        _moon.Bind(TextureUnit.Texture1);

        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);

        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        // Put the pipeline back exactly as it was found; the chunk pass sets neither per frame.
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _sun.Dispose();
        _moon.Dispose();
        _shader.Dispose();
    }
}
