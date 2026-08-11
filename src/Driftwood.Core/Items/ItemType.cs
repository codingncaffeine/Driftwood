using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;

namespace Driftwood.Core.Items;

/// <summary>What a tool is for. A block names the class that takes it, not the tool.</summary>
/// <remarks>
/// The class is the whole reason a pickaxe is not simply a faster hand. Naming it on both sides —
/// the block says which class harvests it, the tool says which class it is — means a new block
/// declares its own rule and a new tool inherits every rule already written.
/// </remarks>
public enum ToolClass
{
    /// <summary>Not a tool. A hand, a plank, a lump of coal.</summary>
    None = 0,

    /// <summary>Rock, ore and everything smelted out of them.</summary>
    Pickaxe,

    /// <summary>Timber, and everything built out of it.</summary>
    Axe,

    /// <summary>Loose ground: dirt, sand, gravel, clay, snow.</summary>
    Shovel,

    /// <summary>What creatures are fought with.</summary>
    Sword,

    /// <summary>
    /// Takes something off a live animal.
    /// </summary>
    /// <remarks>
    /// ⛳ A tool class rather than a named item, so <c>CreatureDrops</c> can say "whatever shears"
    /// exactly as a block says "whatever picks". It is also the first class here whose work is not
    /// mining at all — which is the point: the class says what a tool is <em>for</em>, and only some
    /// of those things are digging.
    /// </remarks>
    Shears,

    /// <summary>
    /// Turns ground over into something that will take a seed.
    /// </summary>
    /// <remarks>
    /// ⛳ The second class here whose work is not mining, after <see cref="Shears"/>, and it settles
    /// the shape the first one only suggested: a tool class says what a tool is FOR, and digging is
    /// one of several answers. A hoe breaks nothing faster than a hand and is the only way to make a
    /// field.
    /// </remarks>
    Hoe,
}

/// <summary>What right-clicking with a non-placeable item does.</summary>
/// <remarks>
/// Named on the item rather than inferred from its id, so the client, handbook and recipe report
/// all agree that a farpearl is spent by throwing and an arrow is spent by a bow.
/// </remarks>
public enum ItemUse
{
    None,
    Bow,
    BowAmmunition,
    ThrownFarstep,
    Brush,
    TreasureChart,
    TrialKey,
    CrownKey,
    Keepsake,
}

/// <summary>
/// Static description of one thing a player can carry. Registered at startup, then read-only.
/// </summary>
/// <remarks>
/// <para>The mirror of <see cref="BlockType"/> on the other side of the divide. A block is a thing
/// the world is made of; an item is a thing pockets are counted in. Most items put a block down and
/// most blocks leave an item, but neither of those is a rule — a stick puts nothing down, an ore
/// leaves something that is not itself, and every stair orientation leaves the same one item.</para>
/// <para><see cref="Places"/> carries the whole placement rule rather than a single block id,
/// because a slab and a stair are not one block each. Hanging it here rather than in a table beside
/// the inventory is what makes "the thing in your hand knows how it lands" true by construction.</para>
/// </remarks>
public sealed class ItemType
{
    public required string Name { get; init; }

    /// <summary>What a player is shown. Free of the underscores an id needs.</summary>
    public required string Label { get; init; }

    /// <summary>Texture array layer this draws as, in a slot and on the floor.</summary>
    public required ushort IconLayer { get; set; }

    /// <summary>How many fit in a slot. One for anything that carries wear.</summary>
    public int MaxStack { get; init; } = ItemStack.MaxCount;

    /// <summary>What this puts down, and how it decides which way up. Null when it places nothing.</summary>
    public Placeable? Places { get; init; }

    /// <summary>
    /// True when using this puts an ENTITY into the world rather than a block — the carts.
    /// </summary>
    /// <remarks>
    /// The client owns the gesture (a cart is clicked onto a rail, not built into a cell), so
    /// this flag is how anything headless — the recipe report's consumption sweep — knows the
    /// item's purpose is placement, exactly as <see cref="Places"/> says it for blocks.
    /// </remarks>
    public bool PlacesEntity { get; init; }

    /// <summary>Its non-placement use, if it has one.</summary>
    public ItemUse Use { get; init; }

    /// <summary>True when this draws as a cube rather than as a flat sprite.</summary>
    /// <remarks>
    /// Not the same question as <see cref="Places"/>. A torch is placeable and is drawn flat, because
    /// a cube of torch texture is a cube of black. Declared rather than derived for exactly that.
    /// </remarks>
    public bool DrawsAsBlock { get; init; }

    /// <summary>Which class of work this does, if any.</summary>
    public ToolClass Tool { get; init; }

    /// <summary>
    /// The hardest tier of block this will harvest. Zero for anything that is not a tool.
    /// </summary>
    /// <remarks>
    /// Deliberately not the same number as <see cref="MiningSpeed"/>. Gold is the case that proves
    /// they are two axes: it cuts faster than iron and still will not bring up a stormglass, which is
    /// what makes it a choice rather than a rung.
    /// </remarks>
    public int Tier { get; init; }

    /// <summary>How much faster than a hand this is at its own class of work.</summary>
    public float MiningSpeed { get; init; } = 1f;

    /// <summary>
    /// Half-hearts one blow with this takes off, over and above what a bare fist does.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>A third axis, and it has to be.</b> <see cref="Tier"/> says what a tool will bring up and
    /// <see cref="MiningSpeed"/> says how fast it works, and neither of them is what a sword is for —
    /// a sword harvests nothing and digs at the speed of a hand. Written as the extra rather than as
    /// the total so that <see cref="Combat.BareHands"/> is the one place the floor is named, and
    /// everything that is not a weapon can leave this at zero and still hit for something.
    /// </remarks>
    public int AttackDamage { get; init; }

    /// <summary>Uses before it breaks. Zero means it never wears out.</summary>
    public int Durability { get; init; }

    /// <summary>Seconds of furnace burn this is worth. Zero means it is not fuel.</summary>
    public float BurnSeconds { get; init; }

    /// <summary>
    /// Half-hearts eating this puts back. Zero for anything that is not food.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Healing rather than feeding, and that is a decision to revisit rather than a shortcut.</b>
    /// There is no hunger yet, so a meat that only filled a bar nothing showed would be a drop with
    /// nothing on the other end of it — which is exactly what the animals were added to stop. Health
    /// is the resource that exists, so that is what food is measured in; the day hunger lands, this
    /// number splits into two and every food already says how much it is worth.
    /// </remarks>
    public int Feeds { get; init; }

    /// <summary>True when this can be eaten.</summary>
    public bool IsFood => Feeds > 0;

    /// <summary>
    /// Where on the body this is worn, or null for anything that is not armour.
    /// </summary>
    /// <remarks>
    /// The worn slots filter on this, so it is also what decides whether a slot will take a thing at
    /// all — a helmet in the boot square is refused here rather than by a rule written beside the
    /// screen.
    /// </remarks>
    public EquipSlot? Wears { get; init; }

    /// <summary>
    /// Points of armour this is worth while it is worn. Zero for everything else.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Points, not a fraction.</b> See <see cref="Armour"/>: fractions do not add, so four
    /// pieces each stopping a share of a blow would compose into a set of the worst material beating
    /// one piece of the best. The curve from points to a share is written once and lives there.
    /// </remarks>
    public int ArmourPoints { get; init; }

    /// <summary>
    /// Share of an incoming blow this turns aside while it is raised in the other hand.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>A share rather than points, and that is what makes a shield worth carrying in a full
    /// set.</b> Points are capped and the best armour already reaches the cap, so a shield expressed
    /// in points would do exactly nothing for the player most likely to be holding one. A share of
    /// whatever got past the plate always does something and never reaches none-gets-through.
    /// </remarks>
    public float ShieldShare { get; init; }

    /// <summary>Assigned by <see cref="ItemRegistry.Register"/>.</summary>
    public ItemId Id { get; internal set; }

    /// <summary>
    /// The shape of the block this puts down, for a slot that wants to draw it as a block.
    /// </summary>
    /// <remarks>
    /// Filled in by <see cref="ItemRegistry.Seal"/> rather than looked up per frame. An inventory
    /// draws every visible slot every frame and the answer never changes, so the one place that
    /// already walks items against blocks is the place to answer it.
    /// </remarks>
    public BlockModel? IconModel { get; internal set; }

    /// <summary>
    /// The colour this gives off, 0..1 per channel, for a slot to glow behind it. Black for the
    /// vast majority of things, which give off nothing.
    /// </summary>
    /// <remarks>
    /// ⛳ From the user's own reference sheet for the inventory: glowstone, a beacon, a redstone lamp
    /// and a lantern all carry a bloom in their square, and it is the thing that tells a light apart
    /// from a rock at the size of a fingernail. Filled in by <see cref="ItemRegistry.Seal"/> off the
    /// block's own <c>LightEmission</c> rather than from a second list — a light that stops emitting
    /// stops glowing in the pocket too, which is the behaviour you want and comes free.
    /// </remarks>
    public System.Numerics.Vector3 Glow { get; internal set; }

    public bool IsTool => Tool != ToolClass.None;

    public bool IsFuel => BurnSeconds > 0f;

    /// <summary>The block this puts down when nothing about the placement varies.</summary>
    public BlockId PlainBlock => Places is { Variants.Length: > 0 } p ? p.Variants[0] : BlockId.Air;
}
