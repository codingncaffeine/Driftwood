using System.Collections.Concurrent;
using Driftwood.Core.Net;
using Epic.OnlineServices;
using Epic.OnlineServices.P2P;

namespace Driftwood.Net.Eos;

/// <summary>
/// A co-op link over Epic's P2P: direct between the machines when they can reach each other,
/// relayed by Epic when they cannot.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Framing.</b> Epic carries packets of at most <see cref="P2PInterface.MAX_PACKET_SIZE"/>
/// bytes. A reliable message is cut into pieces on the reliable-ordered channel, each led by one byte
/// (<see cref="More"/> or <see cref="Last"/>); pieces of one peer arrive in order, so joining them back
/// needs no numbering. An unreliable message that fits one packet rides the unreliable channel whole;
/// a bigger one goes reliable. A guest's first packet is a one-byte knock on its own channel: it opens
/// the connection, and the host drops it.</para>
/// <para>⛳ <b>Pacing.</b> Pieces are handed to Epic only while its outgoing queue is short, so a
/// world being sent on join cannot stall a pose behind a hundred megabytes, and an unreliable message
/// is dropped (as <see cref="TcpTransport"/> does) when a peer is already behind.</para>
/// <para>⛔ All SDK work runs on the place's thread (<see cref="Pump"/>, <see cref="Open"/>); the
/// public members only queue.</para>
/// </remarks>
internal sealed class EosLink : INetTransport
{
    private const string SocketName = "DRIFTWOOD";
    private const byte ReliableChannel = 0;
    private const byte UnreliableChannel = 1;
    private const byte KnockChannel = 2;
    private const byte More = 0;
    private const byte Last = 1;
    private const int Piece = P2PInterface.MAX_PACKET_SIZE - 1;

    /// <summary>Same cap as TCP's: a corrupt stream cannot make us hold gigabytes.</summary>
    private const int MaxMessage = TcpTransport.MaxMessage;

    /// <summary>Epic's outgoing queue is topped up to this, no further.</summary>
    private const ulong OutgoingWindow = 256 * 1024;

    /// <summary>An unreliable message is dropped when a peer has this many pieces waiting.</summary>
    private const int UnreliableBacklog = 64;

    private readonly EpicMeetingPlace _place;
    private readonly ConcurrentQueue<NetEvent> _events = new();
    private readonly ConcurrentQueue<(int Peer, byte[] Message, Delivery Delivery)> _outgoing = new();
    private readonly ConcurrentQueue<(int Peer, string Reason)> _closing = new();
    private readonly TaskCompletionSource _established = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _unsent;
    private volatile bool _disposed;

    // ⛔ SDK thread only.
    private readonly Dictionary<int, Peer> _peers = [];
    private readonly Dictionary<string, int> _byAddress = [];
    private ulong _requestNotify, _closedNotify, _establishedNotify;
    private int _nextPeer;
    private bool _opened;
    private byte[] _receiveBuffer = new byte[P2PInterface.MAX_PACKET_SIZE];

    public EosLink(EpicMeetingPlace place, bool host)
    {
        _place = place;
        IsHost = host;
    }

    public string Name => "eos";

    public bool IsHost { get; }

    /// <summary>A guest's connection reached the host (or failed: the task faults).</summary>
    public Task Established => _established.Task;

    /// <summary>Pieces still to go: the place's thread ticks faster while this is set.</summary>
    public bool Busy => Volatile.Read(ref _unsent) > 0;

    public void Send(int peer, ReadOnlySpan<byte> message, Delivery delivery)
    {
        if (_disposed) return;
        if (message.Length > MaxMessage) throw new ArgumentException($"message of {message.Length} bytes is over the limit");
        Interlocked.Increment(ref _unsent);
        _outgoing.Enqueue((peer, message.ToArray(), delivery));
        _place.Wake();
    }

    public void Disconnect(int peer, string reason)
    {
        _closing.Enqueue((peer, reason));
        _place.Wake();
    }

    public bool TryPoll(out NetEvent happened) => _events.TryDequeue(out happened);

    public void Drain(TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until && Volatile.Read(ref _unsent) > 0) Thread.Sleep(5);
        // Then a moment for Epic's own queue to reach the wire.
        if (Volatile.Read(ref _unsent) == 0) Thread.Sleep(Math.Min(250, (int)Math.Max(0, (until - DateTime.UtcNow).TotalMilliseconds)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _place.Post(CloseOnThread);
    }

    // ───────────────────────────── SDK thread ─────────────────────────────

    private P2PInterface P2P => _place.Platform.GetP2PInterface();

    private static SocketId Socket() => new() { SocketName = SocketName };

    /// <summary>Starts listening (host) or knocks on the host's door (guest).</summary>
    public void Open(string? hostAddress)
    {
        if (_place.Link is not null && !ReferenceEquals(_place.Link, this)) _place.Link.CloseOnThread();
        _place.Link = this;
        _opened = true;
        var p2p = P2P;
        var me = _place.Me;

        var closed = new AddNotifyPeerConnectionClosedOptions { LocalUserId = me, SocketId = Socket() };
        _closedNotify = p2p.AddNotifyPeerConnectionClosed(ref closed, null, (ref OnRemoteConnectionClosedInfo info) =>
            Lost(info.RemoteUserId, Words(info.Reason)));

        var established = new AddNotifyPeerConnectionEstablishedOptions { LocalUserId = me, SocketId = Socket() };
        _establishedNotify = p2p.AddNotifyPeerConnectionEstablished(ref established, null,
            (ref OnPeerConnectionEstablishedInfo info) =>
            {
                Console.WriteLine($"epic        link to {EpicMeetingPlace.Short(Text(info.RemoteUserId))} "
                                  + $"{(info.NetworkType == NetworkConnectionType.RelayedConnection ? "relayed by Epic" : "direct")}");
                if (!IsHost) _established.TrySetResult();
            });

        if (IsHost)
        {
            var request = new AddNotifyPeerConnectionRequestOptions { LocalUserId = me, SocketId = Socket() };
            _requestNotify = p2p.AddNotifyPeerConnectionRequest(ref request, null, (ref OnIncomingConnectionRequestInfo info) =>
            {
                var accept = new AcceptConnectionOptions { LocalUserId = _place.Me, RemoteUserId = info.RemoteUserId, SocketId = Socket() };
                var result = P2P.AcceptConnection(ref accept);
                if (result != Result.Success)
                {
                    Console.Error.WriteLine($"driftwood: Epic: could not accept a guest ({result})");
                    return;
                }

                PeerFor(info.RemoteUserId);
            });
            return;
        }

        var host = ProductUserId.FromString(hostAddress);
        if (host is null || !host.IsValid())
        {
            _established.TrySetException(new InvalidOperationException($"'{hostAddress}' is not an Epic player address"));
            return;
        }

        // ⚠ A guest accepts its host too: Epic opens a connection only when both sides have.
        var acceptHost = new AcceptConnectionOptions { LocalUserId = me, RemoteUserId = host, SocketId = Socket() };
        p2p.AcceptConnection(ref acceptHost);
        PeerFor(host);
        SendPacket(host, [1], KnockChannel, PacketReliability.ReliableOrdered);
    }

    /// <summary>The peer number for a remote user, made (and announced) the first time it is seen.</summary>
    private Peer PeerFor(ProductUserId user)
    {
        var address = Text(user);
        if (_byAddress.TryGetValue(address, out var id)) return _peers[id];

        id = IsHost ? ++_nextPeer : INetTransport.HostPeer;
        var peer = new Peer(id, user);
        _peers[id] = peer;
        _byAddress[address] = id;
        _events.Enqueue(new NetEvent(NetEventKind.Connected, id));
        return peer;
    }

    private void Lost(ProductUserId user, string reason)
    {
        if (!_byAddress.Remove(Text(user), out var id) || !_peers.Remove(id, out var peer)) return;
        Interlocked.Add(ref _unsent, -peer.QueuedMessages);
        _events.Enqueue(new NetEvent(NetEventKind.Disconnected, id, Reason: reason));
        if (!IsHost) _established.TrySetException(new InvalidOperationException(reason));
    }

    /// <summary>Once a tick: queue new messages, hand pieces to Epic, take in what arrived, close what was asked.</summary>
    public void Pump()
    {
        if (!_opened) return;
        var p2p = P2P;
        var me = _place.Me;

        while (_outgoing.TryDequeue(out var send))
        {
            if (!_peers.TryGetValue(send.Peer, out var peer) || peer.Closing is not null)
            {
                Interlocked.Decrement(ref _unsent);
                continue;
            }

            if (send.Delivery == Delivery.Unreliable)
            {
                Interlocked.Decrement(ref _unsent);
                if (peer.Pieces.Count >= UnreliableBacklog) continue;
                if (send.Message.Length <= P2PInterface.MAX_PACKET_SIZE)
                {
                    SendPacket(peer.User, send.Message, UnreliableChannel, PacketReliability.UnreliableUnordered);
                    continue;
                }

                Interlocked.Increment(ref _unsent);
            }

            Cut(peer, send.Message);
        }

        // Hand pieces over while Epic's queue is short; peers take turns so one big world never starves a pose.
        var queue = new GetPacketQueueInfoOptions();
        var room = p2p.GetPacketQueueInfo(ref queue, out var info) == Result.Success
            ? OutgoingWindow > info.OutgoingPacketQueueCurrentSizeBytes ? OutgoingWindow - info.OutgoingPacketQueueCurrentSizeBytes : 0
            : OutgoingWindow;
        var any = true;
        while (room > 0 && any)
        {
            any = false;
            foreach (var peer in _peers.Values)
            {
                if (!peer.Pieces.TryDequeue(out var piece)) continue;
                any = true;
                SendPacket(peer.User, piece.Bytes, ReliableChannel, PacketReliability.ReliableOrdered);
                room = room > (ulong)piece.Bytes.Length ? room - (ulong)piece.Bytes.Length : 0;
                if (piece.EndsMessage)
                {
                    peer.QueuedMessages--;
                    Interlocked.Decrement(ref _unsent);
                }
            }
        }

        Receive(p2p, me);

        while (_closing.TryDequeue(out var close))
            if (_peers.TryGetValue(close.Peer, out var peer)) peer.Closing ??= (close.Reason, DateTime.UtcNow.AddSeconds(2));

        // A peer being let go keeps its last words (a refusal is useless if the close outruns it).
        foreach (var peer in _peers.Values.ToArray())
        {
            if (peer.Closing is not { } closing) continue;
            if (peer.Pieces.Count > 0 && DateTime.UtcNow < closing.Until) continue;
            if (peer.ClosingAfter is null)
            {
                peer.ClosingAfter = DateTime.UtcNow.AddMilliseconds(250);
                continue;
            }

            if (DateTime.UtcNow < peer.ClosingAfter) continue;
            var shut = new CloseConnectionOptions { LocalUserId = me, RemoteUserId = peer.User, SocketId = Socket() };
            p2p.CloseConnection(ref shut);
            _byAddress.Remove(Text(peer.User));
            _peers.Remove(peer.Id);
            Interlocked.Add(ref _unsent, -peer.QueuedMessages);
        }
    }

    private void Cut(Peer peer, byte[] message)
    {
        peer.QueuedMessages++;
        var at = 0;
        do
        {
            var length = Math.Min(Piece, message.Length - at);
            var bytes = new byte[length + 1];
            var last = at + length >= message.Length;
            bytes[0] = last ? Last : More;
            Buffer.BlockCopy(message, at, bytes, 1, length);
            peer.Pieces.Enqueue((bytes, last));
            at += length;
        }
        while (at < message.Length);
    }

    private void Receive(P2PInterface p2p, ProductUserId me)
    {
        while (true)
        {
            var size = new GetNextReceivedPacketSizeOptions { LocalUserId = me, RequestedChannel = null };
            if (p2p.GetNextReceivedPacketSize(ref size, out var bytes) != Result.Success) return;
            if (_receiveBuffer.Length < bytes) _receiveBuffer = new byte[bytes];

            var options = new ReceivePacketOptions { LocalUserId = me, MaxDataSizeBytes = bytes, RequestedChannel = null };
            ProductUserId from = default!;
            SocketId socket = default;
            if (p2p.ReceivePacket(ref options, ref from, ref socket, out var channel, new ArraySegment<byte>(_receiveBuffer), out var written)
                != Result.Success)
                return;

            var peer = IsHost ? PeerFor(from) : _byAddress.TryGetValue(Text(from), out var id) ? _peers[id] : null;
            if (peer is null || peer.Closing is not null) continue;
            var data = _receiveBuffer.AsSpan(0, (int)written);

            switch (channel)
            {
                case UnreliableChannel:
                    _events.Enqueue(new NetEvent(NetEventKind.Message, peer.Id, data.ToArray()));
                    break;
                case ReliableChannel when data.Length >= 1:
                    if (peer.Joining.Length + data.Length - 1 > MaxMessage)
                    {
                        Console.Error.WriteLine("driftwood: Epic: a peer sent a message that cannot be right");
                        peer.Closing = ("unreadable", DateTime.UtcNow);
                        _events.Enqueue(new NetEvent(NetEventKind.Disconnected, peer.Id, Reason: "unreadable"));
                        continue;
                    }

                    peer.Joining.Write(data[1..]);
                    if (data[0] == Last)
                    {
                        _events.Enqueue(new NetEvent(NetEventKind.Message, peer.Id, peer.Joining.ToArray()));
                        peer.Joining.SetLength(0);
                    }

                    break;
                // The knock, or anything this version does not know: nothing to hand on.
            }
        }
    }

    private void SendPacket(ProductUserId to, byte[] data, byte channel, PacketReliability reliability)
    {
        var options = new SendPacketOptions
        {
            LocalUserId = _place.Me,
            RemoteUserId = to,
            SocketId = Socket(),
            Channel = channel,
            Data = new ArraySegment<byte>(data),
            AllowDelayedDelivery = true,
            Reliability = reliability,
            DisableAutoAcceptConnection = false,
        };
        var result = P2P.SendPacket(ref options);
        if (result != Result.Success) Console.Error.WriteLine($"driftwood: Epic: send {result}");
    }

    /// <summary>Stops listening and closes every connection. SDK thread.</summary>
    public void CloseOnThread()
    {
        if (!_opened) return;
        _opened = false;
        var p2p = P2P;
        if (_requestNotify != 0) p2p.RemoveNotifyPeerConnectionRequest(_requestNotify);
        if (_closedNotify != 0) p2p.RemoveNotifyPeerConnectionClosed(_closedNotify);
        if (_establishedNotify != 0) p2p.RemoveNotifyPeerConnectionEstablished(_establishedNotify);
        var close = new CloseConnectionsOptions { LocalUserId = _place.Me, SocketId = Socket() };
        p2p.CloseConnections(ref close);
        _peers.Clear();
        _byAddress.Clear();
        Volatile.Write(ref _unsent, 0);
        _established.TrySetException(new InvalidOperationException("closed"));
        if (ReferenceEquals(_place.Link, this)) _place.Link = null;
    }

    private static string Text(ProductUserId user)
    {
        user.ToString(out var text);
        return text?.ToString() ?? "";
    }

    private static string Words(ConnectionClosedReason reason) => reason switch
    {
        ConnectionClosedReason.ClosedByLocalUser => "closed",
        ConnectionClosedReason.ClosedByPeer => "the other side closed the connection",
        ConnectionClosedReason.TimedOut => "the connection timed out",
        ConnectionClosedReason.TooManyConnections => "the host has too many connections",
        ConnectionClosedReason.NegotiationFailed => "the two machines could not find a way to each other",
        ConnectionClosedReason.ConnectionFailed or ConnectionClosedReason.ConnectionClosed => "the connection was lost",
        ConnectionClosedReason.InvalidMessage or ConnectionClosedReason.InvalidData => "the other side sent something unreadable",
        ConnectionClosedReason.ConnectionIgnored => "the other side did not answer",
        _ => reason.ToString(),
    };

    private sealed class Peer(int id, ProductUserId user)
    {
        public readonly int Id = id;
        public readonly ProductUserId User = user;
        public readonly Queue<(byte[] Bytes, bool EndsMessage)> Pieces = new();
        public readonly MemoryStream Joining = new();
        public int QueuedMessages;
        public (string Reason, DateTime Until)? Closing;
        public DateTime? ClosingAfter;
    }
}
