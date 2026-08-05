using System.Numerics;

namespace Driftwood.Core.Blocks;

/// <summary>
/// One face of one model element: which texture it reads, what hides it, how it is coloured.
/// </summary>
public sealed class ModelFace
{
    /// <summary>Texture array layer this face samples.</summary>
    public required ushort Layer { get; init; }

    /// <summary>
    /// The direction whose neighbour hides this face, or -1 when nothing hides it.
    /// </summary>
    /// <remarks>
    /// This is the model format's <c>cullface</c>, and it is a separate question from which way the
    /// face points. A stair's inner step faces up but sits in the middle of the block, so nothing
    /// above it can hide it; declaring the two independently is what lets a face be culled by a
    /// neighbour it does not itself face.
    /// </remarks>
    public int CullFace { get; init; } = -1;

    /// <summary>True when the block's climate colour multiplies this face.</summary>
    /// <remarks>
    /// The format calls this <c>tintindex</c> and allows several, one per colour a block can want.
    /// Ours is a flag because <see cref="BlockType.Tint"/> already names the single source a block
    /// draws from; a second index would need a second source to point at.
    /// </remarks>
    public bool Tinted { get; init; }

    /// <summary>Texture rect in model units (0..16), or null for the element's own extent.</summary>
    public Vector4? Uv { get; init; }

    /// <summary>Quarter turns applied to the texture, 0..3.</summary>
    public int Rotation { get; init; }
}

/// <summary>
/// One box of a block model, in the format's own units: 16 to the block, origin at the block's
/// minimum corner.
/// </summary>
/// <remarks>
/// Kept in model units rather than block units on purpose. Every number here is one an artist wrote
/// in a model file, and translating on the way in means a face written <c>[0, 8, 16, 16]</c> reads
/// as <c>[0, 8, 16, 16]</c> here too — which is the difference between checking our slab against a
/// real one and re-deriving what a slab is.
/// </remarks>
public sealed class ModelElement
{
    /// <summary>Minimum corner, in model units.</summary>
    public required Vector3 From { get; init; }

    /// <summary>Maximum corner, in model units.</summary>
    public required Vector3 To { get; init; }

    /// <summary>Six faces in <see cref="Faces"/> order; null where the element has none.</summary>
    public required ModelFace?[] Faces { get; init; }

    /// <summary>False to light this element flat, ignoring which way each face points.</summary>
    public bool Shade { get; init; } = true;

    /// <summary>False to skip ambient occlusion on this element.</summary>
    public bool AmbientOcclusion { get; init; } = true;

    /// <summary>Rotation about <see cref="RotationAxis"/> through <see cref="RotationOrigin"/>.</summary>
    public float RotationAngle { get; init; }

    /// <summary>0 = x, 1 = y, 2 = z.</summary>
    public int RotationAxis { get; init; } = 1;

    /// <summary>Pivot in model units.</summary>
    public Vector3 RotationOrigin { get; init; } = new(8f, 8f, 8f);

    /// <summary>
    /// Scales the element out to keep its rotated corners on the block's own bounds.
    /// </summary>
    /// <remarks>
    /// A plant's two crossed planes are 14.4 units wide and turned 45 degrees; without the rescale
    /// they would span 10.2 units of the block's diagonal instead of all 16 and the tuft would sit
    /// in a hole. The factor is 1/cos(angle), applied in the two axes the rotation moves.
    /// </remarks>
    public bool Rescale { get; init; }
}

/// <summary>One corner of a baked quad: where it sits in the block, and where it reads the texture.</summary>
/// <param name="Position">Offset from the block's minimum corner, in block units.</param>
/// <param name="U">Texture column, in tiles.</param>
/// <param name="V">Texture row, in tiles, growing downward.</param>
public readonly record struct ModelCorner(Vector3 Position, float U, float V);

/// <summary>One quad of a baked model, ready for the mesher to place and light.</summary>
public sealed class ModelQuad
{
    /// <summary>Four corners, wound counter-clockwise seen from outside.</summary>
    public required ModelCorner[] Corners { get; init; }

    public required ushort Layer { get; init; }

    /// <summary>The face direction this quad nominally points, for lighting and occlusion.</summary>
    public required int Face { get; init; }

    public int CullFace { get; init; } = -1;
    public bool Tinted { get; init; }
    public bool Shade { get; init; } = true;
    public bool Occlude { get; init; } = true;

    /// <summary>
    /// True when the quad lies on the block boundary it faces, so it should read the light of the
    /// cell beyond rather than the cell it belongs to.
    /// </summary>
    /// <remarks>
    /// A slab's underside is flush and takes the light of the block below; its top surface is half
    /// way up the block and takes the light of the air it sits in. Sampling the neighbour for both
    /// would light the top of a slab by whatever is standing on the block above it.
    /// </remarks>
    public bool Flush { get; init; }
}

/// <summary>
/// The shape of a block: a list of boxes with a texture on each face, baked down to quads.
/// </summary>
/// <remarks>
/// <para>Replaces "three texture layers and an implied cube". The cube is still the overwhelming
/// majority of the world and still takes the greedy merge — <see cref="IsFullCube"/> is what says
/// so — but it is now one shape among several rather than the only one the mesher can express.</para>
/// <para>A full cube may carry more than one element, all of them the same box. That is how a grass
/// block works: a plain cube, then a second coplanar cube whose four sides are a cut-out overlay
/// tinted by climate. Each of those is a <em>pass</em>, and the mesher merges each pass separately
/// so the overlay is still one greedy quad per wall rather than one per block.</para>
/// <para>Everything is baked at registration: rotations are applied, texture coordinates resolved,
/// and the result is a flat array of quads. Nothing in the mesher's hot path parses a model.</para>
/// </remarks>
public sealed class BlockModel
{
    /// <summary>Coplanar cube passes a model may carry before it stops being greedy-eligible.</summary>
    /// <remarks>Two bits' worth in the vertex, which is what carries the coplanar draw order.</remarks>
    public const int MaxPasses = 4;

    /// <summary>Marks a face a pass does not draw.</summary>
    public const ushort NoLayer = ushort.MaxValue;

    private readonly ushort[] _passLayer;
    private readonly bool[] _passTinted;

    /// <summary>Every quad this model draws, for blocks that cannot be merged.</summary>
    public ModelQuad[] Quads { get; }

    /// <summary>The elements this was built from, kept for the checks that read them back.</summary>
    public IReadOnlyList<ModelElement> Elements { get; }

    /// <summary>
    /// True when every element fills the block exactly and is culled by its own six directions —
    /// the shape the greedy mesher can merge, and the only shape that may hide its neighbours.
    /// </summary>
    public bool IsFullCube { get; }

    /// <summary>Coplanar cube passes, 1 or more. Meaningless unless <see cref="IsFullCube"/>.</summary>
    public int PassCount { get; }

    /// <summary>
    /// The box a selection outline and a cracking overlay wrap around, in block units.
    /// </summary>
    /// <remarks>
    /// Taken from the baked quads, so a turned shape gets the box it actually occupies rather than
    /// the one its unturned corners describe. A few shapes override it: a torch stretches two
    /// planes across the whole cell so a two-unit stick still reads at a distance, and outlining
    /// the planes would draw a full cube around a candle.
    /// </remarks>
    public (Vector3 Min, Vector3 Max) Outline { get; private set; }

    /// <summary>
    /// The tile debris off this block wears.
    /// </summary>
    /// <remarks>
    /// The format declares this outright as <c>particle</c> in a model's texture list, because a
    /// block with six different faces has no obvious answer. Ours takes the first quad's, which for
    /// a cube is a side and for a shape is whatever it draws first — right for everything we have,
    /// and the place the declaration goes when a pack's own models start arriving.
    /// </remarks>
    public ushort ParticleLayer { get; }

    private BlockModel(IReadOnlyList<ModelElement> elements)
    {
        Elements = elements;

        var quads = new List<ModelQuad>(elements.Count * 6);
        foreach (var element in elements) Bake(element, quads);
        Quads = [.. quads];

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var quad in Quads)
        foreach (var corner in quad.Corners)
        {
            min = Vector3.Min(min, corner.Position);
            max = Vector3.Max(max, corner.Position);
        }

        Outline = Quads.Length > 0
            ? (min, max)
            : (Vector3.Zero, Vector3.One);

        ParticleLayer = Quads.Length > 0 ? Quads[0].Layer : (ushort)0;

        var whole = elements.Count is > 0 and <= MaxPasses;
        foreach (var element in elements) whole &= IsWholeBlock(element);

        IsFullCube = whole;
        PassCount = IsFullCube ? elements.Count : 0;

        _passLayer = new ushort[MaxPasses * Blocks.Faces.Count];
        _passTinted = new bool[MaxPasses * Blocks.Faces.Count];
        Array.Fill(_passLayer, NoLayer);

        if (!IsFullCube) return;

        for (var pass = 0; pass < elements.Count; pass++)
        for (var face = 0; face < Blocks.Faces.Count; face++)
        {
            var spec = elements[pass].Faces[face];
            if (spec is null) continue;
            _passLayer[pass * Blocks.Faces.Count + face] = spec.Layer;
            _passTinted[pass * Blocks.Faces.Count + face] = spec.Tinted;
        }
    }

    /// <summary>Texture for one face of one cube pass, or <see cref="NoLayer"/>.</summary>
    public ushort PassLayer(int pass, int face) => _passLayer[pass * Blocks.Faces.Count + face];

    /// <summary>Whether one face of one cube pass takes the block's climate colour.</summary>
    public bool PassTinted(int pass, int face) => _passTinted[pass * Blocks.Faces.Count + face];

    public static BlockModel FromElements(params ModelElement[] elements) => new(elements);

    /// <summary>The ordinary block: one box filling the cell, three textures.</summary>
    public static BlockModel Cube(ushort top, ushort side, ushort bottom, bool tinted = false)
    {
        var faces = new ModelFace?[Blocks.Faces.Count];
        for (var face = 0; face < Blocks.Faces.Count; face++)
        {
            var layer = face switch
            {
                Blocks.Faces.PosY => top,
                Blocks.Faces.NegY => bottom,
                _ => side,
            };
            faces[face] = new ModelFace { Layer = layer, CullFace = face, Tinted = tinted };
        }

        return new BlockModel([WholeBlock(faces)]);
    }

    /// <summary>A cube whose four sides carry a second, tinted cut-out over the first.</summary>
    /// <remarks>
    /// The grass block, and the reason model-driven blocks came before anything else. Packs paint
    /// the fringe down a grass block's side as its own alpha texture, expecting the game to lay it
    /// over the dirt and multiply the climate colour through it. Drawing only the first pass leaves
    /// grass with plain brown sides — the one thing in an imported pack that still looked broken.
    /// </remarks>
    public static BlockModel CubeWithSideOverlay(ushort top, ushort side, ushort bottom, ushort overlay)
    {
        var baseFaces = new ModelFace?[Blocks.Faces.Count];
        var overlayFaces = new ModelFace?[Blocks.Faces.Count];

        for (var face = 0; face < Blocks.Faces.Count; face++)
        {
            var layer = face switch
            {
                Blocks.Faces.PosY => top,
                Blocks.Faces.NegY => bottom,
                _ => side,
            };
            baseFaces[face] = new ModelFace
            {
                Layer = layer,
                CullFace = face,
                Tinted = face == Blocks.Faces.PosY,
            };

            if (face is Blocks.Faces.PosY or Blocks.Faces.NegY) continue;
            overlayFaces[face] = new ModelFace { Layer = overlay, CullFace = face, Tinted = true };
        }

        return new BlockModel([WholeBlock(baseFaces), WholeBlock(overlayFaces)]);
    }

    /// <summary>Two planes crossed through the middle of the block: the shape of every small plant.</summary>
    public static BlockModel Cross(ushort layer, bool tinted = true)
    {
        var elements = new ModelElement[2];
        for (var i = 0; i < 2; i++)
        {
            var faces = new ModelFace?[Blocks.Faces.Count];
            var (a, b) = i == 0
                ? (Blocks.Faces.NegZ, Blocks.Faces.PosZ)
                : (Blocks.Faces.NegX, Blocks.Faces.PosX);

            faces[a] = new ModelFace { Layer = layer, Tinted = tinted, Uv = new Vector4(0f, 0f, 16f, 16f) };
            faces[b] = new ModelFace { Layer = layer, Tinted = tinted, Uv = new Vector4(0f, 0f, 16f, 16f) };

            elements[i] = new ModelElement
            {
                From = i == 0 ? new Vector3(0.8f, 0f, 8f) : new Vector3(8f, 0f, 0.8f),
                To = i == 0 ? new Vector3(15.2f, 16f, 8f) : new Vector3(8f, 16f, 15.2f),
                Faces = faces,
                Shade = false,
                AmbientOcclusion = false,
                RotationAngle = 45f,
                RotationAxis = 1,
                Rescale = true,
            };
        }

        return new BlockModel(elements);
    }

    /// <summary>A thin sheet lying on the floor: a carpet, or the first fall of snow.</summary>
    public static BlockModel Layer(ushort top, ushort side, ushort bottom, float height) =>
        new([Box(Vector3.Zero, new Vector3(16f, height, 16f), top, side, bottom)]);

    /// <summary>Half a block, lying in either half of the cell.</summary>
    public static BlockModel Slab(ushort top, ushort side, ushort bottom, bool upper) =>
        new([upper
            ? Box(new Vector3(0f, 8f, 0f), new Vector3(16f, 16f, 16f), top, side, bottom)
            : Box(Vector3.Zero, new Vector3(16f, 8f, 16f), top, side, bottom)]);

    /// <summary>
    /// A slab with a step raised over one half of it.
    /// </summary>
    /// <param name="facing">The side the raised half sits on, in <see cref="Faces"/> order.</param>
    /// <param name="upper">True for stairs hung the other way up, as if fixed to the ceiling.</param>
    /// <remarks>
    /// Built per facing rather than by turning one model four times. Rotating a box means rotating
    /// which texture is on which face and which direction culls it, and every one of those is a
    /// chance to be off by a quarter turn in a way that only shows from one side. Four sets of two
    /// corners each say the same thing and say it plainly.
    /// </remarks>
    public static BlockModel Stairs(ushort top, ushort side, ushort bottom, int facing, bool upper)
    {
        // The half the solid part fills, and the half the step sits in above or below it.
        var (slabFrom, slabTo) = upper
            ? (new Vector3(0f, 8f, 0f), new Vector3(16f, 16f, 16f))
            : (Vector3.Zero, new Vector3(16f, 8f, 16f));

        var stepLow = upper ? 0f : 8f;
        var stepHigh = upper ? 8f : 16f;

        var (stepFrom, stepTo) = facing switch
        {
            Faces.PosX => (new Vector3(8f, stepLow, 0f), new Vector3(16f, stepHigh, 16f)),
            Faces.NegX => (new Vector3(0f, stepLow, 0f), new Vector3(8f, stepHigh, 16f)),
            Faces.PosZ => (new Vector3(0f, stepLow, 8f), new Vector3(16f, stepHigh, 16f)),
            _ => (new Vector3(0f, stepLow, 0f), new Vector3(16f, stepHigh, 8f)),
        };

        return new BlockModel(
        [
            Box(slabFrom, slabTo, top, side, bottom),
            Box(stepFrom, stepTo, top, side, bottom),
        ]);
    }

    /// <summary>
    /// A torch: a square post drawn as two crossed full-width planes, with the flame on its cap.
    /// </summary>
    /// <remarks>
    /// The planes are wider than the post they draw. That is not a mistake in the format and it is
    /// what makes a torch read at a distance: two 2-unit-wide boxes would be nearly invisible edge
    /// on, so the model stretches the tile across the whole cell and lets the transparent margin do
    /// the shaping. The cap reads a 2x2 patch out of the middle of the tile, which is the one place
    /// an explicit <c>uv</c> is doing real work rather than restating the default.
    /// </remarks>
    public static BlockModel Torch(ushort layer)
    {
        var post = new ModelFace?[Faces.Count];
        post[Faces.PosY] = new ModelFace { Layer = layer, Uv = new Vector4(7f, 6f, 9f, 8f) };
        post[Faces.NegY] = new ModelFace { Layer = layer, Uv = new Vector4(7f, 13f, 9f, 15f) };

        var alongX = new ModelFace?[Faces.Count];
        alongX[Faces.NegX] = new ModelFace { Layer = layer, Uv = new Vector4(0f, 0f, 16f, 16f) };
        alongX[Faces.PosX] = new ModelFace { Layer = layer, Uv = new Vector4(16f, 0f, 0f, 16f) };

        var alongZ = new ModelFace?[Faces.Count];
        alongZ[Faces.NegZ] = new ModelFace { Layer = layer, Uv = new Vector4(16f, 0f, 0f, 16f) };
        alongZ[Faces.PosZ] = new ModelFace { Layer = layer, Uv = new Vector4(0f, 0f, 16f, 16f) };

        var model = new BlockModel(
        [
            new ModelElement
            {
                From = new Vector3(7f, 0f, 7f), To = new Vector3(9f, 10f, 9f),
                Faces = post, Shade = false, AmbientOcclusion = false,
            },
            new ModelElement
            {
                From = new Vector3(7f, 0f, 0f), To = new Vector3(9f, 16f, 16f),
                Faces = alongX, Shade = false, AmbientOcclusion = false,
            },
            new ModelElement
            {
                From = new Vector3(0f, 0f, 7f), To = new Vector3(16f, 16f, 9f),
                Faces = alongZ, Shade = false, AmbientOcclusion = false,
            },
        ]);

        // The stick, not the planes that draw it.
        model.Outline = (new Vector3(7f, 0f, 7f) / 16f, new Vector3(9f, 10f, 9f) / 16f);
        return model;
    }

    /// <summary>
    /// One box with a texture on every face, culled by the sides of the cell it actually touches.
    /// </summary>
    /// <remarks>
    /// The cull rule is derived rather than declared, and it has to be: a face that stops short of
    /// the block boundary can never be hidden by the neighbour beyond it, and saying it can leaves
    /// a hole in the top of every slab with something stacked on it. A face flush with the boundary
    /// is culled by its own direction, which is what every hand-written model in the format says
    /// too.
    /// </remarks>
    private static ModelElement Box(
        Vector3 from, Vector3 to, ushort top, ushort side, ushort bottom, bool tinted = false)
    {
        var element = new ModelElement { From = from, To = to, Faces = new ModelFace?[Faces.Count] };

        for (var face = 0; face < Faces.Count; face++)
        {
            var layer = face switch
            {
                Faces.PosY => top,
                Faces.NegY => bottom,
                _ => side,
            };

            element.Faces[face] = new ModelFace
            {
                Layer = layer,
                CullFace = IsFlush(element, face) ? face : -1,
                Tinted = tinted,
            };
        }

        return element;
    }

    private static ModelElement WholeBlock(ModelFace?[] faces) => new()
    {
        From = Vector3.Zero,
        To = new Vector3(16f, 16f, 16f),
        Faces = faces,
    };

    private static bool IsWholeBlock(ModelElement element)
    {
        if (element.RotationAngle != 0f) return false;
        if (!element.Shade || !element.AmbientOcclusion) return false;
        if (element.From != Vector3.Zero || element.To != new Vector3(16f, 16f, 16f)) return false;

        for (var face = 0; face < Blocks.Faces.Count; face++)
        {
            var spec = element.Faces[face];
            if (spec is null) continue;

            // A face the greedy path draws is culled by the direction it points and reads its whole
            // tile. Anything else — a face nothing hides, a rotated or cropped texture — has to be
            // emitted per block, because merging assumes both.
            if (spec.CullFace != face) return false;
            if (spec.Rotation != 0) return false;
            if (spec.Uv is { } uv && uv != new Vector4(0f, 0f, 16f, 16f)) return false;
        }

        return true;
    }

    /// <summary>
    /// Turns one element into quads: applies the element's rotation, resolves each face's texture
    /// coordinates, and records what the mesher needs to light the result.
    /// </summary>
    private static void Bake(ModelElement element, List<ModelQuad> into)
    {
        var transform = BuildTransform(element);

        // Hoisted: a stackalloc inside the loop is not released until the method returns, so the
        // frame would grow with every face rather than being reused across them.
        Span<float> modelU = stackalloc float[4];
        Span<float> modelV = stackalloc float[4];
        Span<Vector3> local = stackalloc Vector3[4];

        for (var face = 0; face < Blocks.Faces.Count; face++)
        {
            var spec = element.Faces[face];
            if (spec is null) continue;

            var corners = new ModelCorner[4];

            // The face's texture coordinates over the whole block, so an element's own extent can be
            // expressed in the format's units. Taken from the same projection the shader uses for
            // full cubes, which is what keeps a model's explicit uv landing where its author meant.
            var (originU, originV) = FaceUvOrigin(face);

            for (var c = 0; c < 4; c++)
            {
                var unit = Blocks.Faces.Corners[face][c];
                local[c] = new Vector3(
                    float.Lerp(element.From.X, element.To.X, unit.X),
                    float.Lerp(element.From.Y, element.To.Y, unit.Y),
                    float.Lerp(element.From.Z, element.To.Z, unit.Z));

                var (u, v) = ProjectUv(face, local[c] / 16f);
                modelU[c] = (u - originU) * 16f;
                modelV[c] = (v - originV) * 16f;
            }

            var minU = MathF.Min(MathF.Min(modelU[0], modelU[1]), MathF.Min(modelU[2], modelU[3]));
            var maxU = MathF.Max(MathF.Max(modelU[0], modelU[1]), MathF.Max(modelU[2], modelU[3]));
            var minV = MathF.Min(MathF.Min(modelV[0], modelV[1]), MathF.Min(modelV[2], modelV[3]));
            var maxV = MathF.Max(MathF.Max(modelV[0], modelV[1]), MathF.Max(modelV[2], modelV[3]));

            // Zero area on both axes is not a face; the format expresses a flat plane by leaving the
            // four edge-on faces out, and a hand-written model can leave one in by accident.
            if (maxU - minU <= 0f && maxV - minV <= 0f) continue;

            var rect = spec.Uv ?? new Vector4(minU, minV, maxU, maxV);

            for (var c = 0; c < 4; c++)
            {
                var fu = maxU > minU ? (modelU[c] - minU) / (maxU - minU) : 0f;
                var fv = maxV > minV ? (modelV[c] - minV) / (maxV - minV) : 0f;
                (fu, fv) = RotateFraction(fu, fv, spec.Rotation);

                corners[c] = new ModelCorner(
                    Vector3.Transform(local[c], transform) / 16f,
                    float.Lerp(rect.X, rect.Z, fu) / 16f,
                    float.Lerp(rect.Y, rect.W, fv) / 16f);
            }

            into.Add(new ModelQuad
            {
                Corners = corners,
                Layer = spec.Layer,
                Face = face,
                CullFace = spec.CullFace,
                Tinted = spec.Tinted,
                Shade = element.Shade,
                Occlude = element.AmbientOcclusion,
                Flush = element.RotationAngle == 0f && IsFlush(element, face),
            });
        }
    }

    private static Matrix4x4 BuildTransform(ModelElement element)
    {
        if (element.RotationAngle == 0f) return Matrix4x4.Identity;

        var radians = element.RotationAngle * MathF.PI / 180f;
        var rotation = element.RotationAxis switch
        {
            0 => Matrix4x4.CreateRotationX(radians),
            2 => Matrix4x4.CreateRotationZ(radians),
            _ => Matrix4x4.CreateRotationY(radians),
        };

        if (element.Rescale)
        {
            var factor = 1f / MathF.Cos(radians);
            var scale = element.RotationAxis switch
            {
                0 => new Vector3(1f, factor, factor),
                2 => new Vector3(factor, factor, 1f),
                _ => new Vector3(factor, 1f, factor),
            };

            // Scaled after the turn, not before, which is the order the format specifies. The two
            // agree here because the factor is the same on both axes the rotation moves, but only
            // one of them stays right for a rotation that is not a quarter or eighth turn.
            rotation *= Matrix4x4.CreateScale(scale);
        }

        return Matrix4x4.CreateTranslation(-element.RotationOrigin)
             * rotation
             * Matrix4x4.CreateTranslation(element.RotationOrigin);
    }

    /// <summary>True when the element's face sits on the block boundary it points at.</summary>
    private static bool IsFlush(ModelElement element, int face)
    {
        var n = Blocks.Faces.Normals[face];
        var axis = n.X != 0 ? 0 : n.Y != 0 ? 1 : 2;
        var positive = n.X + n.Y + n.Z > 0;

        var value = positive
            ? axis == 0 ? element.To.X : axis == 1 ? element.To.Y : element.To.Z
            : axis == 0 ? element.From.X : axis == 1 ? element.From.Y : element.From.Z;

        return value == (positive ? 16f : 0f);
    }

    /// <summary>
    /// The texture coordinate a face projects a position onto, in tiles.
    /// </summary>
    /// <remarks>
    /// The same six expressions the chunk shader uses to derive coordinates for merged cube faces,
    /// kept here so both answers come from one statement of the convention. They agree, face for
    /// face, with the defaults the model format specifies, which is the whole reason an explicit
    /// <c>uv</c> written for another renderer lands correctly here.
    /// </remarks>
    private static (float U, float V) ProjectUv(int face, Vector3 p) => face switch
    {
        Blocks.Faces.PosX => (-p.Z, -p.Y),
        Blocks.Faces.NegX => (p.Z, -p.Y),
        Blocks.Faces.PosY => (p.X, p.Z),
        Blocks.Faces.NegY => (p.X, -p.Z),
        Blocks.Faces.PosZ => (p.X, -p.Y),
        _ => (-p.X, -p.Y),
    };

    /// <summary>Where the face's own tile starts, so an element's extent reads 0..16 like a model file.</summary>
    private static (float U, float V) FaceUvOrigin(int face)
    {
        var u = float.MaxValue;
        var v = float.MaxValue;
        for (var c = 0; c < 4; c++)
        {
            var unit = Blocks.Faces.Corners[face][c];
            var (cu, cv) = ProjectUv(face, new Vector3(unit.X, unit.Y, unit.Z));
            u = MathF.Min(u, cu);
            v = MathF.Min(v, cv);
        }
        return (u, v);
    }

    /// <summary>
    /// Checks every registered shape, returning one line per fault and nothing when they are sound.
    /// </summary>
    /// <remarks>
    /// <para>Winding is the reason this exists. A quad wound the wrong way round is culled, so the
    /// face you were meant to see is missing and the one behind it is not — and there is no tell in
    /// a block census, a vertex count or a texture check. The horizontal cube faces shipped
    /// inverted once already; a model file is a far easier place to make the same mistake, because
    /// its corners come from two numbers rather than from a table anybody reviewed.</para>
    /// <para>The texture check is the other half. Every quad's four corners must land on the four
    /// corners of its texture rectangle, and consecutive corners must differ in exactly one of the
    /// two coordinates — which is what "the texture is traversed around its edge" means. A mapping
    /// that scrambles two corners produces a bow-tie that still covers the right pixels and reads
    /// as a rendering glitch rather than as a table being wrong.</para>
    /// </remarks>
    public static IReadOnlyList<string> Validate(IReadOnlyList<BlockType> blocks)
    {
        var faults = new List<string>();
        var shaped = 0;
        var quads = 0;

        foreach (var type in blocks)
        {
            var model = type.Model;
            if (model is null)
            {
                faults.Add($"{type.Name}: no model");
                continue;
            }

            if (!model.IsFullCube) shaped++;
            if (type.Opaque && !model.IsFullCube)
                faults.Add($"{type.Name}: opaque, but its model does not fill the block");

            foreach (var quad in model.Quads)
            {
                quads++;
                Check(type.Name, quad, faults);
            }
        }

        // A degenerate pass: every block a cube means the per-block path never runs and nothing
        // above tested anything. The count is the check that says the suite had something to check.
        if (shaped == 0) faults.Add("no registered block has a shape other than a cube");
        if (quads == 0) faults.Add("no model baked any geometry");

        return faults;
    }

    private static void Check(string name, ModelQuad quad, List<string> faults)
    {
        if (quad.Corners.Length != 4)
        {
            faults.Add($"{name}: quad has {quad.Corners.Length} corners");
            return;
        }

        var p = quad.Corners;
        var e1 = p[1].Position - p[0].Position;
        var e2 = p[2].Position - p[1].Position;
        var cross = Vector3.Cross(e1, e2);

        if (cross.Length() < 1e-5f)
        {
            faults.Add($"{name}: quad on face {quad.Face} has no area");
            return;
        }

        // Outward, not inward. A rotated element's normal turns with it, so the test is that the
        // wound normal still leans the way the face was declared rather than that it matches.
        var n = Faces.Normals[quad.Face];
        var declared = new Vector3(n.X, n.Y, n.Z);
        if (Vector3.Dot(cross, declared) <= 0f)
            faults.Add($"{name}: quad on face {quad.Face} is wound inward ({cross.X:F2},{cross.Y:F2},{cross.Z:F2})");

        foreach (var corner in p)
        {
            var q = corner.Position;
            if (q.X is < -1f or > 2f || q.Y is < -1f or > 2f || q.Z is < -1f or > 2f)
                faults.Add($"{name}: corner ({q.X:F2},{q.Y:F2},{q.Z:F2}) is more than a block outside its cell");
        }

        var minU = MathF.Min(MathF.Min(p[0].U, p[1].U), MathF.Min(p[2].U, p[3].U));
        var maxU = MathF.Max(MathF.Max(p[0].U, p[1].U), MathF.Max(p[2].U, p[3].U));
        var minV = MathF.Min(MathF.Min(p[0].V, p[1].V), MathF.Min(p[2].V, p[3].V));
        var maxV = MathF.Max(MathF.Max(p[0].V, p[1].V), MathF.Max(p[2].V, p[3].V));

        if (maxU - minU < 1e-4f || maxV - minV < 1e-4f)
        {
            faults.Add($"{name}: quad on face {quad.Face} reads a texture rectangle with no width");
            return;
        }

        const float Tolerance = 1e-3f;
        for (var c = 0; c < 4; c++)
        {
            var onU = MathF.Abs(p[c].U - minU) < Tolerance || MathF.Abs(p[c].U - maxU) < Tolerance;
            var onV = MathF.Abs(p[c].V - minV) < Tolerance || MathF.Abs(p[c].V - maxV) < Tolerance;
            if (!onU || !onV)
            {
                faults.Add($"{name}: corner {c} on face {quad.Face} reads ({p[c].U:F3},{p[c].V:F3}), "
                         + $"off the rectangle {minU:F3}..{maxU:F3} by {minV:F3}..{maxV:F3}");
                continue;
            }

            var next = p[(c + 1) & 3];
            var movedU = MathF.Abs(next.U - p[c].U) > Tolerance;
            var movedV = MathF.Abs(next.V - p[c].V) > Tolerance;
            if (movedU == movedV)
                faults.Add($"{name}: face {quad.Face} steps from corner {c} diagonally across its texture");
        }
    }

    private static (float U, float V) RotateFraction(float u, float v, int rotation) => (rotation & 3) switch
    {
        1 => (v, 1f - u),
        2 => (1f - u, 1f - v),
        3 => (1f - v, u),
        _ => (u, v),
    };
}
