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

            // Which of our own layers a pack actually supplied, and which kept ours. The count in
            // the startup line says how many and never which, and "is the thing I am looking at one
            // of them" is the only question anybody has when a pack looks like it did nothing.
            if (args.Contains("--pack-report"))
            {
                Console.WriteLine(
                    BlockTextureSet.Build(options.PackPath, options.TextureSize, 4096).Report());
                return 0;
            }

            if (args.Contains("--pack-coverage"))
            {
                if (string.IsNullOrWhiteSpace(options.PackPath))
                {
                    Console.Error.WriteLine("driftwood: --pack-coverage needs --pack <folder or zip>");
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
                    options = options with { Seed = WorldSeed.Parse(Next(args, ref i, "--seed")) };
                    seedGiven = true;
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

              --seed <text>     world seed; digits are literal, words are hashed, omit for random
              --chunks <n>      how far the world stays loaded, in chunks across; overrides the
                                view distance saved on the video tab for this run only
              --ocean <pct>     percent of the surface under water (default 25)
              --width <n>       window width (default 1600)
              --height <n>      window height (default 900)
              --pack <path>     import block, item and skin textures from a texture pack folder
                                or .zip; anything the pack does not carry keeps Driftwood's own art
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
              --pack-report     with --pack, report which of OUR layers the pack supplied and
                                which kept our art — the answer to "is the pack even being used"
              --bench [secs]    fly a fixed path once the world has settled, report frame-time
                                percentiles, then exit (default 15 s, seed defaults to 'driftwood')
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
