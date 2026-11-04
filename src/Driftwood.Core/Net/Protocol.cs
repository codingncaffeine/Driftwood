using System.IO.Compression;
using System.Numerics;

namespace Driftwood.Core.Net;

/// <summary>What a message is. The first byte of every message.</summary>
public enum Msg : byte
{
    /// <summary>Guest → host: who I am and which build I am.</summary>
    Hello = 1,

    /// <summary>Host → guest: no, and why. The link closes after it.</summary>
    Refuse = 2,

    /// <summary>Host → guest: the world, your number, who else is here.</summary>
    Welcome = 3,

    /// <summary>Host → guests: somebody arrived.</summary>
    PlayerJoined = 4,

    /// <summary>Host → guests: somebody left.</summary>
    PlayerLeft = 5,

    /// <summary>Both ways: where a player is and what they are doing. Unreliable.</summary>
    Pose = 6,

    /// <summary>Both ways: cells that changed. From a guest it is a request; from the host, a fact.</summary>
    Cells = 7,

    /// <summary>Host → guests: the sky's clock.</summary>
    Clock = 8,

    /// <summary>Guest → host: my character as it stands, to be kept under my key.</summary>
    Character = 9,

    /// <summary>Both ways: a line of chat.</summary>
    Chat = 10,

    /// <summary>Guest → host: leaving on purpose, so the host says "left" rather than a socket error.</summary>
    Bye = 11,
}

/// <summary>
/// The wire format: one byte of <see cref="Msg"/>, then the message's fields in order.
/// </summary>
/// <remarks>
/// <para>⛔ <b>Bump <see cref="Version"/> whenever any message changes shape.</b> A guest on a
/// different build is refused at <see cref="Msg.Hello"/> with both versions named; nothing after
/// the hello tries to be compatible, because block and item numbers are only the same between
/// identical builds, and the hello also carries the block registry's fingerprint to prove it.</para>
/// </remarks>
public static class Protocol
{
    public const int Version = 1;

    public static byte[] Build(Msg kind, Action<BinaryWriter> fields)
    {
        using var buffer = new MemoryStream();
        using (var into = new BinaryWriter(buffer))
        {
            into.Write((byte)kind);
            fields(into);
        }
        return buffer.ToArray();
    }

    /// <summary>Opens a message: its kind, and a reader positioned on its first field.</summary>
    public static (Msg Kind, BinaryReader Fields) Open(byte[] message)
    {
        if (message.Length == 0) throw new InvalidDataException("an empty message");
        return ((Msg)message[0], new BinaryReader(new MemoryStream(message, 1, message.Length - 1, false)));
    }

    public static void Write(this BinaryWriter into, Vector3 value)
    {
        into.Write(value.X);
        into.Write(value.Y);
        into.Write(value.Z);
    }

    public static Vector3 ReadVector3(this BinaryReader from) =>
        new(from.ReadSingle(), from.ReadSingle(), from.ReadSingle());

    public static void WriteBlob(this BinaryWriter into, byte[] bytes)
    {
        into.Write(bytes.Length);
        into.Write(bytes);
    }

    public static byte[] ReadBlob(this BinaryReader from)
    {
        var length = from.ReadInt32();
        if (length < 0 || length > TcpTransport.MaxMessage) throw new InvalidDataException("a blob that cannot be right");
        return from.ReadBytes(length);
    }

    /// <summary>Brotli, fastest setting: a world is mostly repeated block numbers.</summary>
    public static byte[] Pack(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest)) brotli.Write(raw);
        return output.ToArray();
    }

    public static byte[] Unpack(byte[] packed)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(new MemoryStream(packed), CompressionMode.Decompress)) brotli.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>Guest → host, the first thing said.</summary>
public sealed record HelloMessage(int ProtocolVersion, string GameVersion, string Registry, string PlayerKey, string Name)
{
    public byte[] Encode() => Protocol.Build(Msg.Hello, w =>
    {
        w.Write(ProtocolVersion);
        w.Write(GameVersion);
        w.Write(Registry);
        w.Write(PlayerKey);
        w.Write(Name);
    });

    public static HelloMessage Decode(BinaryReader r) =>
        new(r.ReadInt32(), r.ReadString(), r.ReadString(), r.ReadString(), r.ReadString());
}

/// <summary>Someone in the session, as everybody else sees them.</summary>
public readonly record struct RosterEntry(int Id, string Name);

/// <summary>Host → guest after a hello it accepts.</summary>
/// <remarks>
/// <para><see cref="World"/> is the host's world as a save file would hold it, packed — read on the
/// guest by the save's own reader, so everything a save keeps arrives by one tested path.
/// <see cref="Character"/> is this guest's own character if the host has kept one for its key, or
/// empty for a first visit.</para>
/// </remarks>
public sealed record WelcomeMessage(
    int YourId, string WorldName, string Seed, int TerrainVersion, float DayTime,
    Vector3 Spawn, byte[] World, byte[] Character, IReadOnlyList<RosterEntry> Roster)
{
    public byte[] Encode() => Protocol.Build(Msg.Welcome, w =>
    {
        w.Write(YourId);
        w.Write(WorldName);
        w.Write(Seed);
        w.Write(TerrainVersion);
        w.Write(DayTime);
        w.Write(Spawn);
        w.WriteBlob(World);
        w.WriteBlob(Character);
        w.Write(Roster.Count);
        foreach (var entry in Roster)
        {
            w.Write(entry.Id);
            w.Write(entry.Name);
        }
    });

    public static WelcomeMessage Decode(BinaryReader r)
    {
        var id = r.ReadInt32();
        var name = r.ReadString();
        var seed = r.ReadString();
        var terrain = r.ReadInt32();
        var day = r.ReadSingle();
        var spawn = r.ReadVector3();
        var world = r.ReadBlob();
        var character = r.ReadBlob();
        var count = r.ReadInt32();
        var roster = new List<RosterEntry>(count);
        for (var i = 0; i < count; i++) roster.Add(new RosterEntry(r.ReadInt32(), r.ReadString()));
        return new WelcomeMessage(id, name, seed, terrain, day, spawn, world, character, roster);
    }
}

/// <summary>Where a player is and what they are doing, many times a second.</summary>
/// <param name="Id">Whose pose. A guest sends 0 and the host fills in the guest's number.</param>
/// <param name="Held">The item name in hand, or "" for an empty hand.</param>
public readonly record struct PoseMessage(
    int Id, Vector3 Position, float Yaw, float Pitch, PoseFlags Flags, string Held)
{
    public byte[] Encode()
    {
        var pose = this;
        return Protocol.Build(Msg.Pose, w =>
        {
            w.Write(pose.Id);
            w.Write(pose.Position);
            w.Write(pose.Yaw);
            w.Write(pose.Pitch);
            w.Write((byte)pose.Flags);
            w.Write(pose.Held);
        });
    }

    public static PoseMessage Decode(BinaryReader r) =>
        new(r.ReadInt32(), r.ReadVector3(), r.ReadSingle(), r.ReadSingle(), (PoseFlags)r.ReadByte(), r.ReadString());
}

[Flags]
public enum PoseFlags : byte
{
    None = 0,
    Sneaking = 1,
    Swinging = 2,
    Sprinting = 4,
    Swimming = 8,
}

/// <summary>A run of changed cells, block numbers as this build numbers them (the hello proved both builds agree).</summary>
public sealed record CellsMessage(IReadOnlyList<(int X, int Y, int Z, ushort Block)> Cells)
{
    public byte[] Encode() => Protocol.Build(Msg.Cells, w =>
    {
        w.Write(Cells.Count);
        foreach (var (x, y, z, block) in Cells)
        {
            w.Write(x);
            w.Write((short)y);
            w.Write(z);
            w.Write(block);
        }
    });

    public static CellsMessage Decode(BinaryReader r)
    {
        var count = r.ReadInt32();
        if (count < 0 || count > 1 << 20) throw new InvalidDataException("a cell count that cannot be right");
        var cells = new List<(int, int, int, ushort)>(count);
        for (var i = 0; i < count; i++) cells.Add((r.ReadInt32(), r.ReadInt16(), r.ReadInt32(), r.ReadUInt16()));
        return new CellsMessage(cells);
    }
}

public static class SimpleMessages
{
    public static byte[] Refuse(string why) => Protocol.Build(Msg.Refuse, w => w.Write(why));

    public static byte[] Joined(RosterEntry who) => Protocol.Build(Msg.PlayerJoined, w =>
    {
        w.Write(who.Id);
        w.Write(who.Name);
    });

    public static byte[] Left(int id) => Protocol.Build(Msg.PlayerLeft, w => w.Write(id));

    public static byte[] Clock(float dayTime) => Protocol.Build(Msg.Clock, w => w.Write(dayTime));

    public static byte[] Chat(int from, string text) => Protocol.Build(Msg.Chat, w =>
    {
        w.Write(from);
        w.Write(text.Length > 256 ? text[..256] : text);
    });

    public static byte[] Bye() => Protocol.Build(Msg.Bye, _ => { });

    public static byte[] Character(byte[] record) => Protocol.Build(Msg.Character, w => w.WriteBlob(record));
}
