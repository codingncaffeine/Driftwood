using Driftwood.Core.Blocks;

namespace Driftwood.Core.Items;

/// <summary>
/// What the player is carrying: a row of slots, one of them in hand.
/// </summary>
/// <remarks>
/// <para>The hotbar is the whole inventory for now. A backpack is a second row and a screen to
/// show it on, and neither exists yet; making the row the inventory means everything that puts
/// something in — a pickup, a craft, a starting kit — goes through one door, and the day the
/// backpack arrives it widens rather than being bolted on beside.</para>
/// <para>Adding fills partial stacks before it opens empty ones, which is what a player expects and
/// also what stops a pocketful of single logs. Anything that will not fit comes back rather than
/// vanishing: an inventory that silently eats what it cannot hold is the one bug in this area that
/// nobody forgives.</para>
/// </remarks>
public sealed class Inventory
{
    /// <summary>Slots in the bar. Nine, which is the row every player in the genre knows.</summary>
    public const int Slots = 9;

    private readonly ItemStack[] _slots = new ItemStack[Slots];

    /// <summary>Which slot is in hand.</summary>
    public int Selected { get; private set; }

    public ItemStack this[int slot] => _slots[slot];

    /// <summary>What is in hand right now.</summary>
    public ItemStack Held => _slots[Selected];

    public void Select(int slot) => Selected = ((slot % Slots) + Slots) % Slots;

    /// <summary>Moves the selection by a step, wrapping either way.</summary>
    public void Scroll(int by) => Select(Selected + by);

    /// <summary>
    /// Puts a stack in, and returns whatever would not fit.
    /// </summary>
    public ItemStack Add(ItemStack stack)
    {
        if (stack.IsEmpty) return ItemStack.Empty;

        // Partial stacks of the same thing first, then empty slots. The other order leaves a row
        // of half-full slots and an empty one at the end of every gathering trip.
        for (var pass = 0; pass < 2; pass++)
        for (var i = 0; i < Slots && !stack.IsEmpty; i++)
        {
            var wantEmpty = pass == 1;
            if (_slots[i].IsEmpty != wantEmpty) continue;

            _slots[i] = _slots[i].Merge(stack, out stack);
        }

        return stack;
    }

    /// <summary>Takes one off the held stack, for a block that has just been put down.</summary>
    public void SpendHeld() => _slots[Selected] = _slots[Selected].MinusOne();

    /// <summary>Empties everything. What a fresh world or a respawn does.</summary>
    public void Clear() => Array.Clear(_slots);

    /// <summary>How many of one thing are being carried, across every slot.</summary>
    public int CountOf(BlockId block)
    {
        var total = 0;
        foreach (var slot in _slots)
            if (!slot.IsEmpty && slot.Block.Value == block.Value) total += slot.Count;
        return total;
    }

    /// <summary>Everything being carried, for the display and the checks.</summary>
    public ReadOnlySpan<ItemStack> All => _slots;
}
