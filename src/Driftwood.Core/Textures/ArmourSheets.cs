using Driftwood.Core.Items;

namespace Driftwood.Core.Textures;

/// <summary>One material's worn armour net, at whatever resolution it arrived at.</summary>
/// <param name="FromPack">True when a texture pack supplied it rather than <see cref="ArmourArt"/>.</param>
public readonly record struct ArmourSheet(byte[] Pixels, int Width, int Height, bool FromPack);

/// <summary>
/// The sheets a suit of armour is drawn with — the pack's if it has them, ours if it does not.
/// </summary>
/// <remarks>
/// <para>⛳ <b>These are a different file from the item icons and were being missed entirely.</b> A
/// pack paints armour twice: <c>textures/item/iron_helmet.png</c> is the picture in a slot, and
/// <c>textures/models/armor/iron_layer_1.png</c> is the NET that goes on the body. We read the first
/// and generated the second, so a player in a fully skinned world wore our painted plate.</para>
/// <para>⛔ <b>Three spellings, all measured off the packs on this machine rather than remembered.</b>
/// Modern Java is <c>models/armor/&lt;m&gt;_layer_&lt;n&gt;</c>; Bedrock drops the word and writes
/// <c>models/armor/&lt;m&gt;_&lt;n&gt;</c>; the 2012 layout puts the whole folder at the root as
/// <c>armor/&lt;m&gt;_&lt;n&gt;</c>. Every candidate is tried for every material, because a pack that
/// is half one layout and half another is a real thing and resolving per file costs nothing.</para>
/// <para>⚠ <b>Leather is spelled <c>cloth</c> on the two older layouts</b>, which is the format's
/// own name for it and the one thing here that cannot be derived from our material table.</para>
/// </remarks>
public static class ArmourSheets
{
    /// <summary>Layer one is the helmet, chest and boots; layer two is the leggings.</summary>
    public const int Layers = 2;

    /// <summary>
    /// Every candidate path for one material's layer, in the order they are tried.
    /// </summary>
    /// <remarks>
    /// ⚠ The layer number is 1-based in every layout, which is why it is <c>+ 1</c> here and 0-based
    /// everywhere else in this project. Getting that wrong swaps a breastplate for a pair of
    /// leggings, which reads as a pack being broken rather than as an off-by-one.
    /// </remarks>
    public static IEnumerable<string> Candidates(Armour.Material material, int layer)
    {
        var n = layer + 1;

        foreach (var name in Names(material))
        {
            yield return $"textures/models/armor/{name}_layer_{n}.png";
            yield return $"textures/models/armor/{name}_{n}.png";
            yield return $"armor/{name}_{n}.png";
        }
    }

    /// <summary>What the layouts call this material, ours first.</summary>
    private static IEnumerable<string> Names(Armour.Material material)
    {
        yield return material.Pack;

        // ⛳ The older layouts call leather "cloth". Nothing else in the set is renamed, so this is
        // one row rather than a second column on the material table.
        if (material.Pack == "leather") yield return "cloth";
    }

    /// <summary>
    /// Loads every material's two sheets, taking a pack's where it has them.
    /// </summary>
    /// <param name="pack">An open pack, or null to paint all of them.</param>
    /// <remarks>
    /// ⚠ <b>Takes an already-open pack rather than a path.</b> Six materials times two layers is
    /// twelve lookups, and opening a quarter-gigabyte zip twelve times to answer them is the kind of
    /// thing that turns a load into a wait.
    /// </remarks>
    public static ArmourSheet[] Load(TexturePack? pack)
    {
        var sheets = new ArmourSheet[Armour.Materials.Length * Layers];

        for (var m = 0; m < Armour.Materials.Length; m++)
        {
            var material = Armour.Materials[m];

            for (var layer = 0; layer < Layers; layer++)
            {
                if (pack is null) break;

                foreach (var path in Candidates(material, layer))
                {
                    if (pack.TryLoadSheet(path, out _) is not { } image) continue;

                    sheets[m * Layers + layer] =
                        new ArmourSheet(image.Pixels, image.Width, image.Height, true);
                    break;
                }
            }

            // ⛳ BORROW AND RECOLOUR, exactly as the item icons do. A pack older than copper gear has
            // ten of the twelve nets and leaves one metal in our art — one odd tier out of six, on
            // the body, which reads worse than none of them matching. Iron lends the shape and the
            // metal's own colour is multiplied through it.
            if (pack is not null && material.Borrow.Length > 0)
            {
                var lender = Armour.Materials.FirstOrDefault(x => x.Name == material.Borrow);

                for (var layer = 0; layer < Layers; layer++)
                {
                    if (sheets[m * Layers + layer].Pixels is not null) continue;
                    if (lender.Name is null) break;

                    foreach (var path in Candidates(lender, layer))
                    {
                        if (pack.TryLoadSheet(path, out _) is not { } image) continue;

                        sheets[m * Layers + layer] = new ArmourSheet(
                            BlockTextureSet.RecolourFor(image.Pixels, material.Tint),
                            image.Width, image.Height, true);
                        break;
                    }
                }
            }

            // ⚠ Painted ONCE per material and not once per missing layer: ArmourArt draws both of a
            // metal's sheets in a single pass, so asking it twice would draw each of them twice for
            // a material the pack has neither of.
            if (sheets[m * Layers].Pixels is not null && sheets[m * Layers + 1].Pixels is not null)
                continue;

            var ours = ArmourArt.Build(material);

            for (var layer = 0; layer < Layers; layer++)
                if (sheets[m * Layers + layer].Pixels is null)
                    sheets[m * Layers + layer] =
                        new ArmourSheet(ours[layer], ArmourArt.Width, ArmourArt.Height, false);
        }

        return sheets;
    }

    /// <summary>How many of them a pack supplied, for the report.</summary>
    public static int FromPack(ArmourSheet[] sheets)
    {
        var count = 0;
        foreach (var sheet in sheets) if (sheet.FromPack) count++;
        return count;
    }

    /// <summary>
    /// Fits a net into a square of one size, top-left, so every sheet can share one array.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Top-left and not centred.</b> A net is addressed by coordinates measured from its own
    /// origin — <c>ArmourModel.FaceRect</c> answers in 64-wide texels — so a sheet nudged to the
    /// middle of a square puts every patch somewhere else. <c>PaintedArt.Fit</c> centres on purpose
    /// and is the wrong tool here for exactly that reason.
    /// <para>⚠ The v coordinate then runs to <c>Height/Width</c> of the square rather than to 1,
    /// which is the caller's business and is why the net's proportions are preserved rather than
    /// stretched to fill.</para>
    /// </remarks>
    public static byte[] Square(in ArmourSheet sheet, int size)
    {
        var square = new byte[size * size * 4];

        var drawnW = size;
        var drawnH = (int)MathF.Round(size * (sheet.Height / (float)sheet.Width));

        for (var y = 0; y < drawnH && y < size; y++)
        for (var x = 0; x < drawnW; x++)
        {
            var sx = Math.Clamp(x * sheet.Width / drawnW, 0, sheet.Width - 1);
            var sy = Math.Clamp(y * sheet.Height / drawnH, 0, sheet.Height - 1);

            var from = (sy * sheet.Width + sx) * 4;
            var to = (y * size + x) * 4;

            square[to] = sheet.Pixels[from];
            square[to + 1] = sheet.Pixels[from + 1];
            square[to + 2] = sheet.Pixels[from + 2];
            square[to + 3] = sheet.Pixels[from + 3];
        }

        return square;
    }
}
