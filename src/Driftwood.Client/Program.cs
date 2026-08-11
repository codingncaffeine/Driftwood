using Driftwood.Client.Audio;
using Driftwood.Client.Platform;
using Driftwood.Client.Render;
using Driftwood.Core.Audio;
using Driftwood.Core.Blocks;
using Driftwood.Core.Diagnostics;
using Driftwood.Core.Entities;
using Driftwood.Core.Gen;
using Driftwood.Core.Items;
using Driftwood.Core.Textures;

namespace Driftwood.Client;

public static class Program
{
    private static readonly string ProductVersion =
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    /// <summary>
    /// Long enough that the flight ends further from its start than the streaming drop radius, so
    /// the whole loaded set turns over at least once.
    /// </summary>
    private const int DefaultBenchSeconds = 15;

    public static int Main(string[] args)
    {
        var commandLine = ProcessConsole.Prepare(args);

        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        if (args.Contains("--version"))
        {
            Console.WriteLine($"Driftwood v{ProductVersion}");
            return 0;
        }

        try
        {
            var options = ParseArgs(args);

            if (args.Contains("--audit"))
            {
                var result = WorldAudit.Run(options.Seed, options.ChunksAcross, options.OceanCoverage);
                Console.WriteLine(result.Report);
                return result.Passed ? 0 : 1;
            }

            if (args.Contains("--audio-check"))
            {
                var at = Array.IndexOf(args, "--audio-check");
                var soundPack = at + 1 < args.Length && !args[at + 1].StartsWith('-') ? args[at + 1] : null;
                return AudioCheck(soundPack);
            }

            if (args.Contains("--controller-check"))
                return ControllerCheck();

            if (args.Contains("--pack-matrix"))
            {
                var at = Array.IndexOf(args, "--pack-matrix");
                if (at + 1 >= args.Length || args[at + 1].StartsWith('-'))
                    throw new ArgumentException("--pack-matrix needs an ignored corpus folder");
                var matrix = PackMatrix.Build(args[at + 1]);
                Console.WriteLine(matrix.Report);
                return matrix.Passed ? 0 : 1;
            }

            // ⛳ THE INSTRUMENT THIS PROJECT WAS MISSING, AND THE ONE IT NEEDED MOST. Every tile in
            // the game is drawn in code, and until now the only way to see one was to start the game
            // and look at a square the size of a fingernail. Three separate redraws of the tools
            // went out on guesses because of that, and the user had to be the eye each time.
            //
            // A sheet of them, magnified, in a file anybody — or anything — can open.
            if (args.Contains("--icon-sheet"))
            {
                var at = Array.IndexOf(args, "--icon-sheet");
                var path = at + 1 < args.Length ? args[at + 1] : "icons.png";
                return IconSheet(path);
            }

            // Which of our own layers a pack actually supplied, and which kept ours. The count in
            // the startup line says how many and never which, and "is the thing I am looking at one
            // of them" is the only question anybody has when a pack looks like it did nothing.
            // ⛳ The shelf, without opening a window. Every other feature in this project has a way
            // to be exercised headlessly and the importer had none — which matters more here than
            // most, because what it is actually being asked to do is read files somebody else made
            // in formats nobody documented. Real packs are the only test that means anything.
            if (args.Contains("--packs"))
            {
                if (!string.IsNullOrWhiteSpace(options.PackPath))
                {
                    var added = PackLibrary.Install(options.PackPath, out var why);
                    Console.WriteLine(added is { } entry
                        ? $"added       {entry.Name}  ({entry.Kind})"
                        : $"refused     {options.PackPath}  ({why})");
                }

                var shelf = PackLibrary.List();
                Console.WriteLine($"shelf       {PackLibrary.Folder}");
                Console.WriteLine(
                    shelf.Count == 0 ? "            nothing on it" : $"            {shelf.Count} installed");

                foreach (var pack in shelf)
                    Console.WriteLine($"  {(pack.Readable ? " " : "!")} {pack.Name,-44} {pack.Kind}");

                return shelf.Any(p => !p.Readable) ? 1 : 0;
            }

            // ⛳ EVERY RECIPE, WHERE IT IS REALLY MADE, AND WHAT IT REALLY COSTS. Asked for after a
            // user report that reads as a bug and is not one: a blast furnace wants a furnace in the
            // grid, which means breaking the one already built, and nothing anywhere said so. The
            // list is the small half; the findings under it are the point.
            if (args.Contains("--recipes"))
            {
                var blocks = new BlockRegistry();
                StarterBlocks.Register(blocks);

                var items = StarterItems.Register(blocks);
                var book = StarterRecipes.Build(items);
                var blockDrops = StarterItems.Drops(blocks, items);
                var creatureDrops = StarterItems.Creatures(items);

                Console.WriteLine(
                    $"recipes     {book.Recipes.Count} recipes and {book.Smelting.Count} smelts "
                    + $"over {items.Count} items");

                Console.Write(RecipeReport.Build(blocks, items, book, blockDrops, creatureDrops, out var found));

                Console.WriteLine();
                Console.WriteLine(found.Count == 0
                    ? "findings    none"
                    : $"findings    {found.Count}");

                foreach (var group in found.GroupBy(f => f.Kind))
                {
                    Console.WriteLine($"  {group.Key}  ({group.Count()})");
                    foreach (var finding in group)
                        Console.WriteLine($"    {finding.Recipe,-28} {finding.What}");
                }

                return 0;
            }

            if (args.Contains("--pack-report"))
            {
                Console.WriteLine(
                    BlockTextureSet.Build(options.PackPath, options.TextureSize, 4096).Report());
                return 0;
            }

            // ⛔ THE INSTRUMENT THAT SAYS WHAT IS ON A 2012 GRID, and it exists because the
            // alternative is transcribing a well-known table from memory. A cell holds whatever it
            // holds; that is a fact about the image and it is measurable. One candidate table
            // checked this way already read grey where diamond was expected.
            if (args.Contains("--atlas"))
            {
                if (string.IsNullOrWhiteSpace(options.PackPath))
                {
                    Console.Error.WriteLine("driftwood: --atlas needs --pack <folder or .zip>");
                    return 1;
                }

                return Atlas(options.PackPath);
            }

            // ⛔ THE ANSWER TO "CAN A PACK'S ANIMALS EVER WORK". A creature is two halves from two
            // different places: the skeleton ships with the GAME (a resource pack only overrides
            // shapes it changes, so a pack that repaints every mob carries no geometry at all) and
            // the skin ships with the PACK. Either can be missing and the two failures look
            // identical once something is on screen, so this asks for both, per creature, by name.
            if (args.Contains("--creatures"))
            {
                var at = Array.IndexOf(args, "--creatures");
                var given = at + 1 < args.Length && !args[at + 1].StartsWith('-') ? args[at + 1] : null;
                return Creatures(given, options);
            }

            if (args.Contains("--pack-coverage"))
            {
                if (string.IsNullOrWhiteSpace(options.PackPath))
                {
                    Console.Error.WriteLine(
                        "driftwood: --pack-coverage needs --pack <folder, .zip, .mcpack or .mcaddon>");
                    return 1;
                }

                Console.WriteLine(PackCoverage.Report(options.PackPath));
                return 0;
            }

            using var host = new ClientHost(options);
            return host.Run();
        }
        catch (Exception ex)
        {
            ProcessConsole.ReportFailure(ex, commandLine);
            return 1;
        }
    }

    /// <summary>
    /// Opens the audio device, resolves every sound the block table names, and plays nothing.
    /// </summary>
    /// <remarks>
    /// Sound is the one thing in this project that cannot be checked here, and it is checked here
    /// anyway — everything except whether it is the right noise. Whether a device opens, whether
    /// every material resolves, whether every file named exists and decodes to something audible
    /// rather than to silence: all of that is answerable, and all of it is what actually goes
    /// wrong. Deliberately silent, because making noise on somebody else's machine to prove a
    /// speaker works is not a test anybody asked for.
    /// </remarks>
    /// <summary>
    /// Writes every tool tile to one magnified sheet, so a drawing can be looked at.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>The instrument this project did not have and needed most.</b> Every tile in the game is
    /// drawn in code, and the only way to see one was to start the game and squint at a square the
    /// size of a fingernail — so three redraws of the tools went out on guesses and the user had to
    /// be the eye every time. Nearest-neighbour, so a pixel stays a pixel and what comes out is the
    /// drawing rather than a blur of it.
    /// </remarks>
    /// <summary>
    /// Every tile the game ships, blown up and laid out in a grid, for somebody to look at.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>It was four tool shapes and is now the whole array.</b> Four was enough while the only
    /// question anybody had was whether an axe and a shovel were the same drawing; a content phase
    /// adds a dozen tiles at a time and the question becomes whether any two of a hundred and twenty
    /// can be told apart in a slot. Ours rather than a pack's on purpose — this is the picture of what
    /// is in the box, which is the only art most players will ever see.
    /// </remarks>
    private static int IconSheet(string path)
    {
        const int Zoom = 6;
        const int Gap = 2;
        const int Across = 16;

        var tiles = new List<byte[]>();
        for (var layer = 0; layer < StarterBlocks.LayerCount; layer++)
            tiles.Add(BlockTextureSet.OwnTile(layer));

        var cell = TileGen.Size * Zoom + Gap;
        var down = (tiles.Count + Across - 1) / Across;
        var width = cell * Across;
        var height = cell * down;
        var pixels = new byte[width * height * 4];

        // A mid grey behind them: a tool drawn on nothing cannot be told from a tool with a hole in
        // it, and the outline is the whole point of the drawing.
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 90;
            pixels[i + 1] = 90;
            pixels[i + 2] = 96;
            pixels[i + 3] = 255;
        }

        for (var t = 0; t < tiles.Count; t++)
        for (var y = 0; y < TileGen.Size * Zoom; y++)
        for (var x = 0; x < TileGen.Size * Zoom; x++)
        {
            var from = ((y / Zoom) * TileGen.Size + x / Zoom) * 4;
            if (tiles[t][from + 3] < 128) continue;

            var to = ((t / Across * cell + y) * width + t % Across * cell + x) * 4;
            pixels[to] = tiles[t][from];
            pixels[to + 1] = tiles[t][from + 1];
            pixels[to + 2] = tiles[t][from + 2];
            pixels[to + 3] = 255;
        }

        File.WriteAllBytes(path, Png.Encode(new Image(width, height, pixels)));

        Console.WriteLine(
            $"icons       {tiles.Count} layers at {Zoom}x, {Across} across, written to {path}");
        // ⚠ Four groups now, not three. The fluids, the buckets and the two particle tiles were
        // appended past the tools — because this array's order IS the layer numbering and inserting
        // one beside water would move eighty-five constants — so a report that stopped at "tools"
        // was calling a wisp of smoke a pickaxe.
        Console.WriteLine(
            $"            faces 0-{StarterBlocks.LayerFirstIcon - 1}, "
            + $"items {StarterBlocks.LayerFirstIcon}-{StarterBlocks.LayerFirstTool - 1}, "
            + $"tools {StarterBlocks.LayerFirstTool}-{StarterBlocks.LayerFirstFluid - 1}, "
            + $"fluids and fire {StarterBlocks.LayerFirstFluid}-{StarterBlocks.LayerCount - 1}");

        return 0;
    }

    /// <summary>
    /// Reads creature skeletons off the user's own install and matches them to ours.
    /// </summary>
    /// <remarks>
    /// ⛳ The instrument #29 was blocked on, and the reason it was blocked is worth keeping: a
    /// creature model cannot be recovered from a texture. A box of w×h×d unwraps to a net 2d+2w wide
    /// and d+h tall — two equations, three unknowns — so one net is six different boxes, and on real
    /// sheets the nets abut with no gutter anyway (a pig's and a creeper's are each a single
    /// connected region). Nothing in the picture says where a head sits or where a leg pivots. The
    /// numbers exist, in the game's own geometry files, and this is what reads them.
    /// </remarks>
    /// <summary>
    /// Prints what is on a pre-1.6 pack's grid, cell by cell.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Mean colour and how much of the cell is opaque</b>, which between them identify most of
    /// the sheet: gold block is the most distinctive cell on it, bedrock is near-black, sand is pale,
    /// and a cutout — a sapling, a flower, a torch — is a cell that is mostly clear. The eye that
    /// reads this output is mine, once, to write the index table; the table is then in the code and
    /// this stays as the thing that checks it against the next pack somebody hands us.
    /// </remarks>
    private static int Atlas(string packPath)
    {
        using var pack = TexturePack.Open(packPath);
        if (pack is null)
        {
            Console.Error.WriteLine($"driftwood: nothing readable at {packPath}");
            return 1;
        }

        Console.WriteLine($"atlas       {pack.Name}: {pack.Dialect}, cells of {pack.AtlasTileSize}px");

        if (pack.Dialect != PackDialect.Atlas)
        {
            Console.Error.WriteLine("driftwood: that pack has no terrain.png at its root");
            return 1;
        }

        foreach (var items in (ReadOnlySpan<bool>)[false, true])
        {
            var census = pack.AtlasCensus(items);
            var any = false;
            foreach (var cell in census) any |= cell.Opaque > 0;

            Console.WriteLine();
            Console.WriteLine(items ? "gui/items.png" : "terrain.png");

            if (!any)
            {
                Console.WriteLine("  (not in this pack)");
                continue;
            }

            for (var row = 0; row < TexturePack.AtlasCells; row++)
            {
                var line = new System.Text.StringBuilder($"  row {row,2} ");
                for (var column = 0; column < TexturePack.AtlasCells; column++)
                {
                    var index = row * TexturePack.AtlasCells + column;
                    var (r, g, b, opaque) = census[index];
                    line.Append($"{index,3}:{r,3},{g,3},{b,3}@{opaque,3}%  ");
                }

                Console.WriteLine(line.ToString());
            }
        }

        return 0;
    }

    private static int Creatures(string? geometryPath, ClientOptions options)
    {
        var root = geometryPath ?? CreatureLibrary.FindInstalledGeometry();

        if (root is null)
        {
            Console.Error.WriteLine(
                "driftwood: no geometry found. Give a folder of .geo.json files after --creatures, "
                + "or install the Bedrock client, which ships them."
                + (CreatureLibrary.LastLookupNote.Length > 0
                    ? $"{Environment.NewLine}            ({CreatureLibrary.LastLookupNote})"
                    : ""));
            return 1;
        }

        var faults = new List<string>();
        var models = CreatureLibrary.ReadFolder(root, faults);

        Console.WriteLine($"geometry    {root}");

        using var pack = string.IsNullOrWhiteSpace(options.PackPath)
            ? null
            : TexturePack.Open(options.PackPath);

        if (pack is not null)
            Console.WriteLine($"pack        {pack.Name} ({pack.Dialect.ToString().ToLowerInvariant()})");

        var resolved = CreatureLibrary.Resolve(models, pack);

        Console.WriteLine();
        Console.WriteLine(CreatureLibrary.Report(models, resolved));

        // ⚠ Unreadable files are named rather than counted. One bad file among three hundred is
        // normal and harmless; the same one bad file being the cow is not, and a count cannot tell
        // those apart.
        if (faults.Count > 0)
        {
            Console.WriteLine($"{faults.Count} files could not be read:");
            foreach (var fault in faults.Take(8)) Console.WriteLine($"  {fault}");
        }

        return 0;
    }

    private static int AudioCheck(string? soundPack)
    {
        var registry = new BlockRegistry();
        StarterBlocks.Register(registry);
        registry.Seal();

        var root = SoundLibrary.FindRoot();
        var library = new SoundLibrary(root, soundPack);

        Console.WriteLine($"root        {root}");
        Console.WriteLine($"pack        {(soundPack is null ? "none — local fallback only" : soundPack)}");
        Console.WriteLine($"indexed     {library.Count} clips");

        using var engine = new AudioEngine(library);
        Console.WriteLine($"device      {engine.Summary}");
        Console.WriteLine();

        var faults = new List<string>();
        double shortest = double.MaxValue, longest = 0;

        if (soundPack is null)
        {
            Console.WriteLine("Driftwood's local fallback");
            foreach (var name in SoundLibrary.BuiltInNames.Order(StringComparer.Ordinal))
            {
                var clip = library.Load(name);
                if (clip is null)
                {
                    Console.WriteLine($"  {name,-32} MISSING");
                    faults.Add($"{name} is missing or would not decode");
                    continue;
                }

                shortest = Math.Min(shortest, clip.Seconds);
                longest = Math.Max(longest, clip.Seconds);
                Console.WriteLine(
                    $"  {name,-32} {clip.Seconds,6:F2}s  {clip.Channels}ch {clip.SampleRate}Hz  peak {clip.Peak:F2}");
                if (clip.ToMono().Peak < 0.02f) faults.Add($"{name} plays as near silence");
                if (clip.Seconds > 8f) faults.Add($"{name} is {clip.Seconds:F1}s, which is not a one-shot");
            }

            Console.WriteLine(
                $"optional    {SoundPackArchive.RequiredNames.Count} named slots wait for a downloaded sound pack");
        }
        else
        {
            Console.WriteLine("clips the block table names");
            foreach (var name in MaterialSounds.AllNames().Order(StringComparer.Ordinal))
            {
                Gate(name, 8f, 32);
            }

            // The animals, actions and ambience are measured after the mono fold, which is what the
            // positional engine actually uploads.
            Console.WriteLine(
                "\nclips the creatures name");
            foreach (var name in CreatureSounds.All.Distinct().Order(StringComparer.Ordinal))
            {
                Gate(name, 8f, 32);
            }

            Console.WriteLine("\nclips the actions name");
            foreach (var (name, allowed) in ActionSounds.AllOneShots.Select(n => (n, 8f))
                         .Concat(ActionSounds.Ambience.Distinct().Select(n => (n, 60f)))
                         .OrderBy(pair => pair.Item1, StringComparer.Ordinal))
                Gate(name, allowed, 44);
        }

        void Gate(string name, float allowed, int width)
        {
            var clip = library.Load(name);
            if (clip is null)
            {
                Console.WriteLine($"  {name.PadRight(width)} MISSING");
                faults.Add($"{name} is missing or would not decode");
                return;
            }

            var played = clip.ToMono();
            shortest = Math.Min(shortest, clip.Seconds);
            longest = Math.Max(longest, clip.Seconds);
            Console.WriteLine(
                $"  {name.PadRight(width)} {clip.Seconds,6:F2}s  {clip.Channels}ch {clip.SampleRate}Hz  peak {played.Peak:F2}");
            if (played.Peak < 0.02f)
                faults.Add($"{name} plays as near silence (peak {played.Peak:F3} after the fold to mono)");
            if (clip.Seconds > allowed)
                faults.Add($"{name} is {clip.Seconds:F1}s against the {allowed:F0}s its table allows");
        }

        // And then the whole selected stack, including pack clips not named by a table. This proves
        // every recording present can be decoded by this machine's own decoder.
        Console.WriteLine();
        var swept = 0;
        var sweepFaults = 0;
        var totalSeconds = 0.0;
        foreach (var key in library.AllKeys)
        {
            var clip = library.Load(key);
            swept++;
            if (clip is null) sweepFaults++;
            else totalSeconds += clip.Seconds;
        }
        Console.WriteLine($"shelf       {swept} files decode to {totalSeconds / 60:F1} minutes, {sweepFaults} refusing");
        if (sweepFaults > 0) faults.Add($"{sweepFaults} file(s) on the shelf would not decode");

        // The transform itself, against the specification's own formula.
        var imdct = Driftwood.Core.Audio.OggVorbis.ImdctSelfTest();
        Console.WriteLine($"imdct       {(imdct is null ? "fast path matches the spec formula at every block size" : imdct)}");
        if (imdct is not null) faults.Add(imdct);

        Console.WriteLine();
        Console.WriteLine("materials");
        foreach (var material in MaterialSounds.Materials.Order())
        {
            var counts = new List<string>();
            foreach (var which in Enum.GetValues<SoundEvent>())
            {
                var names = MaterialSounds.For(material, which);
                counts.Add($"{which.ToString().ToLowerInvariant()} {names.Count}");
                if (names.Count == 0) faults.Add($"{material} has no {which} sound");
            }

            Console.WriteLine($"  {material,-8} {string.Join(", ", counts)}");
        }

        var uncovered = new List<string>();
        for (ushort id = 1; id < registry.Count; id++)
            if (MaterialSounds.For(registry[id].Sounds, SoundEvent.Break).Count == 0)
                uncovered.Add(registry[id].Name);

        Console.WriteLine();
        Console.WriteLine($"blocks      {registry.Count - 1} registered, {uncovered.Count} without a break sound");
        Console.WriteLine($"lengths     {shortest:F2}s to {longest:F2}s");
        foreach (var fault in library.Faults) faults.Add(fault);

        Console.WriteLine();
        if (faults.Count == 0)
        {
            Console.WriteLine(soundPack is null
                ? "OK  every owned fallback clip decodes; optional pack slots are structurally valid"
                : "OK  every named slot resolves through the pack or fallback, every clip decodes, nothing is silent");
            return 0;
        }

        foreach (var fault in faults) Console.WriteLine($"FAULT  {fault}");
        return 1;
    }

    /// <summary>Loads the shipped SDL native binary, enumerates safely, and requires no hardware.</summary>
    private static int ControllerCheck()
    {
        var faults = ControllerInput.SelfTest(out var interop);
        faults.AddRange(FlyCamera.ControllerFaults());
        Console.WriteLine($"interop     {interop}");

        using var input = new ControllerInput();
        input.Start();
        input.Update();

        Console.WriteLine($"provider    {input.Provider}");
        Console.WriteLine($"scan        {input.ScanMilliseconds:F0} ms");
        Console.WriteLine($"connected   {input.ConnectedCount}");
        Console.WriteLine($"active      {input.ActiveName}");
        if (input.Fault.Length > 0) Console.WriteLine($"note        {input.Fault}");

        while (input.TryTakeNotice(out var notice))
            Console.WriteLine($"device      {notice.Name} ({notice.Provider})");

        // The fallback is for a player's damaged or unavailable SDL installation. The release
        // check is stricter: this Windows artifact claims to contain SDL, so silently reaching
        // XInput here means single-file publishing dropped the native dependency.
        if (input.Provider != "SDL3")
            faults.Add($"the published controller provider is {input.Provider}, not its bundled SDL3");

        foreach (var fault in faults) Console.WriteLine($"FAULT       {fault}");
        Console.WriteLine(faults.Count == 0
            ? "OK          SDL3 loaded; controller hardware is optional"
            : $"FAILED      {faults.Count} controller checks");
        return faults.Count == 0 ? 0 : 1;
    }

    private static ClientOptions ParseArgs(string[] args)
    {
        var options = new ClientOptions();
        var seedGiven = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seed":
                    var typed = Next(args, ref i, "--seed");
                    options = options with
                    {
                        Seed = WorldSeed.Parse(typed),
                        SeedText = typed,
                        SeedGiven = true,
                    };
                    seedGiven = true;
                    break;
                case "--world":
                    options = options with { WorldName = Next(args, ref i, "--world") };
                    break;
                case "--chunks":
                    options = options with
                    {
                        ChunksAcross = ParseInt(Next(args, ref i, "--chunks"), 2, 64),
                        ChunksGiven = true,
                    };
                    break;
                case "--ocean":
                    options = options with { OceanCoverage = ParseInt(Next(args, ref i, "--ocean"), 0, 90) / 100f };
                    break;
                case "--width":
                    options = options with { Width = ParseInt(Next(args, ref i, "--width"), 320, 7680) };
                    break;
                case "--height":
                    options = options with { Height = ParseInt(Next(args, ref i, "--height"), 240, 4320) };
                    break;
                case "--vsync":
                    options = options with { VSync = true };
                    break;
                case "--pack":
                    options = options with { PackPath = Next(args, ref i, "--pack") };
                    break;
                case "--texture-size":
                    // Clamped again at load against what the card and the memory budget allow.
                    // Omit it and the pack's own resolution is used, which is almost always right.
                    options = options with { TextureSize = ParseInt(Next(args, ref i, "--texture-size"), 16, 4096) };
                    break;
                case "--skin":
                    options = options with { SkinPath = Next(args, ref i, "--skin") };
                    break;
                // ⛔ Given once and remembered. The skeletons ship with an installed Bedrock client
                // rather than with us or with a pack, and that install cannot be found by looking —
                // enumerating WindowsApps throws for a plain process even where a known path under
                // it opens. So the folder is said once and kept in the settings file.
                case "--creature-geometry":
                    options = options with { CreatureGeometry = Next(args, ref i, "--creature-geometry") };
                    break;
                case "--skin-model":
                    options = options with { Arms = ParseArms(Next(args, ref i, "--skin-model")) };
                    break;
                case "--bench":
                    // Seconds of flight, not frames: the path is flown at a fixed speed so the
                    // streamer meets the same pressure whatever the frame rate turns out to be.
                    options = options with
                    {
                        BenchSeconds = TryTakeInt(args, ref i, out var seconds) ? Math.Clamp(seconds, 1, 600) : DefaultBenchSeconds,
                    };
                    break;
                case "--play":
                    options = options with
                    {
                        PlaySeconds = Math.Clamp(ParseInt(Next(args, ref i, "--play"), 1, 3600), 1, 3600),
                    };
                    break;
                case "--time":
                    // Hours, because "start at 19" is a thing anyone can say and 0.79 is not.
                    options = options with { StartTime = ParseInt(Next(args, ref i, "--time"), 0, 23) / 24f };
                    break;
                case "--daylength":
                    options = options with { DayLength = ParseInt(Next(args, ref i, "--daylength"), 10, 86400) };
                    break;
                case "--uploads":
                    options = options with { MaxUploadsPerFrame = ParseInt(Next(args, ref i, "--uploads"), 1, 4096) };
                    break;
                case "--stall":
                    options = options with { StallMs = ParseInt(Next(args, ref i, "--stall"), 0, 1000) };
                    break;
                case "--mute":
                    options = options with { Mute = true };
                    break;
                case "--ui-check":
                    options = options with { UiCheck = true, Mute = true };
                    break;
                case "--shot":
                    options = options with { ShotPath = Next(args, ref i, "--shot"), Mute = true };
                    break;
                // Takes a path, so the parser has to step over it as well as allow it.
                case "--icon-sheet":
                    i++;
                    break;
                // Takes an optional path, so step over it only when one is actually there.
                case "--creatures":
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) i++;
                    break;
                case "--audio-check":
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) i++;
                    break;
                case "--pack-matrix":
                    i++;
                    break;
                case "--audit":
                case "--version":
                case "--controller-check":
                case "--pack-coverage":
                case "--pack-report":
                case "--packs":
                case "--atlas":
                case "--recipes":
                    break;   // handled in Main; listed here so they are not unknown arguments
                default:
                    throw new ArgumentException($"unknown argument '{args[i]}' (try --help)");
            }
        }

        // A benchmark on a random world compares nothing to nothing. Anything unseeded gets pinned
        // so two runs of the same build are the same test.
        if (options.BenchSeconds > 0 && !seedGiven)
            options = options with { Seed = WorldSeed.Parse("driftwood") };

        return options;
    }

    /// <summary>Consumes the next argument as an integer if there is one and it looks like a number.</summary>
    private static bool TryTakeInt(string[] args, ref int i, out int value)
    {
        value = 0;
        if (i + 1 >= args.Length) return false;
        if (!int.TryParse(args[i + 1], out value)) return false;
        i++;
        return true;
    }

    private static string Next(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{flag} needs a value");
        return args[++i];
    }

    private static int ParseInt(string text, int min, int max)
    {
        if (!int.TryParse(text, out var value))
            throw new ArgumentException($"'{text}' is not a number");
        return Math.Clamp(value, min, max);
    }

    /// <summary>
    /// Arm width, when the sheet is not to be trusted about it.
    /// </summary>
    /// <remarks>
    /// Detection reads the sheet for texels only a four-wide arm can use, which is the only signal
    /// a bare PNG carries — the real answer lives in account metadata that never comes with the
    /// file. It is right on everything drawn by a normal editor and wrong on a sheet that filled
    /// its unused columns in, so the override exists rather than the player having to repaint.
    /// </remarks>
    private static ArmStyle ParseArms(string text) => text.ToLowerInvariant() switch
    {
        "classic" or "wide" or "4" => ArmStyle.Classic,
        "slim" or "alex" or "3" => ArmStyle.Slim,
        _ => throw new ArgumentException($"--skin-model wants 'classic' or 'slim', not '{text}'"),
    };

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Driftwood

              --world <name>    which world to open, and the name its save takes. Omit it and the
                                seed names the world, so the same --seed always comes back to the
                                same world and no seed at all comes back to "world". A world that
                                already exists brings its own seed, which beats --seed.
              --seed <text>     world seed; digits are literal, words are hashed, omit for random
              --chunks <n>      how far the world stays loaded, in chunks across; overrides the
                                view distance saved on the video tab for this run only
              --ocean <pct>     percent of the surface under water (default 25)
              --width <n>       window width (default 1600)
              --height <n>      window height (default 900)
              --pack <path>     import block and item textures from a texture pack: a folder, or a
                                .zip, .mcpack or .mcaddon. Both layouts are read — assets/ with a
                                namespace, or textures/ at the root with the older names — and
                                whichever is which is worked out from the pack. Anything it does
                                not carry keeps Driftwood's own art.
              --texture-size n  tile size to build the texture array at; omit it and the pack's own
                                resolution is used, which is almost always what is wanted. Clamped
                                to what the card reports and a memory budget allows.
              --skin <path>     wear a skin PNG: exactly 64x64, or 64x32 for an old one
              --skin-model m    'classic' or 'slim' arms, overriding what the sheet looks like
              --time <hour>     hour of the day to open at, 0 to 23 (default 8)
              --daylength <s>   seconds in a full day (default 1200); short values walk a sunset
              --vsync           cap to the display refresh rate, for this run only
              --mute            open no audio device at all
              --version         print this build's Driftwood version and exit
              --audit           generate and mesh headlessly, print a census and checks, then exit
              --audio-check [sound-pack.zip]
                                decode Driftwood's owned fallback; with a pack, require and report
                                every sound slot the game names
              --controller-check
                                load bundled SDL3, verify the XInput fallback ABI, enumerate any
                                connected pads by name, and pass when no controller is attached
              --pack-coverage   with --pack, report what the pack has art for that we do not
              --pack-matrix dir inspect every top-level pack in an ignored corpus, aggregate exact
                                compatibility outcomes and feature families, and route gaps
              --creature-geometry <dir>
                                put animals in the world, wearing skeletons read from this folder.
                                Same folder --creatures reports on, and remembered once given.
              --creatures [dir] read creature skeletons and say which of ours found one. The
                                skeletons ship with the GAME, not with a texture pack — a pack only
                                overrides shapes it changes — so this wants the folder of .geo.json
                                files an installed Bedrock client keeps under
                                data\resource_packs. Add --pack to check the skins as well.
              --pack-report     with --pack, report which of OUR layers the pack supplied and
                                which kept our art — the answer to "is the pack even being used"
              --atlas           with --pack, print what is on a pre-1.6 terrain.png grid: every
                                cell's mean colour and how much of it is opaque. A 2012 pack
                                addresses blocks by CELL NUMBER rather than by name, so this is
                                what says which cell holds what — measured rather than remembered
              --play <secs>     play normally for this long and then close the window the way a
                                player would, so the world is saved on the way out. The only way to
                                ask whether closing the window keeps the world: a killed process
                                never reaches the code that writes it.
              --bench [secs]    fly a fixed path once the world has settled, report frame-time
                                percentiles, then exit (default 15 s, seed defaults to 'driftwood')
              --shot <folder>   photograph what is in the hand — a pickaxe, a sword, a torch and a
                                block, in each view, at rest and mid-swing, plus first-person sword
                                and shield lowered/raised — write them there and quit. With
                                --ui-check, write each deterministic UI state instead.
                                The real world, the real camera, the real grip; the way to look at
                                a held thing without starting the game and holding one.
              --uploads <n>     chunk uploads allowed per frame (default 4)
              --stall <ms>      with --bench, burn this long every 200th frame — the control that
                                proves the benchmark can see a hitch it is known to contain
              --help            this text

            Controls — all rebindable on the screen's controls tab, and saved between sessions.
            These are what they ship as.

              Arrow keys        move (WASD also works; both are bound)
              Space / Ctrl      jump / sneak — up and down when flying
              Shift             sprint (boost when flying)
              Left / right      hold to mine or place; the arm swings and the swing takes the block
              E                 open the screen: craft, controls, video, audio, world
              Esc               release or recapture the mouse; closes the screen when one is open
              F1                wireframe
              F2                frustum culling
              F3                walk or fly
              F5                first person, over the shoulder, facing
              F6                hold the clock where it is
              F7                wind the day on by an hour or two
              1-9 / wheel       pick what is in hand

            Settings live beside your other application data, as plain key=value text you can
            edit. The path is printed at startup.
            """);
    }
}
