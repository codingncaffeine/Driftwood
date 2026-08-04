using System.Runtime.InteropServices;

namespace Driftwood.Core.Meshing;

/// <summary>
/// One meshed corner: 12 bytes, laid out to go straight to the GPU with no repacking.
/// </summary>
/// <remarks>
/// <para>Everything a corner needs fits in bits. Position is chunk-local and quantised to a
/// sixty-fourth of a block, which is a quarter of a texel on a sixteen-pixel tile — fine enough for
/// a plant's crossed planes to land where the model says and coarse enough that three axes fit in
/// thirty-six bits. The chunk's world origin rides in a uniform instead of being repeated across
/// every vertex.</para>
/// <para>Cube faces and model faces share one format rather than taking two buffers and two
/// shaders. The cube path wastes the fractional bits and the texture coordinates; the model path
/// wastes nothing. Four extra bytes a vertex is around seventeen more megabytes across a
/// three-hundred-block view, which is worth not having a second geometry path to keep in step with
/// the first.</para>
/// <para>Baked light rides in the low half of the third word. The texture layer is twelve bits
/// rather than sixteen: four thousand distinct block textures is far past what a pack ships, and
/// the four bits it frees are what let coordinates and the coplanar pass number fit.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct ChunkVertex
{
    public const int SizeInBytes = 12;

    /// <summary>Distinct tint colours one chunk may use. Six bits' worth.</summary>
    /// <remarks>
    /// Generous rather than tight. Climate varies over hundreds of blocks, so a 32-block chunk
    /// normally needs one or two entries; the ceiling only matters where several biomes meet, and
    /// running out there costs a slightly wrong colour rather than a broken chunk.
    /// </remarks>
    public const int MaxTints = 64;

    /// <summary>Position steps per block.</summary>
    public const int PositionScale = 64;

    /// <summary>
    /// Steps subtracted on the way in, so a model element may hang a block below the chunk.
    /// </summary>
    /// <remarks>
    /// The format lets an element reach outside its own block — a fence arm, a flame, a hanging
    /// vine. Twelve bits at a sixty-fourth of a block covers sixty-four blocks; spending one of
    /// them below zero costs nothing and means a model reaching backwards does not have to be
    /// clamped into the chunk it belongs to.
    /// </remarks>
    public const int PositionBias = PositionScale;

    /// <summary>Highest texture array layer the twelve-bit field can name.</summary>
    public const int MaxLayer = 4095;

    /// <summary>Texture coordinate steps per tile.</summary>
    /// <remarks>
    /// Models are authored in sixteenths of a tile, so half of one step is finer than anything a
    /// model file can say. Merged cube faces do not use this at all — they derive coordinates from
    /// world position, which is the only way a quad spanning six blocks can tile.
    /// </remarks>
    public const int UvScale = 32;

    /// <summary>The face value meaning "light this flat, whichever way it points".</summary>
    /// <remarks>
    /// The model format's <c>shade: false</c>, which every crossed-plane plant sets. A tuft of
    /// grass has two planes at right angles; shading them by direction makes one bright and one
    /// dark, and the plant reads as two flat cards rather than as one plant.
    /// </remarks>
    public const int UnshadedFace = 6;

    /// <summary>bits 0-11 x, 12-23 y, 24-26 face, 27-28 ambient occlusion, 29-30 coplanar pass.</summary>
    public readonly uint Packed0;

    /// <summary>bits 0-11 z, 12-23 texture layer, 24-29 tint, 30 explicit-uv flag.</summary>
    public readonly uint Packed1;

    /// <summary>bits 0-15 baked light (sky, red, green, blue nibbles), 16-21 u, 22-27 v.</summary>
    public readonly uint Packed2;

    private ChunkVertex(
        int qx, int qy, int qz, int face, int ao, int pass,
        int layer, ushort light, int tint, int u, int v, bool explicitUv)
    {
        Packed0 = (uint)(qx & 0xFFF)
                | ((uint)(qy & 0xFFF) << 12)
                | ((uint)(face & 0x7) << 24)
                | ((uint)(ao & 0x3) << 27)
                | ((uint)(pass & 0x3) << 29);

        Packed1 = (uint)(qz & 0xFFF)
                | ((uint)(layer & 0xFFF) << 12)
                | ((uint)(tint & 0x3F) << 24)
                | ((explicitUv ? 1u : 0u) << 30);

        Packed2 = light
                | ((uint)(u & 0x3F) << 16)
                | ((uint)(v & 0x3F) << 22);
    }

    /// <summary>A corner of a merged cube face: whole-block coordinates, coordinates derived in the shader.</summary>
    public static ChunkVertex Cube(
        int x, int y, int z, int face, int ao, int pass, int layer, ushort light, int tint) =>
        new(
            x * PositionScale + PositionBias,
            y * PositionScale + PositionBias,
            z * PositionScale + PositionBias,
            face, ao, pass, layer, light, tint, 0, 0, false);

    /// <summary>A corner of a model quad: quantised coordinates, texture coordinates carried along.</summary>
    public static ChunkVertex Model(
        float x, float y, float z, int face, int ao, int layer, ushort light, int tint, float u, float v) =>
        new(
            Quantise(x), Quantise(y), Quantise(z),
            face, ao, 0, layer, light, tint,
            QuantiseUv(u), QuantiseUv(v), true);

    /// <summary>Rounds a block-space coordinate onto the packed grid.</summary>
    public static int Quantise(float blocks) =>
        Math.Clamp((int)MathF.Round(blocks * PositionScale) + PositionBias, 0, 0xFFF);

    /// <summary>Rounds a texture coordinate onto the packed grid.</summary>
    public static int QuantiseUv(float tiles) =>
        Math.Clamp((int)MathF.Round(tiles * UvScale), 0, 0x3F);
}
