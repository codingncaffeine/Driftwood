using System.Numerics;

namespace Driftwood.Core.Entities;

/// <summary>
/// What one blast does to the world and to whoever is standing in it.
/// </summary>
/// <remarks>
/// <para>⛳ <b>In Core, because the interesting part is a shape and a falloff and neither needs a
/// window.</b> The client walks the carved cells through its ordinary edit path — relight, drops,
/// particles are all machinery that already exists per cell — and this file owns which cells those
/// are and how hard the blast hits at a distance, which is what the audit can hold still.</para>
/// <para>⚠ <b>The block radius and the hurt radius are two numbers on purpose.</b> A blast is felt
/// further than it digs — a player at five blocks keeps their floor and loses a third of their
/// hearts, which is what makes running from a lit fuse matter even when it cannot be outrun clean.
/// </para>
/// </remarks>
public static class Explosion
{
    /// <summary>Blocks out from the centre inside which cells are carved away.</summary>
    public const float BlockRadius = 3.5f;

    /// <summary>Blocks out to which a body is hurt at all.</summary>
    public const float HurtRadius = 6f;

    /// <summary>Half-hearts at the centre of it. Fourteen of a player's twenty: standing on the
    /// fuse is nearly lethal and survivable, in that order.</summary>
    public const int MaxHalfHearts = 14;

    /// <summary>
    /// The hardest rock a blast digs out.
    /// </summary>
    /// <remarks>
    /// ⛳ Between stone at 1.5 and deepstone at 3: a crater in a meadow takes the turf and the stone
    /// under it, and the deep's own rock shrugs the same blast off. The number is a claim about the
    /// hardness TABLE, so a retune of stone moves the crater with it.
    /// </remarks>
    public const float HardnessLimit = 2.5f;

    /// <summary>
    /// The cells a blast at <paramref name="centre"/> digs out.
    /// </summary>
    /// <param name="hardness">
    /// A cell's hardness, or null for one the blast cannot touch — air, a fluid, the unbreakable.
    /// The caller owns the registry; this owns the sphere.
    /// </param>
    public static List<(int X, int Y, int Z)> Carve(Vector3 centre, Func<int, int, int, float?> hardness)
    {
        var carved = new List<(int, int, int)>();
        var reach = (int)MathF.Ceiling(BlockRadius);

        var cx = (int)MathF.Floor(centre.X);
        var cy = (int)MathF.Floor(centre.Y);
        var cz = (int)MathF.Floor(centre.Z);

        for (var y = cy - reach; y <= cy + reach; y++)
        for (var z = cz - reach; z <= cz + reach; z++)
        for (var x = cx - reach; x <= cx + reach; x++)
        {
            // The cell's middle against the blast's own point, so the sphere is a sphere rather
            // than a rounded box of index arithmetic.
            var at = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            if (Vector3.DistanceSquared(at, centre) > BlockRadius * BlockRadius) continue;

            if (hardness(x, y, z) is not { } rock) continue;
            if (rock < 0f || rock > HardnessLimit) continue;

            carved.Add((x, y, z));
        }

        return carved;
    }

    /// <summary>Half-hearts a body standing at <paramref name="at"/> takes. Zero out of reach.</summary>
    /// <remarks>
    /// Linear from the full figure at the centre to nothing at the edge — a curve would be more
    /// physical and would say nothing a player can read; "closer is worse, evenly" is legible.
    /// </remarks>
    public static int HurtAt(Vector3 centre, Vector3 at)
    {
        var apart = Vector3.Distance(centre, at);
        if (apart >= HurtRadius) return 0;

        return (int)MathF.Round(MaxHalfHearts * (1f - apart / HurtRadius));
    }

    /// <summary>
    /// Checks the blast digs a sphere and only a sphere, respects the rock it cannot break, and
    /// hurts by distance.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Each claim is paired with the state that would satisfy a weaker one.</b> "Cells were
    /// carved" says nothing about the ones that must NOT be — so the hard floor under the blast has
    /// to survive, the corner outside the radius has to survive, and the damage at the edge has to
    /// be zero rather than merely small.
    /// </remarks>
    public static List<string> Validate()
    {
        var faults = new List<string>();

        // A world of soft rock with a hard floor at y 60 and one unbreakable cell beside centre.
        static float? World(int x, int y, int z)
        {
            if (y < 58) return null;                    // out of the world
            if (y < 61) return 3f;                      // the deepstone floor
            if (x == 2 && y == 64 && z == 0) return -1f; // the unbreakable one
            return 1.5f;                                 // stone everywhere else
        }

        var centre = new Vector3(0.5f, 64.5f, 0.5f);
        var carved = Carve(centre, World);

        if (carved.Count == 0) return ["a blast in soft stone carved nothing at all"];

        foreach (var (x, y, z) in carved)
        {
            var at = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            var apart = Vector3.Distance(at, centre);

            if (apart > BlockRadius + 0.01f)
                faults.Add($"a cell {apart:F2} blocks out was carved past the radius of {BlockRadius}");

            if (y < 61) faults.Add($"the blast dug out deepstone at y {y}, past its hardness limit");
            if (x == 2 && y == 64 && z == 0) faults.Add("the blast removed an unbreakable cell");
        }

        // The near cell is taken and the far corner is left: the sphere has both edges.
        if (!carved.Contains((0, 64, 0))) faults.Add("the blast left its own centre cell standing");
        if (carved.Contains((3, 66, 3))) faults.Add("a corner outside the sphere was carved");

        // The falloff, at its three readable points.
        if (HurtAt(centre, centre) != MaxHalfHearts)
            faults.Add($"standing on the blast hurt {HurtAt(centre, centre)} rather than the full {MaxHalfHearts}");

        var half = HurtAt(centre, centre + new Vector3(HurtRadius * 0.5f, 0f, 0f));
        if (Math.Abs(half - MaxHalfHearts / 2) > 1)
            faults.Add($"half way out hurt {half}, which is not half of {MaxHalfHearts}");

        if (HurtAt(centre, centre + new Vector3(HurtRadius, 0f, 0f)) != 0)
            faults.Add("a body at the edge of the hurt radius was still hurt");

        return faults;
    }
}
