using System.Numerics;
using Driftwood.Core.Entities;
using Driftwood.Core.Textures;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>Sky and fog terms, shared so the entity pass shades the way the chunk pass does.</summary>
public readonly record struct SkyParams(
    Vector3 SunDirection,
    Vector3 SunColor,
    Vector3 SkyAmbient,
    Vector3 GroundAmbient,
    Vector3 NightFloor,
    Vector3 FogColor,
    float FogStart,
    float FogEnd);

/// <summary>Baked light in the cell a model is standing in.</summary>
public readonly record struct EntityLight(float Sky, Vector3 Block);

/// <summary>
/// Draws the player: the whole model in third person, the right arm alone in first.
/// </summary>
/// <remarks>
/// One vertex buffer holds every box of the model, each with its own index range, and every box is
/// drawn with its own matrix. Twelve draw calls for one character is nothing next to a chunk pass,
/// and the alternative — re-transforming the vertices on the CPU each frame to get one call — gives
/// up the per-part hierarchy that makes the animation expressible at all.
/// </remarks>
public sealed class PlayerRenderer : IDisposable
{
    private readonly record struct BoxDraw(int First, int Count, ModelBox Box);

    private const int FloatsPerVertex = 8;   // position 3, normal 3, uv 2

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private readonly uint _skin;
    private readonly BoxDraw[] _draws;

    public ArmStyle Arms { get; }

    public unsafe PlayerRenderer(GL gl, PlayerSkinData skin)
    {
        _gl = gl;
        Arms = skin.Arms;
        _shader = new Shader(gl, EntityShaders.Vertex, EntityShaders.Fragment);

        var vertices = new List<ModelVertex>();
        var indices = new List<uint>();
        var draws = new List<BoxDraw>();

        foreach (var box in PlayerModel.Build(skin.Arms, skin.Legacy))
        {
            var first = indices.Count;
            PlayerModel.Emit(box, vertices, indices);
            draws.Add(new BoxDraw(first, indices.Count - first, box));
        }

        _draws = [.. draws];

        var buffer = new float[vertices.Count * FloatsPerVertex];
        for (var i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i];
            var o = i * FloatsPerVertex;
            buffer[o] = v.Position.X;
            buffer[o + 1] = v.Position.Y;
            buffer[o + 2] = v.Position.Z;
            buffer[o + 3] = v.Normal.X;
            buffer[o + 4] = v.Normal.Y;
            buffer[o + 5] = v.Normal.Z;
            buffer[o + 6] = v.Uv.X;
            buffer[o + 7] = v.Uv.Y;
        }

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = buffer)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(buffer.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        var packed = indices.ToArray();
        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* p = packed)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(packed.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);

        var stride = (uint)(FloatsPerVertex * sizeof(float));
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));

        _gl.BindVertexArray(0);

        _skin = UploadSkin(gl, skin);
    }

    /// <summary>
    /// Uploads the sheet. Nearest filtering and no mipmaps.
    /// </summary>
    /// <remarks>
    /// A skin sheet is an atlas of unrelated patches packed edge to edge, so a mip chain averages a
    /// sleeve into a trouser leg and a hat into the back of a head. Blocks solve that with a texture
    /// array; a skin cannot, because the patches are different sizes and the format is fixed. There
    /// is exactly one model on screen and it is usually a few blocks away, so the aliasing this
    /// leaves is a much smaller price than the bleeding it avoids.
    /// </remarks>
    private static unsafe uint UploadSkin(GL gl, PlayerSkinData skin)
    {
        var handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, handle);

        fixed (byte* p = skin.Pixels)
        {
            gl.TexImage2D(
                TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                (uint)skin.Size, (uint)skin.Size, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        gl.BindTexture(TextureTarget.Texture2D, 0);
        return handle;
    }

    private void BeginPass(Matrix4x4 viewProj, Vector3 cameraPos, Vector3 sunDirection, in SkyParams sky)
    {
        _shader.Use();
        _shader.SetMatrix4("uViewProj", viewProj);
        _shader.SetVec3("uCameraPos", cameraPos);
        _shader.SetVec3("uSunDir", sunDirection);
        _shader.SetVec3("uSunColor", sky.SunColor);
        _shader.SetVec3("uSkyAmbient", sky.SkyAmbient);
        _shader.SetVec3("uGroundAmbient", sky.GroundAmbient);
        _shader.SetVec3("uNightFloor", sky.NightFloor);
        _shader.SetVec3("uFogColor", sky.FogColor);
        _shader.SetInt("uSkin", 0);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _skin);
        _gl.BindVertexArray(_vao);
    }

    /// <summary>Draws the whole model, stood at <paramref name="feet"/>.</summary>
    public void DrawWorld(
        Matrix4x4 viewProj, Vector3 cameraPos, in SkyParams sky, EntityLight light,
        Vector3 feet, in PlayerPose pose)
    {
        BeginPass(viewProj, cameraPos, sky.SunDirection, sky);
        _shader.SetFloat("uFogStart", sky.FogStart);
        _shader.SetFloat("uFogEnd", sky.FogEnd);
        _shader.SetFloat("uSky", light.Sky);
        _shader.SetVec3("uBlockLight", light.Block);

        var rig = HeldGrip.Rig(feet, pose);

        foreach (var draw in _draws)
            Draw(draw, rig.Part(draw.Box.Part, draw.Box.Pivot, pose));

        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Draws the right arm in the camera's own space, over whatever the world pass left behind.
    /// </summary>
    /// <remarks>
    /// The whole point of the exercise: in first person there is no model to watch, so without this
    /// a block breaks and nothing on screen caused it. Geometry is placed directly in view space and
    /// only the projection is applied, which keeps the arm at a fixed spot on screen however the
    /// camera moves.
    /// </remarks>
    public void DrawViewModel(
        Matrix4x4 projection, Vector3 sunInViewSpace, in SkyParams sky, EntityLight light,
        bool swinging, float swingProgress)
    {
        BeginPass(projection, Vector3.Zero, sunInViewSpace, sky);

        // No fog on the view model: it is a few centimetres from the eye, and distance fog computed
        // against a camera at the origin would otherwise put the horizon's colour on the player's
        // own hand.
        _shader.SetFloat("uFogStart", 1e9f);
        _shader.SetFloat("uFogEnd", 2e9f);
        _shader.SetFloat("uSky", light.Sky);
        _shader.SetVec3("uBlockLight", light.Block);

        var model = HeldGrip.ArmTransform(swinging ? swingProgress : 0f);

        foreach (var draw in _draws)
        {
            if (draw.Box.Part != PlayerPart.RightArm) continue;
            Draw(draw, model);
        }

        _gl.BindVertexArray(0);
    }

    /// <summary>Where what this player is holding sits, in the camera's own space.</summary>
    /// <remarks>
    /// The arm width is the renderer's to know — it comes off the skin sheet — and the grip is
    /// <see cref="HeldGrip"/>'s, which is in Core so the audit can run the whole chain without a
    /// window. This is the one line that joins them.
    /// </remarks>
    public Matrix4x4 HeldTransform(float t, bool flat, Vector3 hold) =>
        HeldGrip.InView(t, flat, hold, Arms);

    /// <summary>And where it sits in the model's own fist, in the world.</summary>
    public Matrix4x4 HeldWorldTransform(Vector3 feet, in PlayerPose pose, bool flat, Vector3 hold) =>
        HeldGrip.InWorld(feet, pose, flat, hold, Arms);

    private unsafe void Draw(in BoxDraw draw, Matrix4x4 model)
    {
        _shader.SetMatrix4("uModel", model);
        _gl.DrawElements(
            PrimitiveType.Triangles, (uint)draw.Count, DrawElementsType.UnsignedInt,
            (void*)(draw.First * sizeof(uint)));
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteTexture(_skin);
        _shader.Dispose();
    }
}
