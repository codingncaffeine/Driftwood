using System.Numerics;
using Driftwood.Core.Entities;
using Driftwood.Core.Items;

namespace Driftwood.Client.Render;

/// <summary>One visible face after the shared player-preview camera has projected it.</summary>
internal readonly record struct ProjectedPlayerFace(
    float Depth,
    Vector2 A,
    Vector2 B,
    Vector2 C,
    Vector2 D,
    Vector2 Ua,
    Vector2 Ub,
    Vector2 Uc,
    Vector2 Ud,
    Vector4 Tint,
    int Layer,
    PlayerPart Part,
    bool Overlay);

/// <summary>Facts published by a projection, for held-item placement and framebuffer checks.</summary>
internal readonly record struct ProjectedPlayerMeasure(
    int SkinFaces,
    int OuterFaces,
    int ArmourFaces,
    float ArmWidth,
    Vector4 Bounds,
    Vector2 MainHand,
    Vector2 OffHand);

/// <summary>
/// Cached emitted geometry and the one orthographic camera used by every screen-space player model.
/// </summary>
/// <remarks>
/// <para>The SKINS pane and inventory paper doll used to be two renderers: one projected all six
/// faces of <see cref="PlayerModel"/>, while the other copied only front rectangles from the skin
/// sheet and repeated the model's placement arithmetic for armour. This class is the single shared
/// geometry path. Both callers now provide only a viewport, angle and optional worn-material table.</para>
/// <para>Pixels and GL textures deliberately do not live here. A candidate skin and the worn skin
/// can have different texture objects while sharing the exact same classic/slim/legacy geometry.</para>
/// </remarks>
internal sealed class ProjectedPlayerPreview
{
    private readonly record struct Part(
        PlayerPart Kind,
        Vector3 Pivot,
        bool Overlay,
        EquipSlot? Slot,
        int Sheet,
        ModelVertex[] Vertices);

    private static readonly Dictionary<(ArmStyle Arms, bool Legacy), ProjectedPlayerPreview> Cache = [];
    private static readonly Part[] Armour = BuildArmour();

    private readonly Part[] _skin;

    private ProjectedPlayerPreview(ArmStyle arms, bool legacy)
    {
        Arms = arms;
        Legacy = legacy;

        var boxes = PlayerModel.Build(arms, legacy);
        _skin = new Part[boxes.Length];
        for (var i = 0; i < boxes.Length; i++)
        {
            var box = boxes[i];
            _skin[i] = new Part(
                box.Part, box.Pivot, box.Overlay, null, 0,
                Emit((vertices, indices) => PlayerModel.Emit(box, vertices, indices)));
        }

        ArmWidth = boxes.First(box => box is { Part: PlayerPart.RightArm, Overlay: false }).Width;
    }

    public ArmStyle Arms { get; }

    public bool Legacy { get; }

    /// <summary>Measured from the emitted model source, so a slim assertion cannot pass on a label.</summary>
    public float ArmWidth { get; }

    public static ProjectedPlayerPreview For(ArmStyle arms, bool legacy)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue((arms, legacy), out var preview))
                Cache[(arms, legacy)] = preview = new ProjectedPlayerPreview(arms, legacy);
            return preview;
        }
    }

    /// <summary>
    /// Projects the skin and any worn armour through the same pose, camera, culling and lighting.
    /// </summary>
    /// <param name="armourMaterials">
    /// Material index per <see cref="EquipSlot"/>, or -1 for an empty/non-armour slot. An empty span
    /// draws no armour, which is what the skin-library preview wants.
    /// </param>
    public ProjectedPlayerMeasure Project(
        float x,
        float y,
        float width,
        float height,
        float yawDegrees,
        float drift,
        float bottomInset,
        ReadOnlySpan<int> armourMaterials,
        List<ProjectedPlayerFace> skinFaces,
        List<ProjectedPlayerFace> armourFaces)
    {
        skinFaces.Clear();
        armourFaces.Clear();

        var yaw = float.DegreesToRadians(yawDegrees);
        var turn = Matrix4x4.CreateRotationY(yaw);
        var view = Vector3.Normalize(new Vector3(0f, 0.14f, -1f));
        var light = Vector3.Normalize(new Vector3(-0.45f, 0.7f, -0.55f));

        // Nineteen model units fit every base/overlay/armour box across; thirty-four include the
        // helmet and boot stand-off. Deriving both from PlayerModel.Unit keeps this camera pinned to
        // the same body scale as the world renderer.
        var across = 19f * PlayerModel.Unit;
        var tall = 34f * PlayerModel.Unit;
        var scale = MathF.Min(
            MathF.Max(1f, width - 8f) / across,
            MathF.Max(1f, height - bottomInset - 6f) / tall);
        var centreX = x + width * 0.5f;
        var floor = y + height - bottomInset - 3f + PlayerModel.Unit;

        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;
        var outerFaces = 0;

        Vector2 ProjectPoint(Vector3 point) => new(
            centreX + point.X * scale,
            floor - (point.Y + point.Z * 0.10f) * scale);

        void ProjectPart(in Part part, int layer, List<ProjectedPlayerFace> into)
        {
            var pose = Pose(part.Kind, drift);
            var vertices = part.Vertices;

            Span<Vector3> points = stackalloc Vector3[4];
            Span<Vector2> uv = stackalloc Vector2[4];

            for (var face = 0; face < vertices.Length / 4; face++)
            {
                var mean = 0f;
                var normal = Vector3.Zero;

                for (var corner = 0; corner < 4; corner++)
                {
                    var vertex = vertices[face * 4 + corner];
                    var local = Vector3.Transform(vertex.Position, pose);
                    var world = local + part.Pivot * PlayerModel.Unit;
                    world = Vector3.Transform(world, turn);
                    points[corner] = world;
                    uv[corner] = vertex.Uv;
                    mean += world.Z;
                    normal = Vector3.TransformNormal(vertex.Normal, pose);
                }

                normal = Vector3.Normalize(Vector3.TransformNormal(normal, turn));
                if (Vector3.Dot(normal, view) <= 0.02f) continue;

                var a = ProjectPoint(points[0]);
                var b = ProjectPoint(points[1]);
                var c = ProjectPoint(points[2]);
                var d = ProjectPoint(points[3]);

                minX = MathF.Min(minX, MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X)));
                minY = MathF.Min(minY, MathF.Min(MathF.Min(a.Y, b.Y), MathF.Min(c.Y, d.Y)));
                maxX = MathF.Max(maxX, MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X)));
                maxY = MathF.Max(maxY, MathF.Max(MathF.Max(a.Y, b.Y), MathF.Max(c.Y, d.Y)));

                var shade = 0.72f + MathF.Max(0f, Vector3.Dot(normal, light)) * 0.28f;
                into.Add(new ProjectedPlayerFace(
                    mean * 0.25f, a, b, c, d, uv[0], uv[1], uv[2], uv[3],
                    new Vector4(shade, shade, shade, 1f), layer, part.Kind, part.Overlay));
                if (part.Overlay) outerFaces++;
            }
        }

        foreach (var part in _skin) ProjectPart(part, 0, skinFaces);

        if (!armourMaterials.IsEmpty)
        {
            foreach (var part in Armour)
            {
                var slot = part.Slot!.Value;
                var material = (int)slot < armourMaterials.Length ? armourMaterials[(int)slot] : -1;
                if (material < 0) continue;
                ProjectPart(part, material * 2 + part.Sheet, armourFaces);
            }
        }

        // Camera sits on negative Z: larger Z is farther and is painted first. Inflated skin faces
        // then naturally land over the base face beneath their transparent texels.
        skinFaces.Sort(static (a, b) => b.Depth.CompareTo(a.Depth));
        armourFaces.Sort(static (a, b) => b.Depth.CompareTo(a.Depth));

        Vector2 Hand(bool right)
        {
            var kind = right ? PlayerPart.RightArm : PlayerPart.LeftArm;
            var pose = Pose(kind, drift);
            var point = Vector3.Transform(PlayerModel.FistInArm(Arms, right) * PlayerModel.Unit, pose)
                      + PlayerModel.ArmPivot(right) * PlayerModel.Unit;
            return ProjectPoint(Vector3.Transform(point, turn));
        }

        var bounds = skinFaces.Count + armourFaces.Count == 0
            ? Vector4.Zero
            : new Vector4(minX, minY, maxX - minX, maxY - minY);

        return new ProjectedPlayerMeasure(
            skinFaces.Count, outerFaces, armourFaces.Count, ArmWidth, bounds,
            Hand(right: true), Hand(right: false));
    }

    private static Matrix4x4 Pose(PlayerPart part, float drift)
    {
        var idle = MathF.Sin(drift * 0.8f) * 0.025f;
        return part switch
        {
            PlayerPart.RightArm => Matrix4x4.CreateRotationX(0.08f + idle),
            PlayerPart.LeftArm => Matrix4x4.CreateRotationX(-0.08f - idle),
            PlayerPart.RightLeg => Matrix4x4.CreateRotationX(-idle * 0.5f),
            PlayerPart.LeftLeg => Matrix4x4.CreateRotationX(idle * 0.5f),
            _ => Matrix4x4.Identity,
        };
    }

    private static Part[] BuildArmour()
    {
        var boxes = ArmourModel.Build();
        var parts = new Part[boxes.Length];
        for (var i = 0; i < boxes.Length; i++)
        {
            var box = boxes[i];
            parts[i] = new Part(
                box.Part, box.Pivot, false, box.Slot, box.Sheet,
                Emit((vertices, indices) => ArmourModel.Emit(box, vertices, indices)));
        }

        return parts;
    }

    private static ModelVertex[] Emit(Action<List<ModelVertex>, List<uint>> emit)
    {
        var vertices = new List<ModelVertex>(24);
        var indices = new List<uint>(36);
        emit(vertices, indices);
        return [.. vertices];
    }
}
