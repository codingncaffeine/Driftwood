using Driftwood.Core.Blocks;

namespace Driftwood.Core.Textures;

/// <summary>
/// The overlay that shows a block coming apart: ours, or a pack's if it carries them.
/// </summary>
/// <remarks>
/// Same posture as block tiles — generated here so a build with no art folder still shows cracking,
/// and layered over by whatever the player's pack ships. Packs almost always repaint these, because
/// they are on screen during the single most common action in the game.
/// </remarks>
public static class CrackTextures
{
    /// <summary>Where a pack keeps each stage, indexed by stage.</summary>
    private const string PackPathFormat = "textures/block/destroy_stage_{0}.png";

    public sealed record Result(byte[][] Stages, int Size, string Summary);

    public static Result Build(string? packPath, int size = TileGen.Size)
    {
        var stages = TileGen.Cracks(2001, MiningRules.Stages);
        for (var i = 0; i < stages.Length; i++) stages[i] = TileGen.Upscale(stages[i], size);

        if (string.IsNullOrWhiteSpace(packPath))
            return new Result(stages, size, $"{MiningRules.Stages} built-in stages");

        using var pack = TexturePack.Open(packPath);
        if (pack is null) return new Result(stages, size, $"{MiningRules.Stages} built-in stages");

        var replaced = 0;
        for (var i = 0; i < stages.Length; i++)
        {
            var tile = pack.TryLoadTile(string.Format(PackPathFormat, i), size);
            if (tile is null) continue;

            stages[i] = tile;
            replaced++;
        }

        // All or nothing is not enforced: a pack that ships six of ten stages gets its six, and our
        // own fill the gaps. It will not look quite right, but neither will refusing to show it.
        return new Result(
            stages, size,
            replaced == 0
                ? $"{MiningRules.Stages} built-in stages"
                : $"{replaced} of {MiningRules.Stages} stages from the pack");
    }
}
