using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Driftwood.Client.Platform;

/// <summary>
/// Asks Windows for a file or a folder — the Explorer window everybody already knows how to use.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Why this exists at all.</b> The pack importer shipped as a box you paste a path into,
/// and a path is not a thing anybody has. What a player has is a file in their downloads folder,
/// and the way they find it is the same window every other program on the machine opens. Typing
/// <c>C:\Users\…\Downloads\Really Real.mcpack</c> by hand, correctly, from a game, is not an import
/// route; it is a workaround for not having one.</para>
/// <para>⛔ <b>The dialog runs on its OWN thread, and both halves of that are load-bearing.</b> The
/// shell's chooser is an apartment-threaded COM object, so it must be called from an STA thread —
/// and the game's thread is not one. Blocking that thread on a modal dialog would also stop the
/// game rendering: the window would hold whatever frame it was on until the dialog was dismissed,
/// which reads as a crash. Asking on a thread of its own means the world carries on drawing behind
/// it, and the answer is collected by the game loop when it turns up.</para>
/// <para>⚠ <b>The result crosses threads through one volatile int and nothing else.</b> The picker
/// thread fills in the answer and *then* publishes the state; the game thread reads the state and
/// only then the answer. Two fields written on one side and read on the other, in that order, is
/// the whole of the synchronisation — a lock here would be a lock the render loop takes every
/// frame for a thing that happens twice a session.</para>
/// <para>⚠ <b>Cancelling is not a failure and must not say anything.</b> Windows reports it as an
/// error code (<c>ERROR_CANCELLED</c>), and a picker that turns that into "could not add that"
/// tells a player they broke something by changing their mind.</para>
/// </remarks>
public sealed class NativeFilePicker
{
    /// <summary>Which of the two things is being asked for.</summary>
    public enum Want
    {
        /// <summary>A pack that is a single archive.</summary>
        File,

        /// <summary>A pack that has been unzipped, and is therefore a folder.</summary>
        Folder,
    }

    /// <summary>Nothing asked, a dialog open, or an answer waiting to be collected.</summary>
    private const int Idle = 0, Asking = 1, Answered = 2;

    private volatile int _state = Idle;

    /// <summary>Written by the picker thread before <see cref="_state"/>, read by the game thread after.</summary>
    private string? _picked;
    private string _why = "";

    /// <summary>True while a chooser is on screen. A second one must not be opened over it.</summary>
    public bool Busy => _state == Asking;

    /// <summary>
    /// True when this machine can show a chooser at all.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Asked rather than assumed, because the box it replaces still has to be there.</b> The
    /// shell chooser is Windows only; on anything else the typed path is not a fallback, it is the
    /// only route, and the screen has to keep saying so rather than offering a button that does
    /// nothing.
    /// </remarks>
    public static bool Available => OperatingSystem.IsWindows();

    /// <summary>
    /// Opens the chooser. False when one is already open or this machine has none.
    /// </summary>
    /// <param name="want">A file or a folder.</param>
    /// <param name="owner">The game's own window, so the dialog cannot end up behind it.</param>
    /// <param name="title">What the dialog's title bar says.</param>
    /// <param name="filterLabel">What to call the kinds of file offered, in the drop-down.</param>
    /// <param name="filterSpec">The semicolon-separated masks, e.g. <c>*.zip;*.mcpack</c>.</param>
    /// <remarks>
    /// ⚠ <b>The owner handle comes from a different thread than the one that shows the dialog</b>,
    /// which is allowed and is the point: an owned window is always drawn above its owner, so a
    /// full-screen game cannot swallow the chooser and leave a player looking at a frozen menu
    /// waiting for a window that is already open behind it.
    /// </remarks>
    public bool Ask(Want want, nint owner, string title, string filterLabel, string filterSpec)
    {
        // ⚠ Written out here rather than behind Available, because the platform analyser follows a
        // test and not a property: the whole of the Win32 half below is Windows-only, and this line
        // is what tells the compiler so.
        if (!OperatingSystem.IsWindows() || _state != Idle) return false;

        _picked = null;
        _why = "";
        _state = Asking;

        StartChooser(want, owner, title, filterLabel, filterSpec);
        return true;
    }

    /// <summary>Puts the chooser on a thread of its own and leaves it there.</summary>
    [SupportedOSPlatform("windows")]
    private void StartChooser(Want want, nint owner, string title, string filterLabel, string filterSpec)
    {
        var thread = new Thread(() =>
        {
            string? path = null;
            var why = "";

            try
            {
                path = Show(want, owner, title, filterLabel, filterSpec, out why);
            }
            catch (Exception error)
            {
                // ⛔ EVERYTHING, and deliberately so. This runs on a thread of its own, and an
                // exception escaping a thread takes the whole process with it — which for the sake
                // of a file dialog would mean losing the world the player is standing in. The shell
                // can fail for reasons that have nothing to do with us (a policy, a shell extension
                // somebody installed), and the honest answer to all of them is a line on the screen.
                why = error.Message;
            }

            // The answer first, the flag second. The game thread reads them the other way round,
            // which is what makes the pair safe without a lock.
            _picked = path;
            _why = why;
            _state = Answered;
        })
        {
            IsBackground = true,
            Name = "pack chooser",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>
    /// Collects an answer if one has arrived. True exactly once per <see cref="Ask"/>.
    /// </summary>
    /// <param name="path">What was chosen, or null when it was cancelled or went wrong.</param>
    /// <param name="why">Empty when it was simply cancelled; otherwise what went wrong.</param>
    public bool TryTake(out string? path, out string why)
    {
        path = null;
        why = "";

        if (_state != Answered) return false;

        path = _picked;
        why = _why;

        _picked = null;
        _why = "";
        _state = Idle;
        return true;
    }

    /// <summary>
    /// Builds a real chooser and works it, without ever showing one. Returns what is wrong with it.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>THE FAILURE THIS EXISTS FOR IS SILENT AND IS INVISIBLE ON THIS MACHINE.</b> A COM
    /// interface is a table of function pointers: the ids and the method order below are a contract
    /// with Windows that the compiler cannot check and that nothing in the game will complain about.
    /// One wrong digit in an interface id and the button does nothing at all; one method left out of
    /// the declaration and every call after it lands on the wrong function. Both arrive as "the
    /// browse button doesn't work" from somebody else, days later.</para>
    /// <para>⛳ <b>Round trips, not calls that returned zero.</b> Setting a flag and reading it back
    /// is what proves two slots are the same property rather than two functions that both happened
    /// to succeed. And <c>GetResult</c> is asked for on purpose <em>before</em> anything is chosen:
    /// it is the deepest slot declared, and it MUST fail there. A declaration one method short lands
    /// that call on <c>SetFileNameLabel</c>, which cheerfully returns success — so "it failed" is
    /// the only answer that says the table is the right length.</para>
    /// <para>⚠ It shows nothing and waits for nobody, so it belongs on the gate.</para>
    /// </remarks>
    public static List<string> SelfTest(out string detail)
    {
        var faults = new List<string>();
        detail = "";

        if (!OperatingSystem.IsWindows())
        {
            detail = "not Windows, so there is no chooser to build";
            return faults;
        }

        if (!Available)
        {
            faults.Add("the file chooser reports itself unavailable on Windows");
            return faults;
        }

        detail = RunProbe(faults);
        return faults;
    }

    /// <summary>Runs the probe on an apartment of its own — the same one the real chooser gets.</summary>
    [SupportedOSPlatform("windows")]
    private static string RunProbe(List<string> faults)
    {
        var told = "";
        var thread = new Thread(() =>
        {
            try
            {
                Probe(faults, out told);
            }
            catch (Exception error)
            {
                faults.Add($"building a file chooser threw {error.GetType().Name}: {error.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "pack chooser check",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // Nothing here waits on a person, so a wait that does not end is a fault of its own.
        if (!thread.Join(TimeSpan.FromSeconds(10)))
            faults.Add("building a file chooser did not finish in ten seconds");

        return told;
    }

    [SupportedOSPlatform("windows")]
    private static void Probe(List<string> faults, out string detail)
    {
        detail = "";

        var type = Type.GetTypeFromCLSID(FileOpenDialogClass);
        if (type is null)
        {
            faults.Add("Windows has no class registered for the file chooser");
            return;
        }

        var dialog = (IFileOpenDialog)Activator.CreateInstance(type)!;

        try
        {
            if (dialog.GetOptions(out var options) < 0)
            {
                faults.Add("a file chooser was built and would not say what its options are");
                return;
            }

            // ⛳ Set a flag, read it back. Two slots that are the same property, or two that are not.
            if (dialog.SetOptions(options | FosPickFolders) < 0)
                faults.Add("the file chooser refused to be put into folder mode");
            else if (dialog.GetOptions(out var back) < 0 || (back & FosPickFolders) == 0)
                faults.Add("folder mode was set on the file chooser and did not stay set");

            dialog.SetOptions(options);

            // The masks the shelf actually offers, through the real marshalling, then the index
            // round-tripped — which is the only thing that proves SetFileTypes took them at all.
            FilterSpec[] filters =
            [
                new() { Name = "check", Spec = "*.zip" },
                new() { Name = "every file", Spec = "*.*" },
            ];

            if (dialog.SetFileTypes((uint)filters.Length, filters) < 0)
                faults.Add("the file chooser refused a list of file types");
            else if (dialog.SetFileTypeIndex(2) < 0
                     || dialog.GetFileTypeIndex(out var picked) < 0
                     || picked != 2)
                faults.Add("the chosen file type did not survive being set");

            if (dialog.SetTitle("Driftwood") < 0) faults.Add("the file chooser refused a title");

            // ⛔ THE ONE THAT MUST FAIL. See the remarks: success here means the table is short.
            if (dialog.GetResult(out var nothing) >= 0)
                faults.Add("the file chooser handed back a result before anything was chosen, "
                         + "which means a method is missing from the interface declaration");
            else if (nothing is not null)
                Marshal.ReleaseComObject(nothing);

            detail = $"a real chooser built, folder mode set and read back, "
                   + $"{filters.Length} file types taken, no result before one is chosen";
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    /// <summary>The whole of the Win32 side: open the shell's chooser and read back one path.</summary>
    [SupportedOSPlatform("windows")]
    private static string? Show(
        Want want, nint owner, string title, string filterLabel, string filterSpec, out string why)
    {
        why = "";

        var type = Type.GetTypeFromCLSID(FileOpenDialogClass);
        if (type is null)
        {
            why = "this copy of Windows has no file chooser";
            return null;
        }

        // A plain cast rather than a test: it is a QueryInterface, and when it fails the reason is
        // worth having. The caller catches it and puts it on the screen.
        var dialog = (IFileOpenDialog)Activator.CreateInstance(type)!;
        IShellItem? chosen = null;

        try
        {
            // ⚠ Read, or-ed, written back — never assigned outright. The shell puts sensible
            // defaults in there and the two flags that matter here are additions to them, not a
            // replacement for them.
            if (dialog.GetOptions(out var options) < 0) options = 0;

            // FORCEFILESYSTEM is what keeps the answer a path. Without it a player can pick out of
            // a library or a phone plugged into the machine, and what comes back is a shell item
            // with no file behind it at all — which our importer would then be handed and refuse.
            options |= FosForceFileSystem | FosPathMustExist | FosNoChangeDir;
            options |= want == Want.Folder ? FosPickFolders : FosFileMustExist;

            dialog.SetOptions(options);
            dialog.SetTitle(title);

            if (want == Want.File)
            {
                // ⛳ The masks come from the shelf's own list of what it accepts, so a shape the
                // importer would take can never be a shape the browser hides. "All files" is under
                // it on purpose: a pack downloaded with the wrong extension is a real thing, and a
                // browser that cannot show it leaves the player with no way to even try.
                FilterSpec[] filters =
                [
                    new() { Name = filterLabel, Spec = filterSpec },
                    new() { Name = "every file", Spec = "*.*" },
                ];

                dialog.SetFileTypes((uint)filters.Length, filters);
                dialog.SetFileTypeIndex(1);
            }

            var shown = dialog.Show(owner);

            // ⚠ Cancelled is a result, not a fault, and it is the commonest one. It arrives as a
            // Win32 error code wrapped in an HRESULT, so it has to be told apart by value.
            if (shown == Cancelled) return null;

            if (shown < 0)
            {
                why = $"the file chooser would not open (0x{shown:X8})";
                return null;
            }

            if (dialog.GetResult(out chosen) < 0 || chosen is null)
            {
                why = "the file chooser closed without choosing anything";
                return null;
            }

            if (chosen.GetDisplayName(SigdnFileSysPath, out var text) < 0 || text == 0)
            {
                why = "what was chosen is not a file on this machine";
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(text);
            }
            finally
            {
                Marshal.FreeCoTaskMem(text);
            }
        }
        finally
        {
            if (chosen is not null) Marshal.ReleaseComObject(chosen);
            Marshal.ReleaseComObject(dialog);
        }
    }

    // ⚠ Every one of these was read out of the Windows SDK's own ShObjIdl_core.h rather than
    // remembered. A wrong digit in an interface id does not fail to compile and does not throw
    // anywhere useful — it comes back as a cast that will not take, at the moment somebody presses
    // the button, on their machine and not on this one.
    private static readonly Guid FileOpenDialogClass = new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");

    /// <summary>HRESULT_FROM_WIN32(ERROR_CANCELLED) — the player closed the dialog.</summary>
    private const int Cancelled = unchecked((int)0x800704C7);

    /// <summary>SIGDN_FILESYSPATH: give me the path, not the pretty name.</summary>
    private const uint SigdnFileSysPath = 0x80058000;

    private const uint FosNoChangeDir = 0x8;
    private const uint FosPickFolders = 0x20;
    private const uint FosForceFileSystem = 0x40;
    private const uint FosPathMustExist = 0x800;
    private const uint FosFileMustExist = 0x1000;
}

/// <summary>One line of the chooser's "files of type" drop-down. COMDLG_FILTERSPEC.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct FilterSpec
{
    [MarshalAs(UnmanagedType.LPWStr)] public string Name;
    [MarshalAs(UnmanagedType.LPWStr)] public string Spec;
}

/// <summary>
/// The shell's file chooser, as much of it as is needed to open one and read one path back.
/// </summary>
/// <remarks>
/// ⛔ <b>THE ORDER OF THESE METHODS IS THE INTERFACE.</b> A COM interface is a table of function
/// pointers and nothing else; the names here are ours and mean nothing to Windows. Inserting a
/// method, or leaving one out, silently shifts every call after it onto the wrong function — so the
/// inherited ones are all written out, in the order the SDK header declares them, including the
/// ones this game never calls. The chain is IUnknown, then IModalWindow (Show), then IFileDialog,
/// then this. Everything from <c>AddPlace</c> onward is unused and therefore absent, which is safe
/// only because it is at the END.
/// </remarks>
[ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOpenDialog
{
    // IModalWindow
    [PreserveSig] int Show(nint owner);

    // IFileDialog
    [PreserveSig] int SetFileTypes(uint count, [In, MarshalAs(UnmanagedType.LPArray)] FilterSpec[] filters);
    [PreserveSig] int SetFileTypeIndex(uint index);
    [PreserveSig] int GetFileTypeIndex(out uint index);
    [PreserveSig] int Advise(nint events, out uint cookie);
    [PreserveSig] int Unadvise(uint cookie);
    [PreserveSig] int SetOptions(uint options);
    [PreserveSig] int GetOptions(out uint options);
    [PreserveSig] int SetDefaultFolder(nint item);
    [PreserveSig] int SetFolder(nint item);
    [PreserveSig] int GetFolder(out nint item);
    [PreserveSig] int GetCurrentSelection(out nint item);
    [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
    [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
    [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
    [PreserveSig] int GetResult(out IShellItem? item);
}

/// <summary>A thing in the shell's namespace. Here, only ever the file that was chosen.</summary>
/// <remarks>⛔ Same rule as above: the order is the interface.</remarks>
[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig] int BindToHandler(nint context, in Guid handler, in Guid iid, out nint got);
    [PreserveSig] int GetParent(out IShellItem? parent);

    /// <summary>The name in the form asked for. <c>SIGDN_FILESYSPATH</c> is the one with a drive on it.</summary>
    [PreserveSig] int GetDisplayName(uint form, out nint name);

    [PreserveSig] int GetAttributes(uint mask, out uint attributes);
    [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
}
