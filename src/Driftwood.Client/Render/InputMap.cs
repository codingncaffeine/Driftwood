using Driftwood.Core.Settings;
using Silk.NET.Input;

namespace Driftwood.Client.Render;

/// <summary>
/// Turns the bindings, which are names, into the keys the input library actually reports.
/// </summary>
/// <remarks>
/// <para>The names live in Core so the table can be saved, hand-edited and checked without Core
/// knowing what an input library is. Resolving them is this side's job, and it happens once when
/// the bindings change rather than on every key that arrives — walking forward tests four actions a
/// frame, and doing that with string comparisons would be a string comparison per frame per key
/// for the whole session.</para>
/// <para>A name that resolves to nothing is dropped rather than refused. A settings file naming a
/// key this build does not have should cost that one binding, not the launch.</para>
/// </remarks>
public sealed class InputMap
{
    private readonly Key[] _primary = new Key[GameActions.All.Length];
    private readonly Key[] _secondary = new Key[GameActions.All.Length];

    /// <summary>Which action each key runs, for the ones that fire on a press.</summary>
    // ⛳ A binding can be a mouse button as well as a key (2026-10-16), so the two hands can move
    // off the mouse and anything else can move onto it. Null means the slot is a key, or empty.
    private readonly MouseButton?[] _primaryButton = new MouseButton?[GameActions.All.Length];
    private readonly MouseButton?[] _secondaryButton = new MouseButton?[GameActions.All.Length];

    private readonly Dictionary<Key, GameAction> _byKey = [];
    private readonly Dictionary<MouseButton, GameAction> _byButton = [];

    public InputMap(Bindings bindings) => Rebuild(bindings);

    public void Rebuild(Bindings bindings)
    {
        _byKey.Clear();
        _byButton.Clear();

        foreach (var action in GameActions.All)
        {
            _primary[(int)action] = Resolve(bindings.Primary(action));
            _secondary[(int)action] = Resolve(bindings.Secondary(action));
            _primaryButton[(int)action] = ButtonOf(bindings.Primary(action));
            _secondaryButton[(int)action] = ButtonOf(bindings.Secondary(action));

            if (_primary[(int)action] != Key.Unknown) _byKey[_primary[(int)action]] = action;
            if (_secondary[(int)action] != Key.Unknown) _byKey[_secondary[(int)action]] = action;
            if (_primaryButton[(int)action] is { } first) _byButton[first] = action;
            if (_secondaryButton[(int)action] is { } second) _byButton[second] = action;
        }
    }

    /// <summary>True while either of an action's keys is down.</summary>
    public bool Held(RawInput input, GameAction action) =>
        (_primary[(int)action] != Key.Unknown && input.IsKeyPressed(_primary[(int)action]))
        || (_secondary[(int)action] != Key.Unknown && input.IsKeyPressed(_secondary[(int)action]))
        || (_primaryButton[(int)action] is { } first && input.IsMouseButtonPressed(first))
        || (_secondaryButton[(int)action] is { } second && input.IsMouseButtonPressed(second));

    /// <summary>What a key press means, or null when nothing is bound to it.</summary>
    public GameAction? ActionFor(Key key) => _byKey.TryGetValue(key, out var action) ? action : null;

    /// <summary>The name a key is stored under. Empty for anything unnameable.</summary>
    public static string NameOf(Key key) => key == Key.Unknown ? "" : key.ToString();

    public GameAction? ActionFor(MouseButton button) => _byButton.TryGetValue(button, out var action) ? action : null;

    /// <summary>The binding name of a mouse button, or empty for one nothing can be bound to.</summary>
    public static string NameOf(MouseButton button) => button switch
    {
        MouseButton.Left => Bindings.MouseLeft,
        MouseButton.Right => Bindings.MouseRight,
        MouseButton.Middle => Bindings.MouseMiddle,
        MouseButton.Button4 => Bindings.Mouse4,
        MouseButton.Button5 => Bindings.Mouse5,
        _ => "",
    };

    private static MouseButton? ButtonOf(string name) => name switch
    {
        Bindings.MouseLeft => MouseButton.Left,
        Bindings.MouseRight => MouseButton.Right,
        Bindings.MouseMiddle => MouseButton.Middle,
        Bindings.Mouse4 => MouseButton.Button4,
        Bindings.Mouse5 => MouseButton.Button5,
        _ => null,
    };

    private static Key Resolve(string name) =>
        name.Length > 0 && Enum.TryParse<Key>(name, ignoreCase: true, out var key) ? key : Key.Unknown;
}
