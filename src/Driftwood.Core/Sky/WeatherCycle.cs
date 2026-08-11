namespace Driftwood.Core.Sky;

public enum Precipitation
{
    Clear,
    Rain,
    Snow,
}

/// <summary>A deterministic, gently transitioned weather schedule derived from the world seed.</summary>
public readonly record struct WeatherState(Precipitation Kind, float Strength)
{
    public bool Active => Kind != Precipitation.Clear && Strength > 0.001f;
}

public static class WeatherCycle
{
    public const double PeriodSeconds = 150.0;
    private const double TransitionSeconds = 12.0;

    public static WeatherState Sample(long seed, double elapsedSeconds, float elevation)
    {
        // Seed offsets the schedule, so worlds do not all open into the same sky. The interval
        // number is hashed independently: a clear interval cannot predict the one after it.
        var offset = (uint)Mix((ulong)seed) % (uint)PeriodSeconds;
        var clock = Math.Max(0.0, elapsedSeconds) + offset;
        var interval = (long)Math.Floor(clock / PeriodSeconds);
        var within = clock - interval * PeriodSeconds;
        var roll = (uint)Mix(unchecked((ulong)(seed + interval * 0x5DEECE66DL))) / (float)uint.MaxValue;
        if (roll < 0.56f) return new WeatherState(Precipitation.Clear, 0f);

        var edge = Math.Min(within, PeriodSeconds - within);
        var strength = (float)Math.Clamp(edge / TransitionSeconds, 0.0, 1.0);
        var snow = elevation >= 104f || roll > 0.88f;
        return new WeatherState(snow ? Precipitation.Snow : Precipitation.Rain, strength);
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    public static IReadOnlyList<string> SelfTest(out string detail)
    {
        var faults = new List<string>();
        var kinds = new HashSet<Precipitation>();
        var transitioning = false;
        for (var seed = 1L; seed <= 48; seed++)
        for (var second = 0; second < 600; second += 3)
        {
            var state = Sample(seed, second, second % 2 == 0 ? 70f : 130f);
            kinds.Add(state.Kind);
            transitioning |= state.Strength is > 0f and < 1f;
            if (state.Strength is < 0f or > 1f) faults.Add("weather strength left 0..1");
        }
        if (!kinds.SetEquals(Enum.GetValues<Precipitation>()))
            faults.Add("the deterministic schedule did not produce clear, rain and snow");
        if (!transitioning) faults.Add("weather changes have no transition ramp");
        detail = "seeded 150-second clear/rain/snow intervals with 12-second transitions";
        return faults;
    }
}
