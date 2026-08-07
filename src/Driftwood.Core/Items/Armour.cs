namespace Driftwood.Core.Items;

/// <summary>
/// What can be worn, how much it turns aside, and how long it lasts.
/// </summary>
/// <remarks>
/// <para>⛳ <b>The table that unblocks a queue.</b> Leather has been dropping off cows since the
/// animals landed and nothing in the game consumed it; the four worn slots were real storage with a
/// filter on <see cref="ItemType.Wears"/> and refused everything, because nothing set it. Both ends
/// of that are this file.</para>
/// <para><b>Points rather than a percentage per piece.</b> Five materials times four pieces is
/// twenty numbers, and asking each of them "what fraction do you stop" makes them compose wrongly —
/// two pieces each stopping half would stop three quarters, so a full set of the worst material
/// would beat one piece of the best. Points add, and the curve from points to a fraction is written
/// once, which is also the only place the ceiling lives.</para>
/// <para>⚠ <b>Gold is the rung that is not a rung, exactly as it is on the tool ladder.</b> It turns
/// aside more than iron and wears out in a third of the time, so it is what a player puts on for a
/// particular afternoon rather than a step on the way up. A ladder where every rung is strictly
/// better is a ladder with no decisions on it.</para>
/// </remarks>
public static class Armour
{
    /// <summary>Points a whole set of the best material is worth, and the cap.</summary>
    /// <remarks>
    /// ⚠ <b>The cap and stormglass's own total are the same number on purpose.</b> A ceiling above
    /// what anything can reach is a ceiling nobody ever meets, and a ceiling below it makes the last
    /// two pieces of the best set do nothing while still costing what they cost.
    /// </remarks>
    public const int MaxPoints = 20;

    /// <summary>Share of a blow one point turns aside.</summary>
    /// <remarks>
    /// Four percent a point, so a full set of the best stops four fifths and nothing stops all of
    /// it. Armour that could reach nothing-gets-through would make the deep safe rather than
    /// survivable, and the whole of the Emberdeep rests on it not being safe.
    /// </remarks>
    public const float PerPoint = 0.04f;

    /// <summary>One material a set can be made of.</summary>
    /// <param name="Name">Ours, and the stem every item of it is named from.</param>
    /// <param name="Made">What it is beaten out of — an item's name.</param>
    /// <param name="Pack">
    /// The material a texture pack calls this. ⚠ Not our name: a pack ships six materials by the
    /// genre's own names and ours deliberately are not those, so the mapping is stated rather than
    /// derived. Copper wears chainmail's sheet and stormglass wears diamond's, which are the nearest
    /// things a pack actually paints.
    /// </param>
    /// <param name="Points">Head, chest, legs and feet, in <see cref="EquipSlot"/> order.</param>
    public readonly record struct Material(
        string Name, string Made, string Pack, byte R, byte G, byte B, int[] Points, int Durability)
    {
        /// <summary>What a whole set of it is worth.</summary>
        public int Total => Points[0] + Points[1] + Points[2] + Points[3];
    }

    /// <summary>
    /// The five, weakest first.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Leather is first and it is the reason this is worth building at all.</b> It is the only
    /// armour a player can have before going underground, it comes off an animal rather than out of
    /// a wall, and it is what makes a herd worth keeping rather than worth eating.
    /// </remarks>
    public static readonly Material[] Materials =
    [
        new("leather", "leather", "leather", 152, 100, 62, [1, 3, 2, 1], 90),
        new("copper", "copper_ingot", "chainmail", 198, 124, 78, [2, 4, 3, 2], 200),
        new("gold", "gold_ingot", "gold", 232, 196, 82, [2, 6, 5, 3], 100),
        new("iron", "iron_ingot", "iron", 214, 214, 220, [2, 6, 5, 2], 280),
        new("stormglass", "stormglass", "diamond", 118, 224, 220, [3, 8, 6, 3], 600),
    ];

    /// <summary>One piece: what it is called, where it is worn, and how it is laid out.</summary>
    /// <param name="Rows">
    /// The pattern, <c>M</c> for the material. The genre's four shapes, and they are shapes rather
    /// than counts on purpose: a helmet is a picture of a helmet and reads as one on the bench.
    /// </param>
    public readonly record struct Piece(string Name, EquipSlot Slot, string[] Rows);

    /// <summary>The four, in <see cref="EquipSlot"/> order so an index is a slot.</summary>
    public static readonly Piece[] Pieces =
    [
        new("helmet", EquipSlot.Head, ["MMM", "M M"]),
        new("chestplate", EquipSlot.Chest, ["M M", "MMM", "MMM"]),
        new("leggings", EquipSlot.Legs, ["MMM", "M M", "M M"]),
        new("boots", EquipSlot.Feet, ["M M", "M M"]),
    ];

    /// <summary>The name of one piece of one material, and the one place it is spelled.</summary>
    public static string ItemName(in Material material, in Piece piece) => $"{material.Name}_{piece.Name}";

    /// <summary>
    /// Points currently worn. Zero for a player in their own clothes.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Asked of the items rather than counted as things are put on.</b> A running total is a
    /// second copy of the truth, and every path that can change what is worn — putting a piece on,
    /// taking it off, a load, a death, a piece wearing through mid-fight — is a path that can forget
    /// to update it. Five lookups on the frame something changed is not a cost worth a bug.
    /// </remarks>
    public static int PointsOf(Equipment worn, ItemRegistry items)
    {
        var points = 0;

        for (var slot = 0; slot < (int)EquipSlot.Offhand; slot++)
        {
            var stack = worn.At(slot);
            if (stack.IsEmpty) continue;

            var type = items[stack.Item];
            if (type.Wears is not { } wears || (int)wears != slot) continue;

            points += type.ArmourPoints;
        }

        return Math.Min(MaxPoints, points);
    }

    /// <summary>
    /// Share of whatever got past the armour that a raised shield turns aside as well.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Applied AFTER the plate rather than added to the points, and that is the whole reason a
    /// shield is worth carrying in a full set.</b> Points are capped at twenty and stormglass alone
    /// reaches twenty, so a shield expressed in points would do exactly nothing for the player most
    /// likely to be holding one. A share of the remainder always does something and never reaches
    /// none-gets-through, because it is a share of a share.
    /// </remarks>
    public const float ShieldShare = 0.5f;

    /// <summary>The one item that is carried in the other hand rather than worn.</summary>
    public const string ShieldName = "shield";

    /// <summary>True when what is in the other hand is a shield.</summary>
    /// <remarks>
    /// ⚠ Asked by name in exactly one place. The offhand takes anything — a torch, a stack of
    /// blocks, dinner — so "is there something in it" is not the question; "is the thing in it a
    /// shield" is, and it wants one spelling rather than one per caller.
    /// </remarks>
    public static bool ShieldInHand(Equipment worn, ItemRegistry items)
    {
        var stack = worn[EquipSlot.Offhand];
        return !stack.IsEmpty && items[stack.Item].Name == ShieldName;
    }

    /// <summary>What is left of a blow after this much armour and, perhaps, a raised shield.</summary>
    public static int Survive(int halfHearts, int points, bool shielded = false)
    {
        if (halfHearts <= 0) return Math.Max(0, halfHearts);

        var through = halfHearts * (1f - Math.Min(MaxPoints, Math.Max(0, points)) * PerPoint);
        if (shielded) through *= 1f - ShieldShare;

        // ⛔ Rounded UP, and never to nothing. A blow reduced to zero is a blow a player in a full
        // set can stand in lava and ignore for ever — the reduction is a fraction of the damage, so
        // anything that took one half-heart would take none of it, and the smallest hits in the game
        // are exactly the repeated ones. One is the floor.
        return Math.Max(1, (int)MathF.Ceiling(through));
    }

    /// <summary>
    /// Puts a blow's worth of wear on every piece worn, and says how many broke.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Every piece, not the piece that was hit.</b> Nothing in this game knows where a blow
    /// landed — a fall lands on the feet, lava on all of it, a spider on whatever it reached — and
    /// inventing a hit location to spend durability by would be a rule with no observable behaviour
    /// behind it. A set wears evenly and is replaced as a set, which is also how a player thinks
    /// about it.
    /// </remarks>
    public static int Wear(Equipment worn, ItemRegistry items, int halfHearts)
    {
        if (halfHearts <= 0) return 0;

        var broke = 0;

        for (var slot = 0; slot < (int)EquipSlot.Offhand; slot++)
        {
            var stack = worn.At(slot);
            if (stack.IsEmpty) continue;

            var type = items[stack.Item];
            if (type.Wears is null || type.Durability <= 0) continue;

            var after = stack.Worn(type.Durability);
            worn.Restore((EquipSlot)slot, after);
            if (after.IsEmpty) broke++;
        }

        return broke;
    }

    /// <summary>Puts wear on the shield, if there is one up. True when it gave out.</summary>
    /// <remarks>
    /// ⚠ <b>Only when it was raised.</b> A shield that wore out from being carried would be a thing
    /// a player takes out of the other hand between fights, which is an inventory chore rather than
    /// a decision. It costs durability for the blows it actually turned.
    /// </remarks>
    public static bool WearShield(Equipment worn, ItemRegistry items, int halfHearts)
    {
        if (halfHearts <= 0 || !ShieldInHand(worn, items)) return false;

        var stack = worn[EquipSlot.Offhand];
        var type = items[stack.Item];
        if (type.Durability <= 0) return false;

        var after = stack.Worn(type.Durability);
        worn.Restore(EquipSlot.Offhand, after);
        return after.IsEmpty;
    }

    /// <summary>
    /// Checks the table says what the rest of the game believes about it.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The cap and the totals are two numbers that must agree, and nothing else would notice.</b>
    /// A material whose pieces add to more than <see cref="MaxPoints"/> has pieces that do nothing, and
    /// one that adds to less than the cap means the ceiling is unreachable — both look exactly like
    /// working armour from every screen in the game.
    /// </remarks>
    public static List<string> Validate(ItemRegistry items)
    {
        var faults = new List<string>();
        var best = 0;

        foreach (var material in Materials)
        {
            if (material.Points.Length != (int)EquipSlot.Offhand)
                faults.Add($"{material.Name} names {material.Points.Length} pieces, not {(int)EquipSlot.Offhand}");

            if (material.Total > MaxPoints)
                faults.Add($"a set of {material.Name} is worth {material.Total}, past the cap of {MaxPoints}");

            best = Math.Max(best, material.Total);

            foreach (var piece in Pieces)
            {
                var name = ItemName(material, piece);
                if (!items.TryByName(name, out var type))
                {
                    faults.Add($"'{name}' is in the armour table and is not a registered item");
                    continue;
                }

                if (type.Wears != piece.Slot)
                    faults.Add($"'{name}' is worn on {type.Wears?.ToString() ?? "nothing"}, not {piece.Slot}");

                if (type.ArmourPoints != material.Points[(int)piece.Slot])
                    faults.Add($"'{name}' is worth {type.ArmourPoints} rather than the table's "
                             + $"{material.Points[(int)piece.Slot]}");

                if (type.MaxStack != 1)
                    faults.Add($"'{name}' stacks {type.MaxStack} deep, so wear would be shared");
            }
        }

        if (best != MaxPoints)
            faults.Add($"the best set in the game is worth {best} against a cap of {MaxPoints}, "
                     + "so the ceiling can never be reached");

        // ⛔ THE CONTROL THE REDUCTION NEEDS. "Armour reduces damage" is true of a build where it
        // reduces everything to nothing, and of one where it takes a single half-heart off a mortal
        // blow. Both ends are asserted against real numbers rather than against the formula.
        if (Survive(10, 0) != 10) faults.Add("a blow with no armour on is not the blow that was thrown");
        if (Survive(10, MaxPoints) is var capped && capped is < 1 or >= 10)
            faults.Add($"a full set turns a blow of 10 into {capped}");
        if (Survive(1, MaxPoints) < 1) faults.Add("a full set makes the smallest blow free");

        // ⛳ And the shield, which is a share of what got past the plate rather than more plate.
        // Both halves are asserted: it has to do something to somebody wearing nothing, and it has
        // to STILL do something to somebody in a full set — which is exactly what expressing it in
        // points would have failed, since the cap is already reached by the best set alone.
        if (!items.TryByName(ShieldName, out var shield))
        {
            faults.Add($"'{ShieldName}' is not a registered item");
        }
        else
        {
            if (shield.Wears != EquipSlot.Offhand)
                faults.Add($"the shield is worn on {shield.Wears?.ToString() ?? "nothing"}, not the other hand");
            if (shield.Durability <= 0) faults.Add("the shield never wears out");
            if (shield.MaxStack != 1) faults.Add("shields stack, so wear would be shared between them");
        }

        if (Survive(10, 0, shielded: true) >= Survive(10, 0))
            faults.Add("raising a shield with no armour on changes nothing");

        if (Survive(10, MaxPoints, shielded: true) >= Survive(10, MaxPoints))
            faults.Add("raising a shield in a full set changes nothing, so it is only for the poor");

        return faults;
    }
}
