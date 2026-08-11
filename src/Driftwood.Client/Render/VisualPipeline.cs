using System.Numerics;
using Driftwood.Core.Settings;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>
/// HDR scene target and P9 screen-space passes: SSAO, bloom, god rays and temporal antialiasing.
/// </summary>
/// <remarks>
/// The world is rendered once into a floating-point target. Its opaque colour/depth are copied
/// before water, giving refraction and reflection a stable image that can never feed back into
/// itself. At the end of the world pass a small bloom chain and one composite pass write a clamped
/// temporal history, which is then presented before the HUD draws on the default framebuffer.
/// Every attachment is checked. A driver that cannot supply the targets simply keeps the original
/// direct-to-window path; no visual option is allowed to make the game fail to open.
/// </remarks>
public sealed class VisualPipeline : IDisposable
{
    private const string FullscreenVertex = """
        #version 330 core
        layout(location = 0) in vec2 aClip;
        out vec2 vUv;
        void main()
        {
            vUv = aClip * 0.5 + 0.5;
            gl_Position = vec4(aClip, 0.0, 1.0);
        }
        """;

    private const string BloomExtractFragment = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uScene;
        out vec4 FragColor;
        void main()
        {
            vec3 color = texture(uScene, vUv).rgb;
            float brightness = max(max(color.r, color.g), color.b);
            float knee = smoothstep(0.72, 1.65, brightness);
            FragColor = vec4(color * knee, 1.0);
        }
        """;

    private const string BloomBlurFragment = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uImage;
        uniform vec2 uDirection;
        out vec4 FragColor;
        void main()
        {
            vec3 color = texture(uImage, vUv).rgb * 0.227027;
            color += texture(uImage, vUv + uDirection * 1.384615).rgb * 0.316216;
            color += texture(uImage, vUv - uDirection * 1.384615).rgb * 0.316216;
            color += texture(uImage, vUv + uDirection * 3.230769).rgb * 0.070270;
            color += texture(uImage, vUv - uDirection * 3.230769).rgb * 0.070270;
            FragColor = vec4(color, 1.0);
        }
        """;

    private const string CompositeFragment = """
        #version 330 core
        in vec2 vUv;

        uniform sampler2D uScene;
        uniform sampler2D uDepth;
        uniform sampler2D uBloom;
        uniform sampler2D uHistory;
        uniform vec2 uTexel;
        uniform vec2 uSunUv;
        uniform vec3 uSunColor;
        uniform mat4 uInvViewProj;
        uniform mat4 uPrevViewProj;
        uniform float uNear;
        uniform float uFar;
        uniform int uSsao;
        uniform int uBloomEnabled;
        uniform int uGodRays;
        uniform int uTaa;
        uniform int uHistoryValid;

        out vec4 FragColor;

        float linearDepth(float depth)
        {
            float z = depth * 2.0 - 1.0;
            return (2.0 * uNear * uFar) / max(uFar + uNear - z * (uFar - uNear), 0.0001);
        }

        vec3 mapped(vec3 hdr)
        {
            if (uBloomEnabled == 0)
                return pow(clamp(hdr, vec3(0.0), vec3(1.0)), vec3(1.0 / 2.2));
            // A fixed photographic exposure keeps night intact while rolling bright lamps and sun
            // highlights into display range. The curve is monotonic and cannot clip a color channel.
            vec3 exposed = vec3(1.0) - exp(-hdr * 1.12);
            return pow(max(exposed, vec3(0.0)), vec3(1.0 / 2.2));
        }

        float ambientOcclusion(float centreDepth)
        {
            if (uSsao == 0 || centreDepth >= 0.99999) return 1.0;
            float centre = linearDepth(centreDepth);
            float hidden = 0.0;
            const vec2 directions[8] = vec2[8](
                vec2(1,0), vec2(-1,0), vec2(0,1), vec2(0,-1),
                vec2(0.707,0.707), vec2(-0.707,0.707),
                vec2(0.707,-0.707), vec2(-0.707,-0.707));
            for (int i = 0; i < 8; ++i)
            {
                float sampleDepth = linearDepth(texture(uDepth, vUv + directions[i] * uTexel * 4.0).r);
                float delta = centre - sampleDepth;
                hidden += smoothstep(0.08, 1.8, delta) * (1.0 - smoothstep(4.0, 12.0, abs(delta)));
            }
            return 1.0 - hidden * 0.055;
        }

        vec3 currentAt(vec2 uv)
        {
            vec3 hdr = texture(uScene, uv).rgb;
            if (uBloomEnabled != 0) hdr += texture(uBloom, uv).rgb * 0.42;
            return mapped(hdr);
        }

        void main()
        {
            float depth = texture(uDepth, vUv).r;
            vec3 hdr = texture(uScene, vUv).rgb;
            hdr *= ambientOcclusion(depth);
            if (uBloomEnabled != 0) hdr += texture(uBloom, vUv).rgb * 0.42;

            // Radial sky visibility toward the projected sun. Terrain interrupts the samples, so
            // the shafts appear round a ridge or canopy and vanish when the sun is fully covered.
            if (uGodRays != 0 && uSunUv.x > -0.2 && uSunUv.x < 1.2
                              && uSunUv.y > -0.2 && uSunUv.y < 1.2)
            {
                vec2 stepUv = (uSunUv - vUv) / 20.0;
                vec2 at = vUv;
                float visibility = 0.0;
                float decay = 1.0;
                for (int i = 0; i < 20; ++i)
                {
                    at += stepUv;
                    float sky = step(0.9997, texture(uDepth, clamp(at, vec2(0.0), vec2(1.0))).r);
                    visibility += sky * decay;
                    decay *= 0.94;
                }
                float centre = 1.0 - smoothstep(0.0, 0.9, distance(vUv, uSunUv));
                hdr += uSunColor * visibility * centre * 0.010;
            }

            vec3 color = mapped(hdr);
            if (uTaa != 0 && uHistoryValid != 0)
            {
                vec4 clip = vec4(vUv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
                vec4 world = uInvViewProj * clip;
                world /= max(abs(world.w), 0.00001);
                vec4 previous = uPrevViewProj * vec4(world.xyz, 1.0);
                vec2 historyUv = previous.xy / max(abs(previous.w), 0.00001) * 0.5 + 0.5;

                if (all(greaterThanEqual(historyUv, vec2(0.0)))
                    && all(lessThanEqual(historyUv, vec2(1.0))))
                {
                    vec3 lo = color;
                    vec3 hi = color;
                    for (int y = -1; y <= 1; ++y)
                    for (int x = -1; x <= 1; ++x)
                    {
                        vec3 neighbour = currentAt(vUv + vec2(x, y) * uTexel);
                        lo = min(lo, neighbour);
                        hi = max(hi, neighbour);
                    }
                    vec3 history = clamp(texture(uHistory, historyUv).rgb, lo, hi);
                    float motion = length(historyUv - vUv);
                    float historyWeight = mix(0.90, 0.18, smoothstep(0.002, 0.04, motion));
                    color = mix(color, history, historyWeight);
                }
            }
            FragColor = vec4(color, 1.0);
        }
        """;

    private sealed class Target
    {
        public uint Framebuffer;
        public uint Color;
        public uint Depth;
    }

    private readonly GL _gl;
    private Shader? _extract;
    private Shader? _blur;
    private Shader? _composite;
    private uint _vao;
    private uint _vbo;
    private Target _scene = new();
    private Target _opaque = new();
    private readonly Target[] _bloom = [new(), new()];
    private readonly Target[] _history = [new(), new()];
    private int _width;
    private int _height;
    private int _historyRead;
    private bool _historyValid;
    private Matrix4x4 _previousViewProj = Matrix4x4.Identity;
    private int _jitterFrame;

    public bool Available { get; private set; }
    public bool Active { get; private set; }
    public string Failure { get; private set; } = "";
    public int CompletedFrames { get; private set; }
    public int OpaqueCaptures { get; private set; }
    public int Width => _width;
    public int Height => _height;
    public bool HistoryReady => _historyValid;
    /// <summary>Conservative color/depth/history/bloom attachment footprint at the current size.</summary>
    public double EstimatedMiB => _width * (double)_height * 44.0 / (1024.0 * 1024.0);
    public string Summary => Available
        ? $"HDR {_width}x{_height}, half-resolution bloom, depth SSAO/god-rays, clamped TAA history"
        : $"direct framebuffer fallback ({Failure})";

    public VisualPipeline(GL gl)
    {
        _gl = gl;
        try
        {
            _extract = new Shader(gl, FullscreenVertex, BloomExtractFragment);
            _blur = new Shader(gl, FullscreenVertex, BloomBlurFragment);
            _composite = new Shader(gl, FullscreenVertex, CompositeFragment);
            BuildTriangle();
            Available = true;
        }
        catch (Exception error)
        {
            Failure = error.Message;
            Available = false;
        }
    }

    private unsafe void BuildTriangle()
    {
        float[] corners = [-1f, -1f, 3f, -1f, -1f, 3f];
        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);
        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = corners)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(corners.Length * sizeof(float)), p,
                BufferUsageARB.StaticDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        _gl.BindVertexArray(0);
    }

    /// <summary>Binds the scene target, allocating it on first use or after a resize.</summary>
    public bool BeginFrame(int width, int height)
    {
        Active = false;
        if (!Available || width <= 0 || height <= 0) return false;
        if (width != _width || height != _height)
        {
            if (!Resize(width, height)) return false;
        }
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _scene.Framebuffer);
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        Active = true;
        return true;
    }

    /// <summary>Copies opaque color and depth for refraction, SSR and later depth effects.</summary>
    public void CaptureOpaque()
    {
        if (!Active) return;
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _scene.Framebuffer);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _opaque.Framebuffer);
        _gl.BlitFramebuffer(0, 0, _width, _height, 0, 0, _width, _height,
            ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit,
            BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _scene.Framebuffer);
        OpaqueCaptures++;
    }

    public void BindOpaque(TextureUnit colorUnit, TextureUnit depthUnit)
    {
        if (!Active) return;
        _gl.ActiveTexture(colorUnit);
        _gl.BindTexture(TextureTarget.Texture2D, _opaque.Color);
        _gl.ActiveTexture(depthUnit);
        _gl.BindTexture(TextureTarget.Texture2D, _opaque.Depth);
    }

    /// <summary>Resolves the world into the default framebuffer and leaves it ready for the HUD.</summary>
    public void EndFrame(
        Matrix4x4 currentViewProj,
        Vector2 sunUv,
        Vector3 sunColor,
        float nearPlane,
        float farPlane,
        GameSettings settings)
    {
        if (!Active || _extract is null || _blur is null || _composite is null) return;

        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(false);
        _gl.BindVertexArray(_vao);

        if (settings.Bloom)
        {
            // Bright-pass at half resolution.
            _gl.Viewport(0, 0, (uint)Math.Max(1, _width / 2), (uint)Math.Max(1, _height / 2));
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _bloom[0].Framebuffer);
            _extract.Use();
            _extract.SetInt("uScene", 0);
            BindTexture(_scene.Color, TextureUnit.Texture0);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            // Two separable blur pairs are enough for a broad, restrained halo at half resolution.
            _blur.Use();
            _blur.SetInt("uImage", 0);
            for (var pass = 0; pass < 4; pass++)
            {
                var source = _bloom[pass & 1].Color;
                var destination = _bloom[1 - (pass & 1)];
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, destination.Framebuffer);
                BindTexture(source, TextureUnit.Texture0);
                _blur.SetVec2("uDirection", pass % 2 == 0
                    ? new Vector2(2f / Math.Max(1, _width), 0f)
                    : new Vector2(0f, 2f / Math.Max(1, _height)));
                _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            }
        }

        var write = 1 - _historyRead;
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _history[write].Framebuffer);
        _composite.Use();
        _composite.SetInt("uScene", 0);
        _composite.SetInt("uDepth", 1);
        _composite.SetInt("uBloom", 2);
        _composite.SetInt("uHistory", 3);
        BindTexture(_scene.Color, TextureUnit.Texture0);
        BindTexture(_opaque.Depth, TextureUnit.Texture1);
        BindTexture(_bloom[0].Color, TextureUnit.Texture2);
        BindTexture(_history[_historyRead].Color, TextureUnit.Texture3);
        _composite.SetVec2("uTexel", new Vector2(1f / _width, 1f / _height));
        _composite.SetVec2("uSunUv", sunUv);
        _composite.SetVec3("uSunColor", sunColor);
        _composite.SetFloat("uNear", nearPlane);
        _composite.SetFloat("uFar", farPlane);
        _composite.SetInt("uSsao", settings.AmbientOcclusion ? 1 : 0);
        _composite.SetInt("uBloomEnabled", settings.Bloom ? 1 : 0);
        _composite.SetInt("uGodRays", settings.GodRays ? 1 : 0);
        _composite.SetInt("uTaa", settings.TemporalAntialiasing ? 1 : 0);
        _composite.SetInt("uHistoryValid", _historyValid ? 1 : 0);
        if (!Matrix4x4.Invert(currentViewProj, out var inverse)) inverse = Matrix4x4.Identity;
        _composite.SetMatrix4("uInvViewProj", inverse);
        _composite.SetMatrix4("uPrevViewProj", _previousViewProj);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        // Present without another shader so the exact history image is what reaches the window.
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _history[write].Framebuffer);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
        _gl.BlitFramebuffer(0, 0, _width, _height, 0, 0, _width, _height,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        _historyRead = write;
        _historyValid = true;
        _previousViewProj = currentViewProj;
        CompletedFrames++;
        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
        Active = false;
    }

    /// <summary>Reads one resolved world pixel for the framebuffer release gate.</summary>
    public unsafe (byte R, byte G, byte B, byte A) ReadResolvedPixel(int x, int y)
    {
        if (!_historyValid || _width <= 0 || _height <= 0) return default;
        x = Math.Clamp(x, 0, _width - 1);
        y = Math.Clamp(y, 0, _height - 1);
        Span<byte> pixel = stackalloc byte[4];
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _history[_historyRead].Framebuffer);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        fixed (byte* p = pixel)
            _gl.ReadPixels(x, y, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        return (pixel[0], pixel[1], pixel[2], pixel[3]);
    }

    /// <summary>Sub-pixel Halton jitter for the projection used by a TAA frame.</summary>
    public Matrix4x4 Jitter(Matrix4x4 projection, int width, int height, bool enabled)
    {
        if (!Available || !enabled || width <= 0 || height <= 0) return projection;
        var sample = _jitterFrame++ & 7;
        var x = (Halton(sample + 1, 2) - 0.5f) * 2f / width;
        var y = (Halton(sample + 1, 3) - 0.5f) * 2f / height;
        projection.M31 += projection.M34 * x;
        projection.M32 += projection.M34 * y;
        return projection;
    }

    public void ResetHistory()
    {
        _historyValid = false;
        _jitterFrame = 0;
    }

    private static float Halton(int index, int radix)
    {
        var fraction = 1f;
        var result = 0f;
        while (index > 0)
        {
            fraction /= radix;
            result += fraction * (index % radix);
            index /= radix;
        }
        return result;
    }

    private bool Resize(int width, int height)
    {
        DeleteTargets();
        _width = width;
        _height = height;
        try
        {
            _scene = CreateTarget(width, height, depth: true, halfFloat: true);
            _opaque = CreateTarget(width, height, depth: true, halfFloat: true);
            _bloom[0] = CreateTarget(Math.Max(1, width / 2), Math.Max(1, height / 2), false, true);
            _bloom[1] = CreateTarget(Math.Max(1, width / 2), Math.Max(1, height / 2), false, true);
            _history[0] = CreateTarget(width, height, false, true);
            _history[1] = CreateTarget(width, height, false, true);
            ResetHistory();
            return true;
        }
        catch (Exception error)
        {
            Failure = error.Message;
            Available = false;
            Active = false;
            DeleteTargets();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.Viewport(0, 0, (uint)width, (uint)height);
            return false;
        }
    }

    private unsafe Target CreateTarget(int width, int height, bool depth, bool halfFloat)
    {
        var target = new Target
        {
            Framebuffer = _gl.GenFramebuffer(),
            Color = _gl.GenTexture(),
        };
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, target.Framebuffer);
        _gl.BindTexture(TextureTarget.Texture2D, target.Color);
        _gl.TexImage2D(TextureTarget.Texture2D, 0,
            halfFloat ? InternalFormat.Rgba16f : InternalFormat.Rgba8,
            (uint)width, (uint)height, 0, PixelFormat.Rgba,
            halfFloat ? PixelType.HalfFloat : PixelType.UnsignedByte, null);
        TextureParameters(linear: true);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, target.Color, 0);

        if (depth)
        {
            target.Depth = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, target.Depth);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24,
                (uint)width, (uint)height, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, null);
            TextureParameters(linear: false);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                TextureTarget.Texture2D, target.Depth, 0);
        }

        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException($"framebuffer incomplete: {status}");
        return target;
    }

    private void TextureParameters(bool linear)
    {
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            linear ? (int)TextureMinFilter.Linear : (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            linear ? (int)TextureMagFilter.Linear : (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }

    private void BindTexture(uint texture, TextureUnit unit)
    {
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.Texture2D, texture);
    }

    private void DeleteTargets()
    {
        Delete(_scene);
        Delete(_opaque);
        foreach (var target in _bloom) Delete(target);
        foreach (var target in _history) Delete(target);
        _scene = new Target();
        _opaque = new Target();
        _bloom[0] = new Target();
        _bloom[1] = new Target();
        _history[0] = new Target();
        _history[1] = new Target();
    }

    private void Delete(Target target)
    {
        if (target.Depth != 0) _gl.DeleteTexture(target.Depth);
        if (target.Color != 0) _gl.DeleteTexture(target.Color);
        if (target.Framebuffer != 0) _gl.DeleteFramebuffer(target.Framebuffer);
    }

    public void Dispose()
    {
        DeleteTargets();
        if (_vbo != 0) _gl.DeleteBuffer(_vbo);
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        _extract?.Dispose();
        _blur?.Dispose();
        _composite?.Dispose();
    }
}
