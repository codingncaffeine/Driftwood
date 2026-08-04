namespace Driftwood.Core.Gen;

/// <summary>
/// Temperature and rainfall over the world, as two slow noise fields.
/// </summary>
/// <remarks>
/// <para>This is the input biomes will be chosen from at P4, but it is worth having now for its
/// own sake: it is what decides the colour of grass and leaves. A world where every blade of grass
/// is the same green reads as a texture applied to terrain rather than as terrain.</para>
/// <para>Both fields run at a much longer wavelength than the height field, and deliberately
/// independently of it. Tying colour to altitude would put a snowline on every hill and a jungle in
/// every valley; real climate crosses terrain rather than following it. A little correlation is
/// added back through altitude alone, because high ground genuinely is colder.</para>
/// </remarks>
public sealed class ClimateField
{
    private readonly int _seedTemperature;
    private readonly int _seedDownfall;

    public ClimateField(WorldSeed seed)
    {
        _seedTemperature = seed.Derive("climate.temperature");
        _seedDownfall = seed.Derive("climate.downfall");
    }

    /// <summary>Warmth at a column, 0 (freezing) to 1 (hot), before altitude is taken off.</summary>
    public float Temperature(int wx, int wz) =>
        Normalise(Noise.Fbm2(wx / 1400f, wz / 1400f, _seedTemperature, 3));

    /// <summary>Rainfall at a column, 0 (arid) to 1 (soaking).</summary>
    public float Downfall(int wx, int wz) =>
        Normalise(Noise.Fbm2(wx / 900f, wz / 900f, _seedDownfall, 3));

    /// <summary>
    /// The pair a colour lookup wants, with height folded into the temperature.
    /// </summary>
    /// <remarks>
    /// Cooling with altitude is the one place climate is allowed to follow terrain, because it is
    /// the one place the two are actually related. Roughly a degree of the 0..1 scale per twenty
    /// blocks above sea level, so a tall mountain in a warm region still has a cold summit.
    /// </remarks>
    public (float Temperature, float Downfall) At(int wx, int wy, int wz)
    {
        var temperature = Temperature(wx, wz);
        if (wy > TerrainGenerator.SeaLevel)
            temperature -= (wy - TerrainGenerator.SeaLevel) / 300f;

        return (Math.Clamp(temperature, 0f, 1f), Downfall(wx, wz));
    }

    /// <summary>
    /// fBm normalised by its octave-amplitude sum peaks near ±0.4, not ±1, so a raw remap to 0..1
    /// would leave every sample bunched around the middle and the world one temperature.
    /// </summary>
    private static float Normalise(float fbm) => Math.Clamp(fbm * 1.25f + 0.5f, 0f, 1f);
}
