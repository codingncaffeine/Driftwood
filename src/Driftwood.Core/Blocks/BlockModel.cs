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
    /// The boxes a body actually walks into, in block units. Empty means nothing to collide with.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>Separate from <see cref="Outline"/> on purpose, and separate from the drawn shape
    /// too.</b> The outline is one box round everything so there is something to aim at; collision is
    /// every box, so a stair is two steps rather than one block-sized lump that happens to enclose
    /// them. And it is not simply the elements either: a fence is a metre of post that a body must
    /// not be able to hop over, and a campfire is a ring of logs with a hole in the middle that
    /// nobody should be able to stand in.</para>
    /// <para>Built from the <em>baked</em> element boxes, so a turned shape collides where it ended
    /// up. Planes are dropped — a crossed tuft has no volume — coplanar passes are one box, and
    /// every box is clamped into its own cell, which is what lets a body scan only the cells its own
    /// box overlaps and still be sure it has seen everything.</para>
    /// </remarks>
    public (Vector3 Min, Vector3 Max)[] Collision { get; private set; }

    /// <summary>One box of a block, and the three faces of it a slot icon can see.</summary>
    /// <param name="Top">The layer on its +y face, or <see cref="NoLayer"/>.</param>
    /// <param name="Left">Its +z face — the one drawn to the lower left.</param>
    /// <param name="Right">Its +x face — the one drawn to the lower right.</param>
    public readonly record struct IconBox(
        Vector3 Min, Vector3 Max, ushort Top, ushort Left, ushort Right);

    /// <summary>
    /// The boxes a slot draws this block as, nearest last.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>Because a block drawn as one of its faces is not recognisable, and that was only
    /// half fixed.</b> A slot could already draw a <em>cube</em> as three shaded faces — which is
    /// why a bench reads as a bench — but every shaped block fell straight back to a single flat
    /// tile. A stone block, a stone slab and stone stairs were the same grey square, and that is
    /// forty of the hundred and ten things in the game.</para>
    /// <para>Sorted by how near the viewer each box is, so a slot can draw them in order and let
    /// later ones cover earlier ones. With three faces per box and no depth buffer on the overlay,
    /// painter's order is the whole of the hidden-surface problem.</para>
    /// <para>Planes are dropped and boxes are clamped into the cell for the same reasons
    /// <see cref="Collision"/> is — a crossed tuft has no volume to shade, and a torch's planes
    /// reach outside the block they draw.</para>
    /// </remarks>
    public IconBox[] Icon { get; private set; }

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

        Collision = BuildCollision(elements, Outline);
        Icon = BuildIcon(elements);

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

    /// <summary>
    /// A cube whose two pairs of sides differ — a working face and a plain one.
    /// </summary>
    /// <remarks>
    /// ⚠ What a bench actually is, and what ours was missing. Every pack in the genre paints a
    /// <c>crafting_table_front</c> as well as a <c>crafting_table_side</c>, and mapping one tile to
    /// all four sides made it read as a crate: no saw, no working edge, nothing to say which way you
    /// stand at it. Two-and-two rather than a single facing because a bench has no back — you work
    /// at it from either side, and the pack's own art says so by only being two textures.
    /// </remarks>
    public static BlockModel CubeTwoSided(ushort top, ushort front, ushort side, ushort bottom)
    {
        var faces = new ModelFace?[Blocks.Faces.Count];
        for (var face = 0; face < Blocks.Faces.Count; face++)
        {
            var layer = face switch
            {
                Blocks.Faces.PosY => top,
                Blocks.Faces.NegY => bottom,
                Blocks.Faces.PosZ or Blocks.Faces.NegZ => front,
                _ => side,
            };

            faces[face] = new ModelFace { Layer = layer, CullFace = face };
        }

        return new BlockModel([WholeBlock(faces)]);
    }

    /// <summary>
    /// A standing banner: the pole up the middle of the cell, and a hanging cloth on the side
    /// it faces.
    /// </summary>
    /// <remarks>
    /// ⚠ The reference hangs its banner art off an entity sheet no block texture can express —
    /// #56's own problem — so the cloth simply wears the wool it was woven from: the colour IS
    /// the banner, exactly as the colour is the dye.
    /// </remarks>
    public static BlockModel Banner(ushort cloth, ushort pole, int facing)
    {
        var poleFaces = new ModelFace?[Blocks.Faces.Count];
        for (var face = 0; face < Blocks.Faces.Count; face++)
            poleFaces[face] = new ModelFace { Layer = pole, Uv = new Vector4(7f, 0f, 9f, 16f) };

        var clothFaces = new ModelFace?[Blocks.Faces.Count];
        for (var face = 0; face < Blocks.Faces.Count; face++)
            clothFaces[face] = new ModelFace { Layer = cloth, Uv = new Vector4(1f, 1f, 15f, 15f) };

        var (from, to) = facing switch
        {
            Blocks.Faces.PosX => (new Vector3(9.5f, 1f, 1f), new Vector3(10.5f, 15f, 15f)),
            Blocks.Faces.NegX => (new Vector3(5.5f, 1f, 1f), new Vector3(6.5f, 15f, 15f)),
            Blocks.Faces.PosZ => (new Vector3(1f, 1f, 9.5f), new Vector3(15f, 15f, 10.5f)),
            _ => (new Vector3(1f, 1f, 5.5f), new Vector3(15f, 15f, 6.5f)),
        };

        return new BlockModel(
        [
            new ModelElement
            {
                From = new Vector3(7f, 0f, 7f), To = new Vector3(9f, 16f, 9f), Faces = poleFaces,
            },
            new ModelElement { From = from, To = to, Faces = clothFaces },
        ]);
    }

    /// <summary>A cube with one side different from the other three — a machine with a face.</summary>
    /// <remarks>
    /// Still a full cube, so it still merges: the greedy pass keys on the block id and every facing
    /// is its own id, so four furnaces in a row facing the same way merge and four facing different
    /// ways do not. That is the correct answer to both.
    /// </remarks>
    public static BlockModel CubeFacing(ushort top, ushort side, ushort bottom, ushort front, int facing)
    {
        var faces = new ModelFace?[Blocks.Faces.Count];
        for (var face = 0; face < Blocks.Faces.Count; face++)
        {
            var layer = face == facing
                ? front
                : face switch
                {
                    Blocks.Faces.PosY => top,
                    Blocks.Faces.NegY => bottom,
                    _ => side,
                };
            faces[face] = new ModelFace { Layer = layer, CullFace = face };
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

    /// <summary>
    /// A post with arms reaching toward whichever sides it is connected to — a fence, a wall, a pane.
    /// </summary>
    /// <param name="postHalf">Half the post's width, in sixteenths.</param>
    /// <param name="armHalf">Half an arm's width.</param>
    /// <param name="bars">The heights the arms run at. Two for a fence's rails, one for a wall.</param>
    /// <param name="mask">
    /// Which sides are connected, one bit per entry of <see cref="Placeable.Facings"/>.
    /// </param>
    /// <remarks>
    /// <para>Sixteen shapes rather than one shape turned: which sides a thing joins is not an
    /// orientation, it is a set, and a set of four has sixteen members. Every one of them is its own
    /// registered block, the way every stair facing is — a cell holds an id and nothing beside it.
    /// </para>
    /// <para>The arms start at the post's face rather than at the middle of the cell. Overlapping
    /// boxes are legal and invisible from outside, but they double the quads through the post and
    /// every fence in a line pays for it.</para>
    /// </remarks>
    /// <param name="collideHigh">
    /// How high a body finds this, in model units, when that is not how tall it is drawn — 0 to
    /// collide with what is drawn.
    /// </param>
    public static BlockModel Connected(
        ushort top, ushort side, ushort bottom,
        float postHalf, float armHalf, (float Low, float High)[] bars, int mask,
        float height = 16f, float collideHigh = 0f)
    {
        var elements = new List<ModelElement>(1 + 4 * bars.Length)
        {
            Box(
                new Vector3(8f - postHalf, 0f, 8f - postHalf),
                new Vector3(8f + postHalf, height, 8f + postHalf),
                top, side, bottom),
        };

        for (var i = 0; i < Blocks.Placeable.Facings.Length; i++)
        {
            if ((mask & (1 << i)) == 0) continue;

            foreach (var (low, high) in bars)
            {
                var (from, to) = Blocks.Placeable.Facings[i] switch
                {
                    Blocks.Faces.PosX => (
                        new Vector3(8f + postHalf, low, 8f - armHalf),
                        new Vector3(16f, high, 8f + armHalf)),
                    Blocks.Faces.NegX => (
                        new Vector3(0f, low, 8f - armHalf),
                        new Vector3(8f - postHalf, high, 8f + armHalf)),
                    Blocks.Faces.PosZ => (
                        new Vector3(8f - armHalf, low, 8f + postHalf),
                        new Vector3(8f + armHalf, high, 16f)),
                    _ => (
                        new Vector3(8f - armHalf, low, 0f),
                        new Vector3(8f + armHalf, high, 8f - postHalf)),
                };

                elements.Add(Box(from, to, top, side, bottom));
            }
        }

        var model = new BlockModel(elements);

        // ⛳ A fence is a metre of timber you cannot hop over, and that is a rule about the game
        // rather than about the shape — the reference draws one a block high and collides with it a
        // block and a half high for exactly this reason. Written here as the boxes that are drawn,
        // raised, so a fence still lets a body walk *through* the gap beside the post it always did.
        if (collideHigh > 0f)
        {
            var raised = new List<(Vector3 Min, Vector3 Max)>(model.Collision.Length);
            foreach (var (min, max) in model.Collision)
                raised.Add((min, new Vector3(max.X, MathF.Max(max.Y, collideHigh / 16f), max.Z)));

            model.Collision = [.. raised];
        }

        return model;
    }

    /// <summary>A thin sheet lying on the floor: a carpet, or the first fall of snow.</summary>
    public static BlockModel Layer(ushort top, ushort side, ushort bottom, float height) =>
        new([Box(Vector3.Zero, new Vector3(16f, height, 16f), top, side, bottom)]);

    /// <summary>
    /// A lever: a plate, and a stick that leans one way or the other — which is the whole reading.
    /// </summary>
    /// <param name="facing">The way it leans out of a wall, or -1 for one standing on the floor.</param>
    /// <remarks>
    /// The state is the LEAN, not a repaint: an off lever tips one way and a thrown one the other,
    /// which reads across a room the way a tile swap never would. The wall forms lean the stick up
    /// or down against the wall instead, on the torch's own one-rotation-per-facing arithmetic.
    /// </remarks>
    public static BlockModel Lever(ushort baseLayer, ushort stickLayer, int facing, bool on)
    {
        if (facing < 0)
        {
            return new BlockModel(
            [
                Box(new Vector3(5f, 0f, 4f), new Vector3(11f, 2f, 12f), baseLayer, baseLayer, baseLayer),
                RotatedBox(
                    new Vector3(7f, 2f, 7f), new Vector3(9f, 10f, 9f), stickLayer,
                    axis: 0, new Vector3(8f, 2f, 8f), on ? 35f : -35f),
            ]);
        }

        // Against a wall: the plate stands on it, and the stick tips up (on) or down (off) in the
        // plane the wall allows — about z for the two leaning along x, about x for the other two.
        // The sign that reads as "up" flips with which side of the cell the wall is on.
        var (plateFrom, plateTo, stickFrom, stickTo, pivot, axis) = facing switch
        {
            Faces.PosX => (new Vector3(0f, 4f, 5f), new Vector3(2f, 12f, 11f),
                new Vector3(2f, 7f, 7f), new Vector3(10f, 9f, 9f), new Vector3(2f, 8f, 8f), 2),
            Faces.NegX => (new Vector3(14f, 4f, 5f), new Vector3(16f, 12f, 11f),
                new Vector3(6f, 7f, 7f), new Vector3(14f, 9f, 9f), new Vector3(14f, 8f, 8f), 2),
            Faces.PosZ => (new Vector3(5f, 4f, 0f), new Vector3(11f, 12f, 2f),
                new Vector3(7f, 7f, 2f), new Vector3(9f, 9f, 10f), new Vector3(8f, 8f, 2f), 0),
            _ => (new Vector3(5f, 4f, 14f), new Vector3(11f, 12f, 16f),
                new Vector3(7f, 7f, 6f), new Vector3(9f, 9f, 14f), new Vector3(8f, 8f, 14f), 0),
        };

        var outward = facing is Faces.PosX or Faces.PosZ ? 1f : -1f;

        return new BlockModel(
        [
            Box(plateFrom, plateTo, baseLayer, baseLayer, baseLayer),
            RotatedBox(stickFrom, stickTo, stickLayer, axis, pivot, (on ? 30f : -30f) * outward),
        ]);
    }

    /// <summary>
    /// A rail: a film on the floor whose top face turns with the track, or climbs at forty-five.
    /// </summary>
    /// <param name="rotation">Quarter turns of the top tile — an elbow is the straight tile bent
    /// in the art, so each orientation is the one drawing turned.</param>
    /// <param name="climb">The face a climbing rail rises toward, or -1 for one lying flat.</param>
    /// <remarks>
    /// The climb is the crossed plant's own trick at a different angle: a flat sheet turned
    /// forty-five degrees about the cell's centre line, rescaled so its corners land on the cell
    /// bounds — the low edge on one floor seam, the high edge on the ceiling seam it hands over to.
    /// </remarks>
    public static BlockModel Rail(ushort layer, int rotation, int climb)
    {
        var faces = new ModelFace?[Faces.Count];
        faces[Faces.PosY] = new ModelFace { Layer = layer, Rotation = rotation };
        faces[Faces.NegY] = new ModelFace { Layer = layer, Rotation = rotation, CullFace = Faces.NegY };

        if (climb < 0)
        {
            return new BlockModel(
            [
                new ModelElement
                {
                    From = new Vector3(0f, 0.25f, 0f),
                    To = new Vector3(16f, 0.25f, 16f),
                    Faces = faces,
                },
            ]);
        }

        // Rising along x turns about z, and along z about x; the sign puts the high edge on the
        // named side. Rescale stretches the sheet's 16 units across the cell's diagonal.
        var (axis, angle) = climb switch
        {
            Faces.PosX => (2, -45f),
            Faces.NegX => (2, 45f),
            Faces.PosZ => (0, 45f),
            _ => (0, -45f),
        };

        return new BlockModel(
        [
            new ModelElement
            {
                From = new Vector3(0f, 8f, 0f),
                To = new Vector3(16f, 8f, 16f),
                Faces = faces,
                RotationAxis = axis,
                RotationAngle = angle,
                RotationOrigin = new Vector3(8f, 8f, 8f),
                Rescale = true,
            },
        ]);
    }

    /// <summary>A box turned about one axis, for the parts whose state is a lean.</summary>
    private static ModelElement RotatedBox(
        Vector3 from, Vector3 to, ushort layer, int axis, Vector3 pivot, float angle)
    {
        var faces = new ModelFace?[Faces.Count];
        for (var face = 0; face < Faces.Count; face++)
            faces[face] = new ModelFace { Layer = layer };

        return new ModelElement
        {
            From = from,
            To = to,
            Faces = faces,
            RotationAxis = axis,
            RotationOrigin = pivot,
            RotationAngle = angle,
        };
    }

    /// <summary>A button: a small block proud of its surface, and nearly flush while pressed.</summary>
    /// <param name="facing">The wall face it stands out of, or -1 for one on the floor.</param>
    public static BlockModel Button(ushort layer, int facing, bool pressed)
    {
        var depth = pressed ? 1f : 2f;

        var (from, to) = facing switch
        {
            Faces.PosX => (new Vector3(0f, 6f, 5f), new Vector3(depth, 10f, 11f)),
            Faces.NegX => (new Vector3(16f - depth, 6f, 5f), new Vector3(16f, 10f, 11f)),
            Faces.PosZ => (new Vector3(5f, 6f, 0f), new Vector3(11f, 10f, depth)),
            Faces.NegZ => (new Vector3(5f, 6f, 16f - depth), new Vector3(11f, 10f, 16f)),
            _ => (new Vector3(5f, 0f, 5f), new Vector3(11f, depth, 11f)),
        };

        return new BlockModel([Box(from, to, layer, layer, layer)]);
    }

    /// <summary>
    /// A logic gate: a slab-topped block one unit shy of full, its working face on top.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>One unit short on purpose, and it is not a style choice.</b> A full cube goes down the
    /// greedy path, where texture coordinates are derived from world position and every facing
    /// wears its tile the same way up — an arrow on top would lie for three facings out of four.
    /// One unit shy keeps it on the model path, where the top face carries its own rotation, and
    /// the shortfall reads as a working surface the way an anvil's silhouette reads as an anvil.
    /// </remarks>
    public static BlockModel Gate(ushort top, ushort side, int facing)
    {
        var faces = new ModelFace?[Blocks.Faces.Count];
        for (var face = 0; face < Blocks.Faces.Count; face++)
        {
            if (face == Blocks.Faces.PosY)
            {
                faces[face] = new ModelFace
                {
                    Layer = top,

                    // The tile is drawn pointing +z; the rotation turns it to the output.
                    Rotation = facing switch
                    {
                        Blocks.Faces.PosZ => 0,
                        Blocks.Faces.NegX => 90,
                        Blocks.Faces.NegZ => 180,
                        _ => 270,
                    },
                };
                continue;
            }

            // The bottom and the four sides run to the cell's own bounds, so each is culled by
            // its neighbour; the top sits a unit inside and nothing above can hide it.
            faces[face] = new ModelFace
            {
                Layer = side,
                CullFace = face,
            };
        }

        var element = new ModelElement
        {
            From = Vector3.Zero,
            To = new Vector3(16f, 15f, 16f),
            Faces = faces,
        };

        return new BlockModel([element]);
    }

    /// <summary>
    /// An anvil: a broad foot, a narrow waist, and a wide face to strike on.
    /// </summary>
    /// <param name="alongX">True when the face runs east-west rather than north-south.</param>
    /// <remarks>
    /// ⛳ <b>Three boxes, and the waist is the whole silhouette.</b> A block-shaped anvil is a block;
    /// what says anvil at ten paces is that it is pinched in the middle, so the foot and the face are
    /// wide and the thing between them is not. The face is longer than it is deep, which is what
    /// gives it an axis worth placing.
    /// <para>⚠ The stage of wear is carried on the TOP layer alone — the sides of a chipped anvil are
    /// the sides of a new one, which is what the format's single <c>anvil.png</c> is saying.</para>
    /// </remarks>
    public static BlockModel Anvil(ushort top, ushort side, bool alongX)
    {
        // Written along x and turned by swapping the two horizontal spans, rather than by rotating —
        // a rotation would carry the textures and the cull faces round with it and every one of
        // those is a chance to be a quarter turn out in a way that shows from one side only.
        static (Vector3 From, Vector3 To) Span(float x0, float x1, float y0, float y1, float z0, float z1, bool alongX) =>
            alongX
                ? (new Vector3(x0, y0, z0), new Vector3(x1, y1, z1))
                : (new Vector3(z0, y0, x0), new Vector3(z1, y1, x1));

        var foot = Span(2f, 14f, 0f, 4f, 3f, 13f, alongX);
        var waist = Span(5f, 11f, 4f, 10f, 6f, 10f, alongX);
        var face = Span(0f, 16f, 10f, 16f, 3f, 13f, alongX);

        return new BlockModel(
        [
            Box(foot.From, foot.To, top, side, side),
            Box(waist.From, waist.To, top, side, side),
            Box(face.From, face.To, top, side, side),
        ]);
    }

    /// <summary>
    /// A bin for rot: a floor, four walls, and whatever has been thrown in rising inside.
    /// </summary>
    /// <param name="stage">0 empty through 8 ready — how high the fill plate sits.</param>
    /// <remarks>
    /// ⚠ Hollow on purpose, so the fill level is read from above at a glance — a solid cube with a
    /// busier top texture would need walking up to. The fill is its own box rather than a repaint
    /// of the floor, which is what lets the level rise without another wall texture per stage.
    /// </remarks>
    public static BlockModel Composter(ushort side, ushort bottom, ushort fill, int stage)
    {
        var elements = new List<ModelElement>
        {
            Box(new Vector3(0f, 0f, 0f), new Vector3(16f, 2f, 16f), bottom, side, bottom),
            Box(new Vector3(0f, 0f, 0f), new Vector3(2f, 16f, 16f), side, side, side),
            Box(new Vector3(14f, 0f, 0f), new Vector3(16f, 16f, 16f), side, side, side),
            Box(new Vector3(2f, 0f, 0f), new Vector3(14f, 16f, 2f), side, side, side),
            Box(new Vector3(2f, 0f, 14f), new Vector3(14f, 16f, 16f), side, side, side),
        };

        if (stage > 0)
            elements.Add(Box(
                new Vector3(2f, 0f, 2f), new Vector3(14f, 2f + stage * 1.65f, 14f), fill, fill, fill));

        return new BlockModel([.. elements]);
    }

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
    public static BlockModel Torch(ushort layer) =>
        Stick(layer, new Vector3(7f, 0f, 7f), new Vector3(9f, 10f, 9f), Vector3.Zero, 1, 0f);

    /// <summary>
    /// A torch fixed to a wall, leaning out of it.
    /// </summary>
    /// <param name="facing">The way it leans, which is the face of the wall it came off.</param>
    /// <remarks>
    /// <para>Written per facing, the way <see cref="Stairs"/> is, and for a better reason than
    /// symmetry: the format expresses this as one model turned four times about y <em>on top of</em>
    /// a 22.5 degree lean about z, and <see cref="ModelElement"/> takes one axis. Turned into four
    /// facings by hand, each is a single rotation — about z for the two that lean along x, about x
    /// for the two that lean along z — so nothing needs composing at all. Six numbers a facing, and
    /// the part that is easy to get wrong is derived below rather than written out four times.</para>
    /// <para>The lean is 22.5 degrees, the same as the format's own wall torch, and the stick starts
    /// three units up the wall so the flame clears the block it is fixed to.</para>
    /// </remarks>
    public static BlockModel WallTorch(ushort layer, int facing)
    {
        var (from, to, pivot, axis, angle) = facing switch
        {
            Faces.PosX => (new Vector3(0f, 3f, 7f), new Vector3(2f, 13f, 9f), new Vector3(0f, 3f, 8f), 2, -22.5f),
            Faces.NegX => (new Vector3(14f, 3f, 7f), new Vector3(16f, 13f, 9f), new Vector3(16f, 3f, 8f), 2, 22.5f),
            Faces.PosZ => (new Vector3(7f, 3f, 0f), new Vector3(9f, 13f, 2f), new Vector3(8f, 3f, 0f), 0, 22.5f),
            _ => (new Vector3(7f, 3f, 14f), new Vector3(9f, 13f, 16f), new Vector3(8f, 3f, 16f), 0, -22.5f),
        };

        return Stick(layer, from, to, pivot, axis, angle);
    }

    /// <summary>
    /// A two-unit post drawn as two crossed full-width planes, with a cap on top of it.
    /// </summary>
    /// <remarks>
    /// <para>The planes are wider than the post they draw. That is not a mistake in the format and it
    /// is what makes a torch read at a distance: two 2-unit-wide boxes would be nearly invisible edge
    /// on, so the model stretches the tile across a whole cell's worth of plane and lets the
    /// transparent margin do the shaping. The cap reads a 2x2 patch out of the middle of the tile,
    /// which is the one place an explicit <c>uv</c> is doing real work rather than restating the
    /// default.</para>
    /// <para><b>The planes are placed from the post rather than written down beside it.</b> Every
    /// side face here reads its texture with u running along the plane's own axis from its minimum
    /// and v running down from its top — which means the tile's stick pixels land on the post only
    /// if the plane starts seven units before it and sixteen units of it are spanned. Writing that
    /// out per facing is how a wall torch ends up with its flame a few pixels off the stick in one
    /// direction out of four, so it is arithmetic here instead.</para>
    /// </remarks>
    private static BlockModel Stick(
        ushort layer, Vector3 from, Vector3 to, Vector3 pivot, int axis, float angle)
    {
        const float Tile = 16f;

        // Where the tile draws the stick: two columns in from the left, and the bottom ten rows.
        const float StickU = 7f;

        var cap = new ModelFace?[Faces.Count];
        cap[Faces.PosY] = new ModelFace { Layer = layer, Uv = new Vector4(7f, 6f, 9f, 8f) };
        cap[Faces.NegY] = new ModelFace { Layer = layer, Uv = new Vector4(7f, 13f, 9f, 15f) };

        var alongX = new ModelFace?[Faces.Count];
        alongX[Faces.NegX] = new ModelFace { Layer = layer, Uv = new Vector4(0f, 0f, 16f, 16f) };
        alongX[Faces.PosX] = new ModelFace { Layer = layer, Uv = new Vector4(16f, 0f, 0f, 16f) };

        var alongZ = new ModelFace?[Faces.Count];
        alongZ[Faces.NegZ] = new ModelFace { Layer = layer, Uv = new Vector4(16f, 0f, 0f, 16f) };
        alongZ[Faces.PosZ] = new ModelFace { Layer = layer, Uv = new Vector4(0f, 0f, 16f, 16f) };

        ModelElement Turned(Vector3 lo, Vector3 hi, ModelFace?[] faces) => new()
        {
            From = lo,
            To = hi,
            Faces = faces,
            Shade = false,
            AmbientOcclusion = false,
            RotationAngle = angle,
            RotationAxis = axis,
            RotationOrigin = pivot,
        };

        var model = new BlockModel(
        [
            Turned(from, to, cap),

            // Faces along x: u runs with z, so the plane starts seven units before the post in z.
            Turned(
                new Vector3(from.X, from.Y, from.Z - StickU),
                new Vector3(to.X, from.Y + Tile, from.Z - StickU + Tile),
                alongX),

            // Faces along z: u runs with x, so the same offset applies in x instead.
            Turned(
                new Vector3(from.X - StickU, from.Y, from.Z),
                new Vector3(from.X - StickU + Tile, from.Y + Tile, to.Z),
                alongZ),
        ]);

        // The stick, not the planes that draw it — and the stick where it ends up, not where it
        // was written, or a leaning torch is outlined as if it were standing upright.
        model.Outline = Bounds(Turned(from, to, cap));

        // Nothing to walk into. The planes that draw the stick are a cell wide and reach outside the
        // block, so left to the elements a torch would be a wall — and a torch is a thing you walk
        // through in every game that has one.
        model.Collision = [];
        return model;
    }

    /// <summary>
    /// The element boxes a body can walk into, cleaned up.
    /// </summary>
    /// <remarks>
    /// <para><b>Clamped into the cell</b>, which is load bearing rather than tidy: a torch draws its
    /// stick with planes that start seven units <em>before</em> the block, and a collision box
    /// hanging outside its own cell is one that a scan over the cells a body overlaps would step
    /// straight past.</para>
    /// <para><b>Planes are dropped and duplicates collapse.</b> A tuft is two crossed sheets with no
    /// volume; a grass block is the same cube written twice, once for the overlay.</para>
    /// <para>The fallback is the outline rather than a full cube, so a shape made entirely of planes
    /// that somebody marks solid gets the box it is drawn in rather than the whole cell.</para>
    /// </remarks>
    private static (Vector3 Min, Vector3 Max)[] BuildCollision(
        IReadOnlyList<ModelElement> elements, (Vector3 Min, Vector3 Max) outline)
    {
        var boxes = new List<(Vector3 Min, Vector3 Max)>(elements.Count);

        foreach (var element in elements)
        {
            var box = Clamped(Bounds(element));
            if (Flat(box)) continue;
            if (!boxes.Contains(box)) boxes.Add(box);
        }

        if (boxes.Count > 0) return [.. boxes];

        var only = Clamped(outline);
        return Flat(only) ? [] : [only];
    }

    /// <summary>
    /// The boxes a slot draws, each carrying the three faces of it that are seen, nearest last.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>A face that this element does not draw falls back to one that it does</b> rather than
    /// being left blank. A slab has no side texture declared separately from its top on most of our
    /// materials, and an icon with a hole where a face should be reads as a modelling fault; the
    /// same tile on two faces at two shades reads as a block.
    /// </remarks>
    private static IconBox[] BuildIcon(IReadOnlyList<ModelElement> elements)
    {
        var boxes = new List<IconBox>(elements.Count);

        foreach (var element in elements)
        {
            var box = Clamped(Bounds(element));
            if (Flat(box)) continue;

            var top = element.Faces[Blocks.Faces.PosY]?.Layer ?? NoLayer;
            var left = element.Faces[Blocks.Faces.PosZ]?.Layer ?? NoLayer;
            var right = element.Faces[Blocks.Faces.PosX]?.Layer ?? NoLayer;

            // Whatever this element does draw, for the faces it does not.
            var any = NoLayer;
            foreach (var face in element.Faces)
                if (face is not null) { any = face.Layer; break; }

            if (any == NoLayer) continue;

            boxes.Add(new IconBox(
                box.Min, box.Max,
                top == NoLayer ? any : top,
                left == NoLayer ? any : left,
                right == NoLayer ? any : right));
        }

        // Nearest last. The viewer is off the +x +y +z corner, so a box is nearer the further its
        // own near corner is along all three — and the three faces drawn are the three facing that
        // corner, which is what makes a straight painter's sort correct here.
        boxes.Sort((a, b) =>
            (a.Min.X + a.Min.Y + a.Min.Z).CompareTo(b.Min.X + b.Min.Y + b.Min.Z));

        return [.. boxes];
    }

    private static (Vector3 Min, Vector3 Max) Clamped((Vector3 Min, Vector3 Max) box) =>
        (Vector3.Clamp(box.Min, Vector3.Zero, Vector3.One),
         Vector3.Clamp(box.Max, Vector3.Zero, Vector3.One));

    /// <summary>True when a box has no thickness in some direction, so there is nothing to hit.</summary>
    private static bool Flat((Vector3 Min, Vector3 Max) box)
    {
        var size = box.Max - box.Min;
        return size.X < 1e-4f || size.Y < 1e-4f || size.Z < 1e-4f;
    }

    /// <summary>Where one element's box actually ends up, in block units.</summary>
    private static (Vector3 Min, Vector3 Max) Bounds(ModelElement element)
    {
        var transform = BuildTransform(element);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (var corner = 0; corner < 8; corner++)
        {
            var p = Vector3.Transform(
                new Vector3(
                    (corner & 1) == 0 ? element.From.X : element.To.X,
                    (corner & 2) == 0 ? element.From.Y : element.To.Y,
                    (corner & 4) == 0 ? element.From.Z : element.To.Z),
                transform) / 16f;

            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return (min, max);
    }

    /// <summary>
    /// A small box hung in the middle of the cell, standing on the floor or under the ceiling.
    /// </summary>
    /// <param name="hanging">True for the form that hangs, which draws a chain up to the ceiling.</param>
    /// <remarks>
    /// Every face reads the whole tile rather than the strip its own extent would project onto, and
    /// that is the deliberate choice here. A pack's lantern is one texture holding a body and a
    /// chain in a layout only its own model knows, so a face that reads part of it reads whichever
    /// part that model happened to put there; a face that reads all of it is a small picture of a
    /// lantern, which is wrong by a little in every pack rather than wrong entirely in most.
    /// </remarks>
    public static BlockModel Lantern(ushort layer, bool hanging)
    {
        var body = new ModelFace?[Faces.Count];
        for (var face = 0; face < Faces.Count; face++)
            body[face] = new ModelFace { Layer = layer, Uv = new Vector4(0f, 0f, 16f, 16f) };

        var low = hanging ? 6f : 0f;
        var elements = new List<ModelElement>(2)
        {
            new()
            {
                From = new Vector3(5f, low, 5f),
                To = new Vector3(11f, low + 7f, 11f),
                Faces = body,
            },
        };

        if (hanging)
        {
            // Two units of chain, reading the patch at the top of the tile where a lantern's
            // hanging gear is drawn in every layout there is.
            var chain = new ModelFace?[Faces.Count];
            for (var face = 0; face < Faces.Count; face++)
            {
                if (face is Faces.PosY or Faces.NegY) continue;
                chain[face] = new ModelFace { Layer = layer, Uv = new Vector4(7f, 0f, 9f, 2f) };
            }

            elements.Add(new ModelElement
            {
                From = new Vector3(7f, 13f, 7f),
                To = new Vector3(9f, 16f, 9f),
                Faces = chain,
                Shade = false,
                AmbientOcclusion = false,
            });
        }

        var model = new BlockModel(elements);
        model.Outline = (new Vector3(5f, low, 5f) / 16f, new Vector3(11f, low + 7f, 11f) / 16f);
        return model;
    }

    /// <summary>
    /// Four logs laid in a square, with a fire standing in them when it is alight.
    /// </summary>
    /// <param name="facing">Which way the lower pair of logs runs.</param>
    /// <remarks>
    /// <para>The logs are our own timber rather than a texture drawn for a campfire, and that is the
    /// better answer twice over: a fire built out of the logs a player chopped reads as one, and a
    /// box sixteen long by four tall reading a strip of bark is what the natural projection already
    /// does correctly. A campfire-specific tile would be a layout only somebody else's model knows,
    /// with a bite taken out of it that would show through as a hole in ours.</para>
    /// <para>The fire is two crossed planes rather than one, so it reads from every side, and it is
    /// lit flat because a flame does not take the shading of the direction it happens to face.</para>
    /// </remarks>
    public static BlockModel Campfire(ushort logSide, ushort logTop, ushort fire, int facing, bool lit)
    {
        var alongX = facing is Faces.PosX or Faces.NegX;

        // The lower pair, then the upper pair crossing them.
        var elements = new List<ModelElement>(6)
        {
            alongX
                ? Box(new Vector3(0f, 0f, 1f), new Vector3(16f, 4f, 5f), logTop, logSide, logTop)
                : Box(new Vector3(1f, 0f, 0f), new Vector3(5f, 4f, 16f), logTop, logSide, logTop),
            alongX
                ? Box(new Vector3(0f, 0f, 11f), new Vector3(16f, 4f, 15f), logTop, logSide, logTop)
                : Box(new Vector3(11f, 0f, 0f), new Vector3(15f, 4f, 16f), logTop, logSide, logTop),
            alongX
                ? Box(new Vector3(1f, 4f, 0f), new Vector3(5f, 8f, 16f), logTop, logSide, logTop)
                : Box(new Vector3(0f, 4f, 1f), new Vector3(16f, 8f, 5f), logTop, logSide, logTop),
            alongX
                ? Box(new Vector3(11f, 4f, 0f), new Vector3(15f, 8f, 16f), logTop, logSide, logTop)
                : Box(new Vector3(0f, 4f, 11f), new Vector3(16f, 8f, 15f), logTop, logSide, logTop),
        };

        if (lit)
        {
            var acrossZ = new ModelFace?[Faces.Count];
            acrossZ[Faces.NegZ] = new ModelFace { Layer = fire, Uv = new Vector4(16f, 0f, 0f, 16f) };
            acrossZ[Faces.PosZ] = new ModelFace { Layer = fire, Uv = new Vector4(0f, 0f, 16f, 16f) };

            var acrossX = new ModelFace?[Faces.Count];
            acrossX[Faces.NegX] = new ModelFace { Layer = fire, Uv = new Vector4(0f, 0f, 16f, 16f) };
            acrossX[Faces.PosX] = new ModelFace { Layer = fire, Uv = new Vector4(16f, 0f, 0f, 16f) };

            elements.Add(new ModelElement
            {
                From = new Vector3(1f, 4f, 8f), To = new Vector3(15f, 14f, 8f),
                Faces = acrossZ, Shade = false, AmbientOcclusion = false,
            });
            elements.Add(new ModelElement
            {
                From = new Vector3(8f, 4f, 1f), To = new Vector3(8f, 14f, 15f),
                Faces = acrossX, Shade = false, AmbientOcclusion = false,
            });
        }

        var model = new BlockModel(elements);

        // The logs, not the flame standing in them, so the outline is something to aim at.
        model.Outline = (Vector3.Zero, new Vector3(1f, 0.5f, 1f));

        // ⚠ One box across the whole cell, not the four logs. The logs leave a square hole in the
        // middle, and a body that fits through it would stand inside the fire with its feet on the
        // floor — which is both wrong to look at and the one place a campfire must not be safe.
        model.Collision = [(Vector3.Zero, new Vector3(1f, 0.5f, 1f))];
        return model;
    }

    /// <summary>
    /// A box standing a little clear of its cell on all four sides, with a front — a chest.
    /// </summary>
    /// <remarks>
    /// One unit in and two down from the top, which is the shape the genre uses and is the reason a
    /// row of chests reads as a row of chests rather than as a plank wall: the gaps between them are
    /// what draw the outline of each. That also means it can never be a full cube, so it cannot hide
    /// what is behind it — and no face is flush, so nothing culls any of them.
    /// </remarks>
    public static BlockModel Chest(ushort top, ushort side, ushort front, int facing)
    {
        var faces = new ModelFace?[Faces.Count];
        for (var face = 0; face < Faces.Count; face++)
        {
            var layer = face == facing
                ? front
                : face is Faces.PosY or Faces.NegY ? top : side;

            faces[face] = new ModelFace { Layer = layer, Uv = new Vector4(0f, 0f, 16f, 16f) };
        }

        return new BlockModel(
        [
            new ModelElement
            {
                From = new Vector3(1f, 0f, 1f),
                To = new Vector3(15f, 14f, 15f),
                Faces = faces,
            },
        ]);
    }

    /// <summary>
    /// A flat sheet fixed to a wall, drawn on both sides — a ladder.
    /// </summary>
    /// <param name="facing">The way it faces, which is out of the wall it is fixed to.</param>
    /// <remarks>
    /// A plane rather than a thin box, because the whole texture is a cut-out and a box would draw
    /// four edge strips of rung and gap. Standing two units off the wall is what stops it fighting
    /// with the wall's own face for the same depth.
    /// </remarks>
    public static BlockModel Sheet(ushort layer, int facing)
    {
        const float Off = 2f;

        var faces = new ModelFace?[Faces.Count];

        // Both uv rects together are what makes u run the same way along the cell from either side,
        // which is the same cancellation the torch's planes rely on: the mirrored projection and the
        // mirrored rect undo each other.
        var (from, to, near, far) = facing switch
        {
            Faces.PosX => (new Vector3(Off, 0f, 0f), new Vector3(Off, 16f, 16f), Faces.PosX, Faces.NegX),
            Faces.NegX => (new Vector3(16f - Off, 0f, 0f), new Vector3(16f - Off, 16f, 16f), Faces.NegX, Faces.PosX),
            Faces.PosZ => (new Vector3(0f, 0f, Off), new Vector3(16f, 16f, Off), Faces.PosZ, Faces.NegZ),
            _ => (new Vector3(0f, 0f, 16f - Off), new Vector3(16f, 16f, 16f - Off), Faces.NegZ, Faces.PosZ),
        };

        faces[near] = new ModelFace { Layer = layer, Uv = new Vector4(0f, 0f, 16f, 16f) };
        faces[far] = new ModelFace { Layer = layer, Uv = new Vector4(16f, 0f, 0f, 16f) };

        var model = new BlockModel(
        [
            new ModelElement { From = from, To = to, Faces = faces, AmbientOcclusion = false },
        ]);

        // A sheet with no thickness has nothing to aim at, so the outline is given a little.
        var (dx, dy, dz) = Faces.Normals[facing];
        var lift = new Vector3(dx, dy, dz) * (Off / 16f);
        model.Outline = (Vector3.Min(from / 16f, from / 16f + lift), Vector3.Max(to / 16f, to / 16f + lift));
        return model;
    }

    /// <summary>
    /// A panel three units thick, lying flat in one half of the cell or stood up on one edge.
    /// </summary>
    /// <param name="facing">Which way it faces when it is open, hinged on the opposite side.</param>
    /// <param name="upper">True for one fitted to the ceiling rather than the floor.</param>
    /// <param name="open">True for the form stood up against its hinge.</param>
    public static BlockModel Trapdoor(ushort layer, int facing, bool upper, bool open)
    {
        const float Thick = 3f;

        if (!open)
            return new BlockModel([upper
                ? Box(new Vector3(0f, 16f - Thick, 0f), new Vector3(16f, 16f, 16f), layer, layer, layer)
                : Box(Vector3.Zero, new Vector3(16f, Thick, 16f), layer, layer, layer)]);

        // Swung up about its hinge, which is the edge opposite the way it faces.
        var (from, to) = facing switch
        {
            Faces.PosX => (Vector3.Zero, new Vector3(Thick, 16f, 16f)),
            Faces.NegX => (new Vector3(16f - Thick, 0f, 0f), new Vector3(16f, 16f, 16f)),
            Faces.PosZ => (Vector3.Zero, new Vector3(16f, 16f, Thick)),
            _ => (new Vector3(0f, 0f, 16f - Thick), new Vector3(16f, 16f, 16f)),
        };

        return new BlockModel([Box(from, to, layer, layer, layer)]);
    }

    /// <summary>
    /// Half a door: a panel on one edge of the cell, or swung a quarter turn onto the next edge.
    /// </summary>
    /// <param name="facing">The way the shut door faces — out of the wall it was hung against.</param>
    /// <param name="hinge">The side the hinge is on, as a face; the panel swings toward it.</param>
    /// <param name="open">True for the swung form.</param>
    /// <remarks>
    /// <para>Both forms are boxes on cell edges rather than one box turned, which is what
    /// <see cref="Stairs"/> does and for the same reason: a quarter turn about a vertical edge takes
    /// one edge of the cell exactly onto another, so the turned form can be written down instead of
    /// computed, and no texture ends up rotated by a rounding error.</para>
    /// <para>Which edge is not free to choose. Shut, the panel is on the side the door faces; open,
    /// it has swung onto the hinge's side — so a door hung on its left opens to the left, and the
    /// pair of them hung on facing sides of a doorway open outward together.</para>
    /// </remarks>
    public static BlockModel Door(ushort layer, int facing, int hinge, bool open)
    {
        const float Thick = 3f;

        var edge = open ? hinge : facing;
        var (from, to) = edge switch
        {
            Faces.PosX => (new Vector3(16f - Thick, 0f, 0f), new Vector3(16f, 16f, 16f)),
            Faces.NegX => (Vector3.Zero, new Vector3(Thick, 16f, 16f)),
            Faces.PosZ => (new Vector3(0f, 0f, 16f - Thick), new Vector3(16f, 16f, 16f)),
            _ => (Vector3.Zero, new Vector3(16f, 16f, Thick)),
        };

        return new BlockModel([Box(from, to, layer, layer, layer)]);
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
