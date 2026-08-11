using System.Numerics;
using Driftwood.Core.Blocks;

namespace Driftwood.Core.Particles;

/// <summary>
/// Frame-rate-independent cadence for a sustained emitter. The effect owns one of these beside its
/// authoritative lifetime and asks how many particles to emit each simulation step.
/// </summary>
public struct ParticleCadence
{
    private float _carried;

    public int Take(float particlesPerSecond, float dt, int perStepCap = 64)
    {
        if (particlesPerSecond <= 0f || dt <= 0f || perStepCap <= 0) return 0;

        _carried += particlesPerSecond * dt;
        var count = Math.Min((int)_carried, perStepCap);
        _carried -= count;
        return count;
    }

    public void Reset() => _carried = 0f;
}

/// <summary>Stable phase names sent by an action/effect; never renderer method names.</summary>
public enum SpellParticlePhase
{
    Cast,
    Travel,
    Impact,
    Sustain,
    End,
}

/// <summary>The exact initial P10.5 spell catalogue, as semantic VFX identities.</summary>
public enum SpellParticleId
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

public enum SpellParticleGroup
{
    BeaconRites,
    Gravecalling,
    Tidecalling,
    Arcanistry,
}

/// <summary>Documentation and registry bridge for one spell's stable particle identity.</summary>
public readonly record struct SpellParticleDefinition(
    SpellParticleId Id, string StableName, SpellParticleGroup Group);

/// <summary>
/// The complete semantic spell-particle catalogue. It provides a visual grammar now, before spells
/// exist, so M4 can send <c>spell + phase + positions + rank</c> rather than grow nineteen renderer
/// branches in the client host.
/// </summary>
public static class SpellParticleEffects
{
    private static readonly SpellParticleDefinition[] AllDefinitions =
    [
        new(SpellParticleId.HolyMight, "Holy Might", SpellParticleGroup.BeaconRites),
        new(SpellParticleId.QuickHeal, "Quick Heal", SpellParticleGroup.BeaconRites),
        new(SpellParticleId.Revive, "Revive", SpellParticleGroup.BeaconRites),
        new(SpellParticleId.HolyShield, "Holy Shield", SpellParticleGroup.BeaconRites),
        new(SpellParticleId.Root, "Root", SpellParticleGroup.BeaconRites),

        new(SpellParticleId.SummonBones, "Summon Bones", SpellParticleGroup.Gravecalling),
        new(SpellParticleId.AnimateZombie, "Animate Zombie", SpellParticleGroup.Gravecalling),
        new(SpellParticleId.Fear, "Fear", SpellParticleGroup.Gravecalling),
        new(SpellParticleId.DrawLifeforce, "Draw Lifeforce", SpellParticleGroup.Gravecalling),
        new(SpellParticleId.Leech, "Leech", SpellParticleGroup.Gravecalling),

        new(SpellParticleId.LightningStreak, "Lightning Streak", SpellParticleGroup.Tidecalling),
        new(SpellParticleId.Ignite, "Ignite", SpellParticleGroup.Tidecalling),
        new(SpellParticleId.TreeOfLife, "Tree of Life", SpellParticleGroup.Tidecalling),
        new(SpellParticleId.SpiritWolf, "Spirit Wolf", SpellParticleGroup.Tidecalling),

        new(SpellParticleId.IceShock, "Ice Shock", SpellParticleGroup.Arcanistry),
        new(SpellParticleId.FireBolt, "Fire Bolt", SpellParticleGroup.Arcanistry),
        new(SpellParticleId.GatewayRift, "Gateway Rift", SpellParticleGroup.Arcanistry),
        new(SpellParticleId.Snare, "Snare", SpellParticleGroup.Arcanistry),
        new(SpellParticleId.EarthElemental, "Earth Elemental", SpellParticleGroup.Arcanistry),
    ];

    public static ReadOnlySpan<SpellParticleDefinition> Definitions => AllDefinitions;

    public static bool TryFind(string stableName, out SpellParticleId id)
    {
        foreach (var definition in AllDefinitions)
        {
            if (!string.Equals(definition.StableName, stableName, StringComparison.Ordinal)) continue;
            id = definition.Id;
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>
    /// Emits one bounded event. Rank changes emphasis slightly, never particle count by a 4× flood;
    /// mechanics become stronger while the visual identity stays readable and affordable.
    /// </summary>
    public static void Emit(
        ParticleSystem particles,
        SpellParticleId spell,
        SpellParticlePhase phase,
        Vector3 origin,
        Vector3 target,
        int rank = 1)
    {
        var emphasis = 1f + (Math.Clamp(rank, 1, 4) - 1) * 0.10f;
        var extra = Math.Clamp(rank, 1, 4) - 1;

        switch (spell)
        {
            case SpellParticleId.HolyMight:
                Directed(particles, phase, origin, target, Gold(), 7 + extra, emphasis);
                break;
            case SpellParticleId.QuickHeal:
                Heal(particles, phase, target, 5 + extra, emphasis, GreenGold());
                break;
            case SpellParticleId.Revive:
                if (phase is SpellParticlePhase.Cast or SpellParticlePhase.Impact)
                {
                    particles.Ring(Rune(Gold(), 0.12f * emphasis, 1.05f), target, Vector3.UnitY, 14 + extra * 2, 0.75f);
                    particles.Column(Heart(GreenGold(), 0.11f * emphasis, 1.35f), target, 7 + extra, 0.48f, 1.7f, 0.42f);
                    particles.Column(Glow(Gold(), 0.08f, 1.0f), target, 10 + extra, 0.36f, 1.9f, 0.6f);
                }
                break;
            case SpellParticleId.HolyShield:
                if (phase is SpellParticlePhase.Cast or SpellParticlePhase.Sustain or SpellParticlePhase.Impact)
                {
                    particles.Ring(Rune(Gold(), 0.10f * emphasis, 0.8f), target + Vector3.UnitY * 0.85f, Vector3.UnitY, 12 + extra, 0.62f);
                    particles.Ring(Rune(Gold(), 0.09f * emphasis, 0.8f), target + Vector3.UnitY * 0.85f, Vector3.UnitX, 10 + extra, 0.72f);
                }
                break;
            case SpellParticleId.Root:
                Bind(particles, phase, target, RootGreen(), 0.58f, extra, emphasis, hard: true);
                break;

            case SpellParticleId.SummonBones:
                Summon(particles, phase, target, Bone(), Grave(), extra, emphasis);
                break;
            case SpellParticleId.AnimateZombie:
                Summon(particles, phase, target, RotGreen(), Grave(), extra, emphasis);
                break;
            case SpellParticleId.Fear:
                if (phase is SpellParticlePhase.Cast or SpellParticlePhase.Impact or SpellParticlePhase.Sustain)
                {
                    particles.Sphere(Soft(Grave(), 0.11f * emphasis, 0.85f, grow: 0.05f), target + Vector3.UnitY * 1.15f, 9 + extra, 0.8f, 0.1f, 0.25f);
                    particles.Ring(Rune(Grave(), 0.08f, 0.65f), target + Vector3.UnitY * 1.1f, Vector3.UnitY, 8 + extra, 0.42f, 0.18f);
                }
                break;
            case SpellParticleId.DrawLifeforce:
                Drain(particles, phase, origin, target, 11 + extra * 2, emphasis, channel: true);
                break;
            case SpellParticleId.Leech:
                Drain(particles, phase, origin, target, 7 + extra, emphasis, channel: false);
                break;

            case SpellParticleId.LightningStreak:
                Directed(particles, phase, origin, target, Lightning(), 13 + extra * 2, emphasis);
                break;
            case SpellParticleId.Ignite:
                if (phase is SpellParticlePhase.Cast or SpellParticlePhase.Impact or SpellParticlePhase.Sustain)
                {
                    particles.Sphere(Glow(Fire(), 0.09f * emphasis, 0.55f), target + Vector3.UnitY * 0.7f, 8 + extra, 1.1f, 0.05f, 0.3f);
                    particles.Flame(target + Vector3.UnitY * 0.25f, 0.75f * emphasis, StarterBlocks.LayerFlame, 3 + extra);
                }
                break;
            case SpellParticleId.TreeOfLife:
                Heal(particles, phase, target, 6 + extra, emphasis, LifeGreen());
                if (phase is SpellParticlePhase.Cast or SpellParticlePhase.Sustain)
                    particles.Column(Soft(LifeGreen(), 0.07f, 1.15f, grow: -0.015f), target, 8 + extra, 0.52f, 1.55f, 0.32f);
                break;
            case SpellParticleId.SpiritWolf:
                Summon(particles, phase, target, SpiritBlue(), WolfGrey(), extra, emphasis);
                break;

            case SpellParticleId.IceShock:
                if (phase is SpellParticlePhase.Cast or SpellParticlePhase.Impact)
                {
                    particles.Sphere(Spark(Ice(), 0.075f * emphasis, 0.6f, fall: 0.12f), target + Vector3.UnitY * 0.85f, 10 + extra, 1.25f, 0.04f, 0.15f);
                    particles.Ring(Rune(Ice(), 0.075f, 0.55f), target + Vector3.UnitY * 0.15f, Vector3.UnitY, 9 + extra, 0.48f, 0.16f);
                }
                break;
            case SpellParticleId.FireBolt:
                Directed(particles, phase, origin, target, Fire(), 8 + extra, emphasis);
                if (phase == SpellParticlePhase.Impact)
                    particles.Flame(target, 0.65f * emphasis, StarterBlocks.LayerFlame, 4 + extra);
                break;
            case SpellParticleId.GatewayRift:
                if (phase is SpellParticlePhase.Cast or SpellParticlePhase.Sustain or SpellParticlePhase.Impact)
                {
                    var centre = target + Vector3.UnitY;
                    particles.Ring(Rune(Rift(), 0.13f * emphasis, 0.9f), centre, Vector3.UnitX, 18 + extra * 2, 0.92f, 0.03f);
                    particles.Ring(Glow(RiftBlue(), 0.065f, 0.7f), centre, Vector3.UnitX, 12 + extra, 0.72f, -0.08f);
                    particles.Column(Soft(Rift(), 0.06f, 0.8f), target, 5 + extra, 0.55f, 1.9f, 0.22f);
                }
                break;
            case SpellParticleId.Snare:
                Bind(particles, phase, target, Ice(), 0.72f, extra, emphasis, hard: false);
                break;
            case SpellParticleId.EarthElemental:
                Summon(particles, phase, target, StoneLight(), StoneDark(), extra, emphasis);
                break;
        }
    }

    private static void Directed(
        ParticleSystem particles, SpellParticlePhase phase, Vector3 origin, Vector3 target,
        Vector4 colour, int count, float emphasis)
    {
        if (phase == SpellParticlePhase.Cast)
            particles.Ring(Rune(colour, 0.07f * emphasis, 0.45f), origin, Vector3.UnitY, 8, 0.24f, 0.08f);
        else if (phase == SpellParticlePhase.Travel)
            particles.Trail(Glow(colour, 0.055f * emphasis, 0.34f), origin, target, count, 0.025f);
        else if (phase == SpellParticlePhase.Impact)
            particles.Sphere(Spark(colour, 0.075f * emphasis, 0.48f, fall: 0.08f), target, count, 1.25f, 0.03f, 0.2f);
    }

    private static void Heal(
        ParticleSystem particles, SpellParticlePhase phase, Vector3 target,
        int count, float emphasis, Vector4 colour)
    {
        if (phase is not (SpellParticlePhase.Cast or SpellParticlePhase.Impact or SpellParticlePhase.Sustain)) return;
        particles.Column(Heart(colour, 0.09f * emphasis, 0.95f), target, count, 0.42f, 1.35f, 0.35f);
        particles.Ring(Soft(colour, 0.065f, 0.65f), target + Vector3.UnitY * 0.12f, Vector3.UnitY, 8, 0.45f, 0.12f);
    }

    private static void Bind(
        ParticleSystem particles, SpellParticlePhase phase, Vector3 target, Vector4 colour,
        float radius, int extra, float emphasis, bool hard)
    {
        if (phase is not (SpellParticlePhase.Cast or SpellParticlePhase.Impact or SpellParticlePhase.Sustain)) return;
        particles.Ring(Rune(colour, 0.075f * emphasis, 0.75f), target + Vector3.UnitY * 0.08f, Vector3.UnitY, 11 + extra, radius, hard ? -0.08f : 0.04f);
        particles.Column(Soft(colour, 0.055f, 0.65f), target, 4 + extra, radius * 0.7f, hard ? 0.75f : 0.35f, hard ? 0.18f : 0.08f);
    }

    private static void Summon(
        ParticleSystem particles, SpellParticlePhase phase, Vector3 target,
        Vector4 bright, Vector4 dark, int extra, float emphasis)
    {
        if (phase is not (SpellParticlePhase.Cast or SpellParticlePhase.Impact or SpellParticlePhase.End)) return;
        var inward = phase == SpellParticlePhase.End ? 0.24f : -0.22f;
        particles.Ring(Rune(bright, 0.085f * emphasis, 0.8f), target + Vector3.UnitY * 0.08f, Vector3.UnitY, 13 + extra, 0.72f, inward);
        particles.Column(Soft(dark, 0.075f, 0.9f, grow: 0.015f), target, 9 + extra, 0.46f, 1.25f, 0.34f);
        particles.Sphere(Spark(bright, 0.055f, 0.5f, fall: 0.08f), target + Vector3.UnitY * 0.55f, 5 + extra, 0.7f, 0.02f, 0.15f);
    }

    private static void Drain(
        ParticleSystem particles, SpellParticlePhase phase, Vector3 caster, Vector3 target,
        int count, float emphasis, bool channel)
    {
        if (phase is SpellParticlePhase.Cast or SpellParticlePhase.Sustain or SpellParticlePhase.Travel)
        {
            // Direction matters: lifeforce visibly travels from victim to caster.
            particles.Trail(Glow(Blood(), 0.047f * emphasis, channel ? 0.5f : 0.7f), target, caster, count, 0.045f, 0.18f);
            particles.Ring(Soft(Grave(), 0.06f, 0.65f), target, Vector3.UnitY, 7, channel ? 0.34f : 0.46f, -0.08f);
        }

        if (phase is SpellParticlePhase.Impact or SpellParticlePhase.Sustain)
            particles.Column(Heart(Blood(), 0.065f, 0.65f), caster, channel ? 3 : 2, 0.22f, 0.9f, 0.18f);
    }

    private static ParticleRecipe Spark(Vector4 colour, float size, float life, float fall = 0f) =>
        new(StarterBlocks.LayerParticleSpark, ParticleLook.Glow, colour, size, life,
            size * 0.20f, life * 0.15f, fall, 0.65f, -size * 0.05f, 0f, 4.5f);

    private static ParticleRecipe Glow(Vector4 colour, float size, float life) =>
        new(StarterBlocks.LayerParticleSoft, ParticleLook.Glow, colour, size, life,
            size * 0.18f, life * 0.12f, 0f, 0.45f, size * 0.04f, 0.04f, 2.4f);

    private static ParticleRecipe Soft(Vector4 colour, float size, float life, float grow = 0f) =>
        new(StarterBlocks.LayerParticleSoft, ParticleLook.Soft, colour, size, life,
            size * 0.18f, life * 0.12f, 0f, 0.85f, grow, 0.06f, 1.8f);

    private static ParticleRecipe Rune(Vector4 colour, float size, float life) =>
        new(StarterBlocks.LayerParticleRune, ParticleLook.Glow, colour, size, life,
            size * 0.08f, life * 0.08f, 0f, 0.35f, 0f, 0f, 2.2f);

    private static ParticleRecipe Heart(Vector4 colour, float size, float life) =>
        new(StarterBlocks.LayerParticleHeart, ParticleLook.Soft, colour, size, life,
            size * 0.12f, life * 0.10f, -0.015f, 0.75f, -size * 0.035f, 0.05f, 0.7f);

    private static Vector4 C(byte r, byte g, byte b, byte a = 255) =>
        new(r / 255f, g / 255f, b / 255f, a / 255f);

    private static Vector4 Gold() => C(255, 224, 118);
    private static Vector4 GreenGold() => C(220, 242, 156);
    private static Vector4 RootGreen() => C(116, 174, 88);
    private static Vector4 LifeGreen() => C(112, 224, 132);
    private static Vector4 Bone() => C(226, 220, 190);
    private static Vector4 Grave() => C(116, 76, 142);
    private static Vector4 RotGreen() => C(112, 150, 94);
    private static Vector4 Blood() => C(202, 74, 106);
    private static Vector4 Lightning() => C(160, 224, 255);
    private static Vector4 Fire() => C(255, 132, 52);
    private static Vector4 SpiritBlue() => C(108, 180, 246);
    private static Vector4 WolfGrey() => C(126, 138, 154);
    private static Vector4 Ice() => C(146, 226, 248);
    private static Vector4 Rift() => C(164, 104, 230);
    private static Vector4 RiftBlue() => C(90, 172, 240);
    private static Vector4 StoneLight() => C(178, 174, 160);
    private static Vector4 StoneDark() => C(92, 96, 102);
}

/// <summary>
/// Small, material-aware feedback for ordinary verbs. These are punctuation, not fireworks: the
/// block/item/creature remains the thing being read, while particles confirm that work happened.
/// </summary>
public static class InteractionParticleEffects
{
    public static void BlockPlaced(ParticleSystem particles, BlockType type, Vector3 top)
    {
        particles.Puff(type, top, 5, 0.32f);
    }

    public static void Tilled(ParticleSystem particles, BlockType soil, Vector3 surface)
    {
        particles.Puff(soil, surface, 9, 0.55f);
        particles.Spray(Soft(C(138, 104, 72), 0.045f, 0.45f), surface, Vector3.UnitY, 4, 0.35f, 1.1f, 0.22f);
    }

    public static void Planted(ParticleSystem particles, BlockType soil, Vector3 at)
    {
        particles.Puff(soil, at, 4, 0.24f);
        particles.Column(Soft(C(112, 174, 88), 0.045f, 0.55f), at, 3, 0.18f, 0.32f, 0.10f);
    }

    public static void Grew(ParticleSystem particles, Vector3 at, int count = 8)
    {
        particles.Column(Glow(C(126, 218, 112), 0.052f, 0.72f), at, count, 0.38f, 0.95f, 0.24f);
    }

    public static void Harvested(ParticleSystem particles, BlockType type, Vector3 at)
    {
        particles.Puff(type, at, 7, 0.48f);
    }

    public static void Brushed(ParticleSystem particles, BlockType type, int x, int y, int z, int face)
    {
        particles.Chip(type, x, y, z, face, 8);
        var n = Faces.Normals[face];
        particles.Spray(
            Soft(C(164, 156, 132, 205), 0.052f, 0.65f, grow: 0.025f),
            new Vector3(x + 0.5f + n.X * 0.52f, y + 0.5f + n.Y * 0.52f, z + 0.5f + n.Z * 0.52f),
            new Vector3(n.X, n.Y, n.Z), 6, 0.45f, 0.85f, 0.22f);
    }

    public static void WaterSplash(ParticleSystem particles, Vector3 at, int count = 10)
    {
        particles.Sphere(
            new ParticleRecipe(
                StarterBlocks.LayerParticleBubble, ParticleLook.Soft, C(126, 196, 238, 220),
                0.055f, 0.72f, 0.015f, 0.12f, 0.10f, 0.75f, -0.015f, 0.04f, 1.4f),
            at, count, 0.95f, 0.04f, 0.42f);
    }

    public static void LavaSplash(ParticleSystem particles, Vector3 at)
    {
        particles.Flame(at, 0.65f, StarterBlocks.LayerFlame, 5);
        particles.Smoke(at + Vector3.UnitY * 0.18f, 0.35f, StarterBlocks.LayerSmoke, 2, 0.6f);
        particles.Sphere(Spark(C(255, 150, 56), 0.055f, 0.48f, 0.35f), at, 5, 1.0f, 0.02f, 0.3f);
    }

    public static void ItemPickup(ParticleSystem particles, Vector3 at, int stacks)
    {
        var count = Math.Clamp(3 + stacks, 4, 9);
        particles.Ring(Glow(C(246, 220, 132), 0.045f, 0.42f), at, Vector3.UnitY, count, 0.26f, -0.18f);
    }

    public static void CreatureHit(ParticleSystem particles, Vector3 at, Vector3 from)
    {
        var normal = at - from;
        particles.Spray(Spark(C(240, 190, 148), 0.06f, 0.38f, 0.18f), at, normal, 7, 1.25f, 0.75f, 0.08f);
    }

    public static void CreatureHarvest(ParticleSystem particles, Vector3 at)
    {
        particles.Sphere(Soft(C(230, 224, 208), 0.07f, 0.58f), at, 8, 0.65f, 0.08f, 0.3f);
    }

    public static void Affection(ParticleSystem particles, Vector3 at, bool courting)
    {
        var recipe = new ParticleRecipe(
            StarterBlocks.LayerParticleHeart, ParticleLook.Soft, C(242, 98, 122),
            0.085f, 1.0f, 0.015f, 0.15f, -0.02f, 0.7f, -0.012f, 0.08f, 0.9f);
        particles.Column(recipe, at, courting ? 8 : 4, 0.32f, 0.72f, 0.26f);
    }

    public static void MetalWorked(ParticleSystem particles, Vector3 at)
    {
        particles.Spray(Spark(C(255, 198, 92), 0.052f, 0.42f, 0.55f), at + Vector3.UnitY * 0.28f, Vector3.UnitY, 9, 1.8f, 1.4f, 0.28f);
    }

    public static void Composted(ParticleSystem particles, Vector3 at, bool ready)
    {
        var compost = new ParticleRecipe(
            ready ? StarterBlocks.LayerCompostReady : StarterBlocks.LayerCompost,
            ParticleLook.Debris, Vector4.One,
            ready ? 0.06f : 0.045f, ready ? 0.75f : 0.52f,
            0.012f, 0.1f, 0.75f, 1.3f, 0f, 0f, 2f,
            FullTile: false);
        particles.Sphere(
            compost, at + Vector3.UnitY * 0.42f,
            ready ? 8 : 4, ready ? 0.62f : 0.35f, 0.05f, 0.45f);
        if (ready) particles.Column(Soft(C(128, 184, 92), 0.05f, 0.6f), at, 5, 0.25f, 0.7f, 0.18f);
    }

    private static ParticleRecipe Spark(Vector4 colour, float size, float life, float fall = 0f) =>
        new(StarterBlocks.LayerParticleSpark, ParticleLook.Glow, colour, size, life,
            size * 0.18f, life * 0.12f, fall, 0.7f, -size * 0.04f, 0f, 4f);

    private static ParticleRecipe Glow(Vector4 colour, float size, float life) =>
        new(StarterBlocks.LayerParticleSoft, ParticleLook.Glow, colour, size, life,
            size * 0.15f, life * 0.12f, 0f, 0.55f, 0f, 0.03f, 2f);

    private static ParticleRecipe Soft(Vector4 colour, float size, float life, float grow = 0f) =>
        new(StarterBlocks.LayerParticleSoft, ParticleLook.Soft, colour, size, life,
            size * 0.15f, life * 0.12f, 0f, 0.9f, grow, 0.04f, 1.5f);

    private static Vector4 C(byte r, byte g, byte b, byte a = 255) =>
        new(r / 255f, g / 255f, b / 255f, a / 255f);
}
