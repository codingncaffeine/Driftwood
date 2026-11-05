using Driftwood.Core.Net;
using Driftwood.Core.Settings;
using Driftwood.Core.Textures;

namespace Driftwood.Client.Render;

/// <summary>
/// N6 (2026-11-04): Epic as the meeting place — a hosted world is listed, friends pick it from a
/// browser page instead of typing an address, and the link runs over Epic's P2P.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Optional all the way down.</b> No module or no credentials means
/// <see cref="Meeting"/> is null, and co-op is direct addresses exactly as before.</para>
/// <para>⛳ <b>Signed in only when wanted</b>: the first time the Multiplayer page opens, a world is
/// hosted, or an Epic join starts — never for a player who only plays alone.</para>
/// </remarks>
public sealed partial class ClientHost
{
    /// <summary>A join address that means "this Epic player", not a network address.</summary>
    private const string EpicPrefix = "epic:";

    /// <summary>How often the browser page asks Epic for the open worlds again.</summary>
    private const double BrowseInterval = 5.0;

    private const string BrowseHeading = "Friends' Worlds";
    private const string ConfirmJoinRow = "Confirm Join";

    private static IMeetingPlace? s_meeting;
    private static string? s_meetingWhy;
    private static bool s_meetingTried;

    /// <summary>Set by <c>--shot</c> runs: no Epic at all.</summary>
    internal static bool s_noMeeting;
    private static readonly object s_meetingLock = new();

    private CombinedTransport? _hostLinks;
    private bool _epicListening;
    private int _epicRoomPlayers = -1;
    private volatile string? _epicRoomStatus;

    private bool _startBrowsing;
    private IReadOnlyList<MeetingRoom> _rooms = [];
    private Task<IReadOnlyList<MeetingRoom>>? _roomsSearch;
    private double _sinceBrowse = BrowseInterval;
    private bool _roomsSearched;
    private string? _roomPick;
    private string? _coopCode;

    /// <summary>The shot instrument's browser: a fixed list, no Epic, no status line.</summary>
    private bool _browseShot;

    /// <summary>The meeting place, started the first time something wants it. Null = direct addresses only.</summary>
    private static IMeetingPlace? Meeting(string playerName)
    {
        lock (s_meetingLock)
        {
            if (s_meetingTried) return s_meeting;
            s_meetingTried = true;
            // ⛔ Photographs never sign in to Epic: a shot that hosts would list a test world for real players.
            if (s_noMeeting)
            {
                s_meetingWhy = "not for instrument runs";
                return null;
            }

            s_meeting = MeetingPlaces.Start(
                UserFolders.Config, Path.Combine(UserFolders.Cache, "epic"), playerName, out var why);
            if (s_meeting is null)
            {
                s_meetingWhy = why;
                Console.WriteLine($"epic        off: {why}");
            }

            return s_meeting;
        }
    }

    /// <summary>Why there is no meeting place, or what went wrong with it, for the menus.</summary>
    private static string? MeetingTrouble() =>
        s_meeting is null ? s_meetingWhy : s_meeting.State == MeetingState.Failed ? s_meeting.Fault : null;

    /// <summary>This build can meet through Epic: the module is beside it and credentials are found.</summary>
    private static bool EpicHere() =>
        s_meeting?.State is MeetingState.Starting or MeetingState.Ready
        || (!s_meetingTried
            && File.Exists(Path.Combine(AppContext.BaseDirectory, MeetingPlaces.ModuleFile))
            && MeetingPlaces.FindConfig(UserFolders.Config) is not null);

    /// <summary>A guest's link to the host of the world opened with <paramref name="code"/>. Off the game thread.</summary>
    private static async Task<INetTransport> ConnectByCode(string code, string playerName, CancellationToken cancel)
    {
        var place = Meeting(playerName)
                    ?? throw new InvalidOperationException($"join codes need Epic, which is not available here ({s_meetingWhy})");
        if (!await place.WhenReady(cancel).ConfigureAwait(false))
            throw new InvalidOperationException(place.Fault ?? "could not sign in to Epic");

        // ⚠ A world listed a moment ago can take a few seconds to show in Epic's search.
        for (var tries = 0; ; tries++)
        {
            if (await place.FindRoomByCodeAsync(code, cancel).ConfigureAwait(false) is { } room)
            {
                Console.WriteLine($"co-op       code {JoinCodes.Show(code)} is {room.HostName}'s '{room.World}'");
                return await place.ConnectAsync(room.HostAddress, cancel).ConfigureAwait(false);
            }

            if (tries >= 4) throw new InvalidOperationException($"no world is open with the code {JoinCodes.Show(code)}");
            await Task.Delay(1500, cancel).ConfigureAwait(false);
        }
    }

    /// <summary>"epic:PUID" → the Epic player address; anything else → null.</summary>
    private static string? EpicAddressIn(string address) =>
        address.StartsWith(EpicPrefix, StringComparison.OrdinalIgnoreCase) ? address[EpicPrefix.Length..].Trim() : null;

    /// <summary>A guest's link to an Epic host, signing in first if need be. Off the game thread.</summary>
    private static async Task<INetTransport> ConnectThroughEpic(string hostAddress, string playerName, CancellationToken cancel)
    {
        var place = Meeting(playerName)
                    ?? throw new InvalidOperationException($"Epic is not available here ({s_meetingWhy})");
        if (!await place.WhenReady(cancel).ConfigureAwait(false))
            throw new InvalidOperationException(place.Fault ?? "could not sign in to Epic");
        return await place.ConnectAsync(hostAddress, cancel).ConfigureAwait(false);
    }

    /// <summary>From <see cref="StartHosting"/>: the port's link, with Epic's joined to it once signed in.</summary>
    private CombinedTransport HostLinks(TcpTransport tcp)
    {
        _hostLinks = new CombinedTransport(tcp);
        _epicListening = false;
        _epicRoomPlayers = -1;
        _epicRoomStatus = null;
        Meeting(MyCoopName());
        StepEpicHost();
        return _hostLinks;
    }

    /// <summary>Once a frame from <see cref="StepCoop"/>: Epic's side of hosting and of the browser page.</summary>
    private void StepEpic(double dt)
    {
        StepEpicHost();
        StepBrowse(dt);
    }

    private void StepEpicHost()
    {
        if (_coopHost is null && _hostLinks is null) return;
        if (_hostLinks is not { } links || s_meeting is not { } place) return;

        if (!_epicListening)
        {
            if (place.State == MeetingState.Failed)
            {
                _epicListening = true; // gave up: the port alone
                _epicRoomStatus = $"not listed on Epic: {place.Fault}";
                return;
            }

            if (place.State != MeetingState.Ready) return;
            _epicListening = true;
            try
            {
                links.Add(place.Listen());
            }
            catch (Exception error)
            {
                _epicRoomStatus = $"not listed on Epic: {error.Message}";
                return;
            }

            _epicRoomPlayers = 1 + (_coopHost?.Players.Count ?? 0);
            _epicRoomStatus = "listing on Epic…";
            var world = _worldName;
            var code = _coopCode;
            place.OpenRoomAsync(world, MyCoopName(), _epicRoomPlayers, ProductVersion, code).ContinueWith(opened =>
            {
                var fault = opened.IsCompletedSuccessfully ? opened.Result : opened.Exception?.GetBaseException().Message;
                _epicRoomStatus = fault is not null ? $"not on Epic: {fault}"
                    : code is not null ? "found on Epic by its code" : "listed on Epic";
                Console.WriteLine($"epic        {_epicRoomStatus} as '{world}'");
            }, TaskScheduler.Default);
            return;
        }

        var players = 1 + (_coopHost?.Players.Count ?? 0);
        if (_epicRoomPlayers > 0 && players != _epicRoomPlayers)
        {
            _epicRoomPlayers = players;
            place.UpdateRoom(players);
        }
    }

    /// <summary>From <see cref="LeaveCoop"/>: the listing comes down with the world.</summary>
    private void CloseEpicRoom()
    {
        if (_epicRoomPlayers > 0) s_meeting?.CloseRoom();
        _epicRoomPlayers = -1;
        _epicListening = false;
        _hostLinks = null;
        _epicRoomStatus = null;
    }

    // ─── The browser page (the user's multiplayer.jpg: a grid of panels, then Cancel | Confirm Join) ───

    private void OpenBrowsePage()
    {
        Meeting(MyCoopName());
        _startBrowsing = true;
        _startFriends = false;
        _roomPick = null;
        _roomsSearched = false;
        _rooms = [];
        _sinceBrowse = BrowseInterval;
        _hudScreen.Selected = 1;
        _hudScreen.Scroll = 0;
        RefreshScreen();
        ShowSelectedRow();
    }

    private void StepBrowse(double dt)
    {
        if (!_startBrowsing || _browseShot || s_meeting is not { } place) return;

        if (_roomsSearch is { IsCompleted: true } search)
        {
            _roomsSearch = null;
            _rooms = search.IsCompletedSuccessfully ? search.Result : [];
            _roomsSearched = true;
            if (_roomPick is not null && _rooms.All(room => room.HostAddress != _roomPick)) _roomPick = null;
            RefreshScreen();
        }

        _sinceBrowse += dt;
        if (_roomsSearch is null && _sinceBrowse >= BrowseInterval && place.State == MeetingState.Ready)
        {
            _sinceBrowse = 0;
            _roomsSearch = place.FindRoomsAsync(CancellationToken.None);
        }
        else if (place.State == MeetingState.Failed && !_roomsSearched)
        {
            _roomsSearched = true;
            RefreshScreen();
        }
    }

    private void BuildBrowseRows()
    {
        _hudScreen.Rows.Add(new MenuRow(BrowseHeading, Heading: true));

        var mine = s_meeting?.MyAddress;
        var rooms = _rooms.Where(room => room.HostAddress != mine).ToList();
        var status = _browseShot ? ""
            : MeetingTrouble() is { } trouble ? $"Epic is not available: {trouble}"
            : s_meeting?.State != MeetingState.Ready ? "Signing in to Epic…"
            : !_roomsSearched ? "Looking for open worlds…"
            : rooms.Count == 0 ? "No open worlds right now. A friend opens theirs from the pause menu or Open Your World"
            : "";
        if (status.Length > 0) _hudScreen.Rows.Add(new MenuRow(status, Heading: true));

        for (var i = 0; i < rooms.Count; i++)
        {
            var room = rooms[i];
            var sameVersion = room.Version == ProductVersion;
            _hudScreen.Rows.Add(new MenuRow(
                room.World, sameVersion ? $"{room.HostName}, {room.Players} playing" : $"{room.HostName}, v{room.Version}",
                Art: MenuButtonArt.Panel, Beside: i % 3 != 0, Pinned: room.HostAddress == _roomPick,
                Note: sameVersion
                    ? $"{room.HostName}'s world. Pick it, then Confirm Join"
                    : $"{room.HostName} plays v{room.Version}; this is v{ProductVersion}, so the host will turn you away",
                Value2: room.HostAddress));
        }

        _hudScreen.Rows.Add(new MenuRow(CancelRow, "", Art: MenuButtonArt.Cancel, Note: "Back to Multiplayer"));
        _hudScreen.Rows.Add(new MenuRow(
            ConfirmJoinRow, "", Art: MenuButtonArt.ConfirmJoin, Beside: true,
            Note: rooms.FirstOrDefault(room => room.HostAddress == _roomPick) is { } picked
                ? _worldName.Length > 0 && _loadedWorld
                    ? $"Saves {_worldName} first, then joins {picked.HostName}'s {picked.World}"
                    : $"Joins {picked.HostName}'s {picked.World}"
                : "Pick a world first"));
    }

    /// <summary>For <c>--shot</c>: the browser page with five worlds, the second one picked.</summary>
    private void ShowBrowseShot()
    {
        _browseShot = true;
        _startBrowsing = true;
        _startFriends = false;
        _roomsSearched = true;
        _rooms =
        [
            new MeetingRoom("a1", "Hollow Pines", "Birch", 2, ProductVersion),
            new MeetingRoom("a2", "Saltmarsh", "Wren", 1, ProductVersion),
            new MeetingRoom("a3", "The Long Winter", "Oakley", 4, ProductVersion),
            new MeetingRoom("a4", "Copper Hill", "Fen", 1, "0.7.1"),
            new MeetingRoom("a5", "Driftwood Bay", "Moss", 3, ProductVersion),
        ];
        _roomPick = "a2";
        RefreshScreen();
        _hudScreen.Selected = _hudScreen.Rows.FindIndex(row => row.Value2 == "a2");
    }

    private void ChooseOnBrowsePage(MenuRow row)
    {
        switch (row.Label)
        {
            case CancelRow:
                _startBrowsing = false;
                OpenFriendsPage();
                return;
            case ConfirmJoinRow:
                if (_roomPick is { } pick) JoinFromMenu(EpicPrefix + pick);
                else ShowSelectedRow();
                return;
        }

        if (row.Art != MenuButtonArt.Panel || row.Value2.Length == 0) return;
        // Picking the picked one again is a join: one press less for a pad.
        if (_roomPick == row.Value2)
        {
            JoinFromMenu(EpicPrefix + row.Value2);
            return;
        }

        _roomPick = row.Value2;
        RefreshScreen();
        ShowSelectedRow();
    }
}
