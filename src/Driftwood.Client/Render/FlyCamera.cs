using System.Numerics;
using Silk.NET.Input;

namespace Driftwood.Client.Render;

/// <summary>
/// Free-flying debug camera. Not the player controller — that arrives at P3 with collision,
/// gravity and step-up. This one exists to inspect terrain and read frame cost.
/// </summary>
public sealed class FlyCamera
{
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

    public void Update(float dt, IKeyboard keyboard)
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

    public Matrix4x4 ViewProjection(float aspect)
    {
        var view = Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            float.DegreesToRadians(FovDegrees), aspect, NearPlane, FarPlane);
        return view * proj;
    }
}
