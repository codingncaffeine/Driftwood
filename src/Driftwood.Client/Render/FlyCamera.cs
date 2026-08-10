using System.Numerics;
using Silk.NET.Input;

namespace Driftwood.Client.Render;

/// <summary>
/// Free-flying debug camera. Not the player controller — that arrives at P3 with collision,
/// gravity and step-up. This one exists to inspect terrain and read frame cost.
/// </summary>
public sealed class FlyCamera
{
    /// <summary>
    /// Must stay strictly under 90. At exactly straight up or down the forward vector is parallel
    /// to the world up vector, <see cref="Matrix4x4.CreateLookAt"/> degenerates, and the view
    /// matrix fills with NaN — taking the frustum planes and every draw with it.
    /// </summary>
    private const float PitchLimit = 89f;

    public Vector3 Position;
    public float Yaw = -90f;      // degrees; -90 looks down -Z
    public float Pitch;
    public float FovDegrees = 70f;
    public float NearPlane = 0.1f;
    public float FarPlane = 2000f;

    public float MoveSpeed = 24f;
    public float BoostMultiplier = 5f;
    public float MouseSensitivity = 0.12f;

    public Vector3 Forward
    {
        get
        {
            var yaw = float.DegreesToRadians(Yaw);
            var pitch = float.DegreesToRadians(Pitch);
            var cosPitch = MathF.Cos(pitch);
            return Vector3.Normalize(new Vector3(
                MathF.Cos(yaw) * cosPitch,
                MathF.Sin(pitch),
                MathF.Sin(yaw) * cosPitch));
        }
    }

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));

    public void ApplyMouseDelta(float dx, float dy)
    {
        Yaw += dx * MouseSensitivity;
        Pitch -= dy * MouseSensitivity;
        Pitch = Math.Clamp(Pitch, -PitchLimit, PitchLimit);
    }

    /// <summary>Frame-rate-independent right-stick look, in a genre-sized degrees-per-second rate.</summary>
    public void ApplyControllerLook(Vector2 stick, float dt, int speedPercent, bool invertY)
    {
        var rate = 190f * Math.Clamp(speedPercent, 25, 300) / 100f;
        Yaw += stick.X * rate * Math.Max(0f, dt);
        Pitch -= stick.Y * rate * Math.Max(0f, dt) * (invertY ? -1f : 1f);
        Pitch = Math.Clamp(Pitch, -PitchLimit, PitchLimit);
    }

    /// <summary>Nudges toward a nearby target without ever snapping farther than one frame allows.</summary>
    public void AssistToward(Vector3 direction, float maximumDegrees)
    {
        if (direction.LengthSquared() < 1e-8f || maximumDegrees <= 0f) return;
        direction = Vector3.Normalize(direction);

        var targetYaw = float.RadiansToDegrees(MathF.Atan2(direction.Z, direction.X));
        var targetPitch = float.RadiansToDegrees(MathF.Asin(Math.Clamp(direction.Y, -1f, 1f)));
        var yawDelta = targetYaw - Yaw;
        while (yawDelta > 180f) yawDelta -= 360f;
        while (yawDelta < -180f) yawDelta += 360f;

        Yaw += Math.Clamp(yawDelta, -maximumDegrees, maximumDegrees);
        Pitch += Math.Clamp(targetPitch - Pitch, -maximumDegrees, maximumDegrees);
        Pitch = Math.Clamp(Pitch, -PitchLimit, PitchLimit);
    }

    public void Update(float dt, RawInput keyboard)
    {
        var speed = MoveSpeed * dt;
        if (keyboard.IsKeyPressed(Key.ShiftLeft)) speed *= BoostMultiplier;
        if (keyboard.IsKeyPressed(Key.AltLeft)) speed *= 0.25f;

        var forward = Forward;
        var right = Right;

        // Horizontal movement ignores pitch so looking down does not drive you into the ground.
        var flat = new Vector3(forward.X, 0f, forward.Z);
        if (flat.LengthSquared() > 1e-6f) flat = Vector3.Normalize(flat);

        // Arrows are the primary bind; WASD stays live alongside them. Both are placeholders
        // until the rebindable input map lands, so neither is worth being precious about.
        if (keyboard.IsKeyPressed(Key.Up) || keyboard.IsKeyPressed(Key.W)) Position += flat * speed;
        if (keyboard.IsKeyPressed(Key.Down) || keyboard.IsKeyPressed(Key.S)) Position -= flat * speed;
        if (keyboard.IsKeyPressed(Key.Right) || keyboard.IsKeyPressed(Key.D)) Position += right * speed;
        if (keyboard.IsKeyPressed(Key.Left) || keyboard.IsKeyPressed(Key.A)) Position -= right * speed;
        if (keyboard.IsKeyPressed(Key.Space) || keyboard.IsKeyPressed(Key.PageUp)) Position += Vector3.UnitY * speed;
        if (keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.PageDown)) Position -= Vector3.UnitY * speed;
    }

    public void UpdateController(float dt, Vector2 move, bool rise, bool fall, bool boost)
    {
        var speed = MoveSpeed * Math.Max(0f, dt) * (boost ? BoostMultiplier : 1f);
        var flat = new Vector3(Forward.X, 0f, Forward.Z);
        if (flat.LengthSquared() > 1e-6f) flat = Vector3.Normalize(flat);

        Position += flat * (move.Y * speed);
        Position += Right * (move.X * speed);
        if (rise) Position += Vector3.UnitY * speed;
        if (fall) Position -= Vector3.UnitY * speed;
    }

    /// <summary>Deterministic controller-camera checks that need no native provider or window.</summary>
    public static List<string> ControllerFaults()
    {
        var faults = new List<string>();
        var oneFrame = new FlyCamera();
        var manyFrames = new FlyCamera();
        var stick = new Vector2(0.55f, -0.4f);
        oneFrame.ApplyControllerLook(stick, 0.5f, 100, invertY: false);
        for (var i = 0; i < 50; i++)
            manyFrames.ApplyControllerLook(stick, 0.01f, 100, invertY: false);

        if (MathF.Abs(oneFrame.Yaw - manyFrames.Yaw) > 0.001f
            || MathF.Abs(oneFrame.Pitch - manyFrames.Pitch) > 0.001f)
            faults.Add("right-stick look changes with frame rate");

        var inverted = new FlyCamera();
        inverted.ApplyControllerLook(new Vector2(0f, -0.5f), 0.1f, 100, invertY: true);
        if (oneFrame.Pitch <= 0f || inverted.Pitch >= 0f)
            faults.Add("vertical look or invert-Y points the wrong way");

        var assisted = new FlyCamera();
        assisted.AssistToward(Vector3.UnitX, 5f);
        if (MathF.Abs(assisted.Yaw - -85f) > 0.001f || MathF.Abs(assisted.Pitch) > 0.001f)
            faults.Add("target assist did not take exactly its bounded five-degree step");

        return faults;
    }

    public Matrix4x4 ViewProjection(float aspect) => View(Position, Forward) * Projection(aspect);

    /// <summary>
    /// The view from an arbitrary place looking an arbitrary way.
    /// </summary>
    /// <remarks>
    /// Split out from <see cref="Position"/> and <see cref="Forward"/> because the third-person
    /// camera renders from the end of a boom while everything else — aiming, streaming, the
    /// coordinates in the title bar — still has to mean the player's eye.
    /// </remarks>
    public static Matrix4x4 View(Vector3 eye, Vector3 forward) =>
        Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitY);

    public Matrix4x4 Projection(float aspect) =>
        Matrix4x4.CreatePerspectiveFieldOfView(
            float.DegreesToRadians(FovDegrees), aspect, NearPlane, FarPlane);
}
