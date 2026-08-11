using Driftwood.Core.Entities;
using Driftwood.Core.Exploration;
using Driftwood.Core.Gen;

namespace Driftwood.Core.Magic;

public readonly record struct CharacterReward(int Experience, long Coins, string Reason)
{
    public bool Empty => Experience <= 0 && Coins <= 0;
}

public readonly record struct LeveledGearReward(string ItemName, int Rank, string Reason)
{
    public bool Empty => string.IsNullOrWhiteSpace(ItemName) || Rank is < 1 or > 4;
}

/// <summary>
/// One condition-aware progression table for creature contribution, authored-world discovery,
/// generated chests, encounter completion and bounded survival milestones. The caller still owns
/// the authoritative receipt; this service owns only deterministic value and tuning.
/// </summary>
public static class CharacterRewards
{
    public const int SurvivalSeconds = 600;
    public const int MaximumSurvivalMilestones = 24;

    public static CharacterReward Creature(string kind, bool grown, bool hostile)
    {
        if (!grown) return new(0, 0, "young creatures are trivial and award nothing");
        var family = CreatureSet.All.FirstOrDefault(one => one.Name == kind).Family;
        var health = CreatureVitals.HealthFor(kind);
        var damage = CreatureVitals.DamageFor(kind);
        var threat = Math.Max(1, health + damage * 8);
        var experience = family == CreatureFamily.Encounter
            ? 120 + threat
            : hostile ? 28 + threat : 12 + health;
        var coins = family == CreatureFamily.Encounter
            ? 450L + threat * 12L
            : hostile ? 80L + threat * 4L : 18L + health * 2L;
        return new(Math.Clamp(experience, 1, 600), Math.Max(1, coins),
            family == CreatureFamily.Encounter ? "authored encounter threat"
            : hostile ? "hostile threat and contribution" : "grown creature contribution");
    }

    public static CharacterReward Discovery(StructureKind kind) => kind switch
    {
        StructureKind.BuriedGallery => new(180, 800, "first buried-gallery discovery"),
        StructureKind.Driftstead => new(120, 500, "first Driftstead discovery"),
        StructureKind.Tidewreck => new(220, 1_100, "first tidewreck discovery"),
        StructureKind.StormVault => new(320, 1_800, "first storm-vault discovery"),
        StructureKind.StarfallCrown => new(420, 2_600, "first Starfall Crown discovery"),
        _ => default,
    };

    public static CharacterReward Chest(WorldSeed seed, StructureSite site, int chestIndex)
    {
        if (chestIndex < 0) return default;
        var roll = unchecked((uint)seed.Derive($"character-loot/{site.Id}/{chestIndex}"));
        var baseCoins = site.Kind switch
        {
            StructureKind.BuriedGallery => 260,
            StructureKind.Driftstead => 180,
            StructureKind.Tidewreck => 420,
            StructureKind.StormVault => 700,
            StructureKind.StarfallCrown => 1_100,
            _ => 0,
        };
        return new(0, baseCoins + roll % (uint)Math.Max(1, baseCoins), "personal generated-chest coin cache");
    }

    /// <summary>
    /// The first generated chest at a site carries one personal gear cache tuned to the opener's
    /// current automatic spell rank.  It supplements the shared authored chest rather than
    /// replacing crafting, and its deterministic choice cannot be rerolled by reopening the lid.
    /// </summary>
    public static LeveledGearReward Gear(WorldSeed seed, StructureSite site, int chestIndex)
    {
        if (chestIndex != 0) return default;
        var rank = site.Kind switch
        {
            StructureKind.BuriedGallery or StructureKind.Driftstead => 1,
            StructureKind.Tidewreck => 2,
            StructureKind.StormVault => 3,
            StructureKind.StarfallCrown => 4,
            _ => 0,
        };
        if (rank == 0) return default;
        var material = rank switch { 1 => "leather", 2 => "copper", 3 => "iron", _ => "stormglass" };
        var weapon = rank switch { 1 => "stone_sword", 2 => "copper_sword", 3 => "iron_sword", _ => "stormglass_sword" };
        var armour = new[] { "helmet", "chestplate", "leggings", "boots" };
        var roll = unchecked((uint)seed.Derive($"character-gear/{site.Id}/{chestIndex}/{rank}"));
        var item = roll % 3u == 0u ? weapon
            : $"{material}_{armour[(int)((roll >> 8) % (uint)armour.Length)]}";
        return new(item, rank, $"personal {CharacterProgression.RankName(rank)} gear cache");
    }

    public static CharacterReward Encounter(EncounterKind kind) => kind switch
    {
        EncounterKind.Trial => new(500, 3_500, "storm-vault completion"),
        EncounterKind.Crown => new(900, 8_000, "Starfall Crown completion"),
        _ => default,
    };

    public static CharacterReward Survival(int milestone)
    {
        if (milestone is < 1 or > MaximumSurvivalMilestones) return default;
        return new(40 + Math.Min(milestone, 5) * 10, 0,
            $"survived {milestone * SurvivalSeconds / 60} minutes");
    }
}
