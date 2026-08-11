using System.Text.Json;

namespace Driftwood.Core.Textures;

/// <summary>
/// Normal and material companion maps aligned one-for-one with <see cref="BlockTextureSet.Layers"/>.
/// </summary>
/// <remarks>
/// <para>The material tile is packed as metalness, roughness, emissive and ambient occlusion in
/// RGBA. A missing map is deliberately useful rather than black: flat normal, a material-specific
/// roughness, known metals and known luminous blocks. Packs can then replace any subset without
/// making the other layers invalid.</para>
/// <para>Java suffix maps and Bedrock MER/texture-set maps are both read. The fixed runtime layout
/// means the chunk shader never branches on the dialect that supplied a tile.</para>
/// </remarks>
public static class BlockMaterialSet
{
    public sealed record Result(
        byte[][] Normals,
        byte[][] Materials,
        int Size,
        int NormalMaps,
        int MaterialMaps,
        int EmissiveMaps,
        IReadOnlyList<string> Sources)
    {
        public string Summary =>
            $"{Normals.Length} layers; {NormalMaps} normal/height, {MaterialMaps} roughness/MER, "
            + $"{EmissiveMaps} emissive maps from the pack";
    }

    private readonly record struct TextureSetPaths(string Normal, string Height, string Mer);

    /// <summary>Builds a complete material array at the same resolution as the colour array.</summary>
    public static Result Build(string? packPath, int size)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));

        var normals = new byte[BlockTextureSet.Layers.Length][];
        var materials = new byte[BlockTextureSet.Layers.Length][];
        var sources = new List<string>();
        var normalMaps = 0;
        var materialMaps = 0;
        var emissiveMaps = 0;

        using var pack = string.IsNullOrWhiteSpace(packPath) ? null : TexturePack.Open(packPath);
        for (var layer = 0; layer < BlockTextureSet.Layers.Length; layer++)
        {
            var row = BlockTextureSet.Layers[layer];
            normals[layer] = Solid(size, 128, 128, 255, 255);
            materials[layer] = Solid(size,
                IsMetal(row.Name) ? (byte)210 : (byte)0,
                Roughness(row.Name),
                IsLuminous(row.Name) ? (byte)210 : (byte)0,
                255);

            if (pack is null || row.PackPath.Length == 0) continue;

            var paths = new[] { row.PackPath, row.PackPathAlt }
                .Where(static path => path.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var set = ReadTextureSet(pack, paths);

            if (LoadFirst(pack, size, NormalCandidates(paths, set.Normal), out var normal, out var normalFrom))
            {
                normals[layer] = normal;
                normalMaps++;
                AddSource(sources, normalFrom);
            }
            else if (LoadFirst(pack, size, HeightCandidates(paths, set.Height), out var height, out var heightFrom))
            {
                normals[layer] = NormalFromHeight(height, size);
                normalMaps++;
                AddSource(sources, heightFrom);
            }

            // Bedrock's canonical companion is metalness/emissive/roughness in RGB.
            if (LoadFirst(pack, size, MerCandidates(paths, set.Mer), out var mer, out var merFrom))
            {
                CopyMer(mer, materials[layer]);
                materialMaps++;
                if (AnyChannel(mer, 1)) emissiveMaps++;
                AddSource(sources, merFrom);
            }
            else
            {
                var suppliedMaterial = false;
                if (LoadFirst(pack, size, RoughnessCandidates(paths), out var roughness, out var roughFrom))
                {
                    CopyLuminance(roughness, materials[layer], 1, invert: false);
                    suppliedMaterial = true;
                    AddSource(sources, roughFrom);
                }
                else if (LoadFirst(pack, size, SpecularCandidates(paths), out var specular, out var specFrom))
                {
                    // A brighter specular map is a smoother surface, hence inverse roughness.
                    CopyLuminance(specular, materials[layer], 1, invert: true);
                    suppliedMaterial = true;
                    AddSource(sources, specFrom);
                }

                if (LoadFirst(pack, size, MetalCandidates(paths), out var metal, out var metalFrom))
                {
                    CopyLuminance(metal, materials[layer], 0, invert: false);
                    suppliedMaterial = true;
                    AddSource(sources, metalFrom);
                }

                if (LoadFirst(pack, size, EmissiveCandidates(paths), out var emissive, out var emissiveFrom))
                {
                    CopyLuminance(emissive, materials[layer], 2, invert: false);
                    suppliedMaterial = true;
                    emissiveMaps++;
                    AddSource(sources, emissiveFrom);
                }

                if (suppliedMaterial) materialMaps++;
            }
        }

        return new Result(normals, materials, size, normalMaps, materialMaps, emissiveMaps, sources);
    }

    private static TextureSetPaths ReadTextureSet(TexturePack pack, IReadOnlyList<string> colorPaths)
    {
        foreach (var color in colorPaths)
        {
            var jsonPath = WithoutPng(color) + ".texture_set.json";
            var raw = pack.TryReadAssetBytes(jsonPath, 256 * 1024, out _);
            if (raw is null) continue;

            try
            {
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;
                if (root.TryGetProperty("minecraft:texture_set", out var nested)) root = nested;
                var directory = color[..Math.Max(0, color.LastIndexOf('/') + 1)];
                return new TextureSetPaths(
                    TextureSetPath(root, "normal", directory),
                    TextureSetPath(root, "heightmap", directory),
                    TextureSetPath(root, "metalness_emissive_roughness", directory));
            }
            catch (JsonException)
            {
                // A malformed optional companion cannot make the colour texture unusable.
            }
        }
        return new TextureSetPaths("", "", "");
    }

    private static string TextureSetPath(JsonElement root, string property, string directory)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return "";
        var path = value.GetString()?.Replace('\\', '/').Trim() ?? "";
        if (path.Length == 0 || path.Contains(':')) return "";
        if (!path.Contains('/')) path = directory + path;
        if (!path.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)) path = "textures/" + path.TrimStart('/');
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) path += ".png";
        return path;
    }

    private static IEnumerable<string> NormalCandidates(IEnumerable<string> paths, string explicitPath) =>
        ExplicitThenSuffixes(paths, explicitPath, "_normal", "_norm", "_n");

    private static IEnumerable<string> HeightCandidates(IEnumerable<string> paths, string explicitPath) =>
        ExplicitThenSuffixes(paths, explicitPath, "_heightmap", "_height", "_bump");

    private static IEnumerable<string> MerCandidates(IEnumerable<string> paths, string explicitPath) =>
        ExplicitThenSuffixes(paths, explicitPath, "_mer", "_mers");

    private static IEnumerable<string> RoughnessCandidates(IEnumerable<string> paths) =>
        Suffixes(paths, "_roughness", "_rough", "_r");

    private static IEnumerable<string> SpecularCandidates(IEnumerable<string> paths) =>
        Suffixes(paths, "_specular", "_spec", "_s");

    private static IEnumerable<string> MetalCandidates(IEnumerable<string> paths) =>
        Suffixes(paths, "_metalness", "_metallic", "_metal", "_m");

    private static IEnumerable<string> EmissiveCandidates(IEnumerable<string> paths) =>
        Suffixes(paths, "_emissive", "_emit", "_e");

    private static IEnumerable<string> ExplicitThenSuffixes(
        IEnumerable<string> paths, string explicitPath, params string[] suffixes)
    {
        if (explicitPath.Length > 0) yield return explicitPath;
        foreach (var path in Suffixes(paths, suffixes)) yield return path;
    }

    private static IEnumerable<string> Suffixes(IEnumerable<string> paths, params string[] suffixes)
    {
        foreach (var path in paths)
        foreach (var suffix in suffixes)
            yield return WithoutPng(path) + suffix + ".png";
    }

    private static string WithoutPng(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;

    private static bool LoadFirst(
        TexturePack pack, int size, IEnumerable<string> candidates,
        out byte[] pixels, out string from)
    {
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var loaded = pack.TryLoadTile(candidate, size, out from);
            if (loaded is null) continue;
            pixels = loaded;
            return true;
        }
        pixels = [];
        from = "";
        return false;
    }

    private static byte[] Solid(int size, byte r, byte g, byte b, byte a)
    {
        var pixels = new byte[checked(size * size * 4)];
        for (var p = 0; p < pixels.Length; p += 4)
        {
            pixels[p] = r;
            pixels[p + 1] = g;
            pixels[p + 2] = b;
            pixels[p + 3] = a;
        }
        return pixels;
    }

    private static void CopyMer(ReadOnlySpan<byte> from, Span<byte> to)
    {
        for (var p = 0; p + 3 < from.Length && p + 3 < to.Length; p += 4)
        {
            to[p] = from[p];
            to[p + 1] = from[p + 2];
            to[p + 2] = from[p + 1];
            to[p + 3] = 255;
        }
    }

    private static void CopyLuminance(ReadOnlySpan<byte> from, Span<byte> to, int channel, bool invert)
    {
        for (var p = 0; p + 3 < from.Length && p + 3 < to.Length; p += 4)
        {
            var value = (from[p] * 54 + from[p + 1] * 183 + from[p + 2] * 19) >> 8;
            to[p + channel] = (byte)(invert ? 255 - value : value);
        }
    }

    private static byte[] NormalFromHeight(ReadOnlySpan<byte> height, int size)
    {
        var normal = new byte[height.Length];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = HeightAt(height, size, x + 1, y) - HeightAt(height, size, x - 1, y);
            var dy = HeightAt(height, size, x, y + 1) - HeightAt(height, size, x, y - 1);
            var length = MathF.Sqrt(dx * dx * 4f + dy * dy * 4f + 1f);
            var p = (y * size + x) * 4;
            normal[p] = (byte)Math.Clamp((int)((-dx * 2f / length * 0.5f + 0.5f) * 255f), 0, 255);
            normal[p + 1] = (byte)Math.Clamp((int)((-dy * 2f / length * 0.5f + 0.5f) * 255f), 0, 255);
            normal[p + 2] = (byte)Math.Clamp((int)((1f / length * 0.5f + 0.5f) * 255f), 0, 255);
            normal[p + 3] = 255;
        }
        return normal;
    }

    private static float HeightAt(ReadOnlySpan<byte> height, int size, int x, int y)
    {
        x = (x + size) % size;
        y = (y + size) % size;
        var p = (y * size + x) * 4;
        return (height[p] * 54 + height[p + 1] * 183 + height[p + 2] * 19) / (255f * 256f);
    }

    private static bool AnyChannel(ReadOnlySpan<byte> pixels, int channel)
    {
        for (var p = channel; p < pixels.Length; p += 4)
            if (pixels[p] > 3) return true;
        return false;
    }

    private static void AddSource(List<string> sources, string source)
    {
        if (source.Length > 0 && sources.Count < 32
            && !sources.Contains(source, StringComparer.OrdinalIgnoreCase)) sources.Add(source);
    }

    private static bool IsMetal(string name) =>
        name.Contains("iron", StringComparison.OrdinalIgnoreCase)
        || name.Contains("gold", StringComparison.OrdinalIgnoreCase)
        || name.Contains("copper", StringComparison.OrdinalIgnoreCase)
        || name.Contains("anvil", StringComparison.OrdinalIgnoreCase)
        || name.Contains("bucket", StringComparison.OrdinalIgnoreCase)
        || name.Contains("shears", StringComparison.OrdinalIgnoreCase)
        || name.Contains("compass", StringComparison.OrdinalIgnoreCase);

    private static bool IsLuminous(string name) =>
        name.Contains("emberstone", StringComparison.OrdinalIgnoreCase)
        || name.Contains("torch", StringComparison.OrdinalIgnoreCase)
        || name.Contains("lantern", StringComparison.OrdinalIgnoreCase)
        || name.Contains("fire", StringComparison.OrdinalIgnoreCase)
        || name.Contains("lit", StringComparison.OrdinalIgnoreCase)
        || name.Contains("lamp", StringComparison.OrdinalIgnoreCase)
        || name.Contains("lava", StringComparison.OrdinalIgnoreCase);

    private static byte Roughness(string name)
    {
        if (name.Contains("water", StringComparison.OrdinalIgnoreCase)
            || name.Contains("glass", StringComparison.OrdinalIgnoreCase)) return 48;
        if (IsMetal(name)) return 92;
        if (name.Contains("leaf", StringComparison.OrdinalIgnoreCase)
            || name.Contains("grass", StringComparison.OrdinalIgnoreCase)
            || name.Contains("flower", StringComparison.OrdinalIgnoreCase)) return 226;
        return 188;
    }

    /// <summary>Pure fallback invariants used by the headless release audit.</summary>
    public static IReadOnlyList<string> SelfTest(out string detail)
    {
        var faults = new List<string>();
        var result = Build(null, 8);
        if (result.Normals.Length != BlockTextureSet.Layers.Length
            || result.Materials.Length != BlockTextureSet.Layers.Length)
            faults.Add("the companion arrays do not line up with the colour array");
        if (result.Normals.Any(tile => tile.Length != 8 * 8 * 4)
            || result.Materials.Any(tile => tile.Length != 8 * 8 * 4))
            faults.Add("a fallback companion tile has the wrong dimensions");
        if (result.Normals.Any(tile => tile[0] != 128 || tile[1] != 128 || tile[2] != 255))
            faults.Add("a missing normal map is not the flat-normal fallback");
        var water = result.Materials[Array.FindIndex(BlockTextureSet.Layers,
            static row => row.Name == "water")];
        if (water[1] >= 96) faults.Add("the fallback water material is not smoother than stone");

        var root = Path.Combine(Path.GetTempPath(), $"driftwood-material-{Environment.ProcessId}");
        try
        {
            var java = Path.Combine(root, "java");
            var javaBlocks = Path.Combine(java, "assets", "minecraft", "textures", "block");
            Directory.CreateDirectory(javaBlocks);
            File.WriteAllText(Path.Combine(java, "pack.mcmeta"), "{\"pack\":{\"pack_format\":34}}");
            File.WriteAllBytes(Path.Combine(javaBlocks, "stone_n.png"), Tile(255, 128, 128));
            File.WriteAllBytes(Path.Combine(javaBlocks, "stone_s.png"), Tile(255, 255, 255));
            File.WriteAllBytes(Path.Combine(javaBlocks, "glowstone_e.png"), Tile(220, 220, 220));
            var javaResult = Build(java, 8);
            if (javaResult.Normals[0][0] < 240)
                faults.Add("a Java _n normal did not replace stone's flat fallback");
            if (javaResult.Materials[0][1] > 8)
                faults.Add("a Java white _s map did not make stone smooth");
            if (javaResult.Materials[14][2] < 180)
                faults.Add("a Java _e map did not reach emberstone's emissive channel");

            var bedrock = Path.Combine(root, "bedrock");
            var bedrockBlocks = Path.Combine(bedrock, "textures", "blocks");
            Directory.CreateDirectory(bedrockBlocks);
            File.WriteAllText(Path.Combine(bedrock, "manifest.json"), "{\"format_version\":2}");
            File.WriteAllText(Path.Combine(bedrockBlocks, "stone.texture_set.json"),
                "{\"minecraft:texture_set\":{\"normal\":\"stone_custom_normal\","
                + "\"metalness_emissive_roughness\":\"stone_custom_mer\"}}");
            File.WriteAllBytes(Path.Combine(bedrockBlocks, "stone_custom_normal.png"), Tile(128, 255, 128));
            File.WriteAllBytes(Path.Combine(bedrockBlocks, "stone_custom_mer.png"), Tile(200, 100, 50));
            File.WriteAllBytes(Path.Combine(bedrockBlocks, "log_oak_normal.png"), Tile(230, 128, 160));
            var bedrockResult = Build(bedrock, 8);
            if (bedrockResult.Normals[0][1] < 240)
                faults.Add("a Bedrock texture-set normal did not replace stone's flat fallback");
            if (bedrockResult.Materials[0][0] < 190 || bedrockResult.Materials[0][1] is < 40 or > 60
                || bedrockResult.Materials[0][2] is < 90 or > 110)
                faults.Add("a Bedrock MER map did not unpack metal/emissive/roughness into runtime channels");
            var oak = Array.FindIndex(BlockTextureSet.Layers, static row => row.Name == "driftoak_side");
            if (oak < 0 || bedrockResult.Normals[oak][0] < 220)
                faults.Add("a Bedrock companion did not inherit oak_log's log_oak rename");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            faults.Add($"could not build companion-map packs: {error.Message}");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }

        detail = $"{result.Normals.Length} aligned normal/MER layers with fallbacks; Java suffix and "
                 + "Bedrock texture-set/MER packs read on disk";
        return faults;

        static byte[] Tile(byte r, byte g, byte b)
        {
            var pixels = new byte[2 * 2 * 4];
            for (var p = 0; p < pixels.Length; p += 4)
            {
                pixels[p] = r;
                pixels[p + 1] = g;
                pixels[p + 2] = b;
                pixels[p + 3] = 255;
            }
            return Png.Encode(new Image(2, 2, pixels));
        }
    }
}
