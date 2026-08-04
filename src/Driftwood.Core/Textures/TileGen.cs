namespace Driftwood.Core.Textures;

/// <summary>
/// Draws Driftwood's own block textures in code, one 16x16 RGBA tile per layer.
/// </summary>
/// <remarks>
/// <para>Generated rather than painted, for now, because the alternative was shipping a flat colour
/// per block — and a flat colour hides everything the renderer does. Ambient occlusion, baked light,
/// the difference between a merged quad and four separate ones: none of it reads until there is
/// detail on the surface to read it against.</para>
/// <para>They are also unambiguously ours, which the whole project depends on. An imported texture
/// pack layers over this set rather than replacing a hole in it, and a pack that only reskins half
/// the blocks still leaves a complete world.</para>
/// <para>Deterministic from fixed seeds: the same build always draws the same tiles, so a texture
/// change is a code change and shows up in review rather than drifting.</para>
/// </remarks>
public static class TileGen
{
    public const int Size = 16;
    public const int Stride = Size * 4;
    public const int BytesPerTile = Size * Size * 4;

    /// <summary>Builds every layer's pixels, indexed by layer number.</summary>
    public static byte[][] BuildAll(int layerCount)
    {
        var tiles = new byte[layerCount][];
        for (var i = 0; i < layerCount; i++) tiles[i] = Solid(255, 0, 255, 255);   // loud, so a gap shows
        return tiles;
    }

    public static byte[] Solid(byte r, byte g, byte b, byte a)
    {
        var t = new byte[BytesPerTile];
        for (var i = 0; i < BytesPerTile; i += 4)
        {
            t[i] = r; t[i + 1] = g; t[i + 2] = b; t[i + 3] = a;
        }
        return t;
    }

    /// <summary>Flat colour roughened by per-pixel value noise. The backbone of most tiles.</summary>
    public static byte[] Speckle(int seed, byte r, byte g, byte b, int spread, float coarseness = 0f)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var n = Noise(x, y, seed) * 2f - 1f;

            // A second, blockier octave gives grain a sense of scale — without it every material
            // is the same television static in a different colour.
            if (coarseness > 0f)
                n = n * (1f - coarseness) + (Noise(x >> 2, y >> 2, seed + 7919) * 2f - 1f) * coarseness;

            var d = (int)(n * spread);
            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        return t;
    }

    /// <summary>Speckle plus scattered blobs of a second colour, for ore in rock.</summary>
    public static byte[] Ore(int seed, byte[] baseTile, byte r, byte g, byte b, int blobs)
    {
        var t = (byte[])baseTile.Clone();

        for (var i = 0; i < blobs; i++)
        {
            var cx = (int)(Noise(i, 0, seed) * Size);
            var cy = (int)(Noise(0, i, seed + 31) * Size);
            var radius = 1 + (int)(Noise(i, i, seed + 61) * 2f);

            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > radius * radius) continue;

                // Wrap rather than clip: a tile that stops its detail at the edge shows a grid.
                var x = ((cx + dx) % Size + Size) % Size;
                var y = ((cy + dy) % Size + Size) % Size;

                var shade = (int)(Noise(x, y, seed + 97) * 24f) - 12;
                Put(t, x, y, Clamp(r + shade), Clamp(g + shade), Clamp(b + shade), 255);
            }
        }

        return t;
    }

    /// <summary>Vertical grain with a few darker knots — bark.</summary>
    public static byte[] Bark(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        for (var x = 0; x < Size; x++)
        {
            // One shade per column, so the grain runs the length of the trunk rather than
            // dissolving into noise.
            var columnShade = (int)((Noise(x, 0, seed) * 2f - 1f) * 22f);

            for (var y = 0; y < Size; y++)
            {
                var d = columnShade + (int)((Noise(x, y, seed + 13) * 2f - 1f) * 7f);
                if (Noise(x >> 1, y >> 2, seed + 53) > 0.93f) d -= 30;   // knot
                Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
            }
        }

        return t;
    }

    /// <summary>Concentric rings — the cut end of a log.</summary>
    public static byte[] Rings(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];
        const float centre = (Size - 1) / 2f;

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var dx = x - centre;
            var dy = y - centre;
            var radius = MathF.Sqrt(dx * dx + dy * dy);

            var ring = MathF.Sin(radius * 2.1f) * 12f;
            var grain = (Noise(x, y, seed) * 2f - 1f) * 6f;
            var d = (int)(ring + grain);

            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        return t;
    }

    /// <summary>Horizontal boards with dark seams between them.</summary>
    public static byte[] Planks(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        {
            var board = y >> 2;
            var boardShade = (int)((Noise(board, 0, seed) * 2f - 1f) * 14f);
            var seam = (y & 3) == 0;

            for (var x = 0; x < Size; x++)
            {
                var d = boardShade + (int)((Noise(x, y, seed + 17) * 2f - 1f) * 8f);
                if (seam) d -= 34;

                // Staggered butt joints, so the boards do not read as one long plank.
                if ((x + board * 5) % 16 == 0) d -= 26;

                Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
            }
        }

        return t;
    }

    /// <summary>Foliage: clumped colour with holes punched right through.</summary>
    /// <remarks>
    /// The holes are the point. Opaque leaves read as a solid green cube no matter how good the
    /// colour is; gaps let sky through and let the block behind show, which is what makes a canopy
    /// look like foliage rather than like a hedge.
    /// </remarks>
    public static byte[] Leaves(int seed, byte r, byte g, byte b, float holeChance)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var clump = Noise(x >> 1, y >> 1, seed + 3) * 2f - 1f;
            var fine = Noise(x, y, seed) * 2f - 1f;
            var d = (int)(clump * 26f + fine * 12f);

            var transparent = Noise(x, y, seed + 211) < holeChance;
            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), transparent ? (byte)0 : (byte)255);
        }

        return t;
    }

    /// <summary>Hanging strands, mostly empty.</summary>
    public static byte[] Vine(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        for (var x = 0; x < Size; x++)
        {
            var hasStrand = Noise(x, 0, seed) > 0.55f;
            for (var y = 0; y < Size; y++)
            {
                var on = hasStrand && Noise(x, y >> 1, seed + 5) > 0.22f;
                if (!on) { Put(t, x, y, 0, 0, 0, 0); continue; }

                var d = (int)((Noise(x, y, seed + 11) * 2f - 1f) * 20f);
                Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
            }
        }

        return t;
    }

    /// <summary>A dirt tile with a band of grass rolling over its top edge.</summary>
    public static byte[] GrassSide(int seed, byte[] dirt, byte r, byte g, byte b)
    {
        var t = (byte[])dirt.Clone();

        for (var x = 0; x < Size; x++)
        {
            // A ragged edge, not a straight line: the join is the most-looked-at edge in the game.
            var depth = 3 + (int)(Noise(x, 0, seed) * 3f);

            for (var y = 0; y < depth; y++)
            {
                var d = (int)((Noise(x, y, seed + 29) * 2f - 1f) * 18f);
                Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
            }
        }

        return t;
    }

    /// <summary>Glowing veins through dark rock.</summary>
    public static byte[] Ember(int seed, byte[] baseTile, byte r, byte g, byte b)
    {
        var t = (byte[])baseTile.Clone();

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var vein = Noise(x, y >> 1, seed) * 0.6f + Noise(x >> 1, y, seed + 7) * 0.4f;
            if (vein < 0.62f) continue;

            var heat = (vein - 0.62f) / 0.38f;
            Put(t, x, y,
                Clamp((int)(r * (0.55f + heat * 0.45f))),
                Clamp((int)(g * (0.35f + heat * 0.65f))),
                Clamp((int)(b * (0.25f + heat * 0.75f))),
                255);
        }

        return t;
    }

    private static void Put(byte[] tile, int x, int y, byte r, byte g, byte b, byte a)
    {
        var i = y * Stride + x * 4;
        tile[i] = r; tile[i + 1] = g; tile[i + 2] = b; tile[i + 3] = a;
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

    /// <summary>Deterministic 0..1 hash noise. Stateless, so tiles can be built in any order.</summary>
    private static float Noise(int x, int y, int seed)
    {
        unchecked
        {
            var h = seed;
            h ^= x * 0x27D4EB2D;
            h ^= y * unchecked((int)0x9E3779B1);
            h = (h ^ (h >> 15)) * 0x2C1B3C6D;
            h = (h ^ (h >> 12)) * 0x297A2D39;
            h ^= h >> 15;
            return (h & 0x00FFFFFF) / (float)0x01000000;
        }
    }
}
