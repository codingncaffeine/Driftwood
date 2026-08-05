using Driftwood.Core.Blocks;

namespace Driftwood.Core.Items;

/// <summary>
/// Some number of one thing.
/// </summary>
/// <remarks>
/// <para>The thing is a block, for now. Every item in the game is currently something that can be
/// put back down, so a separate item registry would be a table with the same keys as the block one
/// and nothing else in it. When a stick exists — an item that is not a block — this gains an id
/// space of its own and the block becomes one entry in it; nothing outside this file assumes the
/// two are the same today, which is the whole reason it is a struct with a name rather than a
/// bare <see cref="BlockId"/>.</para>
/// <para>Empty is a real value rather than null. A slot that holds nothing is still a slot, and a
/// nullable stack would put a question mark on every line that touches an inventory.</para>
/// </remarks>
public readonly record struct ItemStack(BlockId Block, int Count)
{
    /// <summary>How many of one thing fit in a slot. The genre's own number.</summary>
    public const int MaxCount = 64;

    public static readonly ItemStack Empty = new(BlockId.Air, 0);

    public bool IsEmpty => Count <= 0 || Block.IsAir;

    /// <summary>Room left in this stack.</summary>
    public int Space => IsEmpty ? MaxCount : Math.Max(0, MaxCount - Count);

    /// <summary>True when two stacks are the same thing and could be merged.</summary>
    public bool Matches(ItemStack other) => !IsEmpty && !other.IsEmpty && Block.Value == other.Block.Value;

    /// <summary>
    /// Pours as much of <paramref name="other"/> into this as fits, and reports what is left over.
    /// </summary>
    public ItemStack Merge(ItemStack other, out ItemStack remainder)
    {
        if (IsEmpty)
        {
            var taken = Math.Min(other.Count, MaxCount);
            remainder = other with { Count = other.Count - taken };
            if (remainder.Count <= 0) remainder = Empty;
            return new ItemStack(other.Block, taken);
        }

        if (!Matches(other))
        {
            remainder = other;
            return this;
        }

        var room = Math.Min(Space, other.Count);
        remainder = other.Count - room <= 0 ? Empty : other with { Count = other.Count - room };
        return this with { Count = Count + room };
    }

    /// <summary>This stack with one taken off, or empty when that was the last.</summary>
    public ItemStack MinusOne() => Count <= 1 ? Empty : this with { Count = Count - 1 };
}
