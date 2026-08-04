namespace Driftwood.Core.Blocks;

/// <summary>
/// A block's identity inside a <see cref="BlockRegistry"/>. Stored in chunks as a raw
/// <see cref="ushort"/>, so 65535 distinct blocks is the ceiling — comfortably past the
/// "hundreds of ores and woods" target even after every variant is counted.
/// </summary>
/// <remarks>
/// Id 0 is always <see cref="Air"/>. That is relied on by chunk storage: a freshly
/// allocated <c>ushort[]</c> is already a chunk full of air, so empty chunks cost nothing
/// to initialise.
/// </remarks>
public readonly struct BlockId : IEquatable<BlockId>
{
    public readonly ushort Value;

    public BlockId(ushort value) => Value = value;

    public static BlockId Air => new(0);

    public bool IsAir => Value == 0;

    public bool Equals(BlockId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is BlockId other && Equals(other);
    public override int GetHashCode() => Value;
    public override string ToString() => $"block:{Value}";

    public static bool operator ==(BlockId a, BlockId b) => a.Value == b.Value;
    public static bool operator !=(BlockId a, BlockId b) => a.Value != b.Value;

    public static implicit operator ushort(BlockId id) => id.Value;
}
