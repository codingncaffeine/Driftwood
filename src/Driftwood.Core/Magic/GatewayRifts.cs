using System.Numerics;

namespace Driftwood.Core.Magic;

public sealed class GatewayRift
{
    public required string Id { get; init; }
    public required string OwnerPlayerId { get; init; }
    public required int Rank { get; init; }
    public required Vector3 Position { get; init; }
    public required float Yaw { get; init; }
    public required float Lifetime { get; init; }
    public float Age { get; set; }
    public float Width => 1.25f + (Rank - 1) * 0.08f;
    public float Height => 2.25f + (Rank - 1) * 0.1f;
}

public readonly record struct GatewayEvent(string RiftId, string PlayerId, string Kind, Vector3 Position, string Reason = "");

/// <summary>Temporary owned portal actors and idempotent, per-entrant bind settlement.</summary>
public sealed class GatewayRiftService
{
    private readonly Dictionary<string, GatewayRift> _byOwner = new(StringComparer.Ordinal);
    private readonly HashSet<string> _entries = new(StringComparer.Ordinal);
    private readonly List<GatewayEvent> _events = [];
    private long _nextId = 1;

    public IReadOnlyCollection<GatewayRift> All => _byOwner.Values;

    public GatewayRift? Open(string ownerPlayerId, int rank, Vector3 position, float yaw, float lifetime)
    {
        if (string.IsNullOrWhiteSpace(ownerPlayerId) || !Finite(position) || lifetime <= 0f) return null;
        if (_byOwner.Remove(ownerPlayerId, out var old))
            _events.Add(new(old.Id, ownerPlayerId, "closed", old.Position, "replaced"));
        var rift = new GatewayRift
        {
            Id = $"rift-{_nextId++:x}", OwnerPlayerId = ownerPlayerId, Rank = Math.Clamp(rank, 1, 4),
            Position = position, Yaw = yaw, Lifetime = lifetime,
        };
        _byOwner[ownerPlayerId] = rift;
        _events.Add(new(rift.Id, ownerPlayerId, "opened", position));
        return rift;
    }

    public bool TryEnter(
        GatewayRift rift,
        string playerId,
        Vector3 playerPosition,
        Func<string, Vector3?> bindFor,
        Func<Vector3, bool> safe,
        Action<Vector3> teleport,
        out string reason)
    {
        reason = "";
        if (!_byOwner.Values.Contains(rift) || rift.Age >= rift.Lifetime) { reason = "rift expired"; return false; }
        var receipt = $"{rift.Id}:{playerId}";
        if (_entries.Contains(receipt)) { reason = "already entered"; return false; }
        if (!Inside(rift, playerPosition)) { reason = "outside aperture"; return false; }
        if (bindFor(playerId) is not { } destination)
        {
            reason = "no bind set";
            _events.Add(new(rift.Id, playerId, "refused", playerPosition, reason));
            return false;
        }
        if (!Finite(destination) || !safe(destination))
        {
            reason = "bind is not safe and loaded";
            _events.Add(new(rift.Id, playerId, "refused", playerPosition, reason));
            return false;
        }
        _entries.Add(receipt);
        teleport(destination);
        _events.Add(new(rift.Id, playerId, "entered", destination));
        return true;
    }

    public void Tick(float dt)
    {
        if (dt <= 0f) return;
        foreach (var pair in _byOwner.ToArray())
        {
            pair.Value.Age += dt;
            if (pair.Value.Age < pair.Value.Lifetime) continue;
            _events.Add(new(pair.Value.Id, pair.Key, "closed", pair.Value.Position, "expired"));
            _byOwner.Remove(pair.Key);
        }
    }

    public List<GatewayEvent> TakeEvents()
    {
        var events = new List<GatewayEvent>(_events);
        _events.Clear();
        return events;
    }

    public static bool Inside(GatewayRift rift, Vector3 point)
    {
        var delta = point - rift.Position;
        var yaw = float.DegreesToRadians(rift.Yaw);
        var normal = new Vector3(-MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        var side = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        var across = Vector3.Dot(delta, side) / (rift.Width * 0.5f);
        var vertical = (delta.Y - rift.Height * 0.5f) / (rift.Height * 0.5f);
        return MathF.Abs(Vector3.Dot(delta, normal)) <= 0.35f && across * across + vertical * vertical <= 1f;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
