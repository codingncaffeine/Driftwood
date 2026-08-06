using System.Numerics;

namespace Driftwood.Core.Entities;

/// <summary>
/// Driftwood's own creatures: the models that ship with the game.
/// </summary>
/// <remarks>
/// <para>⛔ <b>Why these are written out by hand when a reader already exists.</b> The reader takes
/// skeletons off an installed Bedrock client, and that is a fine way to <em>see</em> a creature and
/// no way at all to ship one — the files are somebody else's and they stay on their disk. Same
/// posture as the blocks: <c>TileGen</c> draws every tile we ship and a pack replaces it if there is
/// one. This is <c>TileGen</c>'s counterpart for animals, and <see cref="CreatureArt"/> paints them.
/// </para>
/// <para>⛳ <b>The boxes and the net offsets match the reference's, and that is the whole point.</b>
/// It is the third time this project has signed the same contract, after the 64×64 player skin and
/// the 176×166 panel grid, and it buys the same thing each time: somebody else's art fits ours
/// without a translation layer. A net is a layout, not a drawing — the drawing is
/// <see cref="CreatureArt"/>'s, and it is ours.</para>
/// <para>The measurements were taken off real files rather than remembered, and
/// <c>--audit</c> holds every one of them to <see cref="CreatureModel.Validate"/>: six patches per
/// box, right sizes, all on the sheet.</para>
/// </remarks>
public static class StarterCreatures
{
    private static CreatureCube Box(
        float x, float y, float z, float w, float h, float d, int u, int v, bool mirror = false) =>
        new(new Vector3(x, y, z), new Vector3(w, h, d), u, v, mirror, 0f);

    private static CreatureBone Bone(string name, string parent, Vector3 pivot, params CreatureCube[] cubes) =>
        new(name, parent, pivot, Vector3.Zero, Vector3.Zero, cubes);

    /// <summary>A bone whose own boxes are laid down at rest and whose children are not.</summary>
    /// <remarks>
    /// ⛔ The ninety degrees that turns a torso drawn standing on end into an animal standing on its
    /// legs — and it reaches this bone's boxes only. The head and the legs hang off it and are
    /// written where they finally stand, so carrying it down to them puts the head under the belly.
    /// </remarks>
    private static CreatureBone LaidDown(string name, string parent, Vector3 pivot, params CreatureCube[] cubes) =>
        new(name, parent, pivot, Vector3.Zero, new Vector3(90f, 0f, 0f), cubes);

    /// <summary>Four legs at the corners of a body, all reading one patch.</summary>
    /// <remarks>
    /// ⚠ The two on the creature's left are mirrored, which is what makes a leg's outside face the
    /// outside on both sides. Not decoration: unmirrored, the left pair wear their own inside.
    /// </remarks>
    private static IEnumerable<CreatureBone> Legs(
        string parent, float outAcross, float top, float front, float back,
        float width, float height, int u, int v)
    {
        var half = width * 0.5f;

        yield return Bone("leg0", parent, new Vector3(-outAcross, top, back),
            Box(-outAcross - half, 0f, back - half, width, height, width, u, v));

        yield return Bone("leg1", parent, new Vector3(outAcross, top, back),
            Box(outAcross - half, 0f, back - half, width, height, width, u, v, mirror: true));

        yield return Bone("leg2", parent, new Vector3(-outAcross, top, front),
            Box(-outAcross - half, 0f, front - half, width, height, width, u, v));

        yield return Bone("leg3", parent, new Vector3(outAcross, top, front),
            Box(outAcross - half, 0f, front - half, width, height, width, u, v, mirror: true));
    }

    public static CreatureModel Cow() => new(
        "cow", 64, 32,
        [
            LaidDown("body", "", new Vector3(0f, 19f, 2f),
                Box(-6f, 11f, -5f, 12f, 18f, 10f, 18, 4),
                Box(-2f, 11f, -6f, 4f, 6f, 1f, 52, 0)),

            Bone("head", "body", new Vector3(0f, 20f, -8f),
                Box(-4f, 16f, -14f, 8f, 8f, 6f, 0, 0),
                Box(-5f, 22f, -12f, 1f, 3f, 1f, 22, 0),
                Box(4f, 22f, -12f, 1f, 3f, 1f, 22, 0)),

            .. Legs("body", 4f, 12f, -6f, 7f, 4f, 12f, 0, 16),
        ]);

    public static CreatureModel Pig() => new(
        "pig", 64, 32,
        [
            LaidDown("body", "", new Vector3(0f, 13f, 2f),
                Box(-5f, 7f, -5f, 10f, 16f, 8f, 28, 8)),

            Bone("head", "body", new Vector3(0f, 12f, -6f),
                Box(-4f, 8f, -14f, 8f, 8f, 8f, 0, 0),
                Box(-2f, 9f, -15f, 4f, 3f, 1f, 16, 16)),

            .. Legs("body", 3f, 6f, -5f, 7f, 4f, 6f, 0, 16),
        ]);

    /// <summary>
    /// The woolly one, on one sheet.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Ours carries its fleece in its own boxes rather than as a second inflated model.</b> The
    /// reference draws a sheep twice — a bare body from one sheet and a woollen shell from another —
    /// which is why a pack's <c>sheep.png</c> is 64×32 against a 64×64 net and comes out squashed.
    /// One animal, one sheet, wool included: simpler to draw, simpler to shear later (the fleece is
    /// a box that stops being drawn), and it is the shape a pack's own body sheet still fits.
    /// </remarks>
    /// <remarks>
    /// ⚠ 64×<b>64</b>, unlike the others. Our fleece is part of the body rather than a second shell,
    /// so the body box is bigger than the reference's and its net is 26 texels tall against a 32-tall
    /// sheet with a head already on it. The reference gets away with 64×32 for the shorn one and puts
    /// the woollen version on a taller sheet; ours is one animal, so it takes the taller sheet.
    /// </remarks>
    public static CreatureModel Sheep() => new(
        "sheep", 64, 64,
        [
            LaidDown("body", "", new Vector3(0f, 19f, 2f),
                Box(-5f, 12f, -6f, 10f, 18f, 8f, 28, 8)),

            Bone("head", "body", new Vector3(0f, 18f, -8f),
                Box(-3f, 15f, -14f, 6f, 6f, 8f, 0, 0)),

            .. Legs("body", 3f, 12f, -5f, 7f, 4f, 12f, 0, 16),
        ]);

    /// <summary>The upright one — no bind pose anywhere on it, because a bird stands up.</summary>
    public static CreatureModel Chicken() => new(
        "chicken", 64, 32,
        [
            Bone("body", "", new Vector3(0f, 8f, 0f),
                Box(-3f, 4f, -3f, 6f, 8f, 6f, 0, 9)),

            Bone("head", "body", new Vector3(0f, 9f, -4f),
                Box(-2f, 9f, -6f, 4f, 6f, 3f, 0, 0)),

            Bone("comb", "head", new Vector3(0f, 9f, -4f),
                Box(-1f, 9f, -7f, 2f, 2f, 2f, 14, 4)),

            Bone("beak", "head", new Vector3(0f, 9f, -4f),
                Box(-2f, 11f, -8f, 4f, 2f, 2f, 14, 0)),

            Bone("wing0", "body", new Vector3(-3f, 11f, 0f),
                Box(-4f, 7f, -3f, 1f, 4f, 6f, 24, 13)),

            Bone("wing1", "body", new Vector3(3f, 11f, 0f),
                Box(3f, 7f, -3f, 1f, 4f, 6f, 24, 13, mirror: true)),

            Bone("leg0", "body", new Vector3(-2f, 5f, 1f),
                Box(-3f, 0f, -2f, 3f, 5f, 3f, 26, 0)),

            Bone("leg1", "body", new Vector3(1f, 5f, 1f),
                Box(0f, 0f, -2f, 3f, 5f, 3f, 26, 0, mirror: true)),
        ]);

    /// <summary>Every creature that ships with the game, by our name for it.</summary>
    public static IReadOnlyList<CreatureModel> All { get; } = [Cow(), Pig(), Sheep(), Chicken()];

    /// <summary>Ours for this creature, or null when we have not drawn one yet.</summary>
    public static CreatureModel? ByName(string name)
    {
        foreach (var model in All) if (model.Name == name) return model;
        return null;
    }

    /// <summary>
    /// Checks every model we ship is sound, assembles, and stands on the ground.
    /// </summary>
    /// <remarks>
    /// ⛔ The same three questions asked of somebody else's files, asked of ours — a hand-written
    /// table is exactly as able to put a patch off the edge of a sheet or leave a head floating a
    /// unit above a neck, and rather more likely, because nothing generated it.
    /// </remarks>
    public static List<string> Validate()
    {
        var faults = new List<string>();

        foreach (var model in All)
        {
            faults.AddRange(model.Validate());

            var mesh = CreatureMesh.Build(model);

            if (!mesh.Assembled())
                faults.Add($"{model.Name} is a heap: some part of it touches nothing else");

            var (min, max) = mesh.PosedBounds();
            var size = (max - min) / CreatureMesh.Unit;

            // ⚠ Feet on the ground. A model whose lowest point is not zero either floats or is
            // buried, and both look like a placement fault rather than a modelling one.
            if (MathF.Abs(min.Y) > 0.01f)
                faults.Add($"{model.Name} stands with its lowest point at {min.Y / CreatureMesh.Unit:F1} rather than 0");

            // And it has to be an animal-sized thing. Four units or four hundred is a transposed
            // digit, which is the failure a table of numbers actually has.
            if (size.Y is < 6f or > 48f)
                faults.Add($"{model.Name} is {size.Y:F0} units tall, which is not an animal");
        }

        return faults;
    }
}
