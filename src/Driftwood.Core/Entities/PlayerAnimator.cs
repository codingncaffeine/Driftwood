using System.Numerics;

namespace Driftwood.Core.Entities;

/// <summary>One limb's rotation about its pivot, in radians. Positive pitch swings it forward.</summary>
public readonly record struct LimbPose(float Pitch, float Yaw, float Roll)
{
    public static readonly LimbPose Rest = new(0f, 0f, 0f);
}

/// <summary>Everything the renderer needs to place the model for one frame.</summary>
/// <param name="BodyYawDegrees">Which way the torso faces. Lags the head deliberately.</param>
/// <param name="BodyDropUnits">How far the torso sinks into a crouch, in model units.</param>
/// <param name="LegShiftUnits">How far the legs step back under a crouching torso.</param>
public readonly record struct PlayerPose(
    float BodyYawDegrees,
    float BodyPitch,
    float BodyDropUnits,
    LimbPose Head,
    LimbPose RightArm,
    LimbPose LeftArm,
    LimbPose RightLeg,
    LimbPose LeftLeg,
    float LegShiftUnits,
    float LegLiftUnits);

/// <summary>
/// Turns where the player is and what they are doing into a pose.
/// </summary>
/// <remarks>
/// <para>Lives in Core with no reference to a window, a camera or an input device, so the audit can
/// run a walk cycle and a swing at a fixed step and measure them. An animation that can only be
/// checked by watching it is an animation that gets checked once.</para>
/// <para>The swing is the reason this exists. Breaking a block used to be a click and a block
/// vanishing, with nothing in between — no arm, no motion, no cause. The strike cadence is owned
/// here rather than by the input handler precisely so that the block goes when the arm moves, and
/// holding the button is a sequence of swings rather than a stream of edits.</para>
/// </remarks>
public sealed class PlayerAnimator
{
    /// <summary>
    /// How long one swing takes. Short enough to feel like a strike rather than a wave, long enough
    /// that the arc reads at any frame rate. Also the mining cadence: hold the button and this is
    /// how often a block goes.
    /// </summary>
    public const float SwingSeconds = 0.30f;

    /// <summary>
    /// Blocks of travel per full stride. Picked against walking speed rather than against a clock:
    /// tying the cycle to distance is what keeps the feet from skating when the player sprints,
    /// sneaks or is slowed. At 4.3 blocks/s this is a stride every 0.6 s.
    /// </summary>
    private const float BlocksPerStride = 2.6f;

    /// <summary>Widest a limb swings when walking flat out, in radians.</summary>
    private const float LegSwing = 1.0f;
    private const float ArmSwing = 0.85f;

    /// <summary>How fast the swing amplitude chases the player's speed, per second.</summary>
    private const float AmountResponse = 12f;

    /// <summary>How far the head can turn before the body gives up and follows, in degrees.</summary>
    private const float HeadTurnLimit = 60f;

    /// <summary>Degrees per second the body turns to catch up with the head while moving.</summary>
    private const float BodyTurnRate = 480f;

    /// <summary>How fast a crouch settles in and out, per second.</summary>
    private const float SneakResponse = 9f;

    private const float SneakLean = 0.45f;
    private const float SneakBodyDrop = 3.2f;
    private const float SneakLegShift = 4.0f;
    private const float SneakLegLift = 0.2f;

    /// <summary>Top of the swing arc, in radians. Past horizontal, so it reads as an overhead blow.</summary>
    private const float SwingLift = 1.9f;

    /// <summary>How far the swinging arm comes away from the body at the top of the arc.</summary>
    private const float SwingSpread = 0.4f;

    /// <summary>How far the torso twists into the blow, in degrees.</summary>
    private const float SwingTwist = 11f;

    private float _limbPhase;
    private float _limbAmount;
    private float _sneak;
    private float _age;

    private bool _haveLastPosition;
    private Vector3 _lastPosition;

    private bool _swinging;
    private float _swingTime;
    private int _strikes;

    /// <summary>Which way the torso faces, in degrees, in the same frame as the camera's yaw.</summary>
    public float BodyYawDegrees { get; private set; }

    /// <summary>0 at the start of a swing, 1 at the end. Meaningless unless <see cref="Swinging"/>.</summary>
    public float SwingProgress => _swinging ? Math.Clamp(_swingTime / SwingSeconds, 0f, 1f) : 0f;

    public bool Swinging => _swinging;

    /// <summary>Smoothed 0..1 crouch, for anything that wants to follow the model down.</summary>
    public float SneakAmount => _sneak;

    /// <summary>Begins a swing now. Ignored while one is already running; the repeat handles that.</summary>
    public void Strike()
    {
        if (!_swinging)
        {
            _swinging = true;
            _swingTime = 0f;
        }

        _strikes++;
    }

    /// <summary>
    /// How many swings have begun since this was last asked, and clears the count.
    /// </summary>
    /// <remarks>
    /// The caller breaks a block per strike rather than per click. That is the whole point of
    /// routing it through here: the arm coming down is what takes the block, so holding the button
    /// mines at the speed of the animation instead of at the speed of the event queue.
    /// </remarks>
    public int TakeStrikes()
    {
        var strikes = _strikes;
        _strikes = 0;
        return strikes;
    }

    /// <summary>Drops all motion and puts the model back at rest, facing a given way.</summary>
    public void Reset(float yawDegrees)
    {
        _limbPhase = 0f;
        _limbAmount = 0f;
        _sneak = 0f;
        _swinging = false;
        _swingTime = 0f;
        _strikes = 0;
        _haveLastPosition = false;
        BodyYawDegrees = yawDegrees;
    }

    /// <summary>
    /// Advances one frame.
    /// </summary>
    /// <param name="holding">Whether the strike button is still down, which repeats the swing.</param>
    public void Update(
        float dt, Vector3 position, float lookYawDegrees, float walkSpeed, bool sneaking, bool holding)
    {
        if (dt <= 0f) return;
        dt = MathF.Min(dt, 0.1f);
        _age += dt;

        StepStride(dt, position, walkSpeed);
        StepBodyYaw(dt, lookYawDegrees);

        var sneakTarget = sneaking ? 1f : 0f;
        _sneak += (sneakTarget - _sneak) * MathF.Min(1f, SneakResponse * dt);

        StepSwing(dt, holding);
    }

    /// <summary>Advances the walk cycle by how far the body actually moved.</summary>
    private void StepStride(float dt, Vector3 position, float walkSpeed)
    {
        if (!_haveLastPosition)
        {
            _lastPosition = position;
            _haveLastPosition = true;
        }

        var moved = new Vector2(position.X - _lastPosition.X, position.Z - _lastPosition.Z).Length();
        _lastPosition = position;

        // Phase is driven by distance and amplitude by speed. Doing both from speed would leave the
        // feet skating whenever the frame rate changed; doing both from distance would leave a
        // sprinting player with the same short stride as a sneaking one.
        _limbPhase += moved * (MathF.Tau / BlocksPerStride);

        var target = walkSpeed > 0f ? Math.Clamp(moved / dt / walkSpeed, 0f, 1f) : 0f;
        _limbAmount += (target - _limbAmount) * MathF.Min(1f, AmountResponse * dt);
        if (_limbAmount < 1e-4f) _limbAmount = 0f;
    }

    /// <summary>
    /// Turns the torso toward where the player is looking, but only so far and only so fast.
    /// </summary>
    /// <remarks>
    /// A body welded to the camera looks like a turret. Letting the head lead and the shoulders
    /// follow is most of what makes a third-person character read as a person, and the clamp is
    /// what stops the head twisting round to face backwards when someone spins on the spot.
    /// </remarks>
    private void StepBodyYaw(float dt, float lookYawDegrees)
    {
        if (_limbAmount > 0.05f)
        {
            var toward = Wrap(lookYawDegrees - BodyYawDegrees);
            var step = BodyTurnRate * dt * _limbAmount;
            BodyYawDegrees += Math.Clamp(toward, -step, step);
        }

        var relative = Wrap(lookYawDegrees - BodyYawDegrees);
        if (MathF.Abs(relative) > HeadTurnLimit)
            BodyYawDegrees += relative - Math.Clamp(relative, -HeadTurnLimit, HeadTurnLimit);

        BodyYawDegrees = Wrap(BodyYawDegrees);
    }

    private void StepSwing(float dt, bool holding)
    {
        if (!_swinging) return;

        _swingTime += dt;
        while (_swingTime >= SwingSeconds)
        {
            if (!holding)
            {
                _swinging = false;
                _swingTime = 0f;
                return;
            }

            _swingTime -= SwingSeconds;
            _strikes++;
        }
    }

    /// <summary>
    /// Builds the pose for this frame.
    /// </summary>
    /// <param name="lookPitchDegrees">
    /// Camera pitch, positive looking up. Negated on the way in: a limb's pitch is positive
    /// swinging forward, so a head that pitches positive is a head looking down.
    /// </param>
    public PlayerPose Pose(float lookYawDegrees, float lookPitchDegrees)
    {
        var swing = MathF.Cos(_limbPhase) * _limbAmount;

        // Arms counter the legs. A model whose right arm and right leg go forward together is the
        // single most recognisable way for a walk cycle to be wrong.
        var legPitch = swing * LegSwing;
        var armPitch = -swing * ArmSwing;

        // A standing model with perfectly still arms reads as a mannequin. This is small enough to
        // be invisible when it is moving and the only thing moving when it is not.
        var idleRoll = 0.05f + MathF.Cos(_age * 2.5f) * 0.05f;
        var idleLift = MathF.Sin(_age * 1.7f) * 0.03f * (1f - _limbAmount);

        var right = new LimbPose(armPitch + idleLift, 0f, idleRoll);
        var left = new LimbPose(-armPitch + idleLift, 0f, -idleRoll);

        var bodyYaw = BodyYawDegrees;

        if (_swinging)
        {
            var t = SwingProgress;

            // Fast up, slow down: the arm is at the top of the arc inside the first fifth and
            // spends the rest of the swing coming through the block. An even arc reads as a wave.
            var wind = 1f - MathF.Pow(1f - t, 4f);
            var lift = MathF.Sin(wind * MathF.PI);
            var follow = MathF.Sin(t * MathF.PI);

            right = new LimbPose(
                right.Pitch - lift * SwingLift,
                right.Yaw,
                right.Roll + follow * SwingSpread);

            // The torso twists into the blow and back out of it, which is what stops the arm
            // looking like it is bolted to a post.
            bodyYaw += MathF.Sin(MathF.Sqrt(t) * MathF.Tau) * SwingTwist;
        }

        // Crouching tips the torso over the feet and steps the legs back under it. Arms come
        // forward with the shoulders, since they hang from the body.
        var lean = _sneak * SneakLean;
        if (_sneak > 0f)
        {
            right = right with { Pitch = right.Pitch + _sneak * 0.4f };
            left = left with { Pitch = left.Pitch + _sneak * 0.4f };
        }

        return new PlayerPose(
            BodyYawDegrees: bodyYaw,
            BodyPitch: lean,
            BodyDropUnits: _sneak * SneakBodyDrop,
            Head: new LimbPose(
                float.DegreesToRadians(-lookPitchDegrees),
                float.DegreesToRadians(Wrap(lookYawDegrees - bodyYaw)),
                0f),
            RightArm: right,
            LeftArm: left,
            RightLeg: new LimbPose(legPitch, 0f, 0f),
            LeftLeg: new LimbPose(-legPitch, 0f, 0f),
            LegShiftUnits: _sneak * SneakLegShift,
            LegLiftUnits: _sneak * SneakLegLift);
    }

    /// <summary>Folds an angle into −180..180 so differences are the short way round.</summary>
    public static float Wrap(float degrees)
    {
        degrees %= 360f;
        if (degrees > 180f) degrees -= 360f;
        if (degrees < -180f) degrees += 360f;
        return degrees;
    }
}
