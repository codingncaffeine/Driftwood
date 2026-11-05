using System.Security.Cryptography;

namespace Driftwood.Core.Net;

/// <summary>
/// A world opened with a code (the user, 2026-11-04): not shown in Friends' Worlds, found on Epic by
/// its code, and the host lets in only guests who give it.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Eight characters, read aloud or typed without doubt</b>: <c>K7MQ-3XHD</c>. The alphabet has no
/// 0/O or 1/I/L, 31 symbols, so ~40 bits — far past guessing within one evening's session.</para>
/// <para>⛳ <b>The code is the key, not only the hiding place.</b> A listing on Epic can always be found by
/// a client that asks for every listing; the host's door (<see cref="CoopHost.Code"/>) is what keeps
/// such a client out.</para>
/// </remarks>
public static class JoinCodes
{
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ"; // 31 symbols: no 0 O 1 I L

    public const int Length = 8;

    /// <summary>A fresh code, normalized (no dash).</summary>
    public static string New()
    {
        Span<char> code = stackalloc char[Length];
        for (var i = 0; i < Length; i++) code[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(code);
    }

    /// <summary>"K7MQ-3XHD" for showing.</summary>
    public static string Show(string code) => code.Length == Length ? $"{code[..4]}-{code[4..]}" : code;

    /// <summary>
    /// What a player typed, as a code — case, a dash and spaces ignored — or null when it is not one
    /// (an address). A code never holds 0, O, 1, I or L, so a word or a number is not mistaken for one.
    /// </summary>
    /// <remarks>⚠ Only the "XXXX-XXXX" shape (or eight characters with no dot or colon) counts, so a
    /// typed host name like "pine" or "192.168.1.20" is never taken for a code.</remarks>
    public static string? Read(string? typed)
    {
        if (typed is null) return null;
        var text = typed.Trim().ToUpperInvariant().Replace(" ", "");
        if (text.Length == Length + 1 && text[4] == '-') text = text.Remove(4, 1);
        if (text.Length != Length) return null;

        Span<char> code = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
        {
            var c = text[i];
            if (Alphabet.IndexOf(c) < 0) return null;
            code[i] = c;
        }

        return new string(code);
    }
}
