using System.Reflection;

namespace Driftwood.Core.Net;

/// <summary>A world someone has opened to friends, as the meeting place lists it.</summary>
/// <param name="HostAddress">What a guest connects to: for Epic, the host's product user id.</param>
/// <param name="World">The world's name.</param>
/// <param name="HostName">The host player's name.</param>
/// <param name="Players">People in the world now, the host included.</param>
/// <param name="Version">The host's game version; a guest on another version is refused at the door anyway.</param>
public sealed record MeetingRoom(string HostAddress, string World, string HostName, int Players, string Version);

public enum MeetingState : byte
{
    /// <summary>Signing in. Nothing can be listed or opened yet.</summary>
    Starting,

    /// <summary>Signed in: rooms can be opened, found and joined.</summary>
    Ready,

    /// <summary>Gave up; <see cref="IMeetingPlace.Fault"/> says why. Direct addresses still work.</summary>
    Failed,
}

/// <summary>
/// Where players find each other without typing an address (N6, the user 2026-11-04: "epic just acts
/// like the meeting place").
/// </summary>
/// <remarks>
/// <para>⛳ <b>Optional, and outside the game's own assembly.</b> The only implementation is the Epic
/// module (<c>Driftwood.Net.Eos.dll</c>), built only where Epic's SDK is unpacked and loaded only when
/// it sits beside the program with credentials. Without it, co-op is direct addresses, unchanged.</para>
/// <para>⛔ <b>Nothing here blocks the caller.</b> Every call queues work for the module's own thread
/// and returns; answers come back as tasks.</para>
/// </remarks>
public interface IMeetingPlace : IDisposable
{
    /// <summary>"Epic", for menus and logs.</summary>
    string Name { get; }

    MeetingState State { get; }

    /// <summary>Why <see cref="MeetingState.Failed"/>, in words a player can act on.</summary>
    string? Fault { get; }

    /// <summary>This player's address here once signed in: what a room advertises.</summary>
    string? MyAddress { get; }

    /// <summary>Waits until signed in or failed. True when <see cref="MeetingState.Ready"/>.</summary>
    Task<bool> WhenReady(CancellationToken cancel);

    /// <summary>Lists this world so friends can find it. Null on success, else why not.</summary>
    Task<string?> OpenRoomAsync(string world, string hostName, int players, string version);

    /// <summary>Keeps the listed head count right as people come and go.</summary>
    void UpdateRoom(int players);

    /// <summary>Takes the listing down (hosting stopped, or the world closed).</summary>
    void CloseRoom();

    /// <summary>The worlds open right now.</summary>
    Task<IReadOnlyList<MeetingRoom>> FindRoomsAsync(CancellationToken cancel);

    /// <summary>The link a host listens on for guests who come through here.</summary>
    INetTransport Listen();

    /// <summary>A guest's link to the host at <paramref name="hostAddress"/>.</summary>
    Task<INetTransport> ConnectAsync(string hostAddress, CancellationToken cancel);
}

/// <summary>
/// Finds and starts the optional meeting-place module.
/// </summary>
public static class MeetingPlaces
{
    /// <summary>The module file, beside the program.</summary>
    public const string ModuleFile = "Driftwood.Net.Eos.dll";

    /// <summary>The credentials file: a release ships one beside the program.</summary>
    public const string ConfigFile = "eos.txt";

    /// <summary>
    /// Where the credentials are: <c>DRIFTWOOD_EOS_CONFIG</c>, then the config folder, then beside
    /// the program. Null when there are none.
    /// </summary>
    public static string? FindConfig(string configFolder)
    {
        if (Environment.GetEnvironmentVariable("DRIFTWOOD_EOS_CONFIG") is { Length: > 0 } named)
            return File.Exists(named) ? named : null;
        foreach (var candidate in new[]
                 {
                     Path.Combine(configFolder, ConfigFile),
                     Path.Combine(AppContext.BaseDirectory, ConfigFile),
                 })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    /// <summary>
    /// Loads the module and starts signing in. Null (with the reason) when there is no module, no
    /// credentials, or the module will not start — which only means co-op stays on addresses.
    /// </summary>
    /// <param name="configFolder">The game's config folder (one of the places credentials may be).</param>
    /// <param name="cacheFolder">Where the module keeps Epic's cache and this install's device identity.</param>
    /// <param name="playerName">The name Epic is told for this player.</param>
    public static IMeetingPlace? Start(string configFolder, string cacheFolder, string playerName, out string why)
    {
        var module = Path.Combine(AppContext.BaseDirectory, ModuleFile);
        if (!File.Exists(module))
        {
            why = "this build has no Epic module";
            return null;
        }

        if (FindConfig(configFolder) is not { } config)
        {
            why = "no Epic credentials (eos.txt)";
            return null;
        }

        try
        {
            var start = Assembly.LoadFrom(module).GetType("Driftwood.Net.Eos.EpicMeetingPlace")
                ?.GetMethod("Start", BindingFlags.Public | BindingFlags.Static);
            if (start is null)
            {
                why = "the Epic module is not this version's";
                return null;
            }

            var place = (IMeetingPlace?)start.Invoke(null, [config, cacheFolder, playerName]);
            why = place is null ? "the Epic module would not start" : "";
            return place;
        }
        catch (Exception error)
        {
            why = $"the Epic module failed to load: {error.GetBaseException().Message}";
            return null;
        }
    }
}
