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
/// <para>It holds the item registry because the per-slot ceiling is a property of the item — sixty
/// four planks and exactly one pickaxe — and the slot is the only place that knows both.</para>
/// </remarks>
public sealed class Inventory
{
    /// <summary>Slots in the bar. Nine, which is the row every player in the genre knows.</summary>
    public const int Slots = 9;

    private readonly ItemStack[] _slots = new ItemStack[Slots];
    private readonly ItemRegistry _items;

    public Inventory(ItemRegistry items) => _items = items;

    /// <summary>Which slot is in hand.</summary>
    public int Selected { get; private set; }

    /// <summary>
    /// Bumped whenever what is being carried changes. Not when the selection moves.
    /// </summary>
    /// <remarks>
    /// So anything that wants to react to a pickup can ask one integer instead of doing its own
    /// work every frame. Working out what has become craftable means paying for forty recipes
    /// against nine slots, and doing that sixty times a second to answer "nothing changed" is the
    /// kind of cost that never shows up as one thing.
    /// </remarks>
    public int Version { get; private set; }

    public ItemStack this[int slot] => _slots[slot];

    /// <summary>What is in hand right now.</summary>
    public ItemStack Held => _slots[Selected];

    /// <summary>What is in hand, described. Null when the hand is empty.</summary>
    public ItemType? HeldType => Held.IsEmpty ? null : _items[Held.Item];

    public void Select(int slot) => Selected = ((slot % Slots) + Slots) % Slots;

    /// <summary>Moves the selection by a step, wrapping either way.</summary>
    public void Scroll(int by) => Select(Selected + by);

    /// <summary>
    /// Puts a stack in, and returns whatever would not fit.
    /// </summary>
    public ItemStack Add(ItemStack stack)
    {
        if (stack.IsEmpty) return ItemStack.Empty;

        var cap = _items[stack.Item].MaxStack;

        // Partial stacks of the same thing first, then empty slots. The other order leaves a row
        // of half-full slots and an empty one at the end of every gathering trip.
        for (var pass = 0; pass < 2; pass++)
        for (var i = 0; i < Slots && !stack.IsEmpty; i++)
        {
            var wantEmpty = pass == 1;
            if (_slots[i].IsEmpty != wantEmpty) continue;

            _slots[i] = _slots[i].Merge(stack, cap, out stack);
        }

        Version++;
        return stack;
    }

    /// <summary>Takes one off the held stack, for a block that has just been put down.</summary>
    public void SpendHeld()
    {
        _slots[Selected] = _slots[Selected].MinusOne();
        Version++;
    }

    /// <summary>
    /// Takes some off the held stack specifically, rather than off whichever slot holds that thing.
    /// </summary>
    /// <remarks>
    /// Not the same as <see cref="Take"/>, and the difference shows the moment a player is carrying
    /// two stacks of planks: feeding a furnace from the one in hand must empty the one in hand.
    /// </remarks>
    public void SpendHeld(int howMany)
    {
        _slots[Selected] = _slots[Selected].Minus(howMany);
        Version++;
    }

    /// <summary>
    /// Puts one use on whatever is in hand, and empties the slot when that use was its last.
    /// </summary>
    /// <returns>True when the tool broke on this use, for the sound that says so.</returns>
    public bool WearHeld()
    {
        var before = _slots[Selected];
        if (before.IsEmpty) return false;

        _slots[Selected] = before.Worn(_items[before.Item].Durability);
        Version++;
        return _slots[Selected].IsEmpty;
    }

    /// <summary>Empties everything. What a fresh world or a respawn does.</summary>
    public void Clear()
    {
        Array.Clear(_slots);
        Version++;
    }

    /// <summary>How many of one thing are being carried, across every slot.</summary>
    public int CountOf(ItemId item)
    {
        var total = 0;
        foreach (var slot in _slots)
            if (!slot.IsEmpty && slot.Item.Value == item.Value) total += slot.Count;
        return total;
    }

    /// <summary>
    /// Removes up to <paramref name="howMany"/> of one thing, and reports how many it actually got.
    /// </summary>
    /// <remarks>
    /// Partial by design. A craft asks whether it can be paid for before it spends anything, so this
    /// returning less than it was asked for is a bug upstream rather than a case to handle here —
    /// but returning the number means the check can be written, and it is.
    /// </remarks>
    public int Take(ItemId item, int howMany)
    {
        var taken = 0;

        // Smallest stacks first, so paying for a craft tidies the bar instead of fragmenting it.
        while (taken < howMany)
        {
            var best = -1;
            for (var i = 0; i < Slots; i++)
            {
                if (_slots[i].IsEmpty || _slots[i].Item.Value != item.Value) continue;
                if (best < 0 || _slots[i].Count < _slots[best].Count) best = i;
            }

            if (best < 0) break;

            var take = Math.Min(howMany - taken, _slots[best].Count);
            _slots[best] = _slots[best].Minus(take);
            taken += take;
        }

        if (taken > 0) Version++;
        return taken;
    }

    /// <summary>Everything being carried, for the display and the checks.</summary>
    public ReadOnlySpan<ItemStack> All => _slots;

    /// <summary>Slots holding something. What a crafting screen counts.</summary>
    public int Used
    {
        get
        {
            var used = 0;
            foreach (var slot in _slots) if (!slot.IsEmpty) used++;
            return used;
        }
    }
}
