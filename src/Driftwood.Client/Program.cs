using Driftwood.Client.Render;
using Driftwood.Core.Diagnostics;
using Driftwood.Core.Gen;

namespace Driftwood.Client;

public static class Program
{
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
            host.Run();
            return 0;
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

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seed":
                    options = options with { Seed = WorldSeed.Parse(Next(args, ref i, "--seed")) };
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
                case "--audit":
                    break;   // handled in Main; listed here so it is not an unknown argument
                default:
                    throw new ArgumentException($"unknown argument '{args[i]}' (try --help)");
            }
        }

        return options;
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

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Driftwood

              --seed <text>     world seed; digits are literal, words are hashed, omit for random
              --chunks <n>      chunks across the generated square (default 16, so 512 blocks)
              --ocean <pct>     percent of the surface under water (default 25)
              --width <n>       window width (default 1600)
              --height <n>      window height (default 900)
              --vsync           cap to the display refresh rate (off by default so fps is readable)
              --audit           generate and mesh headlessly, print a census and checks, then exit
              --help            this text

            Controls
              Arrow keys        move (WASD also works)
              Space / Ctrl      up / down (PgUp / PgDn also work)
              Shift / Alt       boost / slow
              Esc               release or recapture the mouse
              F1                wireframe
            """);
    }
}
