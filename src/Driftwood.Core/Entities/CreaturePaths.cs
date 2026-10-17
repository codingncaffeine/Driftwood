namespace Driftwood.Core.Entities;

/// <summary>One cell of a walked path: the cell the feet stand in.</summary>
public readonly record struct PathStep(int X, int Y, int Z);

/// <summary>
/// A* over the block grid for things that walk: floors, one-block steps up, and drops down.
/// </summary>
/// <remarks>
/// <para>⛔ <b>The reason this exists is hunters caught on the terrain.</b> A hunter used to turn its face to the player every frame and walk straight at them; a wall in
/// between was answered by a random turn, which the next frame's "face the player" undid — so it
/// stood pressing into the rock. The reference paths every chaser with A* over the same grid, and
/// so does this.</para>
/// <para>⛳ <b>A node is a place to stand, not a cell.</b> Feet in air, head room above, something
/// solid underneath — or, for a climber, a wall beside it to hold. Moves are the eight neighbours,
/// each either level, one up (with the head room to make the step), or any fall down to
/// <c>maxDrop</c>. Diagonals need both of their sides open, or a body cuts the corner of a wall it
/// cannot pass through.</para>
/// <para>⚠ <b>Budgeted, and partial on purpose.</b> A search that cannot reach the goal inside its
/// node budget returns the path to the closest place it did reach — the reference's own answer, and
/// the one that makes a zombie come to the cliff edge below you rather than stand still.</para>
/// </remarks>
public sealed class CreaturePaths
{
    private readonly Dictionary<long, Node> _nodes = new();
    private readonly PriorityQueue<long, float> _open = new();

    private sealed class Node
    {
        public PathStep Step;
        public long Parent;
        public float Cost;
        public bool Closed;
    }

    private static long Key(int x, int y, int z) =>
        ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

    private static readonly (int X, int Z, float Cost)[] Moves =
    [
        (1, 0, 1f), (-1, 0, 1f), (0, 1, 1f), (0, -1, 1f),
        (1, 1, 1.414f), (1, -1, 1.414f), (-1, 1, 1.414f), (-1, -1, 1.414f),
    ];

    /// <summary>Whether a body this many cells tall can stand with its feet in this cell.</summary>
    public static bool Stands(Func<int, int, int, bool> solid, int x, int y, int z, int tall, bool climbs)
    {
        for (var h = 0; h < tall; h++) if (solid(x, y + h, z)) return false;
        if (solid(x, y - 1, z)) return true;

        // A climber holds a wall as well as it stands on a floor.
        return climbs && (solid(x + 1, y, z) || solid(x - 1, y, z) || solid(x, y, z + 1) || solid(x, y, z - 1));
    }

    /// <summary>
    /// The path from <paramref name="from"/> toward <paramref name="goal"/>, or null for none at all.
    /// </summary>
    /// <param name="tall">Cells of head room the body needs.</param>
    /// <param name="maxDrop">The furthest it will fall in one step, in blocks.</param>
    /// <param name="climbs">True for a kind that walks up walls.</param>
    /// <param name="budget">Most nodes looked at before settling for the nearest.</param>
    /// <param name="near">How close to the goal counts as arrived, in blocks.</param>
    public List<PathStep>? Find(
        Func<int, int, int, bool> solid, PathStep from, PathStep goal, int tall, int maxDrop,
        bool climbs = false, int budget = 600, float near = 1.5f)
    {
        _nodes.Clear();
        _open.Clear();

        var start = new Node { Step = from, Parent = -1, Cost = 0f };
        var startKey = Key(from.X, from.Y, from.Z);
        _nodes[startKey] = start;
        _open.Enqueue(startKey, Heuristic(from, goal));

        var bestKey = startKey;
        var bestScore = Heuristic(from, goal);
        var looked = 0;

        while (_open.TryDequeue(out var key, out _))
        {
            var node = _nodes[key];
            if (node.Closed) continue;
            node.Closed = true;

            var h = Heuristic(node.Step, goal);
            if (h < bestScore)
            {
                bestScore = h;
                bestKey = key;
            }

            if (h <= near) return Build(key);
            if (++looked >= budget) break;

            foreach (var (mx, mz, moveCost) in Moves)
            {
                var nx = node.Step.X + mx;
                var nz = node.Step.Z + mz;

                // No cutting a corner: both sides of a diagonal have to be open at body height.
                if (mx != 0 && mz != 0
                    && (Blocked(solid, node.Step.X + mx, node.Step.Y, node.Step.Z, tall)
                        || Blocked(solid, node.Step.X, node.Step.Y, node.Step.Z + mz, tall)))
                    continue;

                if (!TryLand(solid, node.Step, nx, nz, tall, maxDrop, climbs, out var ny, out var extra)) continue;

                var next = new PathStep(nx, ny, nz);
                var nextKey = Key(nx, ny, nz);
                var cost = node.Cost + moveCost + extra;

                if (_nodes.TryGetValue(nextKey, out var seen))
                {
                    if (seen.Closed || seen.Cost <= cost) continue;
                    seen.Cost = cost;
                    seen.Parent = key;
                }
                else
                {
                    _nodes[nextKey] = new Node { Step = next, Parent = key, Cost = cost };
                }

                _open.Enqueue(nextKey, cost + Heuristic(next, goal));
            }

            // A climber also goes straight up a wall it is holding.
            if (climbs)
            {
                var up = new PathStep(node.Step.X, node.Step.Y + 1, node.Step.Z);
                if (Stands(solid, up.X, up.Y, up.Z, tall, true))
                {
                    var upKey = Key(up.X, up.Y, up.Z);
                    var cost = node.Cost + 1.2f;
                    if (!_nodes.TryGetValue(upKey, out var seen))
                    {
                        _nodes[upKey] = new Node { Step = up, Parent = key, Cost = cost };
                        _open.Enqueue(upKey, cost + Heuristic(up, goal));
                    }
                    else if (!seen.Closed && seen.Cost > cost)
                    {
                        seen.Cost = cost;
                        seen.Parent = key;
                        _open.Enqueue(upKey, cost + Heuristic(up, goal));
                    }
                }
            }
        }

        // ⚠ Partial: the nearest place reached, unless that is where it already stands.
        return bestKey == startKey ? null : Build(bestKey);
    }

    /// <summary>How many nodes the last search kept. For the audit's budget check.</summary>
    public int LastNodes => _nodes.Count;

    private static bool Blocked(Func<int, int, int, bool> solid, int x, int y, int z, int tall)
    {
        for (var h = 0; h < tall; h++) if (solid(x, y + h, z)) return true;
        return false;
    }

    /// <summary>Where a step into a neighbouring column lands: level, one up, or a fall.</summary>
    private static bool TryLand(
        Func<int, int, int, bool> solid, PathStep from, int nx, int nz, int tall, int maxDrop, bool climbs,
        out int ny, out float extra)
    {
        extra = 0f;
        ny = from.Y;

        if (!Blocked(solid, nx, from.Y, nz, tall))
        {
            if (Stands(solid, nx, from.Y, nz, tall, climbs)) return true;

            // Open but nothing underfoot: a drop. Fall down the column to the first floor.
            for (var d = 1; d <= maxDrop + 1; d++)
            {
                var y = from.Y - d;
                if (solid(nx, y, nz)) break;
                if (!solid(nx, y - 1, nz)) continue;
                if (d > maxDrop) return false;

                ny = y;
                // ⚠ A fall costs a little per block, so a gentler way down wins when there is one.
                extra = d * 0.4f;
                return true;
            }

            return false;
        }

        // Blocked at the feet: maybe a step. Head room above the current cell for the lift, and the
        // neighbour one up has to be a place to stand.
        if (solid(from.X, from.Y + tall, from.Z)) return false;
        if (!Stands(solid, nx, from.Y + 1, nz, tall, climbs)) return false;

        ny = from.Y + 1;
        extra = 0.5f;
        return true;
    }

    private static float Heuristic(PathStep a, PathStep b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy * 1.5f + dz * dz);
    }

    private List<PathStep> Build(long key)
    {
        var path = new List<PathStep>();
        while (key != -1)
        {
            var node = _nodes[key];
            path.Add(node.Step);
            key = node.Parent;
        }

        path.Reverse();
        return path;
    }
}
