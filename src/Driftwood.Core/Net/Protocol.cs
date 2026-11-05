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

    /// <summary>Guest → host (v3): the terrain sums of chunks the guest's generator just made.</summary>
    ChunkSums = 12,

    /// <summary>Host → guest (v3): a chunk as the host holds it, because the guest's terrain differed.</summary>
    ChunkBlocks = 13,

    /// <summary>Host → guest (v3, N5): the creatures round that guest, as they stand. Unreliable, ten a second.</summary>
    Creatures = 14,

    /// <summary>Guest → host (v3, N5): I struck this creature, this hard, from here.</summary>
    StrikeCreature = 15,

    /// <summary>Host → guest (v3, N5): a creature landed a blow on you.</summary>
    Hurt = 16,

    /// <summary>Host → guest (v4, N5): the stacks on the ground round that guest. Unreliable, ten a second.</summary>
    Drops = 17,

    /// <summary>Guest → host (v4, N5): I walked into this stack; may I have it?</summary>
    PickUp = 18,

    /// <summary>Host → guest (v4, N5): this is yours now — into your pockets.</summary>
    Given = 19,

    /// <summary>Guest → host (v4, N5): I threw this out (a drop, a spill, what a broken block left).</summary>
    DropItem = 20,

    /// <summary>Host → guest (v4, N5): a creature you struck has died — your share of the credit.</summary>
    Kill = 21,

    /// <summary>Guest → host (v4, N5): may I open the chest (furnace, stand) at this cell?</summary>
    ContainerOpen = 22,

    /// <summary>Host → guest (v4, N5): yes and here is what is in it, or no and why.</summary>
    ContainerGrant = 23,

    /// <summary>Guest → host (v4, N5): the container I hold, as it stands now (and whether I have shut it).</summary>
    ContainerWrite = 24,

    /// <summary>Host → guest (v4, N5): the container you hold is gone (broken, blown up); shut its screen.</summary>
    ContainerShut = 25,

    /// <summary>Both ways (v5): a sign's four lines. From a guest it is a request; from the host, a fact.</summary>
    Sign = 26,
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
    public const int Version = 5;

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

    /// <summary>A stack by this build's item numbers (the hello proved both builds agree).</summary>
    public static void Write(this BinaryWriter into, Items.ItemStack stack)
    {
        into.Write(stack.IsEmpty ? (ushort)0 : stack.Item.Value);
        into.Write(stack.IsEmpty ? 0 : stack.Count);
        into.Write(stack.IsEmpty ? 0 : stack.Damage);
    }

    public static Items.ItemStack ReadStack(this BinaryReader from)
    {
        var item = from.ReadUInt16();
        var count = from.ReadInt32();
        var damage = from.ReadInt32();
        if (count < 0 || count > 1 << 16) throw new InvalidDataException("a stack count that cannot be right");
        return item == 0 || count == 0 ? Items.ItemStack.Empty : new Items.ItemStack(new Items.ItemId(item), count, damage);
    }

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
/// <param name="Code">The join code the guest was given (v2), or empty.</param>
public sealed record HelloMessage(
    int ProtocolVersion, string GameVersion, string Registry, string PlayerKey, string Name, string Code = "")
{
    public byte[] Encode() => Protocol.Build(Msg.Hello, w =>
    {
        w.Write(ProtocolVersion);
        w.Write(GameVersion);
        w.Write(Registry);
        w.Write(PlayerKey);
        w.Write(Name);
        w.Write(Code);
    });

    /// <remarks>⚠ A v1 hello ends after the name: it is read whole, and then refused for its version
    /// with a reason, rather than failing to read.</remarks>
    public static HelloMessage Decode(BinaryReader r)
    {
        var hello = new HelloMessage(r.ReadInt32(), r.ReadString(), r.ReadString(), r.ReadString(), r.ReadString());
        return r.BaseStream.Position < r.BaseStream.Length ? hello with { Code = r.ReadString() } : hello;
    }
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

/// <summary>Guest → host: what this guest's generator made, chunk by chunk, as 64-bit sums (D3).</summary>
public sealed record ChunkSumsMessage(IReadOnlyList<(World.ChunkPos Pos, ulong Sum)> Sums)
{
    /// <summary>Sums per message: 20 bytes each, so one message stays well inside a single Epic packet's few.</summary>
    public const int MaxPerMessage = 256;

    public byte[] Encode() => Protocol.Build(Msg.ChunkSums, w =>
    {
        w.Write(Sums.Count);
        foreach (var (pos, sum) in Sums)
        {
            w.Write(pos.X);
            w.Write(pos.Y);
            w.Write(pos.Z);
            w.Write(sum);
        }
    });

    public static ChunkSumsMessage Decode(BinaryReader r)
    {
        var count = r.ReadInt32();
        if (count < 0 || count > 4096) throw new InvalidDataException("a sum count that cannot be right");
        var sums = new List<(World.ChunkPos, ulong)>(count);
        for (var i = 0; i < count; i++) sums.Add((new World.ChunkPos(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()), r.ReadUInt64()));
        return new ChunkSumsMessage(sums);
    }
}

/// <summary>Host → guest: one chunk's blocks as the host holds them, packed.</summary>
public sealed record ChunkBlocksMessage(World.ChunkPos Pos, ushort[] Blocks)
{
    public byte[] Encode() => Protocol.Build(Msg.ChunkBlocks, w =>
    {
        w.Write(Pos.X);
        w.Write(Pos.Y);
        w.Write(Pos.Z);
        w.WriteBlob(Protocol.Pack(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Blocks.AsSpan()).ToArray()));
    });

    public static ChunkBlocksMessage Decode(BinaryReader r)
    {
        var pos = new World.ChunkPos(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        var raw = Protocol.Unpack(r.ReadBlob());
        if (raw.Length != World.Chunk.Volume * sizeof(ushort)) throw new InvalidDataException("a chunk of the wrong size");
        return new ChunkBlocksMessage(pos, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(raw).ToArray());
    }
}

/// <summary>One creature as a guest is to draw it: the host's numbering, never the host's object.</summary>
/// <param name="Id">The host's number for this creature for as long as it lives; never reused within a session.</param>
public readonly record struct CreatureState(
    int Id, string Kind, byte Bulk, Vector3 Position, float Yaw, short Health,
    float HurtFor, float DyingFor, float Grown, float FuseFor);

/// <summary>Host → guest: every creature within <see cref="Radius"/> of the guest. A creature missing from it is gone.</summary>
/// <param name="Sequence">Counts up per guest; an older set arriving late (unreliable) is thrown away.</param>
public sealed record CreaturesMessage(uint Sequence, IReadOnlyList<CreatureState> Creatures)
{
    /// <summary>Blocks round a guest whose creatures it is told about: about its fog at a normal view distance.</summary>
    public const float Radius = 96f;

    /// <summary>At most this many per message, nearest first, so a pen of a hundred sheep cannot make a message Epic drops.</summary>
    public const int MaxPerMessage = 160;

    public byte[] Encode() => Protocol.Build(Msg.Creatures, w =>
    {
        w.Write(Sequence);
        w.Write(Creatures.Count);
        foreach (var c in Creatures)
        {
            w.Write(c.Id);
            w.Write(c.Kind);
            w.Write(c.Bulk);
            w.Write(c.Position);
            w.Write((Half)c.Yaw);
            w.Write(c.Health);
            w.Write(Fraction(c.HurtFor, Entities.CreatureHerd.HurtSeconds));
            w.Write(Fraction(c.DyingFor, Entities.CreatureHerd.DyingSeconds));
            w.Write(Fraction(c.Grown, 1f));
            w.Write(Fraction(c.FuseFor, Entities.CreatureHerd.FuseSeconds));
        }
    });

    public static CreaturesMessage Decode(BinaryReader r)
    {
        var sequence = r.ReadUInt32();
        var count = r.ReadInt32();
        if (count < 0 || count > 4096) throw new InvalidDataException("a creature count that cannot be right");
        var list = new List<CreatureState>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(new CreatureState(
                r.ReadInt32(), r.ReadString(), r.ReadByte(), r.ReadVector3(), (float)r.ReadHalf(), r.ReadInt16(),
                Whole(r.ReadByte(), Entities.CreatureHerd.HurtSeconds), Whole(r.ReadByte(), Entities.CreatureHerd.DyingSeconds),
                Whole(r.ReadByte(), 1f), Whole(r.ReadByte(), Entities.CreatureHerd.FuseSeconds)));
        }
        return new CreaturesMessage(sequence, list);
    }

    private static byte Fraction(float value, float of) => (byte)Math.Clamp((int)MathF.Round(value / of * 255f), 0, 255);

    private static float Whole(byte value, float of) => value / 255f * of;
}

/// <summary>Guest → host: a blow on one of the host's creatures, already worked out by the guest's own stats.</summary>
/// <param name="From">Where the blow came from, for the knock-back.</param>
/// <param name="Ranged">An arrow's or a spell's, reaching as far as the guest can see; a hand's reaches a hand's length.</param>
public readonly record struct StrikeMessage(int Creature, int HalfHearts, Vector3 From, bool Ranged = false)
{
    public byte[] Encode()
    {
        var strike = this;
        return Protocol.Build(Msg.StrikeCreature, w =>
        {
            w.Write(strike.Creature);
            w.Write(strike.HalfHearts);
            w.Write(strike.From);
            w.Write(strike.Ranged);
        });
    }

    public static StrikeMessage Decode(BinaryReader r) => new(r.ReadInt32(), r.ReadInt32(), r.ReadVector3(), r.ReadBoolean());
}

/// <summary>Host → guest: a creature's blow landed on this guest.</summary>
public readonly record struct HurtMessage(string Kind, int HalfHearts, Vector3 From)
{
    /// <summary>The kind a blast's hurt carries: no creature, no pet in the way, the blast's rumble.</summary>
    public const string Explosion = "~explosion";

    private const string ShotPrefix = "~shot:";

    /// <summary>A creature's shot that struck a guest, or null for one that hurts nobody by itself (a flask splashes).</summary>
    public static HurtMessage? ForShot(Projectiles.ProjectileKind kind, Entities.ShotTag tag, int damage) => kind switch
    {
        Projectiles.ProjectileKind.Arrow or Projectiles.ProjectileKind.Ember or Projectiles.ProjectileKind.SkullBolt
            => new HurtMessage($"{ShotPrefix}{(int)kind}:{(int)tag}", damage, default),
        Projectiles.ProjectileKind.WailBolt => new HurtMessage($"{ShotPrefix}{(int)kind}:0", 6, default),
        _ => null,
    };

    /// <summary>The shot a hurt's kind names, or null for a blow or a blast.</summary>
    public static (Projectiles.ProjectileKind Kind, Entities.ShotTag Tag)? ShotOf(string kind)
    {
        if (!kind.StartsWith(ShotPrefix, StringComparison.Ordinal)) return null;
        var parts = kind[ShotPrefix.Length..].Split(':');
        return parts.Length == 2 && int.TryParse(parts[0], out var k) && int.TryParse(parts[1], out var t)
            ? ((Projectiles.ProjectileKind)k, (Entities.ShotTag)t)
            : null;
    }

    public byte[] Encode()
    {
        var hurt = this;
        return Protocol.Build(Msg.Hurt, w =>
        {
            w.Write(hurt.Kind);
            w.Write(hurt.HalfHearts);
            w.Write(hurt.From);
        });
    }

    public static HurtMessage Decode(BinaryReader r) => new(r.ReadString(), r.ReadInt32(), r.ReadVector3());
}

/// <summary>One stack on the host's ground, as a guest is to draw it and ask for it.</summary>
/// <param name="Ready">Past its pickup delay on the host: asking before then is only refused.</param>
public readonly record struct DropState(uint Id, Items.ItemStack Stack, Vector3 Position, bool Ready = true);

/// <summary>Host → guest: every stack on the ground within <see cref="Radius"/> of the guest. One missing from it is gone.</summary>
public sealed record DropsMessage(uint Sequence, IReadOnlyList<DropState> Drops)
{
    public const float Radius = 48f;

    /// <summary>Nearest first, at most this many: 27 bytes each, a few Epic packets at worst.</summary>
    public const int MaxPerMessage = 192;

    public byte[] Encode() => Protocol.Build(Msg.Drops, w =>
    {
        w.Write(Sequence);
        w.Write(Drops.Count);
        foreach (var d in Drops)
        {
            w.Write(d.Id);
            w.Write(d.Stack);
            w.Write(d.Position);
            w.Write(d.Ready);
        }
    });

    public static DropsMessage Decode(BinaryReader r)
    {
        var sequence = r.ReadUInt32();
        var count = r.ReadInt32();
        if (count < 0 || count > 4096) throw new InvalidDataException("a drop count that cannot be right");
        var list = new List<DropState>(count);
        for (var i = 0; i < count; i++) list.Add(new DropState(r.ReadUInt32(), r.ReadStack(), r.ReadVector3(), r.ReadBoolean()));
        return new DropsMessage(sequence, list);
    }
}

/// <summary>Guest → host: a stack thrown out on the guest's machine, to lie on the host's ground.</summary>
public readonly record struct DropItemMessage(Items.ItemStack Stack, Vector3 Position, Vector3 Velocity)
{
    public byte[] Encode()
    {
        var drop = this;
        return Protocol.Build(Msg.DropItem, w =>
        {
            w.Write(drop.Stack);
            w.Write(drop.Position);
            w.Write(drop.Velocity);
        });
    }

    public static DropItemMessage Decode(BinaryReader r) => new(r.ReadStack(), r.ReadVector3(), r.ReadVector3());
}

/// <summary>Host → guest: a creature this guest struck has died. The guest's character takes the credit.</summary>
/// <param name="RuntimeId">The host's id for the creature, so a reward is settled once per kill.</param>
public readonly record struct KillMessage(string RuntimeId, string Kind, bool Grown, bool Hostile)
{
    public byte[] Encode()
    {
        var kill = this;
        return Protocol.Build(Msg.Kill, w =>
        {
            w.Write(kill.RuntimeId);
            w.Write(kill.Kind);
            w.Write(kill.Grown);
            w.Write(kill.Hostile);
        });
    }

    public static KillMessage Decode(BinaryReader r) => new(r.ReadString(), r.ReadString(), r.ReadBoolean(), r.ReadBoolean());
}

/// <summary>Which sort of container a cell holds, as the wire names it.</summary>
public enum ContainerKind : byte
{
    Chest = 1,
    Furnace = 2,
    BrewingStand = 3,
}

/// <summary>One container's cell and sort: what every container message starts with.</summary>
public readonly record struct ContainerAt(ContainerKind Kind, int X, int Y, int Z)
{
    public (int X, int Y, int Z) Cell => (X, Y, Z);

    public void Write(BinaryWriter w)
    {
        w.Write((byte)Kind);
        w.Write(X);
        w.Write(Y);
        w.Write(Z);
    }

    public static ContainerAt Read(BinaryReader r)
    {
        var kind = (ContainerKind)r.ReadByte();
        if (kind is not (ContainerKind.Chest or ContainerKind.Furnace or ContainerKind.BrewingStand))
            throw new InvalidDataException("a container of no known sort");
        return new ContainerAt(kind, r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
    }
}

public static class ContainerMessages
{
    public static byte[] Open(ContainerAt at) => Protocol.Build(Msg.ContainerOpen, at.Write);

    /// <param name="contents">The container's state (<see cref="ContainerCodec"/>), or null with a reason for no.</param>
    public static byte[] Grant(ContainerAt at, byte[]? contents, string refusal = "") => Protocol.Build(Msg.ContainerGrant, w =>
    {
        at.Write(w);
        w.Write(contents is not null);
        if (contents is not null) w.WriteBlob(contents);
        else w.Write(refusal);
    });

    public static byte[] Write(ContainerAt at, byte[] contents, bool closing) => Protocol.Build(Msg.ContainerWrite, w =>
    {
        at.Write(w);
        w.WriteBlob(contents);
        w.Write(closing);
    });

    public static byte[] Shut(ContainerAt at) => Protocol.Build(Msg.ContainerShut, at.Write);

    public static byte[] Given(Items.ItemStack stack) => Protocol.Build(Msg.Given, w => w.Write(stack));

    public static byte[] PickUp(uint id) => Protocol.Build(Msg.PickUp, w => w.Write(id));
}

/// <summary>A guest's answer to its <see cref="Msg.ContainerOpen"/>.</summary>
public sealed record ContainerGrant(ContainerAt At, byte[]? Contents, string Refusal);

/// <summary>A sign's words at a cell (v5).</summary>
public sealed record SignMessage(int X, int Y, int Z, IReadOnlyList<string> Lines)
{
    public byte[] Encode() => Protocol.Build(Msg.Sign, w =>
    {
        w.Write(X);
        w.Write(Y);
        w.Write(Z);
        w.Write((byte)Lines.Count);
        foreach (var line in Lines) w.Write(line);
    });

    public static SignMessage Decode(BinaryReader r)
    {
        int x = r.ReadInt32(), y = r.ReadInt32(), z = r.ReadInt32();
        var count = r.ReadByte();
        if (count > 8) throw new InvalidDataException("a sign with more lines than a sign has");
        var lines = new string[count];
        for (var i = 0; i < count; i++) lines[i] = r.ReadString();
        return new SignMessage(x, y, z, CoopRules.CleanSign(lines));
    }
}
