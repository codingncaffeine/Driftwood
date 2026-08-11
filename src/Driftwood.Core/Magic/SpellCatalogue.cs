using Driftwood.Core.Particles;

namespace Driftwood.Core.Magic;

/// <summary>The four open shelves in the spellbook. They are labels, never player classes.</summary>
public enum SpellGroup : byte
{
    BeaconRites,
    Gravecalling,
    Tidecalling,
    Arcanistry,
}

/// <summary>Stable catalogue identity. Values deliberately match <see cref="SpellParticleId"/>.</summary>
public enum SpellId : byte
{
    HolyMight,
    QuickHeal,
    Revive,
    HolyShield,
    Root,
    SummonBones,
    AnimateZombie,
    Fear,
    DrawLifeforce,
    Leech,
    LightningStreak,
    Ignite,
    TreeOfLife,
    SpiritWolf,
    IceShock,
    FireBolt,
    GatewayRift,
    Snare,
    EarthElemental,
}

public enum SpellTarget : byte
{
    Self,
    SelfOrAlly,
    DeadAlly,
    Hostile,
    Ground,
}

public enum SpellDelivery : byte
{
    Instant,
    Cast,
    Channel,
    Projectile,
    Summon,
    Portal,
}

/// <summary>
/// One rank's simulation values. Primary is damage/healing/pet health, Secondary is shield,
/// drain, toughness or movement percentage according to the definition's mechanic label.
/// </summary>
public readonly record struct SpellRank(
    int Primary,
    int Secondary,
    float Duration,
    int Focus,
    float Cooldown,
    float CastTime,
    float Range)
{
    public string Roman(int rank) => rank switch { 1 => "I", 2 => "II", 3 => "III", _ => "IV" };
}

/// <summary>Everything UI, simulation and the generated wiki know about one spell.</summary>
public sealed record SpellDefinition(
    SpellId Id,
    string StableName,
    string DisplayName,
    SpellGroup Group,
    SpellTarget Target,
    SpellDelivery Delivery,
    long Price,
    string Mechanic,
    string Description,
    string Flavour,
    string WikiSlug,
    string IconKey,
    string AudioKey,
    string[] Tags,
    SpellRank[] Ranks)
{
    public SpellRank AtRank(int rank) => Ranks[Math.Clamp(rank, 1, 4) - 1];
    public SpellParticleId Particle => (SpellParticleId)Id;
}

/// <summary>The exact, immutable P10.5 catalogue. All values displayed publicly originate here.</summary>
public static class SpellCatalogue
{
    private static readonly SpellDefinition[] Definitions =
    [
        Spell(SpellId.HolyMight, "Holy Might", SpellGroup.BeaconRites, SpellTarget.Hostile,
            SpellDelivery.Instant, 12_00, "sacred damage",
            "Strike one hostile with concentrated sacred force.",
            "A clear light leaves nowhere for malice to hide.", "holy-might", "holy_might",
            ["damage", "direct", "sacred"],
            R((5,0,0,12,3,0,18), (8,0,0,13,3,0,19), (12,0,0,14,3,0,20), (17,0,0,15,3,0,21))),
        Spell(SpellId.QuickHeal, "Quick Heal", SpellGroup.BeaconRites, SpellTarget.SelfOrAlly,
            SpellDelivery.Cast, 14_00, "health restored",
            "Rapidly restore health to yourself or an allied player.",
            "Even a small kindness can turn a hard fight.", "quick-heal", "quick_heal",
            ["healing", "ally", "cast"],
            R((6,0,0,16,5,0.55f,16), (9,0,0,17,5,0.5f,17), (13,0,0,18,5,0.45f,18), (18,0,0,19,5,0.4f,19))),
        Spell(SpellId.Revive, "Revive", SpellGroup.BeaconRites, SpellTarget.DeadAlly,
            SpellDelivery.Cast, 30_00, "health on return",
            "Return a dead allied player at a safe death-site position.",
            "The beacon remembers every traveller it has guided.", "revive", "revive",
            ["healing", "ally", "revive"],
            R((5,0,0,36,45,3.0f,8), (8,0,0,34,42,2.8f,9), (12,0,0,32,39,2.6f,10), (16,0,0,30,36,2.4f,11))),
        Spell(SpellId.HolyShield, "Holy Shield", SpellGroup.BeaconRites, SpellTarget.SelfOrAlly,
            SpellDelivery.Instant, 20_00, "damage absorbed",
            "Wrap yourself or an ally in a barrier that absorbs incoming damage.",
            "Hold fast behind a promise made visible.", "holy-shield", "holy_shield",
            ["shield", "ally", "effect"],
            R((6,0,8,22,14,0,14), (10,0,9,23,14,0,15), (15,0,10,24,14,0,16), (21,0,11,25,14,0,17))),
        Spell(SpellId.Root, "Root", SpellGroup.BeaconRites, SpellTarget.Hostile,
            SpellDelivery.Instant, 16_00, "immobilize seconds",
            "Bind one hostile in place until the effect ends or enough damage breaks it.",
            "The oldest roads know how to hold a trespasser.", "root", "root",
            ["control", "root", "direct"],
            R((0,4,3,18,12,0,16), (0,5,3.5f,19,12,0,17), (0,6,4,20,12,0,18), (0,7,4.5f,21,12,0,19))),

        Spell(SpellId.SummonBones, "Summon Bones", SpellGroup.Gravecalling, SpellTarget.Ground,
            SpellDelivery.Summon, 22_00, "nimble servant",
            "Call an owned skeleton servant that obeys the shared pet commands.",
            "Old bones still remember how to stand watch.", "summon-bones", "summon_bones",
            ["summon", "pet", "damage"],
            R((20,4,0,28,12,1.4f,8), (27,6,0,29,12,1.3f,8), (35,8,0,30,12,1.2f,8), (44,11,0,31,12,1.1f,8))),
        Spell(SpellId.AnimateZombie, "Animate Zombie", SpellGroup.Gravecalling, SpellTarget.Ground,
            SpellDelivery.Summon, 24_00, "durable servant",
            "Raise an owned zombie guard with rank-scaled health and toughness.",
            "The quiet earth lends one last pair of hands.", "animate-zombie", "animate_zombie",
            ["summon", "pet", "guard"],
            R((28,3,0,30,14,1.6f,8), (38,5,0,31,14,1.5f,8), (50,7,0,32,14,1.4f,8), (64,9,0,33,14,1.3f,8))),
        Spell(SpellId.Fear, "Fear", SpellGroup.Gravecalling, SpellTarget.Hostile,
            SpellDelivery.Instant, 17_00, "forced retreat seconds",
            "Drive one hostile away and suspend its ordinary attack for a short time.",
            "A remembered grave can be louder than a battle cry.", "fear", "fear",
            ["control", "fear", "direct"],
            R((0,0,3,20,15,0,15), (0,0,4,21,15,0,16), (0,0,5,22,15,0,17), (0,0,6,23,15,0,18))),
        Spell(SpellId.DrawLifeforce, "Draw Lifeforce", SpellGroup.Gravecalling, SpellTarget.Hostile,
            SpellDelivery.Channel, 25_00, "damage and healing per tick",
            "Channel a life drain; each settled damage tick restores your health.",
            "Life travels every road, even the road unwillingly taken.", "draw-lifeforce", "draw_lifeforce",
            ["damage", "healing", "channel", "drain"],
            R((2,2,3,24,10,0.35f,13), (3,2,3,25,10,0.3f,14), (4,3,3,26,10,0.25f,15), (5,4,3,27,10,0.2f,16))),
        Spell(SpellId.Leech, "Leech", SpellGroup.Gravecalling, SpellTarget.Hostile,
            SpellDelivery.Instant, 18_00, "damage and healing per tick",
            "Afflict one hostile with a life-draining damage-over-time effect.",
            "A patient shadow drinks deepest.", "leech", "leech",
            ["damage", "healing", "effect", "drain"],
            R((2,1,6,19,10,0,15), (3,2,7,20,10,0,16), (4,3,8,21,10,0,17), (5,4,9,22,10,0,18))),

        Spell(SpellId.LightningStreak, "Lightning Streak", SpellGroup.Tidecalling, SpellTarget.Hostile,
            SpellDelivery.Instant, 18_00, "storm damage",
            "Strike one hostile instantly with a bright streak of lightning.",
            "The storm chooses the shortest road.", "lightning-streak", "lightning_streak",
            ["damage", "direct", "lightning"],
            R((6,0,0,15,5,0,18), (10,0,0,16,5,0,19), (15,0,0,17,5,0,20), (21,0,0,18,5,0,21))),
        Spell(SpellId.Ignite, "Ignite", SpellGroup.Tidecalling, SpellTarget.Hostile,
            SpellDelivery.Instant, 19_00, "impact and burning damage",
            "Scorch one hostile instantly and apply the shared burning effect.",
            "A spark is merely a fire that has not decided yet.", "ignite", "ignite",
            ["damage", "fire", "burning", "effect"],
            R((3,2,4,18,9,0,15), (5,2,5,19,9,0,16), (7,3,6,20,9,0,17), (10,4,7,21,9,0,18))),
        Spell(SpellId.TreeOfLife, "Tree of Life", SpellGroup.Tidecalling, SpellTarget.SelfOrAlly,
            SpellDelivery.Instant, 21_00, "healing per tick",
            "Sustain healing over time on yourself or an allied player.",
            "Green things endure by sharing what the rain gives.", "tree-of-life", "tree_of_life",
            ["healing", "ally", "effect"],
            R((2,0,8,21,13,0,15), (3,0,9,22,13,0,16), (4,0,10,23,13,0,17), (5,0,11,24,13,0,18))),
        Spell(SpellId.SpiritWolf, "Spirit Wolf", SpellGroup.Tidecalling, SpellTarget.Ground,
            SpellDelivery.Summon, 23_00, "swift servant",
            "Call a black-and-grey spirit wolf with bright blue eyes.",
            "Some trails are easier with moonlit paws beside you.", "spirit-wolf", "spirit_wolf",
            ["summon", "pet", "pursuit"],
            R((22,4,0,29,12,1.2f,8), (30,6,0,30,12,1.1f,8), (39,9,0,31,12,1.0f,8), (49,12,0,32,12,0.9f,8))),

        Spell(SpellId.IceShock, "Ice Shock", SpellGroup.Arcanistry, SpellTarget.Hostile,
            SpellDelivery.Instant, 17_00, "frost damage",
            "Hit one hostile instantly with concentrated cold.",
            "Winter needs no arrow to find exposed skin.", "ice-shock", "ice_shock",
            ["damage", "direct", "ice"],
            R((5,0,0,14,4,0,17), (9,0,0,15,4,0,18), (13,0,0,16,4,0,19), (18,0,0,17,4,0,20))),
        Spell(SpellId.FireBolt, "Fire Bolt", SpellGroup.Arcanistry, SpellTarget.Hostile,
            SpellDelivery.Projectile, 20_00, "projectile damage",
            "Launch a fiery bolt through the authoritative projectile pool.",
            "Give the flame a direction and let it argue the rest.", "fire-bolt", "fire_bolt",
            ["damage", "fire", "projectile"],
            R((7,0,0,16,4,0.45f,22), (11,0,0,17,4,0.4f,23), (16,0,0,18,4,0.35f,24), (22,0,0,19,4,0.3f,25))),
        Spell(SpellId.GatewayRift, "Gateway Rift", SpellGroup.Arcanistry, SpellTarget.Ground,
            SpellDelivery.Portal, 32_00, "portal lifetime seconds",
            "Open a temporary rift that returns each entrant to their own safe bind.",
            "Home is a place the world can be persuaded to remember.", "gateway-rift", "gateway_rift",
            ["utility", "portal", "travel"],
            R((0,0,18,38,60,2.2f,7), (0,0,22,37,57,2.0f,8), (0,0,26,36,54,1.8f,9), (0,0,30,35,51,1.6f,10))),
        Spell(SpellId.Snare, "Snare", SpellGroup.Arcanistry, SpellTarget.Hostile,
            SpellDelivery.Instant, 16_00, "movement remaining percent",
            "Slow one hostile without completely rooting it.",
            "A road can be made long without moving its end.", "snare", "snare",
            ["control", "slow", "effect"],
            R((0,65,4,16,10,0,17), (0,58,5,17,10,0,18), (0,51,6,18,10,0,19), (0,44,7,19,10,0,20))),
        Spell(SpellId.EarthElemental, "Earth Elemental", SpellGroup.Arcanistry, SpellTarget.Ground,
            SpellDelivery.Summon, 25_00, "stone defender",
            "Call a compact stone defender assembled at half player height.",
            "A small mountain is still a mountain when it stands between you and danger.", "earth-elemental", "earth_elemental",
            ["summon", "pet", "defender", "earth"],
            R((32,3,0,31,15,1.7f,8), (44,5,0,32,15,1.6f,8), (58,7,0,33,15,1.5f,8), (74,10,0,34,15,1.4f,8))),
    ];

    private static readonly Dictionary<string, SpellDefinition> ByStable =
        Definitions.ToDictionary(one => one.StableName, StringComparer.Ordinal);

    public static IReadOnlyList<SpellDefinition> All => Definitions;

    public static SpellDefinition ById(SpellId id)
    {
        var index = (int)id;
        if ((uint)index >= (uint)Definitions.Length || Definitions[index].Id != id)
            throw new ArgumentOutOfRangeException(nameof(id), id, "not a catalogue spell");
        return Definitions[index];
    }

    public static bool TryByStableName(string name, out SpellDefinition definition) =>
        ByStable.TryGetValue(name, out definition!);

    public static string Stable(string displayName) =>
        displayName.Trim().ToLowerInvariant().Replace(' ', '_');

    public static IReadOnlyList<string> Faults()
    {
        var faults = new List<string>();
        if (Definitions.Length != 19) faults.Add($"spell catalogue has {Definitions.Length} rows, not 19");
        if (Definitions.GroupBy(one => one.Group).ToDictionary(g => g.Key, g => g.Count()) is var groups
            && (groups.GetValueOrDefault(SpellGroup.BeaconRites) != 5
                || groups.GetValueOrDefault(SpellGroup.Gravecalling) != 5
                || groups.GetValueOrDefault(SpellGroup.Tidecalling) != 4
                || groups.GetValueOrDefault(SpellGroup.Arcanistry) != 5))
            faults.Add("spell groups are not the locked 5/5/4/5 catalogue");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spell in Definitions)
        {
            if (!names.Add(spell.StableName)) faults.Add($"duplicate stable spell name {spell.StableName}");
            if (spell.StableName != Stable(spell.DisplayName)) faults.Add($"{spell.DisplayName} has an unstable name");
            if (spell.Price <= 0) faults.Add($"{spell.DisplayName} has no positive price");
            if (spell.Ranks.Length != 4) faults.Add($"{spell.DisplayName} does not have four ranks");
            if (string.IsNullOrWhiteSpace(spell.Description) || spell.Description.Contains("TODO", StringComparison.OrdinalIgnoreCase))
                faults.Add($"{spell.DisplayName} has placeholder writing");
            if ((SpellParticleId)spell.Id != spell.Particle) faults.Add($"{spell.DisplayName} particle identity drifted");
            for (var rank = 0; rank < spell.Ranks.Length; rank++)
            {
                var value = spell.Ranks[rank];
                if (value.Focus <= 0 || value.Cooldown < 0f || value.CastTime < 0f || value.Range <= 0f)
                    faults.Add($"{spell.DisplayName} Rank {rank + 1} has invalid timing/cost/range");
                if (rank == 0) continue;
                var before = spell.Ranks[rank - 1];
                if (value.Primary < before.Primary) faults.Add($"{spell.DisplayName} primary value falls at Rank {rank + 1}");
                if (spell.Id != SpellId.Snare && value.Secondary < before.Secondary)
                    faults.Add($"{spell.DisplayName} secondary value falls at Rank {rank + 1}");
            }
        }
        return faults;
    }

    private static SpellDefinition Spell(
        SpellId id, string name, SpellGroup group, SpellTarget target, SpellDelivery delivery,
        long price, string mechanic, string description, string flavour, string slug, string audio,
        string[] tags, SpellRank[] ranks) =>
        new(id, Stable(name), name, group, target, delivery, price, mechanic, description, flavour,
            slug, $"spell_{Stable(name)}", $"magic/spell/{audio}", tags, ranks);

    private static SpellRank[] R(params (int P, int S, float D, int F, float C, float T, float R)[] values) =>
        [.. values.Select(one => new SpellRank(one.P, one.S, one.D, one.F, one.C, one.T, one.R))];
}
