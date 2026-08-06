using Driftwood.Client.Audio;
using Driftwood.Client.Render;
using Driftwood.Core.Audio;
using Driftwood.Core.Blocks;
using Driftwood.Core.Diagnostics;
using Driftwood.Core.Entities;
using Driftwood.Core.Gen;
using Driftwood.Core.Textures;

namespace Driftwood.Client;

public static class Program
{
    /// <summary>
    /// Long enough that the flight ends further from its start than the streaming drop radius, so
    /// the whole loaded set turns over at least once.
    /// </summary>
    private const int DefaultBenchSeconds = 15;

    public static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
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

            if (args.Contains("--audio-check")) return AudioCheck();

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
            if (args.Contains("--pack-report"))
            {
                Console.WriteLine(
                    BlockTextureSet.Build(options.PackPath, options.TextureSize, 4096).Report());
                return 0;
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
            Console.Error.WriteLine($"driftwood: {ex.Message}");
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
    private static int IconSheet(string path)
    {
        const int Zoom = 12;
        const int Gap = 2;

        var tiles = new List<byte[]>();
        for (var shape = 0; shape < TileGen.ToolShapes.Length; shape++)
            tiles.Add(TileGen.IconTool(4000 + shape, shape, 150, 120, 90));

        var cell = TileGen.Size * Zoom + Gap;
        var width = cell * tiles.Count;
        var height = cell;
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

            var to = ((y * width) + t * cell + x) * 4;
            pixels[to] = tiles[t][from];
            pixels[to + 1] = tiles[t][from + 1];
            pixels[to + 2] = tiles[t][from + 2];
            pixels[to + 3] = 255;
        }

        File.WriteAllBytes(path, Png.Encode(new Image(width, height, pixels)));
        Console.WriteLine($"icons       {tiles.Count} tools at {Zoom}x written to {path}");
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

        var size = pack?.DetectResolution() ?? TileGen.Size;
        var resolved = CreatureLibrary.Resolve(models, pack, size);

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

    private static int AudioCheck()
    {
        var registry = new BlockRegistry();
        StarterBlocks.Register(registry);
        registry.Seal();

        var root = SoundLibrary.FindRoot();
        var library = new SoundLibrary(root);

        Console.WriteLine($"root        {root}");
        Console.WriteLine($"indexed     {library.Count} clips");

        using var engine = new AudioEngine(library);
        Console.WriteLine($"device      {engine.Summary}");
        Console.WriteLine();

        var faults = new List<string>();
        double shortest = double.MaxValue, longest = 0;

        Console.WriteLine("clips the block table names");
        foreach (var name in MaterialSounds.AllNames().Order(StringComparer.Ordinal))
        {
            var clip = library.Load(name);
            if (clip is null)
            {
                Console.WriteLine($"  {name,-32} MISSING");
                faults.Add($"{name} is missing or would not decode");
                continue;
            }

            var peak = clip.Peak;
            shortest = Math.Min(shortest, clip.Seconds);
            longest = Math.Max(longest, clip.Seconds);

            Console.WriteLine(
                $"  {name,-32} {clip.Seconds,6:F2}s  {clip.Channels}ch {clip.SampleRate}Hz  peak {peak:F2}");

            // A file that decodes to silence is indistinguishable from one that never plays, and
            // is the failure this whole check exists to be able to see.
            if (peak < 0.02f) faults.Add($"{name} decodes to near silence (peak {peak:F3})");
            if (clip.Seconds > 8f) faults.Add($"{name} is {clip.Seconds:F1}s, which is a loop not a one-shot");
        }

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
            Console.WriteLine("OK  every material resolves, every clip decodes, nothing is silent");
            return 0;
        }

        foreach (var fault in faults) Console.WriteLine($"FAULT  {fault}");
        return 1;
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
                case "--audit":
                case "--audio-check":
                case "--pack-coverage":
                case "--pack-report":
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
              --skin <path>     wear a skin PNG: 64x64, or 64x32 for an old one, at any scale
              --skin-model m    'classic' or 'slim' arms, overriding what the sheet looks like
              --time <hour>     hour of the day to open at, 0 to 23 (default 8)
              --daylength <s>   seconds in a full day (default 1200); short values walk a sunset
              --vsync           cap to the display refresh rate, for this run only
              --mute            open no audio device at all
              --audit           generate and mesh headlessly, print a census and checks, then exit
              --audio-check     resolve every sound the block table names and report, silently
              --pack-coverage   with --pack, report what the pack has art for that we do not
              --creatures [dir] read creature skeletons and say which of ours found one. The
                                skeletons ship with the GAME, not with a texture pack — a pack only
                                overrides shapes it changes — so this wants the folder of .geo.json
                                files an installed Bedrock client keeps under
                                data\resource_packs. Add --pack to check the skins as well.
              --pack-report     with --pack, report which of OUR layers the pack supplied and
                                which kept our art — the answer to "is the pack even being used"
              --play <secs>     play normally for this long and then close the window the way a
                                player would, so the world is saved on the way out. The only way to
                                ask whether closing the window keeps the world: a killed process
                                never reaches the code that writes it.
              --bench [secs]    fly a fixed path once the world has settled, report frame-time
                                percentiles, then exit (default 15 s, seed defaults to 'driftwood')
              --shot <folder>   photograph what is in the hand — a pickaxe, a sword, a torch and a
                                block, in each view, at rest and mid-swing — write them there and
                                quit. The real world, the real camera, the real grip; the way to
                                look at a held thing without starting the game and holding one.
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
