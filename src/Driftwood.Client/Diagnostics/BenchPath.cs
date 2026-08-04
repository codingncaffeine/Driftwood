using System.Numerics;

namespace Driftwood.Client.Diagnostics;

/// <summary>
/// The fixed flight the benchmark measures along: a wide circle starting at the origin, with the
/// view sweeping either side of the direction of travel.
/// </summary>
/// <remarks>
/// <para>The path is parameterised by <em>elapsed time</em>, at a fixed speed in blocks per second.
/// Advancing a fixed distance per <em>frame</em> is the tempting alternative — every run would then
/// visit identical positions — but it makes the flight speed proportional to the frame rate, and
/// this renderer draws an empty world at nearly 2000 fps. The first version of this file did that
/// and flew two thousand blocks a second, so the streamer never caught up, nothing was ever drawn,
/// and it reported a beautifully flat frame graph over a blank screen.</para>
/// <para>At a fixed speed the flight is the same physical journey on every machine and the streamer
/// faces the same real-time pressure. A faster machine samples that journey more densely, which is
/// what the percentiles want anyway.</para>
/// <para>Shape matters too. A circle keeps revisiting the region it left, so chunks stream out and
/// back in — a straight line only ever tests the load half of the pipeline. The yaw sweep either
/// side of the tangent swings the frustum across the loaded set so the drawn-chunk count varies
/// instead of sitting at whatever one heading happens to show.</para>
/// </remarks>
public sealed class BenchPath
{
    /// <summary>
    /// Flight speed. Faster than a player runs, deliberately: chunk-boundary crossings are what
    /// provoke streaming work, and a walking pace crosses too few of them in a short run to say
    /// anything about the tail. One chunk every two thirds of a second.
    /// </summary>
    public const float BlocksPerSecond = 48f;

    /// <summary>Camera height above whichever is higher, the ground or the sea.</summary>
    private const float EyeHeight = 12f;

    private readonly Func<int, int, int> _surfaceHeight;
    private readonly float _seaLevel;

    public float Radius { get; }

    public BenchPath(float radius, float seaLevel, Func<int, int, int> surfaceHeight)
    {
        Radius = radius;
        _seaLevel = seaLevel;
        _surfaceHeight = surfaceHeight;
    }

    public float Circumference => 2f * MathF.PI * Radius;

    public static double DistanceOver(double seconds) => seconds * BlocksPerSecond;

    /// <summary>Straight-line distance from the start after flying the given arc.</summary>
    public float ChordAfter(double seconds)
        => 2f * Radius * MathF.Sin((float)(DistanceOver(seconds) / (2 * Radius)));

    public (Vector3 Position, float Yaw, float Pitch) At(double seconds)
    {
        // Arc length over radius, so the speed stays a speed when the radius changes.
        var theta = (float)(seconds * BlocksPerSecond) / Radius;

        // Starts at the origin heading +X and curves left, rather than starting at (r, 0) — the
        // origin is where the world's own reference points sit.
        var x = Radius * MathF.Sin(theta);
        var z = Radius * (1f - MathF.Cos(theta));

        var ground = _surfaceHeight((int)MathF.Floor(x), (int)MathF.Floor(z));
        var y = MathF.Max(ground, _seaLevel) + EyeHeight;

        // Yaw follows the tangent — the camera's forward vector is built from (cos yaw, _, sin yaw)
        // and the tangent is (cos theta, sin theta), so they are the same angle.
        var yaw = float.RadiansToDegrees(theta) + 25f * MathF.Sin(theta * 2f);
        var pitch = -10f + 8f * MathF.Sin(theta * 3f);

        return (new Vector3(x, y, z), yaw, pitch);
    }
}
