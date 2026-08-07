namespace Driftwood.Core.Items;

/// <summary>
/// What the player is carrying: a row of slots, one of them in hand.
/// </summary>
/// <remarks>
/// <para><b>The bar is the first row of the inventory, not a separate thing.</b> One array, with
/// the nine slots a player can reach without opening anything at the front of it and the rest
/// behind. That is what makes dragging between the two free — it is moving between indices, not
/// copying between containers — and it is why <see cref="Selected"/>, <see cref="Held"/> and
/// everything the world already does with the bar kept working unchanged when the backpack
/// arrived.</para>
/// <para>Adding fills partial stacks before it opens empty ones, which is what a player expects and
/// also what stops a pocketful of single logs. Empty slots in the bar are taken before empty slots
/// behind it, so what has just been picked up is in reach. Anything that will not fit comes back
/// rather than vanishing: an inventory that silently eats what it cannot hold is the one bug in
/// this area that nobody forgives.</para>
/// <para>It holds the item registry because the per-slot ceiling is a property of the item — sixty
/// four planks and exactly one pickaxe — and the slot is the only place that knows both.</para>
/// </remarks>
public sealed class Inventory
{
    /// <summary>Slots in the bar. Nine, which is the row every player in the genre knows.</summary>
    /// <remarks>
    /// They are the first nine of <see cref="Slots"/>, so the wheel and the number row index
    /// straight into the same array the backpack lives in.
    /// </remarks>
    public const int HotbarSlots = 9;

    /// <summary>Every slot: the bar, then three rows behind it.</summary>
    public const int Slots = HotbarSlots + 27;

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

    /// <summary>Which of the bar's nine is in hand. Wraps within the bar, never into the backpack.</summary>
    public void Select(int slot) => Selected = ((slot % HotbarSlots) + HotbarSlots) % HotbarSlots;

    /// <summary>Moves the selection by a step, wrapping either way.</summary>
    public void Scroll(int by) => Select(Selected + by);

    /// <summary>
    /// Puts a stack in, and returns whatever would not fit.
    /// </summary>
    public ItemStack Add(ItemStack stack)
    {
        if (stack.IsEmpty) return ItemStack.Empty;

        var cap = _items[stack.Item].MaxStack;

        // Three passes, in the order a player would do it themselves. Partial stacks of the same
        // thing anywhere first — the other order leaves a row of half-full slots and an empty one
        // at the end of every gathering trip. Then empty slots in the bar, so what has just been
        // picked up is in reach. Then the backpack.
        for (var pass = 0; pass < 3; pass++)
        {
            var from = pass == 2 ? HotbarSlots : 0;
            var to = pass == 0 ? Slots : pass == 1 ? HotbarSlots : Slots;

            for (var i = from; i < to && !stack.IsEmpty; i++)
            {
                if (_slots[i].IsEmpty == (pass == 0)) continue;
                _slots[i] = _slots[i].Merge(stack, cap, out stack);
            }
        }

        Version++;
        return stack;
    }

    /// <summary>
    /// Puts a changed version of the held stack back where it was.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>For a stack that came OUT of this slot and has been altered</b> — mended, worn, renamed
    /// — rather than for putting something new down. Nothing merges and nothing is checked, because
    /// what is going in is what was already there.
    /// </remarks>
    public void SetHeld(ItemStack stack)
    {
        _slots[Selected] = stack;
        Version++;
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

    /// <summary>True when a slot number is one of the nine the player can reach without opening anything.</summary>
    public static bool InHotbar(int slot) => slot is >= 0 and < HotbarSlots;

    /// <summary>
    /// Puts a stack into one particular slot, and hands back whatever was there or would not fit.
    /// </summary>
    /// <remarks>
    /// <para>The one move a screen needs and a pickup does not: everything else in here decides for
    /// itself where something should go, and a player dragging has already decided.</para>
    /// <para>Same thing on both sides merges, up to the ceiling, and the remainder comes back on
    /// the cursor. Different things swap. Both are what the hand expects and neither loses
    /// anything, which is the only rule that matters.</para>
    /// </remarks>
    public ItemStack PutInto(int slot, ItemStack carried)
    {
        if ((uint)slot >= Slots || carried.IsEmpty) return carried;

        var there = _slots[slot];
        Version++;

        if (there.IsEmpty || there.Matches(carried))
        {
            _slots[slot] = there.Merge(carried, _items[carried.Item].MaxStack, out var over);
            return over;
        }

        _slots[slot] = carried;
        return there;
    }

    /// <summary>Lifts a whole slot onto the cursor, leaving it empty.</summary>
    public ItemStack TakeAll(int slot)
    {
        if ((uint)slot >= Slots || _slots[slot].IsEmpty) return ItemStack.Empty;

        var lifted = _slots[slot];
        _slots[slot] = ItemStack.Empty;
        Version++;
        return lifted;
    }

    /// <summary>
    /// Lifts half a slot onto the cursor, rounded up, leaving the rest.
    /// </summary>
    /// <remarks>
    /// Rounded up so that splitting one thing gives you the one thing rather than nothing, which is
    /// the case a player tries first when they are working out what the button does.
    /// </remarks>
    public ItemStack TakeHalf(int slot)
    {
        if ((uint)slot >= Slots || _slots[slot].IsEmpty) return ItemStack.Empty;

        var there = _slots[slot];
        var half = (there.Count + 1) / 2;

        _slots[slot] = there.Minus(half);
        Version++;
        return there with { Count = half };
    }

    /// <summary>Drops one off the cursor into a slot, for painting a stack out one at a time.</summary>
    public ItemStack PutOne(int slot, ItemStack carried)
    {
        if ((uint)slot >= Slots || carried.IsEmpty) return carried;

        var there = _slots[slot];
        if (!there.IsEmpty && !there.Matches(carried)) return carried;
        if (!there.IsEmpty && there.Count >= _items[carried.Item].MaxStack) return carried;

        _slots[slot] = there.IsEmpty ? carried with { Count = 1 } : there with { Count = there.Count + 1 };
        Version++;
        return carried.MinusOne();
    }

    /// <summary>
    /// Sends a slot to the other half of the inventory, the way a shift-click does.
    /// </summary>
    /// <remarks>
    /// Bar to backpack and backpack to bar, filling partial stacks of the same thing before opening
    /// an empty one — the same order <see cref="Add"/> uses, for the same reason. Whatever will not
    /// fit stays where it was rather than being dropped on the floor: a shift-click that empties a
    /// slot it could not empty is how a player loses a stack without seeing it happen.
    /// </remarks>
    public bool Sweep(int slot)
    {
        if ((uint)slot >= Slots || _slots[slot].IsEmpty) return false;

        var moving = _slots[slot];
        var cap = _items[moving.Item].MaxStack;

        var from = InHotbar(slot) ? HotbarSlots : 0;
        var to = InHotbar(slot) ? Slots : HotbarSlots;

        for (var pass = 0; pass < 2; pass++)
        for (var i = from; i < to && !moving.IsEmpty; i++)
        {
            if (i == slot) continue;
            if (_slots[i].IsEmpty != (pass == 1)) continue;

            _slots[i] = _slots[i].Merge(moving, cap, out moving);
        }

        var moved = moving.Count != _slots[slot].Count;
        _slots[slot] = moving;
        if (moved) Version++;
        return moved;
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
