using System.Reflection;

namespace Driftwood.Core.Textures;

/// <summary>
/// The start menu's plank buttons and fields, the user's own art (2026-11-04), one picture each.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Cut from the user's menu mock-ups</b> (<c>menu buttons.jpg</c>, <c>menu with back and
/// join.jpg</c>, <c>more buttons.jpg</c>, <c>address and name.jpg</c>, <c>multiplayer.jpg</c>; the
/// sources stay untracked): each piece's whole iron frame, scaled to 512 wide. A button's word is
/// carved INTO the art, so a row with a picture draws no label of its own; a field's label is
/// carved too, and its recessed box (<see cref="FieldBox"/>) was cleared of the placeholder words
/// with blank planks from the same sheets, so what is typed is drawn there.</para>
/// <para>Each picture sits at the top of a square layer, the rest transparent, because the HUD's
/// textures are square arrays.</para>
/// </remarks>
public static class MenuButtonArt
{
    public const int Width = 512;
    public const int Layer = 512;

    public static readonly string[] Names =
    [
        "play", "create_world", "multiplayer", "options", "quit", "back", "join",
        "field_name", "field_address", "open_world", "cancel", "confirm_join", "panel",
    ];

    public const int Play = 0, CreateWorld = 1, Multiplayer = 2, Options = 3, Quit = 4, Back = 5, Join = 6;
    public const int FieldName = 7, FieldAddress = 8, OpenWorld = 9, Cancel = 10, ConfirmJoin = 11, Panel = 12;

    /// <summary>Each picture's height at 512 wide, as cut. Checked against the files when they load.</summary>
    private static readonly int[] Heights = [109, 109, 109, 109, 109, 109, 109, 109, 109, 109, 153, 122, 340];

    /// <summary>Width over height, which the menu keeps whatever size it draws a piece.</summary>
    public static float Aspect(int art) => (float)Width / Heights[art];

    /// <summary>How far down its layer a picture reaches, inset half a texel so nothing below bleeds in.</summary>
    public static float V(int art) => (Heights[art] - 0.5f) / Layer;

    /// <summary>
    /// Where a field's writing box is inside its picture, as fractions (left, top, right, bottom);
    /// null for a piece that is not a field.
    /// </summary>
    public static (float L, float T, float R, float B)? FieldBox(int art) => art switch
    {
        FieldName => (350f / 778f, 45f / 166f, 718f / 778f, 119f / 166f),
        FieldAddress => (316f / 778f, 45f / 166f, 718f / 778f, 119f / 166f),
        _ => null,
    };

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
            if (image.Width != Width || image.Height != Heights[i])
                throw new InvalidOperationException(
                    $"the button {name} is {image.Width}x{image.Height}, where {Width}x{Heights[i]} was expected");

            var layer = new byte[Layer * Layer * 4];
            for (var y = 0; y < Math.Min(Layer, image.Height); y++)
                Array.Copy(image.Pixels, y * image.Width * 4, layer, y * Layer * 4, Math.Min(Layer, image.Width) * 4);
            layers[i] = layer;
        }

        return layers;
    }
}
