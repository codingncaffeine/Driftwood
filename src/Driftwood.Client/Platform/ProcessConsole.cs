using System.Runtime.InteropServices;
using System.Text;

namespace Driftwood.Client.Platform;

/// <summary>
/// Keeps the ordinary Windows launch window-only while preserving Driftwood's explicit CLI tools.
/// </summary>
internal static class ProcessConsole
{
    private const uint AttachParentProcess = 0xffffffff;
    private const uint ErrorIcon = 0x00000010;

    /// <summary>
    /// Attaches a GUI-subsystem executable to an existing terminal only for an instrument the user
    /// explicitly requested. No parent terminal means no console is allocated.
    /// </summary>
    public static bool Prepare(string[] args)
    {
        var requested = args.Any(IsCommandLineMode);
        if (!requested || !OperatingSystem.IsWindows()) return requested;

        // Already attached is fine (ERROR_ACCESS_DENIED); a redirected batch pipe is fine too. In
        // either case rebinding after the attempt makes Console use the inherited standard handles.
        _ = AttachConsole(AttachParentProcess);
        RebindStandardStreams();
        return true;
    }

    /// <summary>A startup failure belongs in the terminal for a CLI and in a dialog for the game.</summary>
    public static void ReportFailure(Exception error, bool commandLine)
    {
        if (commandLine || !OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine($"driftwood: {error.Message}");
            return;
        }

        _ = MessageBox(
            IntPtr.Zero,
            $"Driftwood could not start.\n\n{error.Message}",
            "Driftwood",
            ErrorIcon);
    }

    private static bool IsCommandLineMode(string argument) => argument switch
    {
        "--help" or "-h" or "--version" or "--audit" or "--audio-check"
            or "--controller-check" or "--icon-sheet" or "--packs" or "--recipes"
            or "--pack-report" or "--atlas" or "--creatures" or "--pack-coverage"
            or "--magic-check" or "--magic-reference"
            or "--ui-check" or "--shot" or "--bench" or "--play" => true,
        _ => false,
    };

    private static void RebindStandardStreams()
    {
        Rebind(Console.OpenStandardOutput(), Console.SetOut);
        Rebind(Console.OpenStandardError(), Console.SetError);

        static void Rebind(Stream stream, Action<TextWriter> set)
        {
            if (ReferenceEquals(stream, Stream.Null)) return;

            set(new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true,
            });
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
