using System.Numerics;
using Driftwood.Core.Sky;
using Driftwood.Core.Textures;
using Driftwood.Core.World;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>
/// Depth-tested rain and snow point sprites, drawn wherever <see cref="WeatherField"/> says a
/// drop exists this frame — which is only where the sky can see the cell.
/// </summary>
/// <remarks>
/// ⛳ <b>This used to hash every drop's place in the vertex shader from nothing but the eye</b>,
/// which is how rain fell through cave air and under every roof (the user's report, 2026-08-22).
/// The world is the one thing a shader cannot ask, so the placing moved to the CPU, where the
/// same code serves a headless check, and this file is left with what a renderer is for: a buffer
/// of positions, a point size and a fade. <see cref="Drawn"/> is now a count of drops that exist,
/// not of points submitted — zero in a cave, full in a field, something in a pit.
/// </remarks>
public sealed class WeatherRenderer : IDisposable
{
    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        uniform mat4 uViewProj;
        uniform vec3 uCamera;
        uniform float uViewportHeight;
        uniform int uSnow;
        out float vFade;

        void main()
        {
            vec3 offset = aPosition - uCamera;
            gl_Position = uViewProj * vec4(aPosition, 1.0);
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
    private readonly Vector3[] _drops = new Vector3[WeatherField.ParticleCount];
    private Shader? _shader;
    private Texture2D? _rain;
    private Texture2D? _snow;
    private uint _vao;
    private uint _vbo;

    public bool Available { get; private set; }
    public string Failure { get; private set; } = "";

    /// <summary>How many drops existed on the last draw — the ones the sky could see.</summary>
    public int Drawn { get; private set; }

    public unsafe WeatherRenderer(GL gl, EnvironmentTextureSet.Result art)
    {
        _gl = gl;
        try
        {
            _shader = new Shader(gl, VertexSource, FragmentSource);
            _rain = new Texture2D(gl, art.Rain);
            _snow = new Texture2D(gl, art.Snow);

            _vao = gl.GenVertexArray();
            gl.BindVertexArray(_vao);
            _vbo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                (nuint)(_drops.Length * 3 * sizeof(float)),
                null,
                BufferUsageARB.StreamDraw);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
            gl.BindVertexArray(0);

            Available = true;
        }
        catch (Exception error)
        {
            Failure = error.Message;
            Available = false;
        }
    }

    /// <summary>Draws the weather round the eye, wherever the world lets it fall.</summary>
    /// <param name="world">Whose stored sky light decides where a drop exists.</param>
    public unsafe void Draw(
        in WeatherState weather,
        VoxelWorld world,
        Matrix4x4 viewProj,
        Vector3 camera,
        float elapsed,
        long seed,
        int viewportHeight)
    {
        Drawn = 0;
        if (!Available || !weather.Active || _shader is null || _rain is null || _snow is null) return;

        var count = WeatherField.Fill(
            _drops, weather, camera, elapsed % 10_000f, seed,
            (x, y, z) => WeatherField.SkyExposed(world, x, y, z));
        if (count == 0) return;

        _shader.Use();
        _shader.SetMatrix4("uViewProj", viewProj);
        _shader.SetVec3("uCamera", camera);
        _shader.SetFloat("uViewportHeight", viewportHeight);
        _shader.SetInt("uSnow", weather.Kind == Precipitation.Snow ? 1 : 0);
        _shader.SetFloat("uStrength", weather.Strength * 0.78f);
        _shader.SetInt("uRain", 0);
        _shader.SetInt("uSnowSprite", 1);
        _rain.Bind(TextureUnit.Texture0);
        _snow.Bind(TextureUnit.Texture1);

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (Vector3* p = _drops)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(count * 3 * sizeof(float)), p);

        _gl.Enable(EnableCap.ProgramPointSize);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        _gl.DrawArrays(PrimitiveType.Points, 0, (uint)count);
        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.ProgramPointSize);
        Drawn = count;
    }

    public void Dispose()
    {
        if (_vbo != 0) _gl.DeleteBuffer(_vbo);
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        _rain?.Dispose();
        _snow?.Dispose();
        _shader?.Dispose();
    }
}
