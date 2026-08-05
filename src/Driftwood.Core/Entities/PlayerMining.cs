using Driftwood.Core.Blocks;
using Driftwood.Core.Items;

namespace Driftwood.Core.Entities;

/// <summary>
/// How far through breaking a block the player is.
/// </summary>
/// <remarks>
/// <para>A block used to go on the first blow whatever it was made of, which made every material
/// feel the same and left nothing for a tool to ever be better at. Progress lives here rather than
/// in the input handler so the audit can hold the button down on a headless block and time it.</para>
/// <para>Progress belongs to the <em>cell</em>, not to the player: look away and come back and you
/// start again. That is the genre's rule and it is the right one — carrying progress around would
/// let a player chip at ten blocks in rotation and drop them all at once, and storing it per cell
/// would mean the world remembering half-mined blocks nobody is standing near.</para>
/// </remarks>
public sealed class PlayerMining
{
    private (int X, int Y, int Z)? _at;
    private float _progress;

    /// <summary>The cell being worked on, if any.</summary>
    public (int X, int Y, int Z)? Target => _at;

    /// <summary>0 to 1 through the current block.</summary>
    public float Progress => _progress;

    /// <summary>True while there is a block part-broken.</summary>
    public bool Active => _at is not null && _progress > 0f;

    /// <summary>Cracking stage to draw, or -1 for none.</summary>
    public int Stage => Active ? MiningRules.StageFor(_progress) : -1;

    /// <summary>How long the current block would take from scratch. Zero when nothing is targeted.</summary>
    public float TargetSeconds { get; private set; }

    /// <summary>Forgets any progress. Used when the button comes up or the cursor is released.</summary>
    public void Cancel()
    {
        _at = null;
        _progress = 0f;
        TargetSeconds = 0f;
    }

    /// <summary>
    /// Advances one frame and reports whether the block should break now.
    /// </summary>
    /// <param name="type">The targeted block's type, or null when nothing is in reach.</param>
    /// <param name="at">Which cell that is.</param>
    /// <param name="mining">Whether the player is still swinging at it.</param>
    /// <param name="held">
    /// What is in hand, which decides both the speed and whether anything is left behind.
    /// </param>
    /// <remarks>
    /// The tool is read every frame rather than latched when the block was first struck, so
    /// switching to a pickaxe part way through a block speeds up the rest of it. Latching would
    /// mean a player who reached for the right tool got no benefit until they let go and started
    /// again, which reads as the swap not having worked.
    /// </remarks>
    public bool Update(float dt, BlockType? type, (int X, int Y, int Z)? at, bool mining, ItemType? held = null)
    {
        if (!mining || type is null || at is null)
        {
            Cancel();
            return false;
        }

        // Moving the crosshair to a different block starts that block from nothing. Without this,
        // progress earned on soft ground would carry straight into the stone beside it.
        if (_at != at)
        {
            _at = at;
            _progress = 0f;
        }

        TargetSeconds = MiningRules.SecondsToBreak(type, held);

        // Unbreakable blocks are not "very slow". They never move, so the bar never appears and no
        // amount of holding changes that.
        if (float.IsPositiveInfinity(TargetSeconds))
        {
            _progress = 0f;
            return false;
        }

        _progress += dt / TargetSeconds;
        if (_progress < 1f) return false;

        Cancel();
        return true;
    }
}
