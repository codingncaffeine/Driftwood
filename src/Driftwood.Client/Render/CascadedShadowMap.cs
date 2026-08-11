using System.Numerics;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>Three stabilized depth cascades for the sun, sampled with PCF by the chunk shader.</summary>
public sealed class CascadedShadowMap : IDisposable
{
    public const int CascadeCount = 3;
    public const int Resolution = 1024;
    public const int EstimatedMiB = Resolution * Resolution * CascadeCount * 4 / (1024 * 1024);

    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in uint aPacked0;
        layout(location = 1) in uint aPacked1;
        uniform mat4 uLightViewProj;
        uniform vec3 uChunkOrigin;
        const float kPositionScale = 1.0 / 64.0;
        const float kPositionBias = 64.0;
        void main()
        {
            vec3 local = (vec3(
                float( aPacked0        & 4095u),
                float((aPacked0 >> 12) & 4095u),
                float( aPacked1        & 4095u)) - kPositionBias) * kPositionScale;
            gl_Position = uLightViewProj * vec4(uChunkOrigin + local, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        void main() { }
        """;

    private readonly GL _gl;
    private Shader? _shader;
    private uint _framebuffer;
    private uint _depthArray;
    private readonly Matrix4x4[] _matrices = new Matrix4x4[CascadeCount];
    private readonly float[] _splits = new float[CascadeCount];

    public bool Available { get; private set; }
    public string Failure { get; private set; } = "";
    public int RenderedCascades { get; private set; }
    public IReadOnlyList<Matrix4x4> Matrices => _matrices;
    public IReadOnlyList<float> Splits => _splits;
    public string Summary => Available
        ? $"{CascadeCount} cascades at {Resolution}², stabilized, 3x3 PCF"
        : $"disabled ({Failure})";

    public unsafe CascadedShadowMap(GL gl)
    {
        _gl = gl;
        try
        {
            _shader = new Shader(gl, VertexSource, FragmentSource);
            _depthArray = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2DArray, _depthArray);
            _gl.TexImage3D(TextureTarget.Texture2DArray, 0, InternalFormat.DepthComponent24,
                Resolution, Resolution, CascadeCount, 0,
                PixelFormat.DepthComponent, PixelType.UnsignedInt, null);
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareMode,
                (int)TextureCompareMode.CompareRefToTexture);
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareFunc,
                (int)DepthFunction.Lequal);

            _framebuffer = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            _gl.FramebufferTextureLayer(FramebufferTarget.Framebuffer,
                FramebufferAttachment.DepthAttachment, _depthArray, 0, 0);
            _gl.DrawBuffer(DrawBufferMode.None);
            _gl.ReadBuffer(ReadBufferMode.None);
            var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete)
                throw new InvalidOperationException($"shadow framebuffer incomplete: {status}");
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            Available = true;
        }
        catch (Exception error)
        {
            Failure = error.Message;
            Available = false;
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }
    }

    /// <summary>Renders all opaque chunk geometry into each cascade.</summary>
    public void Render(
        IEnumerable<ChunkMeshGpu> meshes,
        Vector3 camera,
        Vector3 forward,
        Vector3 sunDirection,
        float visibleDistance)
    {
        if (!Available || _shader is null || sunDirection.Y <= -0.08f) return;

        var far = Math.Clamp(visibleDistance, 96f, 420f);
        _splits[0] = MathF.Min(30f, far * 0.22f);
        _splits[1] = MathF.Min(92f, far * 0.52f);
        _splits[2] = far;

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        _gl.Viewport(0, 0, Resolution, Resolution);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Front);
        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(2.0f, 4.0f);
        _shader.Use();

        var previous = 0f;
        for (var cascade = 0; cascade < CascadeCount; cascade++)
        {
            var split = _splits[cascade];
            var radius = MathF.Max(18f, split * 0.72f);
            var centre = camera + forward * ((previous + split) * 0.38f);

            // Snap the cascade centre in world space at roughly one shadow texel. It is not a full
            // light-space stabilization, but it removes the high-frequency crawl from ordinary
            // walking without sacrificing the independently sized cascades.
            var step = radius * 2f / Resolution;
            centre = new Vector3(
                MathF.Round(centre.X / step) * step,
                MathF.Round(centre.Y / step) * step,
                MathF.Round(centre.Z / step) * step);

            var up = MathF.Abs(Vector3.Dot(sunDirection, Vector3.UnitY)) > 0.96f
                ? Vector3.UnitZ : Vector3.UnitY;
            var eye = centre + Vector3.Normalize(sunDirection) * radius * 2.4f;
            var view = Matrix4x4.CreateLookAt(eye, centre, up);
            var projection = Matrix4x4.CreateOrthographic(radius * 2f, radius * 2f, 0.1f, radius * 5.2f);
            _matrices[cascade] = view * projection;

            _gl.FramebufferTextureLayer(FramebufferTarget.Framebuffer,
                FramebufferAttachment.DepthAttachment, _depthArray, 0, cascade);
            _gl.Clear(ClearBufferMask.DepthBufferBit);
            _shader.SetMatrix4("uLightViewProj", _matrices[cascade]);
            foreach (var mesh in meshes)
            {
                _shader.SetVec3("uChunkOrigin", mesh.Origin);
                mesh.Draw();
            }
            RenderedCascades++;
            previous = split;
        }

        _gl.Disable(EnableCap.PolygonOffsetFill);
        _gl.CullFace(TriangleFace.Back);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Bind(TextureUnit unit)
    {
        if (!Available) return;
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.Texture2DArray, _depthArray);
    }

    public void Dispose()
    {
        if (_framebuffer != 0) _gl.DeleteFramebuffer(_framebuffer);
        if (_depthArray != 0) _gl.DeleteTexture(_depthArray);
        _shader?.Dispose();
    }
}
