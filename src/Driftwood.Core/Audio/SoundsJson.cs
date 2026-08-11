using System.Text.Json;
using Driftwood.Core.Entities;

namespace Driftwood.Core.Audio;

/// <summary>Bounded Java <c>sounds.json</c> parsing and translation into Driftwood event slots.</summary>
internal static class SoundsJson
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    private const int MaximumEvents = 8_192;
    private const int MaximumSoundsPerEvent = 1_024;
    private const int MaximumReferenceDepth = 32;

    internal readonly record struct Document(string Namespace, byte[] Bytes);
    private readonly record struct Choice(string Name, bool Event, int Weight);

    internal static bool TryDocument(string path, out string soundNamespace)
    {
        soundNamespace = "";
        var parts = path.Replace('\\', '/').Trim('/').Split('/');
        var assets = Array.FindIndex(parts, part => part.Equals("assets", StringComparison.OrdinalIgnoreCase));
        if (assets < 0 || assets + 2 >= parts.Length || assets + 3 != parts.Length
            || !parts[assets + 2].Equals("sounds.json", StringComparison.OrdinalIgnoreCase)) return false;
        soundNamespace = parts[assets + 1].ToLowerInvariant();
        return soundNamespace.Length > 0;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<WeightedSoundEntry>> Resolve(
        IEnumerable<Document> documents,
        IReadOnlyDictionary<string, string> resources)
    {
        var events = new Dictionary<string, List<Choice>>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in documents) ReadDocument(document, events);

        var resolved = new Dictionary<string, IReadOnlyList<WeightedSoundEntry>>(StringComparer.OrdinalIgnoreCase);
        var resolving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<WeightedSoundEntry> Event(string eventId, int depth)
        {
            if (resolved.TryGetValue(eventId, out var ready)) return ready;
            if (depth > MaximumReferenceDepth)
                throw new InvalidDataException($"sounds.json event '{eventId}' exceeds {MaximumReferenceDepth} references");
            if (!resolving.Add(eventId))
                throw new InvalidDataException($"sounds.json event reference cycle reaches '{eventId}'");

            var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (events.TryGetValue(eventId, out var choices))
            {
                foreach (var choice in choices)
                {
                    if (choice.Event)
                    {
                        foreach (var nested in Event(Qualify(choice.Name, NamespaceOf(eventId)), depth + 1))
                            Add(nested.Entry, SaturatingMultiply(choice.Weight, nested.Weight));
                    }
                    else
                    {
                        var resource = Qualify(choice.Name, NamespaceOf(eventId));
                        if (resources.TryGetValue(resource, out var entry)) Add(entry, choice.Weight);
                    }
                }
            }

            resolving.Remove(eventId);
            ready = found.Select(pair => new WeightedSoundEntry(pair.Key, pair.Value)).ToArray();
            resolved[eventId] = ready;
            return ready;

            void Add(string entry, int weight)
            {
                found[entry] = Math.Min(1_000_000,
                    found.GetValueOrDefault(entry) + Math.Clamp(weight, 1, 1_000_000));
            }
        }

        // Resolve every graph, not only events Driftwood happens to name. A cycle or excessive
        // reference chain is malformed pack data and must not hide behind an unused event.
        foreach (var eventId in events.Keys) Event(eventId, 0);

        var aliases = new Dictionary<string, IReadOnlyList<WeightedSoundEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (eventName, targets) in Bindings())
        {
            var choices = Event(Qualify(eventName, "minecraft"), 0);
            if (choices.Count == 0) continue;
            foreach (var target in targets) aliases.TryAdd(target, choices);
        }
        return aliases;
    }

    private static void ReadDocument(Document document, Dictionary<string, List<Choice>> events)
    {
        try
        {
            using var json = JsonDocument.Parse(document.Bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 48,
            });
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("sounds.json must contain an object");

            foreach (var property in json.RootElement.EnumerateObject())
            {
                var id = Qualify(property.Name, document.Namespace);
                if (events.Count >= MaximumEvents && !events.ContainsKey(id))
                    throw new InvalidDataException($"sounds.json contains more than {MaximumEvents:N0} events");
                var replace = false;
                JsonElement sounds;
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    sounds = property.Value;
                }
                else if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    replace = property.Value.TryGetProperty("replace", out var replaceValue)
                              && replaceValue.ValueKind == JsonValueKind.True;
                    if (!property.Value.TryGetProperty("sounds", out sounds))
                        throw new InvalidDataException($"sounds.json event '{id}' has no sounds array");
                }
                else throw new InvalidDataException($"sounds.json event '{id}' is not an object or array");

                if (sounds.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException($"sounds.json event '{id}' has a non-array sounds value");
                var list = replace || !events.TryGetValue(id, out var existing) ? [] : existing;
                var count = 0;
                foreach (var element in sounds.EnumerateArray())
                {
                    if (++count > MaximumSoundsPerEvent)
                        throw new InvalidDataException($"sounds.json event '{id}' contains too many choices");
                    list.Add(ReadChoice(element, id));
                }
                events[id] = list;
            }
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"malformed assets/{document.Namespace}/sounds.json: {error.Message}", error);
        }
    }

    private static Choice ReadChoice(JsonElement element, string eventId)
    {
        if (element.ValueKind == JsonValueKind.String)
            return new Choice(RequiredName(element.GetString(), eventId), false, 1);
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("name", out var nameValue)
            || nameValue.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"sounds.json event '{eventId}' has a choice without a name");

        var name = RequiredName(nameValue.GetString(), eventId);
        var isEvent = element.TryGetProperty("type", out var type)
                      && type.ValueKind == JsonValueKind.String
                      && type.GetString()?.Equals("event", StringComparison.OrdinalIgnoreCase) == true;
        if (type.ValueKind == JsonValueKind.String
            && type.GetString() is { } typed
            && !typed.Equals("event", StringComparison.OrdinalIgnoreCase)
            && !typed.Equals("sound", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"sounds.json event '{eventId}' uses unknown sound type '{typed}'");

        var weight = 1;
        if (element.TryGetProperty("weight", out var weightValue)
            && (!weightValue.TryGetInt32(out weight) || weight is < 1 or > 1_000_000))
            throw new InvalidDataException($"sounds.json event '{eventId}' has an invalid weight");
        return new Choice(name, isEvent, weight);
    }

    private static string RequiredName(string? name, string eventId)
    {
        name = name?.Replace('\\', '/').Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(name) || name.Length > 512 || name.Any(char.IsControl)
            || name.Split('/').Any(part => part is "." or ".."))
            throw new InvalidDataException($"sounds.json event '{eventId}' has an unsafe sound name");
        var extension = Path.GetExtension(name);
        if (extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)) name = name[..^extension.Length];
        return name.ToLowerInvariant();
    }

    private static string Qualify(string name, string fallbackNamespace)
    {
        var at = name.IndexOf(':');
        return at >= 0
            ? $"{name[..at].ToLowerInvariant()}:{name[(at + 1)..].ToLowerInvariant()}"
            : $"{fallbackNamespace.ToLowerInvariant()}:{name.ToLowerInvariant()}";
    }

    private static string NamespaceOf(string id) => id[..Math.Max(0, id.IndexOf(':'))];

    private static int SaturatingMultiply(int left, int right) =>
        (int)Math.Min(1_000_000L, (long)left * right);

    private static IEnumerable<(string Event, IReadOnlyList<string> Targets)> Bindings()
    {
        foreach (var item in MaterialBindings()) yield return item;

        yield return ("block.wooden_door.open", ActionSounds.DoorOpen);
        yield return ("block.wooden_door.close", ActionSounds.DoorClose);
        yield return ("block.chest.open", ActionSounds.ChestOpen);
        yield return ("block.chest.close", ActionSounds.ChestClose);
        yield return ("block.barrel.open", ActionSounds.BarrelOpen);
        yield return ("block.barrel.close", ActionSounds.BarrelClose);
        yield return ("block.furnace.fire_crackle", ActionSounds.FurnaceCrackle);
        yield return ("block.blastfurnace.fire_crackle", ActionSounds.BlastFurnaceCrackle);
        yield return ("block.smoker.smoke", ActionSounds.SmokerCrackle);
        yield return ("block.campfire.crackle", ActionSounds.CampfireCrackle);
        yield return ("item.flintandsteel.use", ActionSounds.FireIgnite);
        yield return ("block.fire.extinguish", ActionSounds.FireOut);
        yield return ("entity.generic.extinguish_fire", ActionSounds.Fizz);
        yield return ("entity.item.break", ActionSounds.ToolBreaks);
        yield return ("entity.player.burp", ActionSounds.Burp);
        yield return ("item.hoe.till", ActionSounds.Till);
        yield return ("block.crop.break", ActionSounds.Harvest);
        yield return ("item.sweet_berries.pick_from_bush", ActionSounds.BerryPick);
        yield return ("block.pumpkin.carve", ActionSounds.PumpkinCarve);
        yield return ("item.bucket.fill", ActionSounds.BucketFillWater);
        yield return ("item.bucket.empty", ActionSounds.BucketEmptyWater);
        yield return ("item.bucket.fill_lava", ActionSounds.BucketFillLava);
        yield return ("item.bucket.empty_lava", ActionSounds.BucketEmptyLava);
        yield return ("block.anvil.use", ActionSounds.AnvilUse);
        yield return ("block.composter.fill", ActionSounds.ComposterFill);
        yield return ("block.composter.fill_success", ActionSounds.ComposterRaise);
        yield return ("block.composter.ready", ActionSounds.ComposterReady);
        yield return ("block.composter.empty", ActionSounds.ComposterEmpty);
        yield return ("entity.player.small_fall", ActionSounds.FallSmall);
        yield return ("entity.player.big_fall", ActionSounds.FallBig);
        yield return ("entity.player.hurt_drown", ActionSounds.DrownGasp);
        yield return ("entity.player.hurt_on_fire", ActionSounds.BurnHurt);
        yield return ("block.bubble_column.bubble_pop", ActionSounds.BubblePop);
        yield return ("ambient.underwater.enter", ActionSounds.Submerge);
        yield return ("ambient.underwater.exit", ActionSounds.Surface);
        yield return ("block.ladder.step", ActionSounds.LadderStep);
        yield return ("entity.player.swim", ActionSounds.SwimStroke);
        yield return ("entity.item.pickup", ActionSounds.Pickup);
        yield return ("ui.button.click", ActionSounds.Click);
        yield return ("ui.toast.in", ActionSounds.ToastIn);
        yield return ("ui.toast.out", ActionSounds.ToastOut);
        yield return ("block.lava.pop", ActionSounds.LavaPop);
        yield return ("ambient.cave", ActionSounds.CaveAmbience);
        yield return ("block.lava.ambient", ActionSounds.LavaAmbience);

        yield return ("entity.cow.ambient", CreatureSounds.VoicesFor("cow"));
        yield return ("entity.cow.hurt", CreatureSounds.HurtFor("cow"));
        yield return ("entity.pig.ambient", CreatureSounds.VoicesFor("pig"));
        yield return ("entity.pig.death", CreatureSounds.DeathCryFor("pig"));
        yield return ("entity.sheep.ambient", CreatureSounds.VoicesFor("sheep"));
        yield return ("entity.chicken.ambient", CreatureSounds.VoicesFor("chicken"));
        yield return ("entity.chicken.hurt", CreatureSounds.HurtFor("chicken"));
        yield return ("entity.chicken.egg", CreatureSounds.ShedFor("chicken"));
        yield return ("entity.frog.ambient", CreatureSounds.VoicesFor("frog"));
        yield return ("entity.wolf.ambient", CreatureSounds.VoicesFor("wolf"));
        yield return ("entity.wolf.hurt", CreatureSounds.HurtFor("wolf"));
        yield return ("entity.wolf.growl", CreatureSounds.AngryFor("wolf"));
        yield return ("entity.wolf.death", CreatureSounds.DeathCryFor("wolf"));
        yield return ("entity.bat.ambient", CreatureSounds.VoicesFor("bat"));
        yield return ("entity.spider.ambient", CreatureSounds.VoicesFor("spider"));
        yield return ("entity.spider.hurt", CreatureSounds.AngryFor("spider"));
        yield return ("entity.zombie.ambient", CreatureSounds.VoicesFor("zombie"));
        yield return ("entity.creeper.primed", CreatureSounds.Fuses);
        yield return ("entity.generic.explode", CreatureSounds.Explosions);
        yield return ("entity.ender_pearl.throw", CreatureSounds.Blinks);
        yield return ("entity.sheep.shear", CreatureSounds.Shears);
        yield return ("entity.generic.eat", CreatureSounds.Meals);
    }

    private static IEnumerable<(string Event, IReadOnlyList<string> Targets)> MaterialBindings()
    {
        var stems = new Dictionary<SoundMaterial, string>
        {
            [SoundMaterial.Stone] = "stone", [SoundMaterial.Deepstone] = "deepslate",
            [SoundMaterial.Dirt] = "rooted_dirt", [SoundMaterial.Grass] = "grass",
            [SoundMaterial.Sand] = "sand", [SoundMaterial.Gravel] = "gravel",
            [SoundMaterial.Snow] = "snow", [SoundMaterial.Wood] = "wood",
            [SoundMaterial.Leaves] = "azalea_leaves", [SoundMaterial.Plant] = "crop",
            [SoundMaterial.BerryBush] = "sweet_berry_bush", [SoundMaterial.Cobweb] = "cobweb",
            [SoundMaterial.Metal] = "metal", [SoundMaterial.Glass] = "glass",
            [SoundMaterial.Cloth] = "wool",
        };
        foreach (var (material, stem) in stems)
        foreach (var soundEvent in Enum.GetValues<SoundEvent>())
            yield return ($"block.{stem}.{soundEvent.ToString().ToLowerInvariant()}",
                MaterialSounds.For(material, soundEvent));

        yield return ("entity.player.swim", MaterialSounds.For(SoundMaterial.Water, SoundEvent.Step));
        yield return ("entity.generic.splash", MaterialSounds.For(SoundMaterial.Water, SoundEvent.Hit));
    }
}
