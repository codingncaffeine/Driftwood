namespace Driftwood.Core.Net;

/// <summary>How a message has to arrive.</summary>
public enum Delivery : byte
{
    /// <summary>Arrives, once, in the order it was sent. World changes, joins, anything that is a fact.</summary>
    Reliable,

    /// <summary>May be dropped when the link is behind. A pose that is already stale is worth nothing.</summary>
    Unreliable,
}

public enum NetEventKind : byte
{
    Connected,
    Disconnected,
    Message,
}

/// <summary>Something that happened on the link, handed to the game thread by <see cref="INetTransport.TryPoll"/>.</summary>
public readonly record struct NetEvent(NetEventKind Kind, int Peer, byte[]? Data = null, string? Reason = null);

/// <summary>
/// A connection between the host and its guests, whatever carries it.
/// </summary>
/// <remarks>
/// <para>⛳ <b>The host sees its guests as peers 1, 2, 3…; a guest sees exactly one peer, the host, as
/// <see cref="HostPeer"/>.</b> The session above never knows whether it is a TCP socket, a relay
/// through Epic, or a loopback pair in one process — which is what lets the same session code be
/// checked headlessly over loopback and then shipped over the internet.</para>
/// <para>⛔ <b>Nothing here blocks the caller.</b> Sending queues; receiving is drained by
/// <see cref="TryPoll"/> once a frame on the game thread. Socket work lives on threads of its own.</para>
/// </remarks>
public interface INetTransport : IDisposable
{
    /// <summary>The peer number a guest uses for its host.</summary>
    const int HostPeer = 0;

    /// <summary>Short name for logs: "tcp", "eos", "loopback".</summary>
    string Name { get; }

    bool IsHost { get; }

    /// <summary>Queues a whole message for a peer. Any size; the transport frames or splits it.</summary>
    void Send(int peer, ReadOnlySpan<byte> message, Delivery delivery);

    /// <summary>Closes one peer's link. The other side hears <see cref="NetEventKind.Disconnected"/>.</summary>
    void Disconnect(int peer, string reason);

    /// <summary>The next thing that happened, or false when nothing is waiting.</summary>
    bool TryPoll(out NetEvent happened);

    /// <summary>Waits, up to a limit, for everything queued to have been handed to the network.
    /// For leaving: the last word ("here is my character") must not be lost to the close.</summary>
    void Drain(TimeSpan limit);
}
