namespace Driftwood.Core.Ui;

/// <summary>What a field will take, so a world name cannot be typed with a slash in it.</summary>
public enum TextAllows
{
    /// <summary>Anything the font can draw.</summary>
    Anything,

    /// <summary>The digits, for a seed somebody types as a number.</summary>
    Digits,

    /// <summary>Anything that can also be a file name, for a world.</summary>
    FileSafe,
}

/// <summary>
/// One line of typed text with a caret: what somebody has written and where they are in it.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Built once, as a thing, because four separate features were each waiting for it.</b>
/// A seed to start a world with, a search box in the recipe book, a path in the pack importer and a
/// place name on the map — nothing in the game accepted a typed character at all, and each of those
/// would otherwise have grown its own half of one.</para>
/// <para><b>It knows nothing about an input library.</b> Characters arrive as characters and edits
/// arrive by name, so the whole of it can be typed into and read back headlessly. The client's job
/// is to turn a platform's key events into these calls and nothing more.</para>
/// <para>⚠ <b>A character the font cannot draw is refused here rather than dropped later.</b> The
/// font is 95 glyphs, ASCII 32 to 126, and it is one texture layer per glyph — so a character
/// outside that range has no layer to draw and would come out as a hole in the middle of a word, or
/// as somebody else's letter. Refusing it at the keyboard is the only place the person typing finds
/// out.</para>
/// <para>No selection, deliberately. A caret, backspace and the arrows are the whole of what a seed
/// box or a search line needs, and a selection means a second index in every method here and a
/// highlight in the renderer. Paste arrives as <see cref="Insert(string)"/>, which is the one thing
/// a selection would otherwise have been wanted for.</para>
/// </remarks>
public sealed class TextField(int maxLength = 64, TextAllows allows = TextAllows.Anything)
{
    /// <summary>The lowest and highest character the font has a glyph for.</summary>
    public const char FirstDrawable = ' ';
    public const char LastDrawable = '~';

    private string _text = "";

    public string Text
    {
        get => _text;
        set
        {
            _text = Keep(value);
            Caret = _text.Length;
        }
    }

    /// <summary>Where the next character goes, 0 to <see cref="Text"/>'s length.</summary>
    public int Caret { get; private set; }

    public int MaxLength { get; } = maxLength;

    public TextAllows Allows { get; } = allows;

    /// <summary>Shown, dimmed, when nothing has been typed. Never part of <see cref="Text"/>.</summary>
    public string Placeholder { get; init; } = "";

    public bool Empty => _text.Length == 0;

    /// <summary>True when this character may be typed into this field.</summary>
    public bool Accepts(char c)
    {
        if (c is < FirstDrawable or > LastDrawable) return false;

        return Allows switch
        {
            TextAllows.Digits => c is >= '0' and <= '9',

            // ⚠ Asked of the platform rather than written out. The set differs between them, and a
            // list copied into here is a list that is right on the machine it was copied on.
            TextAllows.FileSafe => !Path.GetInvalidFileNameChars().Contains(c),
            _ => true,
        };
    }

    /// <summary>Types one character at the caret. False when it was refused or there was no room.</summary>
    public bool Insert(char c)
    {
        if (!Accepts(c) || _text.Length >= MaxLength) return false;

        _text = _text.Insert(Caret, c.ToString());
        Caret++;
        return true;
    }

    /// <summary>
    /// Types a whole string at the caret — a paste — and says how many characters landed.
    /// </summary>
    /// <remarks>
    /// Character by character through the same gate, so a pasted newline or a pasted slash is
    /// refused exactly as a typed one is rather than arriving by the side door.
    /// </remarks>
    public int Insert(string run)
    {
        var taken = 0;
        foreach (var c in run)
            if (Insert(c)) taken++;

        return taken;
    }

    /// <summary>Takes the character before the caret.</summary>
    public bool Backspace()
    {
        if (Caret == 0) return false;

        _text = _text.Remove(Caret - 1, 1);
        Caret--;
        return true;
    }

    /// <summary>Takes the character after the caret.</summary>
    public bool Delete()
    {
        if (Caret >= _text.Length) return false;

        _text = _text.Remove(Caret, 1);
        return true;
    }

    public void Left() => Caret = Math.Max(0, Caret - 1);

    public void Right() => Caret = Math.Min(_text.Length, Caret + 1);

    public void Home() => Caret = 0;

    public void End() => Caret = _text.Length;

    public void Clear()
    {
        _text = "";
        Caret = 0;
    }

    /// <summary>What is left of a string once everything this field refuses is taken out of it.</summary>
    private string Keep(string from)
    {
        var kept = new System.Text.StringBuilder(Math.Min(from.Length, MaxLength));
        foreach (var c in from)
        {
            if (kept.Length >= MaxLength) break;
            if (Accepts(c)) kept.Append(c);
        }

        return kept.ToString();
    }
}
