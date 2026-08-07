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
    /// <para>⚠ <b>The cap and the best set's own total are the same number on purpose.</b> A ceiling
    /// above what anything can reach is a ceiling nobody ever meets, and a ceiling below it makes
    /// the last two pieces of the best set do nothing while still costing what they cost.</para>
    /// <para>⛳ <b>Which means a new top material moves this, and that is the intended move.</b> The
    /// user: <em>diamond will be the best equipment you can make until we start working on the
    /// netherealm stuff.</em> When that lands it is the same two lines it was this time — raise the
    /// cap, thin <see cref="PerPoint"/> to keep the 80% ceiling where it is — and the check below
    /// insists the two agree, so forgetting one of them is a red gate rather than a quiet nerf to
    /// everything already made.</para>
    /// </remarks>
    public const int MaxPoints = 24;

    /// <summary>Share of a blow one point turns aside.</summary>
    /// <remarks>
    /// <para>A full set of the best stops four fifths and nothing stops all of it. Armour that could
    /// reach nothing-gets-through would make the deep safe rather than survivable, and the whole of
    /// the Emberdeep rests on it not being safe.</para>
    /// <para>⚠ <b>This was 4% of 20 and is now a thirtieth of 24, which is the SAME 80% ceiling.</b>
    /// Stormglass already reached the old cap exactly, so a better material had nowhere to go —
    /// raising the cap and thinning each point makes room for diamond above it without moving the
    /// wall: stormglass now sits at 66.7% under diamond's 80%.</para>
    /// </remarks>
    public const float PerPoint = 1f / 30f;

    /// <summary>One material a set can be made of.</summary>
    /// <param name="Name">Ours, and the stem every item of it is named from.</param>
    /// <param name="Made">What it is beaten out of — an item's name.</param>
    /// <param name="Pack">
    /// The material a texture pack calls this. ⚠ Not our name: a pack ships its materials by the
    /// genre's own names and ours deliberately are not those, so the mapping is stated rather than
    /// derived — stormglass wears netherite's sheet, which is the nearest thing anybody paints.
    /// </param>
    /// <param name="Borrow">
    /// A material to take the SHAPE from and recolour, when a pack has none of this one's own.
    /// Empty for anything every pack already paints.
    /// </param>
    /// <param name="Points">Head, chest, legs and feet, in <see cref="EquipSlot"/> order.</param>
    public readonly record struct Material(
        string Name, string Made, string Pack, byte R, byte G, byte B, int[] Points, int Durability,
        string Borrow = "")
    {
        /// <summary>What a whole set of it is worth.</summary>
        public int Total => Points[0] + Points[1] + Points[2] + Points[3];

        /// <summary>This material's colour, packed, for recolouring a borrowed picture into it.</summary>
        public uint Tint => ((uint)R << 16) | ((uint)G << 8) | B;
    }

    /// <summary>
    /// Which material an item of armour is, as an index, or −1 when it is not a piece at all.
    /// </summary>
    /// <remarks>
    /// ⚠ Matched on the stem the item was NAMED from rather than on a second table. Every piece is
    /// <c>&lt;material&gt;_&lt;piece&gt;</c> by construction — see <see cref="ItemName"/> — so asking the
    /// name back is asking the same table that built it, and a material added to the array is
    /// answered here without an edit.
    /// </remarks>
    public static int MaterialOf(ItemType? item)
    {
        if (item is null) return -1;

        for (var m = 0; m < Materials.Length; m++)
        foreach (var piece in Pieces)
            if (string.Equals(ItemName(Materials[m], piece), item.Name, StringComparison.Ordinal))
                return m;

        return -1;
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
        // ⛔ THIS WORE CHAINMAIL'S SHEET, and that was the nearest thing anybody had painted right up
        // until copper gear shipped in the reference. Measured against the packs on this machine:
        // three of seven now carry a full copper set — helmet, chestplate, leggings, boots.
        // Chainmail is GREY and this metal is 198,124,78, so borrowing it was a mismatch accepted
        // for want of anything better. There is something better now.
        new("copper", "copper_ingot", "copper", 198, 124, 78, [2, 4, 3, 2], 200, Borrow: "iron"),
        new("gold", "gold_ingot", "gold", 232, 196, 82, [2, 6, 5, 3], 100),
        new("iron", "iron_ingot", "iron", 214, 214, 220, [2, 6, 5, 2], 280),

        // ⚠ Stormglass moved off the pack's "diamond" sheet the day the game got a real diamond.
        // A pack ships six materials and each of ours should wear exactly one of them.
        new("stormglass", "stormglass", "netherite", 118, 224, 220, [3, 8, 6, 3], 600),

        // ⛳ The top, and the only set that reaches the cap. Baby blue at the user's own asking, and
        // deliberately a long way off stormglass's teal: the two are the last two rungs and a player
        // has to be able to tell a full set of one from a full set of the other across a room.
        new("diamond", "diamond", "diamond", DiamondR, DiamondG, DiamondB, [4, 9, 7, 4], 900),
    ];

    /// <summary>
    /// Diamond's colour, in one place because five different things wear it.
    /// </summary>
    /// <remarks>
    /// ⛳ A pale sky blue rather than the white-cyan the genre uses, at the user's request. It has to
    /// read as its own material beside stormglass's 118,224,220 teal, and beside azurite, which is
    /// the other blue in the game — so it is lighter than both and much less saturated.
    /// </remarks>
    public const byte DiamondR = 150, DiamondG = 214, DiamondB = 245;

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
    /// <summary>One shield: what its board is faced with, and what that buys.</summary>
    /// <param name="Made">The metal, which is the only thing that varies.</param>
    /// <param name="Share">Share of whatever got past the plate that raising it turns aside.</param>
    public readonly record struct Shield(
        string Name, string Made, float Share, int Durability, byte R, byte G, byte B);

    /// <summary>
    /// The three, weakest first. Timber in every case; the facing is the ladder.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Three, not six.</b> A leather shield is a coat and a gold one is a decoration; what
    /// makes a board stop a blow is the metal across its face. The plain one keeps the bare name
    /// <c>shield</c> — ⛔ <b>renaming it would strip the shield out of any save that had one</b>,
    /// because a save stores items by name through its own palette.
    /// </remarks>
    public static readonly Shield[] Shields =
    [
        new("shield", "iron_ingot", 0.50f, 340, 190, 190, 198),
        new("stormglass_shield", "stormglass", 0.58f, 760, 118, 224, 220),
        new("diamond_shield", "diamond", 0.66f, 1300, DiamondR, DiamondG, DiamondB),
    ];

    /// <summary>The plainest one, and the name every other part of the game spells.</summary>
    public const string ShieldName = "shield";

    /// <summary>
    /// What the thing in the other hand turns aside when it is raised. Zero when it is not a shield.
    /// </summary>
    /// <remarks>
    /// ⚠ Asked in exactly one place. The offhand takes anything — a torch, a stack of blocks,
    /// dinner — so "is there something in it" is not the question; "what does the thing in it stop"
    /// is, and reading it off the item means a fourth shield is a row in the table above.
    /// </remarks>
    public static float ShieldInHand(Equipment worn, ItemRegistry items)
    {
        var stack = worn[EquipSlot.Offhand];
        return stack.IsEmpty ? 0f : items[stack.Item].ShieldShare;
    }

    /// <summary>What is left of a blow after this much armour and, perhaps, a raised shield.</summary>
    public static int Survive(int halfHearts, int points, float shielded = 0f)
    {
        if (halfHearts <= 0) return Math.Max(0, halfHearts);

        var through = halfHearts * (1f - Math.Min(MaxPoints, Math.Max(0, points)) * PerPoint);
        through *= 1f - Math.Clamp(shielded, 0f, 0.9f);

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
        if (halfHearts <= 0 || ShieldInHand(worn, items) <= 0f) return false;

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

        // ⛳ And the shields, which are a share of what got past the plate rather than more plate.
        // Both halves are asserted: one has to do something to somebody wearing nothing, and it has
        // to STILL do something to somebody in a full set — which is exactly what expressing it in
        // points would have failed, since the cap is reached by the best set alone.
        var bestShield = 0f;

        foreach (var shield in Shields)
        {
            if (!items.TryByName(shield.Name, out var type))
            {
                faults.Add($"'{shield.Name}' is in the shield table and is not a registered item");
                continue;
            }

            if (type.Wears != EquipSlot.Offhand)
                faults.Add($"'{shield.Name}' is worn on {type.Wears?.ToString() ?? "nothing"}, not the other hand");
            if (type.Durability <= 0) faults.Add($"'{shield.Name}' never wears out");
            if (type.MaxStack != 1) faults.Add($"'{shield.Name}' stacks, so wear would be shared");
            if (Math.Abs(type.ShieldShare - shield.Share) > 0.001f)
                faults.Add($"'{shield.Name}' stops {type.ShieldShare:P0} rather than the table's {shield.Share:P0}");

            bestShield = MathF.Max(bestShield, shield.Share);
        }

        if (Survive(10, 0, Shields[0].Share) >= Survive(10, 0))
            faults.Add("raising the plainest shield with no armour on changes nothing");

        if (Survive(10, MaxPoints, bestShield) >= Survive(10, MaxPoints))
            faults.Add("raising the best shield in a full set changes nothing, so it is only for the poor");

        // ⛔ And the ladder itself, which nothing else would notice: three shields that all stop the
        // same share are two items nobody chooses between.
        for (var i = 1; i < Shields.Length; i++)
        {
            if (Shields[i].Share <= Shields[i - 1].Share)
                faults.Add($"'{Shields[i].Name}' stops no more than '{Shields[i - 1].Name}'");
            if (Shields[i].Durability <= Shields[i - 1].Durability)
                faults.Add($"'{Shields[i].Name}' lasts no longer than '{Shields[i - 1].Name}'");
        }

        return faults;
    }
}
