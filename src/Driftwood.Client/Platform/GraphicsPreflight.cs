using System.Runtime.InteropServices;
using Silk.NET.GLFW;

namespace Driftwood.Client.Platform;

/// <summary>
/// Asks for the game's kind of OpenGL window once, hidden, before Silk.NET asks for the real one.
/// </summary>
/// <remarks>
/// <para>⛔ <b>Because Silk.NET does not survive the answer "no".</b> When the driver cannot make an
/// OpenGL 3.3 core context, <c>glfwCreateWindow</c> returns null, and Silk.NET carries straight on
/// into <c>glfwSetWindowPos</c> with it: a segfault inside GLFW, no exception, no dialog, and for a
/// player who started the game from a menu, nothing at all — the game simply never appears. Found
/// 2026-10-15 by pointing a Wayland run at a GPU the compositor was not on; a missing or broken
/// driver lands in the same place on any platform. Asked here first, the same failure becomes an
/// exception, and <see cref="ProcessConsole.ReportFailure"/> turns it into a sentence on screen.</para>
/// <para>⚠ <b>The hints are the ones Silk.NET is about to use</b> — 3.3, core, forward-compatible
/// — and are wiped again afterwards, so nothing set here leaks into the real window. That is also
/// why this runs before <see cref="DesktopIdentity.NameNextWindow"/>, not after it.</para>
/// <para>⚠ <b>Our own error callback for the length of the probe.</b> GLFW reports the reason
/// through it, and the callback Silk.NET installed may throw — which, from inside a native call, is
/// not an exception anybody can catch but the end of the process.</para>
/// </remarks>
internal static unsafe class GraphicsPreflight
{
    public static void RequireOpenGl(int major, int minor)
    {
        var glfw = GlfwProvider.GLFW.Value;
        string? reason = null;

        GlfwCallbacks.ErrorCallback record = (_, description) => reason ??= description;
        var previous = glfw.SetErrorCallback(record);

        try
        {
            glfw.DefaultWindowHints();
            glfw.WindowHint(WindowHintBool.Visible, false);
            glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.OpenGL);
            glfw.WindowHint(WindowHintInt.ContextVersionMajor, major);
            glfw.WindowHint(WindowHintInt.ContextVersionMinor, minor);
            glfw.WindowHint(WindowHintOpenGlProfile.OpenGlProfile, OpenGlProfile.Core);
            glfw.WindowHint(WindowHintBool.OpenGLForwardCompat, true);

            var probe = glfw.CreateWindow(64, 64, "Driftwood", null, null);
            if (probe is null)
                throw new InvalidOperationException(
                    $"the graphics driver could not open an OpenGL {major}.{minor} window"
                    + (reason is null ? "" : $" ({reason})")
                    + ". Check that the graphics driver is installed and up to date.");

            glfw.DestroyWindow(probe);
        }
        finally
        {
            glfw.DefaultWindowHints();
            glfw.SetErrorCallback(previous);
            GC.KeepAlive(record);
        }
    }
}
