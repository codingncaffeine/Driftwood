namespace Driftwood.Core.Items;

/// <summary>One corner of an extruded item sprite, in the item's own space.</summary>
/// <param name="Face">
/// Which of <see cref="Blocks.Faces"/>'s six directions this corner belongs to. Only the shading
/// reads it — every face of a sprite wears the same picture — but it is the same field the cube
/// path uses, so both meshes go through one vertex format and one shader.
/// </param>
public readonly record struct SpriteVertex(
    float X, float Y, float Z, float U, float V, int Face);

/// <summary>
/// Turns a flat item icon into a solid: the picture on the front and the back, and a wall of its own
/// colour round every edge of the silhouette.
/// </summary>
/// <remarks>
/// <para>⛔ <b>From the user, holding a pickaxe:</b> <i>"weapons and tools look like they're being
/// shown twice"</i>. They were. Everything not drawn as a block was drawn as the <em>cube</em> with
/// its icon on all six faces, and since the tools were redrawn to keep their ink off the tile border
/// the four side faces became fully transparent — so what was left was the front face and the back
/// face of a cube, both showing the picture, <b>held two thirds of a block apart</b>. Two pickaxes,
/// one behind the other.</para>
/// <para>Thinning that cube to a wafer would hide the doubling and leave the real problem: a picture
/// with no substance vanishes exactly edge-on, which happens twice through every swing. So the
/// answer is the genre's own — <b>extrude the sprite</b>. The silhouette is walked, and every step
/// where an opaque square meets a transparent one gets a quad spanning the thickness, wearing the
/// colour of the square it came off. What comes out is one object with a visible edge, from any
/// angle, and it is the same mesh whether it is in a fist, on the floor or in flight.</para>
/// <para><b>The mask is always sixteen squares</b> whatever the texture is. A 512-pixel pack would
/// otherwise extrude a quarter of a million edges for one axe, and the extra detail would be
/// invisible: the thickness is a sixteenth, so an edge finer than a sixteenth is finer than the
/// object is deep. Downsampling to the grid the art was designed on is what keeps this bounded and
/// keeps a pack's tool the same shape as ours.</para>
/// </remarks>
public static class ItemSprite
{
    /// <summary>Squares across the silhouette mask, whatever resolution the texture is.</summary>
    public const int Grid = 16;

    /// <summary>
    /// How thick a sprite is, as a share of its width. Two sixteenths — the format's own answer for
    /// an extruded item, and already what a dropped one used.
    /// </summary>
    public const float Thickness = 2f / 16f;

    /// <summary>
    /// Alpha at or above this counts as ink. The same threshold the fragment shader discards at, so
    /// the edge is grown round exactly the pixels that will be drawn.
    /// </summary>
    private const byte Ink = 128;

    /// <summary>
    /// Reduces a tile of any size to a <see cref="Grid"/>-square coverage mask.
    /// </summary>
    /// <remarks>
    /// A square counts as ink when at least a third of the texels under it are. A majority rule
    /// erodes a one-pixel outline away at high resolutions — which is the whole silhouette of a
    /// tool's haft — and "any texel at all" grows a halo round every anti-aliased edge a pack ships.
    /// </remarks>
    public static bool[] Mask(byte[] tile, int size)
    {
        var mask = new bool[Grid * Grid];
        if (size <= 0 || tile.Length < size * size * 4) return mask;

        // How many texels of the source fall under one square of the mask. Never less than one, so
        // a tile smaller than the grid still answers (each square samples one texel, repeatedly).
        var step = MathF.Max(1f, size / (float)Grid);

        for (var gy = 0; gy < Grid; gy++)
        for (var gx = 0; gx < Grid; gx++)
        {
            var x0 = (int)(gx * step);
            var y0 = (int)(gy * step);
            var x1 = Math.Min(size, Math.Max(x0 + 1, (int)((gx + 1) * step)));
            var y1 = Math.Min(size, Math.Max(y0 + 1, (int)((gy + 1) * step)));

            var covered = 0;
            var total = 0;

            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                total++;
                if (tile[(y * size + x) * 4 + 3] >= Ink) covered++;
            }

            mask[gy * Grid + gx] = total > 0 && covered * 3 >= total;
        }

        return mask;
    }

    /// <summary>True when a mask square is inside the tile and inked.</summary>
    private static bool At(bool[] mask, int x, int y) =>
        x >= 0 && y >= 0 && x < Grid && y < Grid && mask[y * Grid + x];

    /// <summary>
    /// Where a wall reads its colour from: one square <em>in</em> from the edge it stands on.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Measured, after the first extrusion came out looking like a charred stick.</b> Every
    /// tool tile carries a one-pixel dark line all the way round — deliberately, because it is what
    /// holds the shape together in a 16-pixel slot — so a wall that wears the square it stands on
    /// wears the <em>outline</em>, on every side, all the way round. On a haft two squares wide with
    /// a line down each side, that is a black object with a brown thread in it.
    /// <para>One step in is exactly one outline thick, so the wall comes out iron on an iron head
    /// and timber on a haft. Falls back to the edge square itself where there is nothing behind it
    /// — a feature one square wide has no inside — which is the old behaviour for the few pixels
    /// that genuinely have no material to borrow.</para>
    /// </remarks>
    private static (float U, float V) WallUv(bool[] mask, int gx, int gy, int dx, int dy)
    {
        const float Cell = 1f / Grid;

        var ix = gx - dx;
        var iy = gy - dy;
        if (!At(mask, ix, iy)) (ix, iy) = (gx, gy);

        return ((ix + 0.5f) * Cell, (iy + 0.5f) * Cell);
    }

    /// <summary>
    /// Builds the solid for one mask: front, back, and one quad per exposed edge of the silhouette.
    /// </summary>
    /// <remarks>
    /// <para>Item space is the same box the cube path uses — half a unit either side of the middle on
    /// x and y — flattened on z to <see cref="Thickness"/>. <b>+y is up and v runs down</b>, which is
    /// the picture the right way up: a tool's head is drawn in the top right of its tile and has to
    /// come out in the top right of the object.</para>
    /// <para>The front and the back are each one quad over the whole tile rather than one per inked
    /// square. The shader already discards transparent texels, so the silhouette costs nothing to
    /// draw and the mask is needed only to know where the <em>edges</em> are.</para>
    /// <para>An edge quad takes its texture from the middle of the square it stands on, so the wall
    /// round a wooden haft is the colour of that haft and the wall round an iron head is iron.</para>
    /// </remarks>
    public static void Build(bool[] mask, List<SpriteVertex> vertices, List<uint> indices)
    {
        const float Half = 0.5f;
        var t = Thickness * 0.5f;
        const float Cell = 1f / Grid;

        void Quad(int face, (float X, float Y, float Z, float U, float V)[] corners)
        {
            var first = (uint)vertices.Count;
            foreach (var c in corners) vertices.Add(new SpriteVertex(c.X, c.Y, c.Z, c.U, c.V, face));
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        // The picture, twice. Wound anticlockwise from outside so back-face culling — if anything
        // ever turns it on for this pass — keeps the face that is meant to be seen.
        Quad(Blocks.Faces.PosZ,
        [
            (-Half, -Half, t, 0f, 1f),
            (Half, -Half, t, 1f, 1f),
            (Half, Half, t, 1f, 0f),
            (-Half, Half, t, 0f, 0f),
        ]);

        Quad(Blocks.Faces.NegZ,
        [
            (Half, -Half, -t, 1f, 1f),
            (-Half, -Half, -t, 0f, 1f),
            (-Half, Half, -t, 0f, 0f),
            (Half, Half, -t, 1f, 0f),
        ]);

        for (var gy = 0; gy < Grid; gy++)
        for (var gx = 0; gx < Grid; gx++)
        {
            if (!mask[gy * Grid + gx]) continue;

            var left = -Half + gx * Cell;
            var right = left + Cell;
            var top = Half - gy * Cell;
            var bottom = top - Cell;

            if (!At(mask, gx - 1, gy))
            {
                var (u, v) = WallUv(mask, gx, gy, -1, 0);
                Quad(Blocks.Faces.NegX,
                [
                    (left, bottom, -t, u, v),
                    (left, bottom, t, u, v),
                    (left, top, t, u, v),
                    (left, top, -t, u, v),
                ]);
            }

            if (!At(mask, gx + 1, gy))
            {
                var (u, v) = WallUv(mask, gx, gy, 1, 0);
                Quad(Blocks.Faces.PosX,
                [
                    (right, bottom, t, u, v),
                    (right, bottom, -t, u, v),
                    (right, top, -t, u, v),
                    (right, top, t, u, v),
                ]);
            }

            if (!At(mask, gx, gy - 1))
            {
                var (u, v) = WallUv(mask, gx, gy, 0, -1);
                Quad(Blocks.Faces.PosY,
                [
                    (left, top, t, u, v),
                    (right, top, t, u, v),
                    (right, top, -t, u, v),
                    (left, top, -t, u, v),
                ]);
            }

            if (!At(mask, gx, gy + 1))
            {
                var (u, v) = WallUv(mask, gx, gy, 0, 1);
                Quad(Blocks.Faces.NegY,
                [
                    (left, bottom, -t, u, v),
                    (right, bottom, -t, u, v),
                    (right, bottom, t, u, v),
                    (left, bottom, t, u, v),
                ]);
            }
        }
    }

    /// <summary>
    /// Where the fingers close on this particular picture, in the sprite's own space.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>Not a constant, and the torch is why.</b> A tool is drawn corner to corner with
    /// its haft running into the bottom-left of its tile, so gripping the tile's bottom-left corner
    /// grips the haft. A torch is a short upright stick in the <em>middle</em> of an otherwise empty
    /// tile, and the same constant grips a transparent corner — which puts the torch floating up and
    /// to the right of a fist that is holding nothing.</para>
    /// <para>So the hold is measured off the ink: a fifth of the way in from its left edge and a
    /// tenth up from its bottom. On a tool that is the haft; on a torch it is the bottom of the
    /// stick; on a lump it is the underside. One rule, and every flat thing in the game sits in the
    /// hand rather than beside it.</para>
    /// </remarks>
    public static System.Numerics.Vector3 Hold(bool[] mask)
    {
        int minX = Grid, maxX = -1, minY = Grid, maxY = -1;

        for (var y = 0; y < Grid; y++)
        for (var x = 0; x < Grid; x++)
        {
            if (!mask[y * Grid + x]) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        // Nothing drawn: hold the middle, which is where nothing is.
        if (maxX < 0) return System.Numerics.Vector3.Zero;

        var wantX = minX + (maxX - minX) * 0.20f;
        var wantY = maxY - (maxY - minY) * 0.10f;

        // ⛔ AND THEN SNAPPED ONTO THE INK, which the audit caught it not being. A share of the
        // bounding box is a point in a RECTANGLE, and a tool is a diagonal that fills about a third
        // of one — a fifth in from the left and a tenth up from the bottom of a pickaxe's box lands
        // in clear tile, a square and a bit off the haft. Near enough to look almost right, which is
        // the worst kind of wrong. Nearest inked square is the fix and it is what was meant all
        // along: hold the low end of the thing, wherever the thing actually is.
        var bestX = minX;
        var bestY = maxY;
        var best = float.MaxValue;

        for (var y = 0; y < Grid; y++)
        for (var x = 0; x < Grid; x++)
        {
            if (!mask[y * Grid + x]) continue;

            var dx = x - wantX;
            var dy = y - wantY;
            var d = dx * dx + dy * dy;
            if (d >= best) continue;

            best = d;
            bestX = x;
            bestY = y;
        }

        // Grid squares to the sprite's own box: x runs right, y runs UP while the grid runs down.
        return new System.Numerics.Vector3(
            -0.5f + (bestX + 0.5f) / Grid,
            0.5f - (bestY + 0.5f) / Grid,
            0f);
    }

    /// <summary>
    /// Checks a built sprite is a solid and not a pair of pictures.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The fault this exists for passed every count.</b> A cube wearing an icon on six faces has
    /// more geometry than this does, draws without error and looks right in a still slot; it is only
    /// wrong in the hand, where two of its faces are visibly two objects. So the claim has to be
    /// about the <em>silhouette</em>: an extruded sprite has a wall wherever its ink stops, and a
    /// count of walls against a count of boundary steps is a claim a cube cannot pass — a cube has
    /// four side quads whatever is drawn on it.
    /// </remarks>
    public static List<string> Validate(bool[] mask, string label)
    {
        var faults = new List<string>();

        var vertices = new List<SpriteVertex>();
        var indices = new List<uint>();
        Build(mask, vertices, indices);

        // Every step of the boundary, counted independently of the builder.
        var boundary = 0;
        var inked = 0;
        for (var y = 0; y < Grid; y++)
        for (var x = 0; x < Grid; x++)
        {
            if (!At(mask, x, y)) continue;
            inked++;
            if (!At(mask, x - 1, y)) boundary++;
            if (!At(mask, x + 1, y)) boundary++;
            if (!At(mask, x, y - 1)) boundary++;
            if (!At(mask, x, y + 1)) boundary++;
        }

        if (inked == 0)
        {
            faults.Add($"{label}: the mask is empty, so there is no sprite to hold");
            return faults;
        }

        var quads = vertices.Count / 4;
        if (quads != boundary + 2)
            faults.Add($"{label}: {quads} quads for {boundary} edges of silhouette plus a front and a back");

        // And it has to be thin. A sprite as deep as it is wide is the cube this replaced.
        var depth = 0f;
        var width = 0f;
        foreach (var v in vertices)
        {
            depth = MathF.Max(depth, MathF.Abs(v.Z) * 2f);
            width = MathF.Max(width, MathF.Abs(v.X) * 2f);
        }

        if (depth > width * 0.25f)
            faults.Add($"{label}: {depth:F3} deep against {width:F3} wide, which is a box rather than a sprite");

        return faults;
    }
}
