using Driftwood.Core.Items;

namespace Driftwood.Core.Blocks;

/// <summary>
/// What every block becomes turned a quarter about the vertical (2026-10-25) — so a ship that sails
/// off east and docks facing south keeps its stairs, doors, signs and furnaces facing the way they
/// did relative to the deck.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Read off the placement tables, not a second list of names.</b> Every block with a
/// facing is put down by an item whose <see cref="Placeable.Variants"/> are stored in
/// <see cref="Placeable.Facings"/> order (east, west, south, north) in a layout its kind fixes, so the
/// turn is the same arithmetic on that layout for every kind there is. A new facing block gets its
/// turn the moment it gets an item.</para>
/// <para>A quarter turn takes +x to +z: east to south, south to west, west to north, north to east —
/// the way a heading's angle grows in the camera's own convention.</para>
/// </remarks>
public sealed class BlockTurns
{
    /// <summary>Where each facing goes, in <see cref="Placeable.Facings"/> index order.</summary>
    private static readonly int[] Quarter = [2, 3, 1, 0];

    private readonly ushort[] _turn;

    public BlockTurns(BlockRegistry registry, ItemRegistry items)
    {
        _turn = new ushort[registry.Count];
        for (var id = 0; id < _turn.Length; id++) _turn[id] = (ushort)id;

        foreach (var item in items.All)
        {
            if (item.Places is not { } places) continue;
            var v = places.Variants;

            void Map(int from, int to)
            {
                if (from < v.Length && to < v.Length) _turn[v[from].Value] = v[to].Value;
            }

            switch (places.Kind)
            {
                case PlacementKind.Stairs:
                case PlacementKind.Trapdoor:
                    for (var f = 0; f < 4; f++)
                    for (var half = 0; half < 2; half++)
                        Map(f * 2 + half, Quarter[f] * 2 + half);
                    break;

                case PlacementKind.Facing:
                case PlacementKind.Pointing:
                case PlacementKind.Wall:
                    if (v.Length >= 4)
                        for (var f = 0; f < 4; f++) Map(f, Quarter[f]);
                    break;

                case PlacementKind.Attached:
                    for (var f = 0; f < 4; f++) Map(1 + f, 1 + Quarter[f]);
                    break;

                case PlacementKind.Axis:
                    Map(0, 1);
                    Map(1, 0);
                    break;

                case PlacementKind.Sign:
                    for (var f = 0; f < 4; f++)
                    {
                        Map(f, Quarter[f]);
                        Map(4 + f, 4 + Quarter[f]);
                    }

                    break;

                case PlacementKind.Door:
                    for (var f = 0; f < 4; f++)
                    for (var rest = 0; rest < 4; rest++)
                        Map(f * 4 + rest, Quarter[f] * 4 + rest);
                    break;
            }
        }

        // A form no item places (a door's top, an open gate, a lit furnace) turns by its name's
        // facing word where a sibling with the turned word exists.
        string[] words = ["east", "south", "west", "north"];
        for (var id = 1; id < registry.Count; id++)
        {
            if (_turn[id] != id) continue;
            var name = registry[(ushort)id].Name;
            for (var w = 0; w < 4; w++)
            {
                var at = name.IndexOf(words[w], StringComparison.Ordinal);
                if (at < 0) continue;
                var turned = name[..at] + words[(w + 1) % 4] + name[(at + words[w].Length)..];
                if (registry.TryByName(turned, out var other)) _turn[id] = other.Id.Value;
                break;
            }
        }
    }

    /// <summary>The block turned by this many quarters (any integer; four is a full turn).</summary>
    public BlockId Turn(BlockId id, int quarters)
    {
        var q = ((quarters % 4) + 4) % 4;
        var value = id.Value;
        for (var i = 0; i < q; i++) value = _turn[value];
        return new BlockId(value);
    }
}
