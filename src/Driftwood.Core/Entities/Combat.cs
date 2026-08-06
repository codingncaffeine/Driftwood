using Driftwood.Core.Items;

namespace Driftwood.Core.Entities;

/// <summary>
/// What a blow is worth, and how far one reaches.
/// </summary>
/// <remarks>
/// <para>The counterpart of <c>MiningRules</c>, and written the same way for the same reason: the
/// rule that turns "what is in hand" into "what happens" belongs in one place, so a new tier is a
/// row in a table rather than an edit somewhere in the input handler. Hardness meets tool speed
/// there; health meets attack damage here.</para>
/// <para>⛔ <b>A bare fist does something.</b> Both halves of that matter. A hand that did nothing
/// would mean the first chicken in a new world could not be killed, and the whole recipe tree hangs
/// off the first thing an animal leaves — but a hand that did as much as a sword would mean the
/// sword was decoration. One half-heart against a chicken's eight is eight blows; a wooden sword
/// takes two.</para>
/// </remarks>
public static class Combat
{
    /// <summary>Half-hearts a bare hand takes off.</summary>
    public const int BareHands = 1;

    /// <summary>
    /// How far a swing reaches, in blocks — a little further than a block can be broken at.
    /// </summary>
    /// <remarks>
    /// ⚠ Deliberately not the same number as the block reach. A cow is a metre and a half of animal
    /// whose middle is what gets aimed at, so a reach measured to the surface of a block and applied
    /// to the middle of a creature is short by half a cow. Three and a half against the block's three.
    /// </remarks>
    public const float Reach = 3.5f;

    /// <summary>Half-hearts one blow with this in hand takes off a creature.</summary>
    public static int DamageOf(ItemType? held) => BareHands + (held?.AttackDamage ?? 0);

    /// <summary>
    /// What each head is worth as a weapon, over a bare hand, at tier zero.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Two numbers per head rather than a damage written out twenty-four times.</b> A base and a
    /// per-tier step is what makes the whole ladder one table — and it is what keeps the shape of the
    /// ladder visible: a sword is a weapon at every rung, an axe is a heavy tool that hurts, and a
    /// shovel is a shovel. ⚠ The step is applied to the <em>tier</em>, which is why gold hits like
    /// stone however fast it digs: it is soft, and softness is the price of the speed.
    /// </remarks>
    public static readonly (int Base, int PerTier)[] HeadDamage =
    [
        (1, 0),   // pickaxe — a lump of metal on a stick, and no better in stormglass than in wood
        (2, 1),   // axe — the tool that is nearly a weapon, and the only reason to carry one to a fight
        (0, 0),   // shovel — a shovel. Worth exactly a bare hand, which is the honest answer
        (3, 1),   // sword — the one thing here made for it
    ];

    /// <summary>What one head at one tier is worth, over a bare hand.</summary>
    /// <remarks>
    /// ⚠ <b>The tier is the material's, not the item's.</b> A sword carries no
    /// <see cref="ItemType.Tier"/> at all — it harvests nothing — so asking the registered item what
    /// rung it is on answers zero for every sword in the game and flattens the whole weapon ladder.
    /// This is called where the rung is still in scope, which is the only place that number is right.
    /// </remarks>
    public static int DamageFor(int head, int tier)
    {
        var (baseDamage, perTier) = HeadDamage[head];
        return baseDamage + perTier * tier;
    }
}
