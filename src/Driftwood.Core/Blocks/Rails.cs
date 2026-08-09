using System.Numerics;
using Driftwood.Core.World;

namespace Driftwood.Core.Blocks;

/// <summary>The ways a rail can lie: two straights, four elbows, four climbs.</summary>
public enum RailForm
{
    None = 0,
    AlongX,
    AlongZ,
    NorthEast,
    NorthWest,
    SouthEast,
    SouthWest,
    UpEast,
    UpWest,
    UpSouth,
    UpNorth,
}

/// <summary>
/// Everything the track knows: which block is which form, how a rail re-picks its shape from its
/// neighbours, and the line a cart's wheels actually follow through each cell.
/// </summary>
/// <remarks>
/// <para>⛳ <b>The reshape reads EXISTENCE, never form, and that is what makes one ring enough.</b>
/// <see cref="ConnectionTable"/>'s one-ring argument — a swap changes a shape, never whether a
/// neighbour would join — holds here only because the wanted form is a pure function of WHICH cells
/// hold rails: a reshape write cannot change that field, so no seventh cell can ever want to move.
/// The audit measures the property rather than trusting it, exactly as it does for fences. A rule
/// that ever reads a neighbour's FORM breaks this silently and needs the Supports queue instead.</para>
/// <para>⚠ Joining is by priority — east, west, south, north, first two win — which is simpler than
/// the genre's notoriously arcane preference order and deterministic in a way a player can learn.
/// A tee keeps its straight-through; curves never carry power (the genre's own rule, kept).</para>
/// </remarks>
public sealed class RailTable
{
    private readonly RailForm[] _form;
    private readonly bool[] _powered;
    private readonly bool[] _on;

    /// <summary>form → plain block id.</summary>
    private readonly ushort[] _plain = new ushort[11];

    /// <summary>form → powered block id, off then on; zero where power cannot lie (curves).</summary>
    private readonly ushort[] _poweredOff = new ushort[11];
    private readonly ushort[] _poweredOn = new ushort[11];

    public RailTable(BlockRegistry registry)
    {
        _form = new RailForm[registry.Count];
        _powered = new bool[registry.Count];
        _on = new bool[registry.Count];

        foreach (var (name, form) in Names())
        {
            var plain = registry.ByName($"rail_{name}").Id.Value;
            _form[plain] = form;
            _plain[(int)form] = plain;

            if (RailForms.CanPower(form))
            {
                var off = registry.ByName($"powered_rail_{name}").Id.Value;
                var on = registry.ByName($"powered_rail_{name}_on").Id.Value;

                _form[off] = form;
                _form[on] = form;
                _powered[off] = true;
                _powered[on] = true;
                _on[on] = true;
                _poweredOff[(int)form] = off;
                _poweredOn[(int)form] = on;
            }
        }
    }

    private static IEnumerable<(string Name, RailForm Form)> Names() =>
    [
        ("x", RailForm.AlongX), ("z", RailForm.AlongZ),
        ("ne", RailForm.NorthEast), ("nw", RailForm.NorthWest),
        ("se", RailForm.SouthEast), ("sw", RailForm.SouthWest),
        ("up_e", RailForm.UpEast), ("up_w", RailForm.UpWest),
        ("up_s", RailForm.UpSouth), ("up_n", RailForm.UpNorth),
    ];

    public RailForm FormOf(ushort id) => id < _form.Length ? _form[id] : RailForm.None;

    public bool IsRail(ushort id) => FormOf(id) != RailForm.None;

    public bool IsPowered(ushort id) => id < _powered.Length && _powered[id];

    public bool IsOn(ushort id) => id < _on.Length && _on[id];

    /// <summary>The id for a form, keeping the family and state of the rail being reshaped.</summary>
    public BlockId Wearing(ushort was, RailForm form)
    {
        if (!_powered[was]) return new BlockId(_plain[(int)form]);

        // A powered rail refused a curve by the rule, so the lookup cannot miss — but a zero here
        // would place air, so the plain form is the honest fallback rather than a hole in the track.
        var id = _on[was] ? _poweredOn[(int)form] : _poweredOff[(int)form];
        return new BlockId(id == 0 ? _plain[(int)form] : id);
    }

    /// <summary>
    /// The form this cell's rail should wear, given which cells around it hold rails.
    /// </summary>
    /// <remarks>
    /// Existence only — see the class remarks; this is the line the one-ring argument stands on.
    /// </remarks>
    public RailForm Wanted(VoxelWorld world, int x, int y, int z, ushort here)
    {
        Span<int> links = stackalloc int[2];
        Span<bool> up = stackalloc bool[2];
        var found = 0;

        // East, west, south, north — the fixed priority. A link is a rail beside us, a rail one
        // up (we climb to it), or a rail one down (its climb tops out at our level).
        foreach (var side in (int[])[Faces.PosX, Faces.NegX, Faces.PosZ, Faces.NegZ])
        {
            if (found == 2) break;

            var (dx, _, dz) = Faces.Normals[side];

            var flat = IsRail(world.GetBlock(x + dx, y, z + dz).Value);
            var above = IsRail(world.GetBlock(x + dx, y + 1, z + dz).Value);
            var below = IsRail(world.GetBlock(x + dx, y - 1, z + dz).Value);

            if (!flat && !above && !below) continue;

            links[found] = side;
            up[found] = !flat && above;
            found++;
        }

        var axis = _form[here] switch
        {
            RailForm.AlongZ or RailForm.UpSouth or RailForm.UpNorth => RailForm.AlongZ,
            _ => RailForm.AlongX,
        };

        if (found == 0) return axis;

        if (found == 1)
            return up[0] ? Climb(links[0]) : Straight(links[0]);

        // Two links. A climb outranks the flat partner (the track has to reach it); an opposite
        // pair is a straight; a perpendicular pair is an elbow — unless power forbids it, in which
        // case the first link's axis wins and the tee keeps its straight-through.
        if (up[0]) return Climb(links[0]);
        if (up[1] && links[1] == Placeable.Opposite(links[0])) return Climb(links[1]);

        if (links[1] == Placeable.Opposite(links[0])) return Straight(links[0]);

        if (_powered[here]) return Straight(links[0]);

        return Elbow(links[0], links[1]);
    }

    private static RailForm Straight(int side) =>
        side is Faces.PosX or Faces.NegX ? RailForm.AlongX : RailForm.AlongZ;

    private static RailForm Climb(int side) => side switch
    {
        Faces.PosX => RailForm.UpEast,
        Faces.NegX => RailForm.UpWest,
        Faces.PosZ => RailForm.UpSouth,
        _ => RailForm.UpNorth,
    };

    private static RailForm Elbow(int a, int b)
    {
        var east = a == Faces.PosX || b == Faces.PosX;
        var north = a == Faces.NegZ || b == Faces.NegZ;

        return north
            ? east ? RailForm.NorthEast : RailForm.NorthWest
            : east ? RailForm.SouthEast : RailForm.SouthWest;
    }

    /// <summary>
    /// Re-picks the rails around one edit. One ring, on the existence argument in the class notes.
    /// </summary>
    public void Reshape(VoxelWorld world, int x, int y, int z, Action<int, int, int, BlockId> write)
    {
        Fix(x, y, z);
        foreach (var side in (int[])[Faces.PosX, Faces.NegX, Faces.PosZ, Faces.NegZ])
        {
            var (dx, _, dz) = Faces.Normals[side];
            Fix(x + dx, y, z + dz);
            Fix(x + dx, y + 1, z + dz);
            Fix(x + dx, y - 1, z + dz);
        }

        void Fix(int cx, int cy, int cz)
        {
            var here = world.GetBlock(cx, cy, cz).Value;
            if (!IsRail(here)) return;

            var want = Wearing(here, Wanted(world, cx, cy, cz, here));
            if (want.Value == here) return;

            write(cx, cy, cz, want);
        }
    }
}

/// <summary>What each form is, without a registry in hand.</summary>
public static class RailForms
{
    /// <summary>Curves never carry power — the genre's rule, and ours.</summary>
    public static bool CanPower(RailForm form) => form is RailForm.AlongX or RailForm.AlongZ
        or RailForm.UpEast or RailForm.UpWest or RailForm.UpSouth or RailForm.UpNorth;

    public static bool IsClimb(RailForm form) => form is RailForm.UpEast or RailForm.UpWest
        or RailForm.UpSouth or RailForm.UpNorth;

    /// <summary>
    /// The two ends of the line through a cell, low end first, in cell-local blocks (0..1), plus
    /// the elbow's control point when the line bends.
    /// </summary>
    /// <remarks>
    /// A climb's A end is always the low one, which is what lets the cart integrate gravity as
    /// "toward A". Elbows run edge to edge through a quadratic bend so a cart holds the corner
    /// instead of cutting it — the difference between a turn and a jolt.
    /// </remarks>
    public static (Vector3 A, Vector3 B, Vector3? Bend) PathOf(RailForm form) => form switch
    {
        RailForm.AlongX => (new(0f, 0.0625f, 0.5f), new(1f, 0.0625f, 0.5f), null),
        RailForm.AlongZ => (new(0.5f, 0.0625f, 0f), new(0.5f, 0.0625f, 1f), null),
        RailForm.NorthEast => (new(0.5f, 0.0625f, 0f), new(1f, 0.0625f, 0.5f), new(0.5f, 0.0625f, 0.5f)),
        RailForm.NorthWest => (new(0.5f, 0.0625f, 0f), new(0f, 0.0625f, 0.5f), new(0.5f, 0.0625f, 0.5f)),
        RailForm.SouthEast => (new(0.5f, 0.0625f, 1f), new(1f, 0.0625f, 0.5f), new(0.5f, 0.0625f, 0.5f)),
        RailForm.SouthWest => (new(0.5f, 0.0625f, 1f), new(0f, 0.0625f, 0.5f), new(0.5f, 0.0625f, 0.5f)),
        RailForm.UpEast => (new(0f, 0.0625f, 0.5f), new(1f, 1.0625f, 0.5f), null),
        RailForm.UpWest => (new(1f, 0.0625f, 0.5f), new(0f, 1.0625f, 0.5f), null),
        RailForm.UpSouth => (new(0.5f, 0.0625f, 0f), new(0.5f, 1.0625f, 1f), null),
        RailForm.UpNorth => (new(0.5f, 0.0625f, 1f), new(0.5f, 1.0625f, 0f), null),
        _ => (Vector3.Zero, Vector3.Zero, null),
    };

    /// <summary>How much track a cell holds, in blocks — what turns speed into parameter.</summary>
    public static float LengthOf(RailForm form) => form switch
    {
        RailForm.AlongX or RailForm.AlongZ => 1f,
        RailForm.UpEast or RailForm.UpWest or RailForm.UpSouth or RailForm.UpNorth => 1.41421f,

        // A quadratic elbow edge-to-edge, measured once: close enough to a quarter circle.
        _ => 1.11f,
    };

    /// <summary>Where on the line a parameter lands, in cell-local blocks.</summary>
    public static Vector3 At(RailForm form, float t)
    {
        var (a, b, bend) = PathOf(form);
        if (bend is not { } c) return Vector3.Lerp(a, b, t);

        var back = 1f - t;
        return back * back * a + 2f * back * t * c + t * t * b;
    }

    /// <summary>The way the line runs at a parameter, for a cart's own facing.</summary>
    public static Vector3 Heading(RailForm form, float t)
    {
        var (a, b, bend) = PathOf(form);
        if (bend is not { } c) return Vector3.Normalize(b - a);

        var d = 2f * (1f - t) * (c - a) + 2f * t * (b - c);
        return d.LengthSquared() < 1e-6f ? Vector3.Normalize(b - a) : Vector3.Normalize(d);
    }

    /// <summary>The face of the cell each end leaves by, and whether it steps up a level.</summary>
    public static ((int Side, bool Up) A, (int Side, bool Up) B) EndsOf(RailForm form) => form switch
    {
        RailForm.AlongX => ((Faces.NegX, false), (Faces.PosX, false)),
        RailForm.AlongZ => ((Faces.NegZ, false), (Faces.PosZ, false)),
        RailForm.NorthEast => ((Faces.NegZ, false), (Faces.PosX, false)),
        RailForm.NorthWest => ((Faces.NegZ, false), (Faces.NegX, false)),
        RailForm.SouthEast => ((Faces.PosZ, false), (Faces.PosX, false)),
        RailForm.SouthWest => ((Faces.PosZ, false), (Faces.NegX, false)),
        RailForm.UpEast => ((Faces.NegX, false), (Faces.PosX, true)),
        RailForm.UpWest => ((Faces.PosX, false), (Faces.NegX, true)),
        RailForm.UpSouth => ((Faces.NegZ, false), (Faces.PosZ, true)),
        RailForm.UpNorth => ((Faces.PosZ, false), (Faces.NegZ, true)),
        _ => ((Faces.NegX, false), (Faces.PosX, false)),
    };
}
