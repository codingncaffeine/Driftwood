namespace Driftwood.Client.Render;

/// <summary>
/// The game's name, as blocks — drawn rather than lettered.
/// </summary>
/// <remarks>
/// <para>Its own alphabet and not the interface font, because the two are doing different jobs. The
/// font is five pixels wide and exists to be legible at a glance in a list; this is nine letters
/// seen once, and wants to be heavy enough that each cell of it can be a block of wood with a side
/// and a shadow.</para>
/// <para>Only the seven letters the word needs. A title alphabet with a Q in it is an alphabet
/// somebody drew twenty-six of and used seven.</para>
/// </remarks>
public static class TitleArt
{
    public const int LetterWidth = 7;
    public const int LetterHeight = 8;

    /// <summary>A column of blank between letters, so the extrusion of one clears the next.</summary>
    public const int Gap = 1;

    public const string Word = "DRIFTWOOD";

    private static readonly Dictionary<char, string[]> Letters = new()
    {
        ['D'] =
        [
            "######.",
            "##...##",
            "##....#",
            "##....#",
            "##....#",
            "##....#",
            "##...##",
            "######.",
        ],
        ['R'] =
        [
            "######.",
            "##...##",
            "##...##",
            "######.",
            "##.##..",
            "##..##.",
            "##...##",
            "##...##",
        ],
        ['I'] =
        [
            "#######",
            "..###..",
            "..###..",
            "..###..",
            "..###..",
            "..###..",
            "..###..",
            "#######",
        ],
        ['F'] =
        [
            "#######",
            "##.....",
            "##.....",
            "######.",
            "##.....",
            "##.....",
            "##.....",
            "##.....",
        ],
        ['T'] =
        [
            "#######",
            "..###..",
            "..###..",
            "..###..",
            "..###..",
            "..###..",
            "..###..",
            "..###..",
        ],
        ['W'] =
        [
            "##...##",
            "##...##",
            "##...##",
            "##.#.##",
            "##.#.##",
            "##.#.##",
            "#######",
            ".##.##.",
        ],
        ['O'] =
        [
            ".#####.",
            "##...##",
            "##...##",
            "##...##",
            "##...##",
            "##...##",
            "##...##",
            ".#####.",
        ],
    };

    /// <summary>How many cells across the whole word is, gaps included.</summary>
    public static int Cells => Word.Length * (LetterWidth + Gap) - Gap;

    /// <summary>True when this cell of this letter is timber rather than air.</summary>
    public static bool Filled(char letter, int x, int y)
    {
        if (!Letters.TryGetValue(letter, out var rows)) return false;
        if ((uint)y >= (uint)rows.Length || (uint)x >= (uint)rows[y].Length) return false;
        return rows[y][x] == '#';
    }

    /// <summary>Every letter of the word is one this alphabet has, which is the whole of it.</summary>
    public static IEnumerable<string> Validate()
    {
        foreach (var letter in Word.Distinct())
        {
            if (!Letters.TryGetValue(letter, out var rows))
            {
                yield return $"the title has no '{letter}' and the word needs one";
                continue;
            }

            if (rows.Length != LetterHeight)
                yield return $"'{letter}' is {rows.Length} rows where every letter is {LetterHeight}";

            foreach (var row in rows)
                if (row.Length != LetterWidth)
                    yield return $"'{letter}' has a row {row.Length} wide where every letter is {LetterWidth}";

            var ink = 0;
            foreach (var row in rows) foreach (var cell in row) if (cell == '#') ink++;
            if (ink == 0) yield return $"'{letter}' is blank";
        }

        // Two letters that come out the same are two letters somebody typed once. Cheap to say.
        foreach (var a in Letters)
        foreach (var b in Letters)
        {
            if (a.Key >= b.Key) continue;
            if (string.Join('/', a.Value) == string.Join('/', b.Value))
                yield return $"'{a.Key}' and '{b.Key}' are drawn identically";
        }
    }
}
