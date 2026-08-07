namespace Driftwood.Core.Settings;

/// <summary>
/// Which key does what, and the second key that does it too.
/// </summary>
/// <remarks>
/// <para>Keys are held as <em>names</em> rather than as codes. The name is what goes in the settings
/// file, what appears on the controls screen, and what a player types when they hand-edit it — and
/// it means this whole table lives in Core, where it can be checked, without Core ever knowing what
/// an input library is. The client turns a name into a key once, when the bindings change.</para>
/// <para>Two keys per action, because this project's own player wants the arrows and everybody
/// else's muscle memory wants WASD, and making that a choice rather than a pair would be picking a
/// fight nobody needs to have. The second is optional and usually empty.</para>
/// </remarks>
public sealed class Bindings
{
    private readonly string[] _primary = new string[GameActions.All.Length];
    private readonly string[] _secondary = new string[GameActions.All.Length];

    public Bindings()
    {
        Array.Fill(_primary, "");
        Array.Fill(_secondary, "");
    }

    /// <summary>
    /// The keys this project ships with.
    /// </summary>
    /// <remarks>
    /// Arrows first and WASD second, which is the way round the person who plays this asked for.
    /// Both are live either way; the order only decides which one the controls screen shows first.
    /// </remarks>
    public static Bindings Defaults()
    {
        var bindings = new Bindings();

        bindings.Set(GameAction.MoveForward, "Up", "W");
        bindings.Set(GameAction.MoveBack, "Down", "S");
        bindings.Set(GameAction.MoveLeft, "Left", "A");
        bindings.Set(GameAction.MoveRight, "Right", "D");
        bindings.Set(GameAction.Jump, "Space");
        bindings.Set(GameAction.Sneak, "ControlLeft");
        bindings.Set(GameAction.Sprint, "ShiftLeft");

        // ⚠ Two, and the pair is the point again: V falls under a left hand on WASD, right control
        // under a right hand on the arrows. This project's own player uses the arrows, and a shield
        // key only that player's other hand can reach is a shield they never raise.
        bindings.Set(GameAction.RaiseShield, "V", "ControlRight");

        // I is what this project's player reaches for; E is what the genre trained everybody else
        // to press. Two keys per action exists precisely so that is not a choice anybody has to
        // make. Escape opens the options and gives the mouse back, which is one gesture everywhere.
        bindings.Set(GameAction.OpenInventory, "I", "E");
        bindings.Set(GameAction.OpenOptions, "Escape");

        bindings.Set(GameAction.ToggleView, "F5");
        bindings.Set(GameAction.ToggleFly, "F3");
        bindings.Set(GameAction.ToggleWireframe, "F1");
        bindings.Set(GameAction.ToggleCulling, "F2");
        bindings.Set(GameAction.HoldClock, "F6");
        bindings.Set(GameAction.WindClock, "F7");

        for (var i = 0; i < 9; i++)
            bindings.Set(GameAction.Slot1 + i, $"Number{i + 1}");

        return bindings;
    }

    public string Primary(GameAction action) => _primary[(int)action];

    public string Secondary(GameAction action) => _secondary[(int)action];

    /// <summary>What the controls screen shows on one line.</summary>
    public string Describe(GameAction action)
    {
        var first = Primary(action);
        var second = Secondary(action);

        if (first.Length == 0 && second.Length == 0) return "unbound";
        if (second.Length == 0) return first;
        return $"{first} or {second}";
    }

    /// <summary>
    /// Binds a key, taking it off whatever else was using it.
    /// </summary>
    /// <remarks>
    /// Stealing rather than refusing. A player rebinding jump to E does not want to be told that E
    /// is busy — they want jump on E, and they will notice the screen has lost its key the moment
    /// they look at the row above. Refusing would mean unbinding first and remembering to, which is
    /// the kind of two-step nobody finishes.
    /// </remarks>
    public void Bind(GameAction action, string key, bool secondary = false)
    {
        if (key.Length > 0)
        {
            for (var i = 0; i < _primary.Length; i++)
            {
                if (string.Equals(_primary[i], key, StringComparison.Ordinal)) _primary[i] = "";
                if (string.Equals(_secondary[i], key, StringComparison.Ordinal)) _secondary[i] = "";
            }
        }

        if (secondary) _secondary[(int)action] = key;
        else _primary[(int)action] = key;
    }

    /// <summary>Sets both keys of an action without disturbing anything else. Startup only.</summary>
    public void Set(GameAction action, string primary, string secondary = "")
    {
        _primary[(int)action] = primary;
        _secondary[(int)action] = secondary;
    }

    /// <summary>Which action a key runs, or null. First match wins.</summary>
    public GameAction? ActionFor(string key)
    {
        if (key.Length == 0) return null;

        for (var i = 0; i < _primary.Length; i++)
        {
            if (string.Equals(_primary[i], key, StringComparison.Ordinal)) return (GameAction)i;
            if (string.Equals(_secondary[i], key, StringComparison.Ordinal)) return (GameAction)i;
        }

        return null;
    }

    /// <summary>Everything wrong with this table, for the audit and for a hand-edited file.</summary>
    /// <remarks>
    /// An action with no key at all is the fault worth naming loudest: it is a feature the player
    /// can no longer reach, and nothing about the running game says so.
    /// </remarks>
    public List<string> Faults()
    {
        var faults = new List<string>();
        var seen = new Dictionary<string, GameAction>(StringComparer.Ordinal);

        foreach (var action in GameActions.All)
        {
            if (Primary(action).Length == 0 && Secondary(action).Length == 0)
                faults.Add($"'{GameActions.Label(action)}' has no key on it");

            foreach (var key in (string[])[Primary(action), Secondary(action)])
            {
                if (key.Length == 0) continue;

                if (seen.TryGetValue(key, out var other))
                    faults.Add($"'{key}' runs both '{GameActions.Label(other)}' and '{GameActions.Label(action)}'");
                else
                    seen[key] = action;
            }
        }

        return faults;
    }

    /// <summary>
    /// Gives a key back to any action that has none, from another table, without disturbing the rest.
    /// </summary>
    /// <remarks>
    /// The upgrade path for a renamed action. A key already in use is skipped rather than bound
    /// twice — a default arriving late must not steal a key the player deliberately moved.
    /// </remarks>
    public void FillGapsFrom(Bindings source)
    {
        foreach (var action in GameActions.All)
        {
            if (Primary(action).Length > 0 || Secondary(action).Length > 0) continue;

            var first = source.Primary(action);
            var second = source.Secondary(action);

            if (first.Length > 0 && ActionFor(first) is not null) first = "";
            if (second.Length > 0 && ActionFor(second) is not null) second = "";
            if (first.Length == 0 && second.Length > 0) (first, second) = (second, "");

            Set(action, first, second);
        }
    }

    public Bindings Copy()
    {
        var copy = new Bindings();
        foreach (var action in GameActions.All) copy.Set(action, Primary(action), Secondary(action));
        return copy;
    }
}
