using Driftwood.Core.Textures;

namespace Driftwood.Client.Render;

/// <summary>
/// Plays the layers that move: water, and whatever else a pack ships as a strip.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Still water reads as blue rock</b>, and it is the reason every pack in the genre ships
/// water as thirty-two pictures rather than one. Ours was a speckle and a pack's strip was read for
/// its first frame only, so a lake was a flat blue surface in both cases.</para>
/// <para>One upload per layer per frame <em>change</em>, not per rendered frame. At sixty frames a
/// second against a strip held for a tenth of a second that is one upload in six, and at a pack's
/// resolution the difference between those two is the difference between free and not.</para>
/// <para>The clock is the game's own elapsed time rather than an accumulator, so a stall, a pause or
/// a slow chunk load does not leave the water somewhere it should not be — the same argument the
/// dropped items make for computing their bob from age.</para>
/// </remarks>
public sealed class TextureAnimator
{
    private readonly record struct Track(int Layer, byte[][] Frames, float[] Seconds, float Loop);

    private readonly BlockTextureArray _array;
    private readonly Track[] _tracks;
    private readonly int[] _showing;

    /// <summary>Uploads since the world opened, so the cost of the pass is a number.</summary>
    public int Uploads { get; private set; }

    public int Count => _tracks.Length;

    /// <summary>Frames across every track, for the startup line.</summary>
    public int FrameCount { get; }

    public TextureAnimator(BlockTextureArray array, IReadOnlyList<BlockTextureSet.LayerAnimation> animations)
    {
        _array = array;

        var tracks = new List<Track>(animations.Count);
        foreach (var animation in animations)
        {
            if (animation.Frames.Length < 2) continue;

            var loop = 0f;
            foreach (var seconds in animation.Seconds) loop += MathF.Max(0.01f, seconds);

            tracks.Add(new Track(animation.Layer, animation.Frames, animation.Seconds, loop));
            FrameCount += animation.Frames.Length;
        }

        _tracks = [.. tracks];

        // −1 rather than 0, so the first tick writes even if the clock starts on frame zero. The
        // tile in the array is already frame zero, so this costs one upload and buys never having
        // to reason about whether the two agree.
        _showing = new int[_tracks.Length];
        Array.Fill(_showing, -1);
    }

    /// <summary>Puts each track's current frame in the array, for those that changed.</summary>
    public void Update(double elapsed)
    {
        if (_tracks.Length == 0) return;

        _array.Bind();

        for (var t = 0; t < _tracks.Length; t++)
        {
            var track = _tracks[t];
            var into = (float)(elapsed % track.Loop);

            var frame = 0;
            for (var i = 0; i < track.Frames.Length; i++)
            {
                var held = MathF.Max(0.01f, track.Seconds[i]);
                if (into < held) { frame = i; break; }
                into -= held;
                frame = i;
            }

            if (frame == _showing[t]) continue;

            _showing[t] = frame;
            _array.WriteLayer(track.Layer, track.Frames[frame]);
            Uploads++;
        }
    }
}
