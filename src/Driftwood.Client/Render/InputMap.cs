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
    private readonly Dictionary<Key, GameAction> _byKey = [];

    public InputMap(Bindings bindings) => Rebuild(bindings);

    public void Rebuild(Bindings bindings)
    {
        _byKey.Clear();

        foreach (var action in GameActions.All)
        {
            _primary[(int)action] = Resolve(bindings.Primary(action));
            _secondary[(int)action] = Resolve(bindings.Secondary(action));

            if (_primary[(int)action] != Key.Unknown) _byKey[_primary[(int)action]] = action;
            if (_secondary[(int)action] != Key.Unknown) _byKey[_secondary[(int)action]] = action;
        }
    }

    /// <summary>True while either of an action's keys is down.</summary>
    public bool Held(RawInput input, GameAction action) =>
        (_primary[(int)action] != Key.Unknown && input.IsKeyPressed(_primary[(int)action]))
        || (_secondary[(int)action] != Key.Unknown && input.IsKeyPressed(_secondary[(int)action]));

    /// <summary>What a key press means, or null when nothing is bound to it.</summary>
    public GameAction? ActionFor(Key key) => _byKey.TryGetValue(key, out var action) ? action : null;

    /// <summary>The name a key is stored under. Empty for anything unnameable.</summary>
    public static string NameOf(Key key) => key == Key.Unknown ? "" : key.ToString();

    private static Key Resolve(string name) =>
        name.Length > 0 && Enum.TryParse<Key>(name, ignoreCase: true, out var key) ? key : Key.Unknown;
}
