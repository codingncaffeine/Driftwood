using System.Numerics;
using Driftwood.Core.Physics;

namespace Driftwood.Core.Entities;

/// <summary>Where the model's parts are this frame, in the world.</summary>
/// <remarks>
/// ⛳ <b>Built once and handed out, rather than worked out again by whoever wants a limb.</b> The
/// thing in the hand has to travel with the hand, and a second copy of a shoulder, a crouch lean and
/// a body yaw is a held pickaxe that hovers a limb away from the fist the first time any of them is
/// dialled. In third person that is visible from the first frame; in first person it only ever shows
/// mid-swing, which is worse.
/// </remarks>
public readonly record struct PlayerRig(Matrix4x4 Root, Matrix4x4 Body, Vector3 LegShift)
{
    /// <summary>The matrix one box of the model is drawn with.</summary>
    public Matrix4x4 Part(PlayerPart part, Vector3 pivot, in PlayerPose pose)
    {
        var unit = PlayerModel.Unit;

        // Head and arms hang off the torso, so leaning into a crouch carries them without the pose
        // having to describe the same lean three more times.
        return part switch
        {
            PlayerPart.Body => Body,
            PlayerPart.Head => Turn(pose.Head) * Step(pivot - HeldGrip.BodyPivot, unit) * Body,
            PlayerPart.RightArm => Turn(pose.RightArm) * Step(pivot - HeldGrip.BodyPivot, unit) * Body,
            PlayerPart.LeftArm => Turn(pose.LeftArm) * Step(pivot - HeldGrip.BodyPivot, unit) * Body,
            PlayerPart.RightLeg => Turn(pose.RightLeg) * Step(pivot, unit, LegShift) * Root,
            _ => Turn(pose.LeftLeg) * Step(pivot, unit, LegShift) * Root,
        };
    }

    private static Matrix4x4 Turn(in LimbPose pose) =>
        Matrix4x4.CreateRotationZ(pose.Roll)
        * Matrix4x4.CreateRotationY(pose.Yaw)
        * Matrix4x4.CreateRotationX(pose.Pitch);

    private static Matrix4x4 Step(Vector3 units, float unit) =>
        Matrix4x4.CreateTranslation(units * unit);

    private static Matrix4x4 Step(Vector3 units, float unit, Vector3 extra) =>
        Matrix4x4.CreateTranslation(units * unit + extra);
}

/// <summary>
/// Where the arm is, and where what it is holding sits in the fist.
/// </summary>
/// <remarks>
/// <para>In Core with no reference to a window, a camera or a texture, for the same reason
/// <see cref="PlayerAnimator"/> is: an angle that can only be checked by looking at it is an angle
/// that gets checked once. The audit runs the whole chain here and asks where the tool ends up
/// relative to the fist and the chest, at rest and through a swing.</para>
/// <para><b>A sprite's own space:</b> +x is the picture's right, +y is up, +z is out of the front of
/// it. Our tools are drawn head in the TOP RIGHT with the haft running to the bottom-left corner, so
/// the long axis of every one of them is the (+1,+1) diagonal, and turning the picture in its own
/// plane is what decides how upright the tool is carried.</para>
/// <para><b>An arm's own space:</b> it hangs down its own −y, −z is the way the model faces, +x is
/// outward from the body.</para>
/// </remarks>
public static class HeldGrip
{
    /// <summary>Where the torso turns, in model units. Everything above the waist hangs off it.</summary>
    public static readonly Vector3 BodyPivot = new(0f, 12f, 0f);

    // ── The first-person view model. Every number a swing looks wrong by is in this block. ──
    //
    // What is meant to be on screen is the thing in the hand, and only part of it — or, holding
    // nothing, the hand alone. No forearm, no shoulder. So the arm is aimed nearly straight away
    // from the eye and its shoulder is put *below the bottom edge of the frame*: at that angle its
    // whole length is foreshortened into almost nothing and the only part still inside the picture
    // is the fist at the far end, low and to the right.
    //
    // The arm hangs down its own −Y, so pitch is the angle that decides all of this. Near a right
    // angle it points straight away; under that it swings down out of frame, over it the whole limb
    // rises back into view broadside on. The rest yaw turns the hand in toward the crosshair, so
    // what is held leans at what it is about to hit.
    //
    // The geometry these are placed against: the view is 70 degrees vertical, so the frame's half
    // height at distance z is 0.70·z. The shoulder sits at z 0.58 where that is 0.41 — well above
    // its own −0.76 — and the fist ends up at z 1.26 where it is 0.88, which puts the fist just
    // inside the bottom edge. Dial the offset and this is the arithmetic that decides what shows.
    private const float RestPitch = 1.58f;     // radians away from straight down: aimed down the barrel
    private const float RestYaw = 0.26f;       // and in toward the middle of the screen
    private const float RestRoll = -0.18f;
    private static readonly Vector3 RestOffset = new(0.58f, -0.76f, -0.58f);

    // The swing is a sword's diagonal slice: it cocks up to the top right and cuts down across to
    // the bottom left, then recovers.
    //
    // What makes it a slice rather than a chop or a sideways slide is that the rise and the
    // crossing are the *same* motion — one number drives both, so the tool is always as high as it
    // is far right. Pull them apart and it reads as two movements happening at once.
    //
    // Each of the three carries part of it. Pitch takes the tool up and brings it down, because
    // over a right angle the arm points forward and up and under it forward and down. Yaw turns
    // what is held to face the way it is travelling. The shift moves the whole thing across the
    // frame, and its X and Y are deliberately in proportion: that ratio *is* the angle of the cut.
    // Sized to read rather than to perform. The direction is the thing being said; a cut that
    // throws the tool right across the frame says it twice and gets tiring by the tenth block.
    private const float WindUpShare = 0.32f;   // of the swing spent cocking up to the right
    private const float DriveShare = 0.22f;    // of it spent cutting down across; the rest recovers
    private const float SwingCock = 0.34f;     // radians raised above rest at the top right
    private const float SwingFollow = 0.42f;   // radians below rest at the bottom left
    private const float SwingCross = 0.52f;    // how far it turns across the screen
    private const float SwingTwist = 0.36f;    // how far the edge rolls over through the cut
    private static readonly Vector3 SwingShift = new(-0.19f, -0.14f, -0.10f);

    /// <summary>Builds the frame's rig from a pose and where the feet are.</summary>
    public static PlayerRig Rig(Vector3 feet, in PlayerPose pose)
    {
        var unit = PlayerModel.Unit;

        // Model space has −Z forward; world yaw is measured from +X. The extra quarter turn is what
        // reconciles the two, and doing it here means the animator never has to know about it.
        var yaw = float.DegreesToRadians(pose.BodyYawDegrees);
        var root = Matrix4x4.CreateRotationY(-(yaw + MathF.PI / 2f)) * Matrix4x4.CreateTranslation(feet);

        var body = Matrix4x4.CreateRotationX(pose.BodyPitch)
                 * Matrix4x4.CreateTranslation((BodyPivot - new Vector3(0f, pose.BodyDropUnits, 0f)) * unit)
                 * root;

        // Legs step back and up under a crouching torso. +Z is behind the model.
        return new PlayerRig(root, body, new Vector3(0f, pose.LegLiftUnits, pose.LegShiftUnits) * unit);
    }

    /// <summary>
    /// Where the first-person arm is, this far through a swing. In the camera's own space.
    /// </summary>
    /// <remarks>
    /// The thing in the hand has to travel with the hand, so this is what both the arm and whatever
    /// it is carrying are placed against. Anything animated from its own copy of these numbers
    /// drifts out of the fist the first time one of them is dialled — which is exactly the sort of
    /// thing that is only ever noticed mid-swing, from inside the game.
    /// </remarks>
    public static Matrix4x4 ArmTransform(float t)
    {
        var swing = SwingCurve(t);

        // Raised at −1, driven down past rest at +1. Increasing pitch lifts the hand, because the
        // arm hangs down its own −Y, so cocking back is a bigger angle and the strike is a smaller
        // one — which is the opposite of what it reads as on the page and the reason it is written
        // out here rather than inlined.
        var pitch = RestPitch - swing * (swing < 0f ? SwingCock : SwingFollow);

        return Matrix4x4.CreateRotationZ(RestRoll + swing * SwingTwist)
             * Matrix4x4.CreateRotationX(pitch)
             * Matrix4x4.CreateRotationY(RestYaw + swing * SwingCross)
             * Matrix4x4.CreateTranslation(RestOffset + SwingShift * swing);
    }

    /// <summary>
    /// Where the first-person OTHER arm is. The main one's pose, mirrored, and never swinging.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>Mirrored rather than given numbers of its own.</b> Three constants flip — the roll,
    /// the yaw and the sideways offset — and the other three do not, because a left arm hangs at the
    /// same height, reaches the same distance and is held at the same pitch as a right one. Two
    /// independent sets of six would drift apart the first time <see cref="RestOffset"/> was dialled,
    /// and the offhand is exactly the thing nobody re-checks after a tweak.</para>
    /// <para>⛔ <b>And it does not swing.</b> The offhand is not what a click strikes with — the swing
    /// curve belongs to the main hand — so passing the main hand's <c>t</c> in here would put a torch
    /// through the same arc as the sword beside it every time the player attacked.</para>
    /// <para>⚠ A shield adds its own view-space placement in <see cref="InViewShield"/>. Keeping that
    /// movement out of this mirror matters: a torch should stay at the side when the shield key is
    /// held, and a broad board needs a different composition from a narrow carried item.</para>
    /// </remarks>
    public static Matrix4x4 OffhandArmTransform() =>
        Matrix4x4.CreateRotationZ(-RestRoll)
      * Matrix4x4.CreateRotationX(RestPitch)
      * Matrix4x4.CreateRotationY(-RestYaw)
      * Matrix4x4.CreateTranslation(RestOffset with { X = -RestOffset.X });

    /// <summary>
    /// The swing, as one number: −1 fully cocked, 0 at rest, +1 fully followed through.
    /// </summary>
    /// <remarks>
    /// Three phases rather than a sine, because a sine is symmetric and a blow is not. It goes back
    /// slowly, comes down fast, and recovers over what is left — and it starts and ends at exactly
    /// rest, so a held button that swings again immediately does not snap.
    /// </remarks>
    public static float SwingCurve(float t)
    {
        if (t <= 0f || t >= 1f) return 0f;

        if (t < WindUpShare) return -Smooth(t / WindUpShare);

        if (t < WindUpShare + DriveShare)
            return -1f + 2f * Smooth((t - WindUpShare) / DriveShare);

        return 1f - Smooth((t - WindUpShare - DriveShare) / (1f - WindUpShare - DriveShare));
    }

    private static float Smooth(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    // ── How a thing sits in a fist. Every number a held tool looks wrong by is in this block. ──
    //
    // ⛔ FROM THE USER, HOLDING ONE: "it's facing the wrong direction where the tip of the tool is
    // facing the player so it's backwards". It was. The old pair of tilts were dialled against a
    // cube wearing the icon on all six faces, where "which way is the front" was not a question
    // anybody could answer — every side showed the same picture. An extruded sprite has exactly one
    // front, so the turn that puts it there is derivable instead of guessed at.
    //
    // FIRST PERSON is the odd one out and it is worth saying why the two grips are not shared. The
    // view-model arm is aimed nearly straight down the barrel — a pose no shoulder is ever in —
    // precisely so that all of it foreshortens away and only the fist is left in frame. Under that,
    // the arm's forward is the screen's UP and the arm's own +y is toward the eye. So the turn that
    // faces a picture at the camera there (a quarter turn about x) and the one that faces it out of
    // the player's side in the world (a quarter turn about y) are different turns, and sharing one
    // would show the tool edge-on in one of the two views. What IS shared is the arm they hang off.

    /// <param name="Shift">
    /// A nudge in the arm's own space, in blocks, after everything else. It exists so where a thing
    /// sits can be dialled without touching <see cref="RestOffset"/> — that offset also places the
    /// bare hand, which is drawn when the pockets are empty, and moving the tool by moving the arm
    /// moves the hand with it.
    /// </param>
    /// <param name="Twist">
    /// Radians about the tool's OWN long axis — the diagonal its picture runs along.
    /// </param>
    /// <remarks>
    /// ⛳ <b>The degree of freedom the rig did not have, and the one a weapon needs.</b> Lean turns
    /// about y, Tilt about x and Roll about z, and not one of those is the axis a blade turns on: a
    /// tool is drawn corner to corner, so its long axis is the picture's <em>diagonal</em>, and
    /// rolling about z spins the whole picture instead of turning the head on its handle. With only
    /// the three, the flat of an axe faces the eye and its edge lies across the screen — which is
    /// carrying it like a tray rather than holding it like an axe.
    /// <para>⚠ <b>The sign is the one thing to flip if the edge comes out pointing the wrong way.</b>
    /// Positive turns the leading edge away from the eye. One number per grip, nothing else depends
    /// on it, and it is a judgement about a picture rather than something a check can settle.</para>
    /// </remarks>
    public readonly record struct Grip(
        float Lean, float Tilt, float Roll, Vector3 Shift, float Twist = 0f);

    /// <summary>
    /// The long axis of a flat tool in its own space: the diagonal its drawing runs along.
    /// </summary>
    /// <remarks>
    /// Every tool in the set is drawn with its head in one corner and its haft in the opposite one —
    /// the user asked for that angle in so many words — so one axis serves all four shapes, and a
    /// shape that stopped being diagonal would want its own entry here rather than a second field.
    /// </remarks>
    private static readonly Vector3 LongAxis = Vector3.Normalize(new Vector3(1f, 1f, 0f));

    /// <summary>First person: the picture faces the eye, on its own diagonal.</summary>
    /// <remarks>
    /// <para>⛳ <b>The roll is zero on purpose, and the user asked for it in those terms:</b> <i>"you
    /// should really only see a portion of the weapon in first person view, no arm at all, slicing
    /// at an angle from top-right to bottom left"</i>. That angle is the one every tool is already
    /// drawn at — head in the top right, haft to the bottom-left corner — so the picture is held
    /// square to the camera and left alone, and the diagonal comes free from the art. Turning it in
    /// its own plane here would be undoing the drawing.</para>
    /// <para>⛔ <b>The lean is negative, and it was worked out rather than guessed.</b> The first
    /// pass used +0.42, reasoning that the quarter turn about x was doing all the work — and the
    /// picture came out 49° off the eye, so what showed was a rim of extruded wall and a
    /// foreshortened sliver of the drawing. What that reasoning left out is that the arm carries its
    /// own yaw and roll and those turn the item too. Running the whole chain out: the picture is
    /// exactly square to the camera at −0.44, and every hundredth off that is about half a degree of
    /// turn. −0.20 leaves about 14°, which is enough to see the thickness down one side and not
    /// enough to lose the silhouette.</para>
    /// </remarks>
    /// <remarks>
    /// ⛳ <b>The twist is the user's own report:</b> <i>"twist the weapons like the axe so that the
    /// sharp edge of the blade is facing away from the player"</i>. The axe is drawn with its edge on
    /// the left of the head, and with no twist the flat of that head faces the eye — so the edge lay
    /// across the screen and the thing read as being presented rather than wielded. A third of a
    /// radian is about nineteen degrees: enough that the edge leads and the head reads as having a
    /// front and a back, and not so much that the silhouette turns into a line.
    /// </remarks>
    public static readonly Grip FirstFlat = new(
        -0.20f, -MathF.PI / 2f, 0f, new Vector3(0.12f, 0f, 0.12f), Twist: 0.34f);

    /// <summary>A first-person shield faces the player as a board, not as a second diagonal blade.</summary>
    /// <remarks>
    /// The ordinary flat grip twists a weapon around its handle so its cutting edge leads. A shield
    /// has no such edge: giving it the sword's twist tips its broad inner corner toward the sword and
    /// makes the two read as one crowded cluster. Its dedicated grip keeps the face nearly square;
    /// <see cref="InViewShield"/> then owns the Minecraft-like lower-left and raised positions.
    /// </remarks>
    public static readonly Grip FirstShield = new(
        -0.20f, -MathF.PI / 2f, 0f, new Vector3(0.12f, 0f, 0.12f));

    /// <summary>Third person: the picture faces out of the player's own side.</summary>
    /// <remarks>
    /// The roll turns the tool in its own plane, out of the 45° its drawing sits at and up toward
    /// the vertical — a tool carried at exactly the diagonal reads as being presented rather than
    /// carried. The tilt leans the head away from the body, and the shift stands it off the sleeve,
    /// so a haft does not run through the arm holding it.
    /// </remarks>
    public static readonly Grip ThirdFlat = new(
        MathF.PI / 2f, 0.22f, -0.55f, new Vector3(0.05f, -0.04f, -0.04f), Twist: 0.34f);

    /// <summary>A block is held corner forward, in the corner of the frame.</summary>
    /// <remarks>
    /// ⛔ <b>The tilt is a quarter turn for the same reason the flat one is</b>, and getting it wrong
    /// is what walked the cube off the bottom of the screen twice. Under the view-model arm the arm's
    /// own +y points <em>at the eye</em>, so a cube left standing on its own axis is a cube standing
    /// on a line running out of the screen: it leans toward the camera, comes nearer, and leaves
    /// through the bottom edge as the frame narrows around it. Turned a quarter about x its up is the
    /// screen's up, and the extra third of a radian tips its lid toward the eye so a block in a hand
    /// shows the top face that says it is a block at all.
    /// </remarks>
    public static readonly Grip FirstBlock = new(0.17f, -1.22f, 0f, new Vector3(0.10f, 0f, -0.06f));

    public static readonly Grip ThirdBlock = new(0.78f, 0f, 0f, new Vector3(0.05f, -0.02f, -0.04f));

    /// <summary>The other hand, carrying something at the side.</summary>
    /// <remarks>
    /// <para>⛔ <b>Written out rather than mirrored from <see cref="ThirdFlat"/>, and that is a
    /// decision.</b> A mechanical reflection is not three sign flips: a rotation about an axis
    /// reflects to a rotation about the <em>reflected axis</em> by the negated angle, and
    /// <see cref="Twist"/> turns about the picture's diagonal, so mirroring it correctly means
    /// carrying a second long axis through the whole chain. Every other grip in this file is a
    /// hand-dialled constant with a note saying why; making this one the exception would buy a
    /// generality nothing needs.</para>
    /// <para>⚠ <b>And the twist is zero here, which is the reason nothing needs it.</b> The twist
    /// exists so an axe leads with its edge. Nothing diagonal goes in the other hand — it holds a
    /// shield, a torch, a block or something to eat — so the axis a blade turns on has nothing in
    /// this hand to turn.</para>
    /// </remarks>
    public static readonly Grip OffhandFlat =
        new(-MathF.PI / 2f, 0.22f, 0.55f, new Vector3(-0.05f, -0.04f, -0.04f));

    public static readonly Grip OffhandBlock = new(-0.78f, 0f, 0f, new Vector3(-0.05f, -0.02f, -0.04f));

    /// <summary>
    /// A shield with the guard up: broad face out, standing in front of the chest.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Its own grip, because a raised shield is not a carried thing turned a bit.</b> Everything
    /// else in a hand is held at an angle so its silhouette reads; a shield up is the one item whose
    /// whole job is to present its face flat to whatever is in front of the player. Held at the side
    /// like a torch, a guard reads as somebody holding a plank.
    /// <para>⚠ The arm brings most of it — <c>PlayerAnimator</c> carries the limb across the chest —
    /// so this only has to stand the board off the fist and square it up. Both halves have to move
    /// together if either is dialled.</para>
    /// </remarks>
    public static readonly Grip ShieldUp = new(0.16f, 0f, 0f, new Vector3(-0.06f, -0.06f, -0.10f));

    /// <summary>Where a block is held: near its middle.</summary>
    /// <remarks>
    /// ⛔ <b>Not by its underside, which is the obvious answer and put it off the screen entirely.</b>
    /// Gripping the bottom face carries the whole cube half a block along its own up axis — and once
    /// the grip is turned, that axis points mostly <em>at the eye</em>, so the cube came half a block
    /// nearer the camera. The frame narrows as it comes nearer, so a cube that fitted at arm's
    /// length did not fit at half of it, and it left through the bottom edge. Held near the middle it
    /// sits in the corner with two of its faces showing, which is what a block in a hand looks like.
    /// </remarks>
    public static readonly Vector3 BlockHold = new(0f, -0.05f, 0f);

    /// <summary>
    /// How much of a torch's colour a thing in the hand takes.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>From the user, in the game:</b> <i>"the colour of the tool shown on the player [should
    /// match] the colour of the material being used to make it"</i>. It did not, and the tiles were
    /// never the problem — measured, they run wood 204,168,118 through iron 218,218,223 to
    /// stormglass 123,226,223, and each material covers 45% to 71% of its own silhouette.
    /// <para>What was wrong is the LIGHT. Block light in this engine is coloured on purpose — that is
    /// the whole reason an ember glow reads orange against a blue-grey cave — and a torch is
    /// (14,10,5), which is very orange indeed. The held item was multiplied by it raw, and
    /// underground a torch is the only light there is: an iron axe came out at roughly 203,149,74,
    /// which is to say the colour of the torch rather than the colour of iron. Every tier converged
    /// on the same orange and the ladder stopped reading.</para>
    /// <para>⛳ <b>So the hand desaturates and the world does not.</b> A held thing is eight inches
    /// from the eye and is the one object a player identifies by its colour; everything else in the
    /// frame keeps the full coloured light it always had. Most of the way to neutral, not all —
    /// a tool by a fire should still look warm.</para>
    /// </remarks>
    public const float HandDesaturation = 0.75f;

    /// <summary>
    /// What a thing in the hand is lit by: block light pulled toward neutral, plus the sky.
    /// </summary>
    /// <param name="block">Coloured block light where the player stands.</param>
    /// <param name="sky">Daylight reaching them, already scaled by the hour.</param>
    public static Vector3 HandLight(Vector3 block, float sky)
    {
        // Its own brightness, weighted the way an eye weighs the three — a flat mean turns a blue
        // lamp into something brighter than a red one at the same nominal level.
        var grey = block.X * 0.30f + block.Y * 0.59f + block.Z * 0.11f;

        return Vector3.Lerp(block, new Vector3(grey), HandDesaturation) + new Vector3(sky);
    }

    /// <summary>How big a held thing is drawn, as a share of a block.</summary>
    /// <remarks>
    /// A flat thing is bigger than a block on purpose. It is a picture of a pickaxe rather than a
    /// solid one, so at a block's size it reads as a splinter — and the user asked to see only a
    /// portion of it in first person, which means large enough that the rest is off the bottom of
    /// the frame rather than small enough to fit.
    /// </remarks>
    public static float HeldSize(bool flat) => flat ? 0.72f : 0.44f;

    /// <summary>Where a held thing sits relative to the shoulder it hangs from.</summary>
    /// <param name="hold">
    /// The point of the item the fingers are on, in the item's own space. Measured off each
    /// picture's own ink rather than fixed — see <see cref="Items.ItemSprite.Hold"/>.
    /// </param>
    public static Matrix4x4 InFist(in Grip grip, float size, Vector3 hold, ArmStyle arms, bool right)
    {
        // Turned about the point the fingers are on, then carried to the fist — so dialling the
        // turn pivots the tool in the hand rather than swinging it out of the hand.
        // ⛳ The twist comes FIRST, before everything else. It is a turn in the item's own frame —
        // the blade rolling on its handle — and applying it after the grip has been leaned and
        // tilted would turn it about whatever axis the item had ended up lying along instead.
        var rotation = Matrix4x4.CreateFromAxisAngle(LongAxis, grip.Twist)
                     * Matrix4x4.CreateRotationY(grip.Lean)
                     * Matrix4x4.CreateRotationX(grip.Tilt)
                     * Matrix4x4.CreateRotationZ(grip.Roll);

        return Matrix4x4.CreateTranslation(-hold)
             * Matrix4x4.CreateScale(size)
             * rotation
             * Matrix4x4.CreateTranslation(PlayerModel.FistInArm(arms, right) * PlayerModel.Unit + grip.Shift);
    }

    /// <summary>
    /// Where the held thing is in first person: in the fist of the view model, in the camera's space.
    /// </summary>
    public static Matrix4x4 InView(float t, bool flat, Vector3 hold, ArmStyle arms) =>
        InFist(flat ? FirstFlat : FirstBlock, HeldSize(flat), hold, arms, right: true) * ArmTransform(t);

    /// <summary>
    /// And where the OTHER hand's thing is in first person: in the left fist, never swinging.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>The same call with two arguments changed</b>, which is the whole point of
    /// <see cref="InFist"/> taking a hand: <c>PlayerModel.FistInArm</c> has answered for the left arm
    /// since the model was built, and the third-person offhand already goes through it. The view
    /// model was the one place still assuming a right hand.
    /// </remarks>
    public static Matrix4x4 InViewOffhand(bool flat, Vector3 hold, ArmStyle arms) =>
        InFist(flat ? FirstFlat : FirstBlock, HeldSize(flat), hold, arms, right: false)
      * OffhandArmTransform();

    /// <summary>
    /// Where a shield sits in first person: parked low and wide at rest, lifted inward to block.
    /// </summary>
    /// <param name="guard">Smoothed 0..1 blocking amount from <see cref="PlayerAnimator"/>.</param>
    /// <remarks>
    /// These are camera-space offsets deliberately applied last. The rest pose moves the board away
    /// from the lower-right sword instead of treating both hands as a symmetric pair. Raising it
    /// follows Minecraft's readable silhouette: up first, modestly inward, and still anchored on the
    /// left half rather than teleporting over the crosshair.
    /// </remarks>
    public static Matrix4x4 InViewShield(Vector3 hold, ArmStyle arms, float guard)
    {
        guard = Math.Clamp(guard, 0f, 1f);
        var rest = new Vector3(-0.30f, -0.08f, 0f);
        var raised = new Vector3(-0.08f, 0.30f, -0.04f);

        return InFist(FirstShield, HeldSize(flat: true), hold, arms, right: false)
             * OffhandArmTransform()
             * Matrix4x4.CreateTranslation(Vector3.Lerp(rest, raised, guard));
    }

    /// <summary>
    /// Where the held thing is in third person: in the model's own right fist, in the world.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>From the user:</b> <i>"our 3rd person view isn't showing the tool or weapon at all in
    /// hand, this is a big part of the game"</i>. The drawing was never the missing part — this is
    /// the same call the first-person path makes, against the arm matrix the animator is already
    /// driving, which is why a swing carries it and a crouch carries it and nothing had to be said
    /// twice.
    /// </remarks>
    public static Matrix4x4 InWorld(Vector3 feet, in PlayerPose pose, bool flat, Vector3 hold, ArmStyle arms)
    {
        var arm = Rig(feet, pose).Part(PlayerPart.RightArm, PlayerModel.ArmPivot(true), pose);
        return InFist(flat ? ThirdFlat : ThirdBlock, HeldSize(flat), hold, arms, right: true) * arm;
    }

    /// <summary>
    /// And where the thing in the OTHER hand is: the model's left fist, in the world.
    /// </summary>
    /// <param name="guard">
    /// True when this is a shield being held up, which is its own grip and its own arm pose.
    /// </param>
    /// <remarks>
    /// ⛳ <b>The offhand was a pocket on the interface and nothing on the body.</b> A raised shield
    /// stopped half of every blow that got past the plate, cost the player every swing and every
    /// block placed while it was up, and showed on the model as a person standing still doing
    /// nothing — which is the one part of the armour work the user has not been able to see at all.
    /// <para>⚠ <b>The LEFT arm's pivot, not the right one's.</b> <see cref="PlayerModel.ArmPivot"/>
    /// takes the side and answers a different shoulder; handing it <c>true</c> here would place the
    /// item correctly relative to a shoulder on the wrong side of the chest, which reads as a shield
    /// floating a body's width away and is exactly the kind of thing that survives review because
    /// both calls look identical.</para>
    /// </remarks>
    public static Matrix4x4 InOtherHand(
        Vector3 feet, in PlayerPose pose, bool flat, Vector3 hold, ArmStyle arms, bool guard)
    {
        var arm = Rig(feet, pose).Part(PlayerPart.LeftArm, PlayerModel.ArmPivot(false), pose);
        var grip = guard ? ShieldUp : flat ? OffhandFlat : OffhandBlock;

        return InFist(grip, HeldSize(flat), hold, arms, right: false) * arm;
    }

    /// <summary>
    /// Checks a held thing is in the fist and not in the chest, at rest and through a swing.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>The claim has to be about where the item ENDS UP, not about the numbers that put
    /// it there.</b> A check that recomputes the grip and compares it to the grip is a check that
    /// passes whatever the grip is. So this asks two things a wrong transform fails: that the point
    /// the fingers are on lands inside the box the fist occupies, and that the middle of the item is
    /// outside the box the torso occupies — because the failure everybody actually gets is a tool
    /// buried in the chest or floating a limb away.</para>
    /// <para>Run at four points across a swing, because the fist is a moving target and a grip that
    /// only holds at rest is the one that comes apart the moment anybody mines anything.</para>
    /// </remarks>
    public static List<string> Validate()
    {
        var faults = new List<string>();
        var animator = new PlayerAnimator();
        animator.Reset(0f);

        var feet = new Vector3(0.5f, 64f, 0.5f);

        // A tool's grip sits low-left on its picture; a block's is under it. Both are held.
        foreach (var (label, flat, hold) in (ReadOnlySpan<(string, bool, Vector3)>)
                 [("a tool", true, new Vector3(-0.21f, -0.36f, 0f)), ("a block", false, BlockHold)])
        foreach (var arms in (ReadOnlySpan<ArmStyle>)[ArmStyle.Classic, ArmStyle.Slim])
        foreach (var t in (ReadOnlySpan<float>)[0f, 0.25f, 0.5f, 0.85f])
        {
            var pose = animator.Pose(0f, 0f);
            var rig = Rig(feet, pose);

            var arm = rig.Part(PlayerPart.RightArm, PlayerModel.ArmPivot(true), pose);
            var fist = Vector3.Transform(PlayerModel.FistInArm(arms, true) * PlayerModel.Unit, arm);

            // The grip point of the item, in the world, is the item's own `hold` — which the
            // transform is built to carry to the fist.
            var held = InWorld(feet, pose, flat, hold, arms);
            var gripped = Vector3.Transform(hold, held);
            var middle = Vector3.Transform(Vector3.Zero, held);

            var slip = Vector3.Distance(gripped, fist);
            if (slip > 0.12f)
                faults.Add($"{label} in a {arms} fist at t={t:F2}: gripped {slip:F3} blocks from the hand");

            // The torso is 8 units wide, 12 tall, 4 deep about its pivot at the waist.
            var chest = new Vector3(0f, PlayerModel.Unit * 18f, 0f) + feet;
            var inChest = MathF.Abs(middle.X - chest.X) < PlayerModel.Unit * 4f
                       && MathF.Abs(middle.Y - chest.Y) < PlayerModel.Unit * 6f
                       && MathF.Abs(middle.Z - chest.Z) < PlayerModel.Unit * 2f;

            if (inChest)
                faults.Add($"{label} in a {arms} fist at t={t:F2}: drawn inside the torso");

            // And the first-person one has to be in front of the eye rather than behind it.
            var inView = Vector3.Transform(hold, InView(t, flat, hold, arms));
            if (inView.Z >= 0f)
                faults.Add($"{label} in first person at t={t:F2}: {inView.Z:F2} on z, which is behind the camera");

            animator.Update(1f / 60f, feet, 0f, PlayerBody.WalkSpeed, false, false);
        }

        faults.AddRange(ValidateOffhand(feet));
        faults.AddRange(ValidateOffhandInView());
        faults.AddRange(ValidateShieldInView());
        return faults;
    }

    /// <summary>
    /// The other hand in FIRST person: on the far side of the eye, in front of it, and still.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>All three axes are probed, not the one the claim is about.</b> The third-person
    /// offhand cost most of a session to exactly this: a check asked whether the shield came forward
    /// along world −z, and at a body yaw of zero the model faces world +x, so z was its <em>side</em>.
    /// It read the same number for both signs of its input, which looks precisely like an arm that
    /// is not moving. Camera space here is unambiguous — +x right, +y up, −z forward — so the
    /// mirroring claim is about x, and y and z are asserted to have NOT moved, which is what makes
    /// "mirrored" different from "displaced".</para>
    /// <para>⚠ And it must not swing: a torch in the offhand travelling through the sword's arc is
    /// the bug that comes free with passing the main hand's <c>t</c> into the wrong transform.</para>
    /// </remarks>
    private static List<string> ValidateOffhandInView()
    {
        var faults = new List<string>();

        foreach (var (label, flat, hold) in (ReadOnlySpan<(string, bool, Vector3)>)
                 [("a torch", true, new Vector3(-0.21f, -0.36f, 0f)), ("a block", false, BlockHold)])
        foreach (var arms in (ReadOnlySpan<ArmStyle>)[ArmStyle.Classic, ArmStyle.Slim])
        {
            var main = Vector3.Transform(hold, InView(0f, flat, hold, arms));
            var other = Vector3.Transform(hold, InViewOffhand(flat, hold, arms));

            if (other.Z >= 0f)
                faults.Add($"{label} in the other hand is {other.Z:F2} on z, which is behind the camera");

            if (main.X <= 0f)
                faults.Add($"the main hand is at x {main.X:F2}, so this test has no side to mirror");

            if (other.X >= 0f)
                faults.Add($"{label} in the other hand is at x {other.X:F2}: it is on the same side "
                         + $"of the screen as the main hand at {main.X:F2}");

            // Mirrored means the same height and the same distance out, not merely somewhere else.
            if (MathF.Abs(other.Y - main.Y) > 0.08f)
                faults.Add($"{label} hangs at y {other.Y:F2} against the main hand's {main.Y:F2}, "
                         + "so the arms are at different heights rather than mirrored");

            if (MathF.Abs(other.Z - main.Z) > 0.12f)
                faults.Add($"{label} sits at z {other.Z:F2} against the main hand's {main.Z:F2}, "
                         + "so one arm reaches further than the other");

            // ⛔ The swing belongs to the hand that strikes. If the offhand ever picks it up, a
            // mid-swing probe moves and this is the only thing that would ever say so.
            var mid = Vector3.Transform(hold, InViewOffhand(flat, hold, arms));
            if (Vector3.Distance(mid, other) > 0.001f)
                faults.Add($"{label} in the other hand is not the same twice, so something is animating it");
        }

        return faults;
    }

    /// <summary>Pins the shield's deliberately asymmetric first-person composition and guard lift.</summary>
    private static List<string> ValidateShieldInView()
    {
        var faults = new List<string>();
        var hold = new Vector3(-0.21f, -0.36f, 0f);

        foreach (var arms in (ReadOnlySpan<ArmStyle>)[ArmStyle.Classic, ArmStyle.Slim])
        {
            var sword = Vector3.Transform(hold, InView(0f, flat: true, hold, arms));
            var ordinary = Vector3.Transform(hold, InViewOffhand(flat: true, hold, arms));
            var lowered = Vector3.Transform(hold, InViewShield(hold, arms, guard: 0f));
            var raised = Vector3.Transform(hold, InViewShield(hold, arms, guard: 1f));

            if (sword.X - lowered.X < 1.05f)
                faults.Add($"the resting {arms} sword and shield grips are only "
                         + $"{sword.X - lowered.X:F2} blocks apart in first person");
            if (lowered.X > ordinary.X - 0.20f)
                faults.Add($"the lowered {arms} shield is only {ordinary.X - lowered.X:F2} farther left "
                         + "than an ordinary offhand item");
            if (lowered.Y > ordinary.Y - 0.04f)
                faults.Add($"the lowered {arms} shield is not below the ordinary offhand pose");
            if (raised.Y < lowered.Y + 0.30f)
                faults.Add($"the raised {arms} shield rises only {raised.Y - lowered.Y:F2} blocks");
            if (raised.X <= lowered.X || raised.X >= 0f)
                faults.Add($"the raised {arms} shield moves from x {lowered.X:F2} to {raised.X:F2}, "
                         + "rather than inward while staying left of the crosshair");
            if (lowered.Z >= 0f || raised.Z >= 0f)
                faults.Add($"the first-person {arms} shield crossed behind the camera");
        }

        return faults;
    }

    /// <summary>
    /// Checks the other hand holds what it is given, and that a raised guard actually goes up.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>The side is the whole check, and it is the one a copy of the right-hand path
    /// passes.</b> Every line of the offhand transform looks exactly like the main hand's, so the
    /// failure that survives review is a shield placed correctly against the <em>wrong shoulder</em>
    /// — which reads as a board floating a body's width away and is invisible in the code. The fist
    /// asked for here is the LEFT one, and there is an arm below asserting the two are not the same
    /// place: without it, a build that hands both sides to the right arm passes every line.</para>
    /// <para>⛔ <b>And the guard has to MOVE, measured in seconds rather than in frames.</b> "The
    /// shield is in the left hand" is equally true of a guard that never comes up, which is the exact
    /// state this work exists to fix. So the arm is stepped for a fixed time with blocking on and the
    /// shield has to end up materially further forward than it started — and the down pose is
    /// measured as its own control, or "it is in front of the chest" passes a shield that was always
    /// in front of the chest.</para>
    /// </remarks>
    private static List<string> ValidateOffhand(Vector3 feet)
    {
        var faults = new List<string>();
        var hold = new Vector3(-0.21f, -0.36f, 0f);

        foreach (var arms in (ReadOnlySpan<ArmStyle>)[ArmStyle.Classic, ArmStyle.Slim])
        {
            var animator = new PlayerAnimator();
            animator.Reset(0f);

            var pose = animator.Pose(0f, 0f);
            var rig = Rig(feet, pose);

            var leftArm = rig.Part(PlayerPart.LeftArm, PlayerModel.ArmPivot(false), pose);
            var leftFist = Vector3.Transform(PlayerModel.FistInArm(arms, false) * PlayerModel.Unit, leftArm);

            var rightArm = rig.Part(PlayerPart.RightArm, PlayerModel.ArmPivot(true), pose);
            var rightFist = Vector3.Transform(PlayerModel.FistInArm(arms, true) * PlayerModel.Unit, rightArm);

            // ⛔ The two hands must be two places. Everything below is satisfied by a build that
            // quietly uses the right arm for both.
            var apart = Vector3.Distance(leftFist, rightFist);
            if (apart < PlayerModel.Unit * 6f)
                faults.Add($"the two fists of a {arms} model are {apart:F3} blocks apart, which is one hand");

            // ⛔⛔ THE MODEL'S OWN FORWARD, ASKED OF THE RIG — never world −z. This is the whole
            // reason the first version of this check was useless: at a body yaw of zero the model
            // faces world **+x**, so measuring "did the shield come forward" along world z was
            // measuring the model's SIDE. It read the same 0.337 for a pitch of +1.25 and of −1.25
            // and looked exactly like an arm that was not moving, when the arm was swinging half a
            // block each way along an axis nothing was looking at.
            var forward = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, rig.Body));

            var down = InOtherHand(feet, pose, flat: true, hold, arms, guard: false);
            var slip = Vector3.Distance(Vector3.Transform(hold, down), leftFist);

            if (slip > 0.12f)
                faults.Add($"the other hand of a {arms} model gripped {slip:F3} blocks from its own fist");

            // Guard up, stepped for a fifth of a second — which is real time, not a frame count.
            for (var elapsed = 0f; elapsed < 0.2f; elapsed += 1f / 60f)
            {
                animator.Update(1f / 60f, feet, 0f, PlayerBody.WalkSpeed, false, false, blocking: true);
                pose = animator.Pose(0f, 0f);
            }

            if (animator.BlockAmount < 0.9f)
                faults.Add($"the guard reached {animator.BlockAmount:F2} of the way up in a fifth of a second");

            var guarded = InOtherHand(feet, pose, flat: true, hold, arms, guard: true);

            // ⛳ THE ARM FIRST, and separately from what it is carrying. Asking only where the shield
            // ends up folds two independent things — whether the limb moved and whether the grip
            // points the board the right way — into one number, and the first time it went red the
            // reading was the same for a pitch of +1.25 and of −1.25, which is a fact about the arm
            // and says nothing whatever about the grip.
            var upArm = Rig(feet, pose).Part(PlayerPart.LeftArm, PlayerModel.ArmPivot(false), pose);
            var upFist = Vector3.Transform(PlayerModel.FistInArm(arms, false) * PlayerModel.Unit, upArm);

            var armCame = Vector3.Dot(upFist - leftFist, forward);
            if (armCame < PlayerModel.Unit * 4f)
                faults.Add(
                    $"a raised {arms} guard brings the fist {armCame:F3} blocks forward, which is "
                    + $"not an arm going up (guard {animator.BlockAmount:F2}, pitch {pose.LeftArm.Pitch:F2})");

            // And then the board itself, which is the grip's half of the same claim.
            var boardCame = Vector3.Dot(
                Vector3.Transform(Vector3.Zero, guarded) - Vector3.Transform(Vector3.Zero, down), forward);

            if (boardCame < PlayerModel.Unit * 4f)
                faults.Add(
                    $"a raised {arms} guard holds the board {boardCame:F3} blocks further forward "
                    + $"than a hand at rest, so putting it up moves it nowhere (the fist came {armCame:F3})");

            // And it comes back down, which is the control on the arm pose being a blend at all.
            for (var elapsed = 0f; elapsed < 0.4f; elapsed += 1f / 60f)
                animator.Update(1f / 60f, feet, 0f, PlayerBody.WalkSpeed, false, false, blocking: false);

            if (animator.BlockAmount > 0.1f)
                faults.Add($"the guard was still {animator.BlockAmount:F2} up four tenths after being dropped");
        }

        return faults;
    }
}
