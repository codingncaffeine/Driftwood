using Driftwood.Core.Blocks;
using Driftwood.Core.Gen;

namespace Driftwood.Core.Textures;

/// <summary>
/// Turns a world position into the colour its grass, leaves or water should be multiplied by.
/// </summary>
/// <remarks>
/// <para>Three sources behind one interface, because the three genuinely work differently and
/// pretending otherwise would mean forcing two of them through a mechanism built for the first.
/// Grass and foliage are colormap lookups keyed on climate; water is a flat colour that has nothing
/// to do with any colormap. A pack can replace the colormaps and will expect all three to keep
/// behaving as they do everywhere else.</para>
/// <para>Colours are quantised before they leave here. Climate is a continuous field, so without
/// rounding no two neighbouring blocks would agree on a colour, every chunk would exhaust its tint
/// palette immediately, and greedy merging would collapse to one quad per face. Five bits a channel
/// keeps thirty-two steps per axis — far below what the eye picks out across a hillside, and far
/// above what makes a chunk run out of entries.</para>
/// </remarks>
public sealed class BlockTinter
{
    /// <summary>White, meaning "do not tint". Never enters a palette.</summary>
    public const int NoTint = 0xFFFFFF;

    private readonly ClimateField _climate;
    private readonly byte[] _grass;
    private readonly byte[] _foliage;
    private readonly int _water;

    public BlockTinter(ClimateField climate, byte[]? grassMap = null, byte[]? foliageMap = null, int water = 0x3F76E4)
    {
        _climate = climate;
        _grass = grassMap ?? Colormap.Grass();
        _foliage = foliageMap ?? Colormap.Foliage();
        _water = water;
    }

    /// <summary>The tint for a position, rounded so neighbouring blocks share entries.</summary>
    /// <remarks>
    /// Colour comes from the column, not the cell — height is deliberately not consulted, even
    /// though the climate field cools with altitude and biome selection will want that. Tint is
    /// part of a face's merge key, and a tint that varies with y varies <em>down a wall</em>, so
    /// every vertical face of every tree would split into single blocks. The gain would be foliage
    /// very slightly paler further up a hill; the price is the mesher's whole reason for existing.
    /// </remarks>
    public int Quantised(TintSource source, int wx, int wy, int wz)
    {
        if (source == TintSource.None) return NoTint;
        if (source == TintSource.Water) return _water;

        var temperature = _climate.Temperature(wx, wz);
        var downfall = _climate.Downfall(wx, wz);

        var map = source == TintSource.Grass ? _grass : _foliage;
        var (r, g, b) = Colormap.Sample(map, temperature, downfall);

        return (Round(r) << 16) | (Round(g) << 8) | Round(b);
    }

    /// <summary>Rounds a channel to five bits, keeping full white reachable.</summary>
    private static int Round(byte value) => (value & 0xF8) | (value >> 5);
}
