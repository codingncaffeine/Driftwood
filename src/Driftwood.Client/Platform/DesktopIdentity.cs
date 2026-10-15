using Silk.NET.GLFW;

namespace Driftwood.Client.Platform;

/// <summary>
/// The name a Linux desktop knows the game's window by, so the taskbar can show its icon.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Why the window needs a name at all.</b> On Windows the taskbar icon comes from the
/// window, and <see cref="Render.ClientHost"/> hands GLFW the embedded PNGs. Wayland has no such
/// call that GLFW makes — the compositor finds the icon by matching the window's app id to an
/// installed <c>.desktop</c> file of the same name — and a window without an app id is a generic
/// blank icon in every taskbar and switcher, however well the game itself looks. X11 does the
/// same matching through WM_CLASS.</para>
/// <para>⚠ <b>The id is a contract with the packaging</b>: <c>packaging/linux</c> installs
/// <c>io.github.codingncaffeine.Driftwood.desktop</c> and icons under the same name, and a change
/// here that is not made there too is the blank icon again. Reverse-DNS on the repository's host,
/// the form Flathub requires, so the id never has to change if the game is published there.</para>
/// </remarks>
internal static class DesktopIdentity
{
    public const string AppId = "io.github.codingncaffeine.Driftwood";

    /// <summary>GLFW_WAYLAND_APP_ID, which this version of the binding has no name for.</summary>
    private const int WaylandAppId = 0x00026001;

    /// <summary>
    /// Sets the hints the next GLFW window is created with. Call after GLFW is up and before the
    /// window is.
    /// </summary>
    public static void NameNextWindow()
    {
        if (!OperatingSystem.IsLinux()) return;

        var glfw = GlfwProvider.GLFW.Value;
        glfw.WindowHintString(WaylandAppId, AppId);

        // ⚠ The instance half of WM_CLASS only. Silk.NET sets the class half itself while creating
        // the window, to the assembly's name ("Driftwood"), over whatever is asked here — read back
        // from KWin, 2026-10-15. Desktops match either half against .desktop files, and the file
        // also says StartupWMClass=Driftwood, so both halves lead to the same icon.
        glfw.WindowHintString((int)WindowHintString.X11InstanceName, AppId);
    }
}
