namespace Driftwood.Core.Items;

/// <summary>Somewhere on the body a thing can be worn or held, in the order they are drawn.</summary>
/// <remarks>
/// Head down to feet, then the other hand. That order is the order the slots run down the left of
/// the panel in every pack's <c>inventory.png</c>, and keeping the enum in it means the layout is a
/// loop rather than a table.
/// </remarks>
public enum EquipSlot
{
    Head,
    Chest,
    Legs,
    Feet,

    /// <summary>The other hand. Takes anything — a torch, a stack of blocks, food.</summary>
    Offhand,
}

/// <summary>
/// What is being worn and what is in the other hand.
/// </summary>
/// <remarks>
/// <para>Its own container rather than five more slots on <see cref="Inventory"/>, because the two
/// answer different questions. A recipe pays out of the pockets and must never quietly strip the
/// boots off a player to do it, and <see cref="Inventory.Take"/> walks every slot it can see.</para>
/// <para>Nothing in the game is armour yet — <see cref="ItemType.Wears"/> is <c>null</c> on every
/// registered item — so the four worn slots refuse everything and read as locked. That is the honest
/// state of it and it is exactly the prerequisite the worn-armour work is blocked on: the art was
/// always the easy half. The offhand takes anything today, because there is no reason it should not.
/// </para>
/// </remarks>
public sealed class Equipment
{
    public const int Slots = 5;

    private readonly ItemStack[] _worn = new ItemStack[Slots];
    private readonly ItemRegistry _items;

    public Equipment(ItemRegistry items) => _items = items;

    /// <summary>Bumped whenever what is worn changes, the same way the pockets do it.</summary>
    public int Version { get; private set; }

    public ItemStack this[EquipSlot slot] => _worn[(int)slot];

    public ItemStack At(int slot) => (uint)slot < Slots ? _worn[slot] : ItemStack.Empty;

    /// <summary>True when this slot would take that item at all.</summary>
    public bool Accepts(EquipSlot slot, ItemStack stack)
    {
        if (stack.IsEmpty) return true;
        if (slot == EquipSlot.Offhand) return true;
        return _items[stack.Item].Wears == slot;
    }

    /// <summary>
    /// Puts a carried stack in, and hands back what was displaced — or the stack itself, refused.
    /// </summary>
    public ItemStack Put(EquipSlot slot, ItemStack carried)
    {
        if (carried.IsEmpty || !Accepts(slot, carried)) return carried;

        var there = _worn[(int)slot];
        Version++;

        if (there.IsEmpty || there.Matches(carried))
        {
            _worn[(int)slot] = there.Merge(carried, _items[carried.Item].MaxStack, out var over);
            return over;
        }

        _worn[(int)slot] = carried;
        return there;
    }

    /// <summary>
    /// Trades what is in the selected pocket with what is in the other hand.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>In Core rather than in the renderer</b>, which is this project's rule for anything
    /// that is a fact about the game rather than about drawing: the trap below is invisible from a
    /// screenshot and needs no window to catch, so <c>--audit</c> can run it.</para>
    /// <para>⛔ <b>The offhand is emptied BEFORE the main hand goes in.</b> <see cref="Put"/> merges
    /// when the two stacks match — so swapping a stack of torches with a stack of torches would fold
    /// them into one and hand the overflow back to nobody. That is a swap which is silently a
    /// transfer, and which loses whatever will not fit. Taking it out first leaves the slot empty and
    /// makes the put a placement.</para>
    /// <para>⚠ It swaps whatever is there, including nothing on either side. Putting a torch into an
    /// empty offhand and taking it back out are deliberately the same gesture.</para>
    /// </remarks>
    public void SwapWithHeld(Inventory pockets)
    {
        var main = pockets.Held;
        var other = TakeAll(EquipSlot.Offhand);

        pockets.SetHeld(other);
        Put(EquipSlot.Offhand, main);
    }

    /// <summary>
    /// Puts <see cref="SwapWithHeld"/> through the four cases, including the one that merges.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The stack-of-torches case is the whole reason this exists.</b> Written the obvious way —
    /// <c>Put</c> the main hand in and keep what comes back — a swap of two matching stacks folds
    /// them into one and returns the overflow, so a player ends the gesture holding nothing and
    /// carrying a doubled stack in the other hand. Every other case looks right, which is exactly
    /// how it would ship. The counts are asserted, not just the item ids.
    /// </remarks>
    public static List<string> Validate(ItemRegistry items)
    {
        var faults = new List<string>();

        if (!items.TryByName("torch", out var torchType)) return faults;
        if (!items.TryByName("stone", out var stoneType)) return faults;

        var torch = torchType.Id;
        var stone = stoneType.Id;

        Case("both hands full", new ItemStack(stone, 4), new ItemStack(torch, 7));
        Case("into an empty hand", new ItemStack(torch, 3), ItemStack.Empty);
        Case("out of an empty hand", ItemStack.Empty, new ItemStack(torch, 3));
        Case("nothing either side", ItemStack.Empty, ItemStack.Empty);

        // ⛔ THE TRAP: the same item on both sides. Merging is right for Put and wrong for a swap.
        Case("the same thing in both", new ItemStack(torch, 30), new ItemStack(torch, 20));

        return faults;

        void Case(string label, ItemStack held, ItemStack offhand)
        {
            var pockets = new Inventory(items);
            var worn = new Equipment(items);

            pockets.SetHeld(held);
            worn.Put(EquipSlot.Offhand, offhand);

            worn.SwapWithHeld(pockets);

            var nowHeld = pockets.Held;
            var nowOff = worn[EquipSlot.Offhand];

            if (nowHeld.Item != offhand.Item || nowHeld.Count != offhand.Count)
                faults.Add($"{label}: the hand ended with {Say(nowHeld)} rather than {Say(offhand)}");

            if (nowOff.Item != held.Item || nowOff.Count != held.Count)
                faults.Add($"{label}: the other hand ended with {Say(nowOff)} rather than {Say(held)}");

            // Nothing may be created or destroyed by a swap, whatever else went wrong.
            if (nowHeld.Count + nowOff.Count != held.Count + offhand.Count)
                faults.Add($"{label}: {held.Count + offhand.Count} went in and "
                         + $"{nowHeld.Count + nowOff.Count} came out");
        }

        string Say(ItemStack stack) =>
            stack.IsEmpty ? "nothing" : $"{stack.Count}x {items[stack.Item].Name}";
    }

    /// <summary>Lifts a slot off, leaving it empty.</summary>
    public ItemStack TakeAll(EquipSlot slot)
    {
        var lifted = _worn[(int)slot];
        if (lifted.IsEmpty) return ItemStack.Empty;

        _worn[(int)slot] = ItemStack.Empty;
        Version++;
        return lifted;
    }

    /// <summary>Empties everything into an inventory, and hands back whatever would not fit.</summary>
    public List<ItemStack> Empty(Inventory into)
    {
        var spilled = new List<ItemStack>();

        for (var i = 0; i < Slots; i++)
        {
            if (_worn[i].IsEmpty) continue;

            var left = into.Add(_worn[i]);
            _worn[i] = ItemStack.Empty;
            Version++;
            if (!left.IsEmpty) spilled.Add(left);
        }

        return spilled;
    }

    /// <summary>
    /// Puts a worn slot back exactly as a save left it, without asking whether it belongs there.
    /// </summary>
    /// <remarks>
    /// ⚠ Deliberately past the <see cref="Accepts"/> filter. Nothing in the game sets
    /// <see cref="ItemType.Wears"/> yet, so every worn slot refuses everything — and a load that
    /// went through <see cref="Put"/> would empty somebody's armour on the day armour exists and
    /// its rules change. What was worn when it was saved is what is worn when it is opened.
    /// </remarks>
    public void Restore(EquipSlot slot, ItemStack stack)
    {
        if ((uint)slot >= (uint)Slots) return;
        _worn[(int)slot] = stack;
        Version++;
    }

    public void Clear()
    {
        Array.Clear(_worn);
        Version++;
    }
}
