using System.Reflection;

namespace Driftwood.Core.Textures;

/// <summary>
/// The start menu's plank buttons, the user's own art (2026-11-04), one picture per word.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Cut from the user's two menu mock-ups</b> (<c>menu buttons.jpg</c>, <c>menu with back
/// and join.jpg</c>): each button's whole iron frame, scaled to 512 wide. The word is carved INTO
/// the art, so a row with a picture draws no text of its own, and a word with no picture yet stays
/// an ordinary row — the user offered more when they are wanted.</para>
/// <para>Each picture sits at the top of a square layer, the rest transparent, because the HUD's
/// textures are square arrays; <see cref="V"/> is how far down the layer the picture reaches.</para>
/// </remarks>
public static class MenuButtonArt
{
    public const int Width = 512;
    public const int Height = 109;
    public const int Layer = 512;

    /// <summary>Width over height, which the menu keeps whatever size it draws a button.</summary>
    public const float Aspect = (float)Width / Height;

    /// <summary>How far down its layer a picture reaches, inset half a texel so nothing below bleeds in.</summary>
    public const float V = (Height - 0.5f) / Layer;

    public static readonly string[] Names = ["play", "create_world", "multiplayer", "options", "quit", "back", "join"];

    public const int Play = 0, CreateWorld = 1, Multiplayer = 2, Options = 3, Quit = 4, Back = 5, Join = 6;

    public static byte[][] Layers()
    {
        var layers = new byte[Names.Length][];
        for (var i = 0; i < Names.Length; i++)
        {
            var name = $"Driftwood.Core.menu-button-{Names[i]}.png";
            using var stream = typeof(MenuButtonArt).GetTypeInfo().Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"the button {name} is not embedded");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            if (!Png.TryDecode(memory.ToArray(), out var image, out var error))
                throw new InvalidOperationException($"the button {name} did not decode: {error}");

            var layer = new byte[Layer * Layer * 4];
            for (var y = 0; y < Math.Min(Layer, image.Height); y++)
                Array.Copy(image.Pixels, y * image.Width * 4, layer, y * Layer * 4, Math.Min(Layer, image.Width) * 4);
            layers[i] = layer;
        }

        return layers;
    }
}
