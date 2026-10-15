namespace Driftwood.Core.Saves;

/// <summary>
/// The characters a file name the game makes may not hold, on every platform at once.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Written out rather than asked of the platform</b>, which is what every one of these
/// sites used to do — and asking was right only while the game ran on one platform. Linux refuses
/// just <c>/</c> and NUL; Windows refuses those and <c>" &lt; &gt; | : * ? \</c> and every control
/// character. A world, skin or pack named on Linux with a colon in it is a file that cannot be
/// copied to a Windows machine, unzipped there, or synced to one — and players move saves between
/// the two. So every platform keeps to the set both can hold. The release audit found it,
/// 2026-10-15: on Linux a file-safe box took <c>myworld:2</c>.</para>
/// <para>⚠ The Windows set is a superset of the Linux one, so nothing a Linux file system needs
/// refused is lost by using it.</para>
/// </remarks>
public static class PortableFileName
{
    private static readonly char[] Refused = ['"', '<', '>', '|', ':', '*', '?', '\\', '/'];

    /// <summary>True when <paramref name="c"/> may not appear in a file name on some platform.</summary>
    public static bool IsInvalid(char c) => c < ' ' || Array.IndexOf(Refused, c) >= 0;
}
