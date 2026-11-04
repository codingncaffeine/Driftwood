using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Driftwood.Core.Net;
using Epic.OnlineServices;
using Epic.OnlineServices.Connect;
using Epic.OnlineServices.Lobby;
using Epic.OnlineServices.Logging;
using Epic.OnlineServices.Platform;

namespace Driftwood.Net.Eos;

/// <summary>
/// Epic Online Services as the meeting place: an anonymous device sign-in, lobbies that list open
/// worlds, and P2P links (relayed by Epic when two machines cannot reach each other).
/// </summary>
/// <remarks>
/// <para>⛳ <b>One thread owns the SDK.</b> Epic's SDK is not thread-safe and only makes progress
/// while it is ticked, so every call — sign-in, lobbies, sending, receiving — runs on this place's
/// own thread, ticked every few milliseconds. The game thread only queues work and reads results,
/// which is what keeps <see cref="INetTransport"/>'s "nothing blocks the caller" true here too.</para>
/// <para>⛳ <b>No Epic account.</b> Connect's device-id login mints an identity for this install
/// (kept by the SDK in <c>cacheFolder</c>); Epic is told the player's name and the platform, nothing
/// about the computer.</para>
/// </remarks>
public sealed class EpicMeetingPlace : IMeetingPlace
{
    private const string Bucket = "Driftwood:coop";
    private const string AttrHost = "HOST";
    private const string AttrWorld = "WORLD";
    private const string AttrHostName = "HOSTNAME";
    private const string AttrPlayers = "PLAYERS";
    private const string AttrVersion = "VERSION";

    private static readonly object s_initLock = new();
    private static bool s_initialized;

    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stopping;
    private volatile MeetingState _state = MeetingState.Starting;
    private volatile string? _fault;
    private volatile string? _myAddress;

    // ⛔ Touched only on the SDK thread.
    private PlatformInterface? _platform;
    private ProductUserId? _me;
    private string? _lobbyId;
    internal EosLink? Link;

    public string Name => "Epic";

    public MeetingState State => _state;

    public string? Fault => _fault;

    public string? MyAddress => _myAddress;

    /// <summary>Called by <see cref="MeetingPlaces.Start"/> through reflection.</summary>
    public static IMeetingPlace? Start(string configPath, string cacheFolder, string playerName)
    {
        var config = EosConfig.Read(configPath, out var why);
        if (config is null)
        {
            Console.Error.WriteLine($"driftwood: Epic: {why}");
            return null;
        }

        return new EpicMeetingPlace(config, cacheFolder, playerName);
    }

    private EpicMeetingPlace(EosConfig config, string cacheFolder, string playerName)
    {
        SdkThread.Post(() =>
        {
            if (!Boot(config, cacheFolder)) return;
            SdkThread.Add(this);
            SignIn(playerName);
        });
    }

    public Task<bool> WhenReady(CancellationToken cancel) => _ready.Task.WaitAsync(cancel);

    /// <summary>Runs <paramref name="work"/> on the SDK thread.</summary>
    internal void Post(Action work) => SdkThread.Post(work);

    internal void Wake() => SdkThread.Wake();

    internal PlatformInterface Platform => _platform!;

    internal ProductUserId Me => _me!;

    /// <summary>One turn of the SDK thread for this place.</summary>
    private void Step()
    {
        if (_platform is null) return;
        _platform.Tick();
        Link?.Pump();
    }

    private bool Boot(EosConfig config, string cacheFolder)
    {
        lock (s_initLock)
        {
            if (!s_initialized)
            {
                var init = new InitializeOptions { ProductName = "Driftwood", ProductVersion = "1" };
                var result = PlatformInterface.Initialize(ref init);
                if (result != Result.Success && result != Result.AlreadyConfigured)
                {
                    Fail($"Epic's SDK would not start ({result})");
                    return false;
                }

                s_initialized = true;
                LoggingInterface.SetLogLevel(LogCategory.AllCategories, LogLevel.Warning);
                LoggingInterface.SetCallback((ref LogMessage message) =>
                    Console.Error.WriteLine($"epic-sdk    {message.Category}: {message.Message}"));

                // ⚠ Epic's worker threads must be stopped before the process tears down its statics,
                // or exit crashes (Siege FX found this on Linux).
                AppDomain.CurrentDomain.ProcessExit += (_, _) => SdkThread.Stop();
            }
        }

        Directory.CreateDirectory(cacheFolder);
        var options = new Options
        {
            ProductId = config.ProductId,
            SandboxId = config.SandboxId,
            DeploymentId = config.DeploymentId,
            ClientCredentials = new ClientCredentials { ClientId = config.ClientId, ClientSecret = config.ClientSecret },
            IsServer = false,
            CacheDirectory = cacheFolder,
            Flags = PlatformFlags.DisableOverlay | PlatformFlags.DisableSocialOverlay,
        };
        _platform = PlatformInterface.Create(ref options);
        if (_platform is null)
        {
            Fail("Epic refused the game's credentials");
            return false;
        }

        // Every packet is drained within milliseconds; a cap would only drop reliable data on a burst.
        var queues = new Epic.OnlineServices.P2P.SetPacketQueueSizeOptions
        {
            IncomingPacketQueueMaxSizeBytes = Epic.OnlineServices.P2P.P2PInterface.MAX_QUEUE_SIZE_UNLIMITED,
            OutgoingPacketQueueMaxSizeBytes = Epic.OnlineServices.P2P.P2PInterface.MAX_QUEUE_SIZE_UNLIMITED,
        };
        _platform.GetP2PInterface().SetPacketQueueSize(ref queues);
        return true;
    }

    private void SignIn(string playerName)
    {
        var connect = _platform!.GetConnectInterface();
        var create = new CreateDeviceIdOptions { DeviceModel = DeviceModel() };
        connect.CreateDeviceId(ref create, null, (ref CreateDeviceIdCallbackInfo made) =>
        {
            // A device id made on an earlier run answers "duplicate": either way, sign in with it.
            if (made.ResultCode != Result.Success && made.ResultCode != Result.DuplicateNotAllowed)
            {
                Fail($"Epic would not make a device identity ({made.ResultCode})");
                return;
            }

            var login = new LoginOptions
            {
                Credentials = new Credentials { Type = ExternalCredentialType.DeviceidAccessToken, Token = null },
                UserLoginInfo = new UserLoginInfo { DisplayName = Fit(playerName) },
            };
            connect.Login(ref login, null, (ref LoginCallbackInfo signed) =>
            {
                if (signed.ResultCode != Result.Success)
                {
                    Fail($"Epic sign-in failed ({signed.ResultCode})");
                    return;
                }

                _me = signed.LocalUserId;
                _me.ToString(out var text);
                _myAddress = text?.ToString();
                _state = MeetingState.Ready;
                Console.WriteLine($"epic        signed in as {_myAddress}");
                _ready.TrySetResult(true);
            });
        });
    }

    private void Fail(string why)
    {
        _fault = why;
        _state = MeetingState.Failed;
        Console.Error.WriteLine($"driftwood: Epic: {why}");
        _ready.TrySetResult(false);
    }

    public Task<string?> OpenRoomAsync(string world, string hostName, int players, string version)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            if (_state != MeetingState.Ready) { done.TrySetResult(_fault ?? "not signed in to Epic yet"); return; }
            if (_lobbyId is not null) DestroyLobby();

            var lobbies = _platform!.GetLobbyInterface();
            var create = new CreateLobbyOptions
            {
                LocalUserId = _me,
                MaxLobbyMembers = (uint)(CoopHost.MaxGuests + 1),
                PermissionLevel = LobbyPermissionLevel.Publicadvertised,
                PresenceEnabled = false,
                AllowInvites = false,
                BucketId = Bucket,
                DisableHostMigration = true,
            };
            lobbies.CreateLobby(ref create, null, (ref CreateLobbyCallbackInfo made) =>
            {
                if (made.ResultCode != Result.Success)
                {
                    done.TrySetResult($"Epic would not list the world ({made.ResultCode})");
                    return;
                }

                _lobbyId = made.LobbyId?.ToString();
                SetAttributes(new()
                {
                    [AttrHost] = _myAddress ?? "",
                    [AttrWorld] = world,
                    [AttrHostName] = hostName,
                    [AttrVersion] = version,
                }, players, done);
            });
        });
        return done.Task;
    }

    public void UpdateRoom(int players) => Post(() =>
    {
        if (_lobbyId is not null) SetAttributes([], players, null);
    });

    public void CloseRoom() => Post(DestroyLobby);

    private void SetAttributes(Dictionary<string, string> text, int players, TaskCompletionSource<string?>? done)
    {
        var lobbies = _platform!.GetLobbyInterface();
        var open = new UpdateLobbyModificationOptions { LocalUserId = _me, LobbyId = _lobbyId };
        if (lobbies.UpdateLobbyModification(ref open, out var change) != Result.Success || change is null)
        {
            done?.TrySetResult("Epic would not change the listing");
            return;
        }

        foreach (var (key, value) in text)
            Add(change, key, new AttributeDataValue { AsUtf8 = value });
        Add(change, AttrPlayers, new AttributeDataValue { AsInt64 = players });

        var update = new UpdateLobbyOptions { LobbyModificationHandle = change };
        lobbies.UpdateLobby(ref update, null, (ref UpdateLobbyCallbackInfo updated) =>
            done?.TrySetResult(updated.ResultCode == Result.Success ? null : $"Epic would not list the world ({updated.ResultCode})"));
        change.Release();

        static void Add(LobbyModification change, string key, AttributeDataValue value)
        {
            var add = new LobbyModificationAddAttributeOptions
            {
                Attribute = new AttributeData { Key = key, Value = value },
                Visibility = LobbyAttributeVisibility.Public,
            };
            change.AddAttribute(ref add);
        }
    }

    private void DestroyLobby()
    {
        if (_lobbyId is null || _platform is null) return;
        var destroy = new DestroyLobbyOptions { LocalUserId = _me, LobbyId = _lobbyId };
        _platform.GetLobbyInterface().DestroyLobby(ref destroy, null, (ref DestroyLobbyCallbackInfo _) => { });
        _lobbyId = null;
    }

    public Task<IReadOnlyList<MeetingRoom>> FindRoomsAsync(CancellationToken cancel)
    {
        var done = new TaskCompletionSource<IReadOnlyList<MeetingRoom>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            if (_state != MeetingState.Ready) { done.TrySetResult([]); return; }

            var lobbies = _platform!.GetLobbyInterface();
            var make = new CreateLobbySearchOptions { MaxResults = 50 };
            if (lobbies.CreateLobbySearch(ref make, out var search) != Result.Success || search is null)
            {
                done.TrySetResult([]);
                return;
            }

            var bucket = new LobbySearchSetParameterOptions
            {
                Parameter = new AttributeData { Key = "bucket", Value = new AttributeDataValue { AsUtf8 = Bucket } },
                ComparisonOp = ComparisonOp.Equal,
            };
            search.SetParameter(ref bucket);
            var find = new LobbySearchFindOptions { LocalUserId = _me };
            search.Find(ref find, null, (ref LobbySearchFindCallbackInfo found) =>
            {
                var rooms = new List<MeetingRoom>();
                if (found.ResultCode == Result.Success)
                {
                    var countOptions = new LobbySearchGetSearchResultCountOptions();
                    var count = search.GetSearchResultCount(ref countOptions);
                    for (uint i = 0; i < count; i++)
                    {
                        var copy = new LobbySearchCopySearchResultByIndexOptions { LobbyIndex = i };
                        if (search.CopySearchResultByIndex(ref copy, out var details) != Result.Success || details is null) continue;
                        var host = Text(details, AttrHost);
                        if (host.Length > 0)
                            rooms.Add(new MeetingRoom(host, Text(details, AttrWorld), Text(details, AttrHostName),
                                (int)Number(details, AttrPlayers), Text(details, AttrVersion)));
                        details.Release();
                    }
                }
                else if (found.ResultCode != Result.NotFound)
                {
                    Console.Error.WriteLine($"driftwood: Epic: lobby search {found.ResultCode}");
                }

                search.Release();
                done.TrySetResult(rooms);
            });
        });
        return done.Task.WaitAsync(cancel);

        static string Text(LobbyDetails details, string key) =>
            Read(details, key)?.Value.AsUtf8?.ToString() ?? "";

        static long Number(LobbyDetails details, string key) => Read(details, key)?.Value.AsInt64 ?? 0;

        static AttributeData? Read(LobbyDetails details, string key)
        {
            var options = new LobbyDetailsCopyAttributeByKeyOptions { AttrKey = key };
            return details.CopyAttributeByKey(ref options, out var attribute) == Result.Success ? attribute?.Data : null;
        }
    }

    public INetTransport Listen()
    {
        if (_state != MeetingState.Ready) throw new InvalidOperationException(_fault ?? "not signed in to Epic yet");
        var link = new EosLink(this, host: true);
        Post(() => link.Open(null));
        return link;
    }

    public async Task<INetTransport> ConnectAsync(string hostAddress, CancellationToken cancel)
    {
        if (_state != MeetingState.Ready) throw new InvalidOperationException(_fault ?? "not signed in to Epic yet");
        var link = new EosLink(this, host: false);
        Post(() => link.Open(hostAddress));
        try
        {
            await link.Established.WaitAsync(cancel).ConfigureAwait(false);
        }
        catch
        {
            link.Dispose();
            throw;
        }

        return link;
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        var done = new ManualResetEventSlim();
        SdkThread.Post(() =>
        {
            ShutDownOnThread();
            done.Set();
        });
        if (!SdkThread.IsCurrent) done.Wait(TimeSpan.FromSeconds(3));
    }

    private void ShutDownOnThread()
    {
        try
        {
            Link?.CloseOnThread();
            DestroyLobby();
            // A last few ticks so the lobby's removal and the closes reach Epic.
            for (var i = 0; i < 20 && _platform is not null; i++)
            {
                _platform.Tick();
                Thread.Sleep(5);
            }

            _platform?.Release();
        }
        catch { }

        _platform = null;
        SdkThread.Remove(this);
        if (_state == MeetingState.Starting) Fail("Epic was closed");
    }

    /// <summary>
    /// The one thread every Epic call in this process runs on, for every place.
    /// </summary>
    /// <remarks>⛔ Two platforms made or ticked on two threads crash inside Epic's library
    /// (<c>EOS_Platform_Create</c>, seen 2026-11-04): the SDK is single-threaded per process, not per platform.</remarks>
    private static class SdkThread
    {
        private static readonly ConcurrentQueue<Action> s_work = new();
        private static readonly AutoResetEvent s_wake = new(false);
        private static readonly List<EpicMeetingPlace> s_places = [];
        private static Thread? s_thread;
        private static volatile bool s_stopping;

        public static bool IsCurrent => Thread.CurrentThread == s_thread;

        public static void Post(Action work)
        {
            s_work.Enqueue(work);
            lock (s_work)
            {
                if (s_thread is null && !s_stopping)
                {
                    s_thread = new Thread(Run) { IsBackground = true, Name = "Epic" };
                    s_thread.Start();
                }
            }

            s_wake.Set();
        }

        public static void Wake() => s_wake.Set();

        public static void Add(EpicMeetingPlace place) => s_places.Add(place);

        public static void Remove(EpicMeetingPlace place) => s_places.Remove(place);

        /// <summary>At process exit: every place shut, then the SDK, on this thread.</summary>
        public static void Stop()
        {
            var done = new ManualResetEventSlim();
            Post(() =>
            {
                foreach (var place in s_places.ToArray())
                {
                    place._stopping = true;
                    place.ShutDownOnThread();
                }

                PlatformInterface.Shutdown();
                s_stopping = true;
                done.Set();
            });
            done.Wait(TimeSpan.FromSeconds(3));
        }

        private static void Run()
        {
            while (!s_stopping)
            {
                while (s_work.TryDequeue(out var work))
                {
                    try { work(); }
                    catch (Exception error) { Console.Error.WriteLine($"driftwood: Epic: {error.GetBaseException().Message}"); }
                }

                var busy = false;
                foreach (var place in s_places.ToArray())
                {
                    try { place.Step(); }
                    catch (Exception error) { place.Fail($"Epic stopped: {error.GetBaseException().Message}"); }
                    busy |= place.Link?.Busy == true;
                }

                s_wake.WaitOne(busy ? 1 : 5);
            }
        }
    }

    /// <summary>"Linux X64": what a new device identity records as its model.</summary>
    private static string DeviceModel()
    {
        var os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux"
            : OperatingSystem.IsMacOS() ? "macOS" : "Unknown OS";
        return $"{os} {RuntimeInformation.OSArchitecture}";
    }

    /// <summary>The player's name within Epic's 32-byte limit.</summary>
    private static string Fit(string name)
    {
        name = name.Trim();
        if (name.Length == 0) name = "Player";
        while (System.Text.Encoding.UTF8.GetByteCount(name) > ConnectInterface.USERLOGININFO_DISPLAYNAME_MAX_LENGTH)
            name = name[..^1];
        return name;
    }

    internal static string Short(string? address) =>
        address is null ? "?" : address.Length > 8 ? address[..8] + "…" : address;
}
