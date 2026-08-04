namespace Driftwood.Core.Gen;

/// <summary>
/// Seeded gradient (Perlin) noise plus fractal Brownian motion on top of it.
/// </summary>
/// <remarks>
/// Gradients are hashed from the integer lattice coordinate and the seed rather than looked up
/// through a shuffled permutation table. That keeps the whole thing stateless and thread-safe:
/// any worker can evaluate any point of any stage without shared mutable state, which is what
/// out-of-order chunk generation needs.
/// </remarks>
public static class Noise
{
    private static int Hash(int x, int y, int z, int seed)
    {
        unchecked
        {
            var h = seed;
            h ^= x * 0x27D4EB2D;
            h ^= y * 0x165667B1;
            h ^= z * unchecked((int)0x9E3779B1);   // golden-ratio constant, wraps past int.MaxValue
            h = (h ^ (h >> 15)) * 0x2C1B3C6D;
            h = (h ^ (h >> 12)) * 0x297A2D39;
            return h ^ (h >> 15);
        }
    }

    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Dot of one of 12 cube-edge gradients with the offset vector.</summary>
    private static float Grad3(int hash, float x, float y, float z)
    {
        return (hash & 15) switch
        {
            0 => x + y,
            1 => -x + y,
            2 => x - y,
            3 => -x - y,
            4 => x + z,
            5 => -x + z,
            6 => x - z,
            7 => -x - z,
            8 => y + z,
            9 => -y + z,
            10 => y - z,
            11 => -y - z,
            12 => x + y,
            13 => -y + z,
            14 => -x + y,
            _ => -y - z,
        };
    }

    private static float Grad2(int hash, float x, float y)
    {
        return (hash & 7) switch
        {
            0 => x,
            1 => x + y,
            2 => y,
            3 => -x + y,
            4 => -x,
            5 => -x - y,
            6 => -y,
            _ => x - y,
        };
    }

    /// <summary>2D gradient noise, roughly -1..1.</summary>
    public static float Perlin2(float x, float y, int seed)
    {
        var xi = (int)MathF.Floor(x);
        var yi = (int)MathF.Floor(y);
        var xf = x - xi;
        var yf = y - yi;
        var u = Fade(xf);
        var v = Fade(yf);

        var n00 = Grad2(Hash(xi, yi, 0, seed), xf, yf);
        var n10 = Grad2(Hash(xi + 1, yi, 0, seed), xf - 1f, yf);
        var n01 = Grad2(Hash(xi, yi + 1, 0, seed), xf, yf - 1f);
        var n11 = Grad2(Hash(xi + 1, yi + 1, 0, seed), xf - 1f, yf - 1f);

        return Lerp(Lerp(n00, n10, u), Lerp(n01, n11, u), v);
    }

    /// <summary>3D gradient noise, roughly -1..1.</summary>
    public static float Perlin3(float x, float y, float z, int seed)
    {
        var xi = (int)MathF.Floor(x);
        var yi = (int)MathF.Floor(y);
        var zi = (int)MathF.Floor(z);
        var xf = x - xi;
        var yf = y - yi;
        var zf = z - zi;
        var u = Fade(xf);
        var v = Fade(yf);
        var w = Fade(zf);

        var n000 = Grad3(Hash(xi, yi, zi, seed), xf, yf, zf);
        var n100 = Grad3(Hash(xi + 1, yi, zi, seed), xf - 1f, yf, zf);
        var n010 = Grad3(Hash(xi, yi + 1, zi, seed), xf, yf - 1f, zf);
        var n110 = Grad3(Hash(xi + 1, yi + 1, zi, seed), xf - 1f, yf - 1f, zf);
        var n001 = Grad3(Hash(xi, yi, zi + 1, seed), xf, yf, zf - 1f);
        var n101 = Grad3(Hash(xi + 1, yi, zi + 1, seed), xf - 1f, yf, zf - 1f);
        var n011 = Grad3(Hash(xi, yi + 1, zi + 1, seed), xf, yf - 1f, zf - 1f);
        var n111 = Grad3(Hash(xi + 1, yi + 1, zi + 1, seed), xf - 1f, yf - 1f, zf - 1f);

        var x00 = Lerp(n000, n100, u);
        var x10 = Lerp(n010, n110, u);
        var x01 = Lerp(n001, n101, u);
        var x11 = Lerp(n011, n111, u);

        return Lerp(Lerp(x00, x10, v), Lerp(x01, x11, v), w);
    }

    /// <summary>Summed octaves of <see cref="Perlin2"/>, normalised to roughly -1..1.</summary>
    public static float Fbm2(float x, float y, int seed, int octaves, float lacunarity = 2f, float gain = 0.5f)
    {
        var sum = 0f;
        var amp = 1f;
        var freq = 1f;
        var norm = 0f;

        for (var i = 0; i < octaves; i++)
        {
            sum += Perlin2(x * freq, y * freq, seed + i * 1013) * amp;
            norm += amp;
            amp *= gain;
            freq *= lacunarity;
        }

        return norm > 0f ? sum / norm : 0f;
    }

    /// <summary>Summed octaves of <see cref="Perlin3"/>, normalised to roughly -1..1.</summary>
    public static float Fbm3(float x, float y, float z, int seed, int octaves, float lacunarity = 2f, float gain = 0.5f)
    {
        var sum = 0f;
        var amp = 1f;
        var freq = 1f;
        var norm = 0f;

        for (var i = 0; i < octaves; i++)
        {
            sum += Perlin3(x * freq, y * freq, z * freq, seed + i * 1013) * amp;
            norm += amp;
            amp *= gain;
            freq *= lacunarity;
        }

        return norm > 0f ? sum / norm : 0f;
    }

    /// <summary>Deterministic 0..1 value from a 2D cell and seed. Used for placement rolls.</summary>
    public static float Value2(int x, int z, int seed)
    {
        var h = Hash(x, 0, z, seed);
        return (h & 0x00FFFFFF) / (float)0x01000000;
    }
}
