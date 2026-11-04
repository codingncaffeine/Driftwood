namespace Driftwood.Core.Net;

/// <summary>
/// A host listening on several links at once — a TCP port for addresses and Epic for the meeting
/// place — seen by the session as one, with one numbering of guests.
/// </summary>
/// <remarks>⛳ A friend who types the address and a friend who picks the world from Epic's list end
/// up in the same world; the session never knows which way either came in.</remarks>
public sealed class CombinedTransport : INetTransport
{
    private readonly List<INetTransport> _links;
    private readonly Dictionary<(int Link, int Peer), int> _ours = [];
    private readonly Dictionary<int, (int Link, int Peer)> _theirs = [];
    private readonly object _lock = new();
    private int _next;
    private int _turn;

    public CombinedTransport(params INetTransport[] links)
    {
        if (links.Length == 0 || links.Any(link => !link.IsHost))
            throw new ArgumentException("every link of a combined host must be a host's");
        _links = [.. links];
    }

    /// <summary>Another way in, opened later (Epic signs in after the port is already open).</summary>
    public void Add(INetTransport link)
    {
        if (!link.IsHost) throw new ArgumentException("every link of a combined host must be a host's");
        lock (_lock) _links.Add(link);
    }

    private INetTransport LinkAt(int index)
    {
        lock (_lock) return _links[index];
    }

    public string Name
    {
        get { lock (_lock) return string.Join("+", _links.Select(link => link.Name)); }
    }

    public bool IsHost => true;

    /// <summary>The link a guest came in on, by its name ("tcp", "eos"), for logs.</summary>
    public string? LinkOf(int peer)
    {
        lock (_lock) return _theirs.TryGetValue(peer, out var theirs) ? _links[theirs.Link].Name : null;
    }

    public void Send(int peer, ReadOnlySpan<byte> message, Delivery delivery)
    {
        (int Link, int Peer) theirs;
        lock (_lock)
            if (!_theirs.TryGetValue(peer, out theirs)) return;
        LinkAt(theirs.Link).Send(theirs.Peer, message, delivery);
    }

    public void Disconnect(int peer, string reason)
    {
        (int Link, int Peer) theirs;
        lock (_lock)
            if (!_theirs.TryGetValue(peer, out theirs)) return;
        LinkAt(theirs.Link).Disconnect(theirs.Peer, reason);
    }

    public bool TryPoll(out NetEvent happened)
    {
        // Links take turns, so a busy one cannot keep another's guests waiting.
        int count;
        lock (_lock) count = _links.Count;
        for (var tried = 0; tried < count; tried++)
        {
            var index = (_turn + tried) % count;
            if (!LinkAt(index).TryPoll(out var theirs)) continue;
            _turn = (index + 1) % count;

            int ours;
            lock (_lock)
            {
                if (!_ours.TryGetValue((index, theirs.Peer), out ours))
                {
                    ours = ++_next;
                    _ours[(index, theirs.Peer)] = ours;
                    _theirs[ours] = (index, theirs.Peer);
                }

                if (theirs.Kind == NetEventKind.Disconnected)
                {
                    _ours.Remove((index, theirs.Peer));
                    _theirs.Remove(ours);
                }
            }

            happened = theirs with { Peer = ours };
            return true;
        }

        happened = default;
        return false;
    }

    public void Drain(TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        INetTransport[] links;
        lock (_lock) links = [.. _links];
        foreach (var link in links)
        {
            var left = until - DateTime.UtcNow;
            if (left > TimeSpan.Zero) link.Drain(left);
        }
    }

    public void Dispose()
    {
        INetTransport[] links;
        lock (_lock) links = [.. _links];
        foreach (var link in links) link.Dispose();
    }
}
