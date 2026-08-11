using Driftwood.Core.Textures;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>A small owned RGBA texture used by sky and weather sprites.</summary>
public sealed class Texture2D : IDisposable
{
    private readonly GL _gl;
    private readonly uint _handle;

    public int Width { get; }
    public int Height { get; }

    public unsafe Texture2D(GL gl, Image image, bool nearest = false)
    {
        _gl = gl;
        Width = image.Width;
        Height = image.Height;
        _handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _handle);
        fixed (byte* pixels = image.Pixels)
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                (uint)image.Width, (uint)image.Height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            nearest ? (int)TextureMinFilter.Nearest : (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            nearest ? (int)TextureMagFilter.Nearest : (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
        gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Bind(TextureUnit unit)
    {
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.Texture2D, _handle);
    }

    public void Dispose() => _gl.DeleteTexture(_handle);
}
