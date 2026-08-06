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

    /// <summary>
    /// Replaces one layer, mip chain and all.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>The whole chain, not just level zero, and the reason is what makes this method
    /// worth its length.</b> <c>GenerateMipmap</c> on the array would rebuild every level of every
    /// one of a hundred-odd layers to change one of them, every time a frame of water ticks — at a
    /// pack's resolution that is tens of megabytes of work several times a second. Uploading only
    /// level zero is the other obvious answer and is quietly wrong: the lower levels would keep
    /// frame zero forever, so a lake would move up close and be frozen at a distance, with a visible
    /// line across it where the mip level changes.</para>
    /// <para>So the chain is built here, on the processor, by halving. One 512-pixel tile's whole
    /// chain is about a third of a megabyte of arithmetic and it is done for one layer rather than
    /// for all of them.</para>
    /// </remarks>
    public unsafe void WriteLayer(int layer, byte[] tile)
    {
        if (layer < 0 || layer >= LayerCount) return;

        var level = 0;
        var size = Size;
        var pixels = tile;

        while (size >= 1)
        {
            fixed (byte* p = pixels)
            {
                _gl.TexSubImage3D(
                    TextureTarget.Texture2DArray, level,
                    0, 0, layer,
                    (uint)size, (uint)size, 1,
                    PixelFormat.Rgba, PixelType.UnsignedByte, p);
            }

            if (size == 1) break;

            pixels = Halve(pixels, size);
            size /= 2;
            level++;
        }
    }

    /// <summary>
    /// One mip level down: each output texel the average of the four under it.
    /// </summary>
    /// <remarks>
    /// ⚠ Averaged in the alpha's own weight, so a cut-out's transparent margin does not drag the
    /// colour of its edge toward whatever happens to be stored in pixels nobody can see. A plain
    /// average over rgba is how alpha-cut foliage grows a dark fringe at distance.
    /// </remarks>
    private static byte[] Halve(byte[] tile, int size)
    {
        var half = size / 2;
        var next = new byte[half * half * 4];

        for (var y = 0; y < half; y++)
        for (var x = 0; x < half; x++)
        {
            int r = 0, g = 0, b = 0, a = 0, weight = 0;

            for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++)
            {
                var src = ((y * 2 + dy) * size + x * 2 + dx) * 4;
                var alpha = tile[src + 3];

                r += tile[src] * alpha;
                g += tile[src + 1] * alpha;
                b += tile[src + 2] * alpha;
                a += alpha;
                weight += alpha;
            }

            var dst = (y * half + x) * 4;
            if (weight > 0)
            {
                next[dst] = (byte)(r / weight);
                next[dst + 1] = (byte)(g / weight);
                next[dst + 2] = (byte)(b / weight);
            }

            next[dst + 3] = (byte)(a / 4);
        }

        return next;
    }

    /// <summary>
    /// Reads one layer back off the card.
    /// </summary>
    /// <remarks>
    /// ⛳ For the check, and only for the check. "The animator uploaded something" is a claim about
    /// this program; "the layer on the card is different from what it was a moment ago" is a claim
    /// about the picture, and it is the only one that catches an upload that goes to the wrong level,
    /// the wrong layer or nowhere at all. ⚠ <c>GetTexImage</c> on an array reads every layer of the
    /// level, so this costs the whole level and hands back the slice that was asked for — fine for a
    /// check run twice, not something to do in a frame.
    /// </remarks>
    public unsafe byte[] ReadLayer(int layer)
    {
        var stride = Size * Size * 4;
        var all = new byte[stride * LayerCount];

        _gl.BindTexture(TextureTarget.Texture2DArray, _handle);
        fixed (byte* p = all)
            _gl.GetTexImage(TextureTarget.Texture2DArray, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);

        var slice = new byte[stride];
        Array.Copy(all, layer * stride, slice, 0, stride);
        return slice;
    }

    public void Dispose() => _gl.DeleteTexture(_handle);
}
