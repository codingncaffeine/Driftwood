using System.Numerics;

namespace Driftwood.Core.Blocks;

/// <summary>How a block decides which of its forms to become when it is put down.</summary>
public enum PlacementKind
{
    /// <summary>One form, whichever way it was placed.</summary>
    Plain,

    /// <summary>Two forms: lying in the lower half of the cell, or the upper.</summary>
    Halved,

    /// <summary>Eight forms: four facings, each in either half.</summary>
    Stairs,

    /// <summary>Four forms: one per cardinal, turned so its face meets the player.</summary>
    Facing,

    /// <summary>One form, and only on top of something.</summary>
    Standing,
}

/// <summary>
/// One thing a player can hold: a block, and the forms it takes depending on how it lands.
/// </summary>
/// <remarks>
/// <para>A shape's orientation is decided at the moment it is placed and then never again — there
/// is no block state to hold it, so each orientation is its own registered block. That is how the
/// genre's own storage works underneath too, and it means the mesher never has to ask which way a
/// block is facing: it already knows, because the id says so.</para>
/// <para>This lives away from the client because it is a rule, not an input handler. Which half a
/// slab lands in and which way a stair faces are the kind of thing that reads correct in code and
/// is wrong by a quarter turn on screen, and putting it here is what lets every combination be
/// checked without a window.</para>
/// </remarks>
public sealed class Placeable
{
    /// <summary>What a picker shows.</summary>
    public required string Label { get; init; }

    public required PlacementKind Kind { get; init; }

    /// <summary>
    /// The forms this takes. One for <see cref="PlacementKind.Plain"/> and
    /// <see cref="PlacementKind.Standing"/>, two for <see cref="PlacementKind.Halved"/> (lower then
    /// upper), eight for <see cref="PlacementKind.Stairs"/> — <see cref="Facings"/> in order, each
    /// lower then upper.
    /// </summary>
    public required BlockId[] Variants { get; init; }

    /// <summary>The four cardinal facings, in the order stair variants are stored.</summary>
    public static readonly int[] Facings = [Faces.PosX, Faces.NegX, Faces.PosZ, Faces.NegZ];

    /// <summary>True when this must have something solid under it to stand on.</summary>
    /// <remarks>
    /// The world query stays with the caller. Everything else here is decided from the hit alone,
    /// which is what lets every orientation be checked headlessly; asking what is under the target
    /// cell needs a world, and dragging one in here to answer one question would cost that.
    /// </remarks>
    public bool NeedsFloor => Kind == PlacementKind.Standing;

    /// <summary>
    /// Works out which form to place, or reports that it cannot go there.
    /// </summary>
    /// <param name="hitFace">The face of the block that was struck, in <see cref="Faces"/> order.</param>
    /// <param name="hitHeight">
    /// Where in the target cell the ray landed, 0 at its floor and 1 at its ceiling.
    /// </param>
    /// <param name="forward">Where the player is looking. Only its horizontal part is read.</param>
    /// <remarks>
    /// <para>One rule covers every face for the halved kinds, and that is why it is written this
    /// way. Clicking a block's top lands the ray at the floor of the cell above it, clicking its
    /// underside lands at the ceiling of the cell below, and clicking a side lands wherever the
    /// crosshair was — so "the half the ray landed in" already answers all three without a single
    /// test on which face was hit.</para>
    /// <para>Stairs take the direction from a vector rather than an angle, deliberately. An angle
    /// needs a convention for where zero points and which way it turns, and getting that wrong puts
    /// every stair in the world a quarter turn out in a way that looks like a modelling bug.</para>
    /// </remarks>
    public bool TryResolve(int hitFace, float hitHeight, Vector3 forward, out BlockId id)
    {
        var upper = hitHeight > 0.5f;

        switch (Kind)
        {
            case PlacementKind.Halved:
                id = Variants[upper ? 1 : 0];
                return true;

            case PlacementKind.Stairs:
                // The raised half goes on the far side from the player, so you meet the low step
                // first and climb away from yourself.
                id = Variants[Array.IndexOf(Facings, Cardinal(forward)) * 2 + (upper ? 1 : 0)];
                return true;

            case PlacementKind.Facing:
                // The face turns back toward whoever put it down. A furnace you have to walk round
                // to use is a furnace placed by a rule written from the block's point of view.
                id = Variants[Array.IndexOf(Facings, Cardinal(-forward))];
                return true;

            case PlacementKind.Standing:
                // Nothing holds it up on a wall or a ceiling yet, so it does not go there at all.
                // Placing it anyway leaves a torch hanging in mid-air, which reads as a bug in the
                // renderer rather than as a rule nobody wrote.
                id = Variants[0];
                return hitFace == Faces.PosY;

            default:
                id = Variants[0];
                return true;
        }
    }

    /// <summary>The cardinal a horizontal direction points most nearly along.</summary>
    /// <remarks>
    /// Taken from the vector rather than from an angle, deliberately. An angle needs a convention
    /// for where zero points and which way it turns, and getting that wrong puts every shape in the
    /// world a quarter turn out in a way that looks like a modelling bug.
    /// </remarks>
    private static int Cardinal(Vector3 direction) =>
        MathF.Abs(direction.X) >= MathF.Abs(direction.Z)
            ? direction.X >= 0f ? Faces.PosX : Faces.NegX
            : direction.Z >= 0f ? Faces.PosZ : Faces.NegZ;
}
