using System.Diagnostics;
using System.Numerics;

namespace Driftwood.Client.Render;

/// <summary>
/// The drawn pointers' tail of dust (2026-10-26): a short string hung from the arrow that swings,
/// lags and stretches behind the mouse, and sheds loose sparkles when it is flung.
/// </summary>
/// <remarks>
/// <para>⛳ <b>USER: "an animated physics string of like pixie dust that follows your mouse cursor
/// around ... if you quickly move right it would take a split second to change."</b> A verlet rope:
/// each point keeps where it was and where it is, the difference is its speed, and a few passes
/// pull neighbours back to their spacing. Everything is in art pixels, so the string is the same
/// shape at every interface scale.</para>
/// <para>⚠ Its gravity points nearly straight down, leaning a touch right toward where the art drew
/// its sparkles; USER: "allow gravity have it". At rest the tail draws in short.</para>
/// <para>⚠ Real time, not game time: it keeps moving while the world is paused under the menu.</para>
/// </remarks>
public sealed class PointerTrail
{
    public const int Points = 14;

    /// <summary>Art pixels between neighbouring points.</summary>
    private const float Spacing = 2f;

    /// <summary>Art pixels between points once the mouse has rested: the tail draws in short.</summary>
    private const float IdleSpacing = 0.8f;
    private const float Step = 1f / 120f;
    private const float Damping = 0.965f;
    private const float Pull = 1400f;
    private const int MaxDust = 40;

    private static readonly Vector2 Down = Vector2.Normalize(new Vector2(0.2f, 1f));

    private readonly Vector2[] _at = new Vector2[Points];
    private readonly Vector2[] _was = new Vector2[Points];
    private readonly List<Mote> _dust = new(MaxDust);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random = new();
    private double _last = -1;
    private float _owed;
    private float _art;
    private Vector2 _anchorWas;

    /// <summary>
    /// How lately the mouse moved, 1 just now to 0 long rested. The spacing follows it, so the tail
    /// is long while it is being flung and shortens once the pointer sits still.
    /// </summary>
    private float _stir;

    /// <summary>One loose sparkle, in art pixels, with the seconds it has left.</summary>
    public record struct Mote(Vector2 At, Vector2 Speed, float Life, float Span);

    /// <summary>Seconds since the trail began, for twinkling.</summary>
    public float Time => (float)_clock.Elapsed.TotalSeconds;

    public ReadOnlySpan<Vector2> Chain => _at;
    public IReadOnlyList<Mote> Dust => _dust;

    /// <summary>
    /// Moves the string on to now, its first point fixed at <paramref name="anchor"/> (overlay
    /// units), with one art pixel <paramref name="art"/> overlay units wide.
    /// </summary>
    public void Advance(Vector2 anchor, float art)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var here = anchor / art;

        // Out of sight for a moment, rescaled, or thrown across the screen in one frame: hang it
        // fresh rather than let it whip in from wherever it was left.
        if (_last < 0 || now - _last > 0.25 || art != _art || Vector2.Distance(here, _anchorWas) > 200f)
        {
            Hang(here);
            _art = art;
            _last = now;
            _anchorWas = here;
            return;
        }

        var elapsed = (float)Math.Min(now - _last, 0.1);
        _last = now;
        _owed += elapsed;

        var from = _anchorWas;
        var speed = elapsed > 0f ? Vector2.Distance(here, from) / elapsed : 0f;
        _stir = Math.Max(_stir * MathF.Exp(-elapsed / 0.6f), Math.Min(1f, speed / 120f));

        var steps = (int)(_owed / Step);
        for (var s = 1; s <= steps; s++)
            Tick(Vector2.Lerp(from, here, s / (float)steps));
        _owed -= steps * Step;

        Shed(speed, elapsed);
        Settle(elapsed);

        _anchorWas = here;
    }

    private void Hang(Vector2 anchor)
    {
        for (var i = 0; i < Points; i++)
            _at[i] = _was[i] = anchor + Down * (i * IdleSpacing);
        _dust.Clear();
        _owed = 0f;
    }

    private void Tick(Vector2 anchor)
    {
        _at[0] = _was[0] = anchor;
        var fall = Down * (Pull * Step * Step);
        var spacing = IdleSpacing + (Spacing - IdleSpacing) * _stir;
        for (var i = 1; i < Points; i++)
        {
            var moving = (_at[i] - _was[i]) * Damping;
            _was[i] = _at[i];
            _at[i] += moving + fall;
        }

        // Soft links: a fast flick stretches the string well out before it springs back.
        // ⚠ USER: "the bounce or elasticity probably needs to be looser", then "go partway back".
        for (var pass = 0; pass < 2; pass++)
        for (var i = 1; i < Points; i++)
        {
            var gap = _at[i] - _at[i - 1];
            var length = gap.Length();
            if (length <= spacing || length < 1e-4f) continue;

            var fix = gap * ((length - spacing) / length) * 0.55f;
            if (i == 1) _at[i] -= fix;
            else
            {
                _at[i] -= fix * 0.5f;
                _at[i - 1] += fix * 0.5f;
            }
        }
    }

    /// <summary>A flung string sheds dust from along its length, more the faster it goes.</summary>
    private void Shed(float speed, float elapsed)
    {
        if (speed < 60f) return;

        var expected = Math.Min(speed * 0.08f, 40f) * elapsed;
        while (expected > 0f && _dust.Count < MaxDust)
        {
            if (expected < 1f && _random.NextSingle() > expected) break;
            expected -= 1f;

            var from = 2 + _random.Next(Points - 2);
            var drift = (_at[from] - _was[from]) / Step * 0.25f;
            var scatter = new Vector2(_random.NextSingle() - 0.5f, _random.NextSingle() - 0.5f) * 24f;
            var span = 0.45f + _random.NextSingle() * 0.45f;
            _dust.Add(new Mote(_at[from], drift + scatter, span, span));
        }
    }

    private void Settle(float elapsed)
    {
        for (var i = _dust.Count - 1; i >= 0; i--)
        {
            var mote = _dust[i];
            mote.Life -= elapsed;
            if (mote.Life <= 0f)
            {
                _dust.RemoveAt(i);
                continue;
            }

            mote.Speed = mote.Speed * MathF.Pow(0.1f, elapsed) + new Vector2(0f, 30f * elapsed);
            mote.At += mote.Speed * elapsed;
            _dust[i] = mote;
        }
    }
}
