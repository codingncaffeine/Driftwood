using System.Runtime.InteropServices;
using Silk.NET.Core.Loader;

namespace Driftwood.Client.Platform;

/// <summary>
/// Makes Silk.NET load the GLFW and OpenAL Soft builds that came with the game, on every distro.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Why this exists.</b> A framework-dependent build keeps its native libraries under
/// <c>runtimes/&lt;rid&gt;/native/</c>, and Silk.NET finds that folder by asking for the machine's
/// runtime identifier. On Linux the answer is the DISTRO's identifier — <c>arch-x64</c>,
/// <c>ubuntu.24.04-x64</c> — and the graph that used to walk those back to the portable
/// <c>linux-x64</c> the packages are actually published under was retired in .NET 8. So the lookup
/// came back empty, GLFW was "not applicable", and the game could not open a window at all, while
/// the Windows build next to it never noticed: <c>win-x64</c> is portable already.</para>
/// <para>⛔ <b>And FIRST, ahead of the system's copy, in the release too.</b> Silk.NET's own first
/// step hands the bare soname to the dynamic loader, so wherever the distro has the library
/// installed, its copy won and the one shipped beside the executable was never opened — measured
/// on the published build, 2026-10-15: <c>/usr/lib/libopenal.so.1</c>. That makes what a player
/// runs depend on their distro rather than on what the release gate tested, and a distro's GLFW
/// 3.3 has no Wayland app id (see <see cref="DesktopIdentity"/>). Windows has always run the bundled
/// build; this makes Linux do the same, with the system copy kept as the fallback it should be.</para>
/// </remarks>
internal static class NativeLibraries
{
    /// <summary>Puts the bundled libraries first in Silk.NET's search. Must run before any window or audio device.</summary>
    public static void Prepare()
    {
        if (OperatingSystem.IsWindows()) return;
        if (PathResolver.Default is not DefaultPathResolver resolver) return;

        // Beside the executable is where a published release keeps them; runtimes/ is a build.
        var folders = new List<string> { AppContext.BaseDirectory };
        if (PortableRid() is { } rid)
            folders.Add(Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native"));

        resolver.Resolvers.Insert(0, name => folders.SelectMany(folder => Bundled(folder, name)));
    }

    private static IEnumerable<string> Bundled(string folder, string name)
    {
        // ⚠ Asked for by its most specific soname and nothing else: GLFW asks for
        // "libglfw.so.3.3" alone and leaves dropping the version to a resolver further down the
        // list, which never looks in this folder. So the version is dropped here too, one segment
        // at a time, down to the bare ".so" — the bundled file is "libglfw.so.3".
        var file = Path.GetFileName(name);
        while (true)
        {
            var path = Path.Combine(folder, file);
            if (File.Exists(path)) yield return path;

            var dot = file.LastIndexOf('.');
            if (dot < 0 || !file.Contains(".so.") || !int.TryParse(file.AsSpan(dot + 1), out _)) yield break;
            file = file[..dot];
        }
    }

    /// <summary>
    /// The identifier the native packages are published under, worked out from the OS and the
    /// processor rather than read from the runtime, which may be a distro's own build.
    /// </summary>
    internal static string? PortableRid()
    {
        var os = OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsWindows() ? "win"
            : null;

        var cpu = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            Architecture.X86 => "x86",
            _ => null,
        };

        return os is null || cpu is null ? null : $"{os}-{cpu}";
    }
}
