using System.Text;
using System.Text.Json;

namespace Driftwood.Core.Textures;

/// <summary>
/// The printable-ASCII part of a resource pack's default font, layered over Driftwood's own font.
/// </summary>
/// <remarks>
/// <para>Driftwood's text renderer is deliberately small: one sixteen-pixel texture-array layer per
/// printable ASCII character. A resource pack can still dress that complete interface without the
/// renderer pretending to be a general Unicode shaping engine. Legacy <c>ascii.png</c>, modern
/// bitmap providers, referenced font definitions and space advances all collapse into the same 95
/// safe layers; every missing or unsupported glyph keeps Driftwood's own drawing.</para>
/// <para>Provider files are data supplied by a pack. Counts, dimensions, recursion and JSON are all
/// bounded, and a malformed provider is an omission with a reason rather than a broken interface.</para>
/// </remarks>
public static class FontTextureSet
{
    private const int MaximumDefinitions = 32;
    private const int MaximumProviders = 512;
    private const int MaximumGrid = 256;

    public sealed record Result(
        byte[][] Tiles,
        int[] Advances,
        int BitmapGlyphs,
        int SpaceAdvances,
        int Providers,
        IReadOnlyList<string> Omissions,
        IReadOnlyList<string> Faults)
    {
        public string Summary => BitmapGlyphs == 0 && SpaceAdvances == 0
            ? "Driftwood ASCII font"
            : $"{BitmapGlyphs} of {TileGen.GlyphCount} ASCII glyphs and "
              + $"{SpaceAdvances} advances from the pack";
    }

    public static Result Load(TexturePack? pack)
    {
        var tiles = TileGen.Font();
        var advances = TileGen.FontAdvance();
        if (pack is null)
            return new Result(tiles, advances, 0, 0, 0, [], []);

        var loaded = new bool[TileGen.GlyphCount];
        var spaced = new bool[TileGen.GlyphCount];
        var omissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var faults = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var providers = 0;
        var definitions = 0;

        ReadDefinition("minecraft:default", 0);

        // Before font JSON became the format, ascii.png itself was the contract. Many current packs
        // still replace only that vanilla sheet, so it remains a standard fallback after any modern
        // providers have had first say. font.png is a measured legacy alias used by older packs.
        foreach (var legacy in new[] { "textures/font/ascii.png", "textures/font/font.png" })
        {
            var raw = pack.TryReadAssetBytes(legacy, out var from);
            if (raw is null) continue;

            if (!Png.TryDecode(raw, out var image, out var error))
            {
                faults.Add($"{from}: {error}");
                continue;
            }

            if (image.Width % 16 != 0 || image.Height % 16 != 0)
            {
                faults.Add($"{from}: the legacy font sheet is not a 16 by 16 grid");
                continue;
            }

            var cellWidth = image.Width / 16;
            var cellHeight = image.Height / 16;
            if (cellWidth <= 0 || cellHeight <= 0) continue;

            for (var glyph = 0; glyph < TileGen.GlyphCount; glyph++)
            {
                if (loaded[glyph]) continue;
                var code = TileGen.FirstGlyph + glyph;
                ApplyCell(image, code % 16, code / 16, cellWidth, cellHeight, glyph);
            }

            break;
        }

        return new Result(
            tiles,
            advances,
            loaded.Count(value => value),
            spaced.Count(value => value),
            providers,
            omissions.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            faults);

        void ReadDefinition(string id, int depth)
        {
            if (depth >= MaximumDefinitions)
            {
                faults.Add($"font reference '{id}' is nested more than {MaximumDefinitions} levels");
                return;
            }

            var key = id.Contains(':', StringComparison.Ordinal) ? id : "minecraft:" + id;
            if (!visited.Add(key)) return;
            definitions++;
            if (definitions > MaximumDefinitions)
            {
                faults.Add($"the font references more than {MaximumDefinitions} definitions");
                return;
            }

            var resource = key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? key
                : key + ".json";
            var raw = pack.TryReadResourceBytes(resource, "font", out var from);
            if (raw is null) return;

            JsonDocument json;
            try
            {
                json = JsonDocument.Parse(raw, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 32,
                });
            }
            catch (JsonException error)
            {
                faults.Add($"{from}: {error.Message}");
                return;
            }

            using (json)
            {
                if (!json.RootElement.TryGetProperty("providers", out var list)
                    || list.ValueKind != JsonValueKind.Array)
                {
                    faults.Add($"{from}: no providers array");
                    return;
                }

                foreach (var provider in list.EnumerateArray())
                {
                    providers++;
                    if (providers > MaximumProviders)
                    {
                        faults.Add($"the font declares more than {MaximumProviders} providers");
                        return;
                    }

                    if (provider.ValueKind != JsonValueKind.Object
                        || !provider.TryGetProperty("type", out var typeValue)
                        || typeValue.ValueKind != JsonValueKind.String)
                    {
                        faults.Add($"{from}: a font provider has no type");
                        continue;
                    }

                    var type = typeValue.GetString() ?? "";
                    var colon = type.LastIndexOf(':');
                    if (colon >= 0) type = type[(colon + 1)..];

                    switch (type)
                    {
                        case "bitmap": ReadBitmap(provider, from); break;
                        case "space": ReadSpaces(provider, from); break;
                        case "reference":
                            if (provider.TryGetProperty("id", out var reference)
                                && reference.ValueKind == JsonValueKind.String
                                && reference.GetString() is { Length: > 0 } next)
                                ReadDefinition(next, depth + 1);
                            else
                                faults.Add($"{from}: a reference provider has no id");
                            break;
                        default:
                            omissions.Add(type.Length == 0 ? "unknown font provider" : type);
                            break;
                    }
                }
            }
        }

        void ReadBitmap(JsonElement provider, string definition)
        {
            if (!provider.TryGetProperty("file", out var fileValue)
                || fileValue.ValueKind != JsonValueKind.String
                || fileValue.GetString() is not { Length: > 0 } file
                || !provider.TryGetProperty("chars", out var rowsValue)
                || rowsValue.ValueKind != JsonValueKind.Array)
            {
                faults.Add($"{definition}: a bitmap provider has no file or character grid");
                return;
            }

            var rows = new List<Rune[]>();
            var columns = 0;
            foreach (var rowValue in rowsValue.EnumerateArray())
            {
                if (rowValue.ValueKind != JsonValueKind.String)
                {
                    faults.Add($"{definition}: a bitmap character row is not text");
                    return;
                }

                var row = (rowValue.GetString() ?? "").EnumerateRunes().ToArray();
                columns = Math.Max(columns, row.Length);
                rows.Add(row);
            }

            if (rows.Count is 0 or > MaximumGrid || columns is 0 or > MaximumGrid)
            {
                faults.Add($"{definition}: bitmap grid is empty or larger than {MaximumGrid} by {MaximumGrid}");
                return;
            }

            var raw = pack.TryReadResourceBytes(file, "textures", out var from);
            if (raw is null)
            {
                faults.Add($"{definition}: bitmap '{file}' is missing");
                return;
            }
            if (!Png.TryDecode(raw, out var image, out var error))
            {
                faults.Add($"{from}: {error}");
                return;
            }
            if (image.Width % columns != 0 || image.Height % rows.Count != 0)
            {
                faults.Add($"{from}: {image.Width}x{image.Height} does not divide into its "
                    + $"{columns} by {rows.Count} character grid");
                return;
            }

            var cellWidth = image.Width / columns;
            var cellHeight = image.Height / rows.Count;
            for (var y = 0; y < rows.Count; y++)
            for (var x = 0; x < rows[y].Length; x++)
            {
                var value = rows[y][x].Value;
                if (value < TileGen.FirstGlyph || value >= TileGen.FirstGlyph + TileGen.GlyphCount)
                    continue;

                var glyph = value - TileGen.FirstGlyph;
                if (!loaded[glyph]) ApplyCell(image, x, y, cellWidth, cellHeight, glyph);
            }
        }

        void ReadSpaces(JsonElement provider, string definition)
        {
            if (!provider.TryGetProperty("advances", out var values)
                || values.ValueKind != JsonValueKind.Object)
            {
                faults.Add($"{definition}: a space provider has no advances object");
                return;
            }

            foreach (var property in values.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Number
                    || !property.Value.TryGetDouble(out var width)
                    || !double.IsFinite(width))
                    continue;

                foreach (var rune in property.Name.EnumerateRunes())
                {
                    if (rune.Value < TileGen.FirstGlyph
                        || rune.Value >= TileGen.FirstGlyph + TileGen.GlyphCount)
                        continue;

                    var glyph = rune.Value - TileGen.FirstGlyph;
                    if (spaced[glyph]) continue;
                    advances[glyph] = Math.Clamp((int)Math.Round(width * 2d), 0, 64);
                    spaced[glyph] = true;
                }
            }
        }

        void ApplyCell(Image image, int column, int row, int cellWidth, int cellHeight, int glyph)
        {
            var tile = new byte[TileGen.Size * TileGen.Size * 4];
            var left = column * cellWidth;
            var top = row * cellHeight;
            var rightmost = -1;

            for (var sy = 0; sy < cellHeight; sy++)
            for (var sx = 0; sx < cellWidth; sx++)
            {
                var source = ((top + sy) * image.Width + left + sx) * 4;
                if (image.Pixels[source + 3] > 0) rightmost = Math.Max(rightmost, sx);
            }

            for (var y = 0; y < TileGen.Size; y++)
            for (var x = 0; x < TileGen.Size; x++)
            {
                var sx = left + Math.Min(x * cellWidth / TileGen.Size, cellWidth - 1);
                var sy = top + Math.Min(y * cellHeight / TileGen.Size, cellHeight - 1);
                var source = (sy * image.Width + sx) * 4;
                var target = (y * TileGen.Size + x) * 4;
                image.Pixels.AsSpan(source, 4).CopyTo(tile.AsSpan(target, 4));
            }

            tiles[glyph] = tile;
            if (!spaced[glyph])
            {
                var ink = rightmost < 0
                    ? glyph == TileGen.GlyphOf(' ') ? 6 : 1
                    : (int)Math.Ceiling((rightmost + 1) * TileGen.Size / (double)cellWidth) + 2;
                advances[glyph] = Math.Clamp(ink, 1, TileGen.Size + 2);
            }
            loaded[glyph] = true;
        }
    }
}
