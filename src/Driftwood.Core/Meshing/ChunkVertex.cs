using System.Runtime.InteropServices;

namespace Driftwood.Core.Meshing;

/// <summary>
/// One meshed corner: 8 bytes, laid out to go straight to the GPU with no repacking.
/// </summary>
/// <remarks>
/// <para>Everything a corner needs fits in bits. Position is chunk-local and integral — greedy
/// quads only ever land on block boundaries, so 0..32 per axis costs six bits rather than a
/// float's thirty-two, and the chunk's world origin rides in a uniform instead of being repeated
/// across every vertex.</para>
/// <para>Layer keeps a full sixteen bits in its own word rather than being squeezed alongside the
/// rest. Packing it down to nine would fit the whole vertex in four bytes, but it would also cap
/// the game at 512 distinct block textures, and "hundreds of ores and woods" is close enough to
/// that ceiling to make the trade a bad one.</para>
/// <para>Baked light rides in the top half of that second word — sixteen bits that were already
/// sitting empty, so lighting cost the vertex nothing.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct ChunkVertex
{
    public const int SizeInBytes = 8;

    /// <summary>bits 0-5 x, 6-11 y, 12-17 z, 18-20 face, 21-22 ambient occlusion.</summary>
    public readonly uint Packed0;

    /// <summary>bits 0-15 texture layer, 16-31 baked light (sky, red, green, blue nibbles).</summary>
    public readonly uint Packed1;

    public ChunkVertex(int x, int y, int z, int face, int ao, ushort layer, ushort light)
    {
        Packed0 = (uint)(x & 0x3F)
                | ((uint)(y & 0x3F) << 6)
                | ((uint)(z & 0x3F) << 12)
                | ((uint)(face & 0x7) << 18)
                | ((uint)(ao & 0x3) << 21);
        Packed1 = layer | ((uint)light << 16);
    }
}
