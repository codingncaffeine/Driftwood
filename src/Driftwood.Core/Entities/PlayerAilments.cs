namespace Driftwood.Core.Entities;

/// <summary>What the dark can leave on a player besides a wound.</summary>
public enum Ailment : byte
{
    /// <summary>Half a heart now and then, and never the last one.</summary>
    Poison,

    /// <summary>Slower on the feet.</summary>
    Slowness,

    /// <summary>A weaker blow.</summary>
    Weakness,

    /// <summary>
    /// The charred skeleton's blow (2026-10-25): the reference's wither. Like poison, but it does
    /// not stop at the last half heart.
    /// </summary>
    Withering,

    // ── What a potion can do (2026-10-25). The good ones live in the same table as the bad: one
    //    clock each, one rule for "is it on", and a strength beside it. ──
    Swiftness,
    Strength,
    Regeneration,
    FireResistance,
    NightVision,
    WaterBreathing,

    /// <summary>Instant: a potion of it heals at once, and is never on a clock.</summary>
    Healing,

    /// <summary>Instant: a potion of it hurts at once.</summary>
    Harming,

    /// <summary>
    /// The elder guardian's curse (2026-10-25): the reference's mining fatigue at its third level,
    /// under which rock barely gives at all — a Deepwatch is not quarried while its elders live.
    /// </summary>
    MiningFatigue,
}

/// <summary>What a shot carries besides its damage. Rides on the projectile from loosing to landing.</summary>
public enum ShotTag : byte
{
    None,

    /// <summary>The stray's arrow.</summary>
    Slowing,

    /// <summary>The bogged's arrow.</summary>
    Poisoned,

    /// <summary>The witch's flasks, one per brew.</summary>
    Harming,
    PoisonFlask,
    SlownessFlask,
    WeaknessFlask,

    /// <summary>A thrown splash potion; the shot's damage carries which, as its place in the potion table.</summary>
    Potion,
}

/// <summary>
/// The player's ailments: how long each has left, and what they do while they last.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Its own table rather than the spell effects', on purpose.</b> The spell service is
/// built round a caster, a rank and a dispel family, and the magic audit holds every row of it to a
/// spell. A cave spider's bite is none of those things; folding it in would make the magic check
/// answer for the dark.</para>
/// <para>The figures are the reference's: poison ticks half a heart every second and a quarter and
/// stops at the last half heart; slowness takes fifteen percent off the feet; weakness takes three
/// off a blow. A fresh dose never shortens one already running.</para>
/// </remarks>
public sealed class PlayerAilments
{
    public const float PoisonEvery = 1.25f;
    public const float SlownessScale = 0.85f;
    public const int WeaknessPenalty = 3;

    private readonly float[] _left = new float[Enum.GetValues<Ailment>().Length];
    private readonly int[] _level = new int[Enum.GetValues<Ailment>().Length];
    private float _mendTick;
    private float _poisonTick;
    private float _witherTick;

    /// <summary>Half hearts the last <see cref="Step"/> withered away — apart from what it returned.</summary>
    public int Withered { get; private set; }

    /// <summary>A half heart this often while withering: the reference's two seconds.</summary>
    public const float WitherEvery = 2f;

    public bool Has(Ailment ailment) => _left[(int)ailment] > 0f;

    public float Left(Ailment ailment) => _left[(int)ailment];

    /// <summary>How strong an effect is: 1, or 2 for a strengthened potion. Zero when it is off.</summary>
    public int Level(Ailment ailment) => Has(ailment) ? Math.Max(1, _level[(int)ailment]) : 0;

    public void Apply(Ailment ailment, float seconds, int level = 1)
    {
        if (seconds <= 0f || !float.IsFinite(seconds)) return;
        if (ailment is Ailment.Healing or Ailment.Harming) return;
        _level[(int)ailment] = Has(ailment) ? Math.Max(_level[(int)ailment], level) : level;
        ref var left = ref _left[(int)ailment];
        if (left <= 0f && ailment == Ailment.Poison) _poisonTick = 0f;
        if (left <= 0f && ailment == Ailment.Withering) _witherTick = 0f;
        left = MathF.Max(left, seconds);
    }

    /// <summary>One of them gone — what honey does for poison (2026-10-26).</summary>
    public void Cure(Ailment ailment)
    {
        _left[(int)ailment] = 0f;
        _level[(int)ailment] = 0;
    }

    /// <summary>All of it gone — a death, or a cure.</summary>
    public void Clear()
    {
        Array.Clear(_left);
        Array.Clear(_level);
        _poisonTick = 0f;
        _mendTick = 0f;
        _witherTick = 0f;
    }

    /// <summary>What the feet are worth: slower by slowness, a fifth faster a level of swiftness.</summary>
    public float SpeedScale =>
        (Has(Ailment.Slowness) ? MathF.Pow(SlownessScale, Level(Ailment.Slowness)) : 1f)
        * (1f + 0.2f * Level(Ailment.Swiftness));

    /// <summary>What is taken off a blow while weakened, net of the reference's three a level strength adds.</summary>
    /// <summary>What digging is worth while cursed: the reference's 0.3 cubed, then again — its 0.0027.</summary>
    public float DigScale => Has(Ailment.MiningFatigue) ? 0.0027f : 1f;

    public int BlowPenalty => (Has(Ailment.Weakness) ? WeaknessPenalty : 0) - 3 * Level(Ailment.Strength);

    /// <summary>Half hearts the last <see cref="Step"/> mended by regeneration.</summary>
    public int Mended { get; private set; }

    /// <summary>
    /// Runs the clocks and says how many half hearts the poison takes this step.
    /// </summary>
    /// <param name="health">The player's health now, in half hearts. Poison never takes the last one.</param>
    public int Step(float dt, int health)
    {
        var taken = 0;

        if (Has(Ailment.Poison))
        {
            _poisonTick += dt;
            while (_poisonTick >= PoisonEvery)
            {
                _poisonTick -= PoisonEvery;
                if (health - taken > 1) taken++;
            }
        }

        // ⚠ Withering is counted apart from poison and handed out as a blow, not as poison, because
        // poison's own rule is never the last half heart and the wither's whole difference is that
        // it takes it.
        Withered = 0;
        if (Has(Ailment.Withering))
        {
            _witherTick += dt;
            while (_witherTick >= WitherEvery)
            {
                _witherTick -= WitherEvery;
                Withered++;
            }
        }

        // Regeneration: the reference's half heart every two and a half seconds, twice as often at II.
        Mended = 0;
        if (Has(Ailment.Regeneration))
        {
            _mendTick += dt;
            var every = Level(Ailment.Regeneration) >= 2 ? 1.25f : 2.5f;
            while (_mendTick >= every)
            {
                _mendTick -= every;
                Mended++;
            }
        }

        for (var i = 0; i < _left.Length; i++) _left[i] = MathF.Max(0f, _left[i] - dt);
        return taken;
    }
}
