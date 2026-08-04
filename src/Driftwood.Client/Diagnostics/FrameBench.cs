using System.Text;

namespace Driftwood.Client.Diagnostics;

/// <summary>One frame's worth of measurement.</summary>
/// <param name="FrameMs">Wall time from the start of this frame to the start of the next, so it
/// includes event handling, the buffer swap, and whatever the driver made us wait for.</param>
/// <param name="UpdateMs">CPU time inside the update callback: camera, streaming pump, uploads.</param>
/// <param name="RenderMs">CPU time inside the render callback. Submission, not GPU execution —
/// the GPU's share lands in <paramref name="FrameMs"/> at the swap.</param>
/// <param name="Uploads">Chunk meshes turned into GL buffers during this frame.</param>
/// <param name="QueueDepth">Chunks waiting to be generated or meshed.</param>
/// <param name="ReadyBacklog">Finished meshes waiting for an upload slot.</param>
public readonly record struct FrameSample(
    double FrameMs,
    double UpdateMs,
    double RenderMs,
    int DrawnChunks,
    int LoadedChunks,
    int Triangles,
    int Uploads,
    int QueueDepth,
    int ReadyBacklog);

/// <summary>
/// Collects per-frame timings along a fixed path and reports the distribution.
/// </summary>
/// <remarks>
/// <para>This exists because P1 claims to have removed frame hitches and nothing measured it.
/// <c>--audit</c> proves the world is correct; it says nothing about whether looking at that world
/// is smooth. The two failures are unrelated and only one of them was instrumented.</para>
/// <para>The number that matters is <b>p99</b>, not the mean and not the frame rate. A single 40 ms
/// stall every few seconds is exactly what a player calls stuttering, and it moves a 300 fps average
/// by nothing at all. Mean frame time cannot see the failure it is being asked about.</para>
/// <para>Warm-up runs until the streaming pipeline goes quiet and is reported separately. Shader
/// compilation, the first buffer uploads and driver warm-up all land in the opening frames; folding
/// them into the tail would hand every run a hitch no player would ever see and bury the ones they
/// would. Measuring before the world has finished arriving is worse still — it times an empty
/// screen, which is fast, and calls the result smooth.</para>
/// </remarks>
public sealed class FrameBench
{
    public sealed record Result(string Report, bool Passed);

    private readonly double _durationMs;
    private readonly List<FrameSample> _samples = [];
    private double _elapsedMs;

    public FrameBench(double durationSeconds) => _durationMs = Math.Max(0.1, durationSeconds) * 1000.0;

    public int Recorded => _samples.Count;
    public double ElapsedSeconds => _elapsedMs / 1000.0;
    public double DurationSeconds => _durationMs / 1000.0;
    public bool Complete => _elapsedMs >= _durationMs;

    public void Add(in FrameSample sample)
    {
        _samples.Add(sample);
        _elapsedMs += sample.FrameMs;
    }

    /// <summary>Context the report prints but the bench does not own.</summary>
    /// <param name="Seed">World seed, so a report can be reproduced.</param>
    /// <param name="ViewRadius">Streaming radius in chunks.</param>
    /// <param name="Path">The flight that was measured.</param>
    /// <param name="UploadCap">Chunk uploads allowed per frame.</param>
    /// <param name="VSync">Whether the swap was synchronised to the display.</param>
    /// <param name="WarmupSettled">False if warm-up gave up waiting for the pipeline to go quiet.</param>
    public readonly record struct Context(
        long Seed,
        int ViewRadius,
        BenchPath Path,
        int UploadCap,
        bool VSync,
        int Workers,
        int WarmupFrames,
        double WarmupSeconds,
        double WarmupPeakMs,
        bool WarmupSettled);

    public Result Finish(in Context context)
    {
        if (_samples.Count == 0) return new Result("bench recorded no frames\n", false);

        var sb = new StringBuilder();

        var frame = SortedColumn(static s => s.FrameMs);
        var update = SortedColumn(static s => s.UpdateMs);
        var render = SortedColumn(static s => s.RenderMs);

        var p50 = Percentile(frame, 0.50);
        var p95 = Percentile(frame, 0.95);
        var p99 = Percentile(frame, 0.99);
        var worst = frame[^1];
        var mean = _elapsedMs / _samples.Count;

        // A hitch has to be both proportionally and absolutely bad. Proportion alone is useless
        // here: an idle frame costs a tenth of a millisecond, so "twice the median" counts ordinary
        // scheduler jitter as a stutter and buries the real ones in four thousand false ones.
        var hitchThreshold = HitchThreshold(p50);
        var hitches = 0;
        var hitchMs = 0.0;
        var dropped = 0;
        var worstFrame = 0;
        for (var i = 0; i < _samples.Count; i++)
        {
            var ms = _samples[i].FrameMs;
            if (ms > hitchThreshold) { hitches++; hitchMs += ms; }
            if (ms > VBlank60Ms) dropped++;
            if (ms >= worst) worstFrame = i;
        }

        // Frames that created GL buffers, split out from the ones that did not. Without this split
        // the streaming cost is unreadable: 22 chunk crossings spread over a hundred thousand
        // frames do not move p99 at all, however expensive each one is. Uploads get their own
        // denominator so the cost per event stays visible however fast the idle path gets.
        var uploading = SortedColumnWhere(static s => s.FrameMs, static s => s.Uploads > 0);
        var idle = SortedColumnWhere(static s => s.FrameMs, static s => s.Uploads == 0);

        var drawn = SortedColumn(static s => s.DrawnChunks);
        var tris = SortedColumn(static s => s.Triangles);
        var queue = SortedColumn(static s => s.QueueDepth);
        var loadedMax = Max(static s => s.LoadedChunks);

        var uploadsTotal = 0L;
        var uploadsMax = 0;
        var framesAtCap = 0;
        var queueDrained = 0;
        var queueMax = 0;
        var backlogMax = 0;
        foreach (var s in _samples)
        {
            uploadsTotal += s.Uploads;
            if (s.Uploads > uploadsMax) uploadsMax = s.Uploads;
            if (s.Uploads >= context.UploadCap) framesAtCap++;
            if (s.QueueDepth == 0) queueDrained++;
            if (s.QueueDepth > queueMax) queueMax = s.QueueDepth;
            if (s.ReadyBacklog > backlogMax) backlogMax = s.ReadyBacklog;
        }

        var drainedPct = queueDrained * 100.0 / _samples.Count;
        var backlogEnd = _samples[^1].ReadyBacklog;

        var seconds = ElapsedSeconds;
        var distance = BenchPath.DistanceOver(seconds);
        var chord = context.Path.ChordAfter(seconds);

        sb.AppendLine($"bench         {_samples.Count:N0} frames over {seconds:F1} s, seed {context.Seed}");
        sb.AppendLine($"world         view {context.ViewRadius} chunks, {context.Workers} stream workers, {context.UploadCap} uploads/frame cap");
        sb.AppendLine($"path          circle r={context.Path.Radius:F0} at {BenchPath.BlocksPerSecond:F0} blocks/s — "
                    + $"{distance:F0} blocks flown, {chord:F0} from the start, ~{(int)(distance / 32):N0} chunk crossings");
        sb.AppendLine($"warm-up       {context.WarmupFrames:N0} frames over {context.WarmupSeconds:F1} s, peak {context.WarmupPeakMs:F1} ms"
                    + (context.WarmupSettled ? " (pipeline went quiet)" : "  ** TIMED OUT, never went quiet **"));
        sb.AppendLine($"vsync         {(context.VSync ? "ON — frame times are quantised to the display, the tail is not readable" : "off")}");
        sb.AppendLine();

        sb.AppendLine("frame time         p50     p95     p99     max    mean   frames");
        sb.AppendLine($"  total       {p50,8:F2}{p95,8:F2}{p99,8:F2}{worst,8:F2}{mean,8:F2}{_samples.Count,9:N0}   "
                    + $"({1000.0 / Math.Max(mean, 0.0001):N0} fps mean, {1000.0 / Math.Max(p99, 0.0001):N0} at p99)");
        sb.AppendLine($"  no uploads  {Percentile(idle, 0.50),8:F2}{Percentile(idle, 0.95),8:F2}{Percentile(idle, 0.99),8:F2}{Last(idle),8:F2}{"",8}{idle.Length,9:N0}");
        sb.AppendLine($"  uploading   {Percentile(uploading, 0.50),8:F2}{Percentile(uploading, 0.95),8:F2}{Percentile(uploading, 0.99),8:F2}{Last(uploading),8:F2}{"",8}{uploading.Length,9:N0}");
        sb.AppendLine($"  update      {Percentile(update, 0.50),8:F2}{Percentile(update, 0.95),8:F2}{Percentile(update, 0.99),8:F2}{update[^1],8:F2}");
        sb.AppendLine($"  render cpu  {Percentile(render, 0.50),8:F2}{Percentile(render, 0.95),8:F2}{Percentile(render, 0.99),8:F2}{render[^1],8:F2}");
        sb.AppendLine();

        var hitchesPerSecond = hitches / seconds;
        var hitchTimeShare = hitchMs / _elapsedMs * 100.0;
        var droppedPerSecond = dropped / seconds;

        var w = _samples[worstFrame];
        sb.AppendLine($"hitches       {hitches:N0} frames over {hitchThreshold:F2} ms (2x p50 or +4 ms) — "
                    + $"{hitchesPerSecond:F1}/s, {hitchTimeShare:F1}% of the wall clock");
        sb.AppendLine($"dropped       {dropped:N0} frames over {VBlank60Ms:F2} ms — {droppedPerSecond:F1}/s a 60 Hz display would miss");
        sb.AppendLine($"worst frame   {worst:F2} ms at {worstFrame:N0} — {w.Uploads} uploads, "
                    + $"{w.QueueDepth:N0} queued, {w.ReadyBacklog} ready, {w.DrawnChunks:N0} drawn "
                    + $"({w.UpdateMs:F2} ms update, {w.RenderMs:F2} ms render)");
        sb.AppendLine($"draw calls    p50 {Percentile(drawn, 0.50):N0}, max {drawn[^1]:N0}, of {loadedMax:N0} chunks loaded");
        sb.AppendLine($"triangles     p50 {Percentile(tris, 0.50):N0}, max {tris[^1]:N0}");
        sb.AppendLine($"uploads       {uploadsTotal:N0} total, max {uploadsMax}/frame, {framesAtCap:N0} frames at the cap");
        sb.AppendLine($"stream queue  p50 {Percentile(queue, 0.50):N0}, max {queueMax:N0}, empty on {drainedPct:F1}% of frames");
        sb.AppendLine($"upload queue  max backlog {backlogMax:N0}, {backlogEnd} left at the end");
        sb.AppendLine();
        sb.AppendLine("checks");

        var passed = true;
        void Check(string label, bool ok, string detail)
        {
            passed &= ok;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {label,-28} {detail}");
        }

        // First and most important: prove there was something to measure. Every timing number below
        // is meaningless against an empty world, and an empty world is fast — it would sail through
        // a pure frame-time check while reporting the renderer in perfect health. This check has
        // already earned its place: the first build of this bench flew faster than the streamer
        // could load, drew nothing at all, and passed every other line on this list.
        var drawnP50 = Percentile(drawn, 0.50);
        var trisP50 = Percentile(tris, 0.50);
        Check("bench had work to do", drawnP50 >= 8 && trisP50 >= 50_000,
            $"p50 {drawnP50:N0} chunks / {trisP50:N0} tris drawn");

        Check("warm-up settled", context.WarmupSettled,
            context.WarmupSettled
                ? $"quiet after {context.WarmupSeconds:F1} s"
                : $"still busy after {context.WarmupSeconds:F1} s — measurement started mid-load");

        Check("frame budget met", p50 <= VBlank60Ms, $"p50 {p50:F2} ms (60 fps needs {VBlank60Ms:F2})");

        // The two gates that actually decide whether this is smooth, and the reason neither of them
        // is a percentile of frames. A control run with a 20 ms stall injected every 200th frame —
        // twenty hitches a second, four tenths of the run spent stalled — passed a p99 gate without
        // a mark against it, because 305 bad frames out of 60,862 is p99.5. When a frame costs a
        // tenth of a millisecond, frame count stops being a denominator anyone lives in. Wall clock
        // is: a player feels hitches per second, and time lost is time lost.
        Check("time lost to hitches", hitchTimeShare < 2.0,
            $"{hitchTimeShare:F1}% of the run in {hitches:N0} frames over {hitchThreshold:F2} ms (want < 2%)");

        Check("holds 60 Hz", droppedPerSecond < 0.5,
            $"{droppedPerSecond:F1} frames/s over {VBlank60Ms:F2} ms (want < 0.5)");

        // The body of the distribution, not the tail — the tail is the two gates above. This one
        // starts to bind once frames get expensive enough for p99 to mean something again.
        var tailAllowance = Math.Max(p50 * 2.5, p50 + 4.0);
        Check("typical frame is steady", p99 <= tailAllowance,
            $"p99 {p99:F2} ms against {tailAllowance:F2} allowed (2.5x p50 or +4 ms)");

        // Uploads are capped per frame, so the cap has to be high enough to keep up with what the
        // workers finish. If it is not, meshes pile up and chunks appear late — the cap trades one
        // failure for another, and this is the side nobody notices.
        Check("uploads keep up", backlogEnd == 0 && backlogMax <= 64,
            $"max backlog {backlogMax}, {backlogEnd} unfinished at the end");

        // The stream queue is meant to spike on a chunk crossing and then drain. Permanently busy
        // means the workers never catch the viewer and the world is being drawn behind where it is.
        Check("stream queue drains", drainedPct >= 5.0,
            $"empty on {drainedPct:F1}% of frames, peak {queueMax:N0}");

        return new Result(sb.ToString(), passed);
    }

    /// <summary>One frame at 60 Hz. The budget everything below is judged against.</summary>
    private const double VBlank60Ms = 1000.0 / 60.0;

    /// <summary>Where a frame stops being ordinary jitter and starts being something a player sees.</summary>
    private static double HitchThreshold(double p50) => Math.Max(p50 * 2.0, p50 + 4.0);

    private double[] SortedColumn(Func<FrameSample, double> select)
    {
        var values = new double[_samples.Count];
        for (var i = 0; i < _samples.Count; i++) values[i] = select(_samples[i]);
        Array.Sort(values);
        return values;
    }

    private double[] SortedColumnWhere(Func<FrameSample, double> select, Func<FrameSample, bool> keep)
    {
        var values = new List<double>();
        foreach (var s in _samples) if (keep(s)) values.Add(select(s));
        var array = values.ToArray();
        Array.Sort(array);
        return array;
    }

    private static double Last(double[] sorted) => sorted.Length == 0 ? 0 : sorted[^1];

    private int Max(Func<FrameSample, int> select)
    {
        var max = 0;
        foreach (var s in _samples)
        {
            var v = select(s);
            if (v > max) max = v;
        }
        return max;
    }

    /// <summary>Nearest-rank percentile over an already-sorted column.</summary>
    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var rank = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}
