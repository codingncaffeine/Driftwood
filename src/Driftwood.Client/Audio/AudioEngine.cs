using System.Numerics;
using Driftwood.Core.Audio;
using Silk.NET.OpenAL;

namespace Driftwood.Client.Audio;

/// <summary>
/// Plays sounds in the world: a device, a pool of voices, and a listener that follows the camera.
/// </summary>
/// <remarks>
/// <para>Every failure here is survivable and none of them stops the game. A machine with no sound
/// card, a container with no audio server, a driver that refuses the context — all of them end with
/// <see cref="Available"/> false and a line on the console, and everything else carries on calling
/// <see cref="Play"/> into nothing. A game that will not start because it could not open a speaker
/// is a worse game than a silent one.</para>
/// <para>Voices are a fixed pool, taken round-robin, and a request that finds them all busy steals
/// the oldest rather than being dropped. Debris and footsteps arrive in bursts; dropping is what
/// makes a burst sound thin, and stealing is what makes it sound loud.</para>
/// <para>Buffers are mono. A stereo buffer already carries its own left and right, so a device given
/// one has nowhere to put the panning that says where the sound is, and plays it flat wherever the
/// listener stands — which is exactly wrong for a block breaking forty metres away.</para>
/// </remarks>
public sealed unsafe class AudioEngine : IDisposable
{
    /// <summary>Sounds that can be going at once.</summary>
    /// <remarks>
    /// Thirty is comfortably more than a scene generates and comfortably under what a driver will
    /// hand out. OpenAL implementations commonly cap sources at 32 or 64 and simply fail past it,
    /// so asking for a couple of hundred would half work on the machines it worked on at all.
    /// </remarks>
    public const int Voices = 30;

    /// <summary>How far away a sound falls to nothing.</summary>
    private const float MaxDistance = 48f;

    private readonly SoundLibrary _library;
    private readonly Dictionary<string, uint> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly uint[] _sources = new uint[Voices];

    private AL? _al;
    private ALContext? _alc;
    private Device* _device;
    private Context* _context;
    private int _next;

    /// <summary>False when there is no device, or it refused. Everything still runs.</summary>
    public bool Available { get; }

    /// <summary>What to print at startup.</summary>
    public string Summary { get; }

    /// <summary>Sounds started since the world opened, for the frame report.</summary>
    public int Played { get; private set; }

    public AudioEngine(SoundLibrary library)
    {
        _library = library;

        try
        {
            _alc = ALContext.GetApi();
            _al = AL.GetApi();

            _device = _alc.OpenDevice("");
            if (_device is null)
            {
                Summary = "no audio device — running silent";
                Dispose();
                return;
            }

            _context = _alc.CreateContext(_device, null);
            if (_context is null || !_alc.MakeContextCurrent(_context))
            {
                Summary = "audio device would not open a context — running silent";
                Dispose();
                return;
            }

            fixed (uint* p = _sources) _al.GenSources(Voices, p);
            for (var i = 0; i < Voices; i++)
            {
                _al.SetSourceProperty(_sources[i], SourceFloat.ReferenceDistance, 3f);
                _al.SetSourceProperty(_sources[i], SourceFloat.MaxDistance, MaxDistance);
                _al.SetSourceProperty(_sources[i], SourceFloat.RolloffFactor, 1f);
            }

            _al.DistanceModel(DistanceModel.LinearDistanceClamped);

            Available = true;
            Summary = $"{library.Count} clips indexed, {Voices} voices";
        }
        catch (Exception ex)
        {
            // Any of DllNotFound, EntryPointNotFound or a driver throwing on init. All of them
            // mean the same thing here.
            Summary = $"audio unavailable ({ex.GetType().Name}) — running silent";
            Available = false;
        }
    }

    /// <summary>Points the listener at where the camera is and which way it faces.</summary>
    public void SetListener(Vector3 position, Vector3 forward)
    {
        if (!Available || _al is null) return;

        var up = Vector3.UnitY;
        Span<float> orientation = [forward.X, forward.Y, forward.Z, up.X, up.Y, up.Z];

        _al.SetListenerProperty(ListenerVector3.Position, position.X, position.Y, position.Z);
        fixed (float* p = orientation) _al.SetListenerProperty(ListenerFloatArray.Orientation, p);
    }

    /// <summary>
    /// Everything's loudness, 0 to 1. Applied to each source as it starts, not to the listener.
    /// </summary>
    /// <remarks>
    /// The listener has a gain of its own and using it would be one call rather than a multiply,
    /// but it does not touch what is already playing — turning the volume down would leave the
    /// current footstep at full and only take effect from the next one. A factor per source is what
    /// makes a slider feel connected to the thing it moves.
    /// </remarks>
    public float MasterVolume { get; set; } = 1f;

    /// <summary>Starts one clip at a point in the world.</summary>
    /// <param name="pitch">1 is as recorded. Small variation is what stops a repeat sounding looped.</param>
    public void Play(string name, Vector3 at, float volume = 1f, float pitch = 1f)
    {
        if (!Available || _al is null) return;
        if (MasterVolume <= 0f) return;

        volume *= MasterVolume;

        var buffer = BufferFor(name);
        if (buffer == 0) return;

        // Round-robin, stopping whatever was there. A burst of debris is several sounds at once and
        // the oldest of them is the one nobody will miss.
        var source = _sources[_next];
        _next = (_next + 1) % Voices;

        _al.SetSourceProperty(source, SourceInteger.Buffer, 0);
        _al.SetSourceProperty(source, SourceInteger.Buffer, (int)buffer);
        _al.SetSourceProperty(source, SourceVector3.Position, at.X, at.Y, at.Z);
        _al.SetSourceProperty(source, SourceFloat.Gain, Math.Clamp(volume, 0f, 4f));
        _al.SetSourceProperty(source, SourceFloat.Pitch, Math.Clamp(pitch, 0.25f, 4f));
        _al.SourcePlay(source);

        Played++;
    }

    /// <summary>Uploads a clip the first time it is asked for, and remembers the handle.</summary>
    private uint BufferFor(string name)
    {
        if (_buffers.TryGetValue(name, out var existing)) return existing;

        var clip = _library.Load(name)?.ToMono();
        if (clip is null || _al is null)
        {
            _buffers[name] = 0;
            return 0;
        }

        var handle = _al.GenBuffer();
        fixed (short* p = clip.Samples)
        {
            _al.BufferData(
                handle, BufferFormat.Mono16, p, clip.Samples.Length * sizeof(short), clip.SampleRate);
        }

        _buffers[name] = handle;
        return handle;
    }

    public void Dispose()
    {
        if (_al is not null)
        {
            foreach (var source in _sources) if (source != 0) _al.SourceStop(source);
            foreach (var buffer in _buffers.Values) if (buffer != 0) _al.DeleteBuffer(buffer);
            foreach (var source in _sources) if (source != 0) _al.DeleteSource(source);
        }

        if (_alc is not null)
        {
            if (_context is not null) _alc.DestroyContext(_context);
            if (_device is not null) _alc.CloseDevice(_device);
        }

        _context = null;
        _device = null;
        _al?.Dispose();
        _alc?.Dispose();
        _al = null;
        _alc = null;
    }
}
