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

    private readonly float[] _left = new float[4];
    private float _poisonTick;
    private float _witherTick;

    /// <summary>Half hearts the last <see cref="Step"/> withered away — apart from what it returned.</summary>
    public int Withered { get; private set; }

    /// <summary>A half heart this often while withering: the reference's two seconds.</summary>
    public const float WitherEvery = 2f;

    public bool Has(Ailment ailment) => _left[(int)ailment] > 0f;

    public float Left(Ailment ailment) => _left[(int)ailment];

    public void Apply(Ailment ailment, float seconds)
    {
        if (seconds <= 0f || !float.IsFinite(seconds)) return;
        ref var left = ref _left[(int)ailment];
        if (left <= 0f && ailment == Ailment.Poison) _poisonTick = 0f;
        if (left <= 0f && ailment == Ailment.Withering) _witherTick = 0f;
        left = MathF.Max(left, seconds);
    }

    /// <summary>All of it gone — a death, or a cure.</summary>
    public void Clear()
    {
        Array.Clear(_left);
        _poisonTick = 0f;
        _witherTick = 0f;
    }

    /// <summary>What the feet are worth while slowed.</summary>
    public float SpeedScale => Has(Ailment.Slowness) ? SlownessScale : 1f;

    /// <summary>What is taken off a blow while weakened.</summary>
    public int BlowPenalty => Has(Ailment.Weakness) ? WeaknessPenalty : 0;

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

        for (var i = 0; i < _left.Length; i++) _left[i] = MathF.Max(0f, _left[i] - dt);
        return taken;
    }
}
