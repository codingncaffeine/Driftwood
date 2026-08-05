using System.Diagnostics;

namespace Driftwood.Client.Diagnostics;

/// <summary>
/// Where the time between double-clicking Driftwood and seeing the world actually goes.
/// </summary>
/// <remarks>
/// <para>⛔ <b>Built before anything was done about the load time, and that ordering is the point.</b>
/// The plan for the start screen is a menu flying over a pre-seeded map — which will make the load
/// <em>look</em> shorter without making it shorter, and if it lands first the two get confused
/// permanently. There was no startup instrument at all, so every opinion about what was slow was a
/// guess.</para>
/// <para>The first number was found by accident: a timed play recorded twice the seconds it ran for,
/// because the window's clock starts when the window does and the first update's step covers
/// everything before it. That said the startup was about eleven seconds and said nothing whatever
/// about where they went.</para>
/// <para>Phases are stamped as they finish rather than timed individually, so the numbers add up to
/// the whole by construction and there is nowhere for time to hide between them. The gap between the
/// last phase and the first frame is reported as its own line for the same reason: it is where
/// waiting for the world to stream in shows up, and it is the one part no phase marker can see.</para>
/// </remarks>
public sealed class StartupTrace
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly List<(string What, double AtMs)> _marks = [];
    private bool _reported;

    /// <summary>Milliseconds since the process started doing anything about a window.</summary>
    public double ElapsedMs => _watch.Elapsed.TotalMilliseconds;

    /// <summary>Notes that a phase has just finished.</summary>
    public void Mark(string what) => _marks.Add((what, ElapsedMs));

    /// <summary>
    /// Prints the breakdown, once, at the first frame that reached the screen.
    /// </summary>
    /// <remarks>
    /// Called from the render loop rather than from the end of loading, because "the world is on
    /// screen" is the moment a player is actually waiting for and it is later than the moment the
    /// loading code finishes.
    /// </remarks>
    public void Report(string tail)
    {
        if (_reported || _marks.Count == 0) return;
        _reported = true;

        Mark(tail);

        Console.WriteLine();
        Console.WriteLine("startup     where the time went");

        var previous = 0.0;
        var slowest = ("", 0.0);

        foreach (var (what, at) in _marks)
        {
            var took = at - previous;
            previous = at;

            if (took > slowest.Item2) slowest = (what, took);

            var share = at > 0 ? took / _marks[^1].AtMs : 0;
            Console.WriteLine($"  {what,-22} {took,8:F0} ms   {Bar(share)} {share * 100,4:F0}%");
        }

        Console.WriteLine($"  {"total",-22} {_marks[^1].AtMs,8:F0} ms");
        Console.WriteLine($"  slowest: {slowest.Item1} at {slowest.Item2:F0} ms");
        Console.WriteLine();
    }

    /// <summary>A share of the total, drawn. Twenty cells, so one cell is five percent.</summary>
    private static string Bar(double share)
    {
        var filled = (int)Math.Round(Math.Clamp(share, 0, 1) * 20);
        return new string('#', filled).PadRight(20, '.');
    }
}
