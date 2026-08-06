using Driftwood.Core.Blocks;

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

    /// <summary>Nothing yet. It is the reason mobs have something to be fought with.</summary>
    Sword,
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
    public required ushort IconLayer { get; init; }

    /// <summary>How many fit in a slot. One for anything that carries wear.</summary>
    public int MaxStack { get; init; } = ItemStack.MaxCount;

    /// <summary>What this puts down, and how it decides which way up. Null when it places nothing.</summary>
    public Placeable? Places { get; init; }

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

    /// <summary>Uses before it breaks. Zero means it never wears out.</summary>
    public int Durability { get; init; }

    /// <summary>Seconds of furnace burn this is worth. Zero means it is not fuel.</summary>
    public float BurnSeconds { get; init; }

    /// <summary>
    /// Where on the body this is worn, or null for anything that is not armour.
    /// </summary>
    /// <remarks>
    /// Nothing sets this yet — there is no armour — and the four worn slots on the player screen
    /// refuse everything as a result. That is deliberate: the slots are the prerequisite the worn
    /// armour work is blocked on, and a field on the item is where the answer belongs.
    /// </remarks>
    public EquipSlot? Wears { get; init; }

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
