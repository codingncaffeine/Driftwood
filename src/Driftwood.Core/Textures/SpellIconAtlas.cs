using System.Reflection;
using Driftwood.Core.Blocks;
using Driftwood.Core.Particles;

namespace Driftwood.Core.Textures;

/// <summary>One clean picture well in the user's original nineteen-spell sheet.</summary>
public readonly record struct SpellIconCrop(
    SpellParticleId Id, string TextureName, int SourceX, int SourceY);

/// <summary>
/// Reads the compact clean-well derivative of the user's original spell-painting sheet.
/// </summary>
/// <remarks>
/// <para>⛳ <b>The sheet is original Driftwood art.</b> The user confirmed that explicitly on
/// 2026-08-11. <c>IconForge --spell-icons</c> takes only each 80×80 picture interior; the original
/// sheet remains local, while the eventual spellbook supplies names and ranks through its own font.
/// </para>
/// <para>⚠ <b>Order is gameplay data.</b> Each row is at the same ordinal as
/// <see cref="SpellParticleId"/> and <see cref="SpellParticleEffects.Definitions"/>. That gives the
/// future spell runtime one stable conversion from a spell id to an atlas layer and prevents a
/// separately maintained UI switch from putting the wrong painting on a spell.</para>
/// <para>⚠ <b>Nearest neighbour from the source crop.</b> The drawings are pixel art. Sampling
/// straight from their 80-pixel wells keeps hard edges at 16px and preserves real detail when a
/// larger pack resolution is selected; a smoothed reduction made the smallest spellbook icons
/// visibly muddy.</para>
/// </remarks>
public static class SpellIconAtlas
{
    public const string ResourceName = "Driftwood.Core.spell-icons.png";
    public const int OriginalSourceWidth = 1408;
    public const int OriginalSourceHeight = 768;
    public const int CropSize = 80;
    public const int Columns = 5;
    public const int AtlasWidth = Columns * CropSize;
    public const int AtlasHeight = 4 * CropSize;

    private static readonly SpellIconCrop[] AllDefinitions =
    [
        new(SpellParticleId.HolyMight,       "spell_holy_might",       120, 116),
        new(SpellParticleId.QuickHeal,       "spell_quick_heal",       391, 116),
        new(SpellParticleId.Revive,          "spell_revive",           586, 116),
        new(SpellParticleId.HolyShield,      "spell_holy_shield",      781, 116),
        new(SpellParticleId.Root,            "spell_root",             120, 272),

        new(SpellParticleId.SummonBones,     "spell_summon_bones",     391, 272),
        new(SpellParticleId.AnimateZombie,   "spell_animate_zombie",   586, 272),
        new(SpellParticleId.Fear,            "spell_fear",             781, 272),
        new(SpellParticleId.DrawLifeforce,   "spell_draw_lifeforce",   120, 428),
        new(SpellParticleId.Leech,           "spell_leech",            385, 428),

        new(SpellParticleId.LightningStreak, "spell_lightning_streak", 586, 428),
        new(SpellParticleId.Ignite,          "spell_ignite",           781, 428),
        new(SpellParticleId.TreeOfLife,      "spell_tree_of_life",     781, 580),
        new(SpellParticleId.SpiritWolf,      "spell_spirit_wolf",      120, 580),

        new(SpellParticleId.IceShock,        "spell_ice_shock",        385, 580),
        new(SpellParticleId.FireBolt,        "spell_fire_bolt",        586, 580),
        new(SpellParticleId.GatewayRift,     "spell_gateway_rift",     996, 428),
        new(SpellParticleId.Snare,           "spell_snare",            995, 580),
        new(SpellParticleId.EarthElemental,  "spell_earth_elemental", 1206, 580),
    ];

    private static readonly Dictionary<(SpellParticleId Id, int Size), byte[]> Tiles = [];
    private static Image? _atlas;
    private static bool _atlasRead;

    public static ReadOnlySpan<SpellIconCrop> Definitions => AllDefinitions;

    /// <summary>The atlas layer for a catalogue spell, or an exception for a non-catalogue value.</summary>
    public static ushort LayerFor(SpellParticleId id)
    {
        var index = (int)id;
        if ((uint)index >= (uint)AllDefinitions.Length || AllDefinitions[index].Id != id)
            throw new ArgumentOutOfRangeException(nameof(id), id, "not a Driftwood spell icon");

        return checked((ushort)(StarterBlocks.LayerFirstSpellIcon + index));
    }

    /// <summary>Maps one appended texture layer back to its semantic spell.</summary>
    public static bool TryIdForLayer(int layer, out SpellParticleId id)
    {
        var index = layer - StarterBlocks.LayerFirstSpellIcon;
        if ((uint)index < (uint)AllDefinitions.Length)
        {
            id = AllDefinitions[index].Id;
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>One cropped icon at the texture array's requested size, or null if art is absent.</summary>
    public static byte[]? Tile(SpellParticleId id, int size)
    {
        if (size <= 0) return null;
        var index = (int)id;
        if ((uint)index >= (uint)AllDefinitions.Length || AllDefinitions[index].Id != id) return null;
        if (Tiles.TryGetValue((id, size), out var cached)) return cached;
        if (Atlas() is not { } atlas) return null;

        var cropX = index % Columns * CropSize;
        var cropY = index / Columns * CropSize;
        if (cropX + CropSize > atlas.Width || cropY + CropSize > atlas.Height)
            return null;

        var tile = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            var sy = cropY + (2 * y + 1) * CropSize / (2 * size);
            for (var x = 0; x < size; x++)
            {
                var sx = cropX + (2 * x + 1) * CropSize / (2 * size);
                var from = (sy * atlas.Width + sx) * 4;
                var to = (y * size + x) * 4;
                tile[to] = atlas.Pixels[from];
                tile[to + 1] = atlas.Pixels[from + 1];
                tile[to + 2] = atlas.Pixels[from + 2];
                tile[to + 3] = atlas.Pixels[from + 3];
            }
        }

        Tiles[(id, size)] = tile;
        return tile;
    }

    /// <summary>Reports the embedded clean atlas's decoded dimensions for the release audit.</summary>
    public static bool TryAtlasDimensions(out int width, out int height)
    {
        if (Atlas() is { } atlas)
        {
            width = atlas.Width;
            height = atlas.Height;
            return true;
        }

        width = height = 0;
        return false;
    }

    private static Image? Atlas()
    {
        if (_atlasRead) return _atlas;
        _atlasRead = true;

        using var stream = typeof(SpellIconAtlas).GetTypeInfo().Assembly
            .GetManifestResourceStream(ResourceName);
        if (stream is null) return null;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        if (Png.TryDecode(buffer.ToArray(), out var decoded, out _)) _atlas = decoded;
        return _atlas;
    }
}
