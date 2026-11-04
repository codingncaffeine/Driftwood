using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Driftwood.Core.Net;

/// <summary>
/// Direct connection by address: the host listens on a TCP port and guests dial it.
/// </summary>
/// <remarks>
/// <para>⛳ <b>The fallback path, and deliberately plain.</b> The meeting place is Epic, which also
/// carries the traffic when it can; this is for a LAN, a forwarded port, or a build without Epic's
/// credentials. TCP gives reliable ordering for free, and an unreliable message is honoured by
/// being DROPPED when the peer's queue is already behind, so a stale pose never delays a fact.</para>
/// <para>Frames are a 4-byte little-endian length then the bytes, capped at <see cref="MaxMessage"/>
/// so a corrupt length cannot ask for gigabytes.</para>
/// </remarks>
public sealed class TcpTransport : INetTransport
{
    /// <summary>The port a host listens on unless told otherwise.</summary>
    public const int DefaultPort = 47790;

    /// <summary>Largest single message: a whole world on join fits several times over.</summary>
    public const int MaxMessage = 64 * 1024 * 1024;

    /// <summary>An unreliable message is dropped when this many are already queued for the peer.</summary>
    private const int UnreliableBacklog = 32;

    private readonly ConcurrentQueue<NetEvent> _events = new();
    private readonly ConcurrentDictionary<int, Link> _links = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly TcpListener? _listener;
    private int _nextPeer;

    public string Name => "tcp";

    public bool IsHost { get; }

    /// <summary>The port actually bound — useful when 0 asked the system to pick one.</summary>
    public int Port { get; }

    private TcpTransport(bool host, TcpListener? listener, int port)
    {
        IsHost = host;
        _listener = listener;
        Port = port;
    }

    /// <summary>Starts hosting on a port (0 = any free one). Both IPv4 and IPv6 where the system allows.</summary>
    public static TcpTransport Listen(int port)
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.IPv6Any, port);
            listener.Server.DualMode = true;
            listener.Start();
        }
        catch (SocketException)
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
        }

        var transport = new TcpTransport(true, listener, ((IPEndPoint)listener.LocalEndpoint).Port);
        _ = transport.AcceptLoop();
        return transport;
    }

    /// <summary>Dials a host. "name", "name:port", "1.2.3.4:port" and "[v6]:port" are all accepted.</summary>
    public static async Task<TcpTransport> ConnectAsync(string address, CancellationToken cancel)
    {
        var (host, port) = SplitAddress(address);
        var client = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
        client.Client.DualMode = true;
        try
        {
            await client.ConnectAsync(host, port, cancel).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        var transport = new TcpTransport(false, null, port);
        transport.Adopt(INetTransport.HostPeer, client);
        return transport;
    }

    /// <summary>Splits "host:port" (or a bare host, which gets <see cref="DefaultPort"/>).</summary>
    public static (string Host, int Port) SplitAddress(string address)
    {
        address = address.Trim();
        if (address.StartsWith('['))
        {
            var close = address.IndexOf(']');
            if (close > 0)
            {
                var host6 = address[1..close];
                var rest = address[(close + 1)..];
                return (host6, rest.StartsWith(':') && int.TryParse(rest[1..], out var p6) ? p6 : DefaultPort);
            }
        }

        var colon = address.LastIndexOf(':');
        // One colon is host:port; more than one without brackets is a bare IPv6 address.
        if (colon > 0 && address.IndexOf(':') == colon && int.TryParse(address[(colon + 1)..], out var port))
            return (address[..colon], port);
        return (address, DefaultPort);
    }

    public void Send(int peer, ReadOnlySpan<byte> message, Delivery delivery)
    {
        if (!_links.TryGetValue(peer, out var link)) return;
        if (message.Length > MaxMessage) throw new ArgumentException($"message of {message.Length} bytes is over the limit");
        if (delivery == Delivery.Unreliable && link.Queued >= UnreliableBacklog) return;

        var frame = new byte[4 + message.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, message.Length);
        message.CopyTo(frame.AsSpan(4));
        Interlocked.Increment(ref link.Queued);
        Interlocked.Increment(ref link.Writing);
        if (!link.Outgoing.Writer.TryWrite(frame))
        {
            Interlocked.Decrement(ref link.Queued);
            Interlocked.Decrement(ref link.Writing);
        }
    }

    public void Disconnect(int peer, string reason)
    {
        if (_links.TryRemove(peer, out var link)) link.Close(reason, announce: false);
    }

    public bool TryPoll(out NetEvent happened) => _events.TryDequeue(out happened);

    public void Drain(TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until && _links.Values.Any(link => Volatile.Read(ref link.Writing) > 0))
            Thread.Sleep(5);
    }

    public void Dispose()
    {
        _closing.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        foreach (var peer in _links.Keys)
            if (_links.TryRemove(peer, out var link)) link.Close("closed", announce: false);
    }

    private async Task AcceptLoop()
    {
        while (!_closing.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_closing.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (_closing.IsCancellationRequested) return; continue; }

            client.NoDelay = true;
            Adopt(Interlocked.Increment(ref _nextPeer), client);
        }
    }

    private void Adopt(int peer, TcpClient client)
    {
        var link = new Link(this, peer, client);
        _links[peer] = link;
        _events.Enqueue(new NetEvent(NetEventKind.Connected, peer));
        _ = link.ReadLoop();
        _ = link.WriteLoop();
    }

    private void Lost(Link link, string reason)
    {
        if (_links.TryRemove(new KeyValuePair<int, Link>(link.Peer, link)))
            _events.Enqueue(new NetEvent(NetEventKind.Disconnected, link.Peer, Reason: reason));
    }

    private sealed class Link(TcpTransport owner, int peer, TcpClient client)
    {
        public readonly int Peer = peer;
        public readonly Channel<byte[]> Outgoing = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true });
        public int Queued;

        /// <summary>Frames queued or still being written — what <see cref="Drain"/> waits on.</summary>
        public int Writing;
        private readonly NetworkStream _stream = client.GetStream();
        private int _closed;

        public async Task ReadLoop()
        {
            var header = new byte[4];
            try
            {
                while (true)
                {
                    await _stream.ReadExactlyAsync(header, owner._closing.Token).ConfigureAwait(false);
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length < 0 || length > MaxMessage)
                    {
                        Fail("the other side sent a frame that cannot be right");
                        return;
                    }

                    var body = new byte[length];
                    await _stream.ReadExactlyAsync(body, owner._closing.Token).ConfigureAwait(false);
                    owner._events.Enqueue(new NetEvent(NetEventKind.Message, Peer, body));
                }
            }
            catch (EndOfStreamException) { Fail("the other side closed the connection"); }
            catch (OperationCanceledException) { Fail("closed"); }
            catch (IOException error) { Fail(error.InnerException?.Message ?? error.Message); }
            catch (ObjectDisposedException) { Fail("closed"); }
        }

        public async Task WriteLoop()
        {
            try
            {
                await foreach (var frame in Outgoing.Reader.ReadAllAsync(owner._closing.Token).ConfigureAwait(false))
                {
                    Interlocked.Decrement(ref Queued);
                    await _stream.WriteAsync(frame, owner._closing.Token).ConfigureAwait(false);
                    Interlocked.Decrement(ref Writing);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException error) { Fail(error.InnerException?.Message ?? error.Message); }
            catch (ObjectDisposedException) { }
        }

        private void Fail(string reason) => Close(reason, announce: true);

        public void Close(string reason, bool announce)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            if (announce) owner.Lost(this, reason);

            // ⚠ The queue is completed first so whatever is already written still goes, then the
            // socket. A refusal ("this host is full") is useless if the close outruns it.
            Outgoing.Writer.TryComplete();
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(250).ConfigureAwait(false); } catch { }
                try { client.Client.Shutdown(SocketShutdown.Both); } catch { }
                client.Dispose();
            });
        }
    }
}
