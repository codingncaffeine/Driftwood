using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;
using Driftwood.Core.Exploration;
using Driftwood.Core.Gen;
using Driftwood.Core.Items;
using Driftwood.Core.Saves;
using Driftwood.Core.World;

namespace Driftwood.Core.Diagnostics;

/// <summary>P14's compact gate: authored world, loot economy, residents and personal rewards.</summary>
internal static class ExplorationAudit
{
    public static List<string> Validate(
        WorldSeed seed,
        BlockRegistry registry,
        StarterBlocks.Ids ids,
        ItemRegistry items,
        out string detail)
    {
        var faults = new List<string>();
        var terrain = new TerrainGenerator(seed, ids);
        var authored = terrain.Exploration;
        var sites = new Dictionary<StructureKind, StructureSite>();

        foreach (var kind in Enum.GetValues<StructureKind>())
        {
            var site = authored.FindNearest(kind, 0, 0, rings: 18);
            if (site is null)
            {
                faults.Add($"no {kind} exists in eighteen deterministic site rings");
                continue;
            }

            sites[kind] = site.Value;
            if (site.Value.Radius > ExplorationGenerator.MaxRadius)
                faults.Add($"{kind} reaches {site.Value.Radius}, past the searched radius");
            if (!authored.TrySiteById(site.Value.Id, out var byId) || byId != site.Value)
                faults.Add($"{kind}'s stable id does not resolve to the same site");

            var cells = FinalCells(authored, site.Value);
            if (cells.Count < 24) faults.Add($"{kind} has only {cells.Count} authored cells");
            if (cells.Keys.Any(cell => cell.Y < site.Value.MinY || cell.Y > site.Value.MaxY))
                faults.Add($"{kind} writes outside the vertical bounds used for chunk culling");

            var chests = authored.ChestCells(site.Value);
            var wantedChests = kind is StructureKind.BuriedGallery or StructureKind.Driftstead ? 2 : 1;
            if (chests.Count < wantedChests)
                faults.Add($"{kind} has {chests.Count} loot cells, fewer than {wantedChests}");
            foreach (var chest in chests)
                if (!cells.TryGetValue(chest, out var block) || block != ids.Chest)
                    faults.Add($"{kind}'s loot cell is overwritten by something else");

            foreach (var required in RequiredBlocks(kind, ids))
                if (!cells.Values.Contains(required))
                    faults.Add($"{kind} never places {registry[required].Name}");

            if (authored.ArchaeologyCell(site.Value) is { } brushable)
            {
                if (!cells.TryGetValue(brushable, out var old) || old != ids.MossyRubble)
                    faults.Add($"{kind}'s archaeology pocket is not visibly suspicious rubble");
            }
        }

        // The palette is a list of names on purpose. Resolve the exact structure vocabulary after
        // moving every id by an arbitrary amount; an id-backed palette cannot pass this.
        var palette = new Palette();
        foreach (var name in ExplorationGenerator.PaletteNames)
        {
            if (!registry.TryByName(name, out _)) faults.Add($"structure palette names missing block '{name}'");
            palette.Add(name);
        }
        var missing = new List<string>();
        var shifted = palette.Resolve(
            name => registry.TryByName(name, out var block) ? block.Id.Value + 1000 : -1,
            missing);
        if (missing.Count > 0) faults.Add($"structure palette lost '{missing[0]}' while resolving names");
        for (var i = 0; i < ExplorationGenerator.PaletteNames.Length && i < shifted.Length; i++)
        {
            var wanted = registry.ByName(ExplorationGenerator.PaletteNames[i]).Id.Value + 1000;
            if (shifted[i] != wanted)
                faults.Add($"structure palette row {i} stayed on an old runtime id");
        }

        foreach (var site in sites.Values) CheckChunkOrder(terrain, site, faults);

        CheckLoot(seed, authored, sites, items, faults);
        CheckProgress(sites, faults);
        CheckInhabitants(authored, sites, items, faults);
        CheckActors(faults);

        detail = $"{sites.Count}/{Enum.GetValues<StructureKind>().Length} site kinds, "
               + $"{ExplorationGenerator.PaletteNames.Length} named palette cells, deterministic seams, "
               + $"{WorldLoot.PossibleItemNames.Count()} loot entries, four scheduled residents, "
               + "real trades and two late-join-safe reward tiers";
        return faults;
    }

    private static Dictionary<(int X, int Y, int Z), BlockId> FinalCells(
        ExplorationGenerator authored,
        StructureSite site)
    {
        var cells = new Dictionary<(int X, int Y, int Z), BlockId>();
        authored.Walk(site, (x, y, z, block) => cells[(x, y, z)] = block);
        return cells;
    }

    private static BlockId[] RequiredBlocks(StructureKind kind, StarterBlocks.Ids ids) => kind switch
    {
        StructureKind.BuriedGallery =>
            [BlockId.Air, ids.Rail, ids.Log, ids.Rubble, ids.MossyRubble, ids.Chest, ids.Cobweb, ids.Lantern],
        StructureKind.Driftstead =>
            [BlockId.Air, ids.Rubble, ids.Bricks, ids.Water, ids.Log, ids.Planks, ids.Glass, ids.Chest],
        StructureKind.Tidewreck => [ids.Log, ids.Planks, ids.MossyRubble, ids.Chest],
        StructureKind.StormVault =>
            [BlockId.Air, ids.Rubble, ids.Moss, ids.Bricks, ids.Chest, ids.Lantern],
        StructureKind.StarfallCrown => [ids.Rubble, ids.Moss, ids.Bricks, ids.Chest, ids.Lantern],
        _ => [],
    };

    private static void CheckChunkOrder(
        TerrainGenerator terrain,
        StructureSite site,
        List<string> faults)
    {
        var low = ChunkPos.FromWorld(
            site.X - site.Radius, site.VerticalBounds.Min, site.Z - site.Radius);
        var high = ChunkPos.FromWorld(
            site.X + site.Radius, site.VerticalBounds.Max, site.Z + site.Radius);
        var positions = new List<ChunkPos>();
        for (var cy = low.Y; cy <= high.Y; cy++)
        for (var cz = low.Z; cz <= high.Z; cz++)
        for (var cx = low.X; cx <= high.X; cx++) positions.Add(new ChunkPos(cx, cy, cz));

        Dictionary<ChunkPos, ushort[]> Snapshot(bool reverse, int reach)
        {
            var result = new Dictionary<ChunkPos, ushort[]>();
            var ordered = reverse ? positions.AsEnumerable().Reverse() : positions;
            foreach (var position in ordered)
            {
                var chunk = new Chunk(position);
                terrain.GenerateChunk(chunk);
                terrain.DecorateChunk(chunk, reach);
                result[position] = [.. chunk.Raw];
            }
            return result;
        }

        var forward = Snapshot(reverse: false, TerrainGenerator.DecorReach);
        var backward = Snapshot(reverse: true, TerrainGenerator.DecorReach);
        var wide = Snapshot(reverse: false, TerrainGenerator.DecorReach * 2);

        foreach (var position in positions)
        {
            if (!forward[position].AsSpan().SequenceEqual(backward[position]))
                faults.Add($"authored chunk {position} changes when generation order reverses");
            if (!forward[position].AsSpan().SequenceEqual(wide[position]))
                faults.Add($"authored chunk {position} changes when search reach is widened");
        }
    }

    private static void CheckLoot(
        WorldSeed seed,
        ExplorationGenerator authored,
        IReadOnlyDictionary<StructureKind, StructureSite> sites,
        ItemRegistry items,
        List<string> faults)
    {
        foreach (var name in WorldLoot.PossibleItemNames)
            if (!items.TryByName(name, out _)) faults.Add($"loot table names missing item '{name}'");
        foreach (var name in ExplorationRewards.DirectItemNames)
            if (!items.TryByName(name, out _)) faults.Add($"exploration rewards name missing item '{name}'");

        if (!WorldLoot.PossibleAt(StructureKind.Tidewreck).Contains("trial_key", StringComparer.Ordinal))
            faults.Add("no exploration loot begins the keyed encounter chain");

        foreach (var (kind, site) in sites)
        {
            var cell = authored.ChestCells(site).FirstOrDefault();
            if (cell == default)
            {
                faults.Add($"{kind} has no chest to initialize");
                continue;
            }

            var firstBank = new ChestBank(items);
            if (!WorldLoot.TryInitialize(
                    seed, authored, firstBank, items, cell.X, cell.Y, cell.Z, out var found)
                || found != site)
            {
                faults.Add($"{kind}'s chest did not initialize against its own site");
                continue;
            }

            var first = firstBank.Open(cell.X, cell.Y, cell.Z);
            var fingerprint = Fingerprint(first, items);
            if (first.IsEmpty) faults.Add($"{kind}'s generated chest rolled empty");

            var secondBank = new ChestBank(items);
            WorldLoot.TryInitialize(seed, authored, secondBank, items, cell.X, cell.Y, cell.Z, out _);
            var repeated = Fingerprint(secondBank.Open(cell.X, cell.Y, cell.Z), items);
            if (fingerprint != repeated) faults.Add($"{kind}'s loot changes on the same seed and site");

            _ = firstBank.Drain(cell.X, cell.Y, cell.Z).ToArray();
            if (WorldLoot.TryInitialize(seed, authored, firstBank, items, cell.X, cell.Y, cell.Z, out _)
                || !first.IsEmpty)
                faults.Add($"{kind}'s broken and replaced generated chest rerolls its loot");

            if (!WorldLoot.TryFindChest(authored, cell.X, cell.Y, cell.Z, out var foundAgain, out _)
                || foundAgain != site)
                faults.Add($"{kind}'s loot cell cannot recover its stable site identity");
        }
    }

    private static string Fingerprint(Chest chest, ItemRegistry items) =>
        string.Join('|', chest.Contents.Select(stack => stack.IsEmpty
            ? "-"
            : $"{items[stack.Item].Name}:{stack.Count}:{stack.Damage}"));

    private static void CheckProgress(
        IReadOnlyDictionary<StructureKind, StructureSite> sites,
        List<string> faults)
    {
        if (!sites.TryGetValue(StructureKind.BuriedGallery, out var gallery)
            || !sites.TryGetValue(StructureKind.StormVault, out var vault)
            || !sites.TryGetValue(StructureKind.StarfallCrown, out var crown)) return;

        var progress = new ExplorationProgress();
        if (!progress.Brush(gallery.Id) || progress.Brush(gallery.Id))
            faults.Add("one archaeology pocket can be paid more than once");

        progress.Begin(vault.Id, EncounterKind.Trial);
        var trialReward = ExplorationRewards.RewardFor(EncounterKind.Trial);
        if (progress.CanClaim(vault.Id, trialReward, "first"))
            faults.Add("a player can claim a vault before its fight is clear");
        if (!progress.Clear(vault.Id)) faults.Add("the active vault would not clear");
        if (!progress.Claim(vault.Id, trialReward, "first")
            || progress.Claim(vault.Id, trialReward, "first"))
            faults.Add("the first player can take the same vault reward twice");
        if (!progress.Claim(vault.Id, trialReward, "late"))
            faults.Add("a player joining after the vault cleared cannot claim their reward");

        progress.Begin(crown.Id, EncounterKind.Crown);
        if (!progress.SetPhase(crown.Id, 2) || progress.SetPhase(crown.Id, 1))
            faults.Add("the Crown encounter can move backward through its phases");
        if (!progress.Clear(crown.Id)
            || !progress.Claim(crown.Id, ExplorationRewards.RewardFor(EncounterKind.Crown), "late"))
            faults.Add("the late landmark cannot finish and pay its personal reward");
    }

    private static void CheckInhabitants(
        ExplorationGenerator authored,
        IReadOnlyDictionary<StructureKind, StructureSite> sites,
        ItemRegistry items,
        List<string> faults)
    {
        if (!sites.TryGetValue(StructureKind.Driftstead, out var settlement)) return;

        var inhabitants = new InhabitantSystem();
        inhabitants.EnsureSettlement(authored, settlement);
        inhabitants.EnsureSettlement(authored, settlement);
        if (inhabitants.All.Count != 4)
            faults.Add($"a settlement owns {inhabitants.All.Count} residents rather than four");
        if (inhabitants.All.Select(one => one.Id).Distinct(StringComparer.Ordinal).Count()
            != inhabitants.All.Count)
            faults.Add("two settlement residents share one persistent identity");
        if (inhabitants.All.Any(one => one.SettlementId != settlement.Id))
            faults.Add("a resident lost the settlement that owns them");

        // Collision leaves the canopy solid, while legal spawn/path support excludes it. A
        // resident restored on that canopy must step down to real ground before scheduling begins.
        var treeResidents = new InhabitantSystem();
        treeResidents.Reload(inhabitants.Capture());
        var treeResident = treeResidents.All[0];
        var treeX = (int)MathF.Floor(treeResident.Position.X);
        var treeZ = (int)MathF.Floor(treeResident.Position.Z);
        treeResident.Position = new Vector3(treeX + 0.5f, 71f, treeZ + 0.5f);
        bool TreeWorld(int x, int y, int z) => y < 64 || x == treeX && z == treeZ && y == 70;
        static bool SoilOnly(int x, int y, int z) => y < 64;
        treeResidents.Update(0.1f, 0.35f, TreeWorld, spawnSupport: SoilOnly);
        if (treeResidents.All[0].Position.Y >= 70f)
            faults.Add("a resident accepted a tree canopy as spawn support");

        var before = inhabitants.All.Select(one => one.Position).ToArray();
        for (var i = 0; i < 240; i++)
            inhabitants.Update(
                0.1f,
                0.35f,
                (_, y, _) => y < settlement.Y);
        if (!inhabitants.All.Where((one, i) => Vector3.DistanceSquared(one.Position, before[i]) > 1f).Any())
            faults.Add("scheduled residents never navigate from home toward work");
        if (inhabitants.All.Any(one =>
                Math.Abs(one.Position.X - settlement.X) > 32f
                || Math.Abs(one.Position.Z - settlement.Z) > 32f))
            faults.Add("a resident's bounded path escaped their settlement");

        var saved = inhabitants.Capture();
        var back = new InhabitantSystem();
        back.Reload(saved);
        if (!saved.SequenceEqual(back.Capture())) faults.Add("inhabitants lose identity, ownership or pose on reload");
        if (back.Dirty) faults.Add("reloaded inhabitants immediately demand another save");

        foreach (var profession in Enum.GetValues<Profession>())
        {
            var offers = Trading.For(profession);
            if (profession == Profession.Lorekeeper)
            {
                if (offers.Count != 0) faults.Add("the Lorekeeper incorrectly owns inventory-token trades");
                continue;
            }
            if (offers.Count < 3) faults.Add($"{profession} offers only {offers.Count} trades");
            foreach (var offer in offers)
            {
                var empty = new Inventory(items);
                if (Trading.CanPay(offer, empty, items) || Trading.TryMake(offer, empty, items))
                    faults.Add($"{profession}'s '{offer.Label}' can be taken without payment");

                if (!items.TryByName(offer.Cost, out var cost)
                    || !items.TryByName(offer.Result, out var result))
                {
                    faults.Add($"{profession}'s '{offer.Label}' names an item this build does not have");
                    continue;
                }

                var pockets = new Inventory(items);
                pockets.Add(new ItemStack(cost.Id, offer.CostCount));
                if (!Trading.TryMake(offer, pockets, items))
                    faults.Add($"{profession}'s payable '{offer.Label}' is refused");
                else if (pockets.CountOf(cost.Id) != (cost.Id == result.Id ? offer.ResultCount : 0)
                         || pockets.CountOf(result.Id) != offer.ResultCount)
                    faults.Add($"{profession}'s '{offer.Label}' does not settle both sides exactly");

                var batch = new Inventory(items);
                batch.Add(new ItemStack(cost.Id, offer.CostCount * 4));
                if (Trading.Maximum(offer, batch, items) != 4)
                    faults.Add($"{profession}'s '{offer.Label}' does not expose its payable quantity");
                else if (!Trading.TryMake(offer, batch, items, 4)
                         || batch.CountOf(result.Id) != offer.ResultCount * 4
                         || cost.Id != result.Id && batch.CountOf(cost.Id) != 0)
                    faults.Add($"{profession}'s '{offer.Label}' does not settle a four-trade batch atomically");
            }
        }
    }

    private static void CheckActors(List<string> faults)
    {
        foreach (var name in new[]
                 {
                     "shorewright", "forager", "waykeeper", "lorekeeper", "storm_sentinel", "starwarden",
                 })
        {
            var kind = CreatureSet.All.FirstOrDefault(one => one.Name == name);
            if (kind.Name is null) faults.Add($"P14 actor '{name}' has no creature catalogue row");
            else if (name is "storm_sentinel" or "starwarden"
                     ? kind.Family != CreatureFamily.Encounter
                     : kind.Family != CreatureFamily.Inhabitant)
                faults.Add($"P14 actor '{name}' is routed through the wrong spawn family");
            if (StarterCreatures.ByName(name) is null) faults.Add($"P14 actor '{name}' has no owned model");
        }

        if (CreatureVitals.HealthFor("starwarden") <= CreatureVitals.HealthFor("storm_sentinel"))
            faults.Add("the late landmark's warden is no tougher than one trial sentinel");
    }
}
