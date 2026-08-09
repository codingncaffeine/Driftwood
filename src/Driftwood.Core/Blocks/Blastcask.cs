namespace Driftwood.Core.Blocks;

/// <summary>
/// The blastcask's rulebook: the pair's names, what a fuse costs in seconds, and the fuse list
/// itself.
/// </summary>
/// <remarks>
/// <para>⛳ <b>The crater is not here.</b> <see cref="Explosion"/> already owns carving and the
/// client already applies it for the crawler — the cask is a third way of arriving at the same
/// blast. This file owns what is new: the fuse. Three doors light one — a right click (the
/// button's own one-way toggle), a powered wire (a one-way sink in <see cref="SignalTable"/>),
/// and another blast (the chain) — and all three funnel through <see cref="Fuses.Light"/>.</para>
/// <para>⛔ <b>A fuse is transient and does not survive the world closing.</b> A lit cask read
/// back from a save stands lit and inert until it is mined — said here rather than discovered,
/// because the window is three seconds wide and the recovery is a swing.</para>
/// </remarks>
public static class Blastcask
{
    /// <summary>Seconds from lit to blast — long enough to run, short enough to regret.</summary>
    public const float FuseSeconds = 3f;

    /// <summary>A cask lit by a blast goes quickly, so a chain reads as one event spread out.</summary>
    public const float ChainSeconds = 0.35f;

    /// <summary>The pair, named once for the three ignition doors.</summary>
    public const string Cold = "blastcask";

    public const string Lit = "blastcask_lit";

    /// <summary>True of either form. The blast treats a cask as ignition, never as debris.</summary>
    public static bool IsCask(string name) => name is Cold or Lit;

    /// <summary>
    /// The burning fuses, keyed on the cell. The client lights one at every ignition door and
    /// drains what burned down each frame.
    /// </summary>
    public sealed class Fuses
    {
        private readonly List<((int X, int Y, int Z) Cell, float Left)> _burning = [];

        public int Count => _burning.Count;

        /// <summary>
        /// Lights a cell's fuse — or shortens one already burning, never lengthens it. A blast
        /// washing over a cask that was already lit must not grant it more time.
        /// </summary>
        public void Light((int X, int Y, int Z) cell, float seconds)
        {
            for (var i = 0; i < _burning.Count; i++)
            {
                if (_burning[i].Cell != cell) continue;

                _burning[i] = (cell, MathF.Min(_burning[i].Left, seconds));
                return;
            }

            _burning.Add((cell, seconds));
        }

        /// <summary>
        /// Burns every fuse down by one frame, appending the cells whose time ran out.
        /// </summary>
        /// <param name="stillLit">
        /// Asked before a burn-down counts — and before the clock even ticks. ⛳ <b>Mining the
        /// lit cask IS the defusal</b>: the fuse finds its block gone and dies with nothing to
        /// show for it.
        /// </param>
        public void Update(
            float dt, Func<(int X, int Y, int Z), bool> stillLit,
            List<(int X, int Y, int Z)> burnedDown)
        {
            for (var i = _burning.Count - 1; i >= 0; i--)
            {
                var (cell, left) = _burning[i];

                if (!stillLit(cell))
                {
                    _burning.RemoveAt(i);
                    continue;
                }

                left -= dt;
                if (left > 0f)
                {
                    _burning[i] = (cell, left);
                    continue;
                }

                _burning.RemoveAt(i);
                burnedDown.Add(cell);
            }
        }
    }
}
