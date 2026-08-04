using System.Text;

namespace Driftwood.Core.Gen;

/// <summary>
/// A world's master seed, plus derivation of independent per-stage seeds.
/// </summary>
/// <remarks>
/// <para>Every generator stage draws its own seed via <see cref="Derive"/> rather than sharing one
/// noise stream. That matters more than it looks: if stages shared a stream, adding cave carving
/// later would shift every downstream draw and the same seed would stop producing the same world.
/// Domain separation means a seed keeps its meaning as the generator grows.</para>
/// <para>The same property makes generation order-independent, which is what lets chunks generate
/// out of order on worker threads once streaming lands.</para>
/// </remarks>
public readonly struct WorldSeed
{
    public readonly long Value;

    public WorldSeed(long value) => Value = value;

    /// <summary>
    /// Parses a user-entered seed. Digits are taken literally; anything else is hashed, so
    /// "driftwood" is a valid seed. Empty input draws a fresh random one.
    /// </summary>
    public static WorldSeed Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Random();
        text = text.Trim();
        if (long.TryParse(text, out var n)) return new WorldSeed(n);

        // FNV-1a over the text so the same words always give the same world.
        ulong h = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return new WorldSeed(unchecked((long)h));
    }

    public static WorldSeed Random() => new(System.Random.Shared.NextInt64());

    /// <summary>Independent seed for one named generator stage, e.g. "caves" or "ore.iron".</summary>
    public int Derive(string stage)
    {
        var h = unchecked((ulong)Value) * 0x9E3779B97F4A7C15UL;
        foreach (var c in stage)
        {
            h ^= c;
            h *= 0x100000001B3UL;
        }
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        return unchecked((int)h);
    }

    public override string ToString() => Value.ToString();
}
