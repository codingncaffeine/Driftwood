namespace Driftwood.Core.Items;

/// <summary>
/// A thing's identity inside an <see cref="ItemRegistry"/> — the id space a player's pockets are
/// counted in, which is not the id space the world is built out of.
/// </summary>
/// <remarks>
/// <para>Separate from <see cref="Blocks.BlockId"/> because the two stopped agreeing the moment a
/// stick existed. They disagree in both directions: a stick is an item with no block, an ore is a
/// block whose item is a lump of something else, and twenty stair orientations are twenty blocks
/// that are all one item. Keying pockets on block ids made the last of those look like twenty
/// different things to carry.</para>
/// <para>Id 0 is always <see cref="None"/>, the way block 0 is always air, so a zeroed slot is an
/// empty slot without anybody having to write that down.</para>
/// </remarks>
public readonly struct ItemId : IEquatable<ItemId>
{
    public readonly ushort Value;

    public ItemId(ushort value) => Value = value;

    public static ItemId None => new(0);

    public bool IsNone => Value == 0;

    public bool Equals(ItemId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is ItemId other && Equals(other);
    public override int GetHashCode() => Value;
    public override string ToString() => $"item:{Value}";

    public static bool operator ==(ItemId a, ItemId b) => a.Value == b.Value;
    public static bool operator !=(ItemId a, ItemId b) => a.Value != b.Value;

    public static implicit operator ushort(ItemId id) => id.Value;
}
