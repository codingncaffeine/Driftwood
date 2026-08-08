using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;
using Driftwood.Core.Items;
using Driftwood.Core.World;

namespace Driftwood.Core.Saves;

/// <summary>Everything about a world that is not the world, for a list to show.</summary>
/// <param name="Saved">When it was written, in real time.</param>
/// <param name="Played">Seconds anybody has spent in it.</param>
/// <param name="DayTime">Where the sun was, 0 to 1 through a day.</param>
public readonly record struct SaveHeader(
    string Name, string Seed, DateTime Saved, double Played, float DayTime, int Edits)
{
    /// <summary>Played time as a person would say it.</summary>
    public string PlayedFor
    {
        get
        {
            var span = TimeSpan.FromSeconds(Played);
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes}m"
                : $"{span.Minutes}m {span.Seconds}s";
        }
    }
}

/// <summary>
/// Everything a session has that is worth keeping, handed over in one piece.
/// </summary>
/// <remarks>
/// A record of references rather than a copy. Saving reads them on the thread that owns them and
/// writes bytes; nothing here is held past that.
/// </remarks>
public sealed record WorldState(
    string Seed,
    ItemRegistry Catalogue,
    VoxelWorld World,
    FurnaceBank Furnaces,
    ChestBank Chests,
    Inventory Pockets,
    Equipment Worn,
    PlayerVitals Vitals,
    RecipeUnlocks Unlocks)
{
    public Vector3 Position { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public double Played { get; set; }
    public float DayTime { get; set; }

    /// <summary>The animals standing in the world, both ways through a save.</summary>
    /// <remarks>
    /// ⛳ A list rather than the herd itself, because the herd does not exist until the world has
    /// been stood up — loading stashes these and the herd takes them the moment it is built.
    /// </remarks>
    public List<Entities.CreatureHerd.SavedCreature> Creatures { get; } = [];
}

/// <summary>
/// Writes a world to disk and reads it back.
/// </summary>
/// <remarks>
/// <para><b>The world itself is barely in here.</b> A chunk is a pure function of seed and position,
/// decoration included, so terrain is never written down — a save is the seed and the difference
/// between what the generator makes and what somebody built. A world somebody has walked across for
/// an hour and changed forty blocks in is forty blocks on disk.</para>
/// <para><b>Everything is written through a palette of names.</b> See <see cref="Palette"/> for why
/// that is not optional: ids are handed out in registration order and adding a block shifts every
/// one after it.</para>
/// <para><b>The write is atomic.</b> Everything goes to a temporary file which is moved over the
/// real one only once it is complete, so a save interrupted half way through leaves the previous
/// one intact rather than a truncated file where a world used to be. The rule is inside this class
/// rather than left to a caller to remember.</para>
/// </remarks>
public static class WorldSave
{
    /// <summary>Where worlds live.</summary>
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Driftwood", "saves");

    public static string PathFor(string name) => Path.Combine(Folder, $"{Sanitised(name)}.dws");

    /// <summary>What a bare launch opens, and what an instrument opens instead.</summary>
    public const string DefaultWorld = "world";

    public const string TestWorld = "driftwood-test";

    /// <summary>
    /// Which world a launch opens, from what it was given.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>A rule with a check beside it rather than four lines at the call site, because it
    /// was wrong and nobody could see it.</b> <c>--play</c> exists to close the window the way a
    /// player does, which means it saves on quit — and with no <c>--world</c> it fell through to the
    /// same name a double-click opens. Every timing run loaded somebody's world, played in it, and
    /// wrote it back.</para>
    /// <para>The order is: a name that was typed, then the test world for anything that is an
    /// instrument, then the seed, then the default. ⚠ <b>The instrument beats the seed</b> — a seeded
    /// instrument run still gets that seed's terrain and still does not get a world of its own, which
    /// is the case that left ten of them in a player's saves folder across two sessions and read from
    /// inside the game as a save list breeding by itself.</para>
    /// </remarks>
    public static string NameFor(string? typed, bool instrument, bool seedGiven, string? seedText) =>
        Sanitised(
            !string.IsNullOrWhiteSpace(typed) ? typed!
            : instrument ? TestWorld
            : seedGiven && !string.IsNullOrWhiteSpace(seedText) ? $"world-{seedText}"
            : DefaultWorld);

    /// <summary>Checks a launch opens the world it should, and never somebody's own by accident.</summary>
    /// <remarks>
    /// ⛔ <b>The pairs are the check.</b> "An instrument opens the test world" is equally true of a
    /// rule that opens it always, so a plain launch and a seeded one are asserted beside it — and the
    /// case that actually broke, a seeded instrument run, is asserted against both.
    /// </remarks>
    public static List<string> ValidateNaming()
    {
        var faults = new List<string>();

        void Want(string what, string got, string expected)
        {
            if (got == expected) return;
            faults.Add($"{what} opens '{got}' rather than '{expected}'");
        }

        Want("a bare launch", NameFor(null, false, false, null), DefaultWorld);
        Want("a seeded launch", NameFor(null, false, true, "stonebreak"), "world-stonebreak");
        Want("a named launch", NameFor("harbour", false, false, null), "harbour");

        // ⛔ THE THREE THAT MATTER. An instrument may not reach a real world by any route that does
        // not involve somebody typing its name.
        Want("an instrument run", NameFor(null, true, false, null), TestWorld);
        Want("a seeded instrument run", NameFor(null, true, true, "stonebreak"), TestWorld);
        Want("a named instrument run", NameFor("harbour", true, false, null), "harbour");

        return faults;
    }

    /// <summary>How many previous states of a world are kept beside it.</summary>
    public const int Backups = 3;

    /// <summary>
    /// Where an earlier state of a world lives. Slot 1 is the most recent.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>A different extension on purpose.</b> <see cref="List"/> enumerates <c>*.dws</c>, so a
    /// backup named as one would appear in the list as a world of its own — four entries where
    /// somebody has one world, three of them older copies they never made. Being unable to match
    /// beats remembering to filter.
    /// </remarks>
    public static string BackupPath(string name, int slot) =>
        Path.Combine(Folder, $"{Sanitised(name)}.{slot}.dwsbak");

    /// <summary>
    /// Throws a world away: its save and every backup beside it.
    /// </summary>
    /// <returns>How many files were removed, or -1 when the world does not exist.</returns>
    /// <remarks>
    /// <para>⛔ <b>BY NAME, one file at a time, and never a wildcard.</b> This project has already
    /// deleted somebody's game with a glob aimed at a saves folder. A world is exactly
    /// <c>&lt;name&gt;.dws</c> plus at most <see cref="Backups"/> numbered <c>.dwsbak</c> files, which
    /// is four known paths — the set is enumerable, so there is no reason whatever to ask the
    /// filesystem to match a pattern and delete what comes back.</para>
    /// <para>⚠ <b>The backups go with it and that is the decision.</b> Leaving them would mean a
    /// world deleted from the list is still three files on the disk, which is the opposite of what
    /// somebody clearing space asked for — and the row they used to reach them is gone, so they
    /// could never be recovered from inside the game anyway.</para>
    /// <para>⚠ Refusing to delete the world currently open is <em>not</em> enforced here. The
    /// caller knows what is open; this knows about files. A rule written in both places is a rule
    /// that can disagree with itself.</para>
    /// </remarks>
    public static int Delete(string name)
    {
        var save = PathFor(name);
        if (!File.Exists(save)) return -1;

        var removed = 0;

        File.Delete(save);
        removed++;

        for (var slot = 1; slot <= Backups; slot++)
        {
            var backup = BackupPath(name, slot);
            if (!File.Exists(backup)) continue;

            File.Delete(backup);
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// Moves the world's previous states down a slot and takes a copy of the current one.
    /// </summary>
    /// <remarks>
    /// <para>The write itself cannot half-happen — it goes to a temporary file and is moved over the
    /// real one — so this is not insurance against a torn file. It is insurance against a
    /// <em>state</em>: an autosave taken as somebody falls into lava, or after a build of ours does
    /// something to a world that it should not have. Those are recoverable only if the step before
    /// them still exists.</para>
    /// <para><b>The current file is copied, never moved.</b> Moving it would leave a moment with no
    /// world on disk at all, and that moment is exactly when the power goes off.</para>
    /// <para>A failure here is reported and never stops the save. A world that will not write
    /// because its backup would not is worse than a world with no backup.</para>
    /// </remarks>
    /// <returns>Null on success, or what went wrong.</returns>
    public static string? Backup(string name)
    {
        try
        {
            var current = PathFor(name);
            if (!File.Exists(current)) return null;

            var oldest = BackupPath(name, Backups);
            if (File.Exists(oldest)) File.Delete(oldest);

            for (var slot = Backups - 1; slot >= 1; slot--)
            {
                var from = BackupPath(name, slot);
                if (File.Exists(from)) File.Move(from, BackupPath(name, slot + 1), overwrite: true);
            }

            File.Copy(current, BackupPath(name, 1), overwrite: true);
            return null;
        }
        catch (Exception fault)
        {
            return fault.Message;
        }
    }

    /// <summary>
    /// A name that is safe to be a file name, and still recognisably what was typed.
    /// </summary>
    /// <remarks>
    /// A world called "con" or "my/world" is a world somebody typed, not an attack — but both are
    /// things a file system refuses or misreads, and the failure would land as "saving does not
    /// work" rather than as anything to do with the name.
    /// </remarks>
    public static string Sanitised(string name)
    {
        var clean = new string([.. name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]).Trim();
        if (clean.Length == 0) clean = "world";

        string[] refused = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "LPT1", "LPT2"];
        if (refused.Contains(clean, StringComparer.OrdinalIgnoreCase)) clean += "_";

        return clean.Length > 64 ? clean[..64] : clean;
    }

    /// <summary>
    /// Writes a world, and leaves whatever was there before untouched until it is finished.
    /// </summary>
    /// <returns>Null on success, or what went wrong.</returns>
    public static string? Write(string name, WorldState state)
    {
        try
        {
            Directory.CreateDirectory(Folder);

            var blocks = new Palette();
            var items = new Palette();

            // Built before anything is written, because the palette has to be at the front of the
            // file and it is not known until everything that uses it has been walked.
            var edits = EditBytes(state.World, blocks);
            var furnaces = FurnaceBytes(state.Furnaces, items, state.Catalogue);
            var chests = ChestBytes(state.Chests, items, state.Catalogue);
            var player = PlayerBytes(state, items);

            var path = PathFor(name);
            var temporary = path + ".writing";

            using (var file = File.Create(temporary))
            using (var into = new BinaryWriter(file))
            {
                into.Write(SaveSection.Magic);
                into.Write(SaveSection.Version);

                SaveSection.Write(into, "HEAD", Bytes(head =>
                {
                    head.Write(name);
                    head.Write(state.Seed);
                    head.Write(DateTime.UtcNow.ToBinary());
                    head.Write(state.Played);
                    head.Write(state.DayTime);
                    head.Write(state.World.Edits.Count);
                }));

                SaveSection.Write(into, "PALB", Bytes(blocks.Write));
                SaveSection.Write(into, "PALI", Bytes(items.Write));
                SaveSection.Write(into, "EDIT", edits);
                SaveSection.Write(into, "FURN", furnaces);
                SaveSection.Write(into, "CHST", chests);
                SaveSection.Write(into, "PLYR", player);

                SaveSection.Write(into, "UNLK", Bytes(unlocked =>
                {
                    unlocked.Write(state.Unlocks.Names.Count);
                    foreach (var recipe in state.Unlocks.Names) unlocked.Write(recipe);
                }));

                // ⛳ A section an older build has never heard of, and that is the compatibility
                // story: the reader skips unknown tags, so a save with animals in it opens
                // everywhere — an old build re-rolls its herds, which is what it always did.
                SaveSection.Write(into, "CRTR", Bytes(beasts => WriteCreatures(beasts, state.Creatures)));
            }

            File.Move(temporary, path, overwrite: true);

            // Both, and the second is easy to forget. Picking something up can announce a recipe
            // without changing a block, so a periodic save that only watched the world would never
            // notice it had something to write.
            state.World.Settled();
            state.Unlocks.Settled();
            return null;
        }
        catch (Exception fault)
        {
            return fault.Message;
        }
    }

    /// <summary>A file in the saves folder that is not a world anybody can be shown.</summary>
    /// <param name="File">Its name, which is what somebody looking in the folder would see.</param>
    /// <param name="Why">What went wrong reading it, in words.</param>
    public readonly record struct UnreadableSave(string File, string Why);

    /// <summary>Every save on disk, newest first, without reading any of them past the header.</summary>
    public static List<SaveHeader> List() => List(out _);

    /// <summary>
    /// Every save on disk, and every file that looked like one and could not be read.
    /// </summary>
    /// <param name="unreadable">
    /// ⛔ <b>Filled rather than swallowed, and that is the whole point of the overload.</b> This
    /// returned a short list silently: a world whose header would not read was simply not added, so
    /// a player with a file sitting in the folder was told "none saved yet" and there was nowhere —
    /// not the screen, not the console — that said otherwise. "There are no worlds" and "there is a
    /// world I cannot open" are opposite problems and looked identical from the front.
    /// </param>
    public static List<SaveHeader> List(out List<UnreadableSave> unreadable)
    {
        var found = new List<SaveHeader>();
        unreadable = [];
        if (!Directory.Exists(Folder)) return found;

        foreach (var path in Directory.EnumerateFiles(Folder, "*.dws"))
        {
            if (TryReadHeader(path, out var header, out var why)) found.Add(header);
            else unreadable.Add(new UnreadableSave(Path.GetFileName(path), why ?? "unreadable"));
        }

        // ⚠ The other way a world can be sitting in the folder and not be in the list. A write goes
        // to a temporary file and is moved over the real one, so one of these left behind means a
        // save was interrupted between the two — which is the moment the player most needs telling,
        // because the file with their last hour in it is right there under a name nothing reads.
        foreach (var path in Directory.EnumerateFiles(Folder, "*.dws.writing"))
            unreadable.Add(new UnreadableSave(
                Path.GetFileName(path), "a save that did not finish being written"));

        found.Sort((a, b) => b.Saved.CompareTo(a.Saved));
        unreadable.Sort((a, b) => string.CompareOrdinal(a.File, b.File));
        return found;
    }

    /// <summary>Reads only the first section, which is all a list needs.</summary>
    public static bool TryReadHeader(string path, out SaveHeader header) =>
        TryReadHeader(path, out header, out _);

    /// <summary>Reads only the first section, and says why if it could not.</summary>
    /// <param name="why">Null when it read, otherwise what stopped it.</param>
    public static bool TryReadHeader(string path, out SaveHeader header, out string? why)
    {
        header = default;
        why = null;

        try
        {
            using var file = File.OpenRead(path);
            using var from = new BinaryReader(file);

            if (!Opens(from, out var reason))
            {
                why = reason;
                return false;
            }

            if (!SaveSection.TryRead(from, out var tag, out var payload) || tag != "HEAD")
            {
                why = "it does not begin with a header";
                return false;
            }

            using var head = new BinaryReader(new MemoryStream(payload));
            header = new SaveHeader(
                head.ReadString(), head.ReadString(),
                DateTime.FromBinary(head.ReadInt64()),
                head.ReadDouble(), head.ReadSingle(), head.ReadInt32());

            return true;
        }
        catch (Exception fault)
        {
            why = fault.Message;
            return false;
        }
    }

    /// <summary>
    /// Reads a world back into a session that is already standing.
    /// </summary>
    /// <param name="missing">
    /// Filled with every name this build no longer knows. A load says so rather than quietly
    /// dropping part of somebody's world.
    /// </param>
    /// <returns>Null on success, or what went wrong.</returns>
    public static string? Read(
        string path, BlockRegistry registry, ItemRegistry catalogue, WorldState into, List<string> missing)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var from = new BinaryReader(file);

            if (!Opens(from)) return "this is not a Driftwood world";

            Palette? blocks = null;
            Palette? items = null;
            byte[]? edits = null;
            byte[]? furnaces = null;
            byte[]? chests = null;
            byte[]? player = null;
            byte[]? unlocks = null;

            while (SaveSection.TryRead(from, out var tag, out var payload))
            {
                switch (tag)
                {
                    // Read rather than skipped. The loader wants the played time and where the sun
                    // was, and making it open the file a second time for two numbers it has already
                    // walked past is how the two copies end up disagreeing.
                    case "HEAD":
                    {
                        using var head = new BinaryReader(new MemoryStream(payload));
                        head.ReadString();      // name — the caller chose which file to open
                        head.ReadString();      // seed — likewise, and it built the world already
                        head.ReadInt64();       // when it was written, which List() is the reader of
                        into.Played = head.ReadDouble();
                        into.DayTime = head.ReadSingle();
                        break;
                    }
                    case "PALB": blocks = Palette.Read(Reader(payload)); break;
                    case "PALI": items = Palette.Read(Reader(payload)); break;
                    case "EDIT": edits = payload; break;
                    case "FURN": furnaces = payload; break;
                    case "CHST": chests = payload; break;
                    case "PLYR": player = payload; break;
                    case "UNLK": unlocks = payload; break;
                    case "CRTR": into.Creatures.AddRange(ReadCreatures(Reader(payload))); break;

                    // Written by a newer build than this one. Skipped, and deliberately not an
                    // error: the length said how far, which is the whole reason sections carry one.
                    default: break;
                }
            }

            if (blocks is null || items is null) return "this world has no palette, so nothing in it can be read";

            var toBlock = blocks.Resolve(n => registry.TryByName(n, out var b) ? b.Id.Value : -1, missing);
            var toItem = items.Resolve(n => catalogue.TryByName(n, out var i) ? i.Id.Value : -1, missing);

            if (edits is not null) ReadEdits(Reader(edits), into.World, toBlock);
            if (furnaces is not null) ReadFurnaces(Reader(furnaces), into.Furnaces, toItem);
            if (chests is not null) ReadChests(Reader(chests), into.Chests, toItem);
            if (player is not null) ReadPlayer(Reader(player), into, toItem);

            if (unlocks is not null)
            {
                var reader = Reader(unlocks);
                var count = reader.ReadInt32();
                var names = new List<string>(Math.Clamp(count, 0, 4096));
                for (var i = 0; i < count; i++) names.Add(reader.ReadString());
                into.Unlocks.Reload(names);
            }

            into.World.Settled();
            return null;
        }
        catch (Exception fault)
        {
            return fault.Message;
        }
    }

    /// <summary>The CRTR payload: a count, then each animal's identity.</summary>
    /// <remarks>
    /// ⚠ <b>Kinds by NAME, not by any table's index</b> — the palette rule, for the palette's
    /// reason: rows are added to <c>CreatureSet.All</c> and an index written today points at a
    /// different animal tomorrow. Internal so the audit can round-trip a herd without a file.
    /// </remarks>
    internal static void WriteCreatures(
        BinaryWriter into, IReadOnlyList<Entities.CreatureHerd.SavedCreature> creatures)
    {
        into.Write(creatures.Count);

        foreach (var one in creatures)
        {
            into.Write(one.Kind);
            into.Write(one.Position.X);
            into.Write(one.Position.Y);
            into.Write(one.Position.Z);
            into.Write(one.Yaw);
            into.Write(one.Health);
            into.Write(one.Shorn);
            into.Write(one.Regrows);
            into.Write(one.Provoked);
            into.Write(one.Grown);
        }
    }

    internal static List<Entities.CreatureHerd.SavedCreature> ReadCreatures(BinaryReader from)
    {
        var count = from.ReadInt32();
        var creatures = new List<Entities.CreatureHerd.SavedCreature>(Math.Clamp(count, 0, 4096));

        for (var i = 0; i < count; i++)
        {
            creatures.Add(new Entities.CreatureHerd.SavedCreature(
                from.ReadString(),
                new Vector3(from.ReadSingle(), from.ReadSingle(), from.ReadSingle()),
                from.ReadSingle(),
                from.ReadInt32(),
                from.ReadBoolean(),
                from.ReadSingle(),
                from.ReadBoolean(),
                from.ReadSingle()));
        }

        return creatures;
    }

    private static bool Opens(BinaryReader from) => Opens(from, out _);

    private static bool Opens(BinaryReader from, out string? why)
    {
        why = null;

        var magic = from.ReadBytes(4);
        if (magic.Length < 4)
        {
            why = "it is too short to be a world";
            return false;
        }

        if (!magic.AsSpan().SequenceEqual(SaveSection.Magic))
        {
            why = "it is not a Driftwood world";
            return false;
        }

        var version = from.ReadInt32();
        if (version <= 0)
        {
            why = $"its version reads {version}";
            return false;
        }

        // ⚠ Newer than this build, which is a different thing from broken and is the one case where
        // saying so matters most: the file is fine and the player needs the newer build back.
        if (version > SaveSection.Version)
        {
            why = $"it was written by a newer build (version {version}, this one reads {SaveSection.Version})";
            return false;
        }

        return true;
    }

    private static BinaryReader Reader(byte[] payload) => new(new MemoryStream(payload));

    private static byte[] Bytes(Action<BinaryWriter> fill)
    {
        using var buffer = new MemoryStream();
        using var into = new BinaryWriter(buffer);
        fill(into);
        into.Flush();
        return buffer.ToArray();
    }

    private static byte[] EditBytes(VoxelWorld world, Palette blocks) => Bytes(into =>
    {
        into.Write(world.Edits.Count);

        foreach (var (cell, id) in world.Edits)
        {
            into.Write(cell.X);
            into.Write(cell.Y);
            into.Write(cell.Z);
            into.Write(blocks.Of(world.Registry[id].Name));
        }
    });

    private static void ReadEdits(BinaryReader from, VoxelWorld world, int[] toBlock)
    {
        var count = from.ReadInt32();

        for (var i = 0; i < count; i++)
        {
            var x = from.ReadInt32();
            var y = from.ReadInt32();
            var z = from.ReadInt32();
            var at = from.ReadInt32();

            // A block this build no longer has leaves the cell as the generator made it, which is
            // the least surprising thing that can happen and is already reported as missing.
            if ((uint)at >= (uint)toBlock.Length || toBlock[at] < 0) continue;
            world.Restore(x, y, z, new BlockId((ushort)toBlock[at]));
        }
    }

    private static void Stack(BinaryWriter into, ItemStack stack, Palette items, ItemRegistry catalogue)
    {
        if (stack.IsEmpty) { into.Write(-1); return; }

        into.Write(items.Of(catalogue[stack.Item].Name));
        into.Write(stack.Count);
        into.Write(stack.Damage);
    }

    private static ItemStack Stack(BinaryReader from, int[] toItem)
    {
        var at = from.ReadInt32();
        if (at < 0) return ItemStack.Empty;

        var count = from.ReadInt32();
        var damage = from.ReadInt32();

        if ((uint)at >= (uint)toItem.Length || toItem[at] < 0) return ItemStack.Empty;
        return new ItemStack(new ItemId((ushort)toItem[at]), count, damage);
    }

    private static byte[] FurnaceBytes(FurnaceBank bank, Palette items, ItemRegistry catalogue) => Bytes(into =>
    {
        into.Write(bank.Count);

        foreach (var (at, furnace) in bank.All)
        {
            into.Write(at.X);
            into.Write(at.Y);
            into.Write(at.Z);
            Stack(into, furnace.Input, items, catalogue);
            Stack(into, furnace.Fuel, items, catalogue);
            Stack(into, furnace.Output, items, catalogue);
            into.Write(furnace.BurnLeft);
            into.Write(furnace.BurnTotal);
            into.Write(furnace.Progress);
        }
    });

    private static void ReadFurnaces(BinaryReader from, FurnaceBank bank, int[] toItem)
    {
        var count = from.ReadInt32();

        for (var i = 0; i < count; i++)
        {
            var furnace = bank.Open(from.ReadInt32(), from.ReadInt32(), from.ReadInt32());
            furnace.Input = Stack(from, toItem);
            furnace.Fuel = Stack(from, toItem);
            furnace.Output = Stack(from, toItem);
            furnace.BurnLeft = from.ReadSingle();
            furnace.BurnTotal = from.ReadSingle();
            furnace.Progress = from.ReadSingle();
        }
    }

    private static byte[] ChestBytes(ChestBank bank, Palette items, ItemRegistry catalogue) => Bytes(into =>
    {
        into.Write(bank.Count);

        foreach (var (at, chest) in bank.All)
        {
            into.Write(at.X);
            into.Write(at.Y);
            into.Write(at.Z);
            into.Write(Chest.Slots);
            foreach (var stack in chest.Contents) Stack(into, stack, items, catalogue);
        }
    });

    private static void ReadChests(BinaryReader from, ChestBank bank, int[] toItem)
    {
        var count = from.ReadInt32();

        for (var i = 0; i < count; i++)
        {
            var chest = bank.Open(from.ReadInt32(), from.ReadInt32(), from.ReadInt32());
            var slots = from.ReadInt32();

            // Written by a build whose chests were a different size. Read what is there and put it
            // where it fits, rather than refusing the whole world over a row of slots.
            for (var slot = 0; slot < slots; slot++)
            {
                var stack = Stack(from, toItem);
                if (slot < Chest.Slots) chest.Contents[slot] = stack;
            }
        }
    }

    private static byte[] PlayerBytes(WorldState state, Palette items) => Bytes(into =>
    {
        into.Write(state.Position.X);
        into.Write(state.Position.Y);
        into.Write(state.Position.Z);
        into.Write(state.Yaw);
        into.Write(state.Pitch);
        into.Write(state.Vitals.Health);
        into.Write(state.Vitals.Breath);
        into.Write(state.Pockets.Selected);

        into.Write(Inventory.Slots);
        foreach (var stack in state.Pockets.All) Stack(into, stack, items, state.Catalogue);

        into.Write(Equipment.Slots);
        for (var slot = 0; slot < Equipment.Slots; slot++)
            Stack(into, state.Worn.At(slot), items, state.Catalogue);

        // ⛔ HUNGER GOES ON THE END, NOT BESIDE HEALTH WHERE IT BELONGS. This section's fields are a
        // flat run with no per-field tag, so a value inserted next to Breath would be read as
        // Selected by every save written before hunger existed — and everything after it would shift
        // by four bytes, which is a pocket full of the wrong items rather than an error.
        // ⛳ The section is length-prefixed and read from its own byte array, so the reader can ask
        // whether there is anything after the worn slots. An older save simply has nothing there.
        into.Write(state.Vitals.Food);
    });

    private static void ReadPlayer(BinaryReader from, WorldState into, int[] toItem)
    {
        into.Position = new Vector3(from.ReadSingle(), from.ReadSingle(), from.ReadSingle());
        into.Yaw = from.ReadSingle();
        into.Pitch = from.ReadSingle();

        var health = from.ReadInt32();
        var breath = from.ReadInt32();
        var selected = from.ReadInt32();

        into.Pockets.Clear();
        var pockets = from.ReadInt32();
        for (var slot = 0; slot < pockets; slot++)
        {
            var stack = Stack(from, toItem);
            if (slot < Inventory.Slots && !stack.IsEmpty) into.Pockets.PutInto(slot, stack);
        }

        into.Pockets.Select(selected);

        into.Worn.Clear();
        var worn = from.ReadInt32();
        for (var slot = 0; slot < worn; slot++)
        {
            var stack = Stack(from, toItem);
            if (slot < Equipment.Slots && !stack.IsEmpty) into.Worn.Restore((EquipSlot)slot, stack);
        }

        // ⛳ Hunger, if this save is new enough to carry any. A world written before it existed has
        // nothing left in the section, and the player opens it fed rather than starving — which is
        // the only honest reading of "this file does not say".
        var food = from.BaseStream.Position < from.BaseStream.Length
            ? from.ReadInt32()
            : PlayerVitals.MaxFood;

        // ⛔⛔ AIR COMES BACK FULL, and the saved number is deliberately not used.
        //
        // Reported by the user, of a world opened after a lungful went from 300 ticks to 900: the
        // bubbles "started at looking over half gone". They had. A breath count is a number of ticks
        // against a maximum the FILE does not record, so a world written when a lungful was 300 reads
        // back as 300 of 900 — a third of a bar, on a player standing on dry land, for no reason they
        // could see and with no way to guess it. There is no reading of that field that survives the
        // constant changing, and there never will be.
        //
        // ⛳ Full is also the right answer on its own terms. Air is fifteen seconds of a resource that
        // refills in under four; it is not a thing worth carrying across a session, and nobody should
        // open a save already drowning. ⚠ The field is still READ, so the section stays the length the
        // writer wrote and anything added after it lands where it should.
        _ = breath;
        into.Vitals.Restore(health, PlayerVitals.MaxBreath, food);
    }
}
