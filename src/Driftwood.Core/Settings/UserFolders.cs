namespace Driftwood.Core.Settings;

/// <summary>
/// The three places a player's own files live: what they made, what they chose, and what can be
/// thrown away.
/// </summary>
/// <remarks>
/// <para>⛳ <b>One answer per platform, asked in one place.</b> Every folder the game writes was
/// <c>ApplicationData\Driftwood</c>, which on Windows is the right single answer and on Linux is
/// <c>~/.config</c> — a folder people back up as a few kilobytes of preferences, about to be handed
/// whole worlds and several hundred megabytes of packs. Linux keeps those apart by kind (the XDG
/// base-directory specification), and so does every game and program that behaves well on it.</para>
/// <list type="bullet">
/// <item><see cref="Data"/>: saves, packs, skins, sound packs — losing these loses work.
/// <c>$XDG_DATA_HOME/Driftwood</c>, normally <c>~/.local/share/Driftwood</c>.</item>
/// <item><see cref="Config"/>: the settings file. <c>$XDG_CONFIG_HOME/Driftwood</c>, normally
/// <c>~/.config/Driftwood</c>.</item>
/// <item><see cref="Cache"/>: downloads and inspection results that are fetched or rebuilt on demand.
/// <c>$XDG_CACHE_HOME/Driftwood</c>, normally <c>~/.cache/Driftwood</c>.</item>
/// </list>
/// <para>⚠ <b>Windows is unchanged</b>, folder for folder, so nobody's existing worlds move:
/// all three are <c>%APPDATA%\Driftwood</c>, the cache under its <c>cache</c> folder as before.</para>
/// <para>⚠ <b>The XDG variables are honoured, not just the defaults</b> — and that is also what
/// keeps a test run out of a real player's saves: it points them at a scratch folder.</para>
/// </remarks>
public static class UserFolders
{
    private const string Product = "Driftwood";

    /// <summary>What the player made or installed: saves, packs, skins, sound packs.</summary>
    public static string Data => OperatingSystem.IsLinux()
        ? Path.Combine(Xdg("XDG_DATA_HOME", ".local/share"), Product)
        : Roaming;

    /// <summary>What the player chose: the settings file.</summary>
    public static string Config => OperatingSystem.IsLinux()
        ? Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), Product)
        : Roaming;

    /// <summary>What can be deleted without losing anything: it is fetched or rebuilt when missing.</summary>
    public static string Cache => OperatingSystem.IsLinux()
        ? Path.Combine(Xdg("XDG_CACHE_HOME", ".cache"), Product)
        : Path.Combine(Roaming, "cache");

    private static string Roaming => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Product);

    /// <summary>
    /// A base directory from its variable, or its default under the home folder.
    /// </summary>
    /// <remarks>
    /// ⚠ A relative value is ignored rather than resolved, as the specification requires: it would
    /// mean a different folder depending on where the game happened to be started from.
    /// </remarks>
    private static string Xdg(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrEmpty(value) && Path.IsPathRooted(value)) return value;

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback);
    }
}
