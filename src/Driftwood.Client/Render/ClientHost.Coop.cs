using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;
using Driftwood.Core.Items;
using Driftwood.Core.Net;
using Driftwood.Core.Physics;
using Driftwood.Core.Saves;
using Driftwood.Core.Settings;
using Driftwood.Core.Textures;
using Driftwood.Core.Ui;

namespace Driftwood.Client.Render;

/// <summary>
/// Co-op (2026-11-04): hosting a world for friends, or playing in theirs.
/// </summary>
/// <remarks>
/// <para>⛳ <b>The user's model.</b> The host's machine IS the world: it runs the game exactly as it
/// does alone, saves the world, and keeps each guest's character under that guest's key so a guest
/// who comes back carries on. A guest opens the host's world from the bytes the host sends — through
/// the save's own reader — generates the terrain itself from the seed, and switches off everything
/// the host owns (creatures, fire, wiring, growth, containers) so there is only ever one answer.</para>
/// <para>See <c>local-notes/MULTIPLAYER-PLAN.md</c> for the phases; this file is N3.</para>
/// </remarks>
public sealed partial class ClientHost
{
    /// <summary>How often a pose is sent: twenty a second, drawn a tenth of a second behind.</summary>
    private const double PoseInterval = 0.05;

    /// <summary>How often a guest hands the host its character, besides on leaving.</summary>
    private const double CharacterInterval = 10.0;

    private CoopHost? _coopHost;
    private CoopGuest? _coopGuest;
    private WelcomeMessage? _welcome;
    private string? _joinFault;
    private readonly List<(int X, int Y, int Z, ushort Block)> _outgoingCells = [];
    private readonly object _outgoingLock = new();
    private bool _applyingRemote;
    private readonly Dictionary<int, PlayerAnimator> _remoteAnimators = [];
    private PlayerRenderer? _remoteRenderer;
    private double _sincePose;
    private double _sinceCharacter;
    private bool _coopEnded;
    private float _coopFrameDt;

    /// <summary>A guest in somebody else's world: the systems the host owns stay off here.</summary>
    private bool IsGuest => _coopGuest is not null;

    /// <summary>Started by Program before the window opens, so the handshake runs beside the loading.</summary>
    private static Task<(CoopGuest? Guest, string? Fault)>? s_pendingJoin;

    /// <summary>What this machine calls itself to other players.</summary>
    private static string CoopName(ClientOptions options) =>
        PlayerNames.Clean(options.PlayerName ?? Environment.UserName, fallback: "Player");

    /// <summary>This player's name: <c>--name</c>, then the setting, then the login name.</summary>
    private string MyCoopName() =>
        PlayerNames.Clean(
            _options.PlayerName ?? (_settings.PlayerName.Length > 0 ? _settings.PlayerName : Environment.UserName),
            fallback: "Player");

    /// <summary>
    /// Dials the host and waits for its welcome, off the game thread. The window loads meanwhile.
    /// </summary>
    public static void BeginJoin(ClientOptions options)
    {
        var address = options.JoinAddress!;
        var registry = new BlockRegistry();
        StarterBlocks.Register(registry);
        var items = StarterItems.Register(registry);
        var build = BuildIdentity.Of(ProductVersion, registry, items);
        var key = PlayerKey.Load(UserFolders.Config);
        var name = CoopName(options);

        s_pendingJoin = Task.Run(async () =>
        {
            Console.WriteLine($"co-op       joining {address} as {name}");
            INetTransport link;
            try
            {
                if (EpicAddressIn(address) is { } epicHost)
                {
                    // Signing in and finding a way between the machines can take a while on a slow link.
                    using var meeting = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    link = await ConnectThroughEpic(epicHost, name, meeting.Token).ConfigureAwait(false);
                }
                else
                {
                    using var dialing = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    link = await TcpTransport.ConnectAsync(address, dialing.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                return ((CoopGuest?)null, (string?)(EpicAddressIn(address) is not null
                    ? $"could not reach your friend through Epic: {error.GetBaseException().Message}"
                    : $"could not reach {address}: {error.Message}"));
            }

            var guest = new CoopGuest(link, new HelloMessage(Protocol.Version, build.GameVersion, build.Registry, key, name));
            var clock = Stopwatch.StartNew();
            while (guest.Welcome is null && guest.Ended is null && clock.Elapsed < TimeSpan.FromSeconds(60))
            {
                guest.Poll(0);
                await Task.Delay(10).ConfigureAwait(false);
            }

            if (guest.Welcome is not null) return (guest, null);
            var why = guest.Ended ?? "the host did not answer";
            guest.Dispose();
            return (null, why);
        });
    }

    /// <summary>From <see cref="OpenWorld"/>: the world is the host's, if the host let us in.</summary>
    /// <returns>True when the joined world is what opens; false falls back to playing alone.</returns>
    private bool OpenJoinedWorld()
    {
        if (s_pendingJoin is null) return false;

        var (guest, fault) = s_pendingJoin.GetAwaiter().GetResult();
        s_pendingJoin = null;

        if (guest?.Welcome is not { } welcome)
        {
            _joinFault = fault ?? "the host did not answer";
            Console.Error.WriteLine($"driftwood: could not join: {_joinFault}");
            return false;
        }

        _coopGuest = guest;
        _welcome = welcome;
        _seed = Core.Gen.WorldSeed.Parse(welcome.Seed);
        _terrainVersion = welcome.TerrainVersion;
        _savedDayTime = welcome.DayTime;
        _playedBefore = 0;
        _loadedWorld = true;

        // ⛔ No name, which is what keeps every save path from writing the host's world to this disk.
        _worldName = "";

        Console.WriteLine(
            $"co-op       joined '{welcome.WorldName}' (seed {welcome.Seed}) as player {welcome.YourId}; "
            + $"{welcome.World.Length:N0} bytes of world, "
            + (welcome.Character.Length > 0 ? "your character from last time" : "a first visit"));
        return true;
    }

    /// <summary>From <see cref="LoadWorld"/>: the host's world, then this guest's own character on top.</summary>
    private string? ReadJoinedWorld(WorldState state, List<string> missing)
    {
        var welcome = _welcome!;
        state.Position = welcome.Spawn;

        if (WorldSave.Read(Protocol.Unpack(welcome.World), _registry, _items, state, missing) is { } worldFault)
            return worldFault;

        // ⚠ The host's world carries no character, so on a first visit health and pockets stay as a
        // new player's — and the spawn above stands, beside the host.
        if (welcome.Character.Length > 0
            && WorldSave.Read(welcome.Character, _registry, _items, state, missing) is { } characterFault)
            return characterFault;

        return null;
    }

    /// <summary>Opens this world to friends on a port. From the menu or <c>--host</c>.</summary>
    private void StartHosting(int port)
    {
        if (_coopHost is not null || IsGuest) return;
        if (_worldName.Length == 0)
        {
            PushToast("Cannot host", "This world has no name to keep it under");
            return;
        }

        try
        {
            var tcp = TcpTransport.Listen(port);
            _hostPort = tcp.Port;
            var link = HostLinks(tcp);
            var records = new GuestRecords(GuestRecords.FolderFor(WorldSave.PathFor(_worldName)));
            _coopHost = new CoopHost(
                link, new HostedWorld(this), records, BuildIdentity.Of(ProductVersion, _registry, _items),
                MyCoopName());
            Console.WriteLine(
                $"co-op       hosting '{_worldName}' on port {tcp.Port}; {records.Count} visitors remembered");
            PushToast("Open to friends", s_meeting is null ? $"Port {tcp.Port}" : $"Port {tcp.Port}, and listed on Epic");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"driftwood: could not open the world to friends: {error.Message}");
            PushToast("Cannot host", error.Message);
        }
    }

    /// <summary>Every cell the world changes, as it changes — the hook both roles send from.</summary>
    private void OnWorldEdited(int x, int y, int z, BlockId id)
    {
        if (_applyingRemote || (_coopHost is null && _coopGuest is null)) return;
        lock (_outgoingLock) _outgoingCells.Add((x, y, z, id.Value));
    }

    /// <summary>Once a frame, from <see cref="OnUpdate"/>.</summary>
    private void StepCoop(double dt)
    {
        _coopFrameDt = (float)dt;
        StepEpic(dt);
        if (_joinFault is { } fault)
        {
            _joinFault = null;
            PushToast("Could not join", fault);
        }

        if (_coopHost is { } host)
        {
            host.Poll(_elapsed, (float)dt);
            while (host.Notices.TryDequeue(out var notice)) Notice(notice);
            while (host.ChatHeard.TryDequeue(out var line)) Console.WriteLine($"chat        {line.Who}: {line.Text}");

            host.SendCells(TakeOutgoing());
            if (Due(ref _sincePose, PoseInterval, dt)) host.SendPose(MyPose());
            return;
        }

        if (_coopGuest is not { } guest) return;

        guest.Poll(_elapsed);
        while (guest.Notices.TryDequeue(out var notice)) Notice(notice);
        while (guest.ChatHeard.TryDequeue(out var line)) Console.WriteLine($"chat        {line.Who}: {line.Text}");

        if (guest.Ended is { } why)
        {
            if (!_coopEnded)
            {
                _coopEnded = true;
                Console.Error.WriteLine($"driftwood: co-op ended: {why}");
                PushToast("Left the world", why);
                ReturnToMenuAfterCoop();
            }
            return;
        }

        if (guest.CellsHeard.Count > 0)
        {
            _applyingRemote = true;
            try
            {
                while (guest.CellsHeard.TryDequeue(out var cell))
                    if (cell.Block < _registry.Count)
                        _streamer.ApplyRemote(cell.X, cell.Y, cell.Z, new BlockId(cell.Block));
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        if (guest.DayTimeHeard is { } day)
        {
            guest.DayTimeHeard = null;
            _clock.SetTime(day);
        }

        guest.SendCells(TakeOutgoing());
        if (Due(ref _sincePose, PoseInterval, dt) && _spawned) guest.SendPose(MyPose());
        if (Due(ref _sinceCharacter, CharacterInterval, dt) && _spawned) SendCoopCharacter();
    }

    private void Notice(string notice)
    {
        Console.WriteLine($"co-op       {notice}");
        PushToast("Co-op", notice);
    }

    private static bool Due(ref double since, double interval, double dt)
    {
        since += dt;
        if (since < interval) return false;
        since = 0;
        return true;
    }

    private List<(int X, int Y, int Z, ushort Block)> TakeOutgoing()
    {
        lock (_outgoingLock)
        {
            if (_outgoingCells.Count == 0) return [];
            var taken = new List<(int, int, int, ushort)>(_outgoingCells);
            _outgoingCells.Clear();
            return taken;
        }
    }

    private PoseMessage MyPose()
    {
        var flags = PoseFlags.None;
        if (_player.Sneaking) flags |= PoseFlags.Sneaking;
        if (_animator.Swinging) flags |= PoseFlags.Swinging;
        if (_player.InWater) flags |= PoseFlags.Swimming;
        return new PoseMessage(0, _player.Position, _camera.Yaw, _camera.Pitch, flags, _inventory.HeldType?.Name ?? "");
    }

    /// <summary>A guest's character, as the host is to keep it.</summary>
    private void SendCoopCharacter()
    {
        if (_coopGuest is not { } guest || guest.Ended is not null) return;
        _sinceCharacter = 0;
        guest.SendCharacter(WorldSave.WriteBytes("guest", CaptureState(), SaveParts.Character));
    }

    /// <summary>The other players, everyone but this machine, in the third-person model.</summary>
    private void DrawRemotePlayers(Matrix4x4 viewProj, in SkyParams sky, float dt)
    {
        _hudScreen.NameTags.Clear();
        var players = _coopHost?.Players ?? _coopGuest?.Players;
        if (players is null || players.Count == 0) return;
        var frame = _window.FramebufferSize;

        _remoteRenderer ??= new PlayerRenderer(_gl, PlayerSkin.Paint(ArmStyle.Classic));

        foreach (var (id, player) in players)
        {
            if (player.Sample(_elapsed) is not { } seen) continue;

            if (!_remoteAnimators.TryGetValue(id, out var animator))
            {
                _remoteAnimators[id] = animator = new PlayerAnimator();
                animator.Reset(seen.Yaw);
            }

            var sneaking = (seen.Flags & PoseFlags.Sneaking) != 0;
            animator.Update(dt, seen.Position, seen.Yaw, PlayerBody.WalkSpeed, sneaking,
                (seen.Flags & PoseFlags.Swinging) != 0);
            var pose = animator.Pose(seen.Yaw, seen.Pitch);
            var light = SampleLight(seen.Position + new Vector3(0f, 1.6f, 0f));

            _remoteRenderer.DrawWorld(viewProj, _viewPosition, sky, light, seen.Position, pose);

            // The name over the head: projected here, drawn by the HUD. Hidden while sneaking (the
            // genre's rule, so a hiding friend stays hidden) and past a lane's length.
            var head = seen.Position + new Vector3(0f, sneaking ? 1.9f : 2.15f, 0f);
            if (!sneaking && Vector3.DistanceSquared(head, _viewPosition) < 64f * 64f)
            {
                var clip = Vector4.Transform(new Vector4(head, 1f), viewProj);
                if (clip.W > 0.1f)
                {
                    var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
                    if (MathF.Abs(ndc.X) < 1.1f && MathF.Abs(ndc.Y) < 1.1f)
                        _hudScreen.NameTags.Add((
                            new Vector2((ndc.X * 0.5f + 0.5f) * frame.X, (0.5f - ndc.Y * 0.5f) * frame.Y),
                            player.Name));
                }
            }

            if (seen.Held.Length > 0 && _items.TryByName(seen.Held, out var carried))
            {
                _blockTextures.Bind();
                _itemRenderer.DrawInHand(
                    viewProj,
                    _remoteRenderer.HeldWorldTransform(
                        seen.Position, pose, !carried.DrawsAsBlock,
                        _itemRenderer.HoldPoint(carried), axe: carried.Tool == ToolClass.Axe),
                    carried, _registry, HandLight(light));
            }
        }

        // Forget the animators of players who have gone.
        foreach (var id in _remoteAnimators.Keys.Where(id => !players.ContainsKey(id)).ToList())
            _remoteAnimators.Remove(id);
    }

    /// <summary>
    /// Containers are the host's (N5): until they are shared, a guest is told so rather than shown
    /// a chest that only exists on its own screen.
    /// </summary>
    private bool GuestCannot(string what)
    {
        if (!IsGuest) return false;
        PushToast("Kept by the host", $"{what} are not shared in co-op yet");
        return true;
    }

    /// <summary>Leaves co-op cleanly: a guest hands its character over first.</summary>
    private void LeaveCoop()
    {
        if (_coopGuest is { } guest)
        {
            Console.WriteLine(
                $"co-op       leaving: heard {guest.PosesHeard} poses and {guest.CellsHeardTotal} cells, "
                + $"sent {guest.CellsSent} cells");
            if (guest.Ended is null && _spawned) SendCoopCharacter();
            guest.Leave(TimeSpan.FromSeconds(2));
            guest.Dispose();
            _coopGuest = null;
        }

        _shotFriend?.Dispose();
        _shotFriend = null;

        if (_coopHost is { } host)
        {
            Console.WriteLine(
                $"co-op       closing: heard {host.PosesHeard} poses and {host.CellsHeard} cells from guests, "
                + $"sent {host.CellsSent} cells");
            host.Dispose();
            _coopHost = null;
        }

        CloseEpicRoom();
    }

    /// <summary>The host is gone: back to this machine's own start screen, by the relaunch every world change uses.</summary>
    private void ReturnToMenuAfterCoop()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;

        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        var was = Environment.GetCommandLineArgs();
        for (var i = 1; i < was.Length; i++)
        {
            if (was[i] is "--join" or "--world" or "--seed") { i++; continue; }
            start.ArgumentList.Add(was[i]);
        }

        try
        {
            Process.Start(start);
            _stopRequested = true;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"driftwood: could not return to the menu: {error.Message}");
        }
    }

    // ─── The menus ────────────────────────────────────────────────────────────────────────────────

    private const string FriendsRow = "Multiplayer";
    private const string CoopNameRow = "Your Name";
    private const string JoinAddressRow = "Friend's Address";
    private const string JoinRow = "Join";
    private const string OpenToFriendsRow = "Open to Friends";
    private const string OpenYourWorldRow = "Open Your World";

    private bool _startFriends;
    private int _hostPort;
    private readonly TextField _coopNameBox = new(24) { Placeholder = "enter name" };
    private readonly TextField _joinBox = new(80) { Placeholder = "enter address" };

    private void OpenFriendsPage()
    {
        _coopNameBox.Text = MyCoopName();
        _joinBox.Text = _settings.JoinAddress;
        Meeting(MyCoopName());
        _startFriends = true;
        _startBrowsing = false;
        _startListing = false;
        _startCreating = false;
        _hudScreen.Selected = 1;
        _hudScreen.Scroll = 0;
        RefreshScreen();
        ShowSelectedRow();
    }

    private void BuildFriendsRows()
    {
        // ⛳ All the user's own planks (2026-11-04: "more buttons ... to replace the old ones"): two
        // fields with their labels carved in, then Join, Open Your World and Back.
        _hudScreen.Rows.Add(new MenuRow(FriendsRow, Heading: true));
        _hudScreen.Rows.Add(new MenuRow(
            CoopNameRow, Edits: _coopNameBox, Art: MenuButtonArt.FieldName,
            Note: "The name over your head that other players see"));
        _hudScreen.Rows.Add(new MenuRow(
            JoinAddressRow, Edits: _joinBox, Art: MenuButtonArt.FieldAddress,
            Note: "The address your friend's game shows when they open their world, like 192.168.1.20 or name:port"));
        _hudScreen.Rows.Add(new MenuRow(
            JoinRow, "", Art: MenuButtonArt.Join,
            Note: _joinBox.Text.Trim().Length == 0
                ? s_meeting is not null || !s_meetingTried
                    ? "Shows the worlds your friends have open. Or type an address above to join one directly"
                    : "Type your friend's address first"
                : _worldName.Length > 0 && _loadedWorld
                    ? $"Saves {_worldName} first, then joins {_joinBox.Text.Trim()}"
                    : $"Joins {_joinBox.Text.Trim()}"));
        _hudScreen.Rows.Add(new MenuRow(
            OpenYourWorldRow, "", Art: MenuButtonArt.OpenWorld,
            Note: _worldName.Length > 0
                ? $"Plays {_worldName} and opens it to friends at once. Your world, and every visitor's things, stay on your machine"
                : "This world isn't being kept, so it can't be opened to friends"));
        _hudScreen.Rows.Add(new MenuRow(BackRow, "", Art: MenuButtonArt.Back, Note: "Back to the main menu"));
    }

    private void ChooseOnFriendsPage(string label)
    {
        switch (label)
        {
            case CoopNameRow:
                StartTyping(_coopNameBox, accepted =>
                {
                    if (!accepted) return;
                    _settings.PlayerName = PlayerNames.Clean(_coopNameBox.Text, fallback: "");
                    _settingsDirty = true;
                });
                return;
            case JoinAddressRow:
                StartTyping(_joinBox, accepted =>
                {
                    if (!accepted) return;
                    _settings.JoinAddress = _joinBox.Text.Trim();
                    _settingsDirty = true;
                });
                return;
            case JoinRow:
                // ⛳ No address typed: pick a friend's world from Epic's list instead.
                if (_joinBox.Text.Trim().Length == 0 && Meeting(MyCoopName()) is not null) OpenBrowsePage();
                else JoinFromMenu(_joinBox.Text.Trim());
                return;
            case OpenYourWorldRow:
                if (_worldName.Length == 0)
                {
                    ShowSelectedRow();
                    return;
                }
                _startFriends = false;
                StartPlaying();
                StartHosting(TcpTransport.DefaultPort);
                return;
            case BackRow:
                _startFriends = false;
                BackToStartMenu(FriendsRow);
                return;
        }
    }

    /// <summary>
    /// Saves what is open and starts again as a guest — the relaunch every world change uses, with
    /// <c>--join</c> where <c>--world</c> would be.
    /// </summary>
    private void JoinFromMenu(string address)
    {
        if (address.Length == 0)
        {
            ShowSelectedRow();
            return;
        }

        var exe = Environment.ProcessPath;
        if (exe is null) return;

        _settings.JoinAddress = address;
        _settings.PlayerName = PlayerNames.Clean(_coopNameBox.Text, fallback: "");
        _settings.Save();
        SaveWorld("before joining a friend");

        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        var was = Environment.GetCommandLineArgs();
        for (var i = 1; i < was.Length; i++)
        {
            if (was[i] is "--world" or "--seed" or "--play" or "--join" or "--name" or "--port") { i++; continue; }
            if (was[i] is "--host") continue;
            start.ArgumentList.Add(was[i]);
        }
        start.ArgumentList.Add("--join");
        start.ArgumentList.Add(address);
        start.ArgumentList.Add("--name");
        start.ArgumentList.Add(MyCoopName());

        try
        {
            Process.Start(start);
            _stopRequested = true;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"driftwood: could not join: {error.Message}");
        }
    }

    /// <summary>The pause menu's co-op section: open to friends, or who is here.</summary>
    private void BuildCoopMenuRows()
    {
        if (_coopGuest is { } guest)
        {
            _hudScreen.Rows.Add(new MenuRow("friends", Heading: true));
            _hudScreen.Rows.Add(new MenuRow(
                "playing in", _welcome?.WorldName ?? "a friend's world",
                Note: "The host keeps this world and your things in it, ready for next time"));
            foreach (var player in guest.Players.Values)
                _hudScreen.Rows.Add(new MenuRow(player.Id == 0 ? $"{player.Name} (host)" : player.Name, "here"));
            return;
        }

        _hudScreen.Rows.Add(new MenuRow("friends", Heading: true));
        if (_coopHost is not { } host)
        {
            _hudScreen.Rows.Add(new MenuRow(
                OpenToFriendsRow, "",
                Note: _worldName.Length > 0
                    ? $"Lets friends join this world at your address, port {TcpTransport.DefaultPort}"
                    : "This world isn't being kept, so it can't be opened to friends"));
            return;
        }

        _hudScreen.Rows.Add(new MenuRow(
            "open at", string.Join("  ", LocalAddresses().Take(2).Select(a => _hostPort == TcpTransport.DefaultPort ? a : $"{a}:{_hostPort}")),
            Note: "Friends on your network type this. Over the internet, forward this port on your router to this machine"));
        if (_epicRoomStatus is { } epic)
            _hudScreen.Rows.Add(new MenuRow(
                "Epic", epic.StartsWith("listed") ? $"listed as {_worldName}" : epic,
                Note: "Friends anywhere pick this world from Multiplayer, Join — no address or port needed"));
        if (host.Players.Count == 0)
            _hudScreen.Rows.Add(new MenuRow("nobody yet", "", Note: "Players who join appear here"));
        foreach (var player in host.Players.Values)
            _hudScreen.Rows.Add(new MenuRow(player.Name, "here"));
    }

    /// <summary>This machine's addresses on its networks, IPv4 first, for the host to read out.</summary>
    private static IEnumerable<string> LocalAddresses()
    {
        var found = new List<(bool V6, string Text)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily == AddressFamily.InterNetwork)
                        found.Add((false, address.ToString()));
                }
            }
        }
        catch (NetworkInformationException) { }

        return found.Count > 0 ? found.Select(f => f.Text) : ["this machine's address"];
    }

    // ─── The shot instrument's friend ─────────────────────────────────────────────────────────────

    private CoopGuest? _shotFriend;

    /// <summary>For <c>--shot</c>: host, and let a guest in this same process join over the loopback.</summary>
    private void StartShotFriend()
    {
        StartHosting(0);
        if (_coopHost is null) return;

        var link = TcpTransport.ConnectAsync($"127.0.0.1:{_hostPort}", CancellationToken.None).GetAwaiter().GetResult();
        var build = BuildIdentity.Of(ProductVersion, _registry, _items);
        _shotFriend = new CoopGuest(link, new HelloMessage(Protocol.Version, build.GameVersion, build.Registry,
            new string('f', 32), "Birch"));
    }

    /// <summary>Keeps the shot's friend standing four blocks ahead, facing the camera.</summary>
    private void StepShotFriend()
    {
        if (_shotFriend is not { } friend) return;
        friend.Poll(_elapsed);
        var ahead = Vector3.Normalize(_camera.Forward with { Y = 0f });
        var at = _player.Position + ahead * 4f;
        at.Y = _player.Position.Y;
        friend.SendPose(new PoseMessage(0, at, _camera.Yaw + 180f, 0f, PoseFlags.None, "stone_pickaxe"));
    }

    /// <summary>The host's game, as the co-op session sees it.</summary>
    private sealed class HostedWorld(ClientHost game) : ICoopWorld
    {
        public string WorldName => game._worldName;
        public string Seed => game._seed.ToString();
        public int TerrainVersion => game._terrain.Version;
        public float DayTime => game._clock.TimeOfDay;

        /// <summary>Beside the host: where a first-time guest appears.</summary>
        public Vector3 Spawn => game._spawned ? game._player.Position + new Vector3(1f, 0.5f, 0f) : game._spawnPoint;

        public byte[] WriteWorld() => WorldSave.WriteBytes(game._worldName, game.CaptureState(), SaveParts.World);

        public IReadOnlyList<(int X, int Y, int Z, ushort Block)> AcceptCells(
            int guest, Vector3 guestAt, IReadOnlyList<(int X, int Y, int Z, ushort Block)> cells)
        {
            var accepted = new List<(int, int, int, ushort)>();
            foreach (var cell in cells)
            {
                if (cell.Block >= game._registry.Count) continue;
                if (!CoopRules.InReach(guestAt + new Vector3(0f, 1.6f, 0f), cell.X, cell.Y, cell.Z)) continue;

                // Applied the way the host's own hand applies one, so wiring and rails answer it here.
                // Everything that answer changes is sent to everyone by the edit hook; the guest's own
                // cell is taken back out of that, because the session passes it on to the others.
                int before;
                lock (game._outgoingLock) before = game._outgoingCells.Count;
                game._streamer.EditBlock(cell.X, cell.Y, cell.Z, new BlockId(cell.Block));
                lock (game._outgoingLock)
                {
                    var own = game._outgoingCells.FindIndex(before, c => c == cell);
                    if (own >= 0) game._outgoingCells.RemoveAt(own);
                }

                accepted.Add(cell);
            }

            return accepted;
        }
    }
}
