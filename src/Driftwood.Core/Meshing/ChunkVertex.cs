using System.Runtime.InteropServices;

namespace Driftwood.Core.Meshing;

/// <summary>
/// One meshed corner: 16 bytes, laid out to go straight to the GPU with no repacking.
/// </summary>
/// <remarks>
/// Position is chunk-local (0..32), so it stays small and the chunk's world origin rides in a
/// uniform instead of being baked into every vertex. Face index, ambient occlusion and texture
/// layer share one word — the shader unpacks them, which costs a few ALU cycles and saves a third
/// of the bandwidth. Greedy meshing at P1 changes how many of these get emitted, not their shape.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct ChunkVertex
{
    public const int SizeInBytes = 16;

    public readonly float X;
    public readonly float Y;
    public readonly float Z;

    /// <summary>bits 0-2 face, bits 3-4 ambient occlusion (0 darkest, 3 unoccluded), bits 8-23 texture layer.</summary>
    public readonly uint Packed;

    public ChunkVertex(float x, float y, float z, int face, int ao, ushort layer)
    {
        X = x;
        Y = y;
        Z = z;
        Packed = (uint)(face & 0x7) | ((uint)(ao & 0x3) << 3) | ((uint)layer << 8);
    }
}
