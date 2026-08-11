using System.Numerics;
using Driftwood.Core.Audio;
using Driftwood.Core.Exploration;
using Driftwood.Core.Gen;
using Driftwood.Core.Magic;
using Driftwood.Core.Settings;

namespace Driftwood.Core.Diagnostics;

/// <summary>The headless P10.5 contract: data, authority, clocks, persistence and two-player isolation.</summary>
public static class MagicAudit
{
    private static readonly string[] ExactNames =
    [
        "Holy Might", "Quick Heal", "Revive", "Holy Shield", "Root",
        "Summon Bones", "Animate Zombie", "Fear", "Draw Lifeforce", "Leech",
        "Lightning Streak", "Ignite", "Tree of Life", "Spirit Wolf",
        "Ice Shock", "Fire Bolt", "Gateway Rift", "Snare", "Earth Elemental",
    ];

    public static List<string> Run(out string detail)
    {
        var faults = new List<string>();
        faults.AddRange(SpellCatalogue.Faults());
        if (!SpellCatalogue.All.Select(one => one.DisplayName).SequenceEqual(ExactNames))
            faults.Add("the public catalogue is not the locked exact 19-name order");

        CheckRanksAndXp(faults);
        CheckRewards(faults);
        CheckWalletAndSpellbook(faults);
        CheckCastingAndEffects(faults);
        CheckCompanionsAndGateway(faults);
        CheckControls(faults);

        detail = faults.Count == 0
            ? "19 spells in 5/5/4/5 open groups; levels 1-20 derive Ranks I-IV; buy-once gold, "
              + "eight prepared slots, simulation-time casts/effects, four summons under one-pet cap, "
              + "idempotent rifts, stable-name round trips and independent player IDs"
            : $"{faults.Count} faults: {faults[0]}";
        return faults;
    }

    private static void CheckRewards(List<string> faults)
    {
        var hostile = CharacterRewards.Creature("zombie", grown: true, hostile: true);
        if (hostile.Empty
            || hostile.Experience <= CharacterRewards.Creature("cow", grown: true, hostile: false).Experience)
            faults.Add("the shared creature reward table does not scale a hostile above a grown beast");
        if (!CharacterRewards.Creature("chicken", grown: false, hostile: false).Empty)
            faults.Add("a trivial young creature awards progression or coins");

        var site = new StructureSite(
            "p14/tidewreck/1/2", StructureKind.Tidewreck, 1, 2, 10, 62, 20, 12, 60, 70);
        var first = CharacterRewards.Chest(new WorldSeed(1234), site, 0);
        var again = CharacterRewards.Chest(new WorldSeed(1234), site, 0);
        if (first.Empty || first != again)
            faults.Add("a generated chest's personal coin value is empty or non-deterministic");
        var gearKinds = Enum.GetValues<StructureKind>();
        var gearRanks = new[] { 1, 1, 2, 3, 4 };
        for (var i = 0; i < gearKinds.Length; i++)
        {
            var gearSite = site with { Id = $"p14/gear/{i}", Kind = gearKinds[i] };
            var gear = CharacterRewards.Gear(new WorldSeed(1234), gearSite, 0);
            if (gear.Empty || gear.Rank != gearRanks[i]
                || gear != CharacterRewards.Gear(new WorldSeed(1234), gearSite, 0))
                faults.Add($"{gearKinds[i]} has no deterministic authored Rank {gearRanks[i]} gear cache");
            if (!CharacterRewards.Gear(new WorldSeed(1234), gearSite, 1).Empty)
                faults.Add($"{gearKinds[i]} duplicated its personal gear cache in a later chest");
        }
        if (Enum.GetValues<StructureKind>().Any(kind => CharacterRewards.Discovery(kind).Empty)
            || Enum.GetValues<EncounterKind>().Any(kind => CharacterRewards.Encounter(kind).Empty))
            faults.Add("a P14 discovery or encounter has no XP/coin reward definition");
        if (CharacterRewards.Survival(1).Experience <= 0
            || !CharacterRewards.Survival(CharacterRewards.MaximumSurvivalMilestones + 1).Empty)
            faults.Add("survival XP is not positive inside its bounded milestone window");
        var claimant = new CharacterProgression("gear-claimant");
        if (!claimant.TryClaimReward("site:1") || claimant.TryClaimReward("site:1"))
            faults.Add("a personal leveled-gear reward did not settle exactly once");
    }

    private static void CheckRanksAndXp(List<string> faults)
    {
        var expected = new[] { (1,1), (5,1), (6,2), (10,2), (11,3), (15,3), (16,4), (20,4) };
        foreach (var (level, rank) in expected)
            if (CharacterProgression.RankFor(level) != rank)
                faults.Add($"level {level} derived Rank {CharacterProgression.RankFor(level)}, wanted {rank}");
        if (CharacterProgression.RankFor(20) != 4) faults.Add("level 20 produced a Rank V");
        for (var level = 2; level <= CharacterProgression.MaximumLevel; level++)
            if (CharacterProgression.TotalExperienceForLevel(level)
                <= CharacterProgression.TotalExperienceForLevel(level - 1))
                faults.Add($"the XP curve does not rise at level {level}");

        var first = new CharacterProgression("player-a");
        var second = new CharacterProgression("player-b");
        var eventNumber = 0;
        while (first.Level < 7)
            first.AwardExperience($"creature-{eventNumber++}", 600, XpSource.Creature);
        if (first.Level < 7 || first.Rank != 2) faults.Add("multi-level XP did not cross the first rank boundary");
        var before = first.Level;
        var duplicate = first.AwardExperience("creature-0", 600, XpSource.Creature);
        if (duplicate.Accepted || first.Level != before) faults.Add("a duplicate XP event paid twice");
        if (second.Level != 1 || second.Rank != 1) faults.Add("one player changed another player's level");

        while (first.Level < CharacterProgression.MaximumLevel)
            first.AwardExperience($"encounter-{eventNumber++}", 1_200, XpSource.Encounter);
        var capped = first.AwardExperience("past-cap", 1_200, XpSource.Encounter);
        if (!capped.Accepted || first.Level != 20 || first.Experience != 0 || first.Rank != 4)
            faults.Add("cap-20 overflow changed level, XP or rank");
    }

    private static void CheckWalletAndSpellbook(List<string> faults)
    {
        var player = new CharacterProgression("buyer");
        if (player.TryCredit("negative", -1) || player.TryDebit(-1)) faults.Add("a negative wallet change settled");
        if (!player.TryCredit("fund", 1_000_000) || player.TryCredit("fund", 1))
            faults.Add("coin pickup receipt did not settle exactly once");
        if (player.TryCredit("overflow", long.MaxValue)) faults.Add("wallet overflow did not fail closed");
        if (CharacterProgression.CoinsText(123_456) != "12g 34s 56c")
            faults.Add("gold/silver/copper formatting drifted");

        var slot = 0;
        foreach (var spell in SpellCatalogue.All)
        {
            var purchase = player.Buy(spell.StableName, "purchase:" + spell.StableName);
            if (!purchase.Accepted || purchase.Paid != spell.Price)
                faults.Add($"fresh level-one buyer could not learn {spell.DisplayName}");
            if (slot < CharacterProgression.PreparedCapacity && !player.Prepare(slot++, spell.StableName))
                faults.Add($"learned spell {spell.DisplayName} could not be prepared");
        }
        if (player.Learned.Count != 19 || player.Prepared.Count(one => one is not null) != 8)
            faults.Add("learning all 19 did not retain an exact eight-slot prepared bar");
        if (player.Buy(SpellCatalogue.All[0].StableName, "another").Accepted)
            faults.Add("an already-learned spell debited twice");
        if (player.Prepare(8, SpellCatalogue.All[8].StableName)) faults.Add("a ninth prepared slot was accepted");
        if (!player.SaveLoadout("mixed eight")) faults.Add("the exact eight-slot bar could not be named as a loadout");
        player.Prepare(0, null);
        if (player.ApplyLoadout("mixed eight", casting: true)
            || !player.ApplyLoadout("mixed eight", casting: false)
            || player.Prepared.Count(one => one is not null) != CharacterProgression.PreparedCapacity)
            faults.Add("a named loadout changed during a cast or failed to restore all eight slots afterward");

        using var bytes = new MemoryStream();
        using (var into = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true)) player.Write(into);
        bytes.Position = 0;
        var back = new CharacterProgression("wrong");
        using (var from = new BinaryReader(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
            if (back.Read(from) is { } why) faults.Add(why);
        if (back.PlayerId != player.PlayerId || back.Coins != player.Coins
            || !back.Learned.SetEquals(player.Learned)
            || !back.Prepared.SequenceEqual(player.Prepared)
            || !back.HasLoadout("mixed eight"))
            faults.Add("stable player, wallet, learned names, prepared names or loadouts changed on save round trip");

        var poor = new CharacterProgression("poor");
        if (poor.Buy(SpellCatalogue.All[^1].StableName, "poor-buy").Accepted || poor.Coins != 0)
            faults.Add("an unaffordable purchase changed a wallet or spellbook");
    }

    private static void CheckCastingAndEffects(List<string> faults)
    {
        var player = new CharacterProgression("caster");
        player.TryCredit("fund", 100_000);
        var holy = SpellCatalogue.ById(SpellId.HolyMight);
        player.Buy(holy.StableName, "buy-holy");
        player.Prepare(0, holy.StableName);
        var casting = new SpellCastingService();
        var target = new CastTarget(EffectTargetKind.Creature, "hostile-1", new Vector3(4, 0, 0), true, false, true);
        if (!casting.Begin(player, holy.StableName, Vector3.Zero, target, true).Accepted)
            faults.Add("a learned prepared funded direct spell was refused");
        casting.Tick(1f / 60f);
        var events = casting.TakeEvents();
        if (!events.Any(one => one.Kind == CastEventKind.Released)
            || !events.Any(one => one.Kind == CastEventKind.Completed))
            faults.Add("an instant cast did not release and complete authoritatively");
        var focusAfter = player.Focus;
        if (casting.Begin(player, holy.StableName, Vector3.Zero, target with { Hostile = false }, true).Accepted
            || player.Focus != focusAfter)
            faults.Add("an invalid target spent Focus or cast");

        var effects = new SpellEffectService();
        var victim = new EffectTarget(EffectTargetKind.Creature, "victim");
        effects.Apply(SpellEffectKind.LifeforceLeech, victim, "player-a", 1, 2, 3, 1, EffectDispelFamily.Grave);
        effects.Apply(SpellEffectKind.LifeforceLeech, victim, "player-b", 2, 3, 3, 1, EffectDispelFamily.Grave);
        if (effects.Count != 2) faults.Add("two players' identical drains lost their distinct sources");
        effects.Tick(1f);
        var ticks = effects.TakeEvents().Where(one => one.Kind == EffectEventKind.TickDamage).ToArray();
        if (ticks.Length != 2 || ticks.Select(one => one.SourcePlayerId).Distinct().Count() != 2)
            faults.Add("source-scoped effect ticks did not retain attribution");

        effects.Apply(SpellEffectKind.HolyShield, new(EffectTargetKind.Player, "player-a"), "player-a", 1, 6, 8);
        if (effects.Absorb(new(EffectTargetKind.Player, "player-a"), 9) != 3)
            faults.Add("Holy Shield did not absorb its exact authoritative amount");
        effects.Apply(SpellEffectKind.Snared, victim, "player-a", 1, 65, 4);
        if (MathF.Abs(effects.MovementMultiplier(victim) - 0.65f) > 0.001f)
            faults.Add("Snare's movement value does not come from its effect snapshot");

        var fire = new SpellEffectService();
        fire.Sustain(SpellEffectKind.Burning, victim, "world:sunlight", 1, 1, 0.2f,
            1f / Driftwood.Core.Entities.CreatureHerd.ScorchRate, EffectDispelFamily.Flame);
        fire.Sustain(SpellEffectKind.Burning, victim, "world:sunlight", 1, 1, 0.2f,
            1f / Driftwood.Core.Entities.CreatureHerd.ScorchRate, EffectDispelFamily.Flame);
        fire.Apply(SpellEffectKind.Burning, victim, "player-a", 2, 3, 4f, 1f,
            EffectDispelFamily.Flame);
        fire.Sustain(SpellEffectKind.Burning, victim, "world:sunlight", 1, 1, 0.2f,
            1f / Driftwood.Core.Entities.CreatureHerd.ScorchRate, EffectDispelFamily.Flame);
        var burning = fire.Snapshots.Single();
        if (fire.Count != 1 || burning.SourcePlayerId != "player-a" || burning.Magnitude != 3
            || MathF.Abs(burning.Remaining - 4f) > 0.001f || MathF.Abs(burning.TickEvery - 1f) > 0.001f
            || fire.TakeEvents().Count(one => one.Kind == EffectEventKind.Applied) != 1)
            faults.Add("daylight scorch and Ignite did not share one quiet-refresh burning clock");

        var revives = new ReviveAuthority();
        revives.MarkDead("player-b", new Vector3(12, 40, 18));
        var restored = (Player: "", At: Vector3.Zero, Health: 0);
        var revived = revives.TryRevive(
            "revive:1", "player-a", "player-b", 8,
            death => death + Vector3.UnitY,
            (id, at, health) => restored = (id, at, health));
        var replay = revives.TryRevive(
            "revive:1", "player-a", "player-b", 8,
            death => death, (_, _, _) => { });
        if (!revived.Accepted || replay.Accepted || revives.IsDead("player-b")
            || restored != ("player-b", new Vector3(12, 41, 18), 8))
            faults.Add("Revive did not settle one dead allied player once at the safe death position");
        revives.MarkDead("player-c", Vector3.Zero);
        if (revives.TryRevive("revive:self", "player-c", "player-c", 5,
                at => at, (_, _, _) => { }).Accepted)
            faults.Add("Revive accepted its own living caster as the dead allied target");
    }

    private static void CheckCompanionsAndGateway(List<string> faults)
    {
        foreach (var kind in Enum.GetValues<CompanionKind>())
        {
            var identityEvents = new[]
            {
                MagicSounds.PetIdentity(kind), MagicSounds.PetMovement(kind), MagicSounds.PetIdle(kind),
                MagicSounds.PetAttackFor(kind), MagicSounds.PetHurtFor(kind),
                MagicSounds.PetLowHealthFor(kind), MagicSounds.PetDeathFor(kind),
            };
            if (identityEvents.Any(sound => !MagicSounds.All.Contains(sound)))
                faults.Add($"{kind} does not own the complete identity/movement/idle/combat sound vocabulary");
        }
        var pets = new CompanionService();
        var first = pets.Summon("summon-1", "player-a", SpellId.SummonBones, 1, Vector3.Zero);
        var second = pets.Summon("summon-2", "player-a", SpellId.SpiritWolf, 2, Vector3.One);
        if (first is null || second is null || pets.All.Count != 1 || pets.For("player-a")?.Kind != CompanionKind.SpiritWolf)
            faults.Add("summoning across families did not enforce one pet per player");
        if (pets.Summon("summon-2", "player-a", SpellId.EarthElemental, 2, Vector3.One) is not null)
            faults.Add("a replayed summon receipt duplicated or replaced a pet");
        pets.Summon("summon-b", "player-b", SpellId.EarthElemental, 1, Vector3.Zero);
        if (pets.All.Count != 2) faults.Add("two player IDs did not retain independent pets");
        var before = pets.For("player-a")!.Health;
        if (pets.Hurt("player-a", 8, "player-a") != 0 || pets.For("player-a")!.Health != before)
            faults.Add("an owner could ordinarily damage their own pet");
        if (!pets.Command("player-a", CompanionCommand.Guard)
            || !pets.Command("player-a", CompanionCommand.Follow)
            || !pets.Command("player-a", CompanionCommand.Stop)
            || pets.Command("player-a", CompanionCommand.Attack))
            faults.Add("pet commands did not enforce their target contract");
        pets.Command("player-a", CompanionCommand.Guard);
        if (!pets.DefendAgainst("player-a", "hostile-1"))
            faults.Add("a guarding pet could not accept a temporary defensive target");
        var guard = pets.For("player-a")!;
        var guardStart = guard.Position;
        pets.Update(0.5f, _ => Vector3.Zero,
            id => id == "hostile-1" ? new Vector3(7, 1, 1) : null);
        if (guard.Position == guardStart || guard.Command != CompanionCommand.Guard)
            faults.Add("a guarding pet did not pursue a hostile while retaining Guard");
        pets.DefendAgainst("player-a", "");
        pets.Command("player-a", CompanionCommand.Stop);
        if (pets.DefendAgainst("player-a", "hostile-2"))
            faults.Add("a stopped pet accepted an automatic defensive target");
        pets.RefreshRank("player-a", 4);
        if (pets.For("player-a") is not { Rank: 4 } ranked || ranked.Health <= before)
            faults.Add("a rank boundary did not update the active pet's health snapshot");

        using var bytes = new MemoryStream();
        using (var into = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true)) pets.Write(into);
        bytes.Position = 0;
        var back = new CompanionService();
        using (var from = new BinaryReader(bytes, System.Text.Encoding.UTF8, true))
            if (back.Read(from) is { } why) faults.Add(why);
        if (back.All.Count != pets.All.Count || back.For("player-a")?.Rank != 4)
            faults.Add("companions did not survive a stable-identity save round trip");

        var rifts = new GatewayRiftService();
        var rift = rifts.Open("player-a", 1, Vector3.Zero, 0f, 10f)!;
        var moved = Vector3.Zero;
        var entered = rifts.TryEnter(rift, "player-b", new Vector3(0, rift.Height / 2, 0),
            _ => new Vector3(8, 4, 8), _ => true, at => moved = at, out _);
        var replay = rifts.TryEnter(rift, "player-b", new Vector3(0, rift.Height / 2, 0),
            _ => new Vector3(9, 4, 9), _ => true, at => moved = at, out _);
        if (!entered || replay || moved != new Vector3(8, 4, 8))
            faults.Add("Gateway Rift entry was not safe, per-entrant and idempotent");
        if (rifts.TryEnter(rift, "unbound", new Vector3(0, rift.Height / 2, 0),
                _ => null, _ => true, _ => { }, out _) )
            faults.Add("Gateway Rift moved a player without a bind");
    }

    private static void CheckControls(List<string> faults)
    {
        var keys = Bindings.Defaults();
        if (keys.Primary(GameAction.SpellCursor).Length == 0) faults.Add("the held spell cursor has no default key");
        if (keys.Primary(GameAction.ToggleDeveloper) != "GraveAccent")
            faults.Add("developer flight is not on the tilde/grave key");
        var pad = ControllerBindings.Defaults();
        if (pad.Control(ControllerAction.SpellBankLeft) != ControllerControl.LeftTrigger
            || pad.Control(ControllerAction.SpellBankRight) != ControllerControl.RightTrigger
            || pad.Control(ControllerAction.BreakOrAttack) != ControllerControl.RightShoulder
            || pad.Control(ControllerAction.UseOrPlace) != ControllerControl.LeftShoulder
            || pad.Control(ControllerAction.RaiseShield) != ControllerControl.RightStick
            || pad.Control(ControllerAction.ToggleView) != ControllerControl.Back)
            faults.Add("the spell-ready controller preset is incomplete");
        faults.AddRange(keys.Faults().Select(one => "keyboard: " + one));
        faults.AddRange(pad.Faults().Select(one => "controller: " + one));
    }
}
