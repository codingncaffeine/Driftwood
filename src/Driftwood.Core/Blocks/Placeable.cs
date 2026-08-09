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

    /// <summary>
    /// Eight forms, picked exactly as <see cref="Stairs"/> picks its own, on a shape that is not
    /// a stair.
    /// </summary>
    /// <remarks>
    /// A separate kind for the same rule, and worth the extra name. The resolution is shared — the
    /// two sit under one case label so they cannot drift — but a trapdoor is a flat panel where a
    /// stair has a step, and every check that reads a facing back off the geometry has to know
    /// which of those it is looking at. Sharing the kind cost exactly one such check, which read
    /// "no step here" as "no facing here" and could then say nothing at all about a trapdoor.
    /// </remarks>
    Trapdoor,

    /// <summary>Four forms: one per cardinal, turned so its face meets the player.</summary>
    Facing,

    /// <summary>
    /// Four forms: one per cardinal, pointed the way the player is looking.
    /// </summary>
    /// <remarks>
    /// <see cref="Facing"/> turned round, for the things with a direction of travel rather than a
    /// front to stand at: a gate's output runs away from whoever places it, so a chain of them laid
    /// walking forward reads in the order it was built.
    /// </remarks>
    Pointing,

    /// <summary>One form, and only on top of something.</summary>
    Standing,

    /// <summary>
    /// Five forms: standing on the floor, or leaning off any of the four walls.
    /// </summary>
    /// <remarks>
    /// <see cref="Variants"/> is the standing form followed by the four wall forms in
    /// <see cref="Facings"/> order, each named for the way it <em>leans</em> — which is the face
    /// that was struck, since a thing put against a wall points away from it.
    /// </remarks>
    Attached,

    /// <summary>Two forms: standing on what is below, or hanging from what is above.</summary>
    /// <remarks>
    /// Lower then upper, which is the same order <see cref="Halved"/> stores its two in. A side
    /// face gives the standing form rather than refusing: a player aiming at the wall beside a
    /// floor meant the floor, and the support test says so if they did not.
    /// </remarks>
    Hung,

    /// <summary>Two forms: lying along x, or along z.</summary>
    /// <remarks>
    /// Not <see cref="Facing"/> with half the answers thrown away. A thing that lies along an axis
    /// looks the same from both ends, so four facings would be four ids for two shapes and a check
    /// that could never tell two of them apart — which is exactly how a campfire arrived before this
    /// kind existed. A log laid sideways and a rail want the same two.
    /// </remarks>
    Axis,

    /// <summary>Four forms, one per wall, and nowhere but a wall.</summary>
    /// <remarks>
    /// <see cref="Attached"/> without the floor form, for the things that genuinely have nowhere
    /// else to be. A ladder lying on the ground is not a shorter ladder, it is a mistake.
    /// </remarks>
    Wall,

    /// <summary>
    /// Sixteen forms and two cells: four facings, two hinges, a lower half and an upper.
    /// </summary>
    /// <remarks>
    /// The only kind that puts down more than one block. <see cref="TryResolve"/> answers with the
    /// lower half; what goes above it is a table the caller reads, because it is a block id and this
    /// class knows only about the ones it was handed.
    /// </remarks>
    Door,
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
    /// <para><b>What holds the result up is not answered here.</b> The form that comes back already
    /// says — a torch fixed to the west wall is a different registered block from one standing on
    /// the floor, and <see cref="BlockType.SupportFace"/> on it is the one statement of that. A
    /// second copy of the answer alongside the id is a second copy to keep in step, and the question
    /// is asked again long after placement, when the wall is taken away.</para>
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
            case PlacementKind.Trapdoor:
                // The raised half goes on the far side from the player, so you meet the low step
                // first and climb away from yourself — and a trapdoor hinges on the far edge for
                // the same reason, which is why the two share this and not merely resemble it.
                id = Variants[Array.IndexOf(Facings, Cardinal(forward)) * 2 + (upper ? 1 : 0)];
                return true;

            case PlacementKind.Facing:
                // The face turns back toward whoever put it down. A furnace you have to walk round
                // to use is a furnace placed by a rule written from the block's point of view.
                id = Variants[Array.IndexOf(Facings, Cardinal(-forward))];
                return true;

            case PlacementKind.Pointing:
                id = Variants[Array.IndexOf(Facings, Cardinal(forward))];
                return true;

            case PlacementKind.Standing:
                // Nothing holds it up on a wall or a ceiling, so it does not go there at all.
                // Placing it anyway leaves it hanging in mid-air, which reads as a bug in the
                // renderer rather than as a rule nobody wrote.
                id = Variants[0];
                return hitFace == Faces.PosY;

            case PlacementKind.Attached:
            {
                // A wall was struck: the thing leans out of it, away from the player. A ceiling has
                // nothing to offer either form, so nothing goes there.
                var wall = Array.IndexOf(Facings, hitFace);
                id = wall >= 0 ? Variants[1 + wall] : Variants[0];
                return hitFace != Faces.NegY;
            }

            case PlacementKind.Hung:
                id = Variants[hitFace == Faces.NegY ? 1 : 0];
                return true;

            case PlacementKind.Axis:
                // Lying along the way the player is looking, so a fire built in front of somebody
                // runs away from them rather than across their path.
                id = Variants[Cardinal(forward) is Faces.PosX or Faces.NegX ? 0 : 1];
                return true;

            case PlacementKind.Wall:
            {
                var wall = Array.IndexOf(Facings, hitFace);
                id = Variants[wall < 0 ? 0 : wall];
                return wall >= 0;
            }

            case PlacementKind.Door:
            {
                // Facing the player, like anything with a front. The hinge goes on the side they
                // are drifting toward, which is what makes a second door placed from the other
                // approach hang the other way and the pair open outward together.
                var facing = Cardinal(-forward);
                var side = facing is Faces.PosX or Faces.NegX
                    ? forward.Z >= 0f ? 0 : 1
                    : forward.X >= 0f ? 0 : 1;

                id = Variants[Array.IndexOf(Facings, facing) * 4 + side * 2];

                // Nothing hangs a door off a ceiling: it grows upward from where it is put, and the
                // cell above is the caller's to check because it is a question about the world.
                return hitFace != Faces.NegY;
            }

            default:
                id = Variants[0];
                return true;
        }
    }

    /// <summary>True when this puts a second block in the cell above the first.</summary>
    public bool IsTall => Kind == PlacementKind.Door;

    /// <summary>
    /// The two sides a thing facing one way can be hinged on, in the order its variants are stored.
    /// </summary>
    /// <remarks>
    /// A door swings about a vertical edge, so its hinge is always one of the two cardinals across
    /// its facing — never the facing itself and never behind it. Written once here so the table that
    /// registers the blocks and the rule that picks between them cannot disagree about which of the
    /// two came first.
    /// </remarks>
    public static int[] Hinges(int facing) => facing is Faces.PosX or Faces.NegX
        ? [Faces.PosZ, Faces.NegZ]
        : [Faces.PosX, Faces.NegX];

    /// <summary>The face opposite another, which is what a pair of them differ by.</summary>
    /// <remarks>
    /// <see cref="Faces"/> lists each direction beside its own opposite, so the pairing is the low
    /// bit and there is nothing to keep in step. Anything that reorders that table breaks this, and
    /// the table says so at the top of itself.
    /// </remarks>
    public static int Opposite(int face) => face ^ 1;

    /// <summary>
    /// True when what holds this up has to be a whole block face rather than merely solid.
    /// </summary>
    /// <remarks>
    /// A torch stands happily on a slab or a stair — anything a foot would rest on. Hanging one off
    /// a fence post or a pane of glass is a different question, because there is no face there to
    /// fix it to, and the answer differs by direction rather than by block: down means "something to
    /// stand on", any other way means "something to fix to". The world query itself stays with the
    /// caller, as it does for everything else here.
    /// </remarks>
    public static bool NeedsFirmSupport(int support) => support >= 0 && support != Faces.NegY;

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
