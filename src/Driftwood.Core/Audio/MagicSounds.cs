using Driftwood.Core.Magic;

namespace Driftwood.Core.Audio;

/// <summary>
/// Stable semantic audio names for progression, the spell lifecycle and commanded companions.
/// A resource pack may replace any path; Driftwood synthesizes a compact original fallback for
/// every one, so a clean install never turns an entire magic system silent.
/// </summary>
public static class MagicSounds
{
    public const string XpGain = "progression/xp_gain";
    public const string LevelUp = "progression/level_up";
    public const string RankUp = "progression/spell_rank_up";
    public const string CoinPickup = "wallet/coin_pickup";
    public const string Purchase = "lorekeeper/purchase";
    public const string PurchaseRefused = "lorekeeper/refused";
    public const string Learn = "spellbook/learn";
    public const string Prepare = "spellbook/prepare";
    public const string Unprepare = "spellbook/unprepare";

    public const string CastStart = "magic/cast_start";
    public const string CastRelease = "magic/cast_release";
    public const string ChannelLoop = "magic/channel_loop";
    public const string Interrupted = "magic/interrupted";
    public const string Invalid = "magic/invalid";
    public const string Impact = "magic/impact";
    public const string EffectEnd = "magic/effect_end";
    public const string PortalOpen = "magic/portal_open";
    public const string PortalLoop = "magic/portal_loop";
    public const string PortalEnter = "magic/portal_enter";
    public const string PortalClose = "magic/portal_close";
    public const string Revive = "magic/revive";
    public const string ReviveRefused = "magic/revive_refused";
    public const string Summon = "magic/summon";
    public const string SummonReplace = "magic/summon_replace";
    public const string SummonDepart = "magic/summon_depart";

    public const string CommandAttack = "pet/command_attack";
    public const string CommandGuard = "pet/command_guard";
    public const string CommandFollow = "pet/command_follow";
    public const string CommandStop = "pet/command_stop";
    public const string CommandGoAway = "pet/command_go_away";
    public const string PetAttack = "pet/attack";
    public const string PetHurt = "pet/hurt";
    public const string PetLowHealth = "pet/low_health";
    public const string PetDeath = "pet/death";

    private static readonly string[] Shared =
    [
        XpGain, LevelUp, RankUp, CoinPickup, Purchase, PurchaseRefused, Learn, Prepare, Unprepare,
        CastStart, CastRelease, ChannelLoop, Interrupted, Invalid, Impact, EffectEnd,
        PortalOpen, PortalLoop, PortalEnter, PortalClose, Revive, ReviveRefused,
        Summon, SummonReplace, SummonDepart,
        CommandAttack, CommandGuard, CommandFollow, CommandStop, CommandGoAway,
        PetAttack, PetHurt, PetLowHealth, PetDeath,
    ];

    private static readonly string[] SpellIdentities =
        [.. SpellCatalogue.All.Select(one => one.AudioKey)];

    private static readonly string[] PetIdentities =
        [.. Enum.GetValues<CompanionKind>().SelectMany(kind => new[]
        {
            PetIdentity(kind), PetMovement(kind), PetIdle(kind), PetAttackFor(kind),
            PetHurtFor(kind), PetLowHealthFor(kind), PetDeathFor(kind),
        })];

    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(Shared.Concat(SpellIdentities).Concat(PetIdentities),
            StringComparer.OrdinalIgnoreCase);

    public static string SpellIdentity(SpellId spell) => SpellCatalogue.ById(spell).AudioKey;

    public static string PetIdentity(CompanionKind kind) =>
        $"pet/{kind.ToString().ToLowerInvariant()}";

    public static string PetMovement(CompanionKind kind) => PetAction(kind, "movement");
    public static string PetIdle(CompanionKind kind) => PetAction(kind, "idle");
    public static string PetAttackFor(CompanionKind kind) => PetAction(kind, "attack");
    public static string PetHurtFor(CompanionKind kind) => PetAction(kind, "hurt");
    public static string PetLowHealthFor(CompanionKind kind) => PetAction(kind, "low_health");
    public static string PetDeathFor(CompanionKind kind) => PetAction(kind, "death");

    private static string PetAction(CompanionKind kind, string action) =>
        $"{PetIdentity(kind)}/{action}";

    public static string Command(CompanionCommand command) => command switch
    {
        CompanionCommand.Attack => CommandAttack,
        CompanionCommand.Guard => CommandGuard,
        CompanionCommand.Follow => CommandFollow,
        CompanionCommand.Stop => CommandStop,
        _ => CommandGoAway,
    };

    public static bool Loops(string name) =>
        name.Equals(ChannelLoop, StringComparison.OrdinalIgnoreCase)
        || name.Equals(PortalLoop, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Tiny deterministic sound design for the redistributable fallback. This is synthesis, not a
/// replacement for curated recordings: it gives every event a coherent audible identity while
/// locally supplied or installed packs can sparsely override the same semantic paths.
/// </summary>
internal static class MagicSoundSynthesis
{
    private const int Rate = 24_000;

    public static WavClip Create(string key)
    {
        var loop = MagicSounds.Loops(key);
        var seconds = loop ? 1f
            : key.Contains("portal", StringComparison.OrdinalIgnoreCase) ? 0.95f
            : key.Contains("summon", StringComparison.OrdinalIgnoreCase) ? 0.72f
            : key.Contains("level_up", StringComparison.OrdinalIgnoreCase)
              || key.Contains("rank_up", StringComparison.OrdinalIgnoreCase) ? 0.8f
            : key.Contains("spell/", StringComparison.OrdinalIgnoreCase) ? 0.52f
            : key.Contains("impact", StringComparison.OrdinalIgnoreCase) ? 0.28f
            : 0.34f;
        var samples = new short[Math.Max(1, (int)(Rate * seconds))];
        var seed = StableHash(key);
        var family = Family(key);
        var baseFrequency = family switch
        {
            0 => 520f + seed % 130, // Beacon: clear bell-like upper mids.
            1 => 105f + seed % 55,  // Grave: low, breathy and uneasy.
            2 => 260f + seed % 170, // Tide: lively elemental motion.
            _ => 330f + seed % 210, // Arcane: glassy rising motion.
        };
        var state = seed | 1u;
        var filteredNoise = 0f;

        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (float)Rate;
            var p = i / (float)Math.Max(1, samples.Length - 1);
            var envelope = loop ? 0.72f : Envelope(p, key);
            var sweep = Sweep(key, p);
            var frequency = baseFrequency * sweep;
            var phase = MathF.Tau * frequency * t;

            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            var noise = ((state & 0xffff) / 32767.5f - 1f);
            filteredNoise += (noise - filteredNoise) * (family == 1 ? 0.035f : 0.11f);

            var tone = MathF.Sin(phase)
                       + 0.34f * MathF.Sin(phase * 2.01f + 0.7f)
                       + 0.16f * MathF.Sin(phase * 3.99f + 1.4f);
            var shimmer = MathF.Sin(MathF.Tau * (frequency * 2.5f) * t + MathF.Sin(t * 9f)) * 0.18f;
            var texture = family switch
            {
                0 => tone * 0.72f + shimmer,
                1 => tone * 0.48f + filteredNoise * 0.52f,
                2 => tone * 0.56f + noise * (key.Contains("lightning", StringComparison.OrdinalIgnoreCase) ? 0.5f : 0.18f),
                _ => tone * 0.63f + shimmer * 0.8f + filteredNoise * 0.12f,
            };

            // Integer-Hz loop voices meet at the one-second seam. Keeping their waveform simple
            // makes a held channel or portal continuous instead of producing a click every lap.
            if (loop)
            {
                var loopHz = family == 1 ? 120f : 240f + family * 60f;
                texture = 0.62f * MathF.Sin(MathF.Tau * loopHz * t)
                          + 0.22f * MathF.Sin(MathF.Tau * loopHz * 2f * t + 0.4f)
                          + 0.1f * MathF.Sin(MathF.Tau * 3f * t);
            }

            var value = Math.Clamp(texture * envelope * 0.42f, -0.92f, 0.92f);
            samples[i] = (short)MathF.Round(value * short.MaxValue);
        }

        return new WavClip(samples, 1, Rate);
    }

    private static float Envelope(float p, string key)
    {
        var attack = key.Contains("impact", StringComparison.OrdinalIgnoreCase)
                     || key.Contains("invalid", StringComparison.OrdinalIgnoreCase) ? 0.015f : 0.09f;
        var in_ = Math.Clamp(p / attack, 0f, 1f);
        var out_ = Math.Clamp((1f - p) / 0.22f, 0f, 1f);
        return MathF.Sin(in_ * MathF.PI * 0.5f) * MathF.Sin(out_ * MathF.PI * 0.5f);
    }

    private static float Sweep(string key, float p)
    {
        if (key.Contains("refused", StringComparison.OrdinalIgnoreCase)
            || key.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || key.Contains("interrupted", StringComparison.OrdinalIgnoreCase)) return 1.15f - p * 0.55f;
        if (key.Contains("level_up", StringComparison.OrdinalIgnoreCase)
            || key.Contains("rank_up", StringComparison.OrdinalIgnoreCase)
            || key.Contains("revive", StringComparison.OrdinalIgnoreCase)) return 0.72f + p * 0.9f;
        if (key.Contains("impact", StringComparison.OrdinalIgnoreCase)
            || key.Contains("attack", StringComparison.OrdinalIgnoreCase)) return 1.45f - p * 0.65f;
        return 0.85f + p * 0.35f;
    }

    private static int Family(string key)
    {
        foreach (var spell in SpellCatalogue.All)
            if (key.Equals(spell.AudioKey, StringComparison.OrdinalIgnoreCase)) return (int)spell.Group;
        if (key.Contains("holy", StringComparison.OrdinalIgnoreCase)
            || key.Contains("heal", StringComparison.OrdinalIgnoreCase)
            || key.Contains("revive", StringComparison.OrdinalIgnoreCase)) return 0;
        if (key.Contains("bone", StringComparison.OrdinalIgnoreCase)
            || key.Contains("zombie", StringComparison.OrdinalIgnoreCase)
            || key.Contains("lifeforce", StringComparison.OrdinalIgnoreCase)) return 1;
        if (key.Contains("wolf", StringComparison.OrdinalIgnoreCase)
            || key.Contains("lightning", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    private static uint StableHash(string value)
    {
        var hash = 2166136261u;
        foreach (var ch in value) { hash ^= char.ToLowerInvariant(ch); hash *= 16777619u; }
        return hash;
    }
}
