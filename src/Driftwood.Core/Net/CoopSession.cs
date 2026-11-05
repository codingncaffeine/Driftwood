using System.Numerics;

namespace Driftwood.Core.Net;

/// <summary>Another player in the session, as this machine sees them.</summary>
/// <remarks>
/// Drawn a little in the past (<see cref="Delay"/>) between the two poses either side of that
/// moment, so a player who sends twenty poses a second still moves smoothly at any frame rate and
/// one lost pose costs nothing visible.
/// </remarks>
public sealed class RemotePlayer(int id, string name)
{
    public const double Delay = 0.1;

    public int Id { get; } = id;
    public string Name { get; } = name;

    private readonly List<(double At, PoseMessage Pose)> _poses = [];

    public PoseMessage? Latest => _poses.Count > 0 ? _poses[^1].Pose : null;

    public void Heard(double now, PoseMessage pose)
    {
        _poses.Add((now, pose));
        if (_poses.Count > 16) _poses.RemoveAt(0);
    }

    /// <summary>Where to draw them at <paramref name="now"/>, or null before the first pose.</summary>
    public PoseMessage? Sample(double now)
    {
        if (_poses.Count == 0) return null;
        var at = now - Delay;
        if (at <= _poses[0].At) return _poses[0].Pose;

        for (var i = _poses.Count - 1; i > 0; i--)
        {
            var (t0, a) = _poses[i - 1];
            var (t1, b) = _poses[i];
            if (at < t0) continue;
            if (at >= t1) return b;

            var f = (float)((at - t0) / Math.Max(t1 - t0, 1e-6));
            return b with
            {
                Position = Vector3.Lerp(a.Position, b.Position, f),
                Yaw = LerpAngle(a.Yaw, b.Yaw, f),
                Pitch = a.Pitch + (b.Pitch - a.Pitch) * f,
            };
        }

        return _poses[^1].Pose;
    }

    private static float LerpAngle(float a, float b, float f)
    {
        var turn = MathF.Tau;
        var delta = (b - a) % turn;
        if (delta > MathF.PI) delta -= turn;
        else if (delta < -MathF.PI) delta += turn;
        return a + delta * f;
    }
}

/// <summary>What the host's game hands the session: the world it is running, by callback.</summary>
public interface ICoopWorld
{
    string WorldName { get; }
    string Seed { get; }
    int TerrainVersion { get; }
    float DayTime { get; }
    Vector3 Spawn { get; }

    /// <summary>The world as a save would hold it, without the host's own character in it.</summary>
    byte[] WriteWorld();

    /// <summary>Applies cells a guest changed. Returns the ones accepted, which are then passed on.</summary>
    IReadOnlyList<(int X, int Y, int Z, ushort Block)> AcceptCells(int guest, Vector3 guestAt, IReadOnlyList<(int X, int Y, int Z, ushort Block)> cells);
}

/// <summary>Every check a hello has to pass, named the same on both ends.</summary>
public readonly record struct BuildIdentity(string GameVersion, string Registry)
{
    /// <summary>
    /// This build: its version, and a fingerprint of every block and item name in registration
    /// order — the numbers the wire carries mean the same thing on both ends only when this matches.
    /// </summary>
    public static BuildIdentity Of(string gameVersion, Blocks.BlockRegistry blocks, Items.ItemRegistry items)
    {
        var names = new System.Text.StringBuilder();
        for (var id = 0; id < blocks.Count; id++) names.Append(blocks[(ushort)id].Name).Append('\n');
        names.Append('\u0001');
        foreach (var item in items.All) names.Append(item.Name).Append('\n');
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(names.ToString()));
        return new BuildIdentity(gameVersion, Convert.ToHexStringLower(hash.AsSpan(0, 12)));
    }
}

/// <summary>
/// The host's side of a co-op session: admits guests, keeps their characters, passes the world on.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Player numbers are transport peer numbers, and the host is 0.</b> A pose or a cell
/// from a guest is stamped with that guest's number by the host, never trusted from the message.</para>
/// <para>Everything happens in <see cref="Poll"/>, on the game thread, once a frame.</para>
/// </remarks>
public sealed class CoopHost : IDisposable
{
    public const int MaxGuests = 7;

    private readonly INetTransport _link;
    private readonly ICoopWorld _world;
    private readonly GuestRecords _records;
    private readonly BuildIdentity _build;
    private readonly Dictionary<int, Guest> _guests = [];
    private double _sinceClock;

    public string HostName { get; }

    /// <summary>A world opened with a code lets in only guests who give it (normalized, see <see cref="JoinCodes"/>).</summary>
    public string? Code { get; set; }

    /// <summary>Totals for the leave line in the log: what actually crossed the wire.</summary>
    public int PosesHeard { get; private set; }
    public int CellsHeard { get; private set; }
    public int CellsSent { get; private set; }

    public CoopHost(INetTransport link, ICoopWorld world, GuestRecords records, BuildIdentity build, string hostName)
    {
        _link = link;
        _world = world;
        _records = records;
        _build = build;
        HostName = hostName;
    }

    /// <summary>Guests who have been welcomed, by number.</summary>
    public IReadOnlyDictionary<int, RemotePlayer> Players => _players;

    private readonly Dictionary<int, RemotePlayer> _players = [];

    /// <summary>Things worth telling the player: "Ash joined", "Ash left (timed out)".</summary>
    public Queue<string> Notices { get; } = new();

    /// <summary>Chat lines heard, as (who, text).</summary>
    public Queue<(string Who, string Text)> ChatHeard { get; } = new();

    public void Poll(double now, float dt)
    {
        while (_link.TryPoll(out var happened))
        {
            switch (happened.Kind)
            {
                case NetEventKind.Connected:
                    _guests[happened.Peer] = new Guest();
                    break;

                case NetEventKind.Disconnected:
                    Drop(happened.Peer, happened.Reason ?? "left");
                    break;

                case NetEventKind.Message:
                    try
                    {
                        Heard(now, happened.Peer, happened.Data!);
                    }
                    catch (Exception error) when (error is InvalidDataException or EndOfStreamException or IOException)
                    {
                        _link.Disconnect(happened.Peer, "sent something unreadable");
                        Drop(happened.Peer, "sent something unreadable");
                    }
                    break;
            }
        }

        _sinceClock += dt;
        if (_sinceClock >= 2.0 && _players.Count > 0)
        {
            _sinceClock = 0;
            Broadcast(SimpleMessages.Clock(_world.DayTime), Delivery.Reliable);
        }
    }

    private void Heard(double now, int peer, byte[] message)
    {
        if (!_guests.TryGetValue(peer, out var guest)) return;
        var (kind, r) = Protocol.Open(message);

        if (kind == Msg.Hello)
        {
            Admit(peer, guest, HelloMessage.Decode(r));
            return;
        }

        // Nothing but a hello is listened to until the hello has been accepted.
        if (!_players.TryGetValue(peer, out var player)) return;

        switch (kind)
        {
            case Msg.Pose:
            {
                var pose = PoseMessage.Decode(r) with { Id = peer };
                player.Heard(now, pose);
                PosesHeard++;
                Broadcast(pose.Encode(), Delivery.Unreliable, except: peer);
                break;
            }
            case Msg.Cells:
            {
                var asked = CellsMessage.Decode(r).Cells;
                CellsHeard += asked.Count;
                var at = player.Latest?.Position ?? _world.Spawn;
                var accepted = _world.AcceptCells(peer, at, asked);
                if (accepted.Count > 0) Broadcast(new CellsMessage(accepted).Encode(), Delivery.Reliable, except: peer);
                break;
            }
            case Msg.Character:
                _records.Keep(guest.Key, r.ReadBlob());
                break;

            case Msg.Bye:
                _link.Disconnect(peer, "left");
                Drop(peer, "left");
                break;

            case Msg.Chat:
            {
                r.ReadInt32();
                var text = r.ReadString();
                ChatHeard.Enqueue((player.Name, text));
                Broadcast(SimpleMessages.Chat(peer, text), Delivery.Reliable, except: peer);
                break;
            }
        }
    }

    private void Admit(int peer, Guest guest, HelloMessage hello)
    {
        string? refusal = null;
        if (hello.ProtocolVersion != Protocol.Version || hello.GameVersion != _build.GameVersion)
            refusal = $"this host is running Driftwood {_build.GameVersion} and you are on {hello.GameVersion} — both need the same version";
        else if (hello.Registry != _build.Registry)
            refusal = "this host's blocks do not match yours — both need the same version of Driftwood";
        else if (Code is not null && JoinCodes.Read(hello.Code) != Code)
            refusal = hello.Code.Length == 0
                ? "this world is open by join code only — ask the host for it"
                : "that join code is not this world's";
        else if (_players.Count >= MaxGuests)
            refusal = "this world is full";
        else if (!GuestRecords.IsKey(hello.PlayerKey))
            refusal = "your player key is not readable";
        else if (_guests.Any(pair => pair.Key != peer && pair.Value.Key == hello.PlayerKey))
            refusal = "you are already in this world";

        if (refusal is not null)
        {
            _link.Send(peer, SimpleMessages.Refuse(refusal), Delivery.Reliable);
            _link.Disconnect(peer, refusal);
            _guests.Remove(peer);
            return;
        }

        guest.Key = hello.PlayerKey;
        var name = PlayerNames.Clean(hello.Name, fallback: $"Player {peer}");
        var player = new RemotePlayer(peer, name);

        var roster = new List<RosterEntry> { new(0, HostName) };
        roster.AddRange(_players.Values.Select(p => new RosterEntry(p.Id, p.Name)));

        var welcome = new WelcomeMessage(
            peer, _world.WorldName, _world.Seed, _world.TerrainVersion, _world.DayTime, _world.Spawn,
            Protocol.Pack(_world.WriteWorld()), _records.Find(hello.PlayerKey) ?? [], roster);

        _link.Send(peer, welcome.Encode(), Delivery.Reliable);
        Broadcast(SimpleMessages.Joined(new RosterEntry(peer, name)), Delivery.Reliable);
        _players[peer] = player;
        Notices.Enqueue($"{name} joined");
    }

    private void Drop(int peer, string reason)
    {
        _guests.Remove(peer);
        if (!_players.Remove(peer, out var player)) return;
        Broadcast(SimpleMessages.Left(peer), Delivery.Reliable);
        Notices.Enqueue(reason == "left" ? $"{player.Name} left" : $"{player.Name} left ({reason})");
    }

    /// <summary>The host's own pose, to every guest.</summary>
    public void SendPose(PoseMessage pose) => Broadcast((pose with { Id = 0 }).Encode(), Delivery.Unreliable);

    /// <summary>Cells the host's world changed, to every guest.</summary>
    public void SendCells(IReadOnlyList<(int X, int Y, int Z, ushort Block)> cells)
    {
        if (cells.Count == 0 || _players.Count == 0) return;
        Broadcast(new CellsMessage(cells).Encode(), Delivery.Reliable);
        CellsSent += cells.Count;
    }

    public void SendChat(string text)
    {
        ChatHeard.Enqueue((HostName, text));
        Broadcast(SimpleMessages.Chat(0, text), Delivery.Reliable);
    }

    private void Broadcast(byte[] message, Delivery delivery, int except = -1)
    {
        foreach (var peer in _players.Keys)
            if (peer != except) _link.Send(peer, message, delivery);
    }

    public void Dispose() => _link.Dispose();

    private sealed class Guest
    {
        public string Key = "";
    }
}

/// <summary>The guest's side: dials, says hello, opens the host's world, and keeps in step with it.</summary>
public sealed class CoopGuest : IDisposable
{
    private readonly INetTransport _link;
    private readonly HelloMessage _hello;
    private readonly Dictionary<int, RemotePlayer> _players = [];

    public CoopGuest(INetTransport link, HelloMessage hello)
    {
        _link = link;
        _hello = hello;
        _link.Send(INetTransport.HostPeer, hello.Encode(), Delivery.Reliable);
    }

    /// <summary>Set once the host has let us in. Taken by the game to open the world.</summary>
    public WelcomeMessage? Welcome { get; private set; }

    /// <summary>Why the session ended, once it has. Null while it is alive.</summary>
    public string? Ended { get; private set; }

    public int MyId => Welcome?.YourId ?? -1;

    public IReadOnlyDictionary<int, RemotePlayer> Players => _players;

    /// <summary>Cells the host says changed, in order, for the game to apply.</summary>
    public Queue<(int X, int Y, int Z, ushort Block)> CellsHeard { get; } = new();

    public float? DayTimeHeard { get; set; }

    /// <summary>Totals for the leave line in the log: what actually crossed the wire.</summary>
    public int PosesHeard { get; private set; }
    public int CellsHeardTotal { get; private set; }
    public int CellsSent { get; private set; }

    public Queue<string> Notices { get; } = new();

    public Queue<(string Who, string Text)> ChatHeard { get; } = new();

    public void Poll(double now)
    {
        while (_link.TryPoll(out var happened))
        {
            switch (happened.Kind)
            {
                case NetEventKind.Disconnected:
                    Ended ??= Welcome is null
                        ? $"the host closed the connection ({happened.Reason})"
                        : $"lost the host ({happened.Reason})";
                    break;

                case NetEventKind.Message:
                    try
                    {
                        Heard(now, happened.Data!);
                    }
                    catch (Exception error) when (error is InvalidDataException or EndOfStreamException or IOException)
                    {
                        Ended ??= "the host sent something unreadable";
                        _link.Disconnect(INetTransport.HostPeer, "unreadable");
                    }
                    break;
            }
        }
    }

    private void Heard(double now, byte[] message)
    {
        var (kind, r) = Protocol.Open(message);
        switch (kind)
        {
            case Msg.Refuse:
                Ended = r.ReadString();
                break;

            case Msg.Welcome:
                Welcome = WelcomeMessage.Decode(r);
                foreach (var entry in Welcome.Roster) _players[entry.Id] = new RemotePlayer(entry.Id, entry.Name);
                break;

            case Msg.PlayerJoined:
            {
                var id = r.ReadInt32();
                var name = r.ReadString();
                if (id == MyId) break;
                _players[id] = new RemotePlayer(id, name);
                Notices.Enqueue($"{name} joined");
                break;
            }
            case Msg.PlayerLeft:
            {
                var id = r.ReadInt32();
                if (_players.Remove(id, out var gone)) Notices.Enqueue($"{gone.Name} left");
                break;
            }
            case Msg.Pose:
            {
                var pose = PoseMessage.Decode(r);
                if (_players.TryGetValue(pose.Id, out var player))
                {
                    player.Heard(now, pose);
                    PosesHeard++;
                }
                break;
            }
            case Msg.Cells:
                foreach (var cell in CellsMessage.Decode(r).Cells)
                {
                    CellsHeard.Enqueue(cell);
                    CellsHeardTotal++;
                }
                break;

            case Msg.Clock:
                DayTimeHeard = r.ReadSingle();
                break;

            case Msg.Chat:
            {
                var from = r.ReadInt32();
                var text = r.ReadString();
                ChatHeard.Enqueue((_players.TryGetValue(from, out var who) ? who.Name : "?", text));
                break;
            }
        }
    }

    public void SendPose(PoseMessage pose) => _link.Send(INetTransport.HostPeer, pose.Encode(), Delivery.Unreliable);

    /// <summary>Says goodbye, then waits, up to a limit, for everything sent to leave this machine.</summary>
    public void Leave(TimeSpan limit)
    {
        if (Ended is null) _link.Send(INetTransport.HostPeer, SimpleMessages.Bye(), Delivery.Reliable);
        _link.Drain(limit);
    }

    public void SendCells(IReadOnlyList<(int X, int Y, int Z, ushort Block)> cells)
    {
        if (cells.Count == 0) return;
        _link.Send(INetTransport.HostPeer, new CellsMessage(cells).Encode(), Delivery.Reliable);
        CellsSent += cells.Count;
    }

    public void SendCharacter(byte[] record) =>
        _link.Send(INetTransport.HostPeer, SimpleMessages.Character(record), Delivery.Reliable);

    public void SendChat(string text)
    {
        ChatHeard.Enqueue((_hello.Name, text));
        _link.Send(INetTransport.HostPeer, SimpleMessages.Chat(0, text), Delivery.Reliable);
    }

    public void Dispose() => _link.Dispose();
}

/// <summary>Player names as other people will see them.</summary>
public static class PlayerNames
{
    public static string Clean(string? name, string fallback)
    {
        var kept = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (kept.Length > 24) kept = kept[..24].TrimEnd();
        return kept.Length == 0 ? fallback : kept;
    }
}
