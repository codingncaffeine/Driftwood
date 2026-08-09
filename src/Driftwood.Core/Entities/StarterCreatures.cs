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

    /// <summary>
    /// The one with teeth, on the reference's own 64×32 wolf net.
    /// </summary>
    /// <remarks>
    /// <para>Smaller than a pig and longer than it is tall, with the mane box that makes a wolf's
    /// front heavier than its haunches — the silhouette that tells it from a stray sheep at dusk,
    /// which is when telling matters.</para>
    /// <para>⚠ The ear patch and the body patch share a few texels of sheet, exactly as the
    /// reference's net does. Ours paints the ears after the body so the ears win the overlap; a
    /// pack's sheet was painted against the same collision and carries whatever its author chose.
    /// </para>
    /// </remarks>
    public static CreatureModel Wolf() => new(
        "wolf", 64, 32,
        [
            LaidDown("body", "", new Vector3(0f, 11f, 2f),
                Box(-3f, 9f, -1f, 6f, 9f, 6f, 18, 14)),

            // The chest fluff, its own bone so it can wear the mane patch — authored standing,
            // like the body it thickens.
            LaidDown("mane", "body", new Vector3(0f, 11f, 2f),
                Box(-4f, 14f, -1.5f, 8f, 6f, 7f, 21, 0)),

            Bone("head", "body", new Vector3(0f, 13f, -4f),
                Box(-3f, 10f, -10f, 6f, 6f, 4f, 0, 0),
                Box(-1.5f, 10f, -13f, 3f, 3f, 4f, 0, 10),
                Box(-3f, 16f, -8f, 2f, 2f, 1f, 16, 14),
                Box(1f, 16f, -8f, 2f, 2f, 1f, 16, 14, mirror: true)),

            Posed("tail", "body", new Vector3(0f, 12f, 5f), new Vector3(-40f, 0f, 0f),
                Box(-1f, 12f, 4f, 2f, 8f, 2f, 9, 18)),

            .. Legs("body", 2f, 8f, -3f, 3f, 2f, 8f, 0, 18),
        ]);

    /// <summary>A bone laid out at rest and then turned, reaching its own boxes only.</summary>
    private static CreatureBone Posed(
        string name, string parent, Vector3 pivot, Vector3 bindPose, params CreatureCube[] cubes) =>
        new(name, parent, pivot, Vector3.Zero, bindPose, cubes);

    /// <summary>
    /// The humanoid net: a head, a torso, two arms and two legs, laid out where the reference lays
    /// them so a pack's zombie skin fits ours.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>One builder for both of the walking dead</b>, because they are the same skeleton wearing
    /// different colours — which is the whole argument the block families already make. It is also
    /// the <see cref="PlayerModel"/> net, so a player skin would fit one of these and a zombie skin
    /// would fit a player: the contract is signed once and every humanoid in the game inherits it.
    /// </remarks>
    /// <param name="reach">
    /// Degrees the arms are held out in front. ⚠ A <em>bind pose</em>, not a rotation — an arm has
    /// nothing hanging off it, and using the field that carries children would be a rule that only
    /// happens to work because this model has no hands.
    /// </param>
    private static CreatureModel Humanoid(string name, float reach) => new(
        name, 64, 64,
        [
            Bone("body", "", new Vector3(0f, 24f, 0f),
                Box(-4f, 12f, -2f, 8f, 12f, 4f, 16, 16)),

            Bone("head", "body", new Vector3(0f, 24f, 0f),
                Box(-4f, 24f, -4f, 8f, 8f, 8f, 0, 0)),

            // ⚠ The pivot is the shoulder, not the hand. An arm turned about its far end swings the
            // shoulder out through the chest, which reads as a broken model rather than as a reach.
            Posed("arm0", "body", new Vector3(-5f, 22f, 0f), new Vector3(-reach, 0f, 0f),
                Box(-8f, 10f, -2f, 4f, 12f, 4f, 40, 16)),

            Posed("arm1", "body", new Vector3(5f, 22f, 0f), new Vector3(-reach, 0f, 0f),
                Box(4f, 10f, -2f, 4f, 12f, 4f, 32, 48, mirror: true)),

            Bone("leg0", "body", new Vector3(-2f, 12f, 0f),
                Box(-4f, 0f, -2f, 4f, 12f, 4f, 0, 16)),

            Bone("leg1", "body", new Vector3(2f, 12f, 0f),
                Box(0f, 0f, -2f, 4f, 12f, 4f, 16, 48, mirror: true)),
        ]);

    /// <summary>The one that comes at you with its arms out.</summary>
    public static CreatureModel Zombie() => Humanoid("zombie", 78f);

    /// <summary>
    /// Its two cousins: the one the sea kept, and the one the desert dried.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>The same humanoid net, which is the block families' own argument</b> — what tells the
    /// three walking dead apart is <see cref="CreatureArt"/>'s palette and one behaviour each: the
    /// drowned spawns in water and burns out of it, the husk walks through noon untouched. The
    /// drowned's arms hang a little lower; water does that.
    /// </remarks>
    public static CreatureModel Drowned() => Humanoid("drowned", 64f);

    public static CreatureModel Husk() => Humanoid("husk", 78f);

    /// <summary>And the one that is all bone, whose arms hold something it has not got yet.</summary>
    /// <remarks>
    /// ⚠ Thinner limbs than the zombie's, which is the only difference in the geometry — everything
    /// else that tells them apart is <c>CreatureArt</c>'s. Two units rather than four, and the net
    /// says so, so a pack's skeleton skin lands on boxes the right size for it.
    /// </remarks>
    public static CreatureModel Skeleton() => new(
        "skeleton", 64, 64,
        [
            Bone("body", "", new Vector3(0f, 24f, 0f),
                Box(-4f, 12f, -2f, 8f, 12f, 4f, 16, 16)),

            Bone("head", "body", new Vector3(0f, 24f, 0f),
                Box(-4f, 24f, -4f, 8f, 8f, 8f, 0, 0)),

            Posed("arm0", "body", new Vector3(-5f, 22f, 0f), new Vector3(-84f, 0f, 0f),
                Box(-7f, 10f, -1f, 2f, 12f, 2f, 40, 16)),

            Posed("arm1", "body", new Vector3(5f, 22f, 0f), new Vector3(-84f, 0f, 0f),
                Box(5f, 10f, -1f, 2f, 12f, 2f, 32, 48, mirror: true)),

            Bone("leg0", "body", new Vector3(-2f, 12f, 0f),
                Box(-3f, 0f, -1f, 2f, 12f, 2f, 0, 16)),

            Bone("leg1", "body", new Vector3(2f, 12f, 0f),
                Box(1f, 0f, -1f, 2f, 12f, 2f, 16, 48, mirror: true)),
        ]);

    /// <summary>
    /// The eight-legged one, whose legs are the whole silhouette.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Each leg is two boxes, out and then down, rather than one box turned.</b> A single
    /// angled box is what the reference does and it needs the leg's far end to land exactly on the
    /// floor — which is a rotation, a pivot and a length that have to agree to a tenth of a unit, in
    /// a table nobody can check by reading. An L reaches the ground by construction, and
    /// <c>StarterCreatures.Validate</c>'s "feet on the ground" claim is then something the geometry
    /// makes true rather than something a number was tuned until it was.
    /// </remarks>
    public static CreatureModel Spider()
    {
        var bones = new List<CreatureBone>
        {
            Bone("body", "", new Vector3(0f, 9f, 3f),
                Box(-5f, 5f, -3f, 10f, 8f, 12f, 0, 12)),

            Bone("neck", "body", new Vector3(0f, 9f, -3f),
                Box(-3f, 6f, -6f, 6f, 6f, 6f, 0, 0)),

            Bone("head", "neck", new Vector3(0f, 9f, -6f),
                Box(-4f, 5f, -14f, 8f, 8f, 8f, 32, 4)),
        };

        // Four a side, fanning fore and aft. The upper arm reads as the span and the lower as the
        // foot, and both take the one leg patch — eight legs, one drawing, exactly as the quadrupeds
        // share theirs.
        for (var i = 0; i < 4; i++)
        {
            var z = -1f + i * 3f;
            var out0 = 3f + MathF.Abs(1.5f - i) * 1.5f;

            for (var side = 0; side < 2; side++)
            {
                var sign = side == 0 ? -1f : 1f;
                var mirror = side == 1;

                bones.Add(Bone($"leg{i * 2 + side}", "body", new Vector3(sign * 5f, 9f, z),
                    Box(sign > 0 ? 5f : -5f - out0, 8f, z - 1f, out0, 2f, 2f, 18, 0, mirror),
                    Box(sign > 0 ? 4f + out0 : -5f - out0, 0f, z - 1f, 2f, 9f, 2f, 18, 0, mirror)));
            }
        }

        return new CreatureModel("spider", 64, 32, [.. bones]);
    }

    /// <summary>
    /// The one that is mostly liquid: a core with a face, inside a shell of gel.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>The reference cuts this creature as two skeletons</b> — the core on one file and
    /// the gel shell on its own "armor" file, drawn over it translucent. Ours is one model carrying
    /// both, because they share a sheet and a creature is one thing here; the shell's box is the
    /// armor file's, to the unit.</para>
    /// <para>⛔ <b>The shell is a bone named <c>gel</c> and <see cref="Textures.CreatureArt"/> leaves
    /// it unpainted on purpose.</b> The entity shader is cutout, not blending — a translucent shell
    /// cannot be drawn honestly, and an opaque one would hide the face this thing aims at you. Left
    /// transparent it is discarded per pixel, so ours is the core with its eyes; a pack whose gel is
    /// painted more than half solid gets its full cube back, which is the better of the two readings
    /// under a rule that cannot blend.</para>
    /// </remarks>
    public static CreatureModel Slime() => new(
        "slime", 64, 32,
        [
            Bone("core", "", new Vector3(0f, 4f, 0f),
                Box(-3f, 1f, -3f, 6f, 6f, 6f, 0, 16)),

            Bone("eye0", "core", new Vector3(0f, 4f, 0f),
                Box(-3.3f, 4f, -3.5f, 2f, 2f, 2f, 32, 0)),

            Bone("eye1", "core", new Vector3(0f, 4f, 0f),
                Box(1.3f, 4f, -3.5f, 2f, 2f, 2f, 32, 4)),

            Bone("mouth", "core", new Vector3(0f, 4f, 0f),
                Box(0f, 2f, -3.5f, 1f, 1f, 1f, 32, 8)),

            Bone("gel", "core", new Vector3(0f, 4f, 0f),
                Box(-4f, 0f, -4f, 8f, 8f, 8f, 0, 0)),
        ]);

    /// <summary>
    /// The one that comes close and lights itself: an upright trunk on four stub legs.
    /// </summary>
    /// <remarks>
    /// <para>⚠ <b>The trunk stands on end</b> — 8 wide, 12 tall, 4 deep — unlike every quadruped
    /// here, so there is no <c>LaidDown</c> anywhere on it: the reference authors it standing and
    /// so do we. All four legs read one patch and none is mirrored, exactly as the reference's
    /// file has it, so a pack's sheet lands face for face.</para>
    /// <para>Its face is <see cref="Textures.CreatureArt"/>'s grim one — our own drawing on the
    /// reference's net, the same split as everywhere else.</para>
    /// </remarks>
    public static CreatureModel Crawler() => new(
        "crawler", 64, 32,
        [
            Bone("body", "", new Vector3(0f, 6f, 0f),
                Box(-4f, 6f, -2f, 8f, 12f, 4f, 16, 16)),

            Bone("head", "body", new Vector3(0f, 18f, 0f),
                Box(-4f, 18f, -4f, 8f, 8f, 8f, 0, 0)),

            Bone("leg0", "body", new Vector3(-2f, 6f, 4f),
                Box(-4f, 0f, 2f, 4f, 6f, 4f, 0, 16)),

            Bone("leg1", "body", new Vector3(2f, 6f, 4f),
                Box(0f, 0f, 2f, 4f, 6f, 4f, 0, 16)),

            Bone("leg2", "body", new Vector3(-2f, 6f, -4f),
                Box(-4f, 0f, -6f, 4f, 6f, 4f, 0, 16)),

            Bone("leg3", "body", new Vector3(2f, 6f, -4f),
                Box(0f, 0f, -6f, 4f, 6f, 4f, 0, 16)),
        ]);

    /// <summary>
    /// The tall one: a head and a trunk on limbs thirty units long, all four off one patch.
    /// </summary>
    /// <remarks>
    /// <para>⚠ <b>The reference authors this creature around engine-side offsets</b> — its legs run
    /// four units below its own zero and the whole model is shifted at render time. Ours is rebuilt
    /// standing on 0 with every box the same SIZE at the same uv, which is the part a pack's sheet
    /// cares about; where a box sits in space is a 3D fact the sheet never sees.</para>
    /// <para>⚠ The jaw layer at uv(0,16) is deliberately not carried: the reference draws it
    /// half a unit INSIDE the head, invisible until a scream animation drops it, and we have
    /// neither the animation nor a blending shader to layer it with. The face is
    /// <see cref="Textures.CreatureArt"/>'s pale-eyed one.</para>
    /// </remarks>
    public static CreatureModel Farwalker() => new(
        "farwalker", 64, 32,
        [
            Bone("body", "", new Vector3(0f, 42f, 0f),
                Box(-4f, 30f, -2f, 8f, 12f, 4f, 32, 16)),

            Bone("head", "body", new Vector3(0f, 42f, 0f),
                Box(-4f, 42f, -4f, 8f, 8f, 8f, 0, 0)),

            Bone("arm0", "body", new Vector3(-3f, 40f, 0f),
                Box(-4f, 12f, -1f, 2f, 30f, 2f, 56, 0)),

            Bone("arm1", "body", new Vector3(3f, 40f, 0f),
                Box(2f, 12f, -1f, 2f, 30f, 2f, 56, 0, mirror: true)),

            Bone("leg0", "body", new Vector3(-2f, 30f, 0f),
                Box(-3f, 0f, -1f, 2f, 30f, 2f, 56, 0)),

            Bone("leg1", "body", new Vector3(2f, 30f, 0f),
                Box(1f, 0f, -1f, 2f, 30f, 2f, 56, 0, mirror: true)),
        ]);

    /// <summary>
    /// The small quick one: a tilted trunk on haunches, with the ears that are half its height.
    /// </summary>
    /// <remarks>
    /// ⚠ Nearly every bone carries a bind pose — the trunk, haunches and tail lean back twenty
    /// degrees, the front legs ten, each ear fifteen out sideways — and each reaches its own boxes
    /// only, exactly as the reference authors it. Drawn at 0.6, the reference's own figure.
    /// </remarks>
    public static CreatureModel Rabbit() => new(
        "rabbit", 64, 32,
        [
            Posed("body", "", new Vector3(0f, 5f, 8f), new Vector3(-20f, 0f, 0f),
                Box(-3f, 2f, -2f, 6f, 5f, 10f, 0, 0, mirror: true)),

            Bone("head", "body", new Vector3(0f, 8f, -1f),
                Box(-2.5f, 8f, -6f, 5f, 4f, 5f, 32, 0, mirror: true)),

            Posed("earRight", "body", new Vector3(0f, 8f, -1f), new Vector3(0f, -15f, 0f),
                Box(-2.5f, 12f, -2f, 2f, 5f, 1f, 58, 0, mirror: true)),

            Posed("earLeft", "body", new Vector3(0f, 8f, -1f), new Vector3(0f, 15f, 0f),
                Box(0.5f, 12f, -2f, 2f, 5f, 1f, 52, 0, mirror: true)),

            Bone("nose", "body", new Vector3(0f, 8f, -1f),
                Box(-0.5f, 9.5f, -6.5f, 1f, 1f, 1f, 32, 9, mirror: true)),

            Posed("haunchLeft", "body", new Vector3(3f, 6.5f, 3.7f), new Vector3(-20f, 0f, 0f),
                Box(2f, 2.5f, 3.7f, 2f, 4f, 5f, 16, 15, mirror: true)),

            Posed("haunchRight", "body", new Vector3(-3f, 6.5f, 3.7f), new Vector3(-20f, 0f, 0f),
                Box(-4f, 2.5f, 3.7f, 2f, 4f, 5f, 30, 15, mirror: true)),

            Bone("rearFootLeft", "body", new Vector3(3f, 6.5f, 3.7f),
                Box(2f, 0f, 0f, 2f, 1f, 7f, 8, 24, mirror: true)),

            Bone("rearFootRight", "body", new Vector3(-3f, 6.5f, 3.7f),
                Box(-4f, 0f, 0f, 2f, 1f, 7f, 26, 24, mirror: true)),

            Posed("frontLegLeft", "body", new Vector3(3f, 7f, -1f), new Vector3(-10f, 0f, 0f),
                Box(2f, 0f, -2f, 2f, 7f, 2f, 8, 15, mirror: true)),

            Posed("frontLegRight", "body", new Vector3(-3f, 7f, -1f), new Vector3(-10f, 0f, 0f),
                Box(-4f, 0f, -2f, 2f, 7f, 2f, 0, 15, mirror: true)),

            Posed("tail", "body", new Vector3(0f, 4f, 7f), new Vector3(-20f, 0f, 0f),
                Box(-1.5f, 2.5f, 7f, 3f, 3f, 2f, 52, 6, mirror: true)),
        ]);

    /// <summary>
    /// The rust-red one: a wolf's posture wearing sharper lines and a brush of a tail.
    /// </summary>
    /// <remarks>
    /// ⚠ The reference's file opens with two empty rigging bones (<c>world</c>, <c>root</c>) and a
    /// second whole head for its sleeping pose; neither is carried — an empty bone draws nothing
    /// and we do not sleep. ⚠ Its first ear reads uv (0,0), overlapping the skull's own patch —
    /// the reference's file does exactly this, and painting ears after the skull is what resolves
    /// it, the wolf's own arrangement.
    /// </remarks>
    public static CreatureModel Fox() => new(
        "fox", 64, 32,
        [
            LaidDown("body", "", new Vector3(0f, 8f, 0f),
                Box(-3f, 0f, -3f, 6f, 11f, 6f, 30, 15)),

            Bone("head", "body", new Vector3(0f, 8f, -3f),
                Box(-4f, 4f, -9f, 8f, 6f, 6f, 0, 0),
                Box(-4f, 10f, -8f, 2f, 2f, 1f, 0, 0),
                Box(2f, 10f, -8f, 2f, 2f, 1f, 22, 0),
                Box(-2f, 4f, -12f, 4f, 2f, 3f, 0, 24)),

            Bone("leg0", "body", new Vector3(-3f, 6f, 6f),
                Box(-3.005f, 0f, 5f, 2f, 6f, 2f, 14, 24)),

            Bone("leg1", "body", new Vector3(1f, 6f, 6f),
                Box(1.005f, 0f, 5f, 2f, 6f, 2f, 22, 24)),

            Bone("leg2", "body", new Vector3(-3f, 6f, -1f),
                Box(-3.005f, 0f, -2f, 2f, 6f, 2f, 14, 24)),

            Bone("leg3", "body", new Vector3(1f, 6f, -1f),
                Box(1.005f, 0f, -2f, 2f, 6f, 2f, 22, 24)),

            Posed("tail", "body", new Vector3(0f, 8f, 7f), new Vector3(80f, 0f, 0f),
                Box(-2f, -2f, 4.75f, 4f, 9f, 5f, 28, 0)),
        ]);

    /// <summary>
    /// The quiet one: long and low, on a two-jointed tail.
    /// </summary>
    /// <remarks>
    /// ⚠ Its trunk and both tail joints are authored on end and laid down by nineties, each
    /// reaching its own box only. Drawn at 0.8, the reference's own figure.
    /// </remarks>
    public static CreatureModel Cat() => new(
        "cat", 64, 32,
        [
            LaidDown("body", "", new Vector3(0f, 7f, 1f),
                Box(-2f, -1f, -2f, 4f, 16f, 6f, 20, 0)),

            Bone("head", "body", new Vector3(0f, 9f, -9f),
                Box(-2.5f, 7f, -12f, 5f, 4f, 5f, 0, 0),
                Box(-1.5f, 7.02f, -13f, 3f, 2f, 2f, 0, 24),
                Box(-2f, 11f, -9f, 1f, 1f, 2f, 0, 10),
                Box(1f, 11f, -9f, 1f, 1f, 2f, 6, 10)),

            Posed("tail1", "body", new Vector3(0f, 9f, 8f), new Vector3(90f, 0f, 0f),
                Box(-0.5f, 1f, 8f, 1f, 8f, 1f, 0, 15)),

            Posed("tail2", "tail1", new Vector3(0f, 9f, 16f), new Vector3(90f, 0f, 0f),
                Box(-0.5f, 1f, 16f, 1f, 8f, 1f, 4, 15)),

            Bone("backLegL", "body", new Vector3(1.1f, 6f, 7f),
                Box(0.1f, 0f, 6f, 2f, 6f, 2f, 8, 13)),

            Bone("backLegR", "body", new Vector3(-1.1f, 6f, 7f),
                Box(-2.1f, 0f, 6f, 2f, 6f, 2f, 8, 13)),

            Bone("frontLegL", "body", new Vector3(1.2f, 10f, -4f),
                Box(0.2f, 0.2f, -5f, 2f, 10f, 2f, 40, 0)),

            Bone("frontLegR", "body", new Vector3(-1.2f, 10f, -4f),
                Box(-2.2f, 0.2f, -5f, 2f, 10f, 2f, 40, 0)),
        ]);

    /// <summary>
    /// The cave's own: ears, spread wings, and the long tail membrane, cut for a 64x64 sheet.
    /// </summary>
    /// <remarks>
    /// <para>⚠ The reference authors it around a hanging pivot with its tail eight units below
    /// zero; ours is the same set of boxes stood up so the membrane's tip is the lowest point at
    /// 0 — position is a 3D fact the sheet never sees. The head is dropped half a unit onto the
    /// body: the reference leaves a one-unit neck gap, which is exactly the assembly margin, and
    /// a check on its own boundary agrees with anything.</para>
    /// <para>Drawn at 0.35, the reference client's own figure — the boxes stay sheet-true and
    /// the animal comes out bat-sized.</para>
    /// </remarks>
    public static CreatureModel Bat() => new(
        "bat", 64, 64,
        [
            Bone("body", "", new Vector3(0f, 24f, 0f),
                Box(-3f, 16f, -3f, 6f, 12f, 6f, 0, 16),
                Box(-5f, 0f, 0f, 10f, 16f, 1f, 0, 34)),

            Bone("head", "body", new Vector3(0f, 28f, 0f),
                Box(-3f, 28.5f, -3f, 6f, 6f, 6f, 0, 0)),

            Bone("rightEar", "head", new Vector3(0f, 32f, 0f),
                Box(-4f, 34f, -2f, 3f, 4f, 1f, 24, 0)),

            Bone("leftEar", "head", new Vector3(0f, 32f, 0f),
                Box(1f, 34f, -2f, 3f, 4f, 1f, 24, 0, mirror: true)),

            Bone("rightWing", "body", new Vector3(0f, 24f, 0f),
                Box(-12f, 15f, 1.5f, 10f, 16f, 1f, 42, 0)),

            Bone("rightWingTip", "rightWing", new Vector3(-12f, 31f, 1.5f),
                Box(-20f, 18f, 1.5f, 8f, 12f, 1f, 24, 16)),

            Bone("leftWing", "body", new Vector3(0f, 24f, 0f),
                Box(2f, 15f, 1.5f, 10f, 16f, 1f, 42, 0, mirror: true)),

            Bone("leftWingTip", "leftWing", new Vector3(12f, 31f, 1.5f),
                Box(12f, 18f, 1.5f, 8f, 12f, 1f, 24, 16, mirror: true)),
        ]);

    /// <summary>
    /// The water's own: a mantle over eight tentacles hung in a ring.
    /// </summary>
    /// <remarks>
    /// ⚠ Every tentacle is one box and one patch, turned about its own pivot by its bearing on
    /// the ring — the y-rotation case that pinned <see cref="CreatureMesh.Turn"/>'s sign. The
    /// reference authors it floating around zero; ours stands on 0 by the same argument as the
    /// bat. No face in v1, and that is a noted hole rather than an accident: its eyes belong on
    /// the mantle's sides, which is a drawing the painter does not yet make.
    /// </remarks>
    public static CreatureModel Squid()
    {
        var bones = new List<CreatureBone>
        {
            Bone("body", "", new Vector3(0f, 25f, 0f),
                Box(-6f, 17f, -6f, 12f, 16f, 12f, 0, 0)),
        };

        // Eight around the rim: the four cardinals and the four diagonals, each turned to face
        // its own radius, exactly as the reference lays them.
        (float X, float Z, float Turn)[] ring =
        [
            (5f, 0f, 90f), (3.5f, 3.5f, 45f), (0f, 5f, 0f), (-3.5f, 3.5f, -45f),
            (-5f, 0f, -90f), (-3.5f, -3.5f, -135f), (0f, -5f, -180f), (3.5f, -3.5f, -225f),
        ];

        for (var i = 0; i < ring.Length; i++)
        {
            var (x, z, turn) = ring[i];
            bones.Add(Posed($"tentacle{i + 1}", "body", new Vector3(x, 18f, z), new Vector3(0f, turn, 0f),
                Box(x - 1f, 0f, z - 1f, 2f, 18f, 2f, 48, 0)));
        }

        return new CreatureModel("squid", 64, 32, [.. bones]);
    }

    /// <summary>Every creature that ships with the game, by our name for it.</summary>
    public static IReadOnlyList<CreatureModel> All { get; } =
        [Cow(), Pig(), Sheep(), Chicken(), Wolf(), Zombie(), Drowned(), Husk(), Skeleton(), Spider(),
         Slime(), Crawler(), Farwalker(), Rabbit(), Fox(), Cat(), Bat(), Squid()];

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
            // digit, which is the failure a table of numbers actually has. ⚠ The ceiling is the
            // farwalker's 50 with a little headroom — it is the tallest thing we ship on purpose.
            if (size.Y is < 6f or > 56f)
                faults.Add($"{model.Name} is {size.Y:F0} units tall, which is not an animal");
        }

        return faults;
    }
}
