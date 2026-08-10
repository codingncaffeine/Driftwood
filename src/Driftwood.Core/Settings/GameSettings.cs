using System.Globalization;
using System.Text;

namespace Driftwood.Core.Settings;

/// <summary>
/// Everything a player has changed about the game, and the file it lives in.
/// </summary>
/// <remarks>
/// <para>Plain <c>key=value</c> lines rather than a serialiser. It is a file a person opens when
/// something has gone wrong, and every format that is easier to write is harder to read; this one
/// diffs, greps and hand-edits. It also costs no package, which this project does not take.</para>
/// <para>Keys it does not recognise are kept and written back out. A settings file edited by a
/// newer build and then opened by an older one should lose nothing, and the alternative — silently
/// dropping whatever the reader did not expect — is the way a downgrade eats somebody's controls.
/// </para>
/// <para>It lives beside the player's other application data rather than beside the executable,
/// because the executable is republished on every build and anything next to it is scaffolding.</para>
/// </remarks>
public sealed class GameSettings
{
    /// <summary>How far the world is kept loaded, in chunks. Applies when the game is next opened.</summary>
    public int ViewDistance { get; set; } = 8;

    public int FieldOfView { get; set; } = 70;

    public bool Fullscreen { get; set; }

    public bool VSync { get; set; }

    /// <summary>
    /// The most frames a second the game will draw, or 0 for as many as it can.
    /// </summary>
    /// <remarks>
    /// <para>⛳⛳ <b>175 by default, the user's own number — it is what their display can show.</b>
    /// Uncapped, this game measures about <b>5,000 frames a second</b> on that machine, which is
    /// twenty-eight frames drawn for every one anybody sees: fans, heat and battery spent on pictures
    /// thrown away.</para>
    /// <para>⛔ <b>It is NOT a fix for anything, and must never be treated as one.</b> A rate written
    /// per frame is wrong at any frame rate — capping to 175 would only have turned a lungful that
    /// vanished in 59 milliseconds into one that vanished in 1.7 seconds, which is still wrong and is
    /// far harder to notice. Rates go in seconds and the audit runs them at three frame rates; this
    /// setting exists because 5,000 fps is wasteful, not because it is dangerous.</para>
    /// <para>⚠ <b>Ignored while the display is being waited for</b> — vsync is already a cap, and two
    /// limiters fighting is how a steady 175 becomes an uneven 87.</para>
    /// </remarks>
    public int FrameCap { get; set; } = 175;

    /// <summary>Everything's loudness, 0 to 100.</summary>
    public int Volume { get; set; } = 100;

    public bool Mute { get; set; }

    /// <summary>The sound pack to use, by stable ID on <see cref="Audio.SoundPackLibrary"/>.</summary>
    /// <remarks>
    /// Empty means Driftwood's five owned fallback recordings. The setting never stores a CDN URL
    /// or a downloads-folder path: the original archive is copied to AppData first, so an author's
    /// update or a tidied Downloads folder cannot silently change what the player selected.
    /// </remarks>
    public string SoundPack { get; set; } = "";

    /// <summary>How fast looking around is, as a percentage of the old fixed rate.</summary>
    public int MouseSensitivity { get; set; } = 100;

    /// <summary>
    /// Whether to say so in the corner the first time something becomes makeable.
    /// </summary>
    /// <remarks>
    /// On, because it is how a new player finds out that picking up coal has given them torches,
    /// and it never repeats itself — what has been said is remembered between sessions. Off for
    /// anyone who already knows the tree and would rather have the corner back.
    /// </remarks>
    public bool RecipeNotices { get; set; } = true;

    /// <summary>
    /// A folder of creature skeletons to read at startup, or empty for no animals.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>A path rather than a switch, and it lives here rather than on the command line, because
    /// it cannot be found by looking.</b> The skeletons ship with an installed Bedrock client, and
    /// enumerating <c>WindowsApps</c> to find that install throws for a plain process even where a
    /// known path under it opens perfectly. So it is said once — by <c>--creature-geometry</c> now,
    /// by the import screen later — and remembered.
    /// </remarks>
    public string CreatureGeometry { get; set; } = "";

    /// <summary>
    /// The texture pack to wear, by NAME on the shelf — empty for our own art.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>A name and not a path</b>, resolved through <see cref="Textures.PackLibrary"/>. A path
    /// remembered here breaks the day a downloads folder is tidied, and it makes the set of packs a
    /// player has a set of one. A name survives the file moving, and it lets two packs be swapped
    /// between without going and finding either of them again.
    /// </remarks>
    public string TexturePack { get; set; } = "";

    /// <summary>The player skin to wear, by name on <see cref="Textures.SkinLibrary"/>.</summary>
    public string PlayerSkin { get; set; } = "";

    public Bindings Keys { get; set; } = Bindings.Defaults();

    /// <summary>Lines the reader did not recognise, kept so a newer build's file survives an older one.</summary>
    private readonly Dictionary<string, string> _unknown = new(StringComparer.Ordinal);

    /// <summary>Where the file lives.</summary>
    public static string Path =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Driftwood",
            "settings.txt");

    /// <summary>
    /// Reads the file, falling back to the defaults for anything missing or unreadable.
    /// </summary>
    /// <remarks>
    /// A settings file is never a reason not to start. Every failure here — no file, a bad number,
    /// a key that names nothing — leaves that one setting at its default and lets the rest through,
    /// because the alternative is a game that will not open because somebody mistyped a volume.
    /// </remarks>
    public static GameSettings Load(string? path = null)
    {
        var settings = new GameSettings();
        path ??= Path;

        string[] lines;
        try
        {
            if (!File.Exists(path)) return settings;
            lines = File.ReadAllLines(path);
        }
        catch (Exception)
        {
            return settings;
        }

        // Only replace the defaults once a file has said something about bindings, so a file with
        // no bind lines in it keeps the shipped keys rather than ending up with none.
        var boundAnything = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var split = line.IndexOf('=');
            if (split <= 0) continue;

            var key = line[..split].Trim().ToLowerInvariant();
            var value = line[(split + 1)..].Trim();

            if (key.StartsWith("bind.", StringComparison.Ordinal))
            {
                if (!settings.ReadBinding(key, value, ref boundAnything)) settings._unknown[key] = value;
                continue;
            }

            switch (key)
            {
                case "video.viewdistance": settings.ViewDistance = Int(value, 2, 32, settings.ViewDistance); break;
                case "video.fov": settings.FieldOfView = Int(value, 50, 110, settings.FieldOfView); break;
                case "video.fullscreen": settings.Fullscreen = Bool(value, settings.Fullscreen); break;
                case "video.vsync": settings.VSync = Bool(value, settings.VSync); break;

                // ⚠ Zero means uncapped and is inside the range on purpose, so a player who wants
                // every frame the machine can make can still say so.
                case "video.framecap": settings.FrameCap = Int(value, 0, 1000, settings.FrameCap); break;
                case "audio.volume": settings.Volume = Int(value, 0, 100, settings.Volume); break;
                case "audio.mute": settings.Mute = Bool(value, settings.Mute); break;
                case "audio.soundpack": settings.SoundPack = value; break;
                case "input.sensitivity": settings.MouseSensitivity = Int(value, 10, 400, settings.MouseSensitivity); break;
                case "ui.recipenotices": settings.RecipeNotices = Bool(value, settings.RecipeNotices); break;

                // ⚠ Taken verbatim, not trimmed of anything but its edges. A Windows path is full of
                // characters every other value here would reject, and one of them is a backslash.
                case "world.creaturegeometry": settings.CreatureGeometry = value; break;
                case "video.texturepack": settings.TexturePack = value; break;
                case "player.skin": settings.PlayerSkin = value; break;
                default: settings._unknown[key] = value; break;
            }
        }

        // Anything the file said nothing about gets its shipped key back.
        //
        // This is the upgrade path, and without it renaming an action silently unbinds it: a file
        // written by an older build names actions that no longer exist, the first one that IS
        // recognised throws away every default, and the renamed ones are left with no key on them
        // and nothing on screen saying so. Filling the gaps afterwards costs nothing when the file
        // is current and is the difference between a rename and a player who cannot open their
        // inventory. A key already taken by something else is left alone rather than duplicated.
        if (boundAnything) settings.Keys.FillGapsFrom(Bindings.Defaults());

        return settings;
    }

    /// <summary>
    /// Writes the file, and reports whether it landed.
    /// </summary>
    /// <remarks>
    /// Written through a temporary file and moved into place, so a crash or a full disk mid-write
    /// leaves the old settings rather than half of the new ones. It is a small file and this costs
    /// nothing; the alternative is a player losing every binding to a power cut.
    /// </remarks>
    public bool Save(string? path = null)
    {
        path ??= Path;

        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var temporary = path + ".new";
            File.WriteAllText(temporary, Write(), Encoding.UTF8);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The file's contents, for saving and for the round-trip check.</summary>
    public string Write()
    {
        var text = new StringBuilder();
        text.AppendLine("# Driftwood settings. Delete a line to go back to its default.");
        text.AppendLine();
        text.AppendLine($"video.viewdistance={ViewDistance}");
        text.AppendLine($"video.framecap={FrameCap}");
        text.AppendLine($"video.fov={FieldOfView}");
        text.AppendLine($"video.fullscreen={Text(Fullscreen)}");
        text.AppendLine($"video.vsync={Text(VSync)}");
        text.AppendLine();
        text.AppendLine($"audio.volume={Volume}");
        text.AppendLine($"audio.mute={Text(Mute)}");
        if (SoundPack.Length > 0) text.AppendLine($"audio.soundpack={SoundPack}");
        text.AppendLine();
        text.AppendLine($"input.sensitivity={MouseSensitivity}");
        text.AppendLine();
        text.AppendLine($"ui.recipenotices={Text(RecipeNotices)}");
        text.AppendLine();

        // Only written when there is one, so a file from a machine with no creature geometry does
        // not carry an empty key that reads like a setting somebody cleared.
        if (CreatureGeometry.Length > 0)
        {
            text.AppendLine($"world.creaturegeometry={CreatureGeometry}");
            text.AppendLine();
        }

        if (TexturePack.Length > 0)
        {
            text.AppendLine($"video.texturepack={TexturePack}");
            text.AppendLine();
        }

        if (PlayerSkin.Length > 0)
        {
            text.AppendLine($"player.skin={PlayerSkin}");
            text.AppendLine();
        }


        foreach (var action in GameActions.All)
        {
            var name = action.ToString().ToLowerInvariant();
            text.AppendLine($"bind.{name}={Keys.Primary(action)}");

            if (Keys.Secondary(action).Length == 0) continue;
            text.AppendLine($"bind.{name}.2={Keys.Secondary(action)}");
        }

        if (_unknown.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("# kept from a file this build did not recognise");
            foreach (var (key, value) in _unknown) text.AppendLine($"{key}={value}");
        }

        return text.ToString();
    }

    private bool ReadBinding(string key, string value, ref bool boundAnything)
    {
        var secondary = key.EndsWith(".2", StringComparison.Ordinal);
        var name = key[5..];
        if (secondary) name = name[..^2];

        if (!Enum.TryParse<GameAction>(name, ignoreCase: true, out var action)) return false;

        // The shipped keys stay until the file says otherwise, and then they all go at once — a
        // file that binds three things should not leave the other twenty on their defaults and
        // collide with them.
        if (!boundAnything)
        {
            boundAnything = true;
            Keys = new Bindings();
        }

        Keys.Set(
            action,
            secondary ? Keys.Primary(action) : value,
            secondary ? value : Keys.Secondary(action));

        return true;
    }

    private static string Text(bool value) => value ? "true" : "false";

    private static int Int(string text, int min, int max, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;

    private static bool Bool(string text, bool fallback) => text.ToLowerInvariant() switch
    {
        "true" or "yes" or "on" or "1" => true,
        "false" or "no" or "off" or "0" => false,
        _ => fallback,
    };
}
