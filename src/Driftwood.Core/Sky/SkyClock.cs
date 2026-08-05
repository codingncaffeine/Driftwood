using System.Numerics;

namespace Driftwood.Core.Sky;

/// <summary>Everything the sky and the world's lighting need to know about the time of day.</summary>
/// <param name="SunDirection">From a surface toward the sun, normalised.</param>
/// <param name="MoonDirection">The same, toward the moon.</param>
/// <param name="SunColor">Colour and strength of direct sun on a surface facing it.</param>
/// <param name="SkyAmbient">What arrives from above on a surface facing nothing in particular.</param>
/// <param name="GroundAmbient">The bounce arriving from below.</param>
/// <param name="Zenith">Sky colour straight up.</param>
/// <param name="Horizon">Sky colour at eye level, and what distance fades into.</param>
/// <param name="StarFade">0 by day, 1 in the dark.</param>
public readonly record struct SkyState(
    Vector3 SunDirection,
    Vector3 MoonDirection,
    Vector3 SunColor,
    Vector3 SkyAmbient,
    Vector3 GroundAmbient,
    Vector3 Zenith,
    Vector3 Horizon,
    float StarFade);

/// <summary>
/// The day/night cycle: where the sun is, what colour the sky is, and how much of it a surface gets.
/// </summary>
/// <remarks>
/// <para>Headless and pure — one number in, a state out — because everything about a sky is a
/// judgement about colour, and a judgement about colour is exactly the sort of thing that gets
/// checked by eye at one moment of one day and shipped broken for the other twenty-three hours. A
/// clock that answers questions without a window can be asked about every minute of the day.</para>
/// <para>Midnight is zero. Sunrise a quarter, noon a half, sunset three quarters. Choosing midnight
/// rather than dawn for the origin means the interesting half of the cycle does not straddle the
/// wrap, and the wrap itself lands where nothing is changing quickly.</para>
/// <para>Colour comes from the sun's elevation rather than from the clock, and that is what makes it
/// right at both ends of the day without a second table. The horizon goes gold as the sun passes
/// through it, whichever direction it is travelling.</para>
/// </remarks>
public sealed class SkyClock
{
    /// <summary>A full day, in seconds. Twenty minutes, which is the genre's own.</summary>
    public const float DefaultDayLength = 1200f;

    /// <summary>
    /// How far the sun's arc leans out of the east-west plane, in radians.
    /// </summary>
    /// <remarks>
    /// A sun that passes exactly through the zenith puts every vertical face in identical light at
    /// noon and leaves the ground with no direction at all. Leaning the arc means midday still has a
    /// bright side and a shaded side, which is most of what makes terrain read as terrain.
    /// </remarks>
    private const float ArcTilt = 0.36f;

    /// <summary>Where in the day the clock currently is, 0 to 1 with midnight at zero.</summary>
    public float TimeOfDay { get; private set; }

    /// <summary>Seconds in a full cycle.</summary>
    public float DayLength { get; }

    /// <summary>Paused clocks are what a screenshot of a particular hour needs.</summary>
    public bool Running { get; set; } = true;

    public SkyClock(float startTime = 0.35f, float dayLength = DefaultDayLength)
    {
        TimeOfDay = Wrap(startTime);
        DayLength = MathF.Max(dayLength, 1f);
    }

    public void Advance(float seconds)
    {
        if (!Running) return;
        TimeOfDay = Wrap(TimeOfDay + seconds / DayLength);
    }

    public void SetTime(float time) => TimeOfDay = Wrap(time);

    /// <summary>The sky as it stands right now.</summary>
    public SkyState Now => At(TimeOfDay);

    /// <summary>Reads the sky at any time without moving the clock, which is what the checks want.</summary>
    public static SkyState At(float time)
    {
        var sun = SunAt(time);
        var elevation = sun.Y;

        // Four keys down the sun's elevation rather than across the clock. Sunrise and sunset are
        // the same event seen twice, and keying on elevation means they are the same entry.
        var zenith = Ramp(elevation,
            new Vector3(0.030f, 0.038f, 0.085f),   // night
            new Vector3(0.150f, 0.170f, 0.330f),   // the sun just under
            new Vector3(0.300f, 0.420f, 0.700f),   // low and golden
            new Vector3(0.290f, 0.500f, 0.900f));  // full day

        var horizon = Ramp(elevation,
            new Vector3(0.055f, 0.070f, 0.140f),
            new Vector3(0.360f, 0.260f, 0.330f),
            new Vector3(0.960f, 0.600f, 0.330f),
            new Vector3(0.640f, 0.780f, 0.930f));

        var sunColor = Ramp(elevation,
            Vector3.Zero,
            new Vector3(0.060f, 0.050f, 0.070f),
            new Vector3(0.780f, 0.480f, 0.260f),
            new Vector3(0.980f, 0.940f, 0.840f) * 0.62f);

        var skyAmbient = Ramp(elevation,
            new Vector3(0.055f, 0.062f, 0.095f),
            new Vector3(0.110f, 0.120f, 0.180f),
            new Vector3(0.290f, 0.300f, 0.400f),
            new Vector3(0.440f, 0.500f, 0.620f));

        var groundAmbient = Ramp(elevation,
            new Vector3(0.026f, 0.028f, 0.040f),
            new Vector3(0.050f, 0.050f, 0.070f),
            new Vector3(0.150f, 0.130f, 0.120f),
            new Vector3(0.220f, 0.200f, 0.170f));

        // Stars come out once the sun is properly down, and are gone before it is properly up.
        var stars = 1f - Smoothstep(-0.10f, 0.06f, elevation);

        return new SkyState(sun, -sun, sunColor, skyAmbient, groundAmbient, zenith, horizon, stars);
    }

    /// <summary>Where the sun sits at a given time, as a direction from the ground toward it.</summary>
    /// <remarks>
    /// East at sunrise, overhead at noon, west at sunset, and under the world at midnight. Written
    /// as one rotation rather than as a table so there is no hour it can disagree with itself about.
    /// </remarks>
    public static Vector3 SunAt(float time)
    {
        var angle = (Wrap(time) - 0.25f) * MathF.Tau;

        // The arc lies in the east-up plane, then leans out of it about the east-west axis.
        var east = MathF.Cos(angle);
        var up = MathF.Sin(angle);

        return Vector3.Normalize(new Vector3(
            east,
            up * MathF.Cos(ArcTilt),
            up * MathF.Sin(ArcTilt)));
    }

    /// <summary>True while the sun is above the horizon.</summary>
    public static bool IsDay(float time) => SunAt(time).Y > 0f;

    /// <summary>
    /// Blends four keys across the sun's elevation: deep night, just under, low and gold, full day.
    /// </summary>
    private static Vector3 Ramp(float elevation, Vector3 night, Vector3 under, Vector3 gold, Vector3 day)
    {
        if (elevation <= -0.25f) return night;
        if (elevation < -0.05f) return Vector3.Lerp(night, under, (elevation + 0.25f) / 0.20f);
        if (elevation < 0.12f) return Vector3.Lerp(under, gold, (elevation + 0.05f) / 0.17f);
        if (elevation < 0.40f) return Vector3.Lerp(gold, day, (elevation - 0.12f) / 0.28f);
        return day;
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Wrap(float time) => time - MathF.Floor(time);
}
