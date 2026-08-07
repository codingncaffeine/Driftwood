namespace Driftwood.Core.Textures;

/// <summary>
/// The shelf of texture packs a player has installed, and how one gets onto it.
/// </summary>
/// <remarks>
/// <para>⛳ <b>A folder rather than a remembered path, and that is the whole design.</b> Pointing the
/// game at a file where it happens to sit today means the setting breaks the day a download folder is
/// tidied — and it means the list of packs is a list of one. A shelf makes the setting a <em>name</em>
/// rather than a path, so it survives the file moving, and it makes switching between two packs a
/// thing a player can do without going and finding either of them again.</para>
/// <para>⛔ <b>An unreadable pack is LISTED WITH A REASON, never dropped.</b> This is the same fault
/// the saves list had, and it cost a session: <c>WorldSave.List()</c> quietly skipped a file it could
/// not open, so "no worlds" and "a world I cannot open" were the same four words on screen. A pack
/// that will not open is the more likely of the two here — people download all sorts — and being told
/// <em>which</em> file and <em>why</em> is the difference between a bug report and a fixable mistake.
/// </para>
/// <para>⚠ <b>Copied in rather than referenced.</b> The alternative is a shelf of shortcuts that rot,
/// and the files are tens of megabytes at most. It also means the shelf is one folder a player can
/// open, look at, and empty by hand — which is what somebody will do first when something is wrong.
/// </para>
/// </remarks>
public static class PackLibrary
{
    /// <summary>The shapes a pack arrives in. Everything else is refused by name rather than tried.</summary>
    /// <remarks>
    /// ⛔ <b>The reader's own list, not a second copy of it.</b> It was written out here as well, and
    /// two lists of the same extensions is exactly the drift <see cref="FilterSpec"/>'s own note
    /// warns about one line further down — the shelf would take a <c>.jar</c>, the browser would
    /// hide it, and a player with a perfectly good pack would be staring at a folder the game says
    /// is empty. One list, in the file that does the opening.
    /// </remarks>
    public static IReadOnlyList<string> Extensions { get; } = TexturePack.Extensions;

    /// <summary>What a file chooser should call these in its "files of type" line.</summary>
    public static string FilterLabel => $"texture packs ({string.Join(", ", Extensions)})";

    /// <summary>
    /// The masks a file chooser wants, e.g. <c>*.zip;*.mcpack;*.mcaddon</c>.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Built from the list above rather than written out beside it.</b> Two lists of the same
    /// extensions is a shape the shelf accepts and the browser hides — a player with a perfectly
    /// good <c>.mcaddon</c> staring at a folder the game says is empty, with nothing anywhere
    /// saying why. One list, and adding to it is one edit.
    /// </remarks>
    public static string FilterSpec => string.Join(";", Extensions.Select(static e => $"*{e}"));

    /// <summary>One pack on the shelf.</summary>
    /// <param name="Name">What it is called, and what the setting stores.</param>
    /// <param name="Path">Where it is.</param>
    /// <param name="Kind">Which layout it turned out to be, or why it could not be read.</param>
    /// <param name="Readable">False when opening it failed; <see cref="Kind"/> then says why.</param>
    public readonly record struct Entry(string Name, string Path, string Kind, bool Readable);

    /// <summary>Where installed packs live.</summary>
    public static string Folder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Driftwood", "packs");

    /// <summary>
    /// Everything on the shelf, in name order, each opened far enough to say what it is.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Opened, not merely listed.</b> Reading the header is what turns "a file called
    /// Really Real.mcpack" into "a Bedrock pack at 512 pixels" — which is the only thing on the row
    /// that tells a player whether they have downloaded what they meant to.
    /// </remarks>
    public static IReadOnlyList<Entry> List()
    {
        var found = new List<Entry>();
        if (!Directory.Exists(Folder)) return found;

        foreach (var path in Directory.EnumerateFileSystemEntries(Folder))
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (name.Length == 0) continue;

            var isFolder = Directory.Exists(path);
            if (!isFolder && !Extensions.Contains(
                    System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;

            found.Add(Describe(name, path));
        }

        found.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return found;
    }

    /// <summary>The path a pack name resolves to, or null when it is not on the shelf.</summary>
    /// <remarks>
    /// ⚠ <b>By name and never by index.</b> A setting holding "the third one" is a setting that means
    /// something different the moment anything is added — and adding is what this screen is for.
    /// </remarks>
    public static string? PathOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        foreach (var entry in List())
            if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)) return entry.Path;

        return null;
    }

    /// <summary>
    /// Copies a pack onto the shelf. Returns the entry, or null with a reason.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>It is opened BEFORE it is copied.</b> A shelf that accepts anything is a shelf a
    /// player fills with holiday photographs and then cannot work out why the game looks the same —
    /// and the failure would arrive one relaunch later, attached to the wrong action. Refusing at the
    /// moment of the mistake, with the reason, is the only place it reads as an answer.</para>
    /// <para>⚠ A name already on the shelf is replaced rather than duplicated. Re-importing is what a
    /// player does when a pack has been updated, and "Really Real (2)" is not what they asked for.
    /// </para>
    /// </remarks>
    public static Entry? Install(string from, out string why)
    {
        why = "";

        if (string.IsNullOrWhiteSpace(from))
        {
            why = "no path given";
            return null;
        }

        from = from.Trim().Trim('"');

        var isFolder = Directory.Exists(from);
        if (!isFolder && !File.Exists(from))
        {
            why = "there is nothing at that path";
            return null;
        }

        // ⛳ The reader answers this now, so a .rar is told it is a .rar rather than told it is not
        // a pack — and the sentence is written once, where the extension list lives.
        if (!isFolder && !Extensions.Contains(
                System.IO.Path.GetExtension(from), StringComparer.OrdinalIgnoreCase))
        {
            TexturePack.Open(from, out why);
            return null;
        }

        // ⛔ Read it where it stands. Copying first and finding out afterwards leaves a shelf with
        // rubbish on it and a player with no idea which row is the bad one.
        var probe = Describe(System.IO.Path.GetFileNameWithoutExtension(from), from);
        if (!probe.Readable)
        {
            why = probe.Kind;
            return null;
        }

        try
        {
            Directory.CreateDirectory(Folder);

            var name = System.IO.Path.GetFileName(from.TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

            var landing = System.IO.Path.Combine(Folder, name);

            // Already where it needs to be, which is what happens when somebody points the box at
            // the shelf itself.
            if (string.Equals(
                    System.IO.Path.GetFullPath(landing),
                    System.IO.Path.GetFullPath(from),
                    StringComparison.OrdinalIgnoreCase))
                return Describe(System.IO.Path.GetFileNameWithoutExtension(landing), landing);

            if (isFolder) CopyFolder(from, landing);
            else File.Copy(from, landing, overwrite: true);

            return Describe(System.IO.Path.GetFileNameWithoutExtension(landing), landing);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            why = error.Message;
            return null;
        }
    }

    /// <summary>Takes a pack off the shelf. True when there is no longer one by that name.</summary>
    public static bool Remove(string name)
    {
        var path = PathOf(name);
        if (path is null) return true;

        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Opens a pack far enough to say what it is, or why it will not open.</summary>
    private static Entry Describe(string name, string path)
    {
        try
        {
            using var pack = TexturePack.Open(path);
            if (pack is null) return new Entry(name, path, "not a texture pack", false);

            // ⛔ AN UNRECOGNISED LAYOUT IS NOT READABLE, and this was found on a real file rather
            // than reasoned about. Picture-perfect-pack-128X128 opens perfectly, reports no dialect,
            // and yields nothing — so the shelf called it fine, and a player would have worn it,
            // relaunched, and seen exactly no change with nothing anywhere saying why. Opening
            // without exploding is not the same as being usable.
            // ⛳ Picture-perfect-pack-128X128 is why this line exists AND why it now has a fourth
            // dialect in it. It opened perfectly, reported no dialect and yielded nothing, so the
            // shelf called it fine — a player would have worn it, relaunched, and seen no change
            // with nothing anywhere saying why. It was refused by name, and refusing it was the
            // right answer only until the layout could be read.
            if (pack.Dialect is not (PackDialect.Java or PackDialect.JavaLegacy
                                     or PackDialect.Bedrock or PackDialect.Atlas))
                return new Entry(name, path, Unrecognised(pack), false);

            var size = pack.DetectResolution();
            var dialect = pack.Dialect switch
            {
                PackDialect.Java => "Java",
                PackDialect.JavaLegacy => "Java, pre-flattening",
                PackDialect.Atlas => "pre-2013 terrain.png",
                _ => "Bedrock",
            };

            return new Entry(name, path, $"{dialect}, {size}px", true);
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // ⛔ The REASON, not a shrug. A row that says only "cannot read" sends a player looking
            // for a bug in the game rather than at the half-downloaded file they actually have.
            return new Entry(name, path, error.Message, false);
        }
    }

    /// <summary>
    /// Names the shape a pack turned out to be when it is one we do not read.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>"We do not read this yet" is a far better answer than "cannot read this".</b> The 2012
    /// atlas format is a real, enormous body of packs — one <c>terrain.png</c> holding every block
    /// on a grid, with <c>pack.txt</c> beside it — and somebody holding one has not done anything
    /// wrong. Telling them which format it is turns a dead end into a thing they can look up, and
    /// tells us which format to add next.
    /// </remarks>
    private static string Unrecognised(TexturePack pack) =>
        pack.Has("terrain.png")
            ? "a pre-2013 terrain.png pack whose grid is not where it should be"
            : "the layout is not one we know: no assets/, no textures/, no terrain.png";

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var landing = System.IO.Path.Combine(to, System.IO.Path.GetRelativePath(from, file));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(landing)!);
            File.Copy(file, landing, overwrite: true);
        }
    }
}
