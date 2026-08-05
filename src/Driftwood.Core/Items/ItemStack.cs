namespace Driftwood.Core.Items;

/// <summary>
/// Some number of one thing, and how worn it is.
/// </summary>
/// <remarks>
/// <para>Keyed on <see cref="ItemId"/> rather than on a block, which is the difference between a
/// pocket and a cell. A stick has no block, an ore leaves something that is not itself, and every
/// orientation of a stair is the same thing to carry.</para>
/// <para>Empty is a real value rather than null. A slot that holds nothing is still a slot, and a
/// nullable stack would put a question mark on every line that touches an inventory.</para>
/// <para>The per-slot ceiling is passed in rather than stored. It is a property of the item, and an
/// item registry is the wrong thing to hand to a struct that has to stay copyable and free — so the
/// one caller that knows the cap supplies it, and this stays arithmetic.</para>
/// </remarks>
public readonly record struct ItemStack(ItemId Item, int Count, int Damage = 0)
{
    /// <summary>The ceiling for anything that does not say otherwise. The genre's own number.</summary>
    public const int MaxCount = 64;

    public static readonly ItemStack Empty = new(ItemId.None, 0);

    public bool IsEmpty => Count <= 0 || Item.IsNone;

    /// <summary>Room left in this stack, given what the item allows in one slot.</summary>
    public int Space(int cap) => IsEmpty ? cap : Math.Max(0, cap - Count);

    /// <summary>
    /// True when two stacks are the same thing in the same condition, and could be merged.
    /// </summary>
    /// <remarks>
    /// Wear is part of the identity. Anything that wears out has a ceiling of one so this never
    /// decides a merge in practice — but a half-broken pickaxe folding into a fresh one would
    /// silently repair or silently ruin it, and which of those it did would depend on argument order.
    /// </remarks>
    public bool Matches(ItemStack other) =>
        !IsEmpty && !other.IsEmpty && Item.Value == other.Item.Value && Damage == other.Damage;

    /// <summary>
    /// Pours as much of <paramref name="other"/> into this as fits, and reports what is left over.
    /// </summary>
    public ItemStack Merge(ItemStack other, int cap, out ItemStack remainder)
    {
        if (IsEmpty)
        {
            var taken = Math.Min(other.Count, cap);
            remainder = other with { Count = other.Count - taken };
            if (remainder.Count <= 0) remainder = Empty;
            return taken <= 0 ? Empty : other with { Count = taken };
        }

        if (!Matches(other))
        {
            remainder = other;
            return this;
        }

        var room = Math.Min(Space(cap), other.Count);
        remainder = other.Count - room <= 0 ? Empty : other with { Count = other.Count - room };
        return this with { Count = Count + room };
    }

    /// <summary>This stack with one taken off, or empty when that was the last.</summary>
    public ItemStack MinusOne() => Count <= 1 ? Empty : this with { Count = Count - 1 };

    /// <summary>This stack with some taken off, or empty when that was all of it.</summary>
    public ItemStack Minus(int howMany) => Count <= howMany ? Empty : this with { Count = Count - howMany };

    /// <summary>
    /// This stack after one use of a tool, or empty when the use was its last.
    /// </summary>
    /// <remarks>
    /// A durability of zero means "never wears out" rather than "breaks immediately", which is what
    /// lets everything that is not a tool run through the same call without a test at each site.
    /// </remarks>
    public ItemStack Worn(int durability)
    {
        if (IsEmpty || durability <= 0) return this;
        return Damage + 1 >= durability ? Empty : this with { Damage = Damage + 1 };
    }
}
