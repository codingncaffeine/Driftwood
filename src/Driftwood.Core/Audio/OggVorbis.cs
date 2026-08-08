namespace Driftwood.Core.Audio;

/// <summary>
/// Reads an Ogg Vorbis file into samples.
/// </summary>
/// <remarks>
/// <para>Ours, like the PNG and WAVE readers, and for the same three reasons: a decoder library is a
/// licence to keep answering for; the sound packs this game is built to read ship their audio as
/// .ogg, so reading it is part of reading packs at all; and shipping compressed audio keeps twenty
/// megabytes of sound from being two hundred in a public repository whose history never shrinks.</para>
/// <para>Vorbis is the largest format in the project by some distance, and the parts have very
/// different failure modes, so each is checked at its own level. The Ogg page layer carries a real
/// CRC and it is verified — a corrupted file fails by name here rather than decoding into a burst of
/// noise. The transform at the bottom (<see cref="Mdct"/>) has a fast path and a slow one: the slow
/// path is the specification's own formula written out, the fast one is what runs, and
/// <see cref="ImdctSelfTest"/> holds them to each other at every block size the format allows —
/// an index or sign slip in the fast path cannot survive that comparison.</para>
/// <para>Two deliberate refusals, both named when they happen: floor type 0, a legacy curve no
/// mainstream encoder has written this century, and more than two channels, which
/// <see cref="WavClip"/> does not carry. Every file the game ships decodes without meeting
/// either.</para>
/// <para>A packet that ends early is not a broken file. The format truncates the last packet of a
/// stream on purpose, and the rule everywhere below is the specification's: reads past the end of a
/// packet stop the decode of that piece and everything undecoded is zero.</para>
/// </remarks>
public static class OggVorbis
{
    /// <summary>Decodes a file, or explains why it could not.</summary>
    public static bool TryDecode(byte[] bytes, out WavClip? clip, out string fault)
    {
        clip = null;
        try
        {
            var packets = new OggStream(bytes);
            var decoder = new VorbisDecoder(packets);
            clip = decoder.ReadAll();
            fault = "";
            return true;
        }
        catch (FormatFault f)
        {
            fault = f.Message;
            return false;
        }
        catch (EndOfPacket)
        {
            // Truncation inside the headers. In the audio it is a normal ending; here there is
            // nothing to decode with.
            fault = "file ends inside its headers";
            return false;
        }
    }

    /// <summary>
    /// Proves the fast transform against the specification's own formula, at every block size the
    /// format allows. Null when they agree; the worst disagreement, named, when they do not.
    /// </summary>
    /// <remarks>
    /// The fast path is two quarter-size FFTs, a pre- and post-rotation and three mirror rules, any
    /// one of which could be off by a sign or an index and still produce plausible-sounding audio.
    /// The slow path is eight lines that cannot be wrong in an interesting way. Holding one to the
    /// other on random spectra is what lets the fast one be trusted at all.
    /// </remarks>
    public static string? ImdctSelfTest()
    {
        var random = new Random(4172);
        for (var n = 64; n <= 8192; n *= 2)
        {
            var spectrum = new float[n / 2];
            for (var i = 0; i < spectrum.Length; i++) spectrum[i] = (float)(random.NextDouble() * 2 - 1);

            var fast = new double[n];
            Mdct.PlanFor(n).Inverse(spectrum, fast, new Mdct.Scratch(n));
            var slow = Mdct.NaiveInverse(spectrum, n);

            double worst = 0;
            for (var j = 0; j < n; j++) worst = Math.Max(worst, Math.Abs(fast[j] - slow[j]));

            // The outputs are sums of n/2 unit-scale terms; a wrong fast path is off by O(1).
            if (worst > 1e-6 * n)
                return $"imdct fast path disagrees with the spec formula at n={n} by {worst:0.###e0}";
        }

        return null;
    }

    /// <summary>
    /// The container's shape in a sentence — pages, packets, position markers, chains — for
    /// diagnosing a file that decodes to the wrong length. Costs a full parse; not for gameplay.
    /// </summary>
    public static string Describe(byte[] bytes)
    {
        var pages = 0;
        var chains = 0;
        var afterEos = 0;
        var eosSeen = false;
        long lastGranule = -1;
        var serials = new HashSet<int>();

        var at = 0;
        while (at + 27 <= bytes.Length)
        {
            if (bytes[at] != 'O' || bytes[at + 1] != 'g' || bytes[at + 2] != 'g' || bytes[at + 3] != 'S') break;
            var flags = bytes[at + 5];
            long granule = 0;
            for (var i = 7; i >= 0; i--) granule = (granule << 8) | bytes[at + 6 + i];
            var serial = bytes[at + 14] | (bytes[at + 15] << 8) | (bytes[at + 16] << 16) | (bytes[at + 17] << 24);
            var segments = bytes[at + 26];
            var body = 0;
            for (var s = 0; s < segments; s++) body += bytes[at + 27 + s];

            pages++;
            if (eosSeen) afterEos++;
            if ((flags & 2) != 0) chains++;
            if ((flags & 4) != 0) eosSeen = true;
            if (granule >= 0) lastGranule = granule;
            serials.Add(serial);

            at = 27 + segments + body + at;
        }

        return $"{pages} pages, {serials.Count} serial(s), {chains} stream start(s), " +
            $"{afterEos} page(s) after the first end-of-stream, last granule {lastGranule}, " +
            $"{bytes.Length - at} byte(s) unread";
    }

    /// <summary>Per-frame decode trace — block sizes, window flags, emit counts — for diagnosis.</summary>
    public static string DescribeFrames(byte[] bytes, int limit)
    {
        var stream = new OggStream(bytes);
        var decoder = new VorbisDecoder(stream) { Trace = [] };
        try
        {
            decoder.ReadAll();
        }
        catch (FormatFault)
        {
        }
        return string.Join("\n", decoder.Trace.Take(limit));
    }

    /// <summary>The one exception this file throws for a malformed file, carrying the reason.</summary>
    private sealed class FormatFault(string message) : Exception(message);

    /// <summary>Thrown by reads that run off the end of a packet. Caught, never reported.</summary>
    /// <remarks>
    /// A single cached instance because this is control flow, not failure — the format truncates
    /// packets deliberately and the specification says what a short read means at each layer.
    /// </remarks>
    private sealed class EndOfPacket : Exception
    {
        public static readonly EndOfPacket Instance = new();
        private EndOfPacket() { }
    }

    // ── The Ogg layer: pages with a checksum, carrying packets ─────────────────────────────────

    /// <summary>
    /// Walks the container's pages and hands back whole packets for one logical stream.
    /// </summary>
    private sealed class OggStream
    {
        private readonly List<byte[]> _packets = [];
        private int _next;

        /// <summary>Total frames in the stream, from the last page's position marker, or -1.</summary>
        public long FinalGranule { get; private set; } = -1;

        public OggStream(byte[] bytes)
        {
            var serial = 0;
            var haveSerial = false;
            var partial = new MemoryStream();
            var at = 0;

            while (at + 27 <= bytes.Length)
            {
                if (bytes[at] != 'O' || bytes[at + 1] != 'g' || bytes[at + 2] != 'g' || bytes[at + 3] != 'S')
                {
                    if (!haveSerial) throw new FormatFault("not an Ogg file");
                    break;
                }

                if (bytes[at + 4] != 0) throw new FormatFault($"Ogg page version {bytes[at + 4]}");

                var flags = bytes[at + 5];
                var granule = ReadI64(bytes, at + 6);
                var pageSerial = ReadI32(bytes, at + 14);
                var segments = bytes[at + 26];
                var head = at + 27 + segments;
                if (head > bytes.Length) throw new FormatFault("Ogg page runs off the end of the file");

                var bodySize = 0;
                for (var s = 0; s < segments; s++) bodySize += bytes[at + 27 + s];
                if (head + bodySize > bytes.Length) throw new FormatFault("Ogg page runs off the end of the file");

                if (!Crc.Verify(bytes, at, head + bodySize - at))
                    throw new FormatFault("Ogg page fails its checksum — the file is damaged");

                // The stream this reader follows is the first one that opens with a Vorbis
                // identification packet. Anything multiplexed beside it is skipped whole.
                if (!haveSerial)
                {
                    var body = head;
                    if (bodySize >= 7 && bytes[body] == 1 && Matches(bytes, body + 1, "vorbis"))
                    {
                        serial = pageSerial;
                        haveSerial = true;
                    }
                    else if ((flags & 2) != 0)
                    {
                        at = head + bodySize;
                        continue;
                    }
                    else
                    {
                        throw new FormatFault("not a Vorbis stream");
                    }
                }

                if (pageSerial != serial)
                {
                    at = head + bodySize;
                    continue;
                }

                // A page that does not continue an earlier packet starts fresh; a continuation
                // flag with nothing held is a hole, and what was held is unfinishable.
                if ((flags & 1) == 0 && partial.Length > 0) partial.SetLength(0);

                var offset = head;
                for (var s = 0; s < segments; s++)
                {
                    var lacing = bytes[at + 27 + s];
                    partial.Write(bytes, offset, lacing);
                    offset += lacing;

                    if (lacing < 255)
                    {
                        _packets.Add(partial.ToArray());
                        partial.SetLength(0);
                    }
                }

                if (granule >= 0) FinalGranule = granule;

                at = head + bodySize;
                if ((flags & 4) != 0) break;
            }

            if (_packets.Count == 0) throw new FormatFault("no packets in the stream");
        }

        public byte[]? NextPacket() => _next < _packets.Count ? _packets[_next++] : null;

        private static bool Matches(byte[] b, int at, string tag)
        {
            for (var i = 0; i < tag.Length; i++)
                if (b[at + i] != tag[i])
                    return false;
            return true;
        }

        private static long ReadI64(byte[] b, int at)
        {
            var v = 0L;
            for (var i = 7; i >= 0; i--) v = (v << 8) | b[at + i];
            return v;
        }

        private static int ReadI32(byte[] b, int at) =>
            b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24);
    }

    /// <summary>The container's own CRC-32: polynomial 0x04C11D B7, no reflection, no final XOR.</summary>
    private static class Crc
    {
        private static readonly uint[] Table = Build();

        private static uint[] Build()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var r = i << 24;
                for (var b = 0; b < 8; b++) r = (r & 0x80000000u) != 0 ? (r << 1) ^ 0x04C11DB7u : r << 1;
                table[i] = r;
            }
            return table;
        }

        /// <summary>Checks one page, treating the four checksum bytes as zero the way the writer did.</summary>
        public static bool Verify(byte[] bytes, int start, int length)
        {
            var claimed = (uint)(bytes[start + 22] | (bytes[start + 23] << 8)
                | (bytes[start + 24] << 16) | (bytes[start + 25] << 24));

            var crc = 0u;
            for (var i = 0; i < length; i++)
            {
                var b = i is >= 22 and < 26 ? (byte)0 : bytes[start + i];
                crc = (crc << 8) ^ Table[(crc >> 24) ^ b];
            }

            return crc == claimed;
        }
    }

    // ── The bit layer: least significant bit first, as the format packs ────────────────────────

    private sealed class PacketReader(byte[] packet, int start = 0)
    {
        private readonly byte[] _bytes = packet;
        private int _at = start;
        private int _bit;

        public int Read(int bits)
        {
            var value = 0L;
            var got = 0;
            while (got < bits)
            {
                if (_at >= _bytes.Length) throw EndOfPacket.Instance;
                var take = Math.Min(8 - _bit, bits - got);
                var chunk = (_bytes[_at] >> _bit) & ((1 << take) - 1);
                value |= (long)chunk << got;
                got += take;
                _bit += take;
                if (_bit == 8)
                {
                    _bit = 0;
                    _at++;
                }
            }
            return (int)value;
        }

        public uint ReadUint(int bits) => (uint)Read(bits);

        public bool ReadFlag() => Read(1) != 0;
    }

    /// <summary>Bits needed to write a number: 0 for 0, 3 for 7, 4 for 8.</summary>
    private static int ILog(int x)
    {
        var bits = 0;
        while (x > 0)
        {
            bits++;
            x >>= 1;
        }
        return bits;
    }

    /// <summary>The format's own 32-bit float packing for codebook scaling values.</summary>
    private static float UnpackFloat(uint x)
    {
        double mantissa = x & 0x1FFFFF;
        var exponent = (int)((x & 0x7FE00000) >> 21);
        if ((x & 0x80000000u) != 0) mantissa = -mantissa;
        return (float)(mantissa * Math.Pow(2.0, exponent - 788));
    }

    // ── Codebooks: a Huffman tree, sometimes with a vector table behind it ─────────────────────

    private sealed class Codebook
    {
        public int Dimensions;
        public int Entries;

        /// <summary>The decode tree: positive is a node index, negative is ~entry.</summary>
        private int[] _left = [];
        private int[] _right = [];
        private int _nodes;

        /// <summary>Vector table for lookup types 1 and 2, entry-major, or empty for type 0.</summary>
        public float[] Vectors = [];
        public bool HasVectors => Vectors.Length > 0;

        public static Codebook Read(PacketReader r)
        {
            if (r.Read(24) != 0x564342) throw new FormatFault("codebook sync pattern missing");

            var book = new Codebook
            {
                Dimensions = r.Read(16),
                Entries = r.Read(24),
            };
            if (book.Entries == 0 || book.Entries > 1 << 22)
                throw new FormatFault($"codebook with {book.Entries} entries");

            var lengths = new int[book.Entries];
            if (r.ReadFlag())
            {
                // Ordered: runs of entries share a length that only ever climbs.
                var length = r.Read(5) + 1;
                var entry = 0;
                while (entry < book.Entries)
                {
                    var run = r.Read(ILog(book.Entries - entry));
                    if (entry + run > book.Entries) throw new FormatFault("ordered codebook overruns its entries");
                    for (var i = 0; i < run; i++) lengths[entry++] = length;
                    length++;
                    if (length > 32) throw new FormatFault("codeword longer than 32 bits");
                }
            }
            else
            {
                var sparse = r.ReadFlag();
                for (var entry = 0; entry < book.Entries; entry++)
                {
                    if (sparse && !r.ReadFlag()) continue;
                    lengths[entry] = r.Read(5) + 1;
                }
            }

            book.BuildTree(lengths);
            book.ReadLookup(r);
            return book;
        }

        /// <summary>
        /// Assigns each used entry the lowest untaken codeword of its length, in entry order —
        /// the format's rule — and builds the walkable tree from them.
        /// </summary>
        /// <remarks>
        /// <c>available[z]</c> holds the single unassigned prefix of length z, left-aligned in 32
        /// bits. Taking a codeword of length L consumes the shortest available prefix that covers
        /// it and re-opens the lengths in between — the textbook first-fit assignment, done without
        /// building the tree twice.
        /// </remarks>
        private void BuildTree(int[] lengths)
        {
            var capacity = 2 * Entries + 2;
            _left = new int[capacity];
            _right = new int[capacity];
            _left[0] = int.MinValue;
            _right[0] = int.MinValue;
            _nodes = 1;

            var available = new uint[33];
            var first = true;

            for (var entry = 0; entry < Entries; entry++)
            {
                var length = lengths[entry];
                if (length == 0) continue;

                uint code;
                if (first)
                {
                    code = 0;
                    for (var i = 1; i <= length; i++) available[i] = 1u << (32 - i);
                    first = false;
                }
                else
                {
                    var z = length;
                    while (z > 0 && available[z] == 0) z--;
                    if (z == 0) throw new FormatFault("codebook tree is over-full");

                    code = available[z];
                    available[z] = 0;
                    for (var y = length; y > z; y--) available[y] = code + (1u << (32 - y));
                }

                Insert(code, length, entry);
            }

            // A tree with exactly one codeword is legal and the loop above gives it length as
            // written; an unfinished tree on anything larger is a writer's bug, not ours to fix.
        }

        private void Insert(uint code, int length, int entry)
        {
            var node = 0;
            for (var depth = 0; depth < length; depth++)
            {
                var bit = (code >> (31 - depth)) & 1;
                var child = bit == 0 ? _left[node] : _right[node];

                if (depth == length - 1)
                {
                    if (bit == 0) _left[node] = ~entry;
                    else _right[node] = ~entry;
                    return;
                }

                if (child == int.MinValue)
                {
                    // A legal tree of E entries wants at most E-1 branch nodes, but nothing says
                    // the file is legal: two 32-bit codewords alone share a spine of thirty.
                    if (_nodes == _left.Length)
                    {
                        Array.Resize(ref _left, _left.Length * 2);
                        Array.Resize(ref _right, _right.Length * 2);
                    }

                    child = _nodes;
                    _left[_nodes] = int.MinValue;
                    _right[_nodes] = int.MinValue;
                    _nodes++;

                    if (bit == 0) _left[node] = child;
                    else _right[node] = child;
                }
                else if (child < 0)
                {
                    throw new FormatFault("codebook codeword under another's prefix");
                }

                node = child;
            }
        }

        private void ReadLookup(PacketReader r)
        {
            var type = r.Read(4);
            if (type == 0) return;
            if (type > 2) throw new FormatFault($"codebook lookup type {type}");

            var min = UnpackFloat(r.ReadUint(32));
            var delta = UnpackFloat(r.ReadUint(32));
            var valueBits = r.Read(4) + 1;
            var sequence = r.ReadFlag();

            var lookupValues = type == 1 ? LargestRoot() : Entries * Dimensions;
            var multiplicands = new int[lookupValues];
            for (var i = 0; i < lookupValues; i++) multiplicands[i] = r.Read(valueBits);

            if ((long)Entries * Dimensions > 1 << 24)
                throw new FormatFault("codebook vector table implausibly large");

            Vectors = new float[Entries * Dimensions];
            for (var entry = 0; entry < Entries; entry++)
            {
                var last = 0f;
                var divisor = 1;
                for (var d = 0; d < Dimensions; d++)
                {
                    int index;
                    if (type == 1)
                    {
                        index = entry / divisor % lookupValues;
                        divisor *= lookupValues;
                    }
                    else
                    {
                        index = entry * Dimensions + d;
                    }

                    var value = multiplicands[index] * delta + min + last;
                    Vectors[entry * Dimensions + d] = value;
                    if (sequence) last = value;
                }
            }
        }

        /// <summary>The largest whole number whose <see cref="Dimensions"/>th power fits the entries.</summary>
        private int LargestRoot()
        {
            var root = 1;
            while (Power(root + 1) <= Entries) root++;
            return root;

            long Power(int b)
            {
                var v = 1L;
                for (var i = 0; i < Dimensions; i++)
                {
                    v *= b;
                    if (v > int.MaxValue) return long.MaxValue;
                }
                return v;
            }
        }

        /// <summary>One symbol off the stream, walking the tree a bit at a time.</summary>
        public int DecodeScalar(PacketReader r)
        {
            var node = 0;
            while (true)
            {
                var child = r.ReadFlag() ? _right[node] : _left[node];
                if (child < 0)
                {
                    if (child == int.MinValue) throw new FormatFault("codeword leads nowhere in this codebook");
                    return ~child;
                }
                node = child;
            }
        }

        /// <summary>A symbol as the offset of its vector in <see cref="Vectors"/>.</summary>
        public int DecodeVector(PacketReader r) => DecodeScalar(r) * Dimensions;
    }

    // ── Floors: the spectral envelope, as a piecewise line in dB ───────────────────────────────

    private sealed class Floor1
    {
        private int[] _partitionClasses = [];
        private int[] _classDimensions = [];
        private int[] _classSubclassBits = [];
        private int[] _classMasterbooks = [];
        private int[][] _subclassBooks = [];
        private int _multiplier;
        private int[] _xList = [];
        private int[] _sortOrder = [];
        private int[] _lowNeighbour = [];
        private int[] _highNeighbour = [];

        /// <summary>Amplitude for each of the 256 coded dB steps, spanning about −140 dB to full.</summary>
        private static readonly float[] InverseDb = BuildInverseDb();

        private static float[] BuildInverseDb()
        {
            var table = new float[256];
            for (var i = 0; i < 256; i++) table[i] = (float)Math.Pow(10.0, -(255 - i) * 7.0 / 256.0);
            return table;
        }

        public static Floor1 Read(PacketReader r, Codebook[] books)
        {
            var floor = new Floor1();
            var partitions = r.Read(5);
            floor._partitionClasses = new int[partitions];
            var maxClass = -1;
            for (var i = 0; i < partitions; i++)
            {
                floor._partitionClasses[i] = r.Read(4);
                maxClass = Math.Max(maxClass, floor._partitionClasses[i]);
            }

            var classes = maxClass + 1;
            floor._classDimensions = new int[classes];
            floor._classSubclassBits = new int[classes];
            floor._classMasterbooks = new int[classes];
            floor._subclassBooks = new int[classes][];

            for (var c = 0; c < classes; c++)
            {
                floor._classDimensions[c] = r.Read(3) + 1;
                floor._classSubclassBits[c] = r.Read(2);
                floor._classMasterbooks[c] = floor._classSubclassBits[c] > 0 ? r.Read(8) : -1;
                if (floor._classMasterbooks[c] >= books.Length)
                    throw new FormatFault("floor names a codebook that does not exist");

                var subclasses = 1 << floor._classSubclassBits[c];
                floor._subclassBooks[c] = new int[subclasses];
                for (var s = 0; s < subclasses; s++)
                {
                    var book = r.Read(8) - 1;
                    if (book >= books.Length) throw new FormatFault("floor names a codebook that does not exist");
                    floor._subclassBooks[c][s] = book;
                }
            }

            floor._multiplier = r.Read(2) + 1;
            var rangeBits = r.Read(4);

            var values = new List<int> { 0, 1 << rangeBits };
            foreach (var cls in floor._partitionClasses)
                for (var d = 0; d < floor._classDimensions[cls]; d++)
                    values.Add(r.Read(rangeBits));

            if (values.Count > 65) throw new FormatFault("floor with more than 65 points");
            floor._xList = [.. values];

            // The curve walks the points in x order, and every point predicts from the nearest
            // decoded points either side of it; both orders depend only on the header, so both
            // are settled here rather than per frame.
            floor._sortOrder = new int[floor._xList.Length];
            for (var i = 0; i < floor._sortOrder.Length; i++) floor._sortOrder[i] = i;
            Array.Sort(floor._sortOrder, (a, b) => floor._xList[a].CompareTo(floor._xList[b]));

            floor._lowNeighbour = new int[floor._xList.Length];
            floor._highNeighbour = new int[floor._xList.Length];
            for (var i = 2; i < floor._xList.Length; i++)
            {
                floor._lowNeighbour[i] = Neighbour(floor._xList, i, below: true);
                floor._highNeighbour[i] = Neighbour(floor._xList, i, below: false);
            }

            return floor;
        }

        private static int Neighbour(int[] x, int i, bool below)
        {
            var best = -1;
            for (var j = 0; j < i; j++)
            {
                if (below ? x[j] >= x[i] : x[j] <= x[i]) continue;
                if (best < 0) best = j;
                else if (below ? x[j] > x[best] : x[j] < x[best]) best = j;
            }
            return best;
        }

        private int Range => _multiplier switch { 1 => 256, 2 => 128, 3 => 86, _ => 64 };

        /// <summary>
        /// Reads one channel's envelope points, or reports the channel silent this frame.
        /// </summary>
        public bool DecodeFrame(PacketReader r, Codebook[] books, int[] yOut)
        {
            if (!r.ReadFlag()) return false;

            var range = Range;
            yOut[0] = r.Read(ILog(range - 1));
            yOut[1] = r.Read(ILog(range - 1));

            var offset = 2;
            foreach (var cls in _partitionClasses)
            {
                var dim = _classDimensions[cls];
                var bits = _classSubclassBits[cls];
                var mask = (1 << bits) - 1;

                var cval = 0;
                if (bits > 0) cval = books[_classMasterbooks[cls]].DecodeScalar(r);

                for (var d = 0; d < dim; d++)
                {
                    var book = _subclassBooks[cls][cval & mask];
                    cval >>= bits;
                    yOut[offset + d] = book >= 0 ? books[book].DecodeScalar(r) : 0;
                }
                offset += dim;
            }

            return true;
        }

        /// <summary>
        /// Turns decoded points into the amplitude curve, one value per spectral line.
        /// </summary>
        /// <remarks>
        /// Every step here is integer arithmetic from the specification — the neighbour
        /// prediction, the room test, the line renderer. A floating-point rendition drifts from
        /// what the encoder computed by a step here and there, and a step in this curve is a step
        /// in a dB table, which is audible as a flutter over the whole band.
        /// </remarks>
        public void Synthesise(int[] y, float[] curve, int n2)
        {
            var count = _xList.Length;
            var range = Range;
            var final = new int[count];
            var stepFlags = new bool[count];

            final[0] = y[0];
            final[1] = y[1];
            stepFlags[0] = true;
            stepFlags[1] = true;

            for (var i = 2; i < count; i++)
            {
                var low = _lowNeighbour[i];
                var high = _highNeighbour[i];
                var predicted = RenderPoint(_xList[low], final[low], _xList[high], final[high], _xList[i]);

                var value = y[i];
                var highRoom = range - predicted;
                var lowRoom = predicted;
                var room = 2 * Math.Min(highRoom, lowRoom);

                if (value != 0)
                {
                    // A deviating point pins its two anchors as well as itself: they are where
                    // its prediction was measured from, so the curve must bend through them even
                    // when their own values rode their predictions. Missing this leaves loud
                    // frames' floors one quantisation step out — audibly, a few percent off on
                    // every loud band, and nothing at all wrong anywhere quiet.
                    stepFlags[low] = true;
                    stepFlags[high] = true;
                    stepFlags[i] = true;
                    if (value >= room)
                    {
                        final[i] = highRoom > lowRoom ? value - lowRoom + predicted : predicted - (value - highRoom) - 1;
                    }
                    else
                    {
                        final[i] = (value & 1) != 0 ? predicted - ((value + 1) >> 1) : predicted + (value >> 1);
                    }
                }
                else
                {
                    stepFlags[i] = false;
                    final[i] = predicted;
                }

                final[i] = Math.Clamp(final[i], 0, range - 1);
            }

            var lx = 0;
            var ly = final[_sortOrder[0]] * _multiplier;
            foreach (var index in _sortOrder)
            {
                if (!stepFlags[index]) continue;

                var hx = _xList[index];
                var hy = final[index] * _multiplier;
                if (hx > lx) RenderLine(lx, ly, hx, hy, curve, n2);
                lx = hx;
                ly = hy;
                if (lx >= n2) break;
            }

            if (lx < n2)
            {
                var flat = InverseDb[Math.Clamp(ly, 0, 255)];
                for (var x = lx; x < n2; x++) curve[x] = flat;
            }
        }

        private static int RenderPoint(int x0, int y0, int x1, int y1, int x)
        {
            var dy = y1 - y0;
            var adx = x1 - x0;
            var err = Math.Abs(dy) * (x - x0);
            var off = adx == 0 ? 0 : err / adx;
            return dy < 0 ? y0 - off : y0 + off;
        }

        private static void RenderLine(int x0, int y0, int x1, int y1, float[] curve, int n2)
        {
            var dy = y1 - y0;
            var adx = x1 - x0;
            var ady = Math.Abs(dy);
            var baseStep = dy / adx;
            var sy = dy < 0 ? baseStep - 1 : baseStep + 1;
            ady -= Math.Abs(baseStep) * adx;

            var y = y0;
            var err = 0;
            if (x0 < n2) curve[x0] = InverseDb[Math.Clamp(y, 0, 255)];

            var limit = Math.Min(x1, n2);
            for (var x = x0 + 1; x < limit; x++)
            {
                err += ady;
                if (err >= adx)
                {
                    err -= adx;
                    y += sy;
                }
                else
                {
                    y += baseStep;
                }
                curve[x] = InverseDb[Math.Clamp(y, 0, 255)];
            }
        }
    }

    // ── Residues: what remains after the floor, vector-quantised in partitions ─────────────────

    private sealed class Residue
    {
        public int Type;
        private int _begin;
        private int _end;
        private int _partitionSize;
        private int _classifications;
        private int _classbook;
        private int[][] _books = [];

        public static Residue Read(PacketReader r, int type, Codebook[] books)
        {
            var residue = new Residue
            {
                Type = type,
                _begin = r.Read(24),
                _end = r.Read(24),
                _partitionSize = r.Read(24) + 1,
                _classifications = r.Read(6) + 1,
                _classbook = r.Read(8),
            };
            if (residue._classbook >= books.Length)
                throw new FormatFault("residue names a codebook that does not exist");

            var cascades = new int[residue._classifications];
            for (var c = 0; c < residue._classifications; c++)
            {
                var low = r.Read(3);
                var high = r.ReadFlag() ? r.Read(5) : 0;
                cascades[c] = (high << 3) | low;
            }

            residue._books = new int[residue._classifications][];
            for (var c = 0; c < residue._classifications; c++)
            {
                residue._books[c] = new int[8];
                for (var pass = 0; pass < 8; pass++)
                {
                    if ((cascades[c] & (1 << pass)) == 0)
                    {
                        residue._books[c][pass] = -1;
                        continue;
                    }

                    var book = r.Read(8);
                    if (book >= books.Length || !books[book].HasVectors)
                        throw new FormatFault("residue names a codebook with no vectors behind it");
                    residue._books[c][pass] = book;
                }
            }

            return residue;
        }

        /// <summary>
        /// Decodes into <paramref name="vectors"/>, one per channel, each n/2 long and already
        /// zeroed. Channels marked do-not-decode stay silent but keep their place in the
        /// interleave.
        /// </summary>
        public void Decode(PacketReader r, Codebook[] books, float[][] vectors, bool[] doNotDecode, int n2)
        {
            var channels = vectors.Length;

            // Type 2 winds every channel into one long vector before quantising; silence only
            // counts when the whole braid is silent.
            if (Type == 2)
            {
                var all = true;
                foreach (var skip in doNotDecode) all &= skip;
                if (all) return;

                var combined = _scratch ??= [];
                var need = channels * n2;
                if (combined.Length < need) _scratch = combined = new float[need];
                Array.Clear(combined, 0, need);

                DecodeInner(r, books, [combined], [false], need, 1);

                for (var c = 0; c < channels; c++)
                {
                    var vector = vectors[c];
                    for (var i = 0; i < n2; i++) vector[i] = combined[i * channels + c];
                }
                return;
            }

            DecodeInner(r, books, vectors, doNotDecode, n2, Type);
        }

        private float[]? _scratch;

        private void DecodeInner(
            PacketReader r, Codebook[] books, float[][] vectors, bool[] doNotDecode, int size, int type)
        {
            var classbook = books[_classbook];
            var classwords = classbook.Dimensions;

            var begin = Math.Min(_begin, size);
            var end = Math.Min(_end, size);
            var toRead = end - begin;
            if (toRead <= 0) return;

            var partitions = toRead / _partitionSize;
            var channels = vectors.Length;

            var classes = _classesScratch;
            if (classes is null || classes.Length < channels || (partitions > 0 && classes[0].Length < partitions + classwords))
            {
                classes = new int[channels][];
                for (var c = 0; c < channels; c++) classes[c] = new int[partitions + classwords];
                _classesScratch = classes;
            }

            try
            {
                for (var pass = 0; pass < 8; pass++)
                {
                    var partition = 0;
                    while (partition < partitions)
                    {
                        if (pass == 0)
                        {
                            for (var c = 0; c < channels; c++)
                            {
                                if (doNotDecode[c]) continue;
                                var temp = classbook.DecodeScalar(r);
                                for (var i = classwords - 1; i >= 0; i--)
                                {
                                    classes[c][partition + i] = temp % _classifications;
                                    temp /= _classifications;
                                }
                            }
                        }

                        for (var word = 0; word < classwords && partition < partitions; word++, partition++)
                        {
                            for (var c = 0; c < channels; c++)
                            {
                                if (doNotDecode[c]) continue;

                                var book = _books[classes[c][partition]][pass];
                                if (book < 0) continue;

                                var offset = begin + partition * _partitionSize;
                                if (type == 0) DecodeInterleaved(r, books[book], vectors[c], offset);
                                else DecodeSequential(r, books[book], vectors[c], offset);
                            }
                        }
                    }
                }
            }
            catch (EndOfPacket)
            {
                // The stream ended mid-residue: everything decoded so far stands and the rest of
                // the spectrum is zero, which is what the format means by ending a packet early.
            }
        }

        private int[][]? _classesScratch;

        // Both write guards below only matter to a malformed file: a partition size that does not
        // divide by the book's dimensions would otherwise write past its own patch of spectrum.

        private void DecodeInterleaved(PacketReader r, Codebook book, float[] vector, int offset)
        {
            var dims = book.Dimensions;
            var step = _partitionSize / dims;
            var limit = Math.Min(offset + _partitionSize, vector.Length);
            for (var i = 0; i < step; i++)
            {
                var at = book.DecodeVector(r);
                for (var d = 0; d < dims; d++)
                {
                    var into = offset + i + d * step;
                    if (into < limit) vector[into] += book.Vectors[at + d];
                }
            }
        }

        private void DecodeSequential(PacketReader r, Codebook book, float[] vector, int offset)
        {
            var dims = book.Dimensions;
            var limit = Math.Min(offset + _partitionSize, vector.Length);
            var wrote = 0;
            while (wrote < _partitionSize)
            {
                var at = book.DecodeVector(r);
                for (var d = 0; d < dims; d++)
                {
                    var into = offset + wrote + d;
                    if (into < limit) vector[into] += book.Vectors[at + d];
                }
                wrote += dims;
            }
        }
    }

    // ── Mappings and modes: which floor and residue serve which channel, at which block size ───

    private sealed class Mapping
    {
        public int[] Mux = [];
        public int[] SubmapFloor = [];
        public int[] SubmapResidue = [];
        public int[] CouplingMagnitude = [];
        public int[] CouplingAngle = [];
    }

    private readonly record struct Mode(bool LongBlock, int MappingIndex);

    // ── The decoder proper ─────────────────────────────────────────────────────────────────────

    private sealed class VorbisDecoder
    {
        private readonly OggStream _stream;
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly int[] _blockSizes = new int[2];

        private readonly Codebook[] _codebooks;
        private readonly Floor1[] _floors;
        private readonly Residue[] _residues;
        private readonly Mapping[] _mappings;
        private readonly Mode[] _modes;

        // Frame-to-frame state: each frame's tail waits to be folded into the next frame's head.
        private readonly float[][] _lap;
        private int _lapLength;
        private bool _primed;

        /// <summary>When set, one line per frame lands here. Diagnostic only.</summary>
        public List<string>? Trace;

        // Reused per frame, sized once to the long block.
        private readonly int[][] _floorY;
        private readonly bool[] _floorUsed;
        private readonly bool[] _residueNeeded;
        private readonly float[][] _residueVectors;
        private readonly float[] _curve;
        private readonly double[] _time;
        private readonly Mdct.Scratch _scratch;
        private readonly List<float>[] _output;

        public VorbisDecoder(OggStream stream)
        {
            _stream = stream;

            // ── Identification ──
            var id = NextHeaderPacket(1);
            if (id.Read(32) != 0)
                throw new FormatFault("Vorbis version is not zero");
            _channels = id.Read(8);
            _sampleRate = id.Read(32);
            if (_channels is 0 or > 2) throw new FormatFault($"{_channels} channels, wanted 1 or 2");
            if (_sampleRate <= 0) throw new FormatFault($"sample rate {_sampleRate}");
            id.Read(32); // bitrate maximum
            id.Read(32); // bitrate nominal
            id.Read(32); // bitrate minimum
            _blockSizes[0] = 1 << id.Read(4);
            _blockSizes[1] = 1 << id.Read(4);
            if (_blockSizes[0] < 64 || _blockSizes[1] > 8192 || _blockSizes[0] > _blockSizes[1])
                throw new FormatFault($"block sizes {_blockSizes[0]} and {_blockSizes[1]}");
            if (!id.ReadFlag()) throw new FormatFault("identification header unframed");

            // ── Comments: somebody's tags, walked past whole ──
            NextHeaderPacket(3);

            // ── Setup ──
            var setup = NextHeaderPacket(5);

            _codebooks = new Codebook[setup.Read(8) + 1];
            for (var i = 0; i < _codebooks.Length; i++) _codebooks[i] = Codebook.Read(setup);

            var timeCount = setup.Read(6) + 1;
            for (var i = 0; i < timeCount; i++)
                if (setup.Read(16) != 0)
                    throw new FormatFault("time transform is not type zero");

            _floors = new Floor1[setup.Read(6) + 1];
            for (var i = 0; i < _floors.Length; i++)
            {
                var type = setup.Read(16);
                if (type == 0) throw new FormatFault("floor type 0 is not one this reads");
                if (type != 1) throw new FormatFault($"floor type {type}");
                _floors[i] = Floor1.Read(setup, _codebooks);
            }

            _residues = new Residue[setup.Read(6) + 1];
            for (var i = 0; i < _residues.Length; i++)
            {
                var type = setup.Read(16);
                if (type > 2) throw new FormatFault($"residue type {type}");
                _residues[i] = Residue.Read(setup, type, _codebooks);
            }

            _mappings = new Mapping[setup.Read(6) + 1];
            for (var i = 0; i < _mappings.Length; i++) _mappings[i] = ReadMapping(setup);

            _modes = new Mode[setup.Read(6) + 1];
            for (var i = 0; i < _modes.Length; i++)
            {
                var longBlock = setup.ReadFlag();
                if (setup.Read(16) != 0) throw new FormatFault("window type is not zero");
                if (setup.Read(16) != 0) throw new FormatFault("transform type is not zero");
                var mapping = setup.Read(8);
                if (mapping >= _mappings.Length) throw new FormatFault("mode names a mapping that does not exist");
                _modes[i] = new Mode(longBlock, mapping);
            }

            var n2 = _blockSizes[1] / 2;
            _lap = new float[_channels][];
            _floorY = new int[_channels][];
            _residueVectors = new float[_channels][];
            _output = new List<float>[_channels];
            for (var c = 0; c < _channels; c++)
            {
                _lap[c] = new float[n2];
                _floorY[c] = new int[65];
                _residueVectors[c] = new float[n2];
                _output[c] = [];
            }
            _floorUsed = new bool[_channels];
            _residueNeeded = new bool[_channels];
            _curve = new float[n2];
            _time = new double[_blockSizes[1]];
            _scratch = new Mdct.Scratch(_blockSizes[1]);
        }

        /// <summary>
        /// The next packet as a reader already past its seven bytes of type and magic. The
        /// comment packet is somebody's tags: taken whole here and never read further.
        /// </summary>
        private PacketReader NextHeaderPacket(int expectedType)
        {
            var packet = _stream.NextPacket() ?? throw new FormatFault("stream ends inside the headers");
            if (packet.Length < 7 || packet[0] != expectedType
                || packet[1] != 'v' || packet[2] != 'o' || packet[3] != 'r'
                || packet[4] != 'b' || packet[5] != 'i' || packet[6] != 's')
                throw new FormatFault($"header packet {expectedType} missing or out of order");

            return new PacketReader(packet, 7);
        }

        private Mapping ReadMapping(PacketReader r)
        {
            if (r.Read(16) != 0) throw new FormatFault("mapping type is not zero");

            var mapping = new Mapping();
            var submaps = r.ReadFlag() ? r.Read(4) + 1 : 1;

            if (r.ReadFlag())
            {
                var steps = r.Read(8) + 1;
                mapping.CouplingMagnitude = new int[steps];
                mapping.CouplingAngle = new int[steps];
                var bits = ILog(_channels - 1);
                for (var s = 0; s < steps; s++)
                {
                    mapping.CouplingMagnitude[s] = r.Read(bits);
                    mapping.CouplingAngle[s] = r.Read(bits);
                    if (mapping.CouplingMagnitude[s] == mapping.CouplingAngle[s]
                        || mapping.CouplingMagnitude[s] >= _channels
                        || mapping.CouplingAngle[s] >= _channels)
                        throw new FormatFault("coupling step names an impossible channel pair");
                }
            }

            if (r.Read(2) != 0) throw new FormatFault("mapping reserved bits set");

            mapping.Mux = new int[_channels];
            if (submaps > 1)
                for (var c = 0; c < _channels; c++)
                {
                    mapping.Mux[c] = r.Read(4);
                    if (mapping.Mux[c] >= submaps) throw new FormatFault("channel routed to a submap that does not exist");
                }

            mapping.SubmapFloor = new int[submaps];
            mapping.SubmapResidue = new int[submaps];
            for (var s = 0; s < submaps; s++)
            {
                r.Read(8); // discarded time configuration
                mapping.SubmapFloor[s] = r.Read(8);
                mapping.SubmapResidue[s] = r.Read(8);
                if (mapping.SubmapFloor[s] >= _floors.Length || mapping.SubmapResidue[s] >= _residues.Length)
                    throw new FormatFault("submap names a floor or residue that does not exist");
            }

            return mapping;
        }

        public WavClip ReadAll()
        {
            while (_stream.NextPacket() is { } packet)
            {
                if (packet.Length == 0) continue;
                try
                {
                    DecodePacket(packet);
                }
                catch (EndOfPacket)
                {
                    // Ran out before the mode and window were even known: nothing to keep.
                }
            }

            var frames = _output[0].Count;

            // The position marker is the stream's true length, and it cuts both ways. Shorter
            // than what lapped decode produced: the tail past it is padding the encoder owed the
            // transform. Longer: the missing samples live in the final frame's windowed tail,
            // which is exactly what overlap-add against the silent frame after the end would
            // yield, so it is flushed as it stands.
            if (_stream.FinalGranule >= 0 && _stream.FinalGranule > frames)
            {
                var flush = (int)Math.Min(_stream.FinalGranule - frames, _lapLength);
                for (var c = 0; c < _channels; c++)
                    for (var j = 0; j < flush; j++)
                        _output[c].Add(_lap[c][j]);
                frames += flush;
            }

            if (_stream.FinalGranule >= 0 && _stream.FinalGranule < frames)
                frames = (int)_stream.FinalGranule;

            if (frames == 0) throw new FormatFault("stream held no audio");

            var samples = new short[(long)frames * _channels];
            for (var c = 0; c < _channels; c++)
            {
                var channel = _output[c];
                for (var f = 0; f < frames; f++)
                {
                    var value = MathF.Round(channel[f] * 32768f);
                    samples[f * _channels + c] = (short)Math.Clamp(value, -32768f, 32767f);
                }
            }

            return new WavClip(samples, _channels, _sampleRate);
        }

        private void DecodePacket(byte[] packet)
        {
            var r = new PacketReader(packet);
            if (r.ReadFlag()) return; // a header packet mid-stream; not audio, not an error

            var mode = _modes[r.Read(ILog(_modes.Length - 1))];
            var n = _blockSizes[mode.LongBlock ? 1 : 0];
            var n2 = n / 2;

            // Long blocks say which neighbours are short so the window can lean to meet them.
            var prevShort = false;
            var nextShort = false;
            if (mode.LongBlock)
            {
                prevShort = !r.ReadFlag();
                nextShort = !r.ReadFlag();
            }

            var mapping = _mappings[mode.MappingIndex];

            // ── Floors, decoded and held; their curves are drawn after the residues ──
            for (var c = 0; c < _channels; c++)
            {
                _floorUsed[c] = false;
                try
                {
                    _floorUsed[c] = _floors[mapping.SubmapFloor[mapping.Mux[c]]]
                        .DecodeFrame(r, _codebooks, _floorY[c]);
                }
                catch (EndOfPacket)
                {
                    // This channel and everything after it decode as silence.
                }
                _residueNeeded[c] = _floorUsed[c];
            }

            // A coupled pair shares energy: if either half sounds, both halves carry residue.
            for (var s = 0; s < mapping.CouplingMagnitude.Length; s++)
            {
                if (_residueNeeded[mapping.CouplingMagnitude[s]] || _residueNeeded[mapping.CouplingAngle[s]])
                {
                    _residueNeeded[mapping.CouplingMagnitude[s]] = true;
                    _residueNeeded[mapping.CouplingAngle[s]] = true;
                }
            }

            // ── Residues, per submap, channels in declaration order ──
            for (var c = 0; c < _channels; c++) Array.Clear(_residueVectors[c], 0, n2);

            var submapCount = mapping.SubmapFloor.Length;
            var members = new float[_channels][];
            var silent = new bool[_channels];
            for (var s = 0; s < submapCount; s++)
            {
                var count = 0;
                for (var c = 0; c < _channels; c++)
                {
                    if (mapping.Mux[c] != s) continue;
                    members[count] = _residueVectors[c];
                    silent[count] = !_residueNeeded[c];
                    count++;
                }

                _residues[mapping.SubmapResidue[s]]
                    .Decode(r, _codebooks, members.AsSpan(0, count).ToArray(), silent.AsSpan(0, count).ToArray(), n2);
            }

            // ── Square-polar coupling undone, newest step first ──
            for (var s = mapping.CouplingMagnitude.Length - 1; s >= 0; s--)
            {
                var magnitude = _residueVectors[mapping.CouplingMagnitude[s]];
                var angle = _residueVectors[mapping.CouplingAngle[s]];
                for (var i = 0; i < n2; i++)
                {
                    var m = magnitude[i];
                    var a = angle[i];
                    float newM, newA;
                    if (m > 0)
                    {
                        if (a > 0) { newM = m; newA = m - a; }
                        else { newA = m; newM = m + a; }
                    }
                    else
                    {
                        if (a > 0) { newM = m; newA = m + a; }
                        else { newA = m; newM = m - a; }
                    }
                    magnitude[i] = newM;
                    angle[i] = newA;
                }
            }

            // ── Floor curves drawn and multiplied in; silent channels stay zero ──
            for (var c = 0; c < _channels; c++)
            {
                var vector = _residueVectors[c];
                if (_floorUsed[c])
                {
                    _floors[mapping.SubmapFloor[mapping.Mux[c]]].Synthesise(_floorY[c], _curve, n2);
                    for (var i = 0; i < n2; i++) vector[i] *= _curve[i];
                }
                else
                {
                    Array.Clear(vector, 0, n2);
                }
            }

            // ── Back to time, windowed, folded onto the last frame's tail ──
            var plan = Mdct.PlanFor(n);
            var shortHalf = _blockSizes[0] / 2;
            var leftStart = mode.LongBlock && prevShort ? n / 4 - shortHalf / 2 : 0;
            var leftLength = mode.LongBlock && prevShort ? shortHalf : n2;
            var rightStart = mode.LongBlock && nextShort ? 3 * n / 4 - shortHalf / 2 : n2;
            var rightLength = mode.LongBlock && nextShort ? shortHalf : n2;

            var emit = rightStart - leftStart;

            Trace?.Add($"n={n} prevShort={prevShort} nextShort={nextShort} " +
                $"emit={(_primed ? emit : 0)} total={_output[0].Count + (_primed ? emit : 0)}");

            for (var c = 0; c < _channels; c++)
            {
                plan.Inverse(_residueVectors[c], _time, _scratch);

                var rising = Mdct.Slope(leftLength);
                for (var j = 0; j < leftStart; j++) _time[j] = 0;
                for (var j = 0; j < leftLength; j++) _time[leftStart + j] *= rising[j];

                var falling = Mdct.Slope(rightLength);
                for (var j = 0; j < rightLength; j++) _time[rightStart + j] *= falling[rightLength - 1 - j];
                for (var j = rightStart + rightLength; j < n; j++) _time[j] = 0;

                var lap = _lap[c];
                var fold = Math.Min(_lapLength, leftLength);
                if (_primed)
                {
                    var channel = _output[c];
                    for (var j = 0; j < emit; j++)
                    {
                        var value = (float)_time[leftStart + j];
                        if (j < fold) value += lap[j];
                        channel.Add(value);
                    }
                }

                for (var j = 0; j < rightLength; j++) lap[j] = (float)_time[rightStart + j];
            }

            _lapLength = rightLength;
            _primed = true;
        }
    }

    // ── The transform: spectrum to time, fast, with the slow formula kept as its judge ─────────

    /// <summary>
    /// The inverse MDCT, as two quarter-size FFTs and three mirror rules.
    /// </summary>
    /// <remarks>
    /// <para>The specification's formula is a half-size cosine sum per output sample — n²/2 work,
    /// which at this game's block sizes is a third of a second per second of audio and would be a
    /// visible hitch the first time any clip plays. The route here: the middle half of the output
    /// is a size-n/2 DST-IV of the spectrum with alternating signs, a DST-IV is a DCT-IV of the
    /// reversed input with alternating output signs, and a DCT-IV splits into two n/4-point
    /// complex FFTs (one serves the even outputs, one the odd). The first and last quarters are
    /// mirrors of the middle: odd about n/4, even about 3n/4 — checked against the formula
    /// symbolically and held to it numerically by <see cref="ImdctSelfTest"/> at every size.</para>
    /// <para>Plans are cached per block size: the format allows seven sizes and a file uses at
    /// most two, so the tables are built once and shared by every clip decoded after.</para>
    /// </remarks>
    private static class Mdct
    {
        private static readonly Dictionary<int, Plan> Plans = [];
        private static readonly Dictionary<int, double[]> Slopes = [];

        /// <summary>One decode's working space, sized once for its largest block.</summary>
        public sealed class Scratch(int maxBlock)
        {
            public readonly double[] FRe = new double[maxBlock / 4];
            public readonly double[] FIm = new double[maxBlock / 4];
            public readonly double[] HRe = new double[maxBlock / 4];
            public readonly double[] HIm = new double[maxBlock / 4];
            public readonly double[] D = new double[maxBlock / 2];
        }

        public static Plan PlanFor(int n)
        {
            lock (Plans)
            {
                if (!Plans.TryGetValue(n, out var plan)) Plans[n] = plan = new Plan(n);
                return plan;
            }
        }

        /// <summary>The window's rising edge over one overlap, the format's doubled sine curve.</summary>
        public static double[] Slope(int length)
        {
            lock (Slopes)
            {
                if (Slopes.TryGetValue(length, out var slope)) return slope;

                slope = new double[length];
                for (var j = 0; j < length; j++)
                {
                    var inner = Math.Sin(Math.PI / 2 * (j + 0.5) / length);
                    slope[j] = Math.Sin(Math.PI / 2 * inner * inner);
                }
                Slopes[length] = slope;
                return slope;
            }
        }

        /// <summary>The specification's own formula, kept only to judge the fast path.</summary>
        public static double[] NaiveInverse(float[] spectrum, int n)
        {
            var y = new double[n];
            var n2 = n / 2;
            for (var j = 0; j < n; j++)
            {
                var sum = 0.0;
                for (var k = 0; k < n2; k++)
                    sum += spectrum[k] * Math.Cos(Math.PI / (2.0 * n) * (2 * j + 1 + n2) * (2 * k + 1));
                y[j] = sum;
            }
            return y;
        }

        public sealed class Plan
        {
            private readonly int _n;
            private readonly double[] _preFRe, _preFIm; // e^(−iπ(4p+1)/(4M)) against z
            private readonly double[] _preHRe, _preHIm; // e^(+3iπ(4p+1)/(4M)) against z
            private readonly double[] _postRe, _postIm; // e^(−iπq/M); the odd branch uses its conjugate
            private readonly int[] _reversal;
            private readonly double[] _twiddleRe, _twiddleIm;

            public Plan(int n)
            {
                _n = n;
                var m = n / 2;
                var m2 = n / 4;

                _preFRe = new double[m2];
                _preFIm = new double[m2];
                _preHRe = new double[m2];
                _preHIm = new double[m2];
                for (var p = 0; p < m2; p++)
                {
                    var angle = Math.PI * (4 * p + 1) / (4.0 * m);
                    _preFRe[p] = Math.Cos(angle);
                    _preFIm[p] = -Math.Sin(angle);
                    _preHRe[p] = Math.Cos(3 * angle);
                    _preHIm[p] = Math.Sin(3 * angle);
                }

                _postRe = new double[m2];
                _postIm = new double[m2];
                for (var q = 0; q < m2; q++)
                {
                    var angle = Math.PI * q / m;
                    _postRe[q] = Math.Cos(angle);
                    _postIm[q] = -Math.Sin(angle);
                }

                _reversal = new int[m2];
                var bits = ILog(m2) - 1;
                for (var i = 0; i < m2; i++)
                {
                    var r = 0;
                    for (var b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
                    _reversal[i] = r;
                }

                _twiddleRe = new double[m2 / 2];
                _twiddleIm = new double[m2 / 2];
                for (var k = 0; k < m2 / 2; k++)
                {
                    var angle = 2 * Math.PI * k / m2;
                    _twiddleRe[k] = Math.Cos(angle);
                    _twiddleIm[k] = -Math.Sin(angle);
                }
            }

            /// <summary>
            /// Spectrum of n/2 lines to n samples of time. The plan itself is shared and holds
            /// only tables; the working space comes from the caller, so two clips decoding at
            /// once cannot scribble on each other's transform.
            /// </summary>
            public void Inverse(float[] spectrum, double[] time, Scratch scratch)
            {
                var n = _n;
                var n2 = n / 2;
                var n4 = n / 4;
                var m = n2;
                var m2 = n4;

                var fRe = scratch.FRe;
                var fIm = scratch.FIm;
                var hRe = scratch.HRe;
                var hIm = scratch.HIm;
                var d = scratch.D;

                // g[k] = −(−1)^k · X[m−1−k]; folded as z_p = g[2p] + i·g[m−1−2p], then rotated
                // once for each of the two transforms.
                for (var p = 0; p < m2; p++)
                {
                    var even = -spectrum[m - 1 - 2 * p]; // g[2p]
                    var odd = spectrum[2 * p];           // g[m−1−2p] = −(−1)^(m−1−2p)·X[2p], m even
                    fRe[p] = even * _preFRe[p] - odd * _preFIm[p];
                    fIm[p] = even * _preFIm[p] + odd * _preFRe[p];
                    hRe[p] = even * _preHRe[p] - odd * _preHIm[p];
                    hIm[p] = even * _preHIm[p] + odd * _preHRe[p];
                }

                Fft(fRe, fIm, m2);
                Fft(hRe, hIm, m2);

                // d[2q] = Re(e^(−iπq/M)·F[q]);  d[2q+1] = Re(e^(+iπq/M)·H[(M2−q) mod M2]).
                for (var q = 0; q < m2; q++)
                {
                    d[2 * q] = fRe[q] * _postRe[q] - fIm[q] * _postIm[q];

                    var h = (m2 - q) % m2;
                    d[2 * q + 1] = hRe[h] * _postRe[q] + hIm[h] * _postIm[q];
                }

                // y[n/4 + j] = (−1)^(j+1)·d[j], then the two mirrors fill the outer quarters.
                for (var j = 0; j < m; j++)
                {
                    var value = (j & 1) == 0 ? -d[j] : d[j];
                    time[n4 + j] = value;
                }
                for (var j = 0; j < n4; j++) time[j] = -time[n2 - 1 - j];
                for (var j = 3 * n4; j < n; j++) time[j] = time[3 * n2 - 1 - j];
            }

            /// <summary>Iterative radix-2 FFT with the kernel e^(−2πi·pq/N), in place.</summary>
            private void Fft(double[] re, double[] im, int count)
            {
                for (var i = 0; i < count; i++)
                {
                    var r = _reversal[i];
                    if (r <= i) continue;
                    (re[i], re[r]) = (re[r], re[i]);
                    (im[i], im[r]) = (im[r], im[i]);
                }

                for (var size = 2; size <= count; size *= 2)
                {
                    var half = size / 2;
                    var stride = count / size;
                    for (var start = 0; start < count; start += size)
                    {
                        for (var k = 0; k < half; k++)
                        {
                            var wRe = _twiddleRe[k * stride];
                            var wIm = _twiddleIm[k * stride];
                            var a = start + k;
                            var b = a + half;
                            var tRe = re[b] * wRe - im[b] * wIm;
                            var tIm = re[b] * wIm + im[b] * wRe;
                            re[b] = re[a] - tRe;
                            im[b] = im[a] - tIm;
                            re[a] += tRe;
                            im[a] += tIm;
                        }
                    }
                }
            }
        }
    }
}
