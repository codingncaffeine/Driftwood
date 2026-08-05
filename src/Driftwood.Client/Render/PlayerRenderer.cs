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

    /// <summary>Where the torso turns, in model units. Everything above the waist hangs off it.</summary>
    private static readonly Vector3 BodyPivot = new(0f, 12f, 0f);

    // ── The first-person view model. Every number a swing looks wrong by is in this block. ──
    //
    // What is meant to be on screen is the thing in the hand, and only part of it — or, holding
    // nothing, the hand alone. No forearm, no shoulder. So the arm is aimed nearly straight away
    // from the eye and its shoulder is put *below the bottom edge of the frame*: at that angle its
    // whole length is foreshortened into almost nothing and the only part still inside the picture
    // is the fist at the far end, low and to the right.
    //
    // The arm hangs down its own −Y, so pitch is the angle that decides all of this. Near a right
    // angle it points straight away; under that it swings down out of frame, over it the whole limb
    // rises back into view broadside on. The rest yaw turns the hand in toward the crosshair, so
    // what is held leans at what it is about to hit.
    //
    // The geometry these are placed against: the view is 70 degrees vertical, so the frame's half
    // height at distance z is 0.70·z. The shoulder sits at z 0.58 where that is 0.41 — well above
    // its own −0.76 — and the fist ends up at z 1.26 where it is 0.88, which puts the fist just
    // inside the bottom edge. Dial the offset and this is the arithmetic that decides what shows.
    private const float RestPitch = 1.58f;     // radians away from straight down: aimed down the barrel
    private const float RestYaw = 0.26f;       // and in toward the middle of the screen
    private const float RestRoll = -0.18f;
    private static readonly Vector3 RestOffset = new(0.58f, -0.76f, -0.58f);

    // The swing is a sword's diagonal slice: it cocks up to the top right and cuts down across to
    // the bottom left, then recovers.
    //
    // What makes it a slice rather than a chop or a sideways slide is that the rise and the
    // crossing are the *same* motion — one number drives both, so the tool is always as high as it
    // is far right. Pull them apart and it reads as two movements happening at once.
    //
    // Each of the three carries part of it. Pitch takes the tool up and brings it down, because
    // over a right angle the arm points forward and up and under it forward and down. Yaw turns
    // what is held to face the way it is travelling. The shift moves the whole thing across the
    // frame, and its X and Y are deliberately in proportion: that ratio *is* the angle of the cut.
    // Sized to read rather than to perform. The direction is the thing being said; a cut that
    // throws the tool right across the frame says it twice and gets tiring by the tenth block.
    private const float WindUpShare = 0.32f;   // of the swing spent cocking up to the right
    private const float DriveShare = 0.22f;    // of it spent cutting down across; the rest recovers
    private const float SwingCock = 0.34f;     // radians raised above rest at the top right
    private const float SwingFollow = 0.42f;   // radians below rest at the bottom left
    private const float SwingCross = 0.52f;    // how far it turns across the screen
    private const float SwingTwist = 0.36f;    // how far the edge rolls over through the cut
    private static readonly Vector3 SwingShift = new(-0.19f, -0.14f, -0.10f);

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

        var unit = PlayerModel.Unit;

        // Model space has −Z forward; world yaw is measured from +X. The extra quarter turn is what
        // reconciles the two, and doing it here means the animator never has to know about it.
        var yaw = float.DegreesToRadians(pose.BodyYawDegrees);
        var root = Matrix4x4.CreateRotationY(-(yaw + MathF.PI / 2f)) * Matrix4x4.CreateTranslation(feet);

        var body = Matrix4x4.CreateRotationX(pose.BodyPitch)
                 * Matrix4x4.CreateTranslation((BodyPivot - new Vector3(0f, pose.BodyDropUnits, 0f)) * unit)
                 * root;

        // Legs step back and up under a crouching torso. +Z is behind the model.
        var legShift = new Vector3(0f, pose.LegLiftUnits, pose.LegShiftUnits) * unit;

        foreach (var draw in _draws)
        {
            var box = draw.Box;

            // Head and arms hang off the torso, so leaning into a crouch carries them without the
            // pose having to describe the same lean three more times.
            var model = box.Part switch
            {
                PlayerPart.Body => body,
                PlayerPart.Head => Rotate(pose.Head) * Offset(box.Pivot - BodyPivot, unit) * body,
                PlayerPart.RightArm => Rotate(pose.RightArm) * Offset(box.Pivot - BodyPivot, unit) * body,
                PlayerPart.LeftArm => Rotate(pose.LeftArm) * Offset(box.Pivot - BodyPivot, unit) * body,
                PlayerPart.RightLeg => Rotate(pose.RightLeg) * Shift(box.Pivot, unit, legShift) * root,
                _ => Rotate(pose.LeftLeg) * Shift(box.Pivot, unit, legShift) * root,
            };

            Draw(draw, model);
        }

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

        var model = ArmTransform(swinging ? swingProgress : 0f);

        foreach (var draw in _draws)
        {
            if (draw.Box.Part != PlayerPart.RightArm) continue;
            Draw(draw, model);
        }

        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Where the first-person arm is, this far through a swing. In the camera's own space.
    /// </summary>
    /// <remarks>
    /// Public and static because the thing in the hand has to travel with the hand. Anything drawn
    /// separately and animated with its own copy of these numbers drifts out of the fist the first
    /// time one of them is dialled — which is exactly the sort of thing that is only ever noticed
    /// mid-swing, from inside the game.
    /// </remarks>
    public static Matrix4x4 ArmTransform(float t)
    {
        var swing = SwingCurve(t);

        // Raised at −1, driven down past rest at +1. Increasing pitch lifts the hand, because the
        // arm hangs down its own −Y, so cocking back is a bigger angle and the strike is a smaller
        // one — which is the opposite of what it reads as on the page and the reason it is written
        // out here rather than inlined.
        var pitch = RestPitch - swing * (swing < 0f ? SwingCock : SwingFollow);

        return Matrix4x4.CreateRotationZ(RestRoll + swing * SwingTwist)
             * Matrix4x4.CreateRotationX(pitch)
             * Matrix4x4.CreateRotationY(RestYaw + swing * SwingCross)
             * Matrix4x4.CreateTranslation(RestOffset + SwingShift * swing);
    }

    /// <summary>
    /// Where the held thing is: at the end of the arm, turned to sit in the fist.
    /// </summary>
    /// <remarks>
    /// The tilt is what makes a tool read as gripped rather than as balanced on a wrist. A pickaxe
    /// held square to the arm shows the viewer its edge, which at this size is a line.
    /// </remarks>
    public static Matrix4x4 HeldTransform(float t, float size, bool flat)
    {
        // Down the arm to the fist, in the arm's own space. The arm is twelve model units long and
        // the grip is near its end rather than at it.
        var inHand = Matrix4x4.CreateTranslation(0f, -10.4f * PlayerModel.Unit, 0f);

        var sit = flat
            ? Matrix4x4.CreateRotationZ(-0.62f) * Matrix4x4.CreateRotationY(0.42f)
            : Matrix4x4.CreateRotationY(0.55f) * Matrix4x4.CreateRotationX(0.28f);

        return Matrix4x4.CreateScale(size) * sit * inHand * ArmTransform(t);
    }

    /// <summary>
    /// The swing, as one number: −1 fully cocked, 0 at rest, +1 fully followed through.
    /// </summary>
    /// <remarks>
    /// Three phases rather than a sine, because a sine is symmetric and a blow is not. It goes back
    /// slowly, comes down fast, and recovers over what is left — and it starts and ends at exactly
    /// rest, so a held button that swings again immediately does not snap.
    /// </remarks>
    private static float SwingCurve(float t)
    {
        if (t <= 0f || t >= 1f) return 0f;

        if (t < WindUpShare) return -Smooth(t / WindUpShare);

        if (t < WindUpShare + DriveShare)
            return -1f + 2f * Smooth((t - WindUpShare) / DriveShare);

        return 1f - Smooth((t - WindUpShare - DriveShare) / (1f - WindUpShare - DriveShare));
    }

    private static float Smooth(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    private unsafe void Draw(in BoxDraw draw, Matrix4x4 model)
    {
        _shader.SetMatrix4("uModel", model);
        _gl.DrawElements(
            PrimitiveType.Triangles, (uint)draw.Count, DrawElementsType.UnsignedInt,
            (void*)(draw.First * sizeof(uint)));
    }

    private static Matrix4x4 Rotate(in LimbPose pose) =>
        Matrix4x4.CreateRotationZ(pose.Roll)
        * Matrix4x4.CreateRotationY(pose.Yaw)
        * Matrix4x4.CreateRotationX(pose.Pitch);

    private static Matrix4x4 Offset(Vector3 unitsFromParent, float unit) =>
        Matrix4x4.CreateTranslation(unitsFromParent * unit);

    private static Matrix4x4 Shift(Vector3 units, float unit, Vector3 extra) =>
        Matrix4x4.CreateTranslation(units * unit + extra);

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteTexture(_skin);
        _shader.Dispose();
    }
}
