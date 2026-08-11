using System.Numerics;

namespace Driftwood.Core.Magic;

public readonly record struct DeadPlayerState(string PlayerId, Vector3 DeathPosition, long Sequence);

public readonly record struct ReviveResult(
    bool Accepted, string Reason, string PlayerId, Vector3 Position, int Health);

/// <summary>
/// Host-side dead-player identity and once-only Revive settlement. It is deliberately independent
/// of a client body so P12 can route a remote player through the same contract without inventing a
/// second resurrection rule.
/// </summary>
public sealed class ReviveAuthority
{
    private readonly Dictionary<string, DeadPlayerState> _dead = new(StringComparer.Ordinal);
    private readonly HashSet<string> _receipts = new(StringComparer.Ordinal);
    private long _sequence;

    public IReadOnlyCollection<DeadPlayerState> Dead => _dead.Values;

    public DeadPlayerState MarkDead(string playerId, Vector3 position)
    {
        if (string.IsNullOrWhiteSpace(playerId) || !Finite(position))
            throw new ArgumentException("a dead player needs a stable identity and finite position", nameof(playerId));
        var state = new DeadPlayerState(playerId.Trim(), position, ++_sequence);
        _dead[state.PlayerId] = state;
        return state;
    }

    public bool IsDead(string playerId) => _dead.ContainsKey(playerId);

    public ReviveResult TryRevive(
        string receipt,
        string casterPlayerId,
        string targetPlayerId,
        int health,
        Func<Vector3, Vector3?> safePosition,
        Action<string, Vector3, int> settle)
    {
        if (string.IsNullOrWhiteSpace(receipt)) return Refuse(targetPlayerId, "missing settlement identity");
        if (_receipts.Contains(receipt)) return Refuse(targetPlayerId, "already settled");
        if (string.IsNullOrWhiteSpace(casterPlayerId) || casterPlayerId == targetPlayerId)
            return Refuse(targetPlayerId, "Revive requires another allied player");
        if (!_dead.TryGetValue(targetPlayerId, out var dead))
            return Refuse(targetPlayerId, "target is not dead");
        if (health <= 0) return Refuse(targetPlayerId, "Revive would return no health");
        if (safePosition(dead.DeathPosition) is not { } at || !Finite(at))
            return Refuse(targetPlayerId, "death position has no safe loaded return point");

        // The callback is allowed to fail loudly before state changes. Once it returns, both the
        // death record and the receipt move together and replay cannot create a second body.
        settle(targetPlayerId, at, health);
        _dead.Remove(targetPlayerId);
        _receipts.Add(receipt);
        return new(true, "revived", targetPlayerId, at, health);
    }

    private static ReviveResult Refuse(string playerId, string reason) =>
        new(false, reason, playerId ?? "", default, 0);

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
