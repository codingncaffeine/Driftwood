using Driftwood.Core.Blocks;
using Driftwood.Core.Gen;
using Driftwood.Core.Textures;

namespace Driftwood.Core.Sky;

/// <summary>One corner of the cloud sheet's geometry: where it is, and how brightly it faces.</summary>
public readonly record struct CloudVertex(float X, float Y, float Z, float Shade)
{
    public const int SizeInBytes = 16;
}

/// <summary>The cloud sheet's geometry, in blocks, with the sheet's own corner at the origin.</summary>
public sealed record CloudMesh(CloudVertex[] Vertices, uint[] Indices, int TopQuads, int SideQuads)
{
    public int QuadCount => Indices.Length / 6;
}

/// <summary>
/// A sheet of cloud: a wrapping bitmap of cloud-or-not, extruded into a slab of boxes.
/// </summary>
/// <remarks>
/// <para>A bitmap rather than a volume, because that is what a cloud layer is — flat, uniform in
/// thickness, and read from above and below far more often than edge on. Extruding it means the
/// underside a player actually spends their time looking at has depth at its edges instead of being
/// a decal, and it costs one quad per cell plus one per boundary.</para>
/// <para>The sheet wraps exactly. Its noise is built on a lattice whose period divides the sheet, so
/// the left edge continues into the right one and the whole thing can be drawn again beside itself
/// without a seam — which is what makes a finite sheet into an endless sky.</para>
/// <para>Clouds cast no shadow and take no light. They sit outside the voxel lighting entirely: the
/// genre's own do too, and the alternative is a shadow volume moving across terrain whose light is
/// baked into vertices.</para>
/// </remarks>
public sealed class CloudField
{
    /// <summary>Cells across the sheet. A power of two so every octave's lattice divides it.</summary>
    public const int Size = 128;

    /// <summary>How many blocks across one cell of the bitmap is.</summary>
    public const float CellBlocks = 12f;

    /// <summary>How thick the slab is, in blocks.</summary>
    public const float Thickness = 4f;

    /// <summary>Where the underside sits. Above the tallest terrain, and above the fog.</summary>
    public const float Altitude = 168f;

    /// <summary>How far the sheet reaches before it repeats, in blocks.</summary>
    public const float Period = Size * CellBlocks;

    /// <summary>Blocks a cloud drifts each second.</summary>
    public const float DriftBlocksPerSecond = 0.6f;

    /// <summary>
    /// How much of the sky is meant to be under cloud.
    /// </summary>
    /// <remarks>
    /// A target rather than an outcome: the cut is found per sheet to hit it, which is the same
    /// posture the generator takes to ocean coverage and for the same reason. The field's lowest
    /// octave has four lattice points across the whole sheet, so its mean swings hard from seed to
    /// seed — one fixed threshold gave 25% cover on one seed and 50% on another, which is the
    /// difference between a clear day and an overcast one arriving by accident. Pinning the share
    /// leaves the shape of the clouds free to vary, which is the part worth varying.
    /// </remarks>
    private const float TargetCoverage = 0.38f;

    private readonly bool[] _cloud;

    /// <summary>How many of the sheet's cells hold cloud.</summary>
    public int CloudCells { get; }

    /// <summary>Share of the sky under cloud, 0 to 1.</summary>
    public float Coverage => CloudCells / (float)(Size * Size);

    /// <summary>Where the sheet came from, for the line the client prints at startup.</summary>
    public string Summary { get; }

    public CloudField(WorldSeed seed, string? packPath = null)
    {
        _cloud = FromPack(packPath, out var summary) ?? FromNoise(seed, out summary);
        Summary = summary;

        var cells = 0;
        foreach (var c in _cloud) if (c) cells++;
        CloudCells = cells;
    }

    /// <summary>Reads the sheet, wrapping in both directions.</summary>
    public bool this[int x, int z] => _cloud[WrapIndex(z) * Size + WrapIndex(x)];

    /// <summary>
    /// Extrudes the bitmap: a top and a bottom for every cloud cell, and a side wherever cloud
    /// meets sky.
    /// </summary>
    /// <remarks>
    /// The side test is the whole economy of this. A cloud sheet is mostly interior, and emitting
    /// every cell's four walls would multiply the geometry by three to draw faces buried inside
    /// their own neighbours. Testing the bitmap costs one array read per side and the test wraps,
    /// so the sheet's own edges are culled against the copy that will be drawn beside them.
    /// </remarks>
    public CloudMesh Build()
    {
        var vertices = new List<CloudVertex>(CloudCells * 8);
        var indices = new List<uint>(CloudCells * 12);
        int tops = 0, sides = 0;

        for (var z = 0; z < Size; z++)
        for (var x = 0; x < Size; x++)
        {
            if (!this[x, z]) continue;

            var x0 = x * CellBlocks;
            var z0 = z * CellBlocks;
            var x1 = x0 + CellBlocks;
            var z1 = z0 + CellBlocks;

            Quad(Faces.PosY, x0, 0f, z0, x1, Thickness, z1);
            Quad(Faces.NegY, x0, 0f, z0, x1, Thickness, z1);
            tops += 2;

            if (!this[x - 1, z]) { Quad(Faces.NegX, x0, 0f, z0, x1, Thickness, z1); sides++; }
            if (!this[x + 1, z]) { Quad(Faces.PosX, x0, 0f, z0, x1, Thickness, z1); sides++; }
            if (!this[x, z - 1]) { Quad(Faces.NegZ, x0, 0f, z0, x1, Thickness, z1); sides++; }
            if (!this[x, z + 1]) { Quad(Faces.PosZ, x0, 0f, z0, x1, Thickness, z1); sides++; }
        }

        return new CloudMesh([.. vertices], [.. indices], tops, sides);

        void Quad(int face, float ax, float ay, float az, float bx, float by, float bz)
        {
            var shade = ShadeFor(face);
            var start = (uint)vertices.Count;

            foreach (var corner in Faces.Corners[face])
            {
                vertices.Add(new CloudVertex(
                    corner.X == 0 ? ax : bx,
                    corner.Y == 0 ? ay : by,
                    corner.Z == 0 ? az : bz,
                    shade));
            }

            indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }
    }

    /// <summary>
    /// How brightly a face reads. Fixed per direction rather than lit, because clouds are outside
    /// the lighting: what they need is enough difference between a top and a side for the slab to
    /// have edges at all.
    /// </summary>
    private static float ShadeFor(int face) => face switch
    {
        Faces.PosY => 1.00f,
        Faces.NegY => 0.72f,
        Faces.PosX or Faces.NegX => 0.86f,
        _ => 0.92f,
    };

    /// <summary>
    /// Builds the sheet from noise on a lattice that divides it, so the result wraps exactly.
    /// </summary>
    /// <remarks>
    /// Ordinary fBm cannot be used here. It is defined over the whole plane and has no period, so a
    /// sheet cut out of it does not meet its own other edge — and the seam runs the full width of
    /// the sky, at the one altitude a player is always looking at. Interpolating a lattice whose
    /// indices are taken modulo its own size wraps by construction, at every octave.
    /// </remarks>
    private static bool[] FromNoise(WorldSeed seed, out string summary)
    {
        var noiseSeed = seed.Derive("sky.clouds");
        var field = new float[Size * Size];

        for (var z = 0; z < Size; z++)
        for (var x = 0; x < Size; x++)
        {
            var value = 0f;
            var amplitude = 1f;
            var total = 0f;

            for (var octave = 0; octave < 4; octave++)
            {
                var frequency = 4 << octave;
                value += Octave(x, z, frequency, noiseSeed + octave * 7919) * amplitude;
                total += amplitude;
                amplitude *= 0.5f;
            }

            field[z * Size + x] = value / total;
        }

        // The cut that puts exactly the target share under cloud, found by sorting a copy and
        // reading off the quantile. Every cell is sampled, so this is the answer rather than an
        // estimate of it.
        var sorted = (float[])field.Clone();
        Array.Sort(sorted);
        var cut = sorted[Math.Clamp((int)((1f - TargetCoverage) * sorted.Length), 0, sorted.Length - 1)];

        var sheet = new bool[Size * Size];
        for (var i = 0; i < field.Length; i++) sheet[i] = field[i] > cut;

        summary = "generated";
        return sheet;
    }

    /// <summary>One octave of value noise on a wrapping lattice.</summary>
    private static float Octave(int x, int z, int frequency, int seed)
    {
        var fx = x * frequency / (float)Size;
        var fz = z * frequency / (float)Size;

        var x0 = (int)MathF.Floor(fx);
        var z0 = (int)MathF.Floor(fz);

        var tx = Smooth(fx - x0);
        var tz = Smooth(fz - z0);

        var a = Lattice(x0, z0, frequency, seed);
        var b = Lattice(x0 + 1, z0, frequency, seed);
        var c = Lattice(x0, z0 + 1, frequency, seed);
        var d = Lattice(x0 + 1, z0 + 1, frequency, seed);

        return float.Lerp(float.Lerp(a, b, tx), float.Lerp(c, d, tx), tz);

        static float Smooth(float t) => t * t * (3f - 2f * t);
    }

    private static float Lattice(int x, int z, int frequency, int seed) =>
        Noise.Value2(((x % frequency) + frequency) % frequency, ((z % frequency) + frequency) % frequency, seed);

    /// <summary>
    /// Takes the sheet from a pack's own cloud texture, where every visible texel is cloud.
    /// </summary>
    /// <remarks>
    /// The format ships this as an image with alpha rather than as a heightmap or a mask, so alpha
    /// is what decides. It is the one sky asset a pack can replace and the one a pack author expects
    /// to change the shape of the sky rather than only its colour.
    /// </remarks>
    private static bool[]? FromPack(string? packPath, out string summary)
    {
        summary = "generated";
        if (string.IsNullOrWhiteSpace(packPath)) return null;

        using var pack = TexturePack.Open(packPath);
        var tile = pack?.TryLoadTile("textures/environment/clouds.png", Size);
        if (tile is null) return null;

        var sheet = new bool[Size * Size];
        for (var i = 0; i < sheet.Length; i++) sheet[i] = tile[i * 4 + 3] >= 128;

        summary = $"from pack '{pack!.Name}'";
        return sheet;
    }

    private static int WrapIndex(int i) => ((i % Size) + Size) % Size;
}
