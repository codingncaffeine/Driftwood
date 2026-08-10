using System.Numerics;

namespace Driftwood.Core.Settings;

/// <summary>The stable, layout-independent controls exposed by a modern gamepad.</summary>
/// <remarks>
/// These are positions and mechanisms, not the letters printed on one manufacturer's plastic.
/// SDL calls the bottom face button <c>South</c> whether it is marked A or a cross; keeping that
/// distinction in the settings file is what lets the same binding follow a player from one pad to
/// another without turning "jump" into a different physical button.
/// </remarks>
public enum ControllerControl
{
    None,
    South,
    East,
    West,
    North,
    Back,
    Start,
    LeftStick,
    RightStick,
    LeftShoulder,
    RightShoulder,
    DPadUp,
    DPadDown,
    DPadLeft,
    DPadRight,
    LeftTrigger,
    RightTrigger,
}

/// <summary>The controller gestures Driftwood lets a player move around.</summary>
/// <remarks>
/// Movement and looking are analogue axes and stay axes. Everything here is a discrete intent,
/// including the triggers after they cross their threshold, so rebinding never has to pretend a
/// stick is four keyboard keys or teach Core about SDL.
/// </remarks>
public enum ControllerAction
{
    Jump,
    Sneak,
    Sprint,
    BreakOrAttack,
    UseOrPlace,
    RaiseShield,
    SwapHands,
    PreviousSlot,
    NextSlot,
    RadialHotbar,
    OpenInventory,
    OpenOptions,
    ToggleView,
}

public static class ControllerActions
{
    public static readonly ControllerAction[] All = Enum.GetValues<ControllerAction>();

    public static string GroupOf(ControllerAction action) => action switch
    {
        <= ControllerAction.Sprint => "moving",
        <= ControllerAction.SwapHands => "using things",
        <= ControllerAction.RadialHotbar => "the bar",
        _ => "screens",
    };

    public static string Label(ControllerAction action) => action switch
    {
        ControllerAction.Jump => "jump",
        ControllerAction.Sneak => "sneak / dismount",
        ControllerAction.Sprint => "sprint",
        ControllerAction.BreakOrAttack => "break / attack",
        ControllerAction.UseOrPlace => "use / place",
        ControllerAction.RaiseShield => "raise shield",
        ControllerAction.SwapHands => "swap hands",
        ControllerAction.PreviousSlot => "previous slot",
        ControllerAction.NextSlot => "next slot",
        ControllerAction.RadialHotbar => "radial hotbar",
        ControllerAction.OpenInventory => "inventory",
        ControllerAction.OpenOptions => "options",
        _ => "change view",
    };
}

/// <summary>One gamepad control per discrete action, persisted as enum names.</summary>
public sealed class ControllerBindings
{
    private readonly ControllerControl[] _controls = new ControllerControl[ControllerActions.All.Length];

    public static ControllerBindings Defaults()
    {
        var bindings = new ControllerBindings();
        bindings.Set(ControllerAction.Jump, ControllerControl.South);
        bindings.Set(ControllerAction.Sneak, ControllerControl.East);
        bindings.Set(ControllerAction.Sprint, ControllerControl.LeftStick);
        bindings.Set(ControllerAction.BreakOrAttack, ControllerControl.RightTrigger);
        bindings.Set(ControllerAction.UseOrPlace, ControllerControl.LeftTrigger);
        bindings.Set(ControllerAction.RaiseShield, ControllerControl.LeftShoulder);
        bindings.Set(ControllerAction.SwapHands, ControllerControl.West);
        bindings.Set(ControllerAction.PreviousSlot, ControllerControl.DPadLeft);
        bindings.Set(ControllerAction.NextSlot, ControllerControl.DPadRight);
        bindings.Set(ControllerAction.RadialHotbar, ControllerControl.DPadUp);
        bindings.Set(ControllerAction.OpenInventory, ControllerControl.North);
        bindings.Set(ControllerAction.OpenOptions, ControllerControl.Start);
        bindings.Set(ControllerAction.ToggleView, ControllerControl.RightStick);
        return bindings;
    }

    public ControllerControl Control(ControllerAction action) => _controls[(int)action];

    public string Describe(ControllerAction action) => Name(Control(action));

    /// <summary>Binds and steals, matching keyboard rebinding.</summary>
    public void Bind(ControllerAction action, ControllerControl control)
    {
        if (control != ControllerControl.None)
        {
            for (var i = 0; i < _controls.Length; i++)
                if (_controls[i] == control) _controls[i] = ControllerControl.None;
        }

        _controls[(int)action] = control;
    }

    /// <summary>Startup-only assignment that does not disturb another row.</summary>
    public void Set(ControllerAction action, ControllerControl control) => _controls[(int)action] = control;

    public ControllerAction? ActionFor(ControllerControl control)
    {
        if (control == ControllerControl.None) return null;
        for (var i = 0; i < _controls.Length; i++)
            if (_controls[i] == control) return (ControllerAction)i;
        return null;
    }

    public void FillGapsFrom(
        ControllerBindings source,
        IReadOnlySet<ControllerAction>? actionsNamedByFile = null)
    {
        foreach (var action in ControllerActions.All)
        {
            // None is a real, persisted choice. Only fill a missing action from an older file;
            // never resurrect one that the player explicitly cleared.
            if (actionsNamedByFile?.Contains(action) == true) continue;
            if (Control(action) != ControllerControl.None) continue;
            var control = source.Control(action);
            if (ActionFor(control) is null) Set(action, control);
        }
    }

    public ControllerBindings Copy()
    {
        var copy = new ControllerBindings();
        foreach (var action in ControllerActions.All) copy.Set(action, Control(action));
        return copy;
    }

    public List<string> Faults(bool requireEveryAction = true)
    {
        var faults = new List<string>();
        var seen = new Dictionary<ControllerControl, ControllerAction>();

        foreach (var action in ControllerActions.All)
        {
            var control = Control(action);
            if (control == ControllerControl.None)
            {
                if (requireEveryAction) faults.Add($"'{ControllerActions.Label(action)}' has no gamepad control on it");
                continue;
            }

            if (seen.TryGetValue(control, out var other))
                faults.Add($"'{Name(control)}' runs both '{ControllerActions.Label(other)}' and '{ControllerActions.Label(action)}'");
            else
                seen[control] = action;
        }

        return faults;
    }

    public static string Name(ControllerControl control) => control switch
    {
        ControllerControl.None => "unbound",
        ControllerControl.South => "south button",
        ControllerControl.East => "east button",
        ControllerControl.West => "west button",
        ControllerControl.North => "north button",
        ControllerControl.Back => "view / back",
        ControllerControl.Start => "menu / start",
        ControllerControl.LeftStick => "left stick press",
        ControllerControl.RightStick => "right stick press",
        ControllerControl.LeftShoulder => "left shoulder",
        ControllerControl.RightShoulder => "right shoulder",
        ControllerControl.DPadUp => "d-pad up",
        ControllerControl.DPadDown => "d-pad down",
        ControllerControl.DPadLeft => "d-pad left",
        ControllerControl.DPadRight => "d-pad right",
        ControllerControl.LeftTrigger => "left trigger",
        _ => "right trigger",
    };
}

/// <summary>A provider-neutral reading of one controller for one frame.</summary>
public readonly record struct ControllerSnapshot(
    bool Connected,
    Vector2 Move,
    Vector2 Look,
    float LeftTrigger,
    float RightTrigger,
    ulong Buttons)
{
    public static ControllerSnapshot Empty => new(false, Vector2.Zero, Vector2.Zero, 0f, 0f, 0);

    public bool Held(ControllerControl control, float triggerThreshold = 0.55f) => control switch
    {
        ControllerControl.None => false,
        ControllerControl.LeftTrigger => LeftTrigger >= triggerThreshold,
        ControllerControl.RightTrigger => RightTrigger >= triggerThreshold,
        _ => (Buttons & Bit(control)) != 0,
    };

    public static ulong Bit(ControllerControl control) => control is > ControllerControl.None and < ControllerControl.LeftTrigger
        ? 1UL << ((int)control - 1)
        : 0;
}

/// <summary>Pure controller shaping and navigation rules, shared by the live client and audit.</summary>
public static class ControllerTuning
{
    public const float TriggerThreshold = 0.55f;
    public const float UiThreshold = 0.62f;
    public const float RadialThreshold = 0.42f;

    /// <summary>Radial deadzone with the surviving range rescaled to zero through one.</summary>
    public static Vector2 Stick(Vector2 raw, int deadzonePercent, float exponent = 1.35f)
    {
        var deadzone = Math.Clamp(deadzonePercent, 0, 90) / 100f;
        var length = raw.Length();
        if (length <= deadzone || length <= float.Epsilon) return Vector2.Zero;

        var direction = raw / length;
        var scaled = Math.Clamp((length - deadzone) / (1f - deadzone), 0f, 1f);
        return direction * MathF.Pow(scaled, MathF.Max(0.1f, exponent));
    }

    /// <summary>Which of nine clockwise wedges a stick points at; zero begins at twelve o'clock.</summary>
    public static int RadialSlot(Vector2 stick, int slots = 9)
    {
        if (slots <= 0 || stick.LengthSquared() < RadialThreshold * RadialThreshold) return -1;
        var turn = MathF.Atan2(stick.X, -stick.Y);
        if (turn < 0f) turn += MathF.Tau;
        var sector = MathF.Tau / slots;
        return (int)MathF.Floor((turn + sector * 0.5f) / sector) % slots;
    }

    /// <summary>Deterministic checks for drift, diagonals and the nine-way radial picker.</summary>
    public static List<string> Faults()
    {
        var faults = new List<string>();
        if (Stick(new Vector2(0.17f, 0f), 18) != Vector2.Zero)
            faults.Add("the shipped deadzone lets 17% stick drift move the player");

        var edge = Stick(new Vector2(1f, 0f), 18);
        if (MathF.Abs(edge.X - 1f) > 0.0001f || MathF.Abs(edge.Y) > 0.0001f)
            faults.Add($"a fully-right stick became {edge}");

        var diagonal = Stick(Vector2.Normalize(Vector2.One), 18);
        if (MathF.Abs(diagonal.Length() - 1f) > 0.0001f)
            faults.Add($"a full diagonal has length {diagonal.Length():F3}, not 1");

        if (RadialSlot(new Vector2(0f, -1f)) != 0) faults.Add("up does not choose radial slot 1");
        if (RadialSlot(new Vector2(1f, 0f)) != 2) faults.Add("right does not choose the nearest clockwise radial slot");
        if (RadialSlot(new Vector2(0.1f, 0f)) != -1) faults.Add("radial hotbar chooses from stick drift");

        var reached = new HashSet<int>();
        for (var i = 0; i < 360; i++)
        {
            var angle = float.DegreesToRadians(i);
            reached.Add(RadialSlot(new Vector2(MathF.Sin(angle), -MathF.Cos(angle))));
        }
        if (reached.Count != 9 || reached.Contains(-1))
            faults.Add($"a full turn reaches {reached.Count} radial slots, not all 9");

        return faults;
    }
}

/// <summary>Initial delay and cadence for held-stick menu navigation.</summary>
public sealed class ControllerRepeat
{
    public const float FirstDelay = 0.34f;
    public const float NextDelay = 0.095f;

    private int _direction;
    private float _until;

    public int Step(float axis, float dt)
    {
        var direction = axis >= ControllerTuning.UiThreshold ? 1
            : axis <= -ControllerTuning.UiThreshold ? -1
            : 0;

        if (direction == 0)
        {
            _direction = 0;
            _until = 0f;
            return 0;
        }

        if (direction != _direction)
        {
            _direction = direction;
            _until = FirstDelay;
            return direction;
        }

        _until -= Math.Max(0f, dt);
        if (_until > 0f) return 0;
        _until += NextDelay;
        return direction;
    }

    public void Reset()
    {
        _direction = 0;
        _until = 0f;
    }
}
