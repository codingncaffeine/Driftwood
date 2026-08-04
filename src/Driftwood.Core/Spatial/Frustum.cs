using System.Numerics;

namespace Driftwood.Core.Spatial;

/// <summary>
/// The six clipping planes of a view-projection matrix, for rejecting whole chunks before they
/// reach the GPU.
/// </summary>
/// <remarks>
/// <para>Planes are pulled straight out of the combined matrix rather than rebuilt from camera
/// angles, so they always agree with whatever the shader actually uses. Each is stored as
/// <c>(a, b, c, d)</c> with <c>ax + by + cz + d &gt; 0</c> meaning inside.</para>
/// <para>The near plane is taken as the bare third column, not column three plus column two.
/// <see cref="Matrix4x4.CreatePerspectiveFieldOfView"/> maps depth to 0..1 rather than -1..1, so
/// the near condition is <c>z &gt; 0</c>. Using the -1..1 form would put the near plane in the
/// wrong place and quietly cull geometry in front of the camera.</para>
/// </remarks>
public struct Frustum
{
    private Vector4 _left, _right, _bottom, _top, _near, _far;

    public static Frustum FromViewProjection(Matrix4x4 m)
    {
        var f = new Frustum
        {
            // Row-vector convention: clip.x is the dot of the world position with column zero,
            // so each plane is a combination of columns, not rows.
            _left = new Vector4(m.M11 + m.M14, m.M21 + m.M24, m.M31 + m.M34, m.M41 + m.M44),
            _right = new Vector4(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41),
            _bottom = new Vector4(m.M12 + m.M14, m.M22 + m.M24, m.M32 + m.M34, m.M42 + m.M44),
            _top = new Vector4(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42),
            _near = new Vector4(m.M13, m.M23, m.M33, m.M43),
            _far = new Vector4(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43),
        };

        Normalize(ref f._left);
        Normalize(ref f._right);
        Normalize(ref f._bottom);
        Normalize(ref f._top);
        Normalize(ref f._near);
        Normalize(ref f._far);
        return f;
    }

    private static void Normalize(ref Vector4 plane)
    {
        var length = new Vector3(plane.X, plane.Y, plane.Z).Length();
        if (length > 1e-6f) plane /= length;
    }

    /// <summary>
    /// True when any part of the box could be visible. Conservative: a box straddling a corner
    /// may pass when it is actually outside, which costs a draw call rather than a missing chunk.
    /// </summary>
    public readonly bool IntersectsBox(Vector3 min, Vector3 max) =>
        InsideOrCrossing(_left, min, max)
        && InsideOrCrossing(_right, min, max)
        && InsideOrCrossing(_bottom, min, max)
        && InsideOrCrossing(_top, min, max)
        && InsideOrCrossing(_near, min, max)
        && InsideOrCrossing(_far, min, max);

    /// <summary>
    /// Tests the box corner furthest along the plane normal. If even that corner is behind the
    /// plane, every other corner is too and the box is wholly outside.
    /// </summary>
    private static bool InsideOrCrossing(Vector4 plane, Vector3 min, Vector3 max)
    {
        var px = plane.X >= 0f ? max.X : min.X;
        var py = plane.Y >= 0f ? max.Y : min.Y;
        var pz = plane.Z >= 0f ? max.Z : min.Z;
        return plane.X * px + plane.Y * py + plane.Z * pz + plane.W >= 0f;
    }

    /// <summary>
    /// Exercises the planes against boxes whose visibility is known by construction, returning one
    /// line per fault and nothing when the frustum behaves.
    /// </summary>
    /// <remarks>
    /// Culling failures are the worst kind to find by eye: geometry vanishes, and it vanishes only
    /// at angles you were not looking from. A sign error on one plane still leaves most of the
    /// world on screen. The sweep below turns the camera through a full circle and insists the box
    /// straight ahead is always kept and the box straight behind is always dropped, which no
    /// single-viewpoint check would catch.
    /// </remarks>
    public static IReadOnlyList<string> SelfTest()
    {
        var faults = new List<string>();
        const float near = 0.1f, far = 500f;

        static (Vector3 Min, Vector3 Max) BoxAt(Vector3 centre, float half) =>
            (centre - new Vector3(half), centre + new Vector3(half));

        static Matrix4x4 Look(Vector3 eye, Vector3 forward, float aspect, float n, float f)
        {
            var view = Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(float.DegreesToRadians(70f), aspect, n, f);
            return view * proj;
        }

        var eye = new Vector3(100f, 64f, 100f);

        for (var degrees = 0; degrees < 360; degrees += 15)
        {
            var yaw = float.DegreesToRadians(degrees);
            var forward = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
            var frustum = FromViewProjection(Look(eye, forward, 16f / 9f, near, far));

            var ahead = BoxAt(eye + forward * 60f, 16f);
            if (!frustum.IntersectsBox(ahead.Min, ahead.Max))
                faults.Add($"yaw {degrees}: box 60 units ahead was culled");

            var behind = BoxAt(eye - forward * 120f, 16f);
            if (frustum.IntersectsBox(behind.Min, behind.Max))
                faults.Add($"yaw {degrees}: box 120 units behind was kept");

            var beyond = BoxAt(eye + forward * (far + 200f), 16f);
            if (frustum.IntersectsBox(beyond.Min, beyond.Max))
                faults.Add($"yaw {degrees}: box past the far plane was kept");

            // A box enclosing the camera crosses every plane and must never be rejected —
            // culling it would delete the ground under the player's feet.
            var enclosing = BoxAt(eye, 20f);
            if (!frustum.IntersectsBox(enclosing.Min, enclosing.Max))
                faults.Add($"yaw {degrees}: box containing the camera was culled");
        }

        // Steepest look the camera actually permits. Not -90: a forward vector parallel to the up
        // vector makes CreateLookAt degenerate and fills the matrix with NaN, which is why the
        // camera clamps pitch to +/-89. That clamp is load-bearing, not cosmetic.
        var steep = float.DegreesToRadians(-89f);
        var steepForward = Vector3.Normalize(new Vector3(MathF.Cos(steep), MathF.Sin(steep), 0f));
        var down = FromViewProjection(Look(eye, steepForward, 16f / 9f, near, far));

        var below = BoxAt(eye + steepForward * 40f, 16f);
        if (!down.IntersectsBox(below.Min, below.Max)) faults.Add("pitch -89: box below was culled");

        var above = BoxAt(eye - steepForward * 120f, 16f);
        if (down.IntersectsBox(above.Min, above.Max)) faults.Add("pitch -89: box behind was kept");

        return faults;
    }
}
