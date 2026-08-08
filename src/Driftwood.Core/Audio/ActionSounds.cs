namespace Driftwood.Core.Audio;

/// <summary>
/// The sounds of doing things — every one-shot that is neither a block surface nor a creature.
/// </summary>
/// <remarks>
/// <para>The same our-name / their-file shape as <see cref="MaterialSounds"/> and
/// <c>CreatureSounds</c>, and the third leg of the audio check: every name here is proved to be on
/// disk and to decode, because a table pointing at a sound nobody shipped is silent in exactly the
/// way a working game is silent.</para>
/// <para><see cref="Ambience"/> is listed apart because the length gate differs: a cave murmur or
/// an underwater drone is <i>supposed</i> to run long, where a door that creaks for fifteen seconds
/// is a broken export. One-shots are gated at eight seconds, ambience at sixty.</para>
/// </remarks>
public static class ActionSounds
{
    private static string[] Run(string stem, int count)
    {
        var names = new string[count];
        for (var i = 0; i < count; i++) names[i] = $"{stem}{i + 1}";
        return names;
    }

    // ── Doors and lids ─────────────────────────────────────────────────────────────────────────
    // The pack numbers variants from the second file on: open, open2, open3…

    public static readonly string[] DoorOpen =
        ["block/wooden_door/open", "block/wooden_door/open2", "block/wooden_door/open3", "block/wooden_door/open4"];

    public static readonly string[] DoorClose =
    [
        "block/wooden_door/close", "block/wooden_door/close2", "block/wooden_door/close3",
        "block/wooden_door/close4", "block/wooden_door/close5", "block/wooden_door/close6",
    ];

    public static readonly string[] ChestOpen = ["block/chest/open"];
    public static readonly string[] ChestClose = ["block/chest/close", "block/chest/close2", "block/chest/close3"];
    public static readonly string[] BarrelOpen = ["block/barrel/open1", "block/barrel/open2"];
    public static readonly string[] BarrelClose = ["block/barrel/close"];

    // ── Fires and the stations that burn ───────────────────────────────────────────────────────

    /// <summary>Each lit smelter crackles in its own voice; the campfire has the softest.</summary>
    public static readonly string[] FurnaceCrackle = Run("block/furnace/fire_crackle", 5);
    public static readonly string[] BlastFurnaceCrackle = Run("block/blastfurnace/blastfurnace", 5);
    public static readonly string[] SmokerCrackle = Run("block/smoker/smoker", 5);
    public static readonly string[] CampfireCrackle = Run("block/campfire/crackle", 6);

    public static readonly string[] FireIgnite = ["fire/ignite"];
    public static readonly string[] FireOut = Run("fire/on/off", 3);
    public static readonly string[] Fizz = ["random/fizz"];

    // ── The player's own verbs ─────────────────────────────────────────────────────────────────

    /// <summary>A tool giving up mid-swing.</summary>
    public static readonly string[] ToolBreaks = Run("item/break/break", 4);

    /// <summary>The end of a good meal, played only when the bar tops out.</summary>
    public static readonly string[] Burp = ["random/burp"];

    public static readonly string[] Till = Run("item/hoe/till", 4);
    public static readonly string[] Harvest = Run("item/plant/harvest", 4);

    /// <summary>Berries coming off the bush, which stays standing.</summary>
    public static readonly string[] BerryPick = Run("item/sweet_berries/pick_from_bush", 2);

    public static readonly string[] BucketFillWater = Run("item/bucket/fill", 3);
    public static readonly string[] BucketEmptyWater = Run("item/bucket/empty", 3);
    public static readonly string[] BucketFillLava = Run("item/bucket/fill_lava", 3);
    public static readonly string[] BucketEmptyLava = Run("item/bucket/empty_lava", 3);

    public static readonly string[] AnvilUse = ["item/anvil/anvil_use2", "item/anvil/anvil_use3"];

    /// <summary>The bin's four moments: a helping in, the level rising, done, and emptied.</summary>
    public static readonly string[] ComposterFill = Run("block/composter/fill", 4);
    public static readonly string[] ComposterRaise = Run("block/composter/fill_success", 4);
    public static readonly string[] ComposterReady = Run("block/composter/ready", 4);
    public static readonly string[] ComposterEmpty = Run("block/composter/empty", 3);

    /// <summary>Falls that cost hearts, keyed to what was landed on where the pack has it.</summary>
    public static readonly string[] FallSmall = ["damage/fallsmall"];
    public static readonly string[] FallBig = ["damage/fallbig1", "damage/fallbig2"];

    private static readonly Dictionary<SoundMaterial, string[]> FallBigOn = new()
    {
        [SoundMaterial.Glass] = ["damage/fall_type/fallbig1_glass", "damage/fall_type/fallbig2_glass"],
        [SoundMaterial.Grass] = ["damage/fall_type/fallbig1_grass", "damage/fall_type/fallbig2_grass"],
        [SoundMaterial.Gravel] = ["damage/fall_type/fallbig1_gravel", "damage/fall_type/fallbig2_gravel"],
        [SoundMaterial.Sand] = ["damage/fall_type/fallbig1_sand", "damage/fall_type/fallbig2_sand"],
        [SoundMaterial.Snow] = ["damage/fall_type/fallbig1_snow", "damage/fall_type/fallbig2_snow"],
        [SoundMaterial.Stone] = ["damage/fall_type/fallbig1_stone", "damage/fall_type/fallbig2_stone"],
        [SoundMaterial.Deepstone] = ["damage/fall_type/fallbig1_stone", "damage/fall_type/fallbig2_stone"],
        [SoundMaterial.Wood] = ["damage/fall_type/fallbig1_wood", "damage/fall_type/fallbig2_wood"],
    };

    /// <summary>A hard landing on a surface the pack recorded, or the plain thud where not.</summary>
    public static string[] FallBigFor(SoundMaterial ground) => FallBigOn.GetValueOrDefault(ground, FallBig);

    public static readonly string[] DrownGasp = Run("entity/player/hurt/drown", 4);
    public static readonly string[] BurnHurt = Run("entity/player/hurt/fire_hurt", 3);

    /// <summary>One breath bubble giving up.</summary>
    public static readonly string[] BubblePop = ["ui/hud/hud_bubble"];

    /// <summary>The head going under and coming back up.</summary>
    public static readonly string[] Submerge = Run("ambient/underwater/enter", 3);
    public static readonly string[] Surface = Run("ambient/underwater/exit", 3);

    public static readonly string[] LadderStep = Run("step/ladder", 5);
    public static readonly string[] SwimStroke = Run("liquid/swim", 18);

    /// <summary>Something small landing in the pockets.</summary>
    public static readonly string[] Pickup = ["random/pop", "random/pop2", "random/pop3"];

    // ── Interface ──────────────────────────────────────────────────────────────────────────────

    public static readonly string[] Click = ["random/click"];
    public static readonly string[] ToastIn = ["ui/toast/in"];
    public static readonly string[] ToastOut = ["ui/toast/out"];

    // ── The world's own noises ─────────────────────────────────────────────────────────────────

    public static readonly string[] LavaPop = ["liquid/lavapop", "liquid/lavapop2", "liquid/lavapop3", "liquid/lavapop4"];

    /// <summary>
    /// The long recordings: cave murmurs for the dark underground, lava for the Emberdeep. Gated
    /// at sixty seconds where everything else is gated at eight.
    /// </summary>
    /// <remarks>
    /// ⚠ The pack's underwater "additions" are not here on purpose: they are wide-stereo beds
    /// whose two channels all but cancel in the fold to mono — measured at peak 0.006 played
    /// against 0.05 on disk — and a positional engine has no other way to carry them. The fold
    /// gate in the audio check is what found them.
    /// </remarks>
    public static readonly string[] CaveAmbience = Run("ambient/cave/cave", 23);
    public static readonly string[] LavaAmbience = ["liquid/lava", "liquid/lava2", "liquid/lava3"];

    /// <summary>Names allowed to run long — see the class remarks.</summary>
    public static IEnumerable<string> Ambience => CaveAmbience.Concat(LavaAmbience);

    /// <summary>Every one-shot named here, for the check that they all resolve and stay short.</summary>
    public static IEnumerable<string> AllOneShots
    {
        get
        {
            var groups = new[]
            {
                DoorOpen, DoorClose, ChestOpen, ChestClose, BarrelOpen, BarrelClose,
                FurnaceCrackle, BlastFurnaceCrackle, SmokerCrackle, CampfireCrackle,
                FireIgnite, FireOut, Fizz,
                ToolBreaks, Burp, Till, Harvest,
                BucketFillWater, BucketEmptyWater, BucketFillLava, BucketEmptyLava,
                AnvilUse, ComposterFill, ComposterRaise, ComposterReady, ComposterEmpty,
                FallSmall, FallBig, DrownGasp, BurnHurt, BubblePop,
                Submerge, Surface, LadderStep, SwimStroke, Pickup,
                Click, ToastIn, ToastOut, LavaPop,
            };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            foreach (var name in group)
                if (seen.Add(name)) yield return name;

            foreach (var fall in FallBigOn.Values)
            foreach (var name in fall)
                if (seen.Add(name)) yield return name;
        }
    }
}
