using System.Numerics;

namespace Driftwood.Core.Magic;

public enum CompanionKind : byte { Bones, Zombie, SpiritWolf, EarthElemental }
public enum CompanionRole : byte { Striker, Guard, Pursuer, Defender }
public enum CompanionCommand : byte { Attack, Guard, Follow, Stop, GoAway }

public readonly record struct CompanionDefinition(
    CompanionKind Kind, SpellId Spell, string Name, string Model, CompanionRole Role,
    float Height, float Speed, float Reach, int[] Health, int[] Damage, int[] Toughness);

public sealed class Companion
{
    public required string InstanceId { get; init; }
    public required string OwnerPlayerId { get; init; }
    public required CompanionKind Kind { get; init; }
    public required int Rank { get; set; }
    public required int Health { get; set; }
    public required int MaxHealth { get; set; }
    public required Vector3 Position { get; set; }
    public float Yaw { get; set; }
    public CompanionCommand Command { get; set; } = CompanionCommand.Follow;
    public Vector3 GuardPosition { get; set; }
    public string TargetId { get; set; } = "";
    public bool Alive => Health > 0;
}

public readonly record struct CompanionEvent(
    string OwnerPlayerId, string InstanceId, CompanionKind Kind, string Event, CompanionCommand Command);

/// <summary>One authoritative commanded companion per stable player identity.</summary>
public sealed class CompanionService
{
    private static readonly CompanionDefinition[] AllDefinitions =
    [
        new(CompanionKind.Bones, SpellId.SummonBones, "Bones", "summoned_skeleton", CompanionRole.Striker,
            1.85f, 3.2f, 2.1f, [20,27,35,44], [4,6,8,11], [0,0,1,1]),
        new(CompanionKind.Zombie, SpellId.AnimateZombie, "Hollow", "summoned_zombie", CompanionRole.Guard,
            1.9f, 2.2f, 2.2f, [28,38,50,64], [3,5,7,9], [1,2,3,4]),
        new(CompanionKind.SpiritWolf, SpellId.SpiritWolf, "Spirit Wolf", "spirit_wolf", CompanionRole.Pursuer,
            0.9f, 4.1f, 1.8f, [22,30,39,49], [4,6,9,12], [0,1,1,2]),
        new(CompanionKind.EarthElemental, SpellId.EarthElemental, "Earth Elemental", "earth_elemental", CompanionRole.Defender,
            0.9f, 2.0f, 1.65f, [32,44,58,74], [3,5,7,10], [2,3,5,7]),
    ];

    private readonly Dictionary<string, Companion> _byOwner = new(StringComparer.Ordinal);
    private readonly HashSet<string> _receipts = new(StringComparer.Ordinal);
    private readonly List<CompanionEvent> _events = [];
    private long _nextId = 1;

    public static IReadOnlyList<CompanionDefinition> Definitions => AllDefinitions;
    public IReadOnlyCollection<Companion> All => _byOwner.Values;
    public bool Dirty { get; private set; }

    public static CompanionDefinition Definition(CompanionKind kind) => AllDefinitions[(int)kind];
    public static CompanionDefinition Definition(SpellId spell) =>
        AllDefinitions.First(one => one.Spell == spell);

    public Companion? For(string ownerPlayerId) => _byOwner.GetValueOrDefault(ownerPlayerId);

    public Companion? Summon(string receipt, string ownerPlayerId, SpellId spell, int rank, Vector3 at)
    {
        if (string.IsNullOrWhiteSpace(receipt) || string.IsNullOrWhiteSpace(ownerPlayerId)
            || _receipts.Contains(receipt) || !AllDefinitions.Any(one => one.Spell == spell)
            || !Finite(at)) return null;
        var definition = Definition(spell);
        rank = Math.Clamp(rank, 1, 4);

        // Fully construct and validate first. Only then does replacement touch the old pet.
        var summoned = new Companion
        {
            InstanceId = $"pet-{_nextId++:x}",
            OwnerPlayerId = ownerPlayerId,
            Kind = definition.Kind,
            Rank = rank,
            Health = definition.Health[rank - 1],
            MaxHealth = definition.Health[rank - 1],
            Position = at,
            GuardPosition = at,
        };
        _receipts.Add(receipt);
        if (_byOwner.TryGetValue(ownerPlayerId, out var old))
            _events.Add(new(ownerPlayerId, old.InstanceId, old.Kind, "replaced", CompanionCommand.GoAway));
        _byOwner[ownerPlayerId] = summoned;
        _events.Add(new(ownerPlayerId, summoned.InstanceId, summoned.Kind, "summoned", summoned.Command));
        Dirty = true;
        return summoned;
    }

    public bool Command(string ownerPlayerId, CompanionCommand command, string targetId = "")
    {
        if (!_byOwner.TryGetValue(ownerPlayerId, out var pet) || !pet.Alive) return false;
        if (command == CompanionCommand.Attack && string.IsNullOrWhiteSpace(targetId)) return false;
        if (command == CompanionCommand.GoAway)
        {
            _byOwner.Remove(ownerPlayerId);
            _events.Add(new(ownerPlayerId, pet.InstanceId, pet.Kind, "dismissed", command));
            Dirty = true;
            return true;
        }
        pet.Command = command;
        pet.TargetId = command == CompanionCommand.Attack ? targetId : "";
        if (command is CompanionCommand.Guard or CompanionCommand.Stop) pet.GuardPosition = pet.Position;
        _events.Add(new(ownerPlayerId, pet.InstanceId, pet.Kind, "command", command));
        Dirty = true;
        return true;
    }

    /// <summary>
    /// Gives a Guard or Following companion a temporary hostile without changing the command the
    /// player chose.  Attack remains the explicit, persistent order; defensive targets disappear
    /// as soon as the caller can no longer validate them.
    /// </summary>
    public bool DefendAgainst(string ownerPlayerId, string targetId)
    {
        if (!_byOwner.TryGetValue(ownerPlayerId, out var pet) || !pet.Alive
            || pet.Command is not (CompanionCommand.Guard or CompanionCommand.Follow)) return false;
        targetId ??= "";
        if (string.Equals(pet.TargetId, targetId, StringComparison.Ordinal)) return true;
        pet.TargetId = targetId;
        Dirty = true;
        return true;
    }

    public int Hurt(string ownerPlayerId, int amount, string sourcePlayerId = "")
    {
        if (!_byOwner.TryGetValue(ownerPlayerId, out var pet) || amount <= 0
            || string.Equals(ownerPlayerId, sourcePlayerId, StringComparison.Ordinal)) return 0;
        var definition = Definition(pet.Kind);
        var toughness = definition.Toughness[pet.Rank - 1];
        var settled = Math.Max(1, amount - toughness);
        var before = pet.Health;
        pet.Health = Math.Max(0, pet.Health - settled);
        settled = before - pet.Health;
        if (pet.Health == 0)
        {
            _byOwner.Remove(ownerPlayerId);
            _events.Add(new(ownerPlayerId, pet.InstanceId, pet.Kind, "died", pet.Command));
        }
        else _events.Add(new(ownerPlayerId, pet.InstanceId, pet.Kind, "hurt", pet.Command));
        Dirty = true;
        return settled;
    }

    public void RefreshRank(string ownerPlayerId, int rank)
    {
        if (!_byOwner.TryGetValue(ownerPlayerId, out var pet)) return;
        rank = Math.Clamp(rank, 1, 4);
        if (pet.Rank == rank) return;
        var oldMaximum = Math.Max(1, pet.MaxHealth);
        var definition = Definition(pet.Kind);
        pet.Rank = rank;
        pet.MaxHealth = definition.Health[rank - 1];
        pet.Health = Math.Clamp((int)MathF.Ceiling(pet.Health / (float)oldMaximum * pet.MaxHealth), 1, pet.MaxHealth);
        Dirty = true;
    }

    public void Update(float dt, Func<string, Vector3?> ownerPosition, Func<string, Vector3?> targetPosition)
    {
        if (dt <= 0f) return;
        foreach (var pet in _byOwner.Values)
        {
            Vector3? destination = pet.Command switch
            {
                CompanionCommand.Attack => targetPosition(pet.TargetId),
                CompanionCommand.Guard => targetPosition(pet.TargetId) ?? pet.GuardPosition,
                CompanionCommand.Follow => targetPosition(pet.TargetId) ?? ownerPosition(pet.OwnerPlayerId),
                _ => null,
            };
            if (destination is not { } goal) continue;
            var delta = goal - pet.Position;
            delta.Y = 0f;
            var distance = delta.Length();
            var definition = Definition(pet.Kind);
            var pursuing = pet.TargetId.Length > 0;
            var stop = pet.Command == CompanionCommand.Follow && !pursuing ? 1.8f : definition.Reach;
            if (distance <= stop || distance < 0.001f) continue;
            if (distance > 32f && pet.Command == CompanionCommand.Follow)
            {
                pet.Position = goal + new Vector3(-1f, 0f, 0f);
            }
            else
            {
                var direction = delta / distance;
                pet.Position += direction * MathF.Min(distance - stop, definition.Speed * dt);
                pet.Yaw = float.RadiansToDegrees(MathF.Atan2(direction.Z, direction.X));
            }
            Dirty = true;
        }
    }

    public List<CompanionEvent> TakeEvents()
    {
        var taken = new List<CompanionEvent>(_events);
        _events.Clear();
        return taken;
    }

    public void Write(BinaryWriter into)
    {
        into.Write(1);
        into.Write(_nextId);
        into.Write(_byOwner.Count);
        foreach (var pet in _byOwner.Values.OrderBy(one => one.OwnerPlayerId, StringComparer.Ordinal))
        {
            into.Write(pet.InstanceId); into.Write(pet.OwnerPlayerId); into.Write((byte)pet.Kind);
            into.Write(pet.Rank); into.Write(pet.Health);
            into.Write(pet.Position.X); into.Write(pet.Position.Y); into.Write(pet.Position.Z);
            into.Write(pet.Yaw); into.Write((byte)pet.Command);
            into.Write(pet.GuardPosition.X); into.Write(pet.GuardPosition.Y); into.Write(pet.GuardPosition.Z);
            into.Write(pet.TargetId);
        }
        into.Write(_receipts.Count);
        foreach (var receipt in _receipts.Order(StringComparer.Ordinal)) into.Write(receipt);
    }

    public string? Read(BinaryReader from)
    {
        try
        {
            if (from.ReadInt32() != 1) return "unknown companion record version";
            _nextId = Math.Max(1, from.ReadInt64());
            _byOwner.Clear();
            var count = from.ReadInt32();
            if (count is < 0 or > 1024) return $"companion record says it has {count} pets";
            for (var i = 0; i < count; i++)
            {
                var instance = from.ReadString(); var owner = from.ReadString();
                var kind = (CompanionKind)from.ReadByte(); var rank = Math.Clamp(from.ReadInt32(), 1, 4);
                var health = from.ReadInt32();
                var position = new Vector3(from.ReadSingle(), from.ReadSingle(), from.ReadSingle());
                var yaw = from.ReadSingle(); var command = (CompanionCommand)from.ReadByte();
                var guard = new Vector3(from.ReadSingle(), from.ReadSingle(), from.ReadSingle());
                var target = from.ReadString();
                if ((byte)kind >= AllDefinitions.Length || !Finite(position)) continue;
                var maximum = Definition(kind).Health[rank - 1];
                _byOwner[owner] = new Companion
                {
                    InstanceId = instance, OwnerPlayerId = owner, Kind = kind, Rank = rank,
                    Health = Math.Clamp(health, 1, maximum), MaxHealth = maximum, Position = position,
                    Yaw = yaw, Command = Enum.IsDefined(command) ? command : CompanionCommand.Follow,
                    GuardPosition = Finite(guard) ? guard : position, TargetId = target,
                };
            }
            _receipts.Clear();
            var receipts = from.ReadInt32();
            if (receipts is < 0 or > 8_192) return $"companion record says it has {receipts} receipts";
            for (var i = 0; i < receipts; i++) _receipts.Add(from.ReadString());
            Dirty = false;
            return null;
        }
        catch (Exception fault) { return $"companion record: {fault.Message}"; }
    }

    public void Settled() => Dirty = false;
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
