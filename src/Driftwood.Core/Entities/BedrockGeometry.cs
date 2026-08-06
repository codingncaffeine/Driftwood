using System.Numerics;
using System.Text.Json;

namespace Driftwood.Core.Entities;

/// <summary>
/// Reads creature skeletons out of the geometry files a Bedrock install ships.
/// </summary>
/// <remarks>
/// <para>⛔ <b>The geometry ships with the GAME, not with texture packs, and that is structural.</b>
/// A resource pack overrides geometry only for entities whose <em>shape</em> it changes, so a pack
/// that repaints every mob in the game carries none at all — measured across three: Intermacgod 0,
/// Really Real 0, Dokucraft 7, four of which are one mob. An installed client carries 274.</para>
/// <para><b>Read from the user's own install at runtime; nothing is extracted and nothing is
/// bundled.</b> Exactly the posture the texture packs already use, and the same one OpenMW, GZDoom
/// and OpenTTD take: the program is ours, the data is theirs and stays on their disk.</para>
/// <para><b>Two formats, both live.</b> The 1.8 files put one model per top-level key named
/// <c>geometry.cow</c>; the 1.12 files put an array under <c>minecraft:geometry</c> with the name in
/// a description block. Bones and cubes are identical inside. Both are in the same install because
/// the versioned overlay packs were written years apart, so a reader that handles one handles about
/// half of what is there.</para>
/// <para>⚠ <b>A real JSON parser, and this is the one that justifies it.</b> The manifest reader
/// deliberately picks two numbers out of a file with string searching, because two numbers do not
/// justify a parser. This is a nested document with arrays of objects of arrays, inheritance between
/// top-level keys, and optional fields at three levels — string searching it would be a bug factory.
/// </para>
/// </remarks>
public static class BedrockGeometry
{
    /// <summary>
    /// Every model in one file. Empty for a file that is not geometry.
    /// </summary>
    /// <remarks>
    /// Tolerant on purpose: a folder of these is read wholesale and one unreadable file must not
    /// cost the other hundred. Faults come back as an out list rather than as exceptions.
    /// </remarks>
    public static List<CreatureModel> Read(string json, List<string>? faults = null)
    {
        var models = new List<CreatureModel>();

        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException error)
        {
            faults?.Add(error.Message);
            return models;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return models;

            // The 1.12 shape: an array, each entry naming itself in a description block.
            if (root.TryGetProperty("minecraft:geometry", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in array.EnumerateArray())
                {
                    var name = entry.TryGetProperty("description", out var description)
                            && description.TryGetProperty("identifier", out var id)
                        ? id.GetString() ?? ""
                        : "";

                    var w = Int(description, "texture_width", 64);
                    var h = Int(description, "texture_height", 64);

                    models.Add(new CreatureModel(Trim(name), w, h, Bones(entry)));
                }

                return models;
            }

            // The 1.8 shape: one model per top-level key. ⚠ A key can carry inheritance as
            // "geometry.child:geometry.parent" — the child names only what it changes and the rest
            // comes from the parent. The parent is very often in ANOTHER FILE, so the pair is only
            // recorded here and resolved once the whole folder is in; see CreatureLibrary.
            foreach (var property in root.EnumerateObject())
            {
                if (!property.Name.StartsWith("geometry.", StringComparison.Ordinal)) continue;
                if (property.Value.ValueKind != JsonValueKind.Object) continue;

                var colon = property.Name.IndexOf(':');
                var name = colon < 0 ? property.Name : property.Name[..colon];

                models.Add(new CreatureModel(
                    Trim(name),
                    Int(property.Value, "texturewidth", 64),
                    Int(property.Value, "textureheight", 64),
                    Bones(property.Value),
                    colon < 0 ? "" : Trim(property.Name[(colon + 1)..])));
            }
        }

        return models;
    }

    /// <summary>
    /// Fills every child model's missing bones in from the parent it names.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>By bone NAME, and the child wins.</b> That is what inheritance means here: a variant
    /// that redraws a head says only "head" and expects to keep the body, the legs and the tail. A
    /// reader that appends instead of overriding gives the creature two heads at slightly different
    /// angles, which draws without complaint.
    /// </remarks>
    public static void ResolveInheritance(List<CreatureModel> models)
    {
        var byName = new Dictionary<string, int>(models.Count);
        for (var i = 0; i < models.Count; i++) byName[models[i].Name] = i;

        for (var i = 0; i < models.Count; i++)
        {
            // Walk the chain rather than one step: a legacy variant inherits a variant that
            // inherits the base, and stopping at the first parent leaves it half a creature.
            var seen = new HashSet<string> { models[i].Name };
            var merged = new List<CreatureBone>(models[i].Bones);
            var at = models[i].Inherits;

            while (at.Length > 0 && seen.Add(at) && byName.TryGetValue(at, out var index))
            {
                foreach (var bone in models[index].Bones)
                    if (!merged.Exists(b => b.Name == bone.Name)) merged.Add(bone);

                at = models[index].Inherits;
            }

            if (merged.Count != models[i].Bones.Length) models[i] = models[i] with { Bones = [.. merged] };
        }
    }

    private static CreatureBone[] Bones(JsonElement owner)
    {
        if (!owner.TryGetProperty("bones", out var bones) || bones.ValueKind != JsonValueKind.Array)
            return [];

        var read = new List<CreatureBone>();

        foreach (var bone in bones.EnumerateArray())
        {
            var mirrorBone = bone.TryGetProperty("mirror", out var bm) && bm.ValueKind == JsonValueKind.True;

            var cubes = new List<CreatureCube>();
            if (bone.TryGetProperty("cubes", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var cube in list.EnumerateArray())
                {
                    // ⚠ uv is a pair on every model in the install, but the format also allows a
                    // per-face object. Taking .GetInt32() on that would throw and lose the file, so
                    // an object uv is read as (0,0) and the net check downstream will say so.
                    var (u, v) = cube.TryGetProperty("uv", out var uv) && uv.ValueKind == JsonValueKind.Array
                        ? (Element(uv, 0, 0), Element(uv, 1, 0))
                        : (0, 0);

                    cubes.Add(new CreatureCube(
                        Vec(cube, "origin"),
                        Vec(cube, "size"),
                        (int)u, (int)v,
                        mirrorBone || (cube.TryGetProperty("mirror", out var cm) && cm.ValueKind == JsonValueKind.True),
                        Float(cube, "inflate", 0f)));
                }
            }

            // ⛔ bind_pose_rotation is where a four-legged animal comes from. A cow's torso is drawn
            // upright and then laid ninety degrees onto its side; drop this and every quadruped in
            // the game stands on its tail.
            //
            // ⛔⛔ AND IT IS NOT THE OLDER SPELLING OF `rotation`, which is what this reader first
            // assumed. They are two fields that do different things and the difference is who else
            // moves. `rotation` turns the bone and everything hanging off it — a hoglin's ears are
            // children of its head and have to come with it. `bind_pose_rotation` lays out this
            // bone's own boxes and reaches nothing below it — measured against a real install, the
            // cow's head and four legs are children of the torso and are authored where they finally
            // stand, so carrying the torso's ninety degrees down to them puts the head under the
            // belly. Sixteen models in that install lay a torso down this way and every one of them
            // is a quadruped, so reading them as one field breaks all sixteen at once.
            //
            // A bone may carry both, and a few do, so both are read rather than one winning.
            read.Add(new CreatureBone(
                bone.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                bone.TryGetProperty("parent", out var p) ? p.GetString() ?? "" : "",
                Vec(bone, "pivot"),
                Vec(bone, "rotation"),
                Vec(bone, "bind_pose_rotation"),
                [.. cubes]));
        }

        return [.. read];
    }

    /// <summary>Drops the "geometry." prefix, which every name in every file carries.</summary>
    private static string Trim(string name) =>
        name.StartsWith("geometry.", StringComparison.Ordinal) ? name["geometry.".Length..] : name;

    /// <summary>
    /// Reads one of each format, and one that is not geometry at all.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Every claim here is one this reader has actually got wrong.</b> The first pass threw on
    /// <c>"texture_width": 64.0</c> and lost ten files including the chicken; it resolved 1.8
    /// inheritance inside a single file and gave every zombie variant nought bones, because the
    /// parent is in another file; and it dropped <c>bind_pose_rotation</c>, which is the ninety
    /// degrees that lays a quadruped's torso down. So the samples are small and each carries one of
    /// those. The last one is the control: a document that is valid JSON and not geometry has to
    /// come back empty and quiet, because a folder of these is read wholesale and half of what is in
    /// it is something else.
    /// </remarks>
    public static List<string> Validate()
    {
        var faults = new List<string>();

        // 1.8: a whole-number-as-float sheet size, a bind pose, and a parent in "another file".
        const string Old = """
            { "format_version": "1.8.0",
              "geometry.beast.v1.8": {
                "texturewidth": 64.0, "textureheight": 32,
                "bones": [ { "name": "body", "pivot": [0,19,2], "bind_pose_rotation": [90,0,0],
                             "cubes": [ {"origin": [-6,11,-5], "size": [12,18,10], "uv": [18,4]} ] } ] } }
            """;

        const string Variant = """
            { "format_version": "1.8.0",
              "geometry.beast.pale.v1.8:geometry.beast.v1.8": {
                "texturewidth": 64, "textureheight": 32,
                "bones": [ { "name": "head", "pivot": [0,20,-8],
                             "cubes": [ {"origin": [-4,16,-14], "size": [8,8,6], "uv": [0,0]} ] } ] } }
            """;

        const string New = """
            { "format_version": "1.12.0",
              "minecraft:geometry": [ { "description": {
                  "identifier": "geometry.newer", "texture_width": 64.0, "texture_height": 64.0 },
                "bones": [ { "name": "body", "parent": "root", "pivot": [0,5,0],
                             "cubes": [ {"origin": [-4,5,-3], "size": [8,6,6], "uv": [0,15]} ] },
                           { "name": "root", "pivot": [0,0,0] } ] } ] }
            """;

        var models = new List<CreatureModel>();
        models.AddRange(Read(Old, faults));
        models.AddRange(Read(Variant, faults));
        models.AddRange(Read(New, faults));

        if (faults.Count > 0) return faults;

        var beast = models.Find(m => m.Name == "beast.v1.8");
        if (beast is null) { faults.Add("the 1.8 model was not read at all"); return faults; }

        if (beast.SheetWidth != 64)
            faults.Add($"a sheet width written 64.0 came back {beast.SheetWidth}");

        if (beast.Bones.Length != 1 || beast.Bones[0].BindPose.X != 90f)
            faults.Add("bind_pose_rotation was dropped, which lays every quadruped on its tail");

        // ⛔ And it landed in the field that reaches nothing below it, rather than in the one that
        // takes the whole skeleton with it. Read into the wrong slot it is not dropped, it is worse:
        // every quadruped's head and legs come round with the torso and end up under the ground.
        if (beast.Bones.Length == 1 && beast.Bones[0].Rotation != Vector3.Zero)
            faults.Add("a bind pose was read as a bone rotation, which carries it down to the children");

        if (beast.CubeCount != 1 || beast.Bones[0].Cubes[0].U != 18)
            faults.Add("the cube's net offset did not survive the read");

        // Inheritance, across what are two files as far as the reader is concerned.
        ResolveInheritance(models);
        var pale = models.Find(m => m.Name == "beast.pale.v1.8");

        if (pale is null || pale.Bones.Length != 2)
            faults.Add($"a variant inheriting across files came back with {pale?.Bones.Length ?? 0} bones, not 2");
        else if (pale.Bones[0].Name != "head")
            faults.Add("the parent's bones overrode the child's instead of filling its gaps");

        var newer = models.Find(m => m.Name == "newer");
        if (newer is null || newer.Bones.Length != 2 || newer.SheetHeight != 64)
            faults.Add("the 1.12 shape did not read");
        else if (newer.Validate().Count > 0)
            faults.Add($"a sound 1.12 model reports a net fault: {newer.Validate()[0]}");

        // ⛔ The control. Anything else in the folder must come back empty and SILENT — a reader that
        // complains about every manifest beside the geometry drowns the one real fault in noise.
        var noise = new List<string>();
        if (Read("""{ "format_version": "1.12.0", "minecraft:client_entity": { "description": {} } }""", noise).Count != 0)
            faults.Add("a document that is not geometry was read as geometry");
        if (noise.Count > 0)
            faults.Add($"a document that is not geometry was reported as a fault: {noise[0]}");

        return faults;
    }

    private static Vector3 Vec(JsonElement owner, string key)
    {
        if (!owner.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array)
            return Vector3.Zero;

        return new Vector3(Element(value, 0, 0f), Element(value, 1, 0f), Element(value, 2, 0f));
    }

    private static float Element(JsonElement array, int index, float fallback)
    {
        var i = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (i++ != index) continue;
            return item.ValueKind == JsonValueKind.Number ? (float)item.GetDouble() : fallback;
        }

        return fallback;
    }

    /// <summary>
    /// A whole number, however it was written.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Read as a double and rounded, never <c>GetInt32</c>.</b> The 1.12 files write
    /// <c>"texture_width": 64.0</c> — a JSON number with a decimal point — and asking for an int
    /// throws, which cost ten files including the chicken on the first run. The exception is not
    /// even wrong: 64.0 is a double. It just is not a reason to lose a model.
    /// </remarks>
    private static int Int(JsonElement owner, string key, int fallback) =>
        owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(value.GetDouble())
            : fallback;

    private static float Float(JsonElement owner, string key, float fallback) =>
        owner.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
            ? (float)value.GetDouble()
            : fallback;
}
