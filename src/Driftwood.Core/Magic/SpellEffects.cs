namespace Driftwood.Core.Magic;

public enum SpellEffectKind : byte
{
    Burning,
    HealingOverTime,
    Rooted,
    Feared,
    LifeforceLeech,
    Snared,
    HolyShield,
}

public enum EffectTargetKind : byte { Player, Creature, Companion }

public enum EffectDispelFamily : byte { None, Flame, Nature, Grave, Beacon, Control }

public readonly record struct EffectTarget(EffectTargetKind Kind, string Id);

public readonly record struct SpellEffectSnapshot(
    SpellEffectKind Kind,
    EffectTarget Target,
    string SourcePlayerId,
    int Rank,
    int Magnitude,
    float Remaining,
    float TickEvery,
    EffectDispelFamily Dispel,
    int ShieldRemaining);

public enum EffectEventKind : byte { Applied, Refreshed, TickDamage, TickHealing, Absorbed, Ended }

public readonly record struct SpellEffectEvent(
    EffectEventKind Kind,
    SpellEffectKind Effect,
    EffectTarget Target,
    string SourcePlayerId,
    int Amount);

/// <summary>
/// One deterministic status-effect clock for players, creatures and companions. Re-applying an
/// effect refreshes duration and keeps the greater magnitude; drains remain source-scoped so two
/// players can own independent healing settlement against one target.
/// </summary>
public sealed class SpellEffectService
{
    private sealed class Active
    {
        public required SpellEffectKind Kind;
        public required EffectTarget Target;
        public required string Source;
        public required int Rank;
        public required int Magnitude;
        public required float Remaining;
        public required float TickEvery;
        public required EffectDispelFamily Dispel;
        public float Tick;
        public int Shield;
    }

    private readonly List<Active> _active = [];
    private readonly List<SpellEffectEvent> _events = [];

    public int Count => _active.Count;

    public IReadOnlyList<SpellEffectSnapshot> Snapshots =>
        [.. _active.Select(one => new SpellEffectSnapshot(
            one.Kind, one.Target, one.Source, one.Rank, one.Magnitude, one.Remaining,
            one.TickEvery, one.Dispel, one.Shield))];

    public bool Apply(
        SpellEffectKind kind,
        EffectTarget target,
        string sourcePlayerId,
        int rank,
        int magnitude,
        float duration,
        float tickEvery = 1f,
        EffectDispelFamily dispel = EffectDispelFamily.None)
    {
        if (string.IsNullOrWhiteSpace(target.Id) || string.IsNullOrWhiteSpace(sourcePlayerId)
            || magnitude < 0 || duration <= 0f || !float.IsFinite(duration)
            || tickEvery < 0f || !float.IsFinite(tickEvery)) return false;

        // A drain owns its source. Other effects have one deterministic strongest/latest instance.
        var sourceScoped = kind == SpellEffectKind.LifeforceLeech;
        var active = _active.FirstOrDefault(one => one.Kind == kind && one.Target == target
            && (!sourceScoped || one.Source == sourcePlayerId));
        if (active is not null)
        {
            active.Source = sourcePlayerId;
            active.Rank = Math.Max(active.Rank, Math.Clamp(rank, 1, 4));
            active.Magnitude = Math.Max(active.Magnitude, magnitude);
            active.Remaining = Math.Max(active.Remaining, duration);
            active.TickEvery = tickEvery;
            active.Dispel = dispel;
            if (kind == SpellEffectKind.HolyShield) active.Shield = Math.Max(active.Shield, magnitude);
            _events.Add(new(EffectEventKind.Refreshed, kind, target, sourcePlayerId, magnitude));
            return true;
        }

        _active.Add(new Active
        {
            Kind = kind,
            Target = target,
            Source = sourcePlayerId,
            Rank = Math.Clamp(rank, 1, 4),
            Magnitude = magnitude,
            Remaining = duration,
            TickEvery = tickEvery,
            Dispel = dispel,
            Shield = kind == SpellEffectKind.HolyShield ? magnitude : 0,
        });
        _events.Add(new(EffectEventKind.Applied, kind, target, sourcePlayerId, magnitude));
        return true;
    }

    /// <summary>
    /// Keeps an environmental effect alive without manufacturing a refresh event every simulation
    /// step. A spell already owning the shared effect keeps its source and stronger snapshot; this
    /// merely extends the same clock while the environment is still supplying it.
    /// </summary>
    public bool Sustain(
        SpellEffectKind kind,
        EffectTarget target,
        string sourcePlayerId,
        int rank,
        int magnitude,
        float duration,
        float tickEvery = 1f,
        EffectDispelFamily dispel = EffectDispelFamily.None)
    {
        if (string.IsNullOrWhiteSpace(target.Id) || string.IsNullOrWhiteSpace(sourcePlayerId)
            || magnitude < 0 || duration <= 0f || !float.IsFinite(duration)
            || tickEvery < 0f || !float.IsFinite(tickEvery)) return false;

        var sourceScoped = kind == SpellEffectKind.LifeforceLeech;
        var active = _active.FirstOrDefault(one => one.Kind == kind && one.Target == target
            && (!sourceScoped || one.Source == sourcePlayerId));
        if (active is null)
            return Apply(kind, target, sourcePlayerId, rank, magnitude, duration, tickEvery, dispel);

        // The environment may keep supplying fire while an explicit Ignite owns this target. It
        // must not silently shorten that spell's tick cadence, increase its rank, or extend its
        // authored lifetime. When the spell ends, the next sunlight step establishes a new world
        // effect through the same service.
        if (!string.Equals(active.Source, sourcePlayerId, StringComparison.Ordinal)) return true;

        active.Rank = Math.Max(active.Rank, Math.Clamp(rank, 1, 4));
        active.Magnitude = Math.Max(active.Magnitude, magnitude);
        active.Remaining = Math.Max(active.Remaining, duration);
        active.TickEvery = Math.Min(active.TickEvery, tickEvery);
        if (active.Dispel == EffectDispelFamily.None) active.Dispel = dispel;
        return true;
    }

    /// <summary>Returns damage left after the target's one shared holy barrier absorbs it.</summary>
    public int Absorb(EffectTarget target, int damage)
    {
        if (damage <= 0) return 0;
        var shield = _active.FirstOrDefault(one =>
            one.Kind == SpellEffectKind.HolyShield && one.Target == target && one.Shield > 0);
        if (shield is null) return damage;
        var taken = Math.Min(damage, shield.Shield);
        shield.Shield -= taken;
        _events.Add(new(EffectEventKind.Absorbed, shield.Kind, target, shield.Source, taken));
        if (shield.Shield <= 0) shield.Remaining = 0f;
        return damage - taken;
    }

    public bool Rooted(EffectTarget target) =>
        _active.Any(one => one.Target == target && one.Kind == SpellEffectKind.Rooted && one.Remaining > 0f);

    public bool Feared(EffectTarget target) =>
        _active.Any(one => one.Target == target && one.Kind == SpellEffectKind.Feared && one.Remaining > 0f);

    public float MovementMultiplier(EffectTarget target)
    {
        if (Rooted(target)) return 0f;
        var percent = _active.Where(one => one.Target == target && one.Kind == SpellEffectKind.Snared)
            .Select(one => one.Magnitude).DefaultIfEmpty(100).Min();
        return Math.Clamp(percent / 100f, 0.2f, 1f);
    }

    public void BreakRoot(EffectTarget target, int settledDamage)
    {
        if (settledDamage < 4) return;
        foreach (var effect in _active)
            if (effect.Target == target && effect.Kind == SpellEffectKind.Rooted) effect.Remaining = 0f;
    }

    public int Dispel(EffectTarget target, EffectDispelFamily family)
    {
        var removed = 0;
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            if (_active[i].Target != target || _active[i].Dispel != family) continue;
            End(_active[i]);
            _active.RemoveAt(i);
            removed++;
        }
        return removed;
    }

    public void Tick(float dt)
    {
        if (dt <= 0f || !float.IsFinite(dt)) return;
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var effect = _active[i];
            effect.Remaining -= dt;
            if (effect.TickEvery > 0f && Ticks(effect.Kind))
            {
                effect.Tick += dt;
                var guard = 0;
                while (effect.Tick >= effect.TickEvery && guard++ < 16)
                {
                    effect.Tick -= effect.TickEvery;
                    var kind = effect.Kind == SpellEffectKind.HealingOverTime
                        ? EffectEventKind.TickHealing : EffectEventKind.TickDamage;
                    _events.Add(new(kind, effect.Kind, effect.Target, effect.Source, effect.Magnitude));
                }
            }

            if (effect.Remaining > 0f && (effect.Kind != SpellEffectKind.HolyShield || effect.Shield > 0)) continue;
            End(effect);
            _active.RemoveAt(i);
        }
    }

    public List<SpellEffectEvent> TakeEvents()
    {
        if (_events.Count == 0) return [];
        var events = new List<SpellEffectEvent>(_events);
        _events.Clear();
        return events;
    }

    private void End(Active effect) =>
        _events.Add(new(EffectEventKind.Ended, effect.Kind, effect.Target, effect.Source, 0));

    private static bool Ticks(SpellEffectKind kind) => kind is
        SpellEffectKind.Burning or SpellEffectKind.HealingOverTime or SpellEffectKind.LifeforceLeech;
}
