using System.Numerics;
using Driftwood.Core.Entities;
using Driftwood.Core.Textures;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>
/// Draws the creatures: one buffer and one sheet per kind, one matrix per bone.
/// </summary>
/// <remarks>
/// <para>The same shape as <see cref="PlayerRenderer"/> and deliberately so — a creature is that
/// model with names instead of an enum. Every bone is drawn with its own matrix rather than the
/// vertices being re-transformed on the processor, because the per-bone hierarchy is the whole of
/// what makes an animation expressible, and a handful of draw calls per animal is nothing beside a
/// chunk pass.</para>
/// <para>⛔ <b>Nothing here is bundled.</b> The skeletons come off the user's own install and the
/// skins out of their own pack; a machine with neither draws no creatures and says so, which is a
/// state this has to survive rather than a fault.</para>
/// </remarks>
public sealed class CreatureRenderer : IDisposable
{
    private sealed class Kind
    {
        public required string Name { get; init; }
        public required CreatureMesh Mesh { get; init; }
        public required uint Vao { get; init; }
        public required uint Vbo { get; init; }
        public required uint Ebo { get; init; }

        /// <summary>Zero when the pack had no art for it — the shape draws untextured.</summary>
        public required uint Skin { get; init; }
    }

    private const int FloatsPerVertex = 8;   // position 3, normal 3, uv 2

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly Dictionary<string, Kind> _kinds = new(StringComparer.Ordinal);

    /// <summary>A line per kind for the startup log, and the reason when there is nothing.</summary>
    public string Summary { get; }

    public int Count => _kinds.Count;

    public CreatureRenderer(GL gl, IReadOnlyList<CreatureSet.Resolved> resolved, TexturePack? pack)
    {
        _gl = gl;
        _shader = new Shader(gl, EntityShaders.Vertex, EntityShaders.Fragment);

        var skinned = 0;
        var triangles = 0;
        var bare = 0;
        var ours = 0;

        foreach (var entry in resolved)
        {
            if (entry.Skeleton is not { } model) continue;

            // ⚠ Our own models are cut for their own declared net, so a pack's sheet dimensions are
            // only allowed to decide the scaling when the sheet is actually going to be worn.
            var mesh = CreatureMesh.Build(model, entry.SkinWidth, entry.SkinHeight);
            if (mesh.Indices.Length == 0) continue;

            var skin = 0u;

            // A pack's art first, at the shape it was painted.
            if (pack is not null && entry.SkinFrom.Length > 0)
            {
                foreach (var path in entry.Kind.Skins)
                {
                    var sheet = pack.TryLoadSheet(path, out _);
                    if (sheet is null) continue;

                    // ⚠ Only if it fits the net this skeleton is cut for. A sheet of the wrong shape
                    // puts every patch somewhere else on the animal, and ours — which fits by
                    // construction — is better than theirs worn inside out.
                    if (sheet.Width * model.SheetHeight != sheet.Height * model.SheetWidth
                        && sheet.Height * model.SheetWidth < model.SheetHeight * sheet.Width)
                    {
                        break;
                    }

                    skin = UploadSheet(gl, sheet);
                    skinned++;
                    break;
                }
            }

            // ⛔ Then ours, which is what ships. Painted in code the way our block tiles are, so a
            // machine with no pack at all still has animals rather than black cut-outs of them.
            if (skin == 0 && CreatureArt.Has(model.Name))
            {
                skin = UploadSheet(gl, CreatureArt.Paint(model));
                ours++;
            }

            // ⛔ NO SKIN MEANS NOT DRAWN, and the alternative was worse than nothing. A creature
            // with no texture bound samples whatever is on the unit, which is nothing, and comes out
            // as a matt black cut-out of an animal — pitch black, unlit, unmistakably a fault, and
            // in a picture with no pack loaded that is every animal in the world. A field with no
            // cows in it reads as "there are no cows yet"; a field of black cows reads as a broken
            // renderer, and it took a photograph to tell the two apart.
            if (skin == 0) { bare++; continue; }

            _kinds[entry.Kind.Name] = Upload(mesh, entry.Kind.Name, skin);
            triangles += mesh.TriangleCount;
        }

        Summary = _kinds.Count == 0
            ? bare > 0
                ? $"none drawn: {bare} have a skeleton and no art anywhere"
                : "no creatures: no skeletons were found"
            : $"{_kinds.Count} kinds, {triangles} triangles, {ours} painted here, {skinned} from the pack"
                + (bare > 0 ? $", {bare} skipped for want of art" : "");
    }

    private Kind Upload(CreatureMesh mesh, string name, uint skin)
    {
        var buffer = new float[mesh.Vertices.Length * FloatsPerVertex];
        for (var i = 0; i < mesh.Vertices.Length; i++)
        {
            var v = mesh.Vertices[i];
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

        return new Kind
        {
            Name = name,
            Mesh = mesh,
            Skin = skin,
            Vao = BuildArray(buffer, mesh.Indices, out var vbo, out var ebo),
            Vbo = vbo,
            Ebo = ebo,
        };
    }

    private unsafe uint BuildArray(float[] buffer, uint[] indices, out uint vbo, out uint ebo)
    {
        var vao = _gl.GenVertexArray();
        _gl.BindVertexArray(vao);

        vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* p = buffer)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(buffer.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        fixed (uint* p = indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);

        var stride = (uint)(FloatsPerVertex * sizeof(float));
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));

        _gl.BindVertexArray(0);
        return vao;
    }

    /// <summary>
    /// Uploads one creature's sheet at the shape it was painted.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Nearest filtering and no mip chain</b>, the same rule and the same reason as the player's
    /// skin: a sheet is an atlas of unrelated patches packed edge to edge with no gutter, so anything
    /// that averages neighbouring texels together drags an ear into a shoulder. Blocks avoid this
    /// with a texture array; a net cannot, because its patches are all different sizes.
    /// </remarks>
    private static unsafe uint UploadSheet(GL gl, Image sheet)
    {
        var handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, handle);

        fixed (byte* p = sheet.Pixels)
        {
            gl.TexImage2D(
                TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                (uint)sheet.Width, (uint)sheet.Height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        gl.BindTexture(TextureTarget.Texture2D, 0);
        return handle;
    }

    /// <summary>How big one kind is, in blocks, standing the way it will be drawn.</summary>
    /// <remarks>
    /// ⚠ Scaled by the kind's own draw scale, so the box a blow lands in is the animal on screen.
    /// The mesh keeps its authored units because its net is a contract — see
    /// <see cref="CreatureSet.DrawScaleFor"/>.
    /// </remarks>
    public bool TryMeasure(string kind, out Vector3 size)
    {
        if (!_kinds.TryGetValue(kind, out var found)) { size = Vector3.Zero; return false; }

        var (min, max) = found.Mesh.PosedBounds();
        size = (max - min) * CreatureSet.DrawScaleFor(kind);
        return true;
    }

    /// <summary>Draws one creature, stood at <paramref name="feet"/> and facing <paramref name="yawDegrees"/>.</summary>
    /// <param name="hurt">
    /// 0 for an animal going about its business, rising to 1 the instant it is struck. ⚠ Always
    /// passed rather than only set when it is non-zero: a uniform is program state and holds its last
    /// value, so a cow hit once would otherwise stay red for the rest of the session and take every
    /// other cow drawn after it with it.
    /// </param>
    /// <param name="tipped">
    /// 0 standing, 1 lying on its side. What a death looks like on the way down.
    /// </param>
    public unsafe void Draw(
        Matrix4x4 viewProj, Vector3 cameraPos, in SkyParams sky, EntityLight light,
        string kind, Vector3 feet, float yawDegrees, float hurt = 0f, float tipped = 0f,
        float scale = 1f)
    {
        if (!_kinds.TryGetValue(kind, out var found)) return;

        _shader.Use();
        _shader.SetMatrix4("uViewProj", viewProj);
        _shader.SetVec3("uCameraPos", cameraPos);
        _shader.SetVec3("uSunDir", sky.SunDirection);
        _shader.SetVec3("uSunColor", sky.SunColor);
        _shader.SetVec3("uSkyAmbient", sky.SkyAmbient);
        _shader.SetVec3("uGroundAmbient", sky.GroundAmbient);
        _shader.SetVec3("uNightFloor", sky.NightFloor);
        _shader.SetVec3("uFogColor", sky.FogColor);
        _shader.SetFloat("uFogStart", sky.FogStart);
        _shader.SetFloat("uFogEnd", sky.FogEnd);
        _shader.SetFloat("uTime", sky.Flicker);
        _shader.SetFloat("uSky", light.Sky);
        _shader.SetVec3("uBlockLight", light.Block);
        _shader.SetFloat("uHurt", Math.Clamp(hurt, 0f, 1f));
        _shader.SetInt("uSkin", 0);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, found.Skin);
        _gl.BindVertexArray(found.Vao);

        // ⚠ Model space faces −z and world yaw is measured from +x, so the extra quarter turn is what
        // reconciles the two. Exactly the line PlayerRig uses, and for the same reason: get it wrong
        // and every animal walks sideways.
        var yaw = float.DegreesToRadians(yawDegrees);
        var root = Matrix4x4.CreateRotationY(-(yaw + MathF.PI / 2f)) * Matrix4x4.CreateTranslation(feet);

        // A calf is its parents at small — same skeleton, same skin, scaled about its own feet
        // before anything else moves it. The kind's own draw scale folds in here too, so a bat
        // authored at the net's units is drawn at bat size and hit at bat size.
        scale *= CreatureSet.DrawScaleFor(kind);
        if (scale != 1f) root = Matrix4x4.CreateScale(scale) * root;

        // ⚠ Going over is a roll about the model's OWN forward axis, applied before the yaw — which
        // is what makes an animal fall on its side whichever way it happened to be facing. Rolled
        // afterwards it would tip about a fixed compass direction, so half the herd would fall
        // forwards. It stops a couple of degrees short of flat: a body at exactly ninety reads as a
        // model rotated rather than as something that has fallen down.
        if (tipped > 0f)
        {
            root = Matrix4x4.CreateRotationZ(float.DegreesToRadians(88f * Math.Clamp(tipped, 0f, 1f)))
                 * root;
        }

        var pose = found.Mesh.Pose(root);

        for (var i = 0; i < found.Mesh.Parts.Length; i++)
        {
            var part = found.Mesh.Parts[i];
            if (part.Count == 0) continue;

            _shader.SetMatrix4("uModel", pose[i]);
            _gl.DrawElements(
                PrimitiveType.Triangles, (uint)part.Count, DrawElementsType.UnsignedInt,
                (void*)(part.First * sizeof(uint)));
        }

        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        foreach (var kind in _kinds.Values)
        {
            _gl.DeleteBuffer(kind.Vbo);
            _gl.DeleteBuffer(kind.Ebo);
            _gl.DeleteVertexArray(kind.Vao);
            if (kind.Skin != 0) _gl.DeleteTexture(kind.Skin);
        }

        _kinds.Clear();
        _shader.Dispose();
    }
}
