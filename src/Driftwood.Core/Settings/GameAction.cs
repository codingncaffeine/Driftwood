namespace Driftwood.Core.Settings;

/// <summary>
/// Everything a player can ask the game to do, named by intent rather than by key.
/// </summary>
/// <remarks>
/// <para>The indirection is the whole point. Until now the keys were written into the input handler,
/// which meant "move forward" and "the W key" were the same sentence and neither could be changed
/// without the other. Naming the intent is what lets a key be rebound, and it is also what a
/// controller binds to when P8 arrives — a stick does not press W.</para>
/// <para>The order is the order the controls screen lists them in, so the grouping here is doing
/// double duty as a layout.</para>
/// </remarks>
public enum GameAction
{
    MoveForward,
    MoveBack,
    MoveLeft,
    MoveRight,
    Jump,
    Sneak,
    Sprint,

    /// <summary>
    /// Held: puts the shield in the other hand up, if there is one there.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Its own key rather than the right mouse button, and that is forced rather than chosen.</b>
    /// Right-click already means "use what is in front of you" — open a chest, light a campfire, put
    /// a block down — and every one of those is something a player does constantly while a shield is
    /// in their other hand. Overloading it would make raising a shield and opening a door the same
    /// gesture, decided by whatever happened to be under the crosshair.
    /// </remarks>
    RaiseShield,

    /// <summary>Opens what this character is carrying and can make. Closes it again.</summary>
    OpenInventory,

    /// <summary>
    /// Opens what this installation is set to, and lets go of the mouse while it is up.
    /// </summary>
    /// <remarks>
    /// Two screens rather than one, because "what am I carrying" and "which key jumps" are not the
    /// same kind of question and a single row of ten tabs answers neither well. Releasing the mouse
    /// is folded in here rather than being its own key: opening a menu is how every game in this
    /// space gives the pointer back, and a separate binding for it would be a key nobody presses.
    /// </remarks>
    OpenOptions,

    ToggleView,
    ToggleFly,
    ToggleWireframe,
    ToggleCulling,
    HoldClock,
    WindClock,

    Slot1,
    Slot2,
    Slot3,
    Slot4,
    Slot5,
    Slot6,
    Slot7,
    Slot8,
    Slot9,
}

/// <summary>What each action is called on screen, and which group it belongs to.</summary>
public static class GameActions
{
    public static readonly GameAction[] All = Enum.GetValues<GameAction>();

    /// <summary>The headings the controls screen breaks the list up under.</summary>
    public static string GroupOf(GameAction action) => action switch
    {
        <= GameAction.Sprint => "moving",
        GameAction.RaiseShield => "fighting",
        <= GameAction.OpenOptions => "screens",
        <= GameAction.WindClock => "looking at things",
        _ => "the bar",
    };

    /// <summary>What a player is shown. Lower case, because the font has both and this reads.</summary>
    public static string Label(GameAction action) => action switch
    {
        GameAction.MoveForward => "forward",
        GameAction.MoveBack => "back",
        GameAction.MoveLeft => "left",
        GameAction.MoveRight => "right",
        GameAction.Jump => "jump",
        GameAction.Sneak => "sneak",
        GameAction.Sprint => "sprint",
        GameAction.RaiseShield => "raise shield",
        GameAction.OpenInventory => "inventory",
        GameAction.OpenOptions => "options",
        GameAction.ToggleView => "change view",
        GameAction.ToggleFly => "walk or fly",
        GameAction.ToggleWireframe => "wireframe",
        GameAction.ToggleCulling => "frustum culling",
        GameAction.HoldClock => "hold the clock",
        GameAction.WindClock => "wind the day on",
        _ => $"slot {(int)action - (int)GameAction.Slot1 + 1}",
    };
}
