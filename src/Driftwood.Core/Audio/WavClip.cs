namespace Driftwood.Core.Audio;

/// <summary>Decoded audio: interleaved 16-bit samples, and what they mean.</summary>
/// <param name="Samples">Interleaved, signed, 16-bit.</param>
/// <param name="Channels">1 or 2.</param>
/// <param name="SampleRate">Frames a second.</param>
public sealed record WavClip(short[] Samples, int Channels, int SampleRate)
{
    /// <summary>Frames, which is samples divided by channels.</summary>
    public int Frames => Samples.Length / Math.Max(Channels, 1);

    public float Seconds => Frames / (float)Math.Max(SampleRate, 1);

    /// <summary>
    /// The same clip folded to one channel.
    /// </summary>
    /// <remarks>
    /// Positional audio needs mono. A stereo buffer already carries its own left and right, so a
    /// device given one has nowhere to put the panning that says where in the world the sound is —
    /// it plays it flat, at the same volume, wherever the listener stands. Most of the pack is
    /// stereo, so most of it would be non-positional if this were left out.
    /// </remarks>
    public WavClip ToMono()
    {
        if (Channels == 1) return this;

        var frames = Frames;
        var mono = new short[frames];
        for (var f = 0; f < frames; f++)
        {
            var sum = 0;
            for (var c = 0; c < Channels; c++) sum += Samples[f * Channels + c];
            mono[f] = (short)(sum / Channels);
        }

        return new WavClip(mono, 1, SampleRate);
    }

    /// <summary>Loudest sample, 0 to 1. Zero means the file decoded to silence.</summary>
    public float Peak
    {
        get
        {
            var peak = 0;
            foreach (var s in Samples) peak = Math.Max(peak, Math.Abs((int)s));
            return peak / 32768f;
        }
    }
}

/// <summary>
/// Reads a RIFF WAVE file into samples.
/// </summary>
/// <remarks>
/// <para>Ours, like the PNG decoder, and for the same reason: the alternative was a licence to keep
/// answering for in a project that already reads other people's art and will read their sounds
/// next. A WAVE file is a chunk list with a header and a block of samples in it, and that is nearly
/// the whole format.</para>
/// <para>Chunks are walked rather than assumed. The header is not always at offset twelve and the
/// samples are not always at forty-four — editors drop <c>LIST</c>, <c>fact</c> and <c>cue</c>
/// chunks in between, and a reader that indexes by constant reads metadata as audio and plays a
/// burst of noise. Walking costs a loop.</para>
/// </remarks>
public static class Wav
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>Decodes a file, or explains why it could not.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out WavClip? clip, out string fault)
    {
        clip = null;
        fault = "";

        if (bytes.Length < 12 || !Matches(bytes, 0, "RIFF") || !Matches(bytes, 8, "WAVE"))
        {
            fault = "not a RIFF WAVE file";
            return false;
        }

        ushort format = 0, channels = 0, bits = 0;
        var rate = 0;
        var haveFormat = false;

        var at = 12;
        while (at + 8 <= bytes.Length)
        {
            var size = (int)ReadU32(bytes, at + 4);
            var body = at + 8;
            if (size < 0 || body + size > bytes.Length) size = bytes.Length - body;

            if (Matches(bytes, at, "fmt ") && size >= 16)
            {
                format = ReadU16(bytes, body);
                channels = ReadU16(bytes, body + 2);
                rate = (int)ReadU32(bytes, body + 4);
                bits = ReadU16(bytes, body + 14);

                // The extensible header repeats the real format in its own tail. Without following
                // it, every modern 24-bit file reads as an unknown format.
                if (format == FormatExtensible && size >= 40) format = ReadU16(bytes, body + 24);
                haveFormat = true;
            }
            else if (Matches(bytes, at, "data"))
            {
                if (!haveFormat)
                {
                    fault = "samples arrived before the format chunk";
                    return false;
                }

                if (channels is 0 or > 2)
                {
                    fault = $"{channels} channels, wanted 1 or 2";
                    return false;
                }

                if (rate <= 0)
                {
                    fault = $"sample rate {rate}";
                    return false;
                }

                var samples = Convert(bytes.Slice(body, size), format, bits, out fault);
                if (samples is null) return false;

                clip = new WavClip(samples, channels, rate);
                return true;
            }

            // Chunks are word aligned; an odd size carries a pad byte the length does not count.
            at = body + size + (size & 1);
        }

        fault = haveFormat ? "no data chunk" : "no format chunk";
        return false;
    }

    /// <summary>Brings any of the sample formats we accept to signed 16-bit.</summary>
    private static short[]? Convert(ReadOnlySpan<byte> data, ushort format, ushort bits, out string fault)
    {
        fault = "";

        switch (format, bits)
        {
            case (FormatPcm, 8):
            {
                // Eight-bit PCM is unsigned, alone among the widths. Reading it as signed inverts
                // the waveform's centre and turns quiet passages into full-scale buzz.
                var samples = new short[data.Length];
                for (var i = 0; i < data.Length; i++) samples[i] = (short)((data[i] - 128) << 8);
                return samples;
            }

            case (FormatPcm, 16):
            {
                var samples = new short[data.Length / 2];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = (short)(data[i * 2] | (data[i * 2 + 1] << 8));
                return samples;
            }

            case (FormatPcm, 24):
            {
                var samples = new short[data.Length / 3];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = (short)(data[i * 3 + 1] | (data[i * 3 + 2] << 8));
                return samples;
            }

            case (FormatPcm, 32):
            {
                var samples = new short[data.Length / 4];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = (short)(data[i * 4 + 2] | (data[i * 4 + 3] << 8));
                return samples;
            }

            case (FormatFloat, 32):
            {
                var samples = new short[data.Length / 4];
                for (var i = 0; i < samples.Length; i++)
                {
                    var value = BitConverter.ToSingle(data.Slice(i * 4, 4));
                    samples[i] = (short)Math.Clamp(value * 32767f, -32768f, 32767f);
                }
                return samples;
            }

            default:
                fault = $"format {format} at {bits} bits is not one this reads";
                return null;
        }
    }

    private static bool Matches(ReadOnlySpan<byte> bytes, int at, string tag) =>
        at + 4 <= bytes.Length
        && bytes[at] == tag[0] && bytes[at + 1] == tag[1]
        && bytes[at + 2] == tag[2] && bytes[at + 3] == tag[3];

    private static ushort ReadU16(ReadOnlySpan<byte> b, int at) => (ushort)(b[at] | (b[at + 1] << 8));

    private static uint ReadU32(ReadOnlySpan<byte> b, int at) =>
        (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));
}
