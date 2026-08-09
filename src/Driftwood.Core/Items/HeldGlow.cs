using Driftwood.Core.Blocks;

namespace Driftwood.Core.Items;

/// <summary>
/// The light a carried thing throws — the emission of the block it would place, or nothing.
/// </summary>
/// <remarks>
/// <para>⛳ One rule and no table: if the item in a hand places a block that emits, the hand emits
/// the same colours. A lantern carried is a lantern's light; a torch a torch's; an UNLIT campfire
/// nothing at all, because the block it places is the unlit one — the rule answers that case
/// correctly without ever being told about it.</para>
/// <para>In Core because it is a lookup the audit can hold still; the falloff and the drawing are
/// the client's. Facing variants share one emission, so the first variant answers for all.</para>
/// </remarks>
public static class HeldGlow
{
    /// <summary>Packed block-light channels this item sheds from a hand. Zero for most things.</summary>
    public static ushort Of(ItemType? held, BlockRegistry blocks) =>
        held is null || held.PlainBlock == BlockId.Air ? (ushort)0 : blocks[held.PlainBlock].LightEmission;
}
