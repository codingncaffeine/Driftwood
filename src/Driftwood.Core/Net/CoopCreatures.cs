using System.Numerics;
using Driftwood.Core.Entities;

namespace Driftwood.Core.Net;

/// <summary>
/// The host's numbering of its creatures for the wire (N5): the same creature keeps one number for as
/// long as it lives, and a number is never handed out twice in a session.
/// </summary>
/// <remarks>
/// Keyed by the object itself, never by anything the creature carries, so a creature that is renamed,
/// grows, or changes kind (a slime splitting is a new creature) stays itself or becomes a new one exactly
/// as the herd sees it.
/// </remarks>
public sealed class CreatureRoster
{
    private readonly Dictionary<Creature, int> _ids = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, Creature> _byId = [];
    private readonly List<(float Distance, Creature One)> _scratch = [];
    private int _next = 1;

    public int Count => _ids.Count;

    /// <summary>The number for a creature, giving it one if it has none yet.</summary>
    public int IdOf(Creature creature)
    {
        if (_ids.TryGetValue(creature, out var id)) return id;
        id = _next++;
        _ids[creature] = id;
        _byId[id] = creature;
        return id;
    }

    /// <summary>The creature behind a number, or null once it has left the herd.</summary>
    public Creature? Find(int id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// The creatures within <paramref name="radius"/> of a point, nearest first, at most
    /// <see cref="CreaturesMessage.MaxPerMessage"/> of them.
    /// </summary>
    public void Near(IReadOnlyList<Creature> herd, Vector3 at, float radius, List<CreatureState> into)
    {
        into.Clear();
        _scratch.Clear();
        var reach = radius * radius;
        foreach (var creature in herd)
        {
            var d = Vector3.DistanceSquared(creature.Position, at);
            if (d <= reach) _scratch.Add((d, creature));
        }

        _scratch.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        var count = Math.Min(_scratch.Count, CreaturesMessage.MaxPerMessage);
        for (var i = 0; i < count; i++)
        {
            var c = _scratch[i].One;
            into.Add(new CreatureState(
                IdOf(c), c.Kind, (byte)Math.Clamp(c.Bulk, 1, 255), c.Position, c.Yaw,
                (short)Math.Clamp(c.Health, short.MinValue, short.MaxValue), c.HurtFor, c.DyingFor, c.Grown, c.FuseFor));
        }
    }

    /// <summary>Forgets the numbers of creatures no longer in the herd. Cheap enough for every few seconds.</summary>
    public void Prune(IReadOnlyList<Creature> herd)
    {
        if (_ids.Count == 0) return;
        var alive = new HashSet<Creature>(herd, ReferenceEqualityComparer.Instance);
        List<Creature>? gone = null;
        foreach (var creature in _ids.Keys)
            if (!alive.Contains(creature)) (gone ??= []).Add(creature);
        if (gone is null) return;
        foreach (var creature in gone)
        {
            _byId.Remove(_ids[creature]);
            _ids.Remove(creature);
        }
    }
}

/// <summary>
/// The guest's side (N5): the host's creatures stood up in a herd of the guest's own that is never
/// stepped — so picking, aiming and drawing work on them exactly as on the host's.
/// </summary>
/// <remarks>
/// <para>⛳ Drawn a little in the past (<see cref="Delay"/>), between the two sets either side of that
/// moment, the way <see cref="RemotePlayer"/> is; a set lost on the way costs nothing visible.</para>
/// <para>A creature missing from the newest set is gone — despawned, too far, or swept after dying —
/// and is taken out of the herd. An older set arriving late is ignored.</para>
/// </remarks>
public sealed class CreatureMirror(Func<string, SpawnKind?> resolve)
{
    /// <summary>Behind real time by more than one set's interval (a tenth of a second) and a lost one's.</summary>
    public const double Delay = 0.2;

    private readonly Dictionary<int, Mirrored> _mirrored = [];
    private readonly Dictionary<Creature, int> _ids = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<int> _seen = [];
    private uint _sequence;
    private bool _any;

    public int Count => _mirrored.Count;

    /// <summary>Sets heard, and how many were thrown away for arriving after a newer one.</summary>
    public int SetsHeard { get; private set; }
    public int SetsLate { get; private set; }

    /// <summary>Kinds this build could not stand up (should be none: the hello proved both builds match).</summary>
    public int Unknown { get; private set; }

    /// <summary>The host's number for a mirrored creature, or null for one that is not the host's.</summary>
    public int? IdOf(Creature creature) => _ids.TryGetValue(creature, out var id) ? id : null;

    public void Heard(double now, CreaturesMessage set, CreatureHerd herd)
    {
        if (_any && unchecked((int)(set.Sequence - _sequence)) <= 0)
        {
            SetsLate++;
            return;
        }
        _any = true;
        _sequence = set.Sequence;
        SetsHeard++;

        _seen.Clear();
        foreach (var state in set.Creatures)
        {
            _seen.Add(state.Id);
            if (!_mirrored.TryGetValue(state.Id, out var mirrored))
            {
                if (resolve(state.Kind) is not { } kind)
                {
                    Unknown++;
                    continue;
                }
                var creature = herd.SpawnAt(kind, state.Position, state.Yaw, state.Bulk);
                mirrored = new Mirrored(creature);
                _mirrored[state.Id] = mirrored;
                _ids[creature] = state.Id;
            }

            var c = mirrored.Creature;
            c.Bulk = state.Bulk;
            c.Health = state.Health;
            c.HurtFor = state.HurtFor;
            c.DyingFor = state.DyingFor;
            c.Grown = state.Grown;
            c.FuseFor = state.FuseFor;
            mirrored.Heard(now, state.Position, state.Yaw);
        }

        List<int>? gone = null;
        foreach (var id in _mirrored.Keys)
            if (!_seen.Contains(id)) (gone ??= []).Add(id);
        if (gone is null) return;
        foreach (var id in gone)
        {
            var creature = _mirrored[id].Creature;
            herd.Remove(creature);
            _ids.Remove(creature);
            _mirrored.Remove(id);
        }
    }

    /// <summary>Moves every mirrored creature to where it was <see cref="Delay"/> ago. Once a frame.</summary>
    public void Step(double now)
    {
        foreach (var mirrored in _mirrored.Values) mirrored.Place(now - Delay);
    }

    private sealed class Mirrored(Creature creature)
    {
        public Creature Creature { get; } = creature;

        private readonly List<(double At, Vector3 Position, float Yaw)> _poses = [];

        public void Heard(double now, Vector3 position, float yaw)
        {
            _poses.Add((now, position, yaw));
            if (_poses.Count > 8) _poses.RemoveAt(0);
        }

        public void Place(double at)
        {
            if (_poses.Count == 0) return;
            var (position, yaw) = Sample(at);
            var moved = Vector3.DistanceSquared(position, Creature.Position) > 1e-6f;
            Creature.Position = position;
            Creature.Yaw = yaw;
            Creature.WantsYaw = yaw;
            Creature.Moving = moved;
        }

        private (Vector3, float) Sample(double at)
        {
            if (at <= _poses[0].At) return (_poses[0].Position, _poses[0].Yaw);
            for (var i = _poses.Count - 1; i > 0; i--)
            {
                var (t0, p0, y0) = _poses[i - 1];
                var (t1, p1, y1) = _poses[i];
                if (at < t0) continue;
                if (at >= t1) return (p1, y1);
                var f = (float)((at - t0) / Math.Max(t1 - t0, 1e-6));
                var turn = (y1 - y0) % 360f;
                if (turn > 180f) turn -= 360f;
                else if (turn < -180f) turn += 360f;
                return (Vector3.Lerp(p0, p1, f), y0 + turn * f);
            }
            return (_poses[^1].Position, _poses[^1].Yaw);
        }
    }
}
