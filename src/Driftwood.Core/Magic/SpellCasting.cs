using System.Numerics;

namespace Driftwood.Core.Magic;

public readonly record struct CastTarget(
    EffectTargetKind Kind,
    string Id,
    Vector3 Position,
    bool Alive,
    bool Allied,
    bool Hostile)
{
    public EffectTarget EffectTarget => new(Kind, Id);
}

public enum CastEventKind : byte { Started, Released, ChannelTick, Completed, Interrupted, Refused }

public readonly record struct SpellCastEvent(
    CastEventKind Kind,
    string PlayerId,
    SpellId Spell,
    int Rank,
    Vector3 Origin,
    CastTarget Target,
    string Reason = "");

public readonly record struct CastResult(bool Accepted, string Reason);

/// <summary>Simulation-time casting, GCD and channel clocks, with settlement only on release.</summary>
public sealed class SpellCastingService
{
    public const float GlobalCooldown = 1.15f;

    private sealed class Active
    {
        public required CharacterProgression Character;
        public required SpellDefinition Spell;
        public required int Rank;
        public required Vector3 Origin;
        public required CastTarget Target;
        public required float UntilRelease;
        public float ChannelLeft;
        public float ChannelTick;
        public bool Released;
    }

    private readonly Dictionary<string, Active> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _global = new(StringComparer.Ordinal);
    private readonly List<SpellCastEvent> _events = [];

    public bool IsCasting(string playerId) => _active.ContainsKey(playerId);

    public CastResult Begin(
        CharacterProgression character,
        string stableSpell,
        Vector3 origin,
        CastTarget target,
        bool hasLineOfSight)
    {
        if (_active.ContainsKey(character.PlayerId)) return Refuse(character, stableSpell, origin, target, "already casting");
        if (_global.GetValueOrDefault(character.PlayerId) > 0f) return Refuse(character, stableSpell, origin, target, "global cooldown");
        if (!SpellCatalogue.TryByStableName(stableSpell, out var spell)) return Refuse(character, stableSpell, origin, target, "unknown spell");
        if (!character.CanCast(stableSpell, out var ready)) return Refuse(character, stableSpell, origin, target, ready);

        var values = spell.AtRank(character.Rank);
        if (!TargetIsLegal(spell.Target, character.PlayerId, target, out var targetReason))
            return Refuse(character, stableSpell, origin, target, targetReason);
        if (Vector3.Distance(origin, target.Position) > values.Range)
            return Refuse(character, stableSpell, origin, target, "out of range");
        if (!hasLineOfSight && spell.Target != SpellTarget.Self)
            return Refuse(character, stableSpell, origin, target, "line of sight blocked");

        var cast = new Active
        {
            Character = character,
            Spell = spell,
            Rank = character.Rank,
            Origin = origin,
            Target = target,
            UntilRelease = values.CastTime,
            ChannelLeft = spell.Delivery == SpellDelivery.Channel ? values.Duration : 0f,
        };
        _active[character.PlayerId] = cast;
        _events.Add(new(CastEventKind.Started, character.PlayerId, spell.Id, cast.Rank, origin, target));
        if (cast.UntilRelease <= 0f) Release(cast);
        return new(true, "accepted");
    }

    public bool Interrupt(string playerId, string reason)
    {
        if (!_active.Remove(playerId, out var cast)) return false;
        _events.Add(new(CastEventKind.Interrupted, playerId, cast.Spell.Id, cast.Rank, cast.Origin, cast.Target, reason));
        return true;
    }

    public void Tick(float dt)
    {
        if (dt <= 0f || !float.IsFinite(dt)) return;
        foreach (var id in _global.Keys.ToArray())
        {
            var left = _global[id] - dt;
            if (left <= 0f) _global.Remove(id); else _global[id] = left;
        }

        foreach (var pair in _active.ToArray())
        {
            var cast = pair.Value;
            if (!cast.Released)
            {
                cast.UntilRelease -= dt;
                if (cast.UntilRelease <= 0f && !Release(cast)) _active.Remove(pair.Key);
                if (!cast.Released) continue;
            }

            if (cast.Spell.Delivery != SpellDelivery.Channel)
            {
                _events.Add(new(CastEventKind.Completed, pair.Key, cast.Spell.Id, cast.Rank, cast.Origin, cast.Target));
                _active.Remove(pair.Key);
                continue;
            }

            cast.ChannelLeft -= dt;
            cast.ChannelTick += dt;
            while (cast.ChannelTick >= 1f && cast.ChannelLeft >= 0f)
            {
                cast.ChannelTick -= 1f;
                _events.Add(new(CastEventKind.ChannelTick, pair.Key, cast.Spell.Id, cast.Rank, cast.Origin, cast.Target));
            }
            if (cast.ChannelLeft > 0f) continue;
            _events.Add(new(CastEventKind.Completed, pair.Key, cast.Spell.Id, cast.Rank, cast.Origin, cast.Target));
            _active.Remove(pair.Key);
        }
    }

    public List<SpellCastEvent> TakeEvents()
    {
        if (_events.Count == 0) return [];
        var events = new List<SpellCastEvent>(_events);
        _events.Clear();
        return events;
    }

    private bool Release(Active cast)
    {
        if (!cast.Character.SettleCast(cast.Spell))
        {
            _events.Add(new(CastEventKind.Refused, cast.Character.PlayerId, cast.Spell.Id, cast.Rank,
                cast.Origin, cast.Target, "resources changed before release"));
            return false;
        }
        cast.Released = true;
        _global[cast.Character.PlayerId] = GlobalCooldown / (1f + cast.Character.Statistics.Haste);
        _events.Add(new(CastEventKind.Released, cast.Character.PlayerId, cast.Spell.Id, cast.Rank, cast.Origin, cast.Target));
        return true;
    }

    private CastResult Refuse(CharacterProgression character, string stable, Vector3 origin, CastTarget target, string why)
    {
        var id = SpellCatalogue.TryByStableName(stable, out var spell) ? spell.Id : default;
        _events.Add(new(CastEventKind.Refused, character.PlayerId, id, character.Rank, origin, target, why));
        return new(false, why);
    }

    private static bool TargetIsLegal(SpellTarget rule, string playerId, CastTarget target, out string reason)
    {
        var legal = rule switch
        {
            SpellTarget.Self => target.Kind == EffectTargetKind.Player && target.Id == playerId && target.Alive,
            SpellTarget.SelfOrAlly => target.Kind == EffectTargetKind.Player && target.Alive && target.Allied,
            SpellTarget.DeadAlly => target.Kind == EffectTargetKind.Player && !target.Alive && target.Allied && target.Id != playerId,
            SpellTarget.Hostile => target.Alive && target.Hostile,
            SpellTarget.Ground => true,
            _ => false,
        };
        reason = legal ? "valid" : rule switch
        {
            SpellTarget.DeadAlly => "requires a dead allied player",
            SpellTarget.Hostile => "requires a living hostile",
            SpellTarget.SelfOrAlly => "requires a living ally",
            _ => "invalid target",
        };
        return legal;
    }
}
