using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.World;

namespace Driftwood.Core.Entities;

/// <summary>One cart: where on the track it is, and how fast it is going.</summary>
/// <remarks>
/// A cart's whole state is (cell, parameter, sign, speed) — its position is DERIVED from the rail
/// under it, never stored as a free vector, which is why it cannot drift off the track and why the
/// save can hold it in sixteen bytes. <see cref="Velocity"/> is signed along the cell's own A→B
/// parameter; the world-space heading comes off the rail's geometry when anybody asks.
/// </remarks>
public sealed class Cart
{
    public int X;
    public int Y;
    public int Z;

    /// <summary>Where along the cell's line, 0 at A and 1 at B.</summary>
    public float T = 0.5f;

    /// <summary>Blocks per second along the parameter: positive toward B.</summary>
    public float Velocity;

    /// <summary>Where the wheels are, in world blocks.</summary>
    public Vector3 Position(RailForm form) => new Vector3(X, Y, Z) + RailForms.At(form, T);

    /// <summary>The way it points, for the renderer and for a rider's push.</summary>
    public Vector3 Heading(RailForm form) =>
        RailForms.Heading(form, T) * (Velocity < 0f ? -1f : 1f);
}

/// <summary>
/// Rolls every cart along the track: gravity on the climbs, friction on the flat, the powered
/// rail's shove or its dead stop, and the hop from cell to cell at the ends of each line.
/// </summary>
/// <remarks>
/// <para>⛳ <b>The numbers are the feel</b>, so they are named here and nowhere else: a cart tops
/// out at 8 blocks a second (the genre's own ceiling), coasts down at 1.2 b/s² on the flat, gains
/// 5 b/s² down a climb, and a fed booster adds 6 b/s² while an unfed one is a brake — the genre's
/// rule, and the reason a station is a lever and two rails.</para>
/// <para>⚠ A cart that runs out of rail stops at the boundary rather than deriving physics it does
/// not have; a cart whose rail is MINED under it is homeless, and the caller turns it back into an
/// item — said here because both reads could pass for bugs and are decisions.</para>
/// </remarks>
public sealed class CartSystem
{
    public const float MaxSpeed = 8f;
    public const float Friction = 1.2f;
    public const float SlopePull = 5f;
    public const float Boost = 6f;

    private readonly RailTable _rails;

    public CartSystem(RailTable rails) => _rails = rails;

    public RailTable Rails => _rails;

    public List<Cart> All { get; } = [];

    /// <summary>Puts a cart on a rail, resting at the middle of its line.</summary>
    public Cart Place(int x, int y, int z)
    {
        var cart = new Cart { X = x, Y = y, Z = z };
        All.Add(cart);
        return cart;
    }

    /// <summary>
    /// Advances every cart. Returns any that lost the rail under them, already removed — theirs
    /// to drop as items where each stood.
    /// </summary>
    public List<Cart>? Step(VoxelWorld world, float dt)
    {
        List<Cart>? homeless = null;

        foreach (var cart in All)
        {
            var here = world.GetBlock(cart.X, cart.Y, cart.Z).Value;
            var form = _rails.FormOf(here);

            if (form == RailForm.None)
            {
                (homeless ??= []).Add(cart);
                continue;
            }

            // The pulls: down the climb, along the booster, against the flat's own drag.
            if (RailForms.IsClimb(form)) cart.Velocity -= SlopePull * dt;

            if (_rails.IsPowered(here))
            {
                if (_rails.IsOn(here))
                {
                    // A shove needs a direction; a cart parked dead on a booster takes its push
                    // toward B, which is the deterministic answer and one nudge fixes the rest.
                    if (MathF.Abs(cart.Velocity) < 0.05f) cart.Velocity = 0.5f;
                    cart.Velocity += MathF.Sign(cart.Velocity) * Boost * dt;
                }
                else
                {
                    cart.Velocity = 0f;
                }
            }
            else if (!RailForms.IsClimb(form))
            {
                var drag = Friction * dt;
                cart.Velocity = MathF.Abs(cart.Velocity) <= drag
                    ? 0f
                    : cart.Velocity - MathF.Sign(cart.Velocity) * drag;
            }

            cart.Velocity = Math.Clamp(cart.Velocity, -MaxSpeed, MaxSpeed);
            if (cart.Velocity == 0f) continue;

            // Parameter speed is real speed over this cell's own length, so an elbow takes longer
            // than a straight at the same pace — which is exactly what a bend should do.
            cart.T += cart.Velocity * dt / RailForms.LengthOf(form);

            while (cart.T is > 1f or < 0f)
            {
                var leavingB = cart.T > 1f;
                var ((aSide, aUp), (bSide, bUp)) = RailForms.EndsOf(form);
                var (side, stepsUp) = leavingB ? (bSide, bUp) : (aSide, aUp);

                var (dx, _, dz) = Faces.Normals[side];
                var nx = cart.X + dx;
                var nz = cart.Z + dz;
                var ny = cart.Y + (stepsUp ? 1 : 0);

                // A climb's low end can also hand over to a rail a level down — the neighbour's
                // own climb topping out at us — which the flat search below finds one cell under.
                var next = FindRail(world, nx, ref ny, nz);

                if (next == RailForm.None)
                {
                    // End of the line: park exactly at the boundary, facing the way it came.
                    cart.T = leavingB ? 1f : 0f;
                    cart.Velocity = 0f;
                    break;
                }

                var spill = leavingB ? cart.T - 1f : -cart.T;
                var carried = spill * RailForms.LengthOf(form);

                cart.X = nx;
                cart.Y = ny;
                cart.Z = nz;
                form = next;

                // Enter at whichever of the new line's ends touches the side we came in by.
                var entry = Placeable.Opposite(side);
                var ((naSide, _), _) = RailForms.EndsOf(next);
                var fromA = naSide == entry;

                var t = carried / RailForms.LengthOf(next);
                cart.T = fromA ? t : 1f - t;

                var speed = MathF.Abs(cart.Velocity);
                cart.Velocity = fromA ? speed : -speed;
            }
        }

        if (homeless is not null)
            foreach (var cart in homeless) All.Remove(cart);

        return homeless;
    }

    /// <summary>The rail at this column: level first, then one down for a climb's handover.</summary>
    private RailForm FindRail(VoxelWorld world, int x, ref int y, int z)
    {
        var flat = _rails.FormOf(world.GetBlock(x, y, z).Value);
        if (flat != RailForm.None) return flat;

        var below = _rails.FormOf(world.GetBlock(x, y - 1, z).Value);
        if (below != RailForm.None && RailForms.IsClimb(below))
        {
            y -= 1;
            return below;
        }

        return RailForm.None;
    }

    /// <summary>The cart nearest a ray, within reach — how a click finds one.</summary>
    public Cart? Pick(VoxelWorld world, Vector3 eye, Vector3 forward, float reach)
    {
        Cart? best = null;
        var bestAlong = float.MaxValue;

        foreach (var cart in All)
        {
            var form = _rails.FormOf(world.GetBlock(cart.X, cart.Y, cart.Z).Value);
            if (form == RailForm.None) continue;

            var centre = cart.Position(form) + new Vector3(0f, 0.4f, 0f);
            var toCart = centre - eye;
            var along = Vector3.Dot(toCart, forward);
            if (along < 0f || along > reach) continue;

            var off = toCart - forward * along;
            if (off.Length() > 0.7f) continue;
            if (along >= bestAlong) continue;

            best = cart;
            bestAlong = along;
        }

        return best;
    }
}
