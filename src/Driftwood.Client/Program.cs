using Driftwood.Client.Render;
using Driftwood.Core.Diagnostics;
using Driftwood.Core.Entities;
using Driftwood.Core.Gen;

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

            using var host = new ClientHost(options);
            return host.Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"driftwood: {ex.Message}");
            return 1;
        }
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
                    options = options with { ChunksAcross = ParseInt(Next(args, ref i, "--chunks"), 2, 64) };
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
                    options = options with { TextureSize = ParseInt(Next(args, ref i, "--texture-size"), 16, 512) };
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
                case "--uploads":
                    options = options with { MaxUploadsPerFrame = ParseInt(Next(args, ref i, "--uploads"), 1, 4096) };
                    break;
                case "--stall":
                    options = options with { StallMs = ParseInt(Next(args, ref i, "--stall"), 0, 1000) };
                    break;
                case "--audit":
                    break;   // handled in Main; listed here so it is not an unknown argument
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
              --chunks <n>      chunks across the generated square (default 16, so 512 blocks)
              --ocean <pct>     percent of the surface under water (default 25)
              --width <n>       window width (default 1600)
              --height <n>      window height (default 900)
              --pack <path>     import block textures from a texture pack folder or .zip;
                                anything the pack does not carry keeps Driftwood's own art
              --texture-size n  tile resolution to build the texture array at (default 16)
              --skin <path>     wear a skin PNG: 64x64, or 64x32 for an old one, at any scale
              --skin-model m    'classic' or 'slim' arms, overriding what the sheet looks like
              --vsync           cap to the display refresh rate (off by default so fps is readable)
              --audit           generate and mesh headlessly, print a census and checks, then exit
              --bench [secs]    fly a fixed path once the world has settled, report frame-time
                                percentiles, then exit (default 15 s, seed defaults to 'driftwood')
              --uploads <n>     chunk uploads allowed per frame (default 4)
              --stall <ms>      with --bench, burn this long every 200th frame — the control that
                                proves the benchmark can see a hitch it is known to contain
              --help            this text

            Controls
              Arrow keys        move (WASD also works)
              Space / Ctrl      jump / sneak — up and down when flying
              Shift             sprint (boost when flying)
              Left / right      hold to mine or place; the arm swings and the swing takes the block
              Esc               release or recapture the mouse
              F1                wireframe
              F2                frustum culling
              F3                walk or fly
              F5                first person, over the shoulder, facing
            """);
    }
}
