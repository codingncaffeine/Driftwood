using Driftwood.Core.Gen;
using Driftwood.Core.Items;
using Driftwood.Core.World;

namespace Driftwood.Core.Exploration;

public enum EncounterKind : byte { Trial, Crown }

/// <summary>
/// The non-container rewards in P14's exploration chain. Keeping these names beside the encounter
/// state lets the client, reachability walk and reports describe the same economy.
/// </summary>
public static class ExplorationRewards
{
    public const string ArchaeologyFind = "relic_shard";

    public static string KeyFor(EncounterKind kind) => kind switch
    {
        EncounterKind.Trial => "trial_key",
        EncounterKind.Crown => "star_key",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string RewardFor(EncounterKind kind) => kind switch
    {
        EncounterKind.Trial => "star_key",
        EncounterKind.Crown => "starheart",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static IEnumerable<string> DirectItemNames =>
        Enum.GetValues<EncounterKind>().Select(RewardFor).Prepend(ArchaeologyFind);
}

public sealed class EncounterRecord
{
    public required string SiteId { get; init; }
    public EncounterKind Kind { get; init; }
    public bool Active { get; set; }
    public bool Cleared { get; set; }
    public int Phase { get; set; }
}

/// <summary>
/// Persistent authored-world facts. Claims include player identity from day one, so a late-joining
/// player receives their own vault reward without respawning or duplicating the shared encounter.
/// </summary>
public sealed class ExplorationProgress
{
    private readonly HashSet<string> _brushed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _claims = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EncounterRecord> _encounters = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Brushed => _brushed;
    public IReadOnlyCollection<string> Claims => _claims;
    public IReadOnlyCollection<EncounterRecord> Encounters => _encounters.Values;
    public bool Dirty { get; private set; }

    public bool Brush(string siteId)
    {
        if (!_brushed.Add(siteId)) return false;
        Dirty = true;
        return true;
    }

    public EncounterRecord Begin(string siteId, EncounterKind kind)
    {
        if (!_encounters.TryGetValue(siteId, out var encounter))
        {
            encounter = new EncounterRecord { SiteId = siteId, Kind = kind };
            _encounters.Add(siteId, encounter);
        }
        if (!encounter.Cleared)
        {
            encounter.Active = true;
            encounter.Phase = Math.Max(1, encounter.Phase);
            Dirty = true;
        }
        return encounter;
    }

    public EncounterRecord? At(string siteId) => _encounters.GetValueOrDefault(siteId);

    public bool SetPhase(string siteId, int phase)
    {
        if (!_encounters.TryGetValue(siteId, out var encounter) || phase <= encounter.Phase) return false;
        encounter.Phase = phase;
        Dirty = true;
        return true;
    }

    public bool Clear(string siteId)
    {
        if (!_encounters.TryGetValue(siteId, out var encounter) || encounter.Cleared) return false;
        encounter.Active = false;
        encounter.Cleared = true;
        Dirty = true;
        return true;
    }

    public bool CanClaim(string siteId, string reward, string playerId) =>
        _encounters.TryGetValue(siteId, out var encounter) && encounter.Cleared
        && !_claims.Contains(ClaimKey(siteId, reward, playerId));

    public bool Claim(string siteId, string reward, string playerId)
    {
        if (!CanClaim(siteId, reward, playerId)) return false;
        _claims.Add(ClaimKey(siteId, reward, playerId));
        Dirty = true;
        return true;
    }

    public void Reload(
        IEnumerable<string> brushed,
        IEnumerable<string> claims,
        IEnumerable<EncounterRecord> encounters)
    {
        _brushed.Clear();
        _claims.Clear();
        _encounters.Clear();
        foreach (var value in brushed) _brushed.Add(value);
        foreach (var value in claims) _claims.Add(value);
        foreach (var value in encounters)
            _encounters[value.SiteId] = new EncounterRecord
            {
                SiteId = value.SiteId,
                Kind = value.Kind,
                Active = value.Active,
                Cleared = value.Cleared,
                Phase = value.Phase,
            };
        Dirty = false;
    }

    public void Settled() => Dirty = false;

    private static string ClaimKey(string siteId, string reward, string playerId) =>
        $"{siteId}\u001f{reward}\u001f{playerId}";
}

/// <summary>Deterministic generated chest contents, initialized exactly once through ChestBank.</summary>
public static class WorldLoot
{
    private readonly record struct Roll(string Name, int Min, int Max, float Chance = 1f);

    private static readonly Dictionary<StructureKind, Roll[]> Tables = new()
    {
        [StructureKind.BuriedGallery] =
        [
            new("coal", 3, 9), new("raw_iron", 1, 4), new("bread", 1, 3),
            new("arrow", 3, 10), new("trade_token", 1, 2, 0.55f),
        ],
        [StructureKind.Driftstead] =
        [
            new("bread", 2, 5), new("seeds", 2, 7), new("berries", 1, 4),
            new("trade_token", 1, 3, 0.75f),
        ],
        [StructureKind.Tidewreck] =
        [
            new("raw_gold", 2, 6), new("paper", 2, 7), new("trial_key", 1, 1),
            new("trade_token", 1, 3), new("relic_chart", 1, 1, 0.35f),
        ],
        [StructureKind.StormVault] =
        [
            new("arrow", 8, 18), new("cooked_beef", 1, 3), new("stormglass", 1, 2, 0.45f),
        ],
        [StructureKind.StarfallCrown] =
        [
            new("azurite", 3, 8), new("diamond", 1, 2, 0.45f), new("gold_ingot", 2, 5),
        ],
    };

    /// <summary>
    /// Every item that can turn up in authored loot. A non-zero chance is reachable because the
    /// world contains indefinitely many independently rolled sites; the audit consumes this same
    /// table instead of maintaining a second list that can drift away from the chests.
    /// </summary>
    public static IEnumerable<string> PossibleItemNames =>
        Tables.Values.SelectMany(table => table).Select(roll => roll.Name).Distinct(StringComparer.Ordinal);

    public static IEnumerable<string> PossibleAt(StructureKind kind) =>
        Tables[kind].Select(roll => roll.Name).Distinct(StringComparer.Ordinal);

    public static bool TryInitialize(
        WorldSeed seed,
        ExplorationGenerator generator,
        ChestBank bank,
        ItemRegistry items,
        int x,
        int y,
        int z,
        out StructureSite site)
    {
        site = default;
        if (bank.TryGet(x, y, z, out _)) return false;
        if (!TryFindChest(generator, x, y, z, out site, out var chestIndex)) return false;

        var chest = bank.Open(x, y, z);
        var random = new Random(seed.Derive($"loot/{site.Id}/{chestIndex}"));
        foreach (var roll in Tables[site.Kind])
        {
            if (random.NextDouble() > roll.Chance || !items.TryByName(roll.Name, out var item)) continue;
            var count = random.Next(roll.Min, roll.Max + 1);
            var left = bank.Add(chest, new ItemStack(item.Id, count));
            if (!left.IsEmpty) throw new InvalidOperationException($"generated loot overflowed {site.Id}");
        }
        return true;
    }

    public static bool TryFindChest(
        ExplorationGenerator generator,
        int x,
        int y,
        int z,
        out StructureSite site,
        out int chestIndex)
    {
        foreach (var candidate in generator.SitesAffecting(x, x, z, z, ExplorationGenerator.MaxRadius))
        {
            var cells = generator.ChestCells(candidate);
            for (var i = 0; i < cells.Count; i++)
            {
                if (cells[i] != (x, y, z)) continue;
                site = candidate;
                chestIndex = i;
                return true;
            }
        }
        site = default;
        chestIndex = -1;
        return false;
    }
}
