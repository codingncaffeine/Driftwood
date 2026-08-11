using System.Text;
using System.Text.Json;

namespace Driftwood.Core.Magic;

/// <summary>Deterministic Core-to-wiki export. No public rank number is maintained by hand.</summary>
public static class MagicReference
{
    public static IReadOnlyList<string> Write(string folder, string productVersion)
    {
        Directory.CreateDirectory(folder);
        var files = new List<string>();

        Page("Magic-and-Progression", Index());
        Page("Progression-and-Statistics", Progression());
        Page("Gold-and-the-Lorekeeper", Economy());
        Page("Spellbook-and-Preparation", Spellbook());
        Page("Spell-Effects", Effects());
        Page("Companions-and-Commands", Companions());
        Page("Revive", Special(SpellId.Revive,
            "Revive only accepts a dead allied player. It never targets a living player, a creature, or the caster while alive. The host returns the ally once at the safe loaded death position or nearest safe point. The current single-player build has no eligible ally; this host-authority contract becomes reachable through P12 co-op."));
        Page("Gateway-Rift", Special(SpellId.GatewayRift,
            "Each entrant resolves their own bed or bind. Missing, unsafe, or unloaded destinations refuse without moving the player. A rift records each entrant once and temporary rifts do not persist through reload."));
        foreach (var group in Enum.GetValues<SpellGroup>()) Page(GroupTitle(group).Replace(' ', '-'), Group(group));

        var export = new
        {
            schema = 1,
            gameVersion = productVersion,
            levelCap = CharacterProgression.MaximumLevel,
            preparedCapacity = CharacterProgression.PreparedCapacity,
            rankFormula = "min(4, 1 + floor((level - 1) / 5))",
            spells = SpellCatalogue.All.Select(spell => new
            {
                id = spell.StableName,
                name = spell.DisplayName,
                group = GroupTitle(spell.Group),
                target = Target(spell.Target),
                delivery = spell.Delivery.ToString().ToLowerInvariant(),
                priceCopper = spell.Price,
                description = spell.Description,
                flavour = spell.Flavour,
                wiki = spell.WikiSlug,
                icon = spell.IconKey,
                audio = spell.AudioKey,
                tags = spell.Tags,
                ranks = spell.Ranks.Select((rank, i) => new
                {
                    rank = i + 1, rank.Primary, rank.Secondary, rank.Duration, rank.Focus,
                    rank.Cooldown, rank.CastTime, rank.Range,
                }),
            }),
            companions = CompanionService.Definitions.Select(pet => new
            {
                kind = pet.Kind.ToString(), spell = SpellCatalogue.ById(pet.Spell).StableName,
                pet.Name, role = pet.Role.ToString(), pet.Height, pet.Speed, pet.Reach,
                pet.Health, pet.Damage, pet.Toughness,
            }),
        };
        var json = Path.Combine(folder, "magic-reference.json");
        File.WriteAllText(json, JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
        files.Add(json);
        return files;

        void Page(string name, string body)
        {
            var path = Path.Combine(folder, name + ".md");
            var header = $"<!-- Generated from Driftwood Core {productVersion}; edit mechanics in SpellCatalogue.cs. -->\n";
            var banner = $"> **Development reference — Driftwood {productVersion}.** Values below are generated from the live game registry.\n\n";
            File.WriteAllText(path, header + banner + body.TrimEnd() + Environment.NewLine, new UTF8Encoding(false));
            files.Add(path);
        }
    }

    /// <summary>Coverage, staleness and link checks for an exported wiki reference.</summary>
    public static List<string> Faults(string folder, string productVersion)
    {
        var faults = new List<string>();
        var pages = new[]
        {
            "Magic-and-Progression", "Progression-and-Statistics", "Gold-and-the-Lorekeeper",
            "Spellbook-and-Preparation", "Spell-Effects", "Companions-and-Commands", "Revive",
            "Gateway-Rift", "Beacon-Rites", "Gravecalling", "Tidecalling", "Arcanistry",
        };
        foreach (var page in pages)
        {
            var path = Path.Combine(folder, page + ".md");
            if (!File.Exists(path)) { faults.Add($"missing generated wiki page {page}"); continue; }
            var text = File.ReadAllText(path);
            if (!text.Contains($"Driftwood Core {productVersion}", StringComparison.Ordinal)
                || !text.Contains($"Driftwood {productVersion}", StringComparison.Ordinal))
                faults.Add($"{page} has a stale or missing build banner");
            if (text.Contains("TODO", StringComparison.OrdinalIgnoreCase)
                || text.Contains("TBD", StringComparison.OrdinalIgnoreCase)
                || text.Contains("placeholder", StringComparison.OrdinalIgnoreCase))
                faults.Add($"{page} contains placeholder writing");
        }

        foreach (var spell in SpellCatalogue.All)
        {
            var group = Path.Combine(folder, GroupTitle(spell.Group).Replace(' ', '-') + ".md");
            if (!File.Exists(group)) continue;
            var text = File.ReadAllText(group);
            if (Count(text, $"## {spell.DisplayName}") != 1
                || !text.Contains(spell.Description, StringComparison.Ordinal)
                || !text.Contains("### How to use", StringComparison.Ordinal))
                faults.Add($"{spell.DisplayName} lacks one generated description/practical entry");
        }

        var linked = new[]
        {
            "Progression-and-Statistics", "Gold-and-the-Lorekeeper", "Spellbook-and-Preparation",
            "Spell-Effects", "Companions-and-Commands", "Beacon-Rites", "Gravecalling",
            "Tidecalling", "Arcanistry", "Revive", "Gateway-Rift",
        };
        var index = Path.Combine(folder, "Magic-and-Progression.md");
        if (File.Exists(index))
        {
            var text = File.ReadAllText(index);
            foreach (var page in linked)
                if (!text.Contains($"[[{page.Replace('-', ' ')}]]", StringComparison.Ordinal))
                    faults.Add($"magic index does not link {page}");
        }

        var jsonPath = Path.Combine(folder, "magic-reference.json");
        if (!File.Exists(jsonPath)) faults.Add("missing magic-reference.json");
        else
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(jsonPath));
                var root = json.RootElement;
                var spells = root.GetProperty("spells");
                if (root.GetProperty("gameVersion").GetString() != productVersion
                    || spells.GetArrayLength() != SpellCatalogue.All.Count)
                    faults.Add("the JSON export has a stale version or non-19 spell count");
                foreach (var spell in spells.EnumerateArray())
                    if (spell.GetProperty("ranks").GetArrayLength() != 4)
                        faults.Add($"{spell.GetProperty("name").GetString()} exported a non-four rank table");
            }
            catch (Exception fault) { faults.Add("magic-reference.json does not parse: " + fault.Message); }
        }
        return faults;

        static int Count(string text, string value)
        {
            var count = 0;
            for (var at = 0; (at = text.IndexOf(value, at, StringComparison.Ordinal)) >= 0; at += value.Length)
                count++;
            return count;
        }
    }

    private static string Index() =>
        """
        # Magic and progression

        Driftwood uses one classless character level and four open spell groups. Every character may learn every spell and mix spells from any group. The initial maximum level is 20.

        Spell rank is automatic: Rank I at levels 1–5, Rank II at 6–10, Rank III at 11–15, and Rank IV at 16–20. Ranks are never bought. All 19 spells are visible from the beginning at one Lorekeeper and each spell is purchased once with gold.

        A character may own all 19 spells but prepare no more than eight. Four summoning spells share a hard one-active-pet limit.

        ## Guides

        - [[Progression and Statistics]]
        - [[Gold and the Lorekeeper]]
        - [[Spellbook and Preparation]]
        - [[Spell Effects]]
        - [[Companions and Commands]]
        - [[Beacon Rites]]
        - [[Gravecalling]]
        - [[Tidecalling]]
        - [[Arcanistry]]
        - [[Revive]]
        - [[Gateway Rift]]
        """;

    private static string Progression()
    {
        var sb = new StringBuilder("# Progression and statistics\n\n");
        sb.AppendLine("Experience comes from host-settled creature contribution, discoveries, encounters, and bounded survival milestones. Repeating an event cannot pay twice. Ordinary death never removes a level.");
        sb.AppendLine();
        sb.AppendLine("| Levels | Automatic rank | Next boundary |");
        sb.AppendLine("| --- | --- | --- |");
        sb.AppendLine("| 1–5 | Rank I | Level 6 |");
        sb.AppendLine("| 6–10 | Rank II | Level 11 |");
        sb.AppendLine("| 11–15 | Rank III | Level 16 |");
        sb.AppendLine("| 16–20 | Rank IV | Maximum level |");
        sb.AppendLine();
        sb.AppendLine("Might supports weapon damage, Finesse supports critical chance and haste, Insight supports Focus and Spell Potency, and Resolve supports health and armour. The character sheet and combat read the same derived-stat snapshot.");
        return sb.ToString();
    }

    private static string Economy() =>
        """
        # Gold and the Lorekeeper

        Coin value is stored per stable player identity as a signed 64-bit total in the smallest denomination: 100 copper is one silver and 100 silver is one gold. Coin pickups settle directly into the wallet; physical `gold_ingot` remains a crafting material.

        Sable, the Lorekeeper in a Driftstead, shows all 19 spells to a fresh level-one character. Gold and already-learned state are the only ordinary purchase checks. A successful purchase debits once and permanently records the spell's stable name. Spell ranks advance with character level at no further cost.

        P14 trade tokens remain the distinct currency for the three ordinary resident professions. They do not buy spells and are not silently converted into gold.

        The first generated chest at each authored site also offers one personal gear cache. Buried Galleries and Driftsteads use Rank-I materials, Tidewrecks Rank II, Storm Vaults Rank III, and the Starfall Crown Rank IV. The item is deterministic, cannot be rerolled, and waits if the player's inventory is full.
        """;

    private static string Spellbook() =>
        """
        # Spellbook and preparation

        The spellbook holds every learned spell. Up to eight learned spells may be prepared in a separate spell bar; swapping is free while no cast or channel is active and does not erase cooldowns, effects, or a companion.

        Three named memory loadouts can record and swap the complete eight-slot bar. An empty loadout is saved with Enter; a saved loadout applies with Enter and can be overwritten with Left or right-click.

        On keyboard, hold the rebindable **spell cursor** action to freeze mouse-look while movement stays live, then click one of the eight icons. Releasing the action without clicking casts nothing and world mouse actions are consumed while it is open.

        On controller, hold the left trigger for prepared slots 1–4 or the right trigger for slots 5–8, then press the corresponding face button. Trigger release alone does nothing. If both banks are held, the most recently pressed trigger wins.

        All spells use renewable Focus, simulation-time cooldowns, range and line-of-sight preflight. Invalid targets spend nothing. Driftwood uses no spell reagents, ammunition, class gates, purchased ranks, or appearance-changing spell forms.

        The spellbook and eight-slot spell bar have decorated movable frames. Hold the spell cursor for the world-space bar. Drag an unlocked title/grip to move it; right-click either frame to lock or unlock it. A drag attempted while locked opens the unlock action instead of failing silently. New layouts begin unlocked, and all positions and lock choices are remembered between sessions.
        """;

    private static string Effects() =>
        """
        # Spell effects

        Burning, healing over time, Root, Fear, Leech, Snare, and Holy Shield use one authoritative effect service. Each effect records its kind, target, source player, rank snapshot, magnitude, remaining simulation time, tick cadence, faction and dispel family.

        Refresh and stacking are deterministic. Holy Shield absorbs its recorded amount. Root immobilizes and may break from meaningful damage; Snare slows without immobilizing; Fear suspends an ordinary attack while the target retreats. Draw Lifeforce channels direct damage and Leech ticks damage over time. Either drain heals its caster only for damage the target actually accepted—immune, dead, or cleared targets cannot create healing.
        """;

    private static string Companions()
    {
        var sb = new StringBuilder("# Companions and commands\n\n");
        sb.AppendLine("Summon Bones, Animate Zombie, Spirit Wolf, and Earth Elemental share one commanded-companion slot per player. A successful summon replaces the previous pet only after the new summon is accepted. Failed or interrupted summons leave the current pet intact. Wild versions never share pet drops, XP, coins, or spawn receipts.");
        sb.AppendLine();
        sb.AppendLine("| Companion | Role | Rank I health | Rank IV health | Rank I damage | Rank IV damage |");
        sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: |");
        foreach (var pet in CompanionService.Definitions)
            sb.AppendLine($"| {pet.Name} | {pet.Role} | {pet.Health[0]} | {pet.Health[3]} | {pet.Damage[0]} | {pet.Damage[3]} |");
        sb.AppendLine();
        sb.AppendLine("- **Attack** engages the legal hostile under the owner's crosshair.");
        sb.AppendLine("- **Guard** anchors here, engages nearby hostiles, then returns until its owner breaks the catch-up leash.");
        sb.AppendLine($"- **Follow** clears guard/stay and trails the owner at about {CompanionService.FollowDistance:F0} blocks rather than crowding their feet.");
        sb.AppendLine("- **Stay** clears the target and waits passively. This is the only order that disables owner catch-up.");
        sb.AppendLine("- **Go Away** dismisses the pet without corpse, loot, coins, or XP.");
        sb.AppendLine();
        sb.AppendLine("The non-modal panel shows portrait, name, role, Rank I–IV, exact health bar and numbers, and current command. Earth Elemental is an original irregular stone biped about half player height; Spirit Wolf has a black-and-grey coat and bright blue eyes.");
        sb.AppendLine();
        sb.AppendLine($"Hold the spell cursor and right-click the companion panel for its lock option. Drag its decorated title strip while unlocked; trying while locked opens that option instead of failing silently. Its bounded position is remembered. Guard and Follow automatically engage nearby hostiles, and a companion close to an incoming melee blow can intercept it. Unless told to Stay, a pet more than {CompanionService.CatchUpDistance:F0} blocks from its owner teleports to loaded, standable space beside them; the leash measures vertical cave separation as well as horizontal distance.");
        return sb.ToString();
    }

    private static string Group(SpellGroup group)
    {
        var sb = new StringBuilder($"# {GroupTitle(group)}\n\n");
        sb.AppendLine("This is an open spellbook group, not a class. Every spell is available from the Lorekeeper at level 1 and automatically uses the owner's current rank.");
        foreach (var spell in SpellCatalogue.All.Where(one => one.Group == group))
        {
            sb.AppendLine();
            sb.AppendLine($"## {spell.DisplayName}");
            sb.AppendLine();
            sb.AppendLine(spell.Description);
            sb.AppendLine();
            sb.AppendLine($"*{spell.Flavour}*");
            sb.AppendLine();
            sb.AppendLine($"Target: {Target(spell.Target)} · Delivery: {spell.Delivery.ToString().ToLowerInvariant()} · Price: {CharacterProgression.CoinsText(spell.Price)} · Tags: {string.Join(", ", spell.Tags)}");
            sb.AppendLine();
            sb.AppendLine("### How to use");
            sb.AppendLine();
            sb.AppendLine(Practical(spell.Id));
            sb.AppendLine();
            RankTable(sb, spell);
        }
        return sb.ToString();
    }

    private static string Special(SpellId id, string practical)
    {
        var spell = SpellCatalogue.ById(id);
        var sb = new StringBuilder($"# {spell.DisplayName}\n\n{spell.Description}\n\n{practical}\n\n");
        RankTable(sb, spell);
        return sb.ToString();
    }

    private static void RankTable(StringBuilder sb, SpellDefinition spell)
    {
        sb.AppendLine($"| Rank | {spell.Mechanic} | Secondary | Duration | Focus | Cast | Cooldown | Range |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        for (var i = 0; i < spell.Ranks.Length; i++)
        {
            var rank = spell.Ranks[i];
            sb.AppendLine($"| {CharacterProgression.RankName(i + 1)} | {rank.Primary} | {rank.Secondary} | {Seconds(rank.Duration)} | {rank.Focus} | {Seconds(rank.CastTime)} | {Seconds(rank.Cooldown)} | {rank.Range:0.#} |");
        }
    }

    private static string Seconds(float value) => value <= 0f ? "—" : $"{value:0.#}s";
    private static string Target(SpellTarget target) => target switch
    {
        SpellTarget.Self => "self",
        SpellTarget.SelfOrAlly => "self or ally",
        SpellTarget.DeadAlly => "dead ally",
        SpellTarget.Hostile => "hostile",
        _ => "ground",
    };
    private static string Practical(SpellId id) => id switch
    {
        SpellId.HolyMight => "Use this as Beacon Rites' dependable ranged attack. Root can hold a dangerous target still before repeated strikes, while Holy Shield buys time to finish it.",
        SpellId.QuickHeal => "A short cast makes this best when you have created a little space. Shield first when pressure is high; Tree of Life is better when steady healing will be enough.",
        SpellId.Revive => "Aim at a dead allied player within range. The cast refuses living targets, creatures, the caster, missing death records, and unsafe return positions without spending an extra settlement.",
        SpellId.HolyShield => "Cast before an expected heavy hit or before committing to a channel. It absorbs a fixed rank-scaled pool rather than reducing damage forever.",
        SpellId.Root => "Root stops movement but meaningful incoming damage can break it, so use it to reposition, escape, heal, or isolate a target rather than as a permanent stun.",
        SpellId.SummonBones => "Bones is the nimble striker of the four pets. Use Attack for a chosen target or Follow when you want it to defend you while travelling.",
        SpellId.AnimateZombie => "The zombie is slower and tougher than Bones. Guard is especially useful at a doorway or work site where its toughness can absorb nearby attacks.",
        SpellId.Fear => "Fear interrupts an ordinary attack and forces a short retreat. Use the opening to heal, begin Draw Lifeforce, or separate one threat from a group.",
        SpellId.DrawLifeforce => "Maintain line of sight through the channel. Every accepted damage tick heals you; an immune, dead, or lost target produces no free healing.",
        SpellId.Leech => "Apply Leech early in a durable fight, then act while it ticks. It pairs well with Fear or Snare because the drain keeps working while distance opens.",
        SpellId.LightningStreak => "This is Tidecalling's immediate single-target strike: no projectile travel and no cast wind-up. It is useful for a fast finish or a mobile opening hit.",
        SpellId.Ignite => "Ignite deals an immediate scorch and then uses the shared burning effect. Apply it early enough for the full burn duration, then change targets or control the fight.",
        SpellId.TreeOfLife => "Use this before or during sustained pressure. Its healing arrives over time, so Quick Heal remains the stronger answer to an immediate health emergency.",
        SpellId.SpiritWolf => "The fastest companion is suited to pursuit and Follow defense. Its speed helps it reach a hostile before the owner has to stand toe-to-toe.",
        SpellId.IceShock => "An instant cold strike with no projectile, useful while moving or when terrain would make a bolt awkward. Snare adds control when damage alone is not enough.",
        SpellId.FireBolt => "Fire Bolt is a real projectile: lead moving targets and keep walls, allies, and range in mind. Root or Snare makes its travel easier to judge.",
        SpellId.GatewayRift => "Place the aperture on clear ground, then walk into its visible ellipse. Each entrant goes to their own bed or bind; no valid safe bind means no movement.",
        SpellId.Snare => "Snare leaves a hostile able to move but at a reduced percentage. Use it for kiting, safer projectile shots, or enough room to complete a cast.",
        _ => "The compact Earth Elemental is the defender pet: slower, tougher, and easier to keep between its owner and an attacker. Guard emphasizes that role.",
    };
    private static string GroupTitle(SpellGroup group) => group switch
    {
        SpellGroup.BeaconRites => "Beacon Rites",
        SpellGroup.Gravecalling => "Gravecalling",
        SpellGroup.Tidecalling => "Tidecalling",
        _ => "Arcanistry",
    };
}
