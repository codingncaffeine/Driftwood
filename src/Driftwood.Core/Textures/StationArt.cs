namespace Driftwood.Core.Textures;

/// <summary>
/// The recognisable objects, drawn by hand as pixel grids — the bench, the furnaces, and
/// the rest of the furniture a player identifies at a glance.
/// </summary>
/// <remarks>
/// <para>⛳ <b>The user's directive (2026-08-09): the fundamentals that carry recognisable art
/// deserve more attention than a generator can offer — do those by hand.</b> A generator is
/// right for materials, where structure IS the texture; it is wrong for a workbench, whose
/// whole identity is a drawing of tools on a panel.</para>
/// <para>Method, same as <c>TileGen.ToolShapes</c> earned the hard way: the reference tile is
/// read out as a quantised ASCII grid first and studied, THEN ours is authored — same object,
/// our own timber, our own marks, never a byte-copy. Grids are sixteen rows of sixteen
/// characters; palettes map a character to a colour; <c>'.'</c> keeps whatever the base
/// already holds (or transparency when there is no base).</para>
/// </remarks>
public static class StationArt
{
    private const int Size = TileGen.Size;
    private const int Stride = TileGen.Stride;

    /// <summary>Paints a grid over a base tile (or over nothing), palette-mapped.</summary>
    private static byte[] Grid(string[] rows, byte[]? baseTile, params (char C, byte R, byte G, byte B)[] palette)
    {
        var t = baseTile is null ? new byte[TileGen.BytesPerTile] : (byte[])baseTile.Clone();

        for (var y = 0; y < Size && y < rows.Length; y++)
        for (var x = 0; x < Size && x < rows[y].Length; x++)
        {
            var c = rows[y][x];
            if (c == '.') continue;

            foreach (var p in palette)
            {
                if (p.C != c) continue;
                var i = y * Stride + x * 4;
                t[i] = p.R; t[i + 1] = p.G; t[i + 2] = p.B; t[i + 3] = 255;
                break;
            }
        }

        return t;
    }

    // ───────────────────────────── The bench ─────────────────────────────
    // Studied off the reference: a worktop in warmer, redder wood than the body, carved with
    // a square of tool-marks inside a bevelled frame, pegs at the corners; the sides are a
    // plank panel with the maker's tools hung on it. Ours hangs a mallet and a handsaw.

    private static readonly (char, byte, byte, byte)[] BenchPalette =
    [
        ('D', 26, 19, 11),      // the deep seam
        ('E', 56, 42, 24),      // frame timber, dark
        ('g', 128, 100, 62),    // shadow wood
        ('a', 166, 132, 78),    // mid wood
        ('b', 188, 152, 92),    // light wood
        ('c', 206, 168, 104),   // lit edge
        ('p', 218, 190, 124),   // corner peg
        ('A', 98, 54, 26),      // worktop, carved line
        ('m', 150, 92, 48),     // worktop, mid
        ('l', 180, 116, 62),    // worktop, light
        ('h', 200, 134, 74),    // worktop, highlight
        ('H', 112, 114, 120),   // mallet head, iron
        ('n', 122, 90, 54),     // tool haft
        ('r', 148, 44, 38),     // saw handle
        ('R', 98, 26, 22),      // saw handle, shadow
        ('S', 212, 212, 216),   // saw blade
        ('s', 168, 170, 176),   // saw blade, shadow tooth
    ];

    public static byte[] BenchTop() => Grid(
    [
        "EEEEDggggggDEEEE",
        "EpbccccccccccbpE",
        "EbcllllllllllcbE",
        "DclmmmmmmmmmmlcD",
        "gcmAAAAAAAAAAmcg",
        "gcmAhllllllhAmcg",
        "gcmAlhllllhlAmcg",
        "gcmAllAAAAllAmcg",
        "gcmAllAAAAllAmcg",
        "gcmAlhllllhlAmcg",
        "gcmAhllllllhAmcg",
        "gcmAAAAAAAAAAmcg",
        "DagmmmmmmmmmmgaD",
        "EaggggggggggggaE",
        "EpaggggggggggapE",
        "EEEEDggggggDEEEE",
    ], null, BenchPalette);

    public static byte[] BenchSide() => Grid(
    [
        "cccgccccgccccgcc",
        "bbbgbbbbgbbbbgbb",
        "aaagaaaagaaaagaa",
        "DDDDDDDDDDDDDDDD",
        "gbbbbbbDDbbbbbbg",
        "gaaaaaaDDarrraag",
        "gaHHHHaDDarRraag",
        "gaHHHHaDDaSSSSag",
        "gaannaaDDaSSSsag",
        "gaannaaDDaaSSsag",
        "gaannaaDDaaSssag",
        "gaannaaDDaaaSsag",
        "gaaaaaaDDaaaasag",
        "gbaaaabDDbaaaabg",
        "gbbbbbbDDbbbbbbg",
        "DDDDDDDDDDDDDDDD",
    ], null, BenchPalette);

    public static byte[] BenchFront() => Grid(
    [
        "cccgccccgccccgcc",
        "bbbgbbbbgbbbbgbb",
        "aaagaaaagaaaagaa",
        "DDDDDDDDDDDDDDDD",
        "gbbbbbbDDbbbbbbg",
        "gannnnaDDaaaRrag",
        "gaaHHaaDDSSSSSrg",
        "gaaHHaaDDSSSSSRg",
        "gaaHHaaDDsasasag",
        "gaaHHaaDDaaaaaag",
        "gaannaaDDarrraag",
        "gaannaaDDaRnRaag",
        "gaaaaaaDDaaaaaag",
        "gbaaaabDDbaaaabg",
        "gbbbbbbDDbbbbbbg",
        "DDDDDDDDDDDDDDDD",
    ], null, BenchPalette);

    // ──────────────────────────── The furnaces ────────────────────────────
    // The read that matters, straight off the reference: the front is TWO openings — the
    // chamber mouth above, the hearth below inside a surround of dressed stone — and the lit
    // form only changes what is inside the hearth. The body is the world's own cobble with a
    // dressed skirt, so a furnace reads as built from the stone beside it.

    private static readonly (char, byte, byte, byte)[] FurnacePalette =
    [
        ('D', 22, 22, 24),      // seams and base
        ('f', 58, 58, 62),      // dark stone
        ('a', 96, 96, 100),     // mid stone
        ('b', 118, 118, 122),   // light stone
        ('c', 138, 138, 142),   // lit stone edge
        ('S', 176, 176, 180),   // dressed surround, light
        ('s', 152, 152, 156),   // dressed surround, shadow
        ('M', 13, 13, 14),      // the dark of an opening
        ('e', 34, 31, 28),      // soot
        ('F', 252, 150, 40),    // flame, orange
        ('Y', 255, 210, 100),   // flame, bright
        ('N', 226, 70, 28),     // flame, red
        ('P', 122, 18, 16),     // embers, deep
    ];

    private static byte[] FurnaceFace(bool lit)
    {
        var rows = new[]
        {
            "DffffbbbbbbffffD",
            "fbccbbbbbbbbccbf",
            "fbbaaaaaaaaaabbf",
            "fabMMMMMMMMMMbaf",
            "fabMMMMMMMMMMbaf",
            "fabMMeMMMMeMMbaf",
            "fabMMMMMMMMMMbaf",
            "fbbaabbbbbbaabbf",
            "DaaaaaaaaaaaaaaD",
            "fSSSSSSSSSSSSSSf",
            "fSsMMMMMMMMMMsSf",
            lit ? "fSsMPNFFNFNPMsSf" : "fSsMMMMMMMMMMsSf",
            lit ? "fSsMNFYFFYFNMsSf" : "fSsMMeMMMMeMMsSf",
            lit ? "fSsMFYFNFYFFMsSf" : "fSsMMMMMMMMMMsSf",
            "fSssssssssssssSf",
            "DffffffffffffffD",
        };

        return Grid(rows, null, FurnacePalette);
    }

    public static byte[] FurnaceFront() => FurnaceFace(lit: false);
    public static byte[] FurnaceFrontLit() => FurnaceFace(lit: true);

    /// <summary>The furnace's flank: the world's cobble with a dressed-stone skirt and seams.</summary>
    public static byte[] FurnaceSide(int seed) => Grid(
    [
        "DDDDDDDDDDDDDDDD",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
        "SSSSSSSSSSSSSSSS",
        "SSSSsSSSSSsSSSSS",
        "SSSSsSSSSSsSSSSS",
        "ssssssssssssssss",
        "SsSSSSSSsSSSSSSs",
        "DDDDDDDDDDDDDDDD",
        "DDDDDDDDDDDDDDDD",
    ], TileGen.Cobble(seed, 104, 104, 108), FurnacePalette);

    /// <summary>The furnace's crown: its own cobble inside a seam ring.</summary>
    public static byte[] FurnaceTop(int seed) => Grid(
    [
        "DDDDDDDDDDDDDDDD",
        "D..............D",
        "D..............D",
        "D..............D",
        "D..............D",
        "D..............D",
        "D..............D",
        "D..............D",
        "D..............D",
        "D..............D",
        "D..............D",
        "D..............D",
        "D..............D",
        "D..............D",
        "D..............D",
        "DDDDDDDDDDDDDDDD",
    ], TileGen.Cobble(seed, 104, 104, 108), FurnacePalette);

    // ───────────────────────── The blast furnace ─────────────────────────
    // Iron plates riveted over a dark body, and a LETTERBOX for a mouth — the shape is what
    // tells it from the furnace's arch at a glance (the project's own rule for the pair),
    // so the hand art keeps the letterbox and spends its pixels on the plating.

    private static readonly (char, byte, byte, byte)[] BlastPalette =
    [
        ('D', 24, 24, 28),
        ('f', 66, 66, 74),      // dark body
        ('P', 198, 200, 206),   // plate, lit edge
        ('p', 164, 166, 172),   // plate
        ('q', 130, 132, 138),   // plate, shadow
        ('R', 92, 94, 100),     // rivet
        ('M', 12, 12, 13),
        ('e', 34, 31, 28),
        ('F', 252, 150, 40),
        ('Y', 255, 210, 100),
        ('N', 226, 70, 28),
        ('X', 122, 18, 16),
    ];

    private static byte[] BlastFace(bool lit) => Grid(
    [
        "DfppppppppppppfD",
        "fPPPPPPPPPPPPPPf",
        "fPqRqqqqqqqqRqPf",
        "fPqqqqqqqqqqqqPf",
        "fppppppppppppppf",
        "DqqqqqqqqqqqqqqD",
        "fqMMMMMMMMMMMMqf",
        lit ? "fqMXNFFYFFNXMMqf" : "fqMMeMMMMMMeMMqf",
        lit ? "fqMNFYFFNFYFNMqf" : "fqMMMMMMMMMMMMqf",
        "fqMMMMMMMMMMMMqf",
        "fppppppppppppppf",
        "fPqRqqqqqqqqRqPf",
        "fPqqqqqqqqqqqqPf",
        "fppppppppppppppf",
        "DffffffffffffffD",
        "DDDDDDDDDDDDDDDD",
    ], null, BlastPalette);

    public static byte[] BlastFront() => BlastFace(lit: false);
    public static byte[] BlastFrontLit() => BlastFace(lit: true);

    /// <summary>Plated shoulders over the brick body.</summary>
    public static byte[] BlastSide(int seed) => Grid(
    [
        "DfppppppppppppfD",
        "fPPPPPPPPPPPPPPf",
        "fPqRqqqqqqqqRqPf",
        "fppppppppppppppf",
        "DqqqqqqqqqqqqqqD",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
        "DffffffffffffffD",
        "DDDDDDDDDDDDDDDD",
    ], TileGen.Bricks(seed, 78, 78, 86, 44), BlastPalette);

    public static byte[] BlastTop() => Grid(
    [
        "DDDDDDDDDDDDDDDD",
        "DppppppppppppppD",
        "DpPPPPPPPPPPPPpD",
        "DpPqqqqqqqqqqPpD",
        "DpPqRqqqqqqRqPpD",
        "DpPqqMMMMMMqqPpD",
        "DpPqqMMMMMMqqPpD",
        "DpPqqMMMMMMqqPpD",
        "DpPqqMMMMMMqqPpD",
        "DpPqqMMMMMMqqPpD",
        "DpPqqMMMMMMqqPpD",
        "DpPqRqqqqqqRqPpD",
        "DpPqqqqqqqqqqPpD",
        "DpPPPPPPPPPPPPpD",
        "DppppppppppppppD",
        "DDDDDDDDDDDDDDDD",
    ], null, BlastPalette);

    // ──────────────────────────── The smoker ────────────────────────────
    // A wood chamber under an iron hood with a vent slit; the mouth is the WIDE one of the
    // three fires, low in the face, and the lit form fills it and nothing else.

    private static readonly (char, byte, byte, byte)[] SmokerPalette =
    [
        ('D', 26, 20, 12),
        ('u', 108, 78, 44),     // dark wood
        ('n', 146, 108, 62),    // mid wood
        ('o', 172, 130, 76),    // light wood
        ('H', 152, 154, 160),   // hood, lit
        ('h', 118, 120, 126),   // hood, shadow
        ('M', 12, 12, 13),
        ('e', 34, 31, 28),
        ('F', 252, 150, 40),
        ('Y', 255, 210, 100),
        ('N', 226, 70, 28),
        ('X', 122, 18, 16),
    ];

    private static byte[] SmokerFace(bool lit) => Grid(
    [
        "DuoooooooooooouD",
        "unnnnnnnnnnnnnnu",
        "uHHHHHHHHHHHHHHu",
        "uhhhhhhhhhhhhhhu",
        "uhMMMMMMMMMMMMhu",
        "uhhhhhhhhhhhhhhu",
        "unnnnnnnnnnnnnnu",
        "DnnnnnnnnnnnnnnD",
        "unMMMMMMMMMMMMnu",
        "unMMMMMMMMMMMMnu",
        lit ? "unMXNFYFFYFNXMnu" : "unMMeMMMMMMeMMnu",
        lit ? "unMNFFYNFYFFNMnu" : "unMMMMMMMMMMMMnu",
        "unMMMMMMMMMMMMnu",
        "unnuunnnnnnuunnu",
        "uuoooooooooooouu",
        "DDDDDDDDDDDDDDDD",
    ], null, SmokerPalette);

    public static byte[] SmokerFront() => SmokerFace(lit: false);
    public static byte[] SmokerFrontLit() => SmokerFace(lit: true);

    /// <summary>The hood wraps the smoker's flanks too, over its plank body.</summary>
    public static byte[] SmokerSide(int seed) => Grid(
    [
        "DuoooooooooooouD",
        "uHHHHHHHHHHHHHHu",
        "uhhhhhhhhhhhhhhu",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
        "uunnnnnnnnnnnnuu",
        "DDDDDDDDDDDDDDDD",
    ], TileGen.Planks(seed, 146, 108, 62), SmokerPalette);

    public static byte[] SmokerTop(int seed) => Grid(
    [
        "DDDDDDDDDDDDDDDD",
        "DHHHHHHHHHHHHHHD",
        "DHhhhhhhhhhhhhHD",
        "DHh..........hHD",
        "DHh..........hHD",
        "DHh..MMMMMM..hHD",
        "DHh..MMMMMM..hHD",
        "DHh..MMMMMM..hHD",
        "DHh..MMMMMM..hHD",
        "DHh..MMMMMM..hHD",
        "DHh..MMMMMM..hHD",
        "DHh..........hHD",
        "DHh..........hHD",
        "DHhhhhhhhhhhhhHD",
        "DHHHHHHHHHHHHHHD",
        "DDDDDDDDDDDDDDDD",
    ], TileGen.Planks(seed, 146, 108, 62), SmokerPalette);

    // ──────────────────────────── The barrel ────────────────────────────
    // Staves and TWO IRON HOOPS — the hoops are the whole read at a distance, which is why
    // the reference gives them a quarter of the tile. The lid is boards inside a rim.

    private static readonly (char, byte, byte, byte)[] BarrelPalette =
    [
        ('d', 58, 40, 22),      // deep seam
        ('a', 96, 66, 38),      // dark stave
        ('c', 112, 80, 46),     // mid stave
        ('e', 128, 94, 54),     // light stave
        ('g', 146, 110, 66),    // lit stave edge
        ('f', 54, 55, 58),      // hoop
        ('h', 90, 92, 98),      // hoop glint
    ];

    public static byte[] BarrelTop() => Grid(
    [
        "aeeaeeaeeaeeaeea",
        "edddddddddddddde",
        "edcceegcceegccde",
        "edcceeccceeccdde",
        "edaceeccceeccade",
        "edcceeccceeccdde",
        "edcceegcceegccde",
        "edcceeccceeccdde",
        "edaceeccceeccade",
        "edcceeccceeccdde",
        "edcceegcceegccde",
        "edcceeccceeccdde",
        "edaceeccceeccade",
        "edcceeccceeccdde",
        "edddddddddddddde",
        "aeeaeeaeeaeeaeea",
    ], null, BarrelPalette);

    public static byte[] BarrelSide() => Grid(
    [
        "aggaggaggaggagga",
        "caeecaeecaeecaee",
        "caeecaeecaeecaee",
        "ffhfffhfffhfffhf",
        "ffffffffffffffff",
        "caeecaeecaeecaee",
        "caeegaeecaeegaee",
        "caeecaeecaeecaee",
        "caeecaeegaeecaee",
        "caeecaeecaeecaee",
        "caeegaeecaeegaee",
        "ffhfffhfffhfffhf",
        "ffffffffffffffff",
        "caeecaeecaeecaee",
        "caeecaeecaeecaee",
        "aggaggaggaggagga",
    ], null, BarrelPalette);

    // ───────────────────────── Doors and trapdoor ─────────────────────────
    // A panelled door: stiles, rails, two recessed panels a floor — and upstairs the panels
    // give way to a four-pane window, which is the one thing a door's top half is FOR.

    private static readonly (char, byte, byte, byte)[] DoorPalette =
    [
        ('W', 168, 132, 76),    // lit face
        ('w', 146, 112, 64),    // face
        ('v', 118, 88, 50),     // shadow face
        ('V', 88, 62, 36),      // deep shadow
        ('D', 40, 28, 16),      // seam
        ('G', 200, 224, 232),   // glass
        ('g', 164, 196, 210),   // glass, shade
    ];

    public static byte[] DoorLower() => Grid(
    [
        "WwwwwwwwwwwwwwvV",
        "WwwwwwwwwwwwwwvV",
        "WwVVVVVwwVVVVVvV",
        "WwVvvvWwwVvvvWvV",
        "WwVvvvWwwVvvvWvV",
        "WwVvvvWwwVvvvWvV",
        "WwWWWWWwwWWWWWvV",
        "WwwwwwwwwwwwwwvV",
        "WwwwwwwwwwwwwwvV",
        "WwVVVVVwwVVVVVvV",
        "WwVvvvWwwVvvvWvV",
        "WwVvvvWwwVvvvWvV",
        "WwVvvvWwwVvvvWvV",
        "WwWWWWWwwWWWWWvV",
        "WwwwwwwwwwwwwwvV",
        "VVVVVVVVVVVVVVVV",
    ], null, DoorPalette);

    public static byte[] DoorUpper() => Grid(
    [
        // The window is a HOLE, not painted glass — the layer draws in the cut-out pass and
        // looking through a door is the whole point of giving it a window.
        "VVVVVVVVVVVVVVVV",
        "WwwwwwwwwwwwwwvV",
        "WwVVVVVwwVVVVVvV",
        "WwV...WwwV...WvV",
        "WwV...WwwV...WvV",
        "WwV...WwwV...WvV",
        "WwWWWWWwwWWWWWvV",
        "WwwwwwwwwwwwwwvV",
        "WwwwwwwwwwwwwwvV",
        "WwVVVVVwwVVVVVvV",
        "WwVvvvWwwVvvvWvV",
        "WwVvvvWwwVvvvWvV",
        "WwVvvvWwwVvvvWvV",
        "WwWWWWWwwWWWWWvV",
        "WwwwwwwwwwwwwwvV",
        "WwwwwwwwwwwwwwvV",
    ], null, DoorPalette);

    public static byte[] TrapdoorTile() => Grid(
    [
        // A braced lattice, and the gaps are REAL — the layer is a cut-out, and a hatch you
        // can peer down through is both the genre's trapdoor and the more interesting one.
        "WWWWWWWWWWWWWWWV",
        "WwwwwwwwwwwwwwvV",
        "WwVVVVVVVVVVVVvV",
        "Wwv....vv....WvV",
        "Wwv....vv...WWvV",
        "Wwv....vv..Wv.vV",
        "Wwv....vv.Wv..vV",
        "WwvWWWWvvWvWWWvV",
        "WwvvvvvWWvvvvvvV",
        "Wwv...Wv.v....vV",
        "Wwv..Wv..v....vV",
        "Wwv.Wv...v....vV",
        "WwvW.....v....vV",
        "WwWWWWWWWWWWWWvV",
        "WwwwwwwwwwwwwwvV",
        "VVVVVVVVVVVVVVVV",
    ], null, DoorPalette);

    /// <summary>Two rails, a rung every fourth row, and nothing else — a ladder is mostly air.</summary>
    public static byte[] LadderTile() => Grid(
    [
        "..wW........Wv..",
        ".wWWWWWWWWWWWWv.",
        ".wvvvvvvvvvvvvv.",
        "..wW........Wv..",
        "..wW........Wv..",
        ".wWWWWWWWWWWWWv.",
        ".wvvvvvvvvvvvvv.",
        "..wW........Wv..",
        "..wW........Wv..",
        ".wWWWWWWWWWWWWv.",
        ".wvvvvvvvvvvvvv.",
        "..wW........Wv..",
        "..wW........Wv..",
        ".wWWWWWWWWWWWWv.",
        ".wvvvvvvvvvvvvv.",
        "..wW........Wv..",
    ], null, DoorPalette);

    // ───────────────────── The smithing table and the loom ─────────────────────
    // The reference's smithing table is an iron top over an OXBLOOD cabinet with copper
    // tools on its face; the loom's whole identity is the warp — pale threads stretched
    // over the top frame — and the woven cloth growing on its side.

    private static readonly (char, byte, byte, byte)[] SmithingPalette =
    [
        ('D', 18, 18, 22),
        ('m', 52, 56, 66),      // iron top
        ('M', 66, 72, 86),      // iron, lit
        ('B', 88, 96, 128),     // blued steel accent
        ('x', 38, 42, 50),      // iron, shadow
        ('r', 96, 30, 30),      // oxblood panel
        ('R', 118, 40, 38),     // oxblood, lit
        ('k', 66, 20, 20),      // oxblood, shadow
        ('C', 176, 104, 70),    // copper tool
        ('c', 132, 74, 50),     // copper, shadow
        ('w', 118, 88, 54),     // timber feet
    ];

    public static byte[] SmithingTop() => Grid(
    [
        "DmMMMMMMMMMMMMmD",
        "mMmmmmmmmmmmmmMm",
        "mMmxxmmmmmmxmmMm",
        "mMmmmmmmmmmmmmBm",
        "mMmmmxxxxxxmmmMm",
        "mMmmxmmmmmmxmmMm",
        "mMmmxmmBmmmxmmMm",
        "mMmmxmmmmmmxmmMm",
        "mMmmxmmmmBmxmmMm",
        "mMmmxmmmmmmxmmMm",
        "mMmmmxxxxxxmmmMm",
        "mBmmmmmmmmmmmmMm",
        "mMmmxmmmmmmmxmMm",
        "mMmmmmmmmmmmmmMm",
        "mMmmmmmmmmmmmmMm",
        "DmmmmmmmmmmmmmmD",
    ], null, SmithingPalette);

    public static byte[] SmithingSide() => Grid(
    [
        "DmMMMMMMMMMMMMmD",
        "mMmmmmmmmmmmmmMm",
        "mxmmmmmmmmmmmmxm",
        "DDDDDDDDDDDDDDDD",
        "krrrrrrrrrrrrrrk",
        "krRRrrrrrrrRRrrk",
        "krRCCcrrrCCCCrrk",
        "krrCcrrrrrCcrrrk",
        "krrCcrrrrrCcrrrk",
        "krrCcrrrrrCCcrrk",
        "krrccrrrrrrccrrk",
        "krrrrrrrrrrrrrrk",
        "krrrrrrrrrrrrrrk",
        "kkrrrrrrrrrrrrkk",
        "wwkkkkkkkkkkkkww",
        "DwwDDDDDDDDDDwwD",
    ], null, SmithingPalette);

    private static readonly (char, byte, byte, byte)[] LoomPalette =
    [
        ('D', 30, 20, 12),
        ('w', 128, 96, 56),     // frame wood
        ('W', 156, 120, 70),    // frame wood, lit
        ('u', 100, 74, 44),     // frame wood, shadow
        ('T', 240, 238, 228),   // warp thread, lit
        ('t', 208, 204, 190),   // warp thread
        ('s', 96, 66, 40),      // the shed between threads
        ('K', 54, 36, 24),      // woven cloth, dark
        ('k', 74, 50, 32),      // woven cloth
        ('b', 96, 112, 128),    // a weft stripe, dusty blue
        ('q', 142, 74, 48),     // a weft stripe, rust
    ];

    public static byte[] LoomTop() => Grid(
    [
        "DwWWWWWWWWWWWWwD",
        "wWwwwwwwwwwwwwWw",
        "wWsTtTtTtTtTtsWw",
        "wWsTtTtTtTtTtsWw",
        "wWstTtTtTtTtTsWw",
        "wWsTtTtTtTtTtsWw",
        "wWsTtTtTtTtTtsWw",
        "wWwuuuuuuuuuuwWw",
        "wWwuuuuuuuuuuwWw",
        "wWsTtTtTtTtTtsWw",
        "wWstTtTtTtTtTsWw",
        "wWsTtTtTtTtTtsWw",
        "wWsTtTtTtTtTtsWw",
        "wWsTtTtTtTtTtsWw",
        "wWwwwwwwwwwwwwWw",
        "DwwwwwwwwwwwwwwD",
    ], null, LoomPalette);

    public static byte[] LoomSide() => Grid(
    [
        "DwWWWWWWWWWWWWwD",
        "wWwwwwwwwwwwwwWw",
        "wWKkKkKkKkKkKkWw",
        "wWkKkKkKkKkKkKWw",
        "wWKkKkKkKkKkKkWw",
        "wWbbbbbbbbbbbbWw",
        "wWkKkKkKkKkKkKWw",
        "wWKkKkKkKkKkKkWw",
        "wWqqqqqqqqqqqqWw",
        "wWkKkKkKkKkKkKWw",
        "wWKkKkKkKkKkKkWw",
        "wWkKkKkKkKkKkKWw",
        "wWKkKkKkKkKkKkWw",
        "wWwwwwwwwwwwwwWw",
        "wuwwwwwwwwwwwwuw",
        "DuuDDDDDDDDDDuuD",
    ], null, LoomPalette);

    // ─────────────────── The stonecutter and the anvil ───────────────────
    // Stone furniture: the cutter is a stone bed with a metal blade track ruled across it,
    // the anvil is one worn lump of iron — dents on the crown, rivets at the foot.

    private static readonly (char, byte, byte, byte)[] StonePalette =
    [
        ('D', 24, 24, 26),
        ('f', 66, 66, 70),      // dark stone
        ('a', 104, 104, 108),   // stone
        ('b', 124, 124, 128),   // stone, lit
        ('w', 112, 82, 48),     // timber peg
        ('t', 168, 170, 176),   // track metal
        ('T', 198, 200, 206),   // track metal, lit
        ('M', 14, 14, 15),      // the slot
        ('i', 88, 88, 94),      // iron
        ('I', 108, 108, 114),   // iron, lit
        ('e', 56, 56, 60),      // iron, shadow
    ];

    public static byte[] StonecutterTopTile() => Grid(
    [
        "DwfaaaaaaaaaafwD",
        "wfabbaabbaabbafw",
        "faabababbabbaaaf",
        "fabaabbabbaabbaf",
        "faaabbaabbabaabf",
        "fTTTTTTTTTTTTTTf",
        "fttttttttttttttf",
        "fMMMMMMMMMMMMMMf",
        "fMMMMMMMMMMMMMMf",
        "fttttttttttttttf",
        "fTTTTTTTTTTTTTTf",
        "faabbaababbaabaf",
        "fabaabbabaabbaaf",
        "faabbaabbaabbabf",
        "wfaabababbabaafw",
        "DwfaaaaaaaaaafwD",
    ], null, StonePalette);

    public static byte[] StonecutterSideTile() => Grid(
    [
        "DffffffffffffffD",
        "fbbbbbbbbbbbbbbf",
        "fabbaabbaabbaabf",
        "faabbaabbaabbaaf",
        "DffffffffffffffD",
        "wwuwwwwwwwwwwuww",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
        "wwuwwwwwwwwwwuww",
        "DDDDDDDDDDDDDDDD",
    ], TileGen.Planks(1064, 128, 96, 56), StonePalette);

    public static byte[] AnvilTopTile() => Grid(
    [
        "DiIIIIIIIIIIIIiD",
        "iIiiiiiiiiiiiiIi",
        "iIiieiiiiiiiiiIi",
        "iIiiiiiiiieiiiIi",
        "iIiiiieeiiiiiiIi",
        "iIiiiiiiiiiiiiIi",
        "iIieiiiiiiieiiIi",
        "iIiiiiieiiiiiiIi",
        "iIiiiiiiiiiiiiIi",
        "iIiieiiiiiiiiiIi",
        "iIiiiiiiieiiiiIi",
        "iIiiieiiiiiiiiIi",
        "iIiiiiiiiiiieiIi",
        "iIiiiiiiiiiiiiIi",
        "iIiiiiiiiiiiiiIi",
        "DiiiiiiiiiiiiiiD",
    ], null, StonePalette);

    /// <summary>Flat worn iron — the anvil's SHAPE is its model's boxes, so the tile stays a
    /// surface: lit crown edge, dents, and a riveted line along the foot.</summary>
    public static byte[] AnvilSideTile() => Grid(
    [
        "DiIIIIIIIIIIIIiD",
        "iIiiiiiiiiiiiiIi",
        "iIiieiiiiiieiiIi",
        "iIiiiiiiiiiiiiIi",
        "ieiiiiieeiiiiiei",
        "ieiiiiiiiiiiiiei",
        "ieiieiiiiiiieiei",
        "ieiiiiiiiiiiiiei",
        "ieiiiiiieiiiiiei",
        "ieiiiiiiiiiiiiei",
        "ieiieiiiiiieiiei",
        "ieiiiiiiiiiiiiei",
        "ieiiiiiiiiiiiiei",
        "eIiiiiiiiiiiiiIe",
        "eIeieieiieieieIe",
        "DeeeeeeeeeeeeeeD",
    ], null, StonePalette);
}
