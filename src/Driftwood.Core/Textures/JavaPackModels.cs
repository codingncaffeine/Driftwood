using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Driftwood.Core.Blocks;
using Driftwood.Core.Items;

namespace Driftwood.Core.Textures;

/// <summary>
/// Resolves the standard Java blockstate/model graph onto Driftwood's existing blocks and texture
/// array. It intentionally stops at the standard format boundary: OptiFine CEM/CIT/CTM are reported
/// by <see cref="PackCompatibility"/>, not interpreted as if they were ordinary models.
/// </summary>
public sealed class JavaPackModels
{
    public const int MaxJsonBytes = 4 * 1024 * 1024;
    public const int MaxDepth = 32;
    public const int MaxElements = 256;
    public const int MaxMultipart = 256;

    public sealed record BlockRequest(string Name, IReadOnlyDictionary<string, string> Properties);

    public sealed record DisplayTransform(Vector3 Rotation, Vector3 Translation, Vector3 Scale)
    {
        public static DisplayTransform Identity { get; } = new(Vector3.Zero, Vector3.Zero, Vector3.One);
    }

    public sealed record Resolution(
        bool Found,
        BlockModel? Model,
        ushort? FlatLayer,
        string Source,
        IReadOnlyDictionary<string, DisplayTransform> Display,
        IReadOnlyList<string> Issues);

    public sealed record Application(
        int BlocksApplied,
        int ItemsApplied,
        int FilesRead,
        int WeightedChoices,
        IReadOnlyList<string> Issues);

    private sealed record RawFace(
        int Direction,
        string Texture,
        int CullFace,
        bool Tinted,
        Vector4? Uv,
        int Rotation);

    private sealed record RawElement(
        Vector3 From,
        Vector3 To,
        RawFace[] Faces,
        bool Shade,
        float RotationAngle,
        int RotationAxis,
        Vector3 RotationOrigin,
        bool Rescale);

    private sealed record ModelOverride(IReadOnlyDictionary<string, float> Predicate, string Model);

    private sealed record RawModel(
        string Id,
        IReadOnlyDictionary<string, string> Textures,
        RawElement[] Elements,
        bool AmbientOcclusion,
        IReadOnlyDictionary<string, DisplayTransform> Display,
        ModelOverride[] Overrides,
        bool Generated);

    private sealed record ModelSpec(string Model, int X = 0, int Y = 0, bool UvLock = false, int Weight = 1);

    private readonly TexturePack _pack;
    private readonly Dictionary<string, RawModel?> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private int _filesRead;
    private int _weightedChoices;

    public List<string> Faults { get; } = [];

    public JavaPackModels(TexturePack pack) => _pack = pack;

    /// <summary>Applies every relevant supplied block model, preserving the registry's collision.</summary>
    public Application Apply(BlockRegistry blocks, ItemRegistry? items = null)
    {
        var startFault = Faults.Count;
        var blockCount = 0;
        foreach (var block in blocks.All)
        {
            if (block.Id.IsAir) continue;
            var request = InferBlock(block.Name);
            if (request is null) continue;

            var resolved = ResolveBlock(request);
            if (!resolved.Found || resolved.Model is not { Quads.Length: > 0 } model) continue;
            if (block.Opaque && !model.OccludesCell)
            {
                Report($"blockstates/{request.Name}: a non-cube model cannot replace opaque '{block.Name}'");
                continue;
            }

            block.Model = model.WithCollisionFrom(block.Model);
            blockCount++;
        }

        var itemCount = 0;
        if (items is not null)
        {
            foreach (var item in items.All)
            {
                if (item.Id.IsNone) continue;
                var foreign = InferItem(item.Name);
                if (foreign is null) continue;
                var resolved = ResolveItem(foreign);
                if (!resolved.Found)
                {
                    // Seal copied the block icon before pack geometry was applied. Keep placeable
                    // items in lockstep even when the sparse pack has no separate item definition.
                    if (!item.PlainBlock.IsAir) item.IconModel = blocks[item.PlainBlock].Model;
                    continue;
                }

                if (resolved.FlatLayer is { } layer)
                {
                    item.IconLayer = layer;
                    itemCount++;
                }
                else if (resolved.Model is { Quads.Length: > 0 } model)
                {
                    item.IconModel = model;
                    itemCount++;
                }
            }
        }

        return new Application(blockCount, itemCount, _filesRead, _weightedChoices,
            Faults.Skip(startFault).ToArray());
    }

    /// <summary>Resolves variants or multipart for one owned block state.</summary>
    public Resolution ResolveBlock(BlockRequest request)
    {
        var issuesAt = Faults.Count;
        var source = $"blockstates/{request.Name}.json";
        byte[]? bytes;
        string from;
        try { bytes = ReadJson(request.Name, "blockstates", out from); }
        catch (InvalidDataException error)
        {
            Report(error.Message);
            return Empty(true, source, issuesAt);
        }
        var specs = new List<ModelSpec>();

        if (bytes is null)
        {
            // A model override without a blockstate is useful and common in sparse packs. Conversely
            // a pack that supplies neither should make no claim on the built-in shape.
            if (!ModelExists($"block/{request.Name}"))
                return Empty(false, source, issuesAt);
            specs.Add(new ModelSpec($"minecraft:block/{request.Name}"));
            source = $"models/block/{request.Name}.json";
        }
        else
        {
            source = from;
            try
            {
                using var document = Parse(bytes, source);
                var root = document.RootElement;
                if (root.TryGetProperty("variants", out var variants)
                    && variants.ValueKind == JsonValueKind.Object)
                {
                    var bestScore = -1;
                    JsonElement best = default;
                    var found = false;
                    foreach (var variant in variants.EnumerateObject())
                    {
                        if (!SelectorMatches(variant.Name, request.Properties, out var score)) continue;
                        if (score < bestScore) continue;
                        bestScore = score;
                        best = variant.Value;
                        found = true;
                    }
                    if (found && ChooseSpec(best, request.Name) is { } chosen) specs.Add(chosen);
                }

                if (root.TryGetProperty("multipart", out var multipart)
                    && multipart.ValueKind == JsonValueKind.Array)
                {
                    var count = 0;
                    foreach (var part in multipart.EnumerateArray())
                    {
                        if (++count > MaxMultipart) throw new InvalidDataException(
                            $"{source}: more than {MaxMultipart} multipart entries");
                        if (part.ValueKind != JsonValueKind.Object) continue;
                        if (part.TryGetProperty("when", out var when)
                            && !WhenMatches(when, request.Properties)) continue;
                        if (part.TryGetProperty("apply", out var apply)
                            && ChooseSpec(apply, $"{request.Name}:{count}") is { } chosen)
                            specs.Add(chosen);
                    }
                }
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or FormatException
                                          or InvalidOperationException or OverflowException)
            {
                Report($"{source}: {error.Message}");
            }
        }

        if (specs.Count == 0)
        {
            Report($"{source}: no variant or multipart entry matches Driftwood's owned state");
            return Empty(true, source, issuesAt);
        }

        var elements = new List<ModelElement>();
        IReadOnlyDictionary<string, DisplayTransform> display =
            new Dictionary<string, DisplayTransform>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in specs)
        {
            var data = ResolveModel(spec.Model, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
            if (data is null) continue;
            display = data.Display;
            var baked = Bake(data, source);
            elements.AddRange(Rotate(baked, spec.X, spec.Y, spec.UvLock));
        }

        var model = elements.Count > 0 ? BlockModel.FromElements([.. elements]) : null;
        return new Resolution(true, model, null, source, display,
            Faults.Skip(issuesAt).ToArray());
    }

    /// <summary>
    /// Resolves a current <c>items/*.json</c> definition or a legacy <c>models/item/*.json</c>.
    /// Predicate values are optional because inventory icons normally represent the resting state.
    /// </summary>
    public Resolution ResolveItem(
        string item,
        IReadOnlyDictionary<string, float>? numericContext = null,
        IReadOnlyDictionary<string, string>? selectContext = null)
    {
        var issuesAt = Faults.Count;
        var source = $"items/{item}.json";
        var modelId = ResolveCurrentItem(item, numericContext, selectContext, out var currentSource);
        if (modelId is not null) source = currentSource;

        modelId ??= ModelExists($"item/{item}") ? $"minecraft:item/{item}" : null;
        if (modelId is null) return Empty(false, source, issuesAt);

        var data = ResolveModel(modelId, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
        if (data is null) return Empty(true, source, issuesAt);

        if (numericContext is { Count: > 0 } && data.Overrides.Length > 0)
        {
            string? selected = null;
            foreach (var candidate in data.Overrides)
            {
                var match = candidate.Predicate.All(predicate => numericContext.TryGetValue(
                    predicate.Key, out var actual) && actual >= predicate.Value);
                if (match) selected = candidate.Model; // Java uses the last matching override.
            }

            if (selected is not null)
                data = ResolveModel(selected, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0) ?? data;
        }

        if (data.Generated)
        {
            var texture = ResolveTexture("#layer0", data.Textures, source);
            if (texture is not null && BlockTextureSet.TryLayerForResource(texture, out var layer))
                return new Resolution(true, null, layer, source, data.Display,
                    Faults.Skip(issuesAt).ToArray());

            if (texture is not null) Report($"{source}: texture '{texture}' has no Driftwood item layer");
            return Empty(true, source, issuesAt, data.Display);
        }

        var elements = Bake(data, source);
        return new Resolution(true,
            elements.Length > 0 ? BlockModel.FromElements(elements) : null,
            null, source, data.Display, Faults.Skip(issuesAt).ToArray());
    }

    /// <summary>Maps a runtime state name to the nearest standard Java block and properties.</summary>
    public static BlockRequest? InferBlock(string localName)
    {
        if (string.IsNullOrWhiteSpace(localName) || localName == "air") return null;
        var original = localName;
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (localName.EndsWith(Waterlogging.Suffix, StringComparison.Ordinal))
        {
            localName = localName[..^Waterlogging.Suffix.Length];
            properties["waterlogged"] = "true";
        }

        if (localName.EndsWith("_open", StringComparison.Ordinal))
        {
            localName = localName[..^5];
            properties["open"] = "true";
        }
        else properties["open"] = "false";

        if (localName.EndsWith("_pressed", StringComparison.Ordinal))
        {
            localName = localName[..^8];
            properties["powered"] = "true";
        }
        else if (localName.EndsWith("_on", StringComparison.Ordinal))
        {
            localName = localName[..^3];
            properties["powered"] = "true";
        }

        if (localName.EndsWith("_lit", StringComparison.Ordinal))
        {
            localName = localName[..^4];
            properties["lit"] = "true";
        }

        foreach (var half in new[] { "upper", "lower" })
        {
            var token = $"_{half}";
            if (!localName.EndsWith(token, StringComparison.Ordinal)) continue;
            localName = localName[..^token.Length];
            properties["half"] = half == "upper" ? "top" : "bottom";
            break;
        }

        var directions = new[] { "east", "west", "south", "north" };
        foreach (var direction in directions)
        {
            var token = $"_{direction}";
            var at = localName.LastIndexOf(token, StringComparison.Ordinal);
            if (at < 0 || at + token.Length != localName.Length) continue;
            localName = localName[..at];
            properties["facing"] = direction;
            break;
        }

        if (localName.EndsWith("_x", StringComparison.Ordinal)
            || localName.EndsWith("_z", StringComparison.Ordinal))
        {
            properties["axis"] = localName[^1].ToString();
            localName = localName[..^2];
        }

        // Runtime colour families put the colour last; Java puts it first.
        foreach (var colour in StarterBlocks.Colours.Select(static colour => colour.Name))
        {
            if (localName == $"wool_{colour}") localName = $"{colour}_wool";
            else if (localName == $"carpet_{colour}") localName = $"{colour}_carpet";
            else if (localName == $"stained_glass_{colour}") localName = $"{colour}_stained_glass";
        }

        localName = localName switch
        {
            "grass" => "grass_block",
            "driftoak_log" => "oak_log",
            "driftoak_leaves" => "oak_leaves",
            "driftoak_planks" => "oak_planks",
            "rubble" => "cobblestone",
            "mossy_rubble" => "mossy_cobblestone",
            "bench" => "crafting_table",
            "pressure_plate" => "stone_pressure_plate",
            "door" => "oak_door",
            "trapdoor" => "oak_trapdoor",
            "torch_wall" => "wall_torch",
            "tidelamp" => "redstone_lamp",
            "smokeglass" => "tinted_glass",
            "deepstone" => "deepslate",
            "deepstone_polished" => "polished_deepslate",
            "marsh_reed" => "sugar_cane",
            "meadowgrass" => "short_grass",
            "moss" => "moss_block",
            _ => localName,
        };

        // Chests, banners and beds are entity-rendered in Java; their JSON block models are not the
        // visual resource and applying a guessed cube would be less compatible than keeping ours.
        if (original.StartsWith("chest_", StringComparison.Ordinal)
            || original.StartsWith("banner_", StringComparison.Ordinal)
            || original.StartsWith("bed_", StringComparison.Ordinal)) return null;

        return new BlockRequest(localName, properties);
    }

    public static string? InferItem(string localName)
    {
        if (string.IsNullOrWhiteSpace(localName) || localName == "nothing") return null;
        foreach (var colour in StarterBlocks.Colours.Select(static colour => colour.Name))
        {
            if (localName == $"wool_{colour}") return $"{colour}_wool";
            if (localName == $"carpet_{colour}") return $"{colour}_carpet";
            if (localName == $"stained_glass_{colour}") return $"{colour}_stained_glass";
        }
        return localName switch
        {
            "driftoak_log" => "oak_log",
            "driftoak_leaves" => "oak_leaves",
            "driftoak_planks" => "oak_planks",
            "rubble" => "cobblestone",
            "mossy_rubble" => "mossy_cobblestone",
            "bench" => "crafting_table",
            "door" => "oak_door",
            "trapdoor" => "oak_trapdoor",
            "marsh_reed" => "sugar_cane",
            "meadowgrass" => "short_grass",
            _ => localName,
        };
    }

    private RawModel? ResolveModel(string id, HashSet<string> stack, int depth)
    {
        var suppliedId = id;
        try { id = NormaliseId(id, "block"); }
        catch (InvalidDataException error)
        {
            Report($"models/{suppliedId}: {error.Message}");
            return null;
        }
        if (_models.TryGetValue(id, out var cached)) return cached;
        if (depth >= MaxDepth)
        {
            Report($"models/{id}: parent depth exceeds {MaxDepth}");
            return _models[id] = null;
        }
        if (!stack.Add(id))
        {
            Report($"models/{id}: parent cycle ({string.Join(" -> ", stack.Append(id))})");
            return _models[id] = null;
        }

        try
        {
            var bytes = ReadJson(id, "models", out var from);
            if (bytes is null)
            {
                if (Builtin(id) is { } builtin) return _models[id] = builtin;
                // Resource packs are sparse overlays. A supplied blockstate commonly points at an
                // unchanged vanilla one-texture model which is absent from the ZIP; the matching
                // picture is nevertheless already on Driftwood's fixed texture array. Recreate
                // only that unambiguous cube/generated case, leaving compound vanilla shapes and
                // arbitrary custom model names visible as honest omissions.
                if (MappedVanillaFallback(id) is { } fallback)
                    return _models[id] = fallback;
                Report($"models/{id}: referenced model is not supplied by the pack");
                return _models[id] = null;
            }

            using var document = Parse(bytes, from);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{from}: model root is not an object");

            RawModel? parent = null;
            var parentId = root.TryGetProperty("parent", out var parentElement)
                           && parentElement.ValueKind == JsonValueKind.String
                ? parentElement.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(parentId)) parent = ResolveModel(parentId!, stack, depth + 1);

            var textures = parent is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(parent.Textures, StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("textures", out var textureObject)
                && textureObject.ValueKind == JsonValueKind.Object)
                foreach (var texture in textureObject.EnumerateObject())
                    if (texture.Value.ValueKind == JsonValueKind.String)
                        textures[texture.Name] = texture.Value.GetString() ?? "";

            RawElement[] elements;
            if (root.TryGetProperty("elements", out var elementArray)) elements = ParseElements(elementArray, from);
            else elements = parent?.Elements ?? [];

            var ambient = root.TryGetProperty("ambientocclusion", out var ao)
                && ao.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? ao.GetBoolean()
                : parent?.AmbientOcclusion ?? true;

            var display = parent is null
                ? new Dictionary<string, DisplayTransform>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, DisplayTransform>(parent.Display, StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("display", out var displayObject)
                && displayObject.ValueKind == JsonValueKind.Object)
                foreach (var transform in displayObject.EnumerateObject())
                    display[transform.Name] = ParseDisplay(transform.Value, from);

            var overrides = root.TryGetProperty("overrides", out var overrideArray)
                ? ParseOverrides(overrideArray, from)
                : parent?.Overrides ?? [];

            var generated = parent?.Generated ?? false;
            var model = new RawModel(id, textures, elements, ambient, display, overrides, generated);
            return _models[id] = model;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or FormatException
                                      or InvalidOperationException or OverflowException)
        {
            Report($"models/{id}: {error.Message}");
            return _models[id] = null;
        }
        finally
        {
            stack.Remove(id);
        }
    }

    private RawElement[] ParseElements(JsonElement array, string source)
    {
        if (array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{source}: elements is not an array");

        var result = new List<RawElement>();
        foreach (var element in array.EnumerateArray())
        {
            if (result.Count >= MaxElements)
                throw new InvalidDataException($"{source}: more than {MaxElements} model elements");
            if (element.ValueKind != JsonValueKind.Object) continue;

            if (!element.TryGetProperty("from", out var fromElement)
                || !element.TryGetProperty("to", out var toElement))
                throw new InvalidDataException($"{source}: a model element has no from or to vector");
            var from = Vector(fromElement, source, "from");
            var to = Vector(toElement, source, "to");
            if (from.X > to.X || from.Y > to.Y || from.Z > to.Z)
                throw new InvalidDataException($"{source}: an element's from is greater than its to");

            var shade = !element.TryGetProperty("shade", out var shadeElement)
                        || shadeElement.ValueKind != JsonValueKind.False;
            float angle = 0;
            var axis = 1;
            var origin = new Vector3(8);
            var rescale = false;
            if (element.TryGetProperty("rotation", out var rotation)
                && rotation.ValueKind == JsonValueKind.Object)
            {
                angle = Number(rotation, "angle", 0);
                if (angle is not (0 or 22.5f or -22.5f or 45f or -45f))
                    throw new InvalidDataException($"{source}: element angle {angle} is not standard");
                axis = rotation.TryGetProperty("axis", out var axisElement)
                       && axisElement.ValueKind == JsonValueKind.String
                    ? Axis(axisElement.GetString()) : 1;
                if (rotation.TryGetProperty("origin", out var originElement))
                    origin = Vector(originElement, source, "rotation origin");
                rescale = rotation.TryGetProperty("rescale", out var resize)
                          && resize.ValueKind == JsonValueKind.True;
            }

            var faces = new List<RawFace>();
            if (element.TryGetProperty("faces", out var faceObject)
                && faceObject.ValueKind == JsonValueKind.Object)
            {
                foreach (var face in faceObject.EnumerateObject())
                {
                    var direction = Direction(face.Name);
                    if (direction < 0 || face.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!face.Value.TryGetProperty("texture", out var textureElement)
                        || textureElement.ValueKind != JsonValueKind.String) continue;
                    var texture = textureElement.GetString() ?? "";
                    var cull = face.Value.TryGetProperty("cullface", out var cullElement)
                               && cullElement.ValueKind == JsonValueKind.String
                        ? Direction(cullElement.GetString() ?? "") : -1;
                    var tinted = face.Value.TryGetProperty("tintindex", out var tint)
                                 && tint.TryGetInt32(out var tintIndex) && tintIndex >= 0;
                    var degrees = face.Value.TryGetProperty("rotation", out var faceRotation)
                        ? Number(faceRotation, source, "face rotation") : 0;
                    if (degrees % 90 != 0 || degrees is < 0 or > 270)
                        throw new InvalidDataException($"{source}: face rotation must be 0, 90, 180 or 270");
                    var quarter = checked((int)degrees / 90);
                    Vector4? uv = null;
                    if (face.Value.TryGetProperty("uv", out var uvElement))
                        uv = Vector4(uvElement, source, "uv");
                    faces.Add(new RawFace(direction, texture, cull, tinted, uv, quarter));
                }
            }

            result.Add(new RawElement(from, to, [.. faces], shade, angle, axis, origin, rescale));
        }
        return [.. result];
    }

    private ModelElement[] Bake(RawModel model, string source)
    {
        var result = new List<ModelElement>(model.Elements.Length);
        foreach (var raw in model.Elements)
        {
            var faces = new ModelFace?[Faces.Count];
            foreach (var face in raw.Faces)
            {
                var texture = ResolveTexture(face.Texture, model.Textures, source);
                if (texture is null) continue;
                if (!BlockTextureSet.TryLayerForResource(texture, out var layer))
                {
                    Report($"{source}: model texture '{texture}' has no Driftwood runtime layer");
                    continue;
                }
                faces[face.Direction] = new ModelFace
                {
                    Layer = layer,
                    CullFace = face.CullFace,
                    Tinted = face.Tinted,
                    Uv = face.Uv,
                    Rotation = face.Rotation,
                };
            }

            if (faces.All(static face => face is null)) continue;
            result.Add(new ModelElement
            {
                From = raw.From,
                To = raw.To,
                Faces = faces,
                Shade = raw.Shade,
                AmbientOcclusion = model.AmbientOcclusion,
                RotationAngle = raw.RotationAngle,
                RotationAxis = raw.RotationAxis,
                RotationOrigin = raw.RotationOrigin,
                Rescale = raw.Rescale,
            });
        }
        return [.. result];
    }

    private string? ResolveTexture(
        string value,
        IReadOnlyDictionary<string, string> textures,
        string source)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (value.StartsWith('#'))
        {
            var key = value[1..];
            if (!seen.Add(key))
            {
                Report($"{source}: texture-variable cycle at '#{key}'");
                return null;
            }
            if (!textures.TryGetValue(key, out value!))
            {
                Report($"{source}: texture variable '#{key}' is not defined");
                return null;
            }
        }
        return value;
    }

    private static ModelElement[] Rotate(ModelElement[] source, int xDegrees, int yDegrees, bool uvLock)
    {
        var xTurns = ModQuarter(xDegrees);
        var yTurns = ModQuarter(yDegrees);
        if (xTurns == 0 && yTurns == 0) return source;

        return source.Select(element =>
        {
            var corners = BoxCorners(element.From, element.To)
                .Select(point => RotatePoint(point, xTurns, yTurns)).ToArray();
            var from = corners.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var to = corners.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            var faces = new ModelFace?[Faces.Count];
            for (var face = 0; face < Faces.Count; face++)
            {
                if (element.Faces[face] is not { } old) continue;
                var direction = RotateDirection(face, xTurns, yTurns);
                var cull = old.CullFace < 0 ? -1 : RotateDirection(old.CullFace, xTurns, yTurns);
                faces[direction] = new ModelFace
                {
                    Layer = old.Layer,
                    CullFace = cull,
                    Tinted = old.Tinted,
                    Uv = old.Uv,
                    Rotation = uvLock ? old.Rotation : (old.Rotation + yTurns) & 3,
                };
            }

            var axisVector = element.RotationAxis switch
            {
                0 => Vector3.UnitX,
                2 => Vector3.UnitZ,
                _ => Vector3.UnitY,
            };
            axisVector = RotateVector(axisVector, xTurns, yTurns);
            var axis = MathF.Abs(axisVector.X) > 0.5f ? 0
                : MathF.Abs(axisVector.Z) > 0.5f ? 2 : 1;

            return new ModelElement
            {
                From = from,
                To = to,
                Faces = faces,
                Shade = element.Shade,
                AmbientOcclusion = element.AmbientOcclusion,
                RotationAngle = element.RotationAngle,
                RotationAxis = axis,
                RotationOrigin = RotatePoint(element.RotationOrigin, xTurns, yTurns),
                Rescale = element.Rescale,
            };
        }).ToArray();
    }

    private string? ResolveCurrentItem(
        string item,
        IReadOnlyDictionary<string, float>? numeric,
        IReadOnlyDictionary<string, string>? select,
        out string source)
    {
        source = $"items/{item}.json";
        try
        {
            var bytes = ReadJson(item, "items", out var from);
            if (bytes is null) return null;
            source = from;
            using var document = Parse(bytes, from);
            var root = document.RootElement;
            if (!root.TryGetProperty("model", out var model))
                throw new InvalidDataException($"{from}: current item definition has no model");
            return SelectCurrentModel(model, numeric, select, from, 0);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or FormatException
                                      or InvalidOperationException or OverflowException)
        {
            Report($"{source}: {error.Message}");
            return null;
        }
    }

    private string? SelectCurrentModel(
        JsonElement model,
        IReadOnlyDictionary<string, float>? numeric,
        IReadOnlyDictionary<string, string>? select,
        string source,
        int depth)
    {
        if (depth >= MaxDepth) throw new InvalidDataException($"{source}: item model graph is too deep");
        if (model.ValueKind == JsonValueKind.String) return model.GetString();
        if (model.ValueKind != JsonValueKind.Object) return null;

        var type = model.TryGetProperty("type", out var typeElement)
                   && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString() ?? "minecraft:model" : "minecraft:model";
        type = type[(type.IndexOf(':') + 1)..];

        if (type == "model")
            return model.TryGetProperty("model", out var ordinary)
                   && ordinary.ValueKind == JsonValueKind.String ? ordinary.GetString() : null;

        if (type == "condition")
        {
            var property = String(model, "property");
            var truth = property is not null && numeric?.TryGetValue(property, out var value) == true && value > 0;
            var branch = truth ? "on_true" : "on_false";
            return model.TryGetProperty(branch, out var selected)
                ? SelectCurrentModel(selected, numeric, select, source, depth + 1) : null;
        }

        if (type == "select")
        {
            var property = String(model, "property");
            var wanted = property is not null && select?.TryGetValue(property, out var value) == true ? value : null;
            if (wanted is not null && model.TryGetProperty("cases", out var cases)
                && cases.ValueKind == JsonValueKind.Array)
            {
                foreach (var candidate in cases.EnumerateArray())
                {
                    if (!candidate.TryGetProperty("when", out var when)) continue;
                    var matches = when.ValueKind == JsonValueKind.String && when.GetString() == wanted
                        || when.ValueKind == JsonValueKind.Array && when.EnumerateArray().Any(
                            valueElement => valueElement.ValueKind == JsonValueKind.String
                                            && valueElement.GetString() == wanted);
                    if (matches && candidate.TryGetProperty("model", out var selected))
                        return SelectCurrentModel(selected, numeric, select, source, depth + 1);
                }
            }
            return model.TryGetProperty("fallback", out var fallback)
                ? SelectCurrentModel(fallback, numeric, select, source, depth + 1) : null;
        }

        if (type == "range_dispatch")
        {
            var property = String(model, "property");
            var value = property is not null && numeric?.TryGetValue(property, out var number) == true ? number : 0;
            JsonElement chosen = default;
            var found = false;
            if (model.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                foreach (var entry in entries.EnumerateArray())
                    if (entry.TryGetProperty("threshold", out var threshold)
                        && threshold.TryGetSingle(out var at) && value >= at
                        && entry.TryGetProperty("model", out var candidate))
                    { chosen = candidate; found = true; }
            if (found) return SelectCurrentModel(chosen, numeric, select, source, depth + 1);
            return model.TryGetProperty("fallback", out var fallback)
                ? SelectCurrentModel(fallback, numeric, select, source, depth + 1) : null;
        }

        if (type is "composite" or "bundle/selected_item")
        {
            if (model.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                foreach (var candidate in models.EnumerateArray())
                    if (SelectCurrentModel(candidate, numeric, select, source, depth + 1) is { } selected)
                        return selected;
        }

        if (type == "special" && model.TryGetProperty("base", out var specialBase))
            return SelectCurrentModel(specialBase, numeric, select, source, depth + 1);

        Report($"{source}: current item model type '{type}' is not relevant to a resting inventory icon");
        return null;
    }

    private ModelSpec? ChooseSpec(JsonElement value, string seed)
    {
        if (value.ValueKind == JsonValueKind.Object) return ParseSpec(value);
        if (value.ValueKind != JsonValueKind.Array) return null;
        var choices = new List<ModelSpec>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object || ParseSpec(element) is not { } parsed) continue;
            if (choices.Count >= MaxMultipart)
                throw new InvalidDataException($"a weighted model list has more than {MaxMultipart} entries");
            choices.Add(parsed);
        }
        if (choices.Count == 0) return null;
        if (choices.Count == 1) return choices[0];

        var total = choices.Sum(static choice => Math.Max(1, choice.Weight));
        var pick = StableHash(seed) % total;
        _weightedChoices++;
        foreach (var choice in choices)
        {
            pick -= Math.Max(1, choice.Weight);
            if (pick < 0) return choice;
        }
        return choices[^1];
    }

    private static ModelSpec? ParseSpec(JsonElement value)
    {
        if (!value.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String)
            return null;
        var x = value.TryGetProperty("x", out var xElement) && xElement.TryGetInt32(out var xv) ? xv : 0;
        var y = value.TryGetProperty("y", out var yElement) && yElement.TryGetInt32(out var yv) ? yv : 0;
        if (x % 90 != 0 || y % 90 != 0)
            throw new InvalidDataException("blockstate x/y rotations must be multiples of 90");
        return new ModelSpec(
            model.GetString() ?? "",
            x,
            y,
            value.TryGetProperty("uvlock", out var uv) && uv.ValueKind == JsonValueKind.True,
            value.TryGetProperty("weight", out var weight) && weight.TryGetInt32(out var w) ? Math.Max(1, w) : 1);
    }

    private static bool SelectorMatches(
        string selector,
        IReadOnlyDictionary<string, string> properties,
        out int score)
    {
        score = 0;
        if (selector.Length == 0 || selector == "normal") return true;
        foreach (var clause in selector.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = clause.IndexOf('=');
            if (equals <= 0) return false;
            var key = clause[..equals];
            var accepted = clause[(equals + 1)..].Split('|');
            if (!properties.TryGetValue(key, out var actual)
                && !TryDefaultProperty(key, out actual)
                || !accepted.Contains(actual, StringComparer.OrdinalIgnoreCase)) return false;
            score++;
        }
        return true;
    }

    private static bool WhenMatches(JsonElement when, IReadOnlyDictionary<string, string> properties)
    {
        if (when.ValueKind != JsonValueKind.Object) return true;
        if (when.TryGetProperty("OR", out var or) && or.ValueKind == JsonValueKind.Array)
            return or.EnumerateArray().Any(candidate => WhenMatches(candidate, properties));
        if (when.TryGetProperty("AND", out var and) && and.ValueKind == JsonValueKind.Array)
            return and.EnumerateArray().All(candidate => WhenMatches(candidate, properties));

        foreach (var condition in when.EnumerateObject())
        {
            if (condition.Name is "OR" or "AND") continue;
            var wanted = PropertyText(condition.Value);
            if (!properties.TryGetValue(condition.Name, out var actual)
                && !TryDefaultProperty(condition.Name, out actual)) return false;
            var alternatives = wanted.Split('|');
            if (!alternatives.Any(option => option.StartsWith('!')
                    ? !string.Equals(actual, option[1..], StringComparison.OrdinalIgnoreCase)
                    : string.Equals(actual, option, StringComparison.OrdinalIgnoreCase))) return false;
        }
        return true;
    }

    /// <summary>
    /// Properties Driftwood does not store still need a deterministic ordinary visual state. Java
    /// blockstates rarely provide an empty fallback for age/level/shape, so refusing every variant
    /// would silently discard an otherwise standard model. These are the format's neutral states;
    /// properties Driftwood does own are supplied explicitly by <see cref="InferBlock"/>.
    /// </summary>
    private static bool TryDefaultProperty(string name, out string value)
    {
        value = name switch
        {
            "axis" => "y",
            "facing" => "north",
            "half" or "type" => "bottom",
            "shape" => "straight",
            "hinge" => "left",
            "face" or "attachment" => "floor",
            "age" or "level" or "stage" or "power" or "moisture" => "0",
            "distance" => "1",
            "lit" or "open" or "powered" or "snowy" or "persistent" or "waterlogged"
                or "in_wall" or "unstable" or "triggered" or "occupied" => "false",
            _ => "",
        };
        return value.Length > 0;
    }

    private static string PropertyText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        _ => "",
    };

    private static RawModel? Builtin(string id)
    {
        var colon = id.IndexOf(':');
        var space = colon >= 0 ? id[..colon] : "minecraft";
        var path = colon >= 0 ? id[(colon + 1)..] : id;
        if (!space.Equals("minecraft", StringComparison.OrdinalIgnoreCase)) return null;
        if (path is "builtin/generated" or "item/generated" or "item/handheld"
            or "item/handheld_rod")
            return new RawModel(id, new Dictionary<string, string>(), [], true,
                new Dictionary<string, DisplayTransform>(), [], Generated: true);
        if (path == "block/air")
            return new RawModel(id, new Dictionary<string, string>(), [], true,
                new Dictionary<string, DisplayTransform>(), [], Generated: false);

        var leaf = path.StartsWith("block/", StringComparison.Ordinal)
                   && path.IndexOf('/', "block/".Length) < 0
            ? path["block/".Length..] : "";
        RawElement[]? elements = leaf switch
        {
            "cube_all" => [Cube(new Dictionary<int, string>
            {
                [Faces.PosX] = "#all", [Faces.NegX] = "#all", [Faces.PosY] = "#all",
                [Faces.NegY] = "#all", [Faces.PosZ] = "#all", [Faces.NegZ] = "#all",
            })],
            "cube_column" or "cube_column_horizontal" => [Cube(new Dictionary<int, string>
            {
                [Faces.PosX] = "#side", [Faces.NegX] = "#side", [Faces.PosY] = "#end",
                [Faces.NegY] = "#end", [Faces.PosZ] = "#side", [Faces.NegZ] = "#side",
            })],
            "cube_bottom_top" => [Cube(new Dictionary<int, string>
            {
                [Faces.PosX] = "#side", [Faces.NegX] = "#side", [Faces.PosY] = "#top",
                [Faces.NegY] = "#bottom", [Faces.PosZ] = "#side", [Faces.NegZ] = "#side",
            })],
            "cube" => [Cube(new Dictionary<int, string>
            {
                [Faces.PosX] = "#east", [Faces.NegX] = "#west", [Faces.PosY] = "#up",
                [Faces.NegY] = "#down", [Faces.PosZ] = "#south", [Faces.NegZ] = "#north",
            })],
            "orientable" => [Cube(new Dictionary<int, string>
            {
                [Faces.PosX] = "#side", [Faces.NegX] = "#side", [Faces.PosY] = "#top",
                [Faces.NegY] = "#top", [Faces.PosZ] = "#side", [Faces.NegZ] = "#front",
            })],
            "orientable_with_bottom" => [Cube(new Dictionary<int, string>
            {
                [Faces.PosX] = "#side", [Faces.NegX] = "#side", [Faces.PosY] = "#top",
                [Faces.NegY] = "#bottom", [Faces.PosZ] = "#side", [Faces.NegZ] = "#front",
            })],
            "cross" or "tinted_cross" => Cross("#cross", leaf == "tinted_cross"),
            "block" or "thin_block" => [],
            _ => null,
        };
        return elements is null ? null : new RawModel(id, new Dictionary<string, string>(), elements,
            true, new Dictionary<string, DisplayTransform>(), [], Generated: false);
    }

    private static RawModel? MappedVanillaFallback(string id)
    {
        var colon = id.IndexOf(':');
        var space = colon >= 0 ? id[..colon] : "minecraft";
        var path = colon >= 0 ? id[(colon + 1)..] : id;
        if (!space.Equals("minecraft", StringComparison.OrdinalIgnoreCase)
            || !BlockTextureSet.TryLayerForResource($"{space}:{path}", out _)) return null;

        if (path.StartsWith("block/", StringComparison.OrdinalIgnoreCase))
            return new RawModel(id,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["all"] = $"{space}:{path}" },
                [Cube(new Dictionary<int, string>
                {
                    [Faces.PosX] = "#all", [Faces.NegX] = "#all", [Faces.PosY] = "#all",
                    [Faces.NegY] = "#all", [Faces.PosZ] = "#all", [Faces.NegZ] = "#all",
                })], true, new Dictionary<string, DisplayTransform>(), [], Generated: false);

        if (path.StartsWith("item/", StringComparison.OrdinalIgnoreCase))
            return new RawModel(id,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["layer0"] = $"{space}:{path}" },
                [], true, new Dictionary<string, DisplayTransform>(), [], Generated: true);

        return null;
    }

    private static RawElement Cube(IReadOnlyDictionary<int, string> textures)
    {
        var faces = textures.Select(pair => new RawFace(pair.Key, pair.Value, pair.Key, false, null, 0)).ToArray();
        return new RawElement(Vector3.Zero, new Vector3(16), faces, true, 0, 1, new Vector3(8), false);
    }

    private static RawElement[] Cross(string texture, bool tinted)
    {
        var bothZ = new[]
        {
            new RawFace(Faces.PosZ, texture, -1, tinted, new Vector4(0, 0, 16, 16), 0),
            new RawFace(Faces.NegZ, texture, -1, tinted, new Vector4(16, 0, 0, 16), 0),
        };
        var bothX = new[]
        {
            new RawFace(Faces.PosX, texture, -1, tinted, new Vector4(0, 0, 16, 16), 0),
            new RawFace(Faces.NegX, texture, -1, tinted, new Vector4(16, 0, 0, 16), 0),
        };
        return
        [
            new RawElement(new Vector3(0, 0, 8), new Vector3(16, 16, 8), bothZ,
                true, 45, 1, new Vector3(8), true),
            new RawElement(new Vector3(8, 0, 0), new Vector3(8, 16, 16), bothX,
                true, 45, 1, new Vector3(8), true),
        ];
    }

    private static ModelOverride[] ParseOverrides(JsonElement array, string source)
    {
        if (array.ValueKind != JsonValueKind.Array) return [];
        var result = new List<ModelOverride>();
        foreach (var item in array.EnumerateArray())
        {
            if (!item.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("predicate", out var predicates)
                || predicates.ValueKind != JsonValueKind.Object) continue;
            var values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var predicate in predicates.EnumerateObject())
                if (predicate.Value.TryGetSingle(out var value)) values[predicate.Name] = value;
            result.Add(new ModelOverride(values, model.GetString() ?? ""));
        }
        return [.. result];
    }

    private static DisplayTransform ParseDisplay(JsonElement value, string source)
    {
        if (value.ValueKind != JsonValueKind.Object) return DisplayTransform.Identity;
        return new DisplayTransform(
            value.TryGetProperty("rotation", out var rotation) ? Vector(rotation, source, "display rotation") : Vector3.Zero,
            value.TryGetProperty("translation", out var translation) ? Vector(translation, source, "display translation") : Vector3.Zero,
            value.TryGetProperty("scale", out var scale) ? Vector(scale, source, "display scale") : Vector3.One);
    }

    private byte[]? ReadJson(string id, string category, out string from)
    {
        id = NormaliseId(id, category == "models" ? "block" : "");
        var colon = id.IndexOf(':');
        var space = colon >= 0 ? id[..colon] : "minecraft";
        var path = colon >= 0 ? id[(colon + 1)..] : id;
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) path += ".json";
        var bytes = _pack.TryReadResourceBytes($"{space}:{path}", category, MaxJsonBytes, out from);
        if (bytes is null) return null;
        if (bytes.Length > MaxJsonBytes) throw new InvalidDataException(
            $"{from}: JSON is larger than {MaxJsonBytes / 1024 / 1024} MiB");
        _filesRead++;
        return bytes;
    }

    private bool ModelExists(string id)
    {
        try { return ReadJson(id, "models", out _) is not null; }
        catch (InvalidDataException error) { Report(error.Message); return false; }
    }

    private static JsonDocument Parse(byte[] bytes, string source)
    {
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = MaxDepth * 2,
            });
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"{source}: malformed JSON ({error.Message})", error);
        }
    }

    private static string NormaliseId(string id, string defaultFolder)
    {
        id = id.Replace('\\', '/').Trim();
        var colon = id.IndexOf(':');
        var space = colon >= 0 ? id[..colon] : "minecraft";
        var path = colon >= 0 ? id[(colon + 1)..] : id;
        path = path.TrimStart('/');
        if (defaultFolder.Length > 0 && !path.Contains('/')) path = $"{defaultFolder}/{path}";
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) path = path[..^5];
        if (space.Length == 0 || path.Length == 0 || path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"unsafe resource identifier '{id}'");
        return $"{space}:{path}";
    }

    private static Vector3 Vector(JsonElement value, string source, string label)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3)
            throw new InvalidDataException($"{source}: {label} needs three numbers");
        var values = value.EnumerateArray().Select(element => Number(element, source, label)).ToArray();
        if (values.Any(number => !float.IsFinite(number) || MathF.Abs(number) > 4096))
            throw new InvalidDataException($"{source}: {label} is outside safe model bounds");
        return new Vector3(values[0], values[1], values[2]);
    }

    private static Vector4 Vector4(JsonElement value, string source, string label)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4)
            throw new InvalidDataException($"{source}: {label} needs four numbers");
        var values = value.EnumerateArray().Select(element => Number(element, source, label)).ToArray();
        return new Vector4(values[0], values[1], values[2], values[3]);
    }

    private static float Number(JsonElement owner, string property, float fallback) =>
        owner.TryGetProperty(property, out var value) ? Number(value, property, property) : fallback;

    private static float Number(JsonElement value, string source, string label)
    {
        if (!value.TryGetSingle(out var number) || !float.IsFinite(number))
            throw new InvalidDataException($"{source}: {label} is not a finite number");
        return number;
    }

    private static string? String(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static int Direction(string value) => value switch
    {
        "east" => Faces.PosX,
        "west" => Faces.NegX,
        "up" => Faces.PosY,
        "down" => Faces.NegY,
        "south" => Faces.PosZ,
        "north" => Faces.NegZ,
        _ => -1,
    };

    private static int Axis(string? value) => value switch
    {
        "x" => 0,
        "z" => 2,
        "y" => 1,
        _ => throw new InvalidDataException($"unknown model rotation axis '{value}'"),
    };

    private static int ModQuarter(int degrees)
    {
        if (degrees % 90 != 0) throw new InvalidDataException(
            $"blockstate rotation {degrees} is not a multiple of 90");
        return ((degrees / 90) % 4 + 4) % 4;
    }

    private static IEnumerable<Vector3> BoxCorners(Vector3 from, Vector3 to)
    {
        for (var x = 0; x < 2; x++)
        for (var y = 0; y < 2; y++)
        for (var z = 0; z < 2; z++)
            yield return new Vector3(x == 0 ? from.X : to.X, y == 0 ? from.Y : to.Y,
                z == 0 ? from.Z : to.Z);
    }

    private static Vector3 RotatePoint(Vector3 point, int xTurns, int yTurns) =>
        RotateVector(point - new Vector3(8), xTurns, yTurns) + new Vector3(8);

    private static Vector3 RotateVector(Vector3 point, int xTurns, int yTurns)
    {
        for (var i = 0; i < xTurns; i++) point = new Vector3(point.X, -point.Z, point.Y);
        for (var i = 0; i < yTurns; i++) point = new Vector3(point.Z, point.Y, -point.X);
        return point;
    }

    private static int RotateDirection(int face, int xTurns, int yTurns)
    {
        var normal = Faces.Normals[face];
        var vector = RotateVector(new Vector3(normal.X, normal.Y, normal.Z), xTurns, yTurns);
        for (var i = 0; i < Faces.Count; i++)
        {
            var candidate = Faces.Normals[i];
            if (Vector3.Dot(vector, new Vector3(candidate.X, candidate.Y, candidate.Z)) > 0.99f)
                return i;
        }
        return face;
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var character in value)
            {
                hash ^= character;
                hash *= 16777619;
            }
            return (int)(hash & 0x7fffffff);
        }
    }

    private Resolution Empty(
        bool found,
        string source,
        int issuesAt,
        IReadOnlyDictionary<string, DisplayTransform>? display = null) =>
        new(found, null, null, source,
            display ?? new Dictionary<string, DisplayTransform>(StringComparer.OrdinalIgnoreCase),
            Faults.Skip(issuesAt).ToArray());

    private void Report(string issue)
    {
        if (_reported.Add(issue)) Faults.Add(issue);
    }
}
