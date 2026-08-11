namespace Driftwood.Core.Magic;

public enum XpSource : byte
{
    Creature,
    Discovery,
    Encounter,
    Survival,
}

public readonly record struct AttributeSet(int Might, int Finesse, int Insight, int Resolve)
{
    public static AttributeSet Starting => new(5, 5, 5, 5);
}

/// <summary>The one snapshot character UI and combat both consume.</summary>
public readonly record struct CharacterStatistics(
    int Level,
    int Rank,
    int MaximumHealth,
    int MaximumFocus,
    int Armour,
    int WeaponDamage,
    float SpellPotency,
    float CriticalChance,
    float Haste);

public readonly record struct LevelResult(
    bool Accepted, int ExperienceGranted, int LevelsGained, int OldRank, int NewRank, string Reason)
{
    public bool RankBoundary => NewRank > OldRank;
}

public readonly record struct PurchaseResult(bool Accepted, long Paid, string Reason);

/// <summary>
/// Per-player level, wallet, spellbook and casting resources. Stable names cross every save edge;
/// rank is always derived and is intentionally absent from serialized state.
/// </summary>
public sealed class CharacterProgression
{
    public const int MaximumLevel = 20;
    public const int PreparedCapacity = 8;
    public const int ReceiptCapacity = 8_192;
    public const int CurveVersion = 1;

    // XP needed to move from this level to the next. Index 0 is unused; level 20 has no next row.
    private static readonly int[] Needs =
        [0, 100, 145, 200, 265, 340, 425, 520, 625, 740, 865, 1_000, 1_145, 1_300,
         1_465, 1_640, 1_825, 2_020, 2_225, 2_440, 0];

    private readonly HashSet<string> _learned = new(StringComparer.Ordinal);
    private readonly string?[] _prepared = new string?[PreparedCapacity];
    private readonly Dictionary<string, string?[]> _loadouts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _cooldowns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _receipts = new(StringComparer.Ordinal);
    private readonly Queue<string> _receiptOrder = new();
    private float _focusCarry;

    public CharacterProgression(string playerId = "local")
    {
        PlayerId = NormalizePlayerId(playerId);
        Focus = Statistics.MaximumFocus;
    }

    public string PlayerId { get; private set; }
    public int Level { get; private set; } = 1;
    public int Experience { get; private set; }
    public int ExperienceNeeded => Level >= MaximumLevel ? 0 : Needs[Level];
    public int Rank => RankFor(Level);
    public long Coins { get; private set; }
    public AttributeSet Attributes { get; private set; } = AttributeSet.Starting;
    public int AttributePoints { get; private set; }
    public int Focus { get; private set; }
    public string ActiveCompanionId { get; private set; } = "";
    public System.Numerics.Vector3? Bind { get; private set; }
    public bool Dirty { get; private set; }
    public IReadOnlySet<string> Learned => _learned;
    public IReadOnlyList<string?> Prepared => _prepared;
    public IReadOnlyCollection<string> LoadoutNames => _loadouts.Keys;
    public IReadOnlyDictionary<string, float> Cooldowns => _cooldowns;

    public bool HasLoadout(string name) => _loadouts.ContainsKey(name);

    public CharacterStatistics Statistics
    {
        get
        {
            var a = Attributes;
            return new CharacterStatistics(
                Level,
                Rank,
                20 + (a.Resolve - 5) * 2 + (Level - 1),
                80 + a.Insight * 4 + Level * 2,
                Math.Max(0, (a.Resolve - 4) / 2),
                1 + Math.Max(0, (a.Might - 4) / 2),
                1f + (a.Insight - 5) * 0.035f + (Level - 1) * 0.0125f,
                Math.Clamp(0.05f + (a.Finesse - 5) * 0.006f, 0f, 0.35f),
                Math.Clamp((a.Finesse - 5) * 0.01f + (a.Insight - 5) * 0.005f, 0f, 0.35f));
        }
    }

    public static int RankFor(int level) => Math.Min(4, 1 + (Math.Clamp(level, 1, MaximumLevel) - 1) / 5);

    public static string RankName(int rank) => rank switch { 1 => "Rank I", 2 => "Rank II", 3 => "Rank III", _ => "Rank IV" };

    public static long TotalExperienceForLevel(int level)
    {
        var total = 0L;
        for (var at = 1; at < Math.Clamp(level, 1, MaximumLevel); at++) total += Needs[at];
        return total;
    }

    public LevelResult AwardExperience(string eventId, int amount, XpSource source, float contribution = 1f)
    {
        if (string.IsNullOrWhiteSpace(eventId)) return new(false, 0, 0, Rank, Rank, "missing event identity");
        if (amount <= 0 || contribution <= 0f) return new(false, 0, 0, Rank, Rank, "trivial events award no XP");
        if (_receipts.Contains("xp:" + eventId)) return new(false, 0, 0, Rank, Rank, "already settled");

        var sourceCap = source switch
        {
            XpSource.Creature => 600,
            XpSource.Discovery => 400,
            XpSource.Encounter => 1_200,
            XpSource.Survival => 250,
            _ => 0,
        };
        var granted = Math.Clamp((int)MathF.Round(amount * Math.Clamp(contribution, 0f, 1f)), 0, sourceCap);
        Remember("xp:" + eventId);
        if (granted == 0) return new(true, 0, 0, Rank, Rank, "trivial contribution");

        var oldRank = Rank;
        var oldLevel = Level;
        if (Level < MaximumLevel)
        {
            Experience = checked(Experience + granted);
            while (Level < MaximumLevel && Experience >= Needs[Level])
            {
                Experience -= Needs[Level];
                Level++;
                AttributePoints++;
            }
            if (Level == MaximumLevel) Experience = 0;
        }
        Focus = Math.Min(Focus, Statistics.MaximumFocus);
        Dirty = true;
        return new(true, granted, Level - oldLevel, oldRank, Rank, Level == MaximumLevel ? "maximum level" : "awarded");
    }

    public bool SpendAttribute(string name)
    {
        if (AttributePoints <= 0) return false;
        var lower = name.Trim().ToLowerInvariant();
        Attributes = lower switch
        {
            "might" => Attributes with { Might = Attributes.Might + 1 },
            "finesse" => Attributes with { Finesse = Attributes.Finesse + 1 },
            "insight" => Attributes with { Insight = Attributes.Insight + 1 },
            "resolve" => Attributes with { Resolve = Attributes.Resolve + 1 },
            _ => Attributes,
        };
        if (lower is not ("might" or "finesse" or "insight" or "resolve")) return false;
        AttributePoints--;
        Focus = Math.Min(Focus, Statistics.MaximumFocus);
        Dirty = true;
        return true;
    }

    public bool TryCredit(string eventId, long amount)
    {
        if (string.IsNullOrWhiteSpace(eventId) || amount <= 0 || _receipts.Contains("coin:" + eventId)) return false;
        if (Coins > long.MaxValue - amount) return false;
        Remember("coin:" + eventId);
        Coins += amount;
        Dirty = true;
        return true;
    }

    public bool TryDebit(long amount)
    {
        if (amount <= 0 || amount > Coins) return false;
        Coins -= amount;
        Dirty = true;
        return true;
    }

    /// <summary>Settles a non-currency personal reward exactly once under the same bounded ledger.</summary>
    public bool TryClaimReward(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId) || _receipts.Contains("reward:" + eventId)) return false;
        Remember("reward:" + eventId);
        Dirty = true;
        return true;
    }

    public PurchaseResult Buy(string stableName, string purchaseId)
    {
        if (!SpellCatalogue.TryByStableName(stableName, out var spell)) return new(false, 0, "unknown spell");
        if (_learned.Contains(stableName)) return new(false, 0, "already learned");
        if (string.IsNullOrWhiteSpace(purchaseId)) return new(false, 0, "missing purchase identity");
        if (_receipts.Contains("buy:" + purchaseId)) return new(false, 0, "already settled");
        if (Coins < spell.Price) return new(false, 0, "not enough gold");

        Coins -= spell.Price;
        _learned.Add(stableName);
        Remember("buy:" + purchaseId);
        Dirty = true;
        return new(true, spell.Price, "learned");
    }

    public bool Prepare(int slot, string? stableName)
    {
        if ((uint)slot >= PreparedCapacity) return false;
        if (stableName is not null && !_learned.Contains(stableName)) return false;
        if (stableName is not null)
        {
            for (var i = 0; i < _prepared.Length; i++)
                if (i != slot && string.Equals(_prepared[i], stableName, StringComparison.Ordinal)) _prepared[i] = null;
        }
        _prepared[slot] = stableName;
        Dirty = true;
        return true;
    }

    public bool IsPrepared(string stableName) => Array.IndexOf(_prepared, stableName) >= 0;

    public bool SaveLoadout(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 24) return false;
        _loadouts[name] = [.. _prepared];
        Dirty = true;
        return true;
    }

    public bool ApplyLoadout(string name, bool casting)
    {
        if (casting || !_loadouts.TryGetValue(name, out var slots)) return false;
        Array.Copy(slots, _prepared, PreparedCapacity);
        Dirty = true;
        return true;
    }

    public bool CanCast(string stableName, out string reason)
    {
        if (!_learned.Contains(stableName)) { reason = "not learned"; return false; }
        if (!IsPrepared(stableName)) { reason = "not prepared"; return false; }
        var spell = SpellCatalogue.TryByStableName(stableName, out var found) ? found : null;
        if (spell is null) { reason = "unknown spell"; return false; }
        if (Cooldown(stableName) > 0f) { reason = "cooling down"; return false; }
        if (Focus < spell.AtRank(Rank).Focus) { reason = "not enough Focus"; return false; }
        reason = "ready";
        return true;
    }

    internal bool SettleCast(SpellDefinition spell)
    {
        var rank = spell.AtRank(Rank);
        if (Focus < rank.Focus || Cooldown(spell.StableName) > 0f) return false;
        Focus -= rank.Focus;
        _cooldowns[spell.StableName] = rank.Cooldown;
        Dirty = true;
        return true;
    }

    public float Cooldown(string stableName) => _cooldowns.GetValueOrDefault(stableName);

    public void Tick(float dt)
    {
        if (dt <= 0f) return;
        var changed = false;
        foreach (var name in _cooldowns.Keys.ToArray())
        {
            var left = _cooldowns[name] - dt;
            if (left <= 0f) _cooldowns.Remove(name);
            else _cooldowns[name] = left;
            changed = true;
        }
        var maximum = Statistics.MaximumFocus;
        if (Focus < maximum)
        {
            _focusCarry += dt * (4f + Statistics.Haste * 4f);
            var restored = (int)_focusCarry;
            if (restored > 0)
            {
                _focusCarry -= restored;
                Focus = Math.Min(maximum, Focus + restored);
                changed = true;
            }
        }
        else _focusCarry = 0f;
        Dirty |= changed;
    }

    public void SetBind(System.Numerics.Vector3? bind)
    {
        Bind = bind;
        Dirty = true;
    }

    public void LinkCompanion(string instanceId)
    {
        instanceId ??= "";
        if (ActiveCompanionId == instanceId) return;
        ActiveCompanionId = instanceId;
        Dirty = true;
    }

    public void Settled() => Dirty = false;

    public static string CoinsText(long coins)
    {
        coins = Math.Max(0, coins);
        var gold = coins / 10_000;
        var silver = coins / 100 % 100;
        var copper = coins % 100;
        return gold > 0 ? $"{gold:N0}g {silver:00}s {copper:00}c"
            : silver > 0 ? $"{silver}s {copper:00}c" : $"{copper}c";
    }

    public void Write(BinaryWriter into)
    {
        into.Write(2); // payload version
        into.Write(PlayerId);
        into.Write(Level);
        into.Write(Experience);
        into.Write(CurveVersion);
        into.Write(Coins);
        into.Write(Attributes.Might); into.Write(Attributes.Finesse);
        into.Write(Attributes.Insight); into.Write(Attributes.Resolve);
        into.Write(AttributePoints);
        into.Write(Focus);
        into.Write(Bind.HasValue);
        if (Bind is { } bind) { into.Write(bind.X); into.Write(bind.Y); into.Write(bind.Z); }
        into.Write(ActiveCompanionId);
        WriteStrings(into, _learned.Order(StringComparer.Ordinal));
        into.Write(_prepared.Length);
        foreach (var name in _prepared) into.Write(name ?? "");
        into.Write(_loadouts.Count);
        foreach (var pair in _loadouts.OrderBy(one => one.Key, StringComparer.Ordinal))
        {
            into.Write(pair.Key);
            foreach (var name in pair.Value) into.Write(name ?? "");
        }
        into.Write(_cooldowns.Count);
        foreach (var pair in _cooldowns.OrderBy(one => one.Key, StringComparer.Ordinal))
        { into.Write(pair.Key); into.Write(pair.Value); }
        WriteStrings(into, _receiptOrder);
    }

    public string? Read(BinaryReader from)
    {
        try
        {
            var version = from.ReadInt32();
            if (version is < 1 or > 2) return $"unknown character record version {version}";
            PlayerId = NormalizePlayerId(from.ReadString());
            Level = Math.Clamp(from.ReadInt32(), 1, MaximumLevel);
            Experience = Level == MaximumLevel ? 0 : Math.Clamp(from.ReadInt32(), 0, Needs[Level] - 1);
            _ = from.ReadInt32(); // curve version is descriptive; the saved level/into-level XP wins
            Coins = Math.Max(0, from.ReadInt64());
            Attributes = new AttributeSet(
                Math.Clamp(from.ReadInt32(), 1, 999), Math.Clamp(from.ReadInt32(), 1, 999),
                Math.Clamp(from.ReadInt32(), 1, 999), Math.Clamp(from.ReadInt32(), 1, 999));
            AttributePoints = Math.Clamp(from.ReadInt32(), 0, 999);
            Focus = Math.Clamp(from.ReadInt32(), 0, Statistics.MaximumFocus);
            _focusCarry = 0f;
            Bind = from.ReadBoolean()
                ? new System.Numerics.Vector3(from.ReadSingle(), from.ReadSingle(), from.ReadSingle()) : null;
            ActiveCompanionId = from.ReadString();
            _learned.Clear();
            foreach (var name in ReadStrings(from, SpellCatalogue.All.Count))
                if (SpellCatalogue.TryByStableName(name, out _)) _learned.Add(name);
            Array.Clear(_prepared);
            var prepared = Math.Clamp(from.ReadInt32(), 0, 128);
            for (var i = 0; i < prepared; i++)
            {
                var name = from.ReadString();
                if (i < PreparedCapacity && _learned.Contains(name) && Array.IndexOf(_prepared, name) < 0)
                    _prepared[i] = name;
            }
            _loadouts.Clear();
            var loadouts = Math.Clamp(from.ReadInt32(), 0, 64);
            for (var n = 0; n < loadouts; n++)
            {
                var name = from.ReadString();
                var slots = new string?[PreparedCapacity];
                for (var i = 0; i < PreparedCapacity; i++)
                {
                    var spell = from.ReadString();
                    if (_learned.Contains(spell) && Array.IndexOf(slots, spell) < 0) slots[i] = spell;
                }
                _loadouts[name] = slots;
            }
            _cooldowns.Clear();
            var cooldowns = Math.Clamp(from.ReadInt32(), 0, 128);
            for (var i = 0; i < cooldowns; i++)
            {
                var name = from.ReadString();
                var seconds = from.ReadSingle();
                if (SpellCatalogue.TryByStableName(name, out _) && float.IsFinite(seconds) && seconds > 0f)
                    _cooldowns[name] = Math.Min(seconds, 3_600f);
            }
            _receipts.Clear(); _receiptOrder.Clear();
            foreach (var receipt in ReadStrings(from, ReceiptCapacity)) Remember(receipt);
            Dirty = false;
            return null;
        }
        catch (Exception fault) { return $"character record: {fault.Message}"; }
    }

    private static string NormalizePlayerId(string value) =>
        string.IsNullOrWhiteSpace(value) ? "local" : value.Trim()[..Math.Min(value.Trim().Length, 128)];

    private void Remember(string receipt)
    {
        if (!_receipts.Add(receipt)) return;
        _receiptOrder.Enqueue(receipt);
        while (_receiptOrder.Count > ReceiptCapacity)
        {
            var forgotten = _receiptOrder.Dequeue();
            _receipts.Remove(forgotten);
        }
    }

    private static void WriteStrings(BinaryWriter into, IEnumerable<string> values)
    {
        var list = values.ToArray();
        into.Write(list.Length);
        foreach (var value in list) into.Write(value);
    }

    private static List<string> ReadStrings(BinaryReader from, int maximum)
    {
        var count = from.ReadInt32();
        if (count is < 0 || count > maximum) throw new InvalidDataException($"list says it has {count} entries");
        var values = new List<string>(count);
        for (var i = 0; i < count; i++) values.Add(from.ReadString());
        return values;
    }
}
