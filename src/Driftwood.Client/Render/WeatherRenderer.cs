using System.Numerics;
using Driftwood.Core.Sky;
using Driftwood.Core.Textures;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>Depth-tested rain and snow point sprites distributed deterministically around the eye.</summary>
public sealed class WeatherRenderer : IDisposable
{
    private const int ParticleCount = 768;

    private const string VertexSource = """
        #version 330 core
        uniform mat4 uViewProj;
        uniform vec3 uCamera;
        uniform float uTime;
        uniform float uSeed;
        uniform float uViewportHeight;
        uniform int uSnow;
        out float vFade;

        float hash(float n) { return fract(sin(n) * 43758.5453123); }

        void main()
        {
            float id = float(gl_VertexID) + uSeed;
            vec3 random = vec3(hash(id * 17.13), hash(id * 41.71 + 3.1), hash(id * 73.37 + 9.7));
            vec3 offset = (random - 0.5) * vec3(56.0, 38.0, 56.0);
            float speed = uSnow != 0 ? 3.4 : 25.0;
            offset.y = mod(offset.y - uTime * speed + 19.0, 38.0) - 19.0;
            if (uSnow != 0)
            {
                offset.x += sin(uTime * 0.8 + id) * 1.8;
                offset.z += cos(uTime * 0.6 + id * 0.7) * 1.4;
            }
            vec3 world = uCamera + offset;
            gl_Position = uViewProj * vec4(world, 1.0);
            float distanceFade = 1.0 - smoothstep(10.0, 39.0, length(offset));
            vFade = distanceFade * smoothstep(-19.0, -13.0, offset.y)
                    * (1.0 - smoothstep(13.0, 19.0, offset.y));
            float pixels = uSnow != 0 ? 10.0 : 18.0;
            gl_PointSize = pixels * clamp(uViewportHeight / 900.0, 0.65, 1.8)
                           / max(0.65, gl_Position.w * 0.045);
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        in float vFade;
        uniform sampler2D uRain;
        uniform sampler2D uSnowSprite;
        uniform int uSnow;
        uniform float uStrength;
        out vec4 FragColor;
        void main()
        {
            vec4 sprite = uSnow != 0 ? texture(uSnowSprite, gl_PointCoord)
                                     : texture(uRain, gl_PointCoord);
            sprite.a *= vFade * uStrength;
            if (sprite.a < 0.025) discard;
            FragColor = sprite;
        }
        """;

    private readonly GL _gl;
    private Shader? _shader;
    private Texture2D? _rain;
    private Texture2D? _snow;
    private uint _vao;

    public bool Available { get; private set; }
    public string Failure { get; private set; } = "";
    public int Drawn { get; private set; }

    public WeatherRenderer(GL gl, EnvironmentTextureSet.Result art)
    {
        _gl = gl;
        try
        {
            _shader = new Shader(gl, VertexSource, FragmentSource);
            _rain = new Texture2D(gl, art.Rain);
            _snow = new Texture2D(gl, art.Snow);
            _vao = gl.GenVertexArray();
            Available = true;
        }
        catch (Exception error)
        {
            Failure = error.Message;
            Available = false;
        }
    }

    public void Draw(
        in WeatherState weather,
        Matrix4x4 viewProj,
        Vector3 camera,
        float elapsed,
        long seed,
        int viewportHeight)
    {
        Drawn = 0;
        if (!Available || !weather.Active || _shader is null || _rain is null || _snow is null) return;

        _shader.Use();
        _shader.SetMatrix4("uViewProj", viewProj);
        _shader.SetVec3("uCamera", camera);
        _shader.SetFloat("uTime", elapsed % 10_000f);
        _shader.SetFloat("uSeed", Math.Abs(seed % 8_191));
        _shader.SetFloat("uViewportHeight", viewportHeight);
        _shader.SetInt("uSnow", weather.Kind == Precipitation.Snow ? 1 : 0);
        _shader.SetFloat("uStrength", weather.Strength * 0.78f);
        _shader.SetInt("uRain", 0);
        _shader.SetInt("uSnowSprite", 1);
        _rain.Bind(TextureUnit.Texture0);
        _snow.Bind(TextureUnit.Texture1);

        _gl.Enable(EnableCap.ProgramPointSize);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Points, 0, ParticleCount);
        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.ProgramPointSize);
        Drawn = ParticleCount;
    }

    public void Dispose()
    {
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        _rain?.Dispose();
        _snow?.Dispose();
        _shader?.Dispose();
    }
}
