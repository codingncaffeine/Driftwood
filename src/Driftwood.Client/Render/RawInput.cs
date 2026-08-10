using System.Numerics;
using Silk.NET.GLFW;
using Silk.NET.Input;
using Silk.NET.Windowing;

using GlfwKeys = Silk.NET.GLFW.Keys;
using GlfwButton = Silk.NET.GLFW.MouseButton;

namespace Driftwood.Client.Render;

/// <summary>
/// Keyboard and mouse, taken straight off the window, without making controller discovery eager.
/// </summary>
/// <remarks>
/// <para>⛔ <b>This exists for one measured reason.</b> Asking the input library for a context builds
/// sixteen joystick wrappers and sixteen gamepad wrappers, and the first of them makes the platform
/// initialise its whole joystick stack. On the machine this was written on that took <b>10.3
/// seconds</b> — ninety seven percent of Driftwood's entire startup, against twelve milliseconds to
/// build every texture in the game. It is not work: three runs varied by eighteen milliseconds,
/// which is a device being waited on rather than something being counted. A controller that is
/// paired and switched off is enough to do it.</para>
/// <para>This sets only the callbacks keyboard and mouse need. P8's controller path is a separate
/// SDL3 owner, initialized on the main thread only after the first rendered frame; it never comes
/// back through Silk's eager context or onto the path between double-clicking and seeing the game.
/// </para>
/// <para><b>The key numbers are the same numbers.</b> <see cref="Key"/> and the window library's own
/// key enum are both the underlying platform's key codes, so a cast is the whole translation. That
/// is an assumption about somebody else's two enums agreeing, so the audit pins a handful of them
/// rather than trusting it — see <c>KeyNumbersMatch</c>.</para>
/// </remarks>
public sealed unsafe class RawInput : IDisposable
{
    private readonly Glfw _glfw;
    private readonly WindowHandle* _handle;

    // ⚠ Held in fields on purpose. These are handed to native code as function pointers, and a
    // delegate nothing references is a delegate the collector is free to take away — after which
    // the first keypress calls into freed memory. It is not a leak; it is the lifetime.
    private readonly GlfwCallbacks.KeyCallback _onKey;
    private readonly GlfwCallbacks.CharCallback _onChar;
    private readonly GlfwCallbacks.CursorPosCallback _onCursor;
    private readonly GlfwCallbacks.MouseButtonCallback _onButton;
    private readonly GlfwCallbacks.ScrollCallback _onScroll;

    private bool _closed;

    /// <summary>A key going down, including the repeats a held key sends.</summary>
    public event Action<Key>? KeyDown;

    /// <summary>
    /// A character somebody typed, which is a different question from which key went down.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>A key is a place on a keyboard and a character is what it produced</b>, and the two are
    /// only the same thing on one layout. Shift and the key give a capital, a dead key and a vowel
    /// give an accented letter, and on a French keyboard the key at position <c>A</c> types a
    /// <c>q</c>. The platform already knows all of that; asking it for the character is the whole of
    /// what makes a text box work for somebody who does not have the keyboard this was written on.
    /// </remarks>
    public event Action<char>? CharTyped;

    public event Action<Silk.NET.Input.MouseButton>? MouseDown;
    public event Action<Silk.NET.Input.MouseButton>? MouseUp;
    public event Action<Vector2>? MouseMove;

    /// <summary>How far the wheel turned, positive away from the hand.</summary>
    public event Action<float>? Scroll;

    /// <summary>Where the pointer is, in window coordinates.</summary>
    public Vector2 Position { get; private set; }

    /// <summary>True when this could not attach to the window and the game has no input.</summary>
    public bool Failed => _handle is null;

    public RawInput(IWindow window)
    {
        _glfw = Glfw.GetApi();

        var native = window.Native?.Glfw;
        if (native is not { } pointer)
        {
            // Nothing to attach to. Reported by the caller rather than thrown: a game that will not
            // start because it could not find a window handle is worse than one that says so.
            _handle = null;
            _onKey = (_, _, _, _, _) => { };
            _onChar = (_, _) => { };
            _onCursor = (_, _, _) => { };
            _onButton = (_, _, _, _) => { };
            _onScroll = (_, _, _) => { };
            return;
        }

        _handle = (WindowHandle*)pointer;

        _onKey = (_, key, _, action, _) =>
        {
            if (action is InputAction.Press or InputAction.Repeat) KeyDown?.Invoke((Key)key);
        };

        // Anything outside the basic plane arrives as a surrogate pair the font has no glyph for
        // either way, so it is dropped here rather than truncated into a different letter.
        _onChar = (_, codepoint) =>
        {
            if (codepoint <= char.MaxValue) CharTyped?.Invoke((char)codepoint);
        };

        _onCursor = (_, x, y) =>
        {
            Position = new Vector2((float)x, (float)y);
            MouseMove?.Invoke(Position);
        };

        _onButton = (_, button, action, _) =>
        {
            if (action == InputAction.Press) MouseDown?.Invoke((Silk.NET.Input.MouseButton)button);
            else if (action == InputAction.Release) MouseUp?.Invoke((Silk.NET.Input.MouseButton)button);
        };

        // The vertical wheel only. A horizontal one exists on some mice and means nothing here.
        _onScroll = (_, _, dy) => Scroll?.Invoke((float)dy);

        _glfw.SetKeyCallback(_handle, _onKey);
        _glfw.SetCharCallback(_handle, _onChar);
        _glfw.SetCursorPosCallback(_handle, _onCursor);
        _glfw.SetMouseButtonCallback(_handle, _onButton);
        _glfw.SetScrollCallback(_handle, _onScroll);

        _glfw.GetCursorPos(_handle, out var startX, out var startY);
        Position = new Vector2((float)startX, (float)startY);
    }

    /// <summary>True while the key is down, asked of the window rather than tracked here.</summary>
    public bool IsKeyPressed(Key key)
    {
        if (_handle is null || key == Key.Unknown) return false;
        return _glfw.GetKey(_handle, (GlfwKeys)key) == (int)InputAction.Press;
    }

    /// <summary>
    /// Sets what the pointer does: left alone, hidden, or taken outright.
    /// </summary>
    /// <remarks>
    /// <para><b>Three states, and the middle one is the one that matters.</b> Playing takes the
    /// pointer — locked to the window, no acceleration, no edge to hit. A screen <em>hides</em> it
    /// instead: the position stays real window coordinates, which is what makes hit testing a
    /// division rather than a running total of deltas, and the arrow drawn is ours.</para>
    /// <para>Raw motion is asked for and not insisted on. It is unsupported on some platforms and
    /// the library says so by refusing; a locked cursor with the machine's acceleration on it is
    /// worse to aim with and still perfectly playable, which is not true of no cursor lock at all.
    /// </para>
    /// </remarks>
    public void SetCursor(CursorMode mode)
    {
        if (_handle is null) return;

        _glfw.SetInputMode(_handle, CursorStateAttribute.Cursor, mode switch
        {
            CursorMode.Raw or CursorMode.Disabled => CursorModeValue.CursorDisabled,
            CursorMode.Hidden => CursorModeValue.CursorHidden,
            _ => CursorModeValue.CursorNormal,
        });

        if (mode == CursorMode.Raw && _glfw.RawMouseMotionSupported())
            _glfw.SetInputMode(_handle, CursorStateAttribute.RawMouseMotion, true);
        else if (_glfw.RawMouseMotionSupported())
            _glfw.SetInputMode(_handle, CursorStateAttribute.RawMouseMotion, false);
    }

    /// <summary>Whatever is on the system clipboard, or nothing at all.</summary>
    /// <remarks>
    /// Read when it is asked for rather than watched. Nothing wants to know what is on a clipboard
    /// until somebody presses paste, and a platform call per frame for that would be absurd.
    /// </remarks>
    public string Clipboard
    {
        get
        {
            if (_handle is null) return "";

            try { return _glfw.GetClipboardString(_handle) ?? ""; }
            catch (Exception) { return ""; }
        }
    }

    /// <summary>Puts the pointer somewhere, in window coordinates.</summary>
    public void MoveTo(Vector2 position)
    {
        if (_handle is null) return;

        Position = position;
        _glfw.SetCursorPos(_handle, position.X, position.Y);
    }

    public void Dispose()
    {
        if (_closed || _handle is null) return;
        _closed = true;

        // Handed back before the window goes, so a callback cannot arrive into a torn-down host.
        _glfw.SetKeyCallback(_handle, null!);
        _glfw.SetCharCallback(_handle, null!);
        _glfw.SetCursorPosCallback(_handle, null!);
        _glfw.SetMouseButtonCallback(_handle, null!);
        _glfw.SetScrollCallback(_handle, null!);
    }

    /// <summary>
    /// Whether this library's key numbers really are the window library's key numbers.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The one assumption the cast rests on, so it is measured rather than believed.</b> Both
    /// enums are meant to be the platform's own key codes, and they are — but they are two enums in
    /// two packages maintained by different hands, and the day one of them renumbers, every key in
    /// the game silently becomes a different key. Not a crash: a game where W walks left.
    /// <para>A handful spread across the ranges that are numbered differently from each other —
    /// printable characters, the function block, the arrows, the modifiers and the keypad — rather
    /// than the whole table, which would be this file written twice.</para>
    /// </remarks>
    public static List<string> KeyNumbersMatch()
    {
        (Key Ours, GlfwKeys Theirs)[] pinned =
        [
            (Key.Space, GlfwKeys.Space),
            (Key.A, GlfwKeys.A),
            (Key.Z, GlfwKeys.Z),
            (Key.Number0, GlfwKeys.Number0),
            (Key.Escape, GlfwKeys.Escape),
            (Key.Enter, GlfwKeys.Enter),
            (Key.Tab, GlfwKeys.Tab),
            (Key.Backspace, GlfwKeys.Backspace),
            (Key.Right, GlfwKeys.Right),
            (Key.Left, GlfwKeys.Left),
            (Key.Down, GlfwKeys.Down),
            (Key.Up, GlfwKeys.Up),
            (Key.F1, GlfwKeys.F1),
            (Key.F12, GlfwKeys.F12),
            (Key.Keypad0, GlfwKeys.Keypad0),
            (Key.KeypadEnter, GlfwKeys.KeypadEnter),
            (Key.ShiftLeft, GlfwKeys.ShiftLeft),
            (Key.ControlLeft, GlfwKeys.ControlLeft),
            (Key.ShiftRight, GlfwKeys.ShiftRight),
            (Key.ControlRight, GlfwKeys.ControlRight),
        ];

        var faults = new List<string>();

        foreach (var (ours, theirs) in pinned)
            if ((int)ours != (int)theirs)
                faults.Add($"{ours} is {(int)ours} here and {(int)theirs} to the window library");

        (Silk.NET.Input.MouseButton Ours, GlfwButton Theirs)[] buttons =
        [
            (Silk.NET.Input.MouseButton.Left, GlfwButton.Left),
            (Silk.NET.Input.MouseButton.Right, GlfwButton.Right),
            (Silk.NET.Input.MouseButton.Middle, GlfwButton.Middle),
        ];

        foreach (var (ours, theirs) in buttons)
            if ((int)ours != (int)theirs)
                faults.Add($"the {ours} button is {(int)ours} here and {(int)theirs} over there");

        return faults;
    }
}
