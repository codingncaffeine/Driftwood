using System.Numerics;

namespace Driftwood.Core.Entities;

/// <summary>One bone's share of a creature's mesh: what to draw, and what it hangs off.</summary>
/// <param name="Parent">
/// Where in this array the bone above it sits, or −1 for a root. An index rather than a name because
/// it is read once a frame per bone; the names are resolved when the mesh is built.
/// </param>
/// <param name="First">Where this bone's indices start, and <paramref name="Count"/> how many.</param>
public readonly record struct CreaturePart(
    string Name, int Parent, Vector3 Pivot, Vector3 Rotation, Vector3 BindPose, int First, int Count);

/// <summary>
/// A creature's skeleton turned into something drawable: boxes as quads, one range per bone.
/// </summary>
/// <remarks>
/// <para>The creature counterpart of <see cref="PlayerModel.Build"/> and
/// <see cref="PlayerRig"/> together, and it shares their machinery rather than restating it —
/// <see cref="PlayerModel.EmitBox"/> cuts every box out of its net, and the matrix chain is the same
/// shape as the player's with names in place of an enum.</para>
/// <para><b>Vertices are in the bone's own space, about its pivot</b>, and every rotation lives in a
/// matrix. ⛔ Baking a bone's angles into its vertices draws exactly the same first frame and can
/// never be animated afterwards, which is the sort of thing that is only discovered a phase later.
/// </para>
/// <para><b>Model space is the format's:</b> +y up, −z forward, +x the creature's own right, sixteen
/// units to a block.</para>
/// </remarks>
public sealed class CreatureMesh
{
    /// <summary>
    /// Blocks per model unit.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Not <see cref="PlayerModel.Unit"/>, and the difference is deliberate.</b> The player's is
    /// pinned to <c>PlayerBody.Height</c> because the body the physics collides with is a game
    /// constant and the drawing has to agree with it. A creature has no such constant — how big a cow
    /// is <em>is</em> whatever its skeleton says — so the format's own sixteen-to-a-block is the
    /// definition, and <see cref="PosedBounds"/> is where its collision box will come from. Two
    /// answers to "how big is it" cannot disagree if there is only one.
    /// </remarks>
    public const float Unit = 1f / 16f;

    public string Name { get; }
    public ModelVertex[] Vertices { get; }
    public uint[] Indices { get; }

    /// <summary>Every bone, <b>parents before children</b>, which is what makes one pass enough.</summary>
    public CreaturePart[] Parts { get; }

    /// <summary>The sheet this was cut for, in pixels. Zero when nothing was loaded.</summary>
    public int SheetWidth { get; }

    public int SheetHeight { get; }

    /// <summary>How many net texels tall the sheet was taken to be. See <see cref="NetHeight"/>.</summary>
    public float NetTexels { get; }

    /// <summary>What the skeleton itself declared, which is not always what was used.</summary>
    public int DeclaredHeight { get; }

    public int TriangleCount => Indices.Length / 3;

    private CreatureMesh(
        string name, ModelVertex[] vertices, uint[] indices, CreaturePart[] parts,
        int sheetWidth, int sheetHeight, float netTexels, int declaredHeight)
    {
        Name = name;
        Vertices = vertices;
        Indices = indices;
        Parts = parts;
        SheetWidth = sheetWidth;
        SheetHeight = sheetHeight;
        NetTexels = netTexels;
        DeclaredHeight = declaredHeight;
    }

    /// <summary>
    /// Cuts a skeleton out of the sheet a pack painted for it.
    /// </summary>
    /// <param name="sheetWidth">
    /// The skin's own pixel size, or zero when there is none — in which case the skeleton's declared
    /// net is used and the mesh is still sound, just wearing nothing yet.
    /// </param>
    public static CreatureMesh Build(CreatureModel model, int sheetWidth = 0, int sheetHeight = 0)
    {
        var order = Order(model.Bones);
        var netTexels = NetHeight(model, sheetWidth, sheetHeight);
        var sheet = new Vector2(MathF.Max(model.SheetWidth, 1f), MathF.Max(netTexels, 1f));

        var vertices = new List<ModelVertex>();
        var indices = new List<uint>();
        var parts = new CreaturePart[order.Length];

        // Where each bone ended up, so a child can name its parent by position.
        var place = new int[model.Bones.Length];
        for (var i = 0; i < order.Length; i++) place[order[i]] = i;

        for (var i = 0; i < order.Length; i++)
        {
            var bone = model.Bones[order[i]];
            var first = indices.Count;

            foreach (var cube in bone.Cubes)
            {
                var inflate = new Vector3(cube.Inflate);

                // ⛳ The pivot comes off here and goes into the matrix. That is the whole of "bone
                // space": the bone turns about its own origin, so whatever turns it needs to know
                // nothing about where on the creature it sits.
                var min = cube.Origin - bone.Pivot - inflate;
                var max = cube.Origin + cube.Size - bone.Pivot + inflate;

                PlayerModel.EmitBox(
                    min, max, cube.Size, cube.U, cube.V, cube.Mirror, Unit, sheet, vertices, indices);
            }

            var parent = -1;
            if (bone.Parent.Length > 0)
            {
                var at = Array.FindIndex(model.Bones, b => b.Name == bone.Parent);
                if (at >= 0) parent = place[at];
            }

            parts[i] = new CreaturePart(
                bone.Name, parent, bone.Pivot, bone.Rotation, bone.BindPose,
                first, indices.Count - first);
        }

        return new CreatureMesh(
            model.Name, [.. vertices], [.. indices], parts,
            sheetWidth, sheetHeight, netTexels, model.SheetHeight);
    }

    /// <summary>
    /// How many net texels tall to treat the sheet as, which is not always what the skeleton says.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>Scale the net by the sheet's WIDTH alone and ignore spare height.</b> Some packs
    /// ship a creature's sheet padded out to a square — a cow at 1024×1024 where the net is 2:1, with
    /// the art in the top half and nothing under it. Scaling both axes to fit stretches every patch
    /// down the sheet and the animal wears its own texture inside out, which throws nothing and looks
    /// like a texturing bug rather than an arithmetic one.</para>
    /// <para>⚠ <b>Never smaller than the net, which is the other half of the rule.</b> A sheet that is
    /// <em>shorter</em> than its net's proportions is not a padded one — it is a genuine version
    /// mismatch, an old skeleton against new art, and there the width rule would run half the patches
    /// off the bottom edge to sample whatever the last row happens to be. Falling back to the
    /// declared net keeps every patch on the sheet: still wrong, but wrong by a squash rather than by
    /// reading the wrong pixels entirely. The mismatch is reported either way — it wants a different
    /// skeleton, not a different scale.</para>
    /// </remarks>
    public static float NetHeight(CreatureModel model, int sheetWidth, int sheetHeight)
    {
        if (sheetWidth <= 0 || sheetHeight <= 0 || model.SheetWidth <= 0) return model.SheetHeight;

        return MathF.Max(model.SheetHeight, sheetHeight * (float)model.SheetWidth / sheetWidth);
    }

    /// <summary>
    /// The format's degrees as a turn in our space.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>x and z run the other way; y does not.</b> Measured against a real install rather
    /// than reasoned about, because it is exactly the sort of convention that is fifty-fifty on paper
    /// and obvious the moment something is on screen:</para>
    /// <list type="bullet">
    /// <item><b>x</b> — a cow's torso is drawn upright and laid down by a ninety. Only one sign puts
    /// its underside at the top of the legs and its front face at the back of the head; the other
    /// leaves a four-unit gap at both.</item>
    /// <item><b>z</b> — a hoglin's ears are flat flaps that droop. Only one sign hangs them down; the
    /// other stands them up like horns. Both ears agree, and so do a piglin's.</item>
    /// <item><b>y</b> — a squid's eight tentacles are a ring, each turned by ninety degrees minus its
    /// own bearing. Only one sign leaves all eight facing the same way relative to their own radius;
    /// the other has them facing outward at two of the eight and scattered at the rest.</item>
    /// </list>
    /// <para>⚠ <b>The order is the format's and the install does not pin it.</b> Thirty-three bones in
    /// it carry more than one non-zero angle and every one of those is a mirrored pair — an ear, a
    /// fin — where both orders give the same picture. So this follows the reference's own x, then y,
    /// then z, and if a creature ever comes out wrong at two angles at once, this line is the
    /// suspect.</para>
    /// </remarks>
    public static Matrix4x4 Turn(Vector3 degrees) =>
        Matrix4x4.CreateRotationX(float.DegreesToRadians(-degrees.X))
        * Matrix4x4.CreateRotationY(float.DegreesToRadians(degrees.Y))
        * Matrix4x4.CreateRotationZ(float.DegreesToRadians(-degrees.Z));

    /// <summary>
    /// Where every bone sits, given where the creature is standing.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>Two chains, not one, and that is the whole of the bind pose.</b> What a bone's own
    /// boxes are drawn with is not what its children hang off. A bone's <c>Rotation</c> turns
    /// everything below it — a hoglin's ears are children of its head and have to travel with it —
    /// while its <c>BindPose</c> lays out that bone's boxes alone and reaches nothing. Sixteen models
    /// in a real install lay a quadruped's torso down that way and author its head and legs where
    /// they finally stand, so a single chain carrying the torso's ninety degrees on down puts the
    /// head under the belly and the legs through the floor.</para>
    /// <para>One pass is enough because <see cref="Parts"/> is ordered parents first.</para>
    /// </remarks>
    public Matrix4x4[] Pose(Matrix4x4 root)
    {
        var hang = new Matrix4x4[Parts.Length];
        var draw = new Matrix4x4[Parts.Length];

        for (var i = 0; i < Parts.Length; i++)
        {
            var part = Parts[i];

            // The pivot is measured on the whole creature rather than from the bone above, so what
            // is carried down is the step between the two. Same arithmetic the player's rig does
            // when it hangs a head off a torso.
            var parentPivot = part.Parent >= 0 ? Parts[part.Parent].Pivot : Vector3.Zero;

            hang[i] = Turn(part.Rotation)
                    * Matrix4x4.CreateTranslation((part.Pivot - parentPivot) * Unit)
                    * (part.Parent >= 0 ? hang[part.Parent] : root);

            draw[i] = Turn(part.BindPose) * hang[i];
        }

        return draw;
    }

    /// <summary>
    /// The box the creature actually fills, once it is standing the way it will be drawn.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Not <see cref="CreatureModel.Bounds"/>, which measures the boxes where they were
    /// authored.</b> A quadruped is drawn with an upright torso and laid down by its bind pose, so
    /// the unposed extent of every one of them is a cow standing on its hind legs: too tall, too
    /// short front to back, and wrong in the one direction anybody would size a collision box from.
    /// </remarks>
    public (Vector3 Min, Vector3 Max) PosedBounds()
    {
        var pose = Pose(Matrix4x4.Identity);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var any = false;

        for (var i = 0; i < Parts.Length; i++)
        {
            var (lo, hi) = PartBounds(i, pose);
            if (Parts[i].Count == 0) continue;

            min = Vector3.Min(min, lo);
            max = Vector3.Max(max, hi);
            any = true;
        }

        return any ? (min, max) : (Vector3.Zero, Vector3.Zero);
    }

    /// <summary>
    /// True when every part of the creature touches another one, which is what says this file poses
    /// it and nothing else has to.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>The one question worth asking of a skeleton before wearing it.</b> An install
    /// carries the same creature modelled several times over, and two of the three eras do not carry
    /// their own pose at all: the oldest is from before there were skeletons, when the engine
    /// assembled each animal in hardened code, and the newest moved the rest pose out into an
    /// animation file beside it. Both parse perfectly, both have the right bones and the right nets,
    /// and both come out as a heap of disconnected boxes — one real cow has its torso authored
    /// seventeen units above its own legs with nothing in the file to bring it down.</para>
    /// <para>⛳ <b>So it is asked of the geometry</b>, rather than guessed from the file's name or its
    /// version — the two things that look like they would answer it and do not. The newest model is
    /// the one that carries its pose the least. A part that touches nothing is a part something else
    /// was going to move.</para>
    /// <para>The margin is a whole model unit: parts that meet exactly, like a torso resting on the
    /// tops of its legs, have to count as touching, and a number that has been through a matrix
    /// chain does not land on the same value twice.</para>
    /// </remarks>
    public bool Assembled(float margin = 1f)
    {
        var pose = Pose(Matrix4x4.Identity);
        var boxes = new List<(Vector3 Min, Vector3 Max)>(Parts.Length);

        for (var i = 0; i < Parts.Length; i++)
        {
            if (Parts[i].Count == 0) continue;
            boxes.Add(PartBounds(i, pose));
        }

        if (boxes.Count < 2) return true;

        var reach = margin * Unit;

        for (var i = 0; i < boxes.Count; i++)
        {
            var touches = false;

            for (var j = 0; j < boxes.Count && !touches; j++)
            {
                if (i == j) continue;

                touches = boxes[i].Min.X - reach <= boxes[j].Max.X && boxes[j].Min.X - reach <= boxes[i].Max.X
                       && boxes[i].Min.Y - reach <= boxes[j].Max.Y && boxes[j].Min.Y - reach <= boxes[i].Max.Y
                       && boxes[i].Min.Z - reach <= boxes[j].Max.Z && boxes[j].Min.Z - reach <= boxes[i].Max.Z;
            }

            if (!touches) return false;
        }

        return true;
    }

    /// <summary>One bone's extent, in blocks, under a pose <see cref="Pose"/> worked out.</summary>
    public (Vector3 Min, Vector3 Max) PartBounds(int part, Matrix4x4[] pose)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var found = false;

        var from = Parts[part].First;
        var to = from + Parts[part].Count;

        for (var i = from; i < to; i++)
        {
            var position = Vector3.Transform(Vertices[Indices[i]].Position, pose[part]);
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
            found = true;
        }

        return found ? (min, max) : (Vector3.Zero, Vector3.Zero);
    }

    /// <summary>
    /// Puts the bones in an order where nothing comes before what it hangs off.
    /// </summary>
    /// <remarks>
    /// A file lists its bones in whatever order it likes and a child very often comes first. Sorting
    /// once here is what lets a frame work the matrices out in a single sweep instead of recursing
    /// per bone. A bone whose parent chain never terminates cannot be placed at all and is taken as a
    /// root — <see cref="CreatureModel.Validate"/> is where that gets said out loud.
    /// </remarks>
    private static int[] Order(CreatureBone[] bones)
    {
        var placed = new bool[bones.Length];
        var order = new List<int>(bones.Length);

        while (order.Count < bones.Length)
        {
            var added = false;

            for (var i = 0; i < bones.Length; i++)
            {
                if (placed[i]) continue;

                if (bones[i].Parent.Length > 0)
                {
                    var parent = Array.FindIndex(bones, b => b.Name == bones[i].Parent);

                    // A parent that is not in the file leaves the bone hanging off the creature's
                    // own origin, which is the least surprising thing to do with it and is already
                    // reported as a fault.
                    if (parent >= 0 && !placed[parent]) continue;
                }

                placed[i] = true;
                order.Add(i);
                added = true;
            }

            if (added) continue;

            for (var i = 0; i < bones.Length; i++)
            {
                if (placed[i]) continue;
                placed[i] = true;
                order.Add(i);
            }
        }

        return [.. order];
    }

    // ── Checks. Everything below is about claims nothing on screen would tell you apart. ──

    /// <summary>The sample: a beast whose torso is drawn upright and laid down by its bind pose.</summary>
    /// <remarks>
    /// The measurements are a real quadruped's, which is the point — the two things asserted about it
    /// are that its torso comes to rest exactly on top of its legs and exactly behind its head, and
    /// those only land for one reading of the file. Round numbers of my own would agree with any
    /// reading at all.
    /// </remarks>
    private static CreatureModel Beast() => new(
        "beast", 64, 32,
        [
            new CreatureBone(
                "body", "", new Vector3(0f, 19f, 2f), Vector3.Zero, new Vector3(90f, 0f, 0f),
                [new CreatureCube(new Vector3(-6f, 11f, -5f), new Vector3(12f, 18f, 10f), 18, 4, false, 0f)]),

            new CreatureBone(
                "head", "body", new Vector3(0f, 20f, -8f), Vector3.Zero, Vector3.Zero,
                [new CreatureCube(new Vector3(-4f, 16f, -14f), new Vector3(8f, 8f, 6f), 0, 0, false, 0f)]),

            // Listed after its parent on one side and before it on the other would be the same
            // creature; a mirrored limb is here because mirroring reverses the winding as well as
            // the net, and a mesher that gets that wrong draws a leg inside out.
            new CreatureBone(
                "leg", "body", new Vector3(-4f, 12f, 7f), Vector3.Zero, Vector3.Zero,
                [new CreatureCube(new Vector3(-6f, 0f, 5f), new Vector3(4f, 12f, 4f), 0, 16, false, 0f)]),

            new CreatureBone(
                "leg_far", "body", new Vector3(4f, 12f, 7f), Vector3.Zero, Vector3.Zero,
                [new CreatureCube(new Vector3(2f, 0f, 5f), new Vector3(4f, 12f, 4f), 0, 16, true, 0f)]),
        ]);

    /// <summary>
    /// Checks a creature comes out of its skeleton standing up, wearing its own net.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>Every claim here is paired with what it would look like if the code did nothing.</b>
    /// A skeleton that is read, meshed and drawn wrong does not throw and does not come back empty —
    /// it comes back as an animal in a heap, and by then it is a rendering problem rather than an
    /// arithmetic one. The two that matter most are the bind pose, which has to be in the matrix and
    /// has to stop at the bone it is written on, and the winding, which has half its faces mirrored.
    /// </para>
    /// </remarks>
    public static List<string> Validate()
    {
        var faults = new List<string>();

        var beast = Beast();
        faults.AddRange(beast.Validate());

        var mesh = Build(beast, 64, 32);

        // ── Winding. Half of these boxes are mirrored, which reverses it. ──
        for (var t = 0; t < mesh.Indices.Length; t += 3)
        {
            var a = mesh.Vertices[mesh.Indices[t]];
            var b = mesh.Vertices[mesh.Indices[t + 1]];
            var c = mesh.Vertices[mesh.Indices[t + 2]];

            if (Vector3.Dot(Vector3.Cross(b.Position - a.Position, c.Position - a.Position), a.Normal) > 0f)
                continue;

            faults.Add($"triangle {t / 3} winds inward, so that face is drawn from the inside only");
            break;
        }

        var body = Array.FindIndex(mesh.Parts, p => p.Name == "body");
        var head = Array.FindIndex(mesh.Parts, p => p.Name == "head");
        var leg = Array.FindIndex(mesh.Parts, p => p.Name == "leg");

        if (body < 0 || head < 0 || leg < 0) { faults.Add("the sample lost a bone on the way in"); return faults; }

        // ── The bind pose is NOT in the vertices. The torso is authored upright: eighteen units of
        // it up and down, ten front to back. Baked, those two would already have swapped over. ──
        var restPose = new Matrix4x4[mesh.Parts.Length];
        Array.Fill(restPose, Matrix4x4.Identity);

        var (restLow, restHigh) = mesh.PartBounds(body, restPose);
        var restSize = (restHigh - restLow) / Unit;

        if (MathF.Abs(restSize.Y - 18f) > 0.01f || MathF.Abs(restSize.Z - 10f) > 0.01f)
        {
            faults.Add(
                $"the torso's own vertices measure {restSize.Y:F1} tall by {restSize.Z:F1} deep, not 18 by 10 "
                + "— its bind pose has been baked into them and can never be animated out");
        }

        // ── And it IS in the matrix. Two adjacencies, and both of them land or neither does: the
        // torso comes to rest on the top of the legs, and its front face meets the back of the head.
        // A sign the wrong way round opens a four-unit gap at both. ──
        var pose = mesh.Pose(Matrix4x4.Identity);

        var (bodyLow, bodyHigh) = mesh.PartBounds(body, pose);
        var (headLow, headHigh) = mesh.PartBounds(head, pose);
        var (legLow, legHigh) = mesh.PartBounds(leg, pose);

        if (MathF.Abs(bodyLow.Y / Unit - 12f) > 0.01f)
        {
            faults.Add(
                $"the laid-down torso's underside is at {bodyLow.Y / Unit:F2} where the legs reach "
                + $"{legHigh.Y / Unit:F2} — it is not standing on them");
        }

        if (MathF.Abs(bodyLow.Z / Unit + 8f) > 0.01f)
        {
            faults.Add(
                $"the laid-down torso's front is at {bodyLow.Z / Unit:F2} where the head's back is "
                + $"{headHigh.Z / Unit:F2} — the two do not meet");
        }

        // ── The bind pose stops at the bone it is written on. The head and the legs are children of
        // that torso and are authored where they finally stand, so they must not have moved at all.
        // Carried down, the head lands under the belly and below the ground. ──
        if (MathF.Abs(headLow.Y / Unit - 16f) > 0.01f || MathF.Abs(headLow.Z / Unit + 14f) > 0.01f)
        {
            faults.Add(
                $"the head is at y {headLow.Y / Unit:F2}, z {headLow.Z / Unit:F2} rather than 16, -14 "
                + "— the torso's bind pose was carried down to it");
        }

        if (MathF.Abs(legLow.Y / Unit) > 0.01f || MathF.Abs(legLow.Z / Unit - 5f) > 0.01f)
        {
            faults.Add(
                $"a leg is at y {legLow.Y / Unit:F2}, z {legLow.Z / Unit:F2} rather than 0, 5 "
                + "— the torso's bind pose was carried down to it");
        }

        // ── A bone's own rotation, on the other hand, has to reach everything below it. A limb eight
        // units in front of a trunk turned a quarter about y ends up eight units to one side; left
        // where it was, it is a hoglin's ears floating beside a head that has gone. ──
        var jointed = new CreatureModel(
            "jointed", 64, 64,
            [
                new CreatureBone(
                    "trunk", "", Vector3.Zero, new Vector3(0f, 90f, 0f), Vector3.Zero,
                    [new CreatureCube(new Vector3(-2f, -2f, -2f), new Vector3(4f, 4f, 4f), 0, 0, false, 0f)]),

                new CreatureBone(
                    "limb", "trunk", new Vector3(0f, 0f, -8f), Vector3.Zero, Vector3.Zero,
                    [new CreatureCube(new Vector3(-1f, -1f, -9f), new Vector3(2f, 2f, 2f), 0, 20, false, 0f)]),
            ]);

        var jointedMesh = Build(jointed, 64, 64);
        var jointedPose = jointedMesh.Pose(Matrix4x4.Identity);
        var limb = Array.FindIndex(jointedMesh.Parts, p => p.Name == "limb");
        var (limbLow, limbHigh) = jointedMesh.PartBounds(limb, jointedPose);
        var limbMiddle = (limbLow + limbHigh) * 0.5f / Unit;

        if (MathF.Abs(limbMiddle.X + 8f) > 0.01f || MathF.Abs(limbMiddle.Z) > 0.01f)
        {
            faults.Add(
                $"a limb on a quarter-turned trunk sits at x {limbMiddle.X:F2}, z {limbMiddle.Z:F2} rather "
                + "than -8, 0 — a bone's rotation is not reaching what hangs off it");
        }

        faults.AddRange(ValidateRing());
        faults.AddRange(ValidateSheet());
        return faults;
    }

    /// <summary>
    /// Checks the one turn that has no up or down to settle it: the one about the vertical.
    /// </summary>
    /// <remarks>
    /// <para>A ring of limbs is the case that pins it. The format arranges a squid's eight tentacles
    /// around a circle and turns each one by ninety degrees less its own bearing, so that every one
    /// of them presents the same face outward. <b>That symmetry only survives one sign.</b> Under the
    /// other, the turn reflects the bearing instead of cancelling it: two of the eight land right and
    /// the other six point off at angles that vary around the ring.</para>
    /// <para>⛳ It is asked as "do all eight agree with each other" rather than "does each face
    /// outward", because agreement is a property of the arrangement that no amount of restating this
    /// code can fake — and it fires just as loudly if the turn is dropped altogether.</para>
    /// </remarks>
    private static List<string> ValidateRing()
    {
        var faults = new List<string>();

        var bones = new CreatureBone[8];
        for (var i = 0; i < 8; i++)
        {
            var bearing = i * 45f;
            var radians = float.DegreesToRadians(bearing);
            var pivot = new Vector3(5f * MathF.Cos(radians), -7f, 5f * MathF.Sin(radians));

            bones[i] = new CreatureBone(
                $"tentacle{i}", "", pivot, new Vector3(0f, 90f - bearing, 0f), Vector3.Zero,
                [new CreatureCube(pivot + new Vector3(-1f, -18f, -1f), new Vector3(2f, 18f, 2f), 0, 0, false, 0f)]);
        }

        var mesh = Build(new CreatureModel("ring", 64, 64, bones), 64, 64);
        var pose = mesh.Pose(Matrix4x4.Identity);

        var first = 0f;
        for (var i = 0; i < mesh.Parts.Length; i++)
        {
            var facing = Vector3.TransformNormal(Vector3.UnitZ, pose[i]);
            var pivot = mesh.Parts[i].Pivot;

            // How far the limb's own facing is turned away from the direction it stands in.
            var offset = MathF.Atan2(facing.Z, facing.X) - MathF.Atan2(pivot.Z, pivot.X);
            while (offset <= -MathF.PI) offset += 2f * MathF.PI;
            while (offset > MathF.PI) offset -= 2f * MathF.PI;

            if (i == 0) { first = offset; continue; }

            if (MathF.Abs(offset - first) > 0.001f)
            {
                faults.Add(
                    $"a ring of limbs does not face consistently: the first is {float.RadiansToDegrees(first):F1}° "
                    + $"off its own bearing and {mesh.Parts[i].Name} is {float.RadiansToDegrees(offset):F1}° "
                    + "— the turn about the vertical runs the wrong way");
                break;
            }
        }

        return faults;
    }

    /// <summary>
    /// Checks a net is scaled by its sheet's width, and that the rule knows when to stop.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Three sheets, because the rule has to be right about all three and any two of them can be
    /// passed by doing nothing.</b> A sheet in proportion must come out unchanged; a padded square
    /// must have every patch in the top half of it; and a sheet that is too short for its net must
    /// <em>not</em> be corrected, because there the correction is what pushes half the patches off
    /// the bottom edge. Applying the rule always passes the first two and fails the third; applying
    /// it never passes the first and third and fails the second.
    /// </remarks>
    private static List<string> ValidateSheet()
    {
        var faults = new List<string>();

        // The front face of the torso, top-left corner: the first vertex of the first box, whose
        // patch starts fourteen texels down a net thirty-two tall.
        const float PatchTop = 14f;

        var square = Build(Beast(), 1024, 1024);
        var inProportion = Build(Beast(), 256, 128);
        var short_ = Build(Beast() with { SheetHeight = 64 }, 128, 64);

        if (MathF.Abs(inProportion.Vertices[0].Uv.Y - PatchTop / 32f) > 0.0001f)
        {
            faults.Add(
                $"a sheet already in proportion was rescaled: the patch starts at "
                + $"{inProportion.Vertices[0].Uv.Y:F4} rather than {PatchTop / 32f:F4}");
        }

        if (MathF.Abs(square.Vertices[0].Uv.Y - PatchTop / 64f) > 0.0001f)
        {
            faults.Add(
                $"a padded square was not read as one: the patch starts at {square.Vertices[0].Uv.Y:F4} "
                + $"rather than {PatchTop / 64f:F4}, which is the whole net stretched over the padding");
        }

        foreach (var vertex in square.Vertices)
        {
            if (vertex.Uv.Y <= 0.5f + 0.0001f) continue;

            faults.Add($"a padded square has a patch at {vertex.Uv.Y:F4}, below the half of it the art is in");
            break;
        }

        if (MathF.Abs(short_.Vertices[0].Uv.Y - PatchTop / 64f) > 0.0001f)
        {
            faults.Add(
                $"a sheet too short for its net was scaled by its width anyway: the patch starts at "
                + $"{short_.Vertices[0].Uv.Y:F4} rather than {PatchTop / 64f:F4}, and half the net is off it");
        }

        foreach (var vertex in short_.Vertices)
        {
            if (vertex.Uv.Y <= 1f + 0.0001f) continue;

            faults.Add($"a sheet too short for its net has a patch at {vertex.Uv.Y:F4}, which is off the bottom");
            break;
        }

        return faults;
    }
}
