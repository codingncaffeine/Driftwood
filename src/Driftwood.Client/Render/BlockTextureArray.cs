using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>
/// Every block texture, uploaded as one GL texture array — one layer per texture.
/// </summary>
/// <remarks>
/// <para>An array rather than an atlas, and the reason is mipmaps. In an atlas, neighbouring tiles
/// bleed into each other at every mip level, so a distant wall picks up the colour of whatever
/// happened to be packed beside it; the usual fixes are padding and a hand-clamped mip chain, both
/// of which have to be got right forever. Each layer of an array is its own image with its own
/// wrapping, so tiling is free and bleeding cannot happen.</para>
/// <para>An array also sidesteps the trap that the deepest usable mip level of an atlas is set by
/// the <em>smallest</em> texture in it — one stray low-resolution tile in an otherwise high-
/// resolution pack caps the whole chain. Here every layer is the same size because everything is
/// scaled to it on the way in.</para>
/// <para>Nearest filtering when magnifying, mipmapped when minifying. Block art is pixel art and
/// smoothing it up close is the one thing that would make an imported pack look wrong; smoothing
/// it in the distance is the one thing that stops it shimmering.</para>
/// </remarks>
public sealed class BlockTextureArray : IDisposable
{
    private readonly GL _gl;
    private readonly uint _handle;

    public int Size { get; }
    public int LayerCount { get; }

    public unsafe BlockTextureArray(GL gl, byte[][] tiles, int size)
    {
        _gl = gl;
        Size = size;
        LayerCount = tiles.Length;

        _handle = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2DArray, _handle);

        _gl.TexImage3D(
            TextureTarget.Texture2DArray, 0, InternalFormat.Rgba8,
            (uint)size, (uint)size, (uint)tiles.Length, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, null);

        for (var layer = 0; layer < tiles.Length; layer++)
        {
            fixed (byte* p = tiles[layer])
            {
                _gl.TexSubImage3D(
                    TextureTarget.Texture2DArray, 0,
                    0, 0, layer,
                    (uint)size, (uint)size, 1,
                    PixelFormat.Rgba, PixelType.UnsignedByte, p);
            }
        }

        _gl.GenerateMipmap(TextureTarget.Texture2DArray);

        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.NearestMipmapLinear);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.Repeat);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.Repeat);

        _gl.BindTexture(TextureTarget.Texture2DArray, 0);
    }

    public void Bind(TextureUnit unit = TextureUnit.Texture0)
    {
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.Texture2DArray, _handle);
    }

    public void Dispose() => _gl.DeleteTexture(_handle);
}
