using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Driftwood.Client.Diagnostics;
using Driftwood.Client.Audio;
using Driftwood.Client.Platform;
using Driftwood.Core.Audio;
using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;
using Driftwood.Core.Gen;
using Driftwood.Core.Items;
using Driftwood.Core.Lighting;
using Driftwood.Core.Meshing;
using Driftwood.Core.Particles;
using Driftwood.Core.Saves;
using Driftwood.Core.Settings;
using Driftwood.Core.Physics;
using Driftwood.Core.Sky;
using Driftwood.Core.Spatial;
using Driftwood.Core.Textures;
using Driftwood.Core.Ui;
using Driftwood.Core.World;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace Driftwood.Client.Render;

public sealed record ClientOptions
{
    public WorldSeed Seed { get; init; } = WorldSeed.Random();
    public int ChunksAcross { get; init; } = 16;

    /// <summary>
    /// Which world to open, and the name its save file has.
    /// </summary>
    /// <remarks>
    /// <para>Null means the seed names it: <c>--seed tidefall</c> opens the world that seed makes
    /// and keeps opening the same one, and no seed at all opens plain "world". <b>So quitting and
    /// starting again comes back to where you were</b>, which is the behaviour anybody would expect
    /// and the reason the default is not a fresh name every launch.</para>
    /// <para>⚠ <b>A world that already exists brings its own seed</b>, and it beats
    /// <see cref="Seed"/> — the terrain under somebody's house cannot be regenerated from a
    /// different number and still be their world. Saying so out loud at startup, because a
    /// <c>--seed</c> that appears to do nothing is otherwise a mystery.</para>
    /// </remarks>
    public string? WorldName { get; init; }

    /// <summary>
    /// True when <c>--seed</c> was actually given, so it can name the world.
    /// </summary>
    /// <remarks>
    /// The same distinction <see cref="ChunksGiven"/> exists for, and for the same reason: a
    /// default is indistinguishable from a choice once it is in the field. Without it, the random
    /// seed a bare launch draws would name a different world every time and nobody would ever open
    /// the same one twice.
    /// </remarks>
    public bool SeedGiven { get; init; }

    /// <summary>
    /// The seed exactly as it was typed, for naming the world after it.
    /// </summary>
    /// <remarks>
    /// <see cref="WorldSeed"/> hashes anything that is not a number, so <c>--seed stonebreak</c>
    /// becomes 6993295270277999969 and there is no way back. A world called
    /// "world-6993295270277999969" is one nobody recognises in a list, and the list is the whole
    /// point of saving.
    /// </remarks>
    public string? SeedText { get; init; }

    /// <summary>
    /// True when <c>--chunks</c> was actually given, so it beats the saved view distance.
    /// </summary>
    /// <remarks>
    /// A command line is somebody saying what they want right now and a settings file is what they
    /// wanted last time; the first wins. Without knowing which of the two the number came from, a
    /// default indistinguishable from a choice would silently overrule the screen.
    /// </remarks>
    public bool ChunksGiven { get; init; }

    /// <summary>
    /// Open a screen by itself, read the pixels back off the framebuffer, and report them.
    /// </summary>
    /// <remarks>
    /// The instrument this needed. "It is not appearing" and "it is appearing and I cannot see it"
    /// have the same symptom and completely different causes, and everything short of reading the
    /// framebuffer only proves the geometry was <em>built</em> — which it was, throughout a fault
    /// where nothing reached the screen. Numbers off the front buffer are the only thing that
    /// settles it, and they need no screenshot and no eyes.
    /// </remarks>
    public bool UiCheck { get; init; }

    /// <summary>Share of the surface at or below sea level, 0..0.9.</summary>
    public float OceanCoverage { get; init; } = TerrainGenerator.DefaultOceanCoverage;

    public bool VSync { get; init; }
    public int Width { get; init; } = 1600;
    public int Height { get; init; } = 900;

    /// <summary>A texture pack folder or .zip to import block textures from, or null for our own.</summary>
    public string? PackPath { get; init; }

    /// <summary>Tile resolution the texture array is built at.</summary>
    /// <summary>
    /// Tile size to build the texture array at, or 0 to take whatever the pack is painted at.
    /// </summary>
    /// <remarks>
    /// Zero by default. This used to be sixteen, so importing a 512-pixel pack without also saying
    /// <c>--texture-size 512</c> squashed every texture in it down to a sixteenth of its width —
    /// the import worked, and what came out looked like a bad copy of the pack somebody had just
    /// chosen. A player who picked a pack has already said what resolution they want.
    /// </remarks>
    public int TextureSize { get; init; }

    /// <summary>A skin PNG to wear, or null for Driftwood's own.</summary>
    public string? SkinPath { get; init; }

    /// <summary>
    /// A folder of creature skeletons, or null for no creatures at all.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>It has to be passed in and it has to be remembered.</b> The skeletons ship with an
    /// installed Bedrock client rather than with a texture pack, and that install cannot be found by
    /// looking: enumerating <c>WindowsApps</c> throws for a plain process even where a known path
    /// under it opens perfectly. So the folder is given once with <c>--creature-geometry</c> and
    /// kept in the settings file, which is also where the import screen will set it.
    /// </remarks>
    public string? CreatureGeometry { get; init; }

    /// <summary>Arm width, or null to read it out of the sheet.</summary>
    public ArmStyle? Arms { get; init; }

    /// <summary>Seconds of flight to measure; 0 runs the game normally.</summary>
    public double BenchSeconds { get; init; }

    /// <summary>
    /// Seconds to play for and then close the window the way a player would; 0 runs until told.
    /// </summary>
    /// <remarks>
    /// <para>The instrument save-on-quit needed, and there was no other way to get it. Killing the
    /// process proves nothing — a killed process never reaches <c>Shutdown</c>, which is precisely
    /// where the save happens — so "does closing the window keep the world" could not be asked at
    /// all without a way to close the window on purpose.</para>
    /// <para>It is the real game loop and the real exit, not a special path: the world loads, the
    /// clock runs, the streamer streams, and the same <c>Shutdown</c> a player triggers writes the
    /// same file. Only the cursor is left alone, because taking somebody's mouse for four seconds to
    /// run a check is rude and has nothing to do with what is being measured.</para>
    /// </remarks>
    public double PlaySeconds { get; init; }

    /// <summary>
    /// Milliseconds to burn on every 200th measured frame. This is the benchmark's control: a
    /// stall of known size, injected on purpose, so the report can be checked against a fault it
    /// is known to contain. A frame-time instrument nobody has ever shown a hitch to is a claim,
    /// not a measurement.
    /// </summary>
    public double StallMs { get; init; }

    /// <summary>
    /// Chunk uploads allowed per frame. Tunable rather than fixed so the benchmark can be shown to
    /// notice when it changes — a frame-time instrument that reports the same numbers whatever the
    /// renderer does is not measuring the renderer.
    /// </summary>
    public int MaxUploadsPerFrame { get; init; } = 4;

    /// <summary>Where in the day to open, 0 to 1 with midnight at zero.</summary>
    public float StartTime { get; init; } = 0.35f;

    /// <summary>Seconds in a full day. Short values are how a sunset gets looked at twice.</summary>
    public float DayLength { get; init; } = SkyClock.DefaultDayLength;

    /// <summary>Opens no audio device at all. The benchmark sets this for itself.</summary>
    public bool Mute { get; init; }

    /// <summary>
    /// A folder to write pictures of the hand into, then quit. Null runs the game normally.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>The instrument the held item needed, and the reason three wrong grips shipped without
    /// anybody noticing.</b> <c>--icon-sheet</c> made a tile lookable-at and six drawings were fixed
    /// in minutes; what was still unlookable-at was the tile <em>in a fist</em> — which is a
    /// projection, a swing and two entirely different arm poses on top of the drawing. So a held
    /// pickaxe was drawn as a cube wearing its picture on all six faces, held two thirds of a block
    /// apart, pointing at the player, and it took the user starting the game to say so.
    /// </remarks>
    public string? ShotPath { get; init; }
}

/// <summary>Where the camera sits relative to the player. Cycled with F5.</summary>
public enum ViewMode
{
    /// <summary>Behind the eyes. The model is not drawn; the arm is.</summary>
    First,

    /// <summary>Over the shoulder, looking the way the player looks.</summary>
    ThirdBehind,

    /// <summary>In front, looking back at them.</summary>
    ThirdFacing,
}

/// <summary>
/// Owns the window, GL context, input and render loop.
/// </summary>
/// <remarks>
/// P0 generates a fixed box of world up front and meshes all of it before the first frame, which
/// keeps the spike honest about steady-state draw cost without needing streaming yet. P1 replaces
/// the up-front pass with a load/mesh queue keyed on camera position; the render loop below does
/// not need to know the difference.
/// </remarks>
public sealed class ClientHost : IDisposable
{
    private readonly ClientOptions _options;
    private readonly IWindow _window;

    private GL _gl = null!;
    /// <summary>
    /// Keyboard and mouse. ⛔ <b>Not the input library's context</b> — see <see cref="RawInput"/>
    /// for the ten seconds that cost.
    /// </summary>
    private RawInput _input = null!;

    private Shader _chunkShader = null!;
    private SkyRenderer _sky = null!;
    private CloudRenderer _clouds = null!;

    /// <summary>The day/night cycle, and the one place the sky's colours come from.</summary>
    private SkyClock _clock = null!;
    private SkyState _skyState;

    /// <summary>Seconds since the world opened, which is what makes the clouds drift.</summary>
    private double _elapsed;

    /// <summary>The world being played, and the name its save file has. Empty means keep nothing.</summary>
    private string _worldName = "";

    /// <summary>
    /// The seed actually in use, which is a loaded world's own rather than the command line's.
    /// </summary>
    /// <remarks>
    /// Held here rather than read off the options every time, because the options are what was
    /// asked for and this is what is true. Terrain, clouds and the climate field all draw from it,
    /// and every one of them has to draw from the same one or a loaded world comes back with its
    /// hills somewhere else.
    /// </remarks>
    private WorldSeed _seed;

    /// <summary>Seconds anybody had already played this world before this session opened it.</summary>
    private double _playedBefore;

    /// <summary>True when this session opened a world that already existed.</summary>
    private bool _loadedWorld;

    /// <summary>Where the sun was when the world was last put down.</summary>
    private float _savedDayTime;

    /// <summary>
    /// True until somebody has chosen to play. The world flies past under the menu.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>The user's own idea, and it is what makes this a screen and a camera rather than an
    /// architecture change.</b> A menu before the world exists would mean a state in which there is
    /// no world, and the host builds a registry, a generator and a streamer in one go. A menu over
    /// a world that is loading anyway needs none of that: the world is the one about to be played,
    /// so the flight is a preview of it rather than a stand-in for one.
    /// </remarks>
    private bool _atStartScreen;

    /// <summary>The circle the camera flies while the menu is up. The benchmark's own path.</summary>
    private BenchPath? _menuPath;

    /// <summary>Seconds into that flight.</summary>
    private double _menuTime;

    /// <summary>True while the menu is showing the list of worlds rather than its four choices.</summary>
    private bool _startListing;

    /// <summary>The row that leaves the settings when they were opened from the menu.</summary>
    private const string BackToMenu = "back to the menu";

    /// <summary>What went wrong the last time the world was written, if anything.</summary>
    private string? _saveFault;

    /// <summary>
    /// Seconds between autosaves, when there is anything to write.
    /// </summary>
    /// <remarks>
    /// Two minutes. Long enough that a session of steady building is not a session of steady disk
    /// writing, and short enough that what a crash costs is a couple of minutes of work rather than
    /// an evening. A save is the seed and a diff, so it is small however long somebody has played.
    /// </remarks>
    private const double AutosaveSeconds = 120;

    /// <summary>Seconds since the last save of any kind.</summary>
    private double _sinceSave;

    /// <summary>How many autosaves this session has taken, for the screen to show.</summary>
    private int _autosaves;

    /// <summary>
    /// Every world on this machine, newest first, as of the last time the screen was opened.
    /// </summary>
    /// <remarks>
    /// Read when the screen opens and after a save, never per frame. The rows are rebuilt every
    /// frame the screen is up, and walking the saves folder sixty times a second to draw a list
    /// that changes twice an hour is a disk read per frame for nothing.
    /// </remarks>
    private List<SaveHeader> _saved = [];

    /// <summary>
    /// Files in that folder that are not worlds this build can show, read at the same moment.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Kept beside the list because an empty list has two causes and they need different
    /// answers.</b> "There is nothing saved" is a new installation; "there is a file here I cannot
    /// read" is somebody's world and a build that will not open it. They read identically on screen
    /// and the second used to say nothing anywhere at all.
    /// </remarks>
    private List<WorldSave.UnreadableSave> _unreadable = [];

    /// <summary>
    /// Walks the saves folder into <see cref="_saved"/> and <see cref="_unreadable"/> together.
    /// </summary>
    /// <remarks>
    /// One way in, because there are six places that want the list and the day one of them fills
    /// only half of it is the day the screen contradicts itself.
    /// </remarks>
    private void ReadSavesFolder() => _saved = WorldSave.List(out _unreadable);

    /// <summary>
    /// Which world's row has been asked to delete once and is waiting to be asked again.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>A row that throws something away must not answer to a direction</b>, and one press
    /// of enter is not enough either. Left and right are how a player walks along every row on this
    /// screen to see what it says, and "forget what has been said" already wiped the persisted
    /// unlock record on either of them once. This answers to enter alone, twice, and the second
    /// press has to land on the same row as the first.</para>
    /// <para>⚠ <b>Cleared by moving off the row</b>, so arming is never a state a player is left in
    /// without seeing it — the row itself is the only place the armed state exists, and looking
    /// somewhere else puts it back.</para>
    /// </remarks>
    private string? _deleteArmed;

    /// <summary>Puts an armed delete back, for anything that moves off the row.</summary>
    private void DisarmDelete() => _deleteArmed = null;

    /// <summary>What was thrown away last, for the row to say so once.</summary>
    private string? _deleted;

    /// <summary>
    /// The seed the menu will start a new world on, or empty for one drawn at random.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>The first box in the game, and #60 left it out for want of one.</b> Typed as a word
    /// rather than a number because that is what <c>--seed</c> already takes and what
    /// <see cref="ClientOptions.SeedText"/> already keeps — the word is what names the world, and
    /// telling somebody their world is 1,748,392,011 is not telling them anything.
    /// </remarks>
    private readonly TextField _seedBox =
        new(32, TextAllows.FileSafe) { Placeholder = "a fresh one" };

    /// <summary>What the box held when it took the keyboard, for escape to put back.</summary>
    private string _typingWas = "";

    /// <summary>What to do when the box gives the keyboard back, and whether it was accepted.</summary>
    private Action<bool>? _typingDone;

    /// <summary>Where the worlds are, which is the answer to most questions about them.</summary>
    private static readonly string SavesFolderNote = $"they live in {WorldSave.Folder}";
    private readonly Dictionary<ChunkPos, ChunkMeshGpu> _meshes = [];
    private readonly FlyCamera _camera = new();
    private WorldStreamer _streamer = null!;
    private int _viewRadius;

    private PlayerBody _player = null!;
    private BlockOutline _outline = null!;
    private BlockTextureArray _blockTextures = null!;
    private BlockTextureSet.Result _textures = null!;

    /// <summary>The layers that move. Ticked once a frame; uploads only when a frame changes.</summary>
    private TextureAnimator _animatedTextures = null!;
    private bool[] _targetable = null!;

    private PlayerRenderer _playerRenderer = null!;

    /// <summary>Null on a machine with no creature geometry, which is the ordinary case.</summary>
    private CreatureRenderer? _creatureRenderer;
    private CreatureHerd? _herd;

    /// <summary>Seconds until the herd is looked at again. See <see cref="TopUpCreatures"/>.</summary>
    private float _creatureTopUp;

    /// <summary>Animals a loaded save carried, waiting for the herd to exist and take them.</summary>
    private readonly List<CreatureHerd.SavedCreature> _savedCreatures = [];

    private BlockCracks _cracks = null!;
    private readonly PlayerAnimator _animator = new();
    private readonly PlayerMining _mining = new();
    private bool[] _solid = null!;
    private BlockRegistry _registry = null!;

    /// <summary>Where the camera renders from, which is the eye only in first person.</summary>
    private Vector3 _viewPosition;
    private Vector3 _viewForward = Vector3.UnitX;

    private ViewMode _view = ViewMode.First;

    /// <summary>
    /// Whether a strike button is still down. Holding one keeps the arm swinging, and every swing
    /// takes a block — so the cadence of mining is the cadence of the animation rather than of the
    /// mouse. Break wins if both are held.
    /// </summary>
    private bool _holdingBreak;
    private bool _holdingPlace;
    private bool _lastStrikeWasBreak = true;

    /// <summary>The block under the crosshair, if anything is in reach.</summary>
    private RayHit? _target;

    /// <summary>
    /// The creature under the crosshair, when one is standing in front of whatever block is.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The two are mutually exclusive, and that is the whole of the rule.</b> Resolved in
    /// <see cref="UpdateTarget"/>, which is the one place that has both distances — an animal nearer
    /// than the block takes the aim outright, so mining stops, the outline goes, and the swing lands
    /// on the cow rather than on the hill behind it. Written as "the nearer wins" rather than as two
    /// independent tests, because two tests is how a player ends up digging through a sheep.
    /// </remarks>
    private Creature? _creatureTarget;

    /// <summary>How far a player can reach to break or place. Genre-standard.</summary>
    private const float Reach = 5f;

    /// <summary>Everything that can be carried, and what each block leaves behind.</summary>
    private ItemRegistry _items = null!;
    private BlockDrops _dropTable = null!;

    /// <summary>The block ids the world is built from, kept for the handful of rules that name one.</summary>
    private StarterBlocks.Ids _ids = null!;

    /// <summary>Which fluid each block is, for the bucket's own ray.</summary>
    private FluidTable _fluidTable = null!;
    private Waterlogging _waterlogging = null!;

    /// <summary>And what each animal leaves, and what had to happen for it to leave it.</summary>
    private CreatureDrops _creatureDropTable = null!;

    /// <summary>Everything that can be made, and every furnace and chest in the world.</summary>
    private RecipeBook _book = null!;
    private FurnaceBank _furnaces = null!;
    private ChestBank _chests = null!;

    /// <summary>Which screen is over the world, and everything it is showing.</summary>
    private readonly HudScreen _hudScreen = new();
    private bool _atBench;
    private (int X, int Y, int Z) _station;

    /// <summary>
    /// Where every clickable thing on the open screen is. Built by the overlay, read by the pointer.
    /// </summary>
    /// <remarks>
    /// One object, filled once a frame as the screen is laid out. That is what makes a click land on
    /// the picture under it rather than near it, and it is why the layout lives in Core with checks
    /// on it rather than in a pile of constants shared by eye between two files.
    /// </remarks>
    private readonly ScreenLayout _layout = new();

    /// <summary>The two-by-two a player always has, and the three-by-three a bench lends them.</summary>
    private CraftingGrid _handGrid = null!;
    private CraftingGrid _benchGrid = null!;

    /// <summary>What is worn, and what is in the other hand.</summary>
    private Equipment _equipment = null!;

    /// <summary>What the crafting tab can make, kept across frames so the selection holds still.</summary>
    private readonly List<Recipe> _shown = [];

    /// <summary>Where the selection sits on each tab, so switching away and back comes home.</summary>
    private readonly int[] _tabRow = new int[Enum.GetValues<GameTab>().Length];

    /// <summary>The action waiting for a key, while the controls tab is listening.</summary>
    private GameAction? _rebinding;

    /// <summary>What the player has changed, and the keys they did it with.</summary>
    private GameSettings _settings = null!;
    private InputMap _keys = null!;
    private bool _settingsDirty;

    /// <summary>Furnaces whose flame changed this frame, so the block drawn can follow.</summary>
    private readonly List<(int X, int Y, int Z, bool Lit)> _relit = [];

    /// <summary>What has become makeable, and the notices on screen saying so.</summary>
    private readonly RecipeUnlocks _unlocks = new();
    private readonly List<Recipe> _justUnlocked = [];
    private readonly List<Toast> _toasts = [];

    /// <summary>How long a notice sits there. Long enough to read twice.</summary>
    private const float ToastSeconds = 5.5f;

    /// <summary>Notices on screen at once before the oldest is pushed off.</summary>
    private const int MaxToasts = 3;

    /// <summary>
    /// What every smelting block becomes when its flame goes in or out, keyed by raw block id.
    /// </summary>
    /// <remarks>
    /// Air where a block has no such form, which is every block in the game but these eight.
    /// </remarks>
    private BlockId[] _smelterLighting = null!;
    private BlockId[] _smelterCooling = null!;

    /// <summary>Which sort of smelter each block id is, for the tick to ask.</summary>
    private FurnaceKind[] _smelterKind = null!;

    /// <summary>Each half-slab and the whole block a second one laid on it makes.</summary>
    private readonly Dictionary<ushort, BlockId> _slabMerge = [];

    /// <summary>Each block with two states, and the one a right click swaps it to.</summary>
    private readonly Dictionary<ushort, BlockId> _toggle = [];

    /// <summary>The burning cask fuses. Every ignition door funnels through LightFuse.</summary>
    private readonly Blastcask.Fuses _fuses = new();

    /// <summary>Scratch for the fuse pass, so a quiet frame allocates nothing.</summary>
    private readonly List<(int X, int Y, int Z)> _burnedDown = [];

    /// <summary>The lit cask's id, asked once — the fuse pass wants it every frame.</summary>
    private BlockId _litCask;

    /// <summary>Each lower half of a two-cell block, and what goes above it.</summary>
    private readonly Dictionary<ushort, BlockId> _tallUpper = [];

    /// <summary>What holds each block up, and what comes down when that is taken away.</summary>
    private SupportTable _supports = null!;

    /// <summary>Scratch for the shed pass, so taking a wall down allocates nothing.</summary>
    private readonly List<(int X, int Y, int Z, BlockId Was)> _fallen = [];

    /// <summary>Nine pockets, one of them in hand.</summary>
    private Inventory _inventory = null!;

    /// <summary>Stacks lying on the ground, and the entities that draw them.</summary>
    private DroppedItems _drops = null!;
    private ItemRenderer _itemRenderer = null!;

    /// <summary>Each block's own box, so the selection and cracking overlays wrap the shape.</summary>
    private (Vector3 Min, Vector3 Max)[] _outlines = null!;

    /// <summary>Chips, bursts and dust.</summary>
    private ParticleSystem _particles = null!;
    private ParticleRenderer _particleRenderer = null!;

    /// <summary>Voices, and the clips they play. Null when the run asked for silence.</summary>
    private AudioEngine? _audio;

    /// <summary>Health, breath, and what a fall costs.</summary>
    private PlayerVitals _vitals = null!;

    /// <summary>Everything on screen that is not the world.</summary>
    private HudRenderer _hud = null!;

    /// <summary>
    /// Which of a material's several sounds to use, and how much to shift its pitch.
    /// </summary>
    /// <remarks>
    /// The pitch shift is what keeps four footsteps from reading as a loop. Four files played in
    /// rotation at exactly their recorded speed is audibly a rotation of four files; a few percent
    /// either way and the ear stops counting.
    /// </remarks>
    private readonly Random _soundPick = new(0x50554E43);

    /// <summary>Ground contact last frame, and the fall it had accumulated, for landing dust.</summary>
    private bool _wasOnGround = true;
    private float _fallInAir;

    /// <summary>Distance walked since the last scuff of dust.</summary>
    private float _stepDistance;

    /// <summary>Whether the head was under water a frame ago, for the crossing sounds.</summary>
    private bool _wasSubmerged;

    /// <summary>Bubbles the breath bar showed a frame ago, so a pop plays as each one goes.</summary>
    private int _lastBubbleCount = 6;

    /// <summary>Seconds until the next look for a canopy to shed a leaf from.</summary>
    private float _untilLeaf;

    /// <summary>The block a falling leaf comes off. One species today.</summary>
    private BlockId _leafBlock;

    /// <summary>
    /// Walking rather than flying. The fly camera stays available behind F3 — it is how terrain
    /// gets inspected, and a bug you can only reach by walking to it is a bug you look at twice.
    /// </summary>
    private bool _walking = true;

    /// <summary>
    /// Physics is held until the ground the player stands on has actually streamed in. Unloaded
    /// chunks read as air, so a body simulated before its floor arrives falls out of the world in
    /// the first fraction of a second and never comes back.
    /// </summary>
    private bool _spawned;
    private Vector3 _spawnPoint;

    /// <summary>
    /// Ceiling on chunk uploads per frame. Buffer creation blocks the driver, so an unbounded
    /// drain turns a burst of finished meshes into a visible hitch exactly when the player is
    /// moving fast enough to have caused it. See <see cref="ClientOptions.MaxUploadsPerFrame"/>.
    /// </summary>
    private readonly int _maxUploadsPerFrame;

    /// <summary>
    /// Where the time before the first frame goes. Running from the moment the host is made.
    /// </summary>
    private readonly StartupTrace _startup = new();

    /// <summary>
    /// The longest step any one update is allowed to advance the world by, in seconds.
    /// </summary>
    /// <remarks>
    /// Past any frame this game has taken and well short of anything anybody would call playing, so
    /// a load or a stall is discarded rather than simulated. See the note at the top of
    /// <see cref="OnUpdate"/> for what happened without it.
    /// </remarks>
    private const double MaxStep = 0.25;

    /// <summary>
    /// Milliseconds the controller scan may take before it is worth telling somebody about it.
    /// </summary>
    /// <remarks>
    /// A machine with nothing paired comes in under a hundred. A second is far enough past that to
    /// mean something is being waited on rather than counted, and near enough that a player who
    /// noticed the pause gets an explanation for it.
    /// </remarks>
    private const double SlowControllerScanMs = 1000;

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    private FrameBench? _bench;
    private BenchPath? _benchPath;
    private bool _benchWarmingUp = true;
    private int _benchWarmupFrames;
    private double _benchWarmupMs;
    private double _benchWarmupPeakMs;
    private int _benchQuietFrames;
    private bool _benchSettled;

    /// <summary>Consecutive quiet frames that count as "the world has finished arriving".</summary>
    private const int BenchQuietFramesNeeded = 30;

    /// <summary>Frames always spent warming up, however quickly the pipeline reports itself idle.</summary>
    private const int BenchMinWarmupFrames = 60;

    /// <summary>Warm-up gives up after this long and says so in the report.</summary>
    private const double BenchWarmupTimeoutMs = 30_000;

    private int _exitCode;
    private bool _stopRequested;
    private bool _shutdown;
    private long _frameStart;
    private bool _frameOpen;
    private double _updateMs;
    private double _renderMs;
    private int _uploadsThisFrame;

    private Vector2 _lastMousePos;
    private bool _haveMouseAnchor;
    private bool _mouseCaptured = true;
    private bool _wireframe;
    private bool _frustumCulling = true;
    private int _drawnChunks;
    private int _drawnTriangles;

    private double _titleTimer;
    private int _framesSinceTitle;
    private double _fps;

    private int _totalVertices;
    private int _totalTriangles;
    private float _fogStart;
    private float _fogEnd;

    /// <summary>What the window is cleared to before the sky pass paints over all of it.</summary>
    private static readonly Vector3 SkyColor = new(0.55f, 0.69f, 0.86f);

    /// <summary>
    /// Cloud white, scaled by how much light the sky is giving before it is drawn.
    /// </summary>
    /// <remarks>
    /// Not quite white, and lit rather than flat. Clouds sit outside the voxel lighting entirely —
    /// they neither cast a shadow nor receive one — so something has to make them go grey at dusk
    /// and dark at night, or the sky turns black around a layer of daylit cotton.
    /// </remarks>
    private static readonly Vector3 CloudTint = new(1.06f, 1.06f, 1.10f);

    /// <summary>
    /// What a cell reached by no light at all still shows. Not zero: a cave lit to pure black is
    /// technically correct and unplayable, and every game in the genre keeps a floor for exactly
    /// that reason. Cool and very dim, so it reads as "your eyes adjusting" rather than as fog.
    /// </summary>
    private static readonly Vector3 NightFloor = new(0.045f, 0.050f, 0.065f);

    public ClientHost(ClientOptions options)
    {
        _options = options;
        _maxUploadsPerFrame = Math.Max(1, options.MaxUploadsPerFrame);

        var windowOptions = WindowOptions.Default with
        {
            Size = new Vector2D<int>(options.Width, options.Height),
            Title = "Driftwood",
            VSync = options.VSync,
            API = new GraphicsAPI(
                ContextAPI.OpenGL,
                ContextProfile.Core,
                ContextFlags.ForwardCompatible,
                new APIVersion(3, 3)),
        };

        _window = Window.Create(windowOptions);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.FramebufferResize += OnResize;
    }

    /// <summary>Runs until the window closes or the benchmark finishes. Returns a process exit
    /// code; only the benchmark ever reports failure through it.</summary>
    /// <remarks>
    /// The loop is driven here rather than handed to <c>IWindow.Run</c> so that a run can be ended
    /// from inside a frame. Silk.NET refuses to tear a window down while its own loop is on the
    /// stack — "you cannot call Reset inside of the render loop" — which a self-terminating
    /// benchmark hits every single time. Owning the loop makes stopping a normal exit condition.
    /// </remarks>
    public int Run()
    {
        _window.Initialize();

        // ⛳ Asks Windows for a one-millisecond scheduler tick, because the pacing below waits with
        // Sleep and Sleep is rounded up to the next tick of the system timer — 15.6 ms by default,
        // against a 5.7 ms frame.
        // ⚠ HONESTLY: this is the established practice rather than a measured win here. It was added
        // to explain a limit of 175 reading as 155, and it did not move that number — because the
        // number was wrong. The rate was being averaged across world loading, and once that was taken
        // out the limit reads exactly 175 either way. Kept because waiting on a coarse timer is a real
        // hazard on a slower machine, not because anything here proved it.
        var timerRaised = TimeBeginPeriod(1) == 0;

        var due = Stopwatch.GetTimestamp();

        while (!_window.IsClosing && !_stopRequested)
        {
            _window.DoEvents();
            _window.DoUpdate();
            _window.DoRender();
            PaceFrame(ref due);
        }

        _window.DoEvents();
        if (timerRaised) TimeEndPeriod(1);

        Shutdown();
        return UiCheckFailed ? 1 : _exitCode;
    }

    /// <summary>
    /// Asks for a finer scheduler tick, so <see cref="Thread.Sleep(int)"/> means what it says.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Paired, and only released if it was taken.</b> Windows counts these, and a process that
    /// raises the timer resolution and never puts it back leaves the whole machine on a one
    /// millisecond tick — which is somebody else's battery.
    /// </remarks>
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint ms);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint ms);

    /// <summary>
    /// Waits until the next frame is due, when there is a limit to wait for.
    /// </summary>
    /// <remarks>
    /// <para>⛔⛔ <b>Done here because <c>IWindow.FramesPerSecond</c> IS IGNORED BY THIS GAME.</b> That
    /// property paces Silk's own <c>Run</c> loop, and this loop is driven by hand — see <see cref="Run"/>
    /// for why. Setting it read as a working frame limit, saved to the settings file, drew a row on
    /// the video tab, and did precisely nothing: measured through <c>--play</c>, a limit of 175
    /// produced <b>12,571 fps</b>. A setting that silently does nothing is worse than no setting,
    /// because it also answers the question.</para>
    /// <para>⛳ <b>Sleep coarsely, then spin.</b> A frame at 175 is 5.7 ms and <c>Thread.Sleep(1)</c>
    /// is only good to a millisecond or so on Windows, so sleeping the whole wait overshoots and
    /// gives an uneven rate under the one asked for. Sleeping while there is time to spare and
    /// spinning out the last two milliseconds costs a sliver of one core and lands on the number.
    /// </para>
    /// <para>⛔ <b>It never tries to CATCH UP.</b> A deadline that keeps advancing while the game is
    /// loading a world comes due a hundred times at once, and the loop then runs flat out to make
    /// good a debt it can never repay — which is the frame-pacing bug that looks exactly like a
    /// stutter. Fall far enough behind and the deadline is simply moved to now.</para>
    /// </remarks>
    private void PaceFrame(ref long due)
    {
        // ⚠ The benchmark is never paced: its whole job is to say how fast this machine can draw,
        // and a limited run reports the limit, which is true of every machine.
        var cap = _settings.VSync || _options.BenchSeconds > 0 ? 0 : _settings.FrameCap;

        if (cap <= 0)
        {
            due = Stopwatch.GetTimestamp();
            return;
        }

        var period = Stopwatch.Frequency / cap;
        due += period;

        var now = Stopwatch.GetTimestamp();
        if (now - due > period * 4)
        {
            due = now;
            return;
        }

        while (true)
        {
            var left = due - Stopwatch.GetTimestamp();
            if (left <= 0) return;

            // ⚠ Sleep while there is room, spin out the last few milliseconds. Spinning the whole
            // wait lands on the number just as well and burns a core doing it, which on a limiter
            // whose entire purpose is to stop wasting the machine would be a joke.
            if (left * 1000 / Stopwatch.Frequency > 3) Thread.Sleep(1);
            else Thread.SpinWait(64);
        }
    }

    /// <summary>
    /// Puts the game's icon on the window, and with it on the taskbar button.
    /// </summary>
    /// <remarks>
    /// <c>ApplicationIcon</c> in the project file dresses the executable, which is what Explorer
    /// and a shortcut show — and it is not what a running window shows. The taskbar button takes
    /// its icon from the window, and a window GLFW created has none, so without this the game runs
    /// under a blank default however well the file is dressed.
    /// <para>Two sizes are offered and the platform picks; handing over one and letting Windows
    /// scale it is how a taskbar icon ends up soft or, at 16 pixels, unreadable.</para>
    /// </remarks>
    /// <summary>
    /// The largest tile this machine will actually take, asked of the card rather than assumed.
    /// </summary>
    /// <remarks>
    /// <para>Two limits, and the one that binds is not the obvious one. The card reports a maximum
    /// texture side and a maximum number of array layers, and both are enormous on anything modern
    /// — sixteen thousand and two thousand are typical. What actually runs out is memory: at the
    /// layer count in use, a 512-pixel array is about eighty megabytes with its mip chain, a
    /// thousand-pixel one about three hundred, and two thousand about one and a third gigabytes.
    /// </para>
    /// <para>So the ceiling is a calculation against a budget rather than a constant, and it comes
    /// down on its own as the block set grows. It used to be the number 512, typed once, which was
    /// neither what the card said nor what the memory allowed.</para>
    /// </remarks>
    private int TextureCeiling()
    {
        const long Budget = 512L * 1024 * 1024;

        // ⛔ BYTES A TEXEL, MIPS INCLUDED — four for the RGBA and a third again for the chain. It
        // was called WithMips and its comment mentioned only the third, so it read as 1.33 and is
        // 5.33; the first line written to report the ceiling multiplied by four again on top of it
        // and announced 1088 MiB against a 512 MiB budget. The value was always right. Saying the
        // number out loud is what made the name being wrong cost something.
        const double BytesPerTexelWithMips = 4.0 * 4.0 / 3.0;

        var maxSide = _gl.GetInteger(GLEnum.MaxTextureSize);
        var maxLayers = _gl.GetInteger(GLEnum.MaxArrayTextureLayers);
        var layers = StarterBlocks.LayerCount;

        if (maxLayers > 0 && layers > maxLayers)
            Console.Error.WriteLine($"driftwood: {layers} texture layers but the card takes {maxLayers}");

        var affordable = (int)Math.Sqrt(Budget / (layers * BytesPerTexelWithMips));

        // Down to a power of two. Every pack is painted at one, and a mip chain built from an
        // awkward size loses a level to rounding at the bottom.
        var ceiling = 16;
        while (ceiling * 2 <= affordable && ceiling * 2 <= maxSide) ceiling *= 2;

        // ⛳ SAID OUT LOUD, because the whole point of asking the card was that the answer stops
        // being a number somebody typed — and an answer nobody can see is indistinguishable from
        // one. Every input goes in the line: a ceiling that is wrong is wrong because one of these
        // is, and which one is not guessable from the result on its own.
        var cost = layers * (double)ceiling * ceiling * BytesPerTexelWithMips / (1024 * 1024);
        Console.WriteLine(
            $"textures    ceiling {ceiling}px — {layers} layers at {ceiling} is {cost:F0} MiB with mips "
            + $"against a {Budget / (1024 * 1024)} MiB budget; the card takes {maxSide}px "
            + $"and {maxLayers} layers");

        return ceiling;
    }

    private void ApplyWindowIcon()
    {
        var icons = new List<RawImage>(2);

        foreach (var size in (ReadOnlySpan<int>)[32, 64])
        {
            using var stream = typeof(ClientHost).Assembly
                .GetManifestResourceStream($"Driftwood.Client.window-icon-{size}.png");

            if (stream is null) continue;

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);

            if (!Png.TryDecode(buffer.ToArray(), out var image, out _)) continue;
            icons.Add(new RawImage(image.Width, image.Height, image.Pixels));
        }

        if (icons.Count == 0) return;
        _window.SetWindowIcon(CollectionsMarshal.AsSpan(icons));
    }

    private void OnLoad()
    {
        _startup.Mark("window and GL");

        _gl = GL.GetApi(_window);
        ApplyWindowIcon();

        // Whatever the player changed last time, before anything reads a key or a field of view.
        // A bad file costs the setting it names and nothing else — see GameSettings.Load.
        _settings = GameSettings.Load();
        _keys = new InputMap(_settings.Keys);
        _startup.Mark("settings");

        // A command line still wins over a saved setting for the run it is on, without writing
        // itself into the file — starting once with --vsync should not turn it on for good.
        if (_options.VSync) _settings.VSync = true;
        if (_options.Mute) _settings.Mute = true;

        // Which world this is and what seed it is made of, before anything is built out of one.
        // Nothing else in here may read _options.Seed after this point.
        OpenWorld();
        _startup.Mark("world header");

        Console.WriteLine($"settings    {GameSettings.Path}");

        // ⛔ NOT _window.CreateInput(). That built thirty two joystick and gamepad wrappers, and the
        // first of them made the platform initialise its whole joystick stack — measured at 10.3
        // seconds here, ninety seven percent of the entire startup, against twelve milliseconds to
        // build every texture in the game. Nothing in Driftwood reads a controller yet, so nothing
        // needed to be paid. See RawInput for the whole measurement and for the rule P8 has to
        // follow when controller support does land.
        _input = new RawInput(_window);

        if (_input.Failed)
            Console.Error.WriteLine("driftwood: could not attach to the window, so there is no keyboard or mouse");

        _input.KeyDown += OnKeyDown;
        _input.CharTyped += OnCharTyped;
        _input.MouseMove += OnMouseMove;
        _input.MouseDown += OnMouseDown;
        _input.MouseUp += OnMouseUp;
        _input.Scroll += OnScroll;
        _startup.Mark("settings and input");

        // The benchmark flies itself, so it leaves the cursor alone; stealing the mouse for a
        // measurement run is rude and changes nothing about what is measured. Same for a timed play.
        SetMouseCaptured(_options.BenchSeconds <= 0 && _options.PlaySeconds <= 0);

        _gl.Enable(EnableCap.DepthTest);

        // Later coplanar passes have to win, not lose. A grass block's tinted fringe sits in the
        // same plane as the dirt under it and is drawn second; under the default "strictly nearer"
        // test it would be rejected wherever the shader's small lift rounds away, which is most of
        // the view at distance. Equal depth going to whatever was drawn last is what the model
        // format assumes.
        _gl.DepthFunc(DepthFunction.Lequal);

        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);
        _gl.ClearColor(SkyColor.X, SkyColor.Y, SkyColor.Z, 1f);

        _chunkShader = new Shader(_gl, ChunkShaders.Vertex, ChunkShaders.Fragment);
        _outline = new BlockOutline(_gl);

        _particleRenderer = new ParticleRenderer(_gl);
        _itemRenderer = new ItemRenderer(_gl);
        _hud = new HudRenderer(_gl);
        _startup.Mark("shaders");

        // The benchmark opens no device at all. It flies a scripted path and hears nothing worth
        // hearing, and a measurement run that makes noise on somebody's machine is rude twice over.
        if (_options.BenchSeconds <= 0 && !_options.Mute)
        {
            _audio = new AudioEngine(new SoundLibrary(SoundLibrary.FindRoot()));
            Console.WriteLine($"sound       {_audio.Summary}");
        }

        _startup.Mark("sound");

        // A loaded world opens where it was put down, and --time only says where a new one starts.
        _clock = new SkyClock(_loadedWorld ? _savedDayTime : _options.StartTime, _options.DayLength);
        _skyState = _clock.Now;
        _sky = new SkyRenderer(_gl);

        ResolvePack();

        var cloudField = new CloudField(_seed, _packPath);
        _clouds = new CloudRenderer(_gl, cloudField.Build());
        Console.WriteLine(
            $"clouds      {cloudField.Summary}, {cloudField.Coverage * 100:F0}% cover, "
            + $"{_clouds.QuadCount:N0} quads over {CloudField.Period:F0} blocks");
        _startup.Mark("sky and clouds");

        var ceiling = TextureCeiling();
        _textures = BlockTextureSet.Build(_packPath, _options.TextureSize, ceiling);
        _startup.Mark("block textures");

        _blockTextures = new BlockTextureArray(_gl, _textures.Tiles, _textures.Size, BlockTextureSet.Cutouts());
        Console.WriteLine(
            $"textures    {_textures.Summary}"
            + (_blockTextures.Reweighted > 0
                ? $", {_blockTextures.Reweighted} cut-outs re-mipped"
                : ""));

        _animatedTextures = new TextureAnimator(_blockTextures, _textures.Animations);
        if (_animatedTextures.Count > 0)
        {
            // ⚠ How different the frames actually are goes in the line. "51 frames" is true of
            // fifty-one copies of the same picture, and that is a real way for a generator or a
            // strip reader to be wrong while every count in the report looks right. The QUIETEST
            // track is the one reported, because that is the one at risk and an average over six
            // would hide it — a real pack's water is a far subtler ripple than ours.
            var quietest = 100;
            var quietestLayer = "";

            foreach (var track in _textures.Animations)
            {
                var apart = 0;
                var half = track.Frames[track.Frames.Length / 2];
                for (var p = 0; p < track.Frames[0].Length; p += 4)
                    if (track.Frames[0][p] != half[p]) apart++;

                var share = apart * 400 / track.Frames[0].Length;
                if (share >= quietest) continue;

                quietest = share;
                quietestLayer = BlockTextureSet.Layers[track.Layer].Name;
            }

            Console.WriteLine(
                $"animated    {_animatedTextures.Count} layers, {_animatedTextures.FrameCount} frames, "
                + $"quietest is {quietestLayer} at {quietest}% of its tile between first and middle");
        }

        _startup.Mark("texture upload");

        var skin = PlayerSkin.Build(_options.SkinPath, _options.Arms);

        // ⚠ Opened once and handed to both, rather than a path each. Two openings of a quarter-
        // gigabyte zip to answer twelve lookups is a load turned into a wait — and it is the same
        // twelve nets either way, so the two would have to agree about them anyway.
        using (var pack = string.IsNullOrWhiteSpace(_packPath) ? null : TexturePack.Open(_packPath))
        {
            _playerRenderer = new PlayerRenderer(_gl, skin, pack);
            _hud.SetSkin(skin, pack);
        }

        Console.WriteLine(
            $"skin        {skin.Summary}"
            + (_playerRenderer.ArmourFromPack > 0
                ? $"; {_playerRenderer.ArmourFromPack} of {Armour.Materials.Length * ArmourSheets.Layers} "
                  + "armour nets from the pack"
                : "; every armour net ours"));

        // The same size the block tiles came out at, whatever decided it — cracks are laid over a
        // block face and a crack chain at a different resolution is visible as a crack chain.
        var cracks = CrackTextures.Build(_packPath, _textures.Size);
        _cracks = new BlockCracks(_gl, cracks);
        Console.WriteLine($"cracks      {cracks.Summary}");
        _startup.Mark("skin and cracks");

        BuildCreatures();

        BuildWorld();

        // Last, because it wants the camera, the window and the audio device to all exist. Doing
        // it here rather than in half a dozen constructors also means there is exactly one place
        // that turns a setting into an effect, which is the place to look when one does nothing.
        ApplySettings();
        _startup.Mark("settings applied");
    }

    /// <summary>
    /// Reads the creature skeletons and the pack's art for them, and builds what can be drawn.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>A machine with no geometry folder is the ordinary case, not a fault.</b> The skeletons
    /// ship with an installed Bedrock client rather than with us or with a pack, so this stays quiet
    /// about having found none and says exactly one line when it has. ⛔ It also must not throw: an
    /// unreadable folder, a pack with no entity art and a path that is simply wrong all have to end
    /// in a world with no animals in it rather than a game that will not start.
    /// </remarks>
    private void BuildCreatures()
    {
        // Given on the command line it is also remembered, so it is typed once ever rather than
        // once a launch. ⚠ Remembered only when it is real: writing a path that does not exist into
        // the settings file leaves somebody with a permanent setting that does nothing.
        var folder = _options.CreatureGeometry;

        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            if (_settings.CreatureGeometry != folder)
            {
                _settings.CreatureGeometry = folder;
                _settings.Save();
            }
        }
        else
        {
            folder = _settings.CreatureGeometry;
        }

        try
        {
            // ⛔ NOT a reason to stop. Our own creatures are drawn from our own table and need no
            // install at all — the folder only ever adds the ones we have not drawn yet. Returning
            // early when there was no folder is what left a machine with no Bedrock client with no
            // animals in the game, which is every machine that is not this one.
            var faults = new List<string>();
            var models = string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)
                ? []
                : CreatureLibrary.ReadFolder(folder, faults);

            using var pack = string.IsNullOrWhiteSpace(_packPath)
                ? null
                : TexturePack.Open(_packPath);

            var resolved = CreatureLibrary.Resolve(models, pack);

            // ⛳ The cart rides the same pipeline without being a creature: its own net, its own
            // palette row, drawn through the entity shader like everything that moves. Appended
            // here so CreatureSet.All — and every census that walks it — never hears about it.
            resolved.Add(new CreatureSet.Resolved(
                new CreatureKind("cart", "cart", default, "cart", []),
                StarterCreatures.Cart(), "ours", "", 0, 0));

            // ⚠ The renderer only. The herd is made when the player spawns, because a herd made here
            // is one SpawnCreatures finds already present and declines to fill — which is a world
            // with every animal loaded, none of them placed, and nothing anywhere saying so.
            _creatureRenderer = new CreatureRenderer(_gl, resolved, pack);

            Console.WriteLine(
                $"creatures   {_creatureRenderer.Summary}, from {models.Count} skeletons"
                + (faults.Count > 0 ? $" ({faults.Count} unreadable)" : ""));
        }
        catch (Exception error)
        {
            Console.WriteLine($"creatures   not loaded: {error.GetType().Name}: {error.Message}");
        }

        _startup.Mark("creatures");
    }

    /// <summary>Whether a cell would hold an animal up. The herd's whole view of the world.</summary>
    private bool SolidForCreature(int x, int y, int z) =>
        _registry[_streamer.World.GetBlock(x, y, z)].Solid;

    /// <summary>How many animals to keep in the world around the player.</summary>
    private const int HerdSize = 12;

    /// <summary>
    /// Tops the herd up near the player, as ground to stand on becomes available.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>A top-up rather than one spawn, and the reason is streaming.</b> Placed the moment the
    /// player's own chunk arrives, eleven of twelve animals find nowhere to stand — unloaded space
    /// reads as air, so the search falls through the world and comes back with nothing. Waiting a
    /// fixed number of seconds instead would be a guess about a worker pool. Asking again while
    /// there is room is neither, and it is also the shape a real spawner wants.
    /// </remarks>
    /// <summary>Sky light at or below which something will spawn in a cell. The genre's own line.</summary>
    private const int SpawnDarkness = SpawnRules.Darkness;

    /// <summary>Seconds until the next attempt at putting something in the dark.</summary>
    private float _hostileTry;

    /// <summary>
    /// The rolls the dark is made of, seeded off the world.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Seeded rather than <c>Random.Shared</c>, so a night is repeatable.</b> A spawner nobody
    /// can reproduce is a spawner nobody can tune, and the whole point of this pass was that it was
    /// tuned wrong.
    /// </remarks>
    private Random _spawnRoll = new(0);

    private void TopUpCreatures(float dt)
    {
        if (_creatureRenderer is null || _creatureRenderer.Count == 0 || !_spawned) return;

        if (_herd is null)
        {
            _herd = new CreatureHerd(_seed.Derive("creatures"));
            _spawnRoll = new Random(_seed.Derive("spawn.hostiles"));

            // ⛳ The save's animals come back the moment the herd exists, before any top-up can
            // count the world as empty and fill it. A kind this build cannot stand up is skipped
            // with a count, the palette's own posture.
            if (_savedCreatures.Count > 0)
            {
                var kept = _herd.Restore(_savedCreatures, KindFor, out var unknown);
                Console.WriteLine(
                    $"creatures   {kept} restored from the save"
                    + (unknown > 0 ? $", {unknown} of kinds this build does not have" : ""));
                _savedCreatures.Clear();
            }
        }

        // ⛳ The hostiles keep their own clock, because theirs is the one that has to be irregular.
        // A herd of cows appearing on a tidy cadence is nothing anybody notices; a spawner does.
        TopUpHostiles(dt);

        _creatureTopUp -= dt;
        if (_creatureTopUp > 0f) return;

        _creatureTopUp = 1f;

        TopUpBeasts();
        TopUpCaveLife();
        TopUpWaterLife();
    }

    /// <summary>How many of the water's own are kept about. Atmosphere, like the cave's.</summary>
    private const int WaterLifeCount = 2;

    /// <summary>Puts the water's harmless things in the water — any water, any light.</summary>
    /// <remarks>
    /// ⛳ The bat's lesson a fourth time: where a creature lives is its own axis. The spawn walk
    /// still stands it on the pond's floor — that is where the ground under water is — and the
    /// swim is what lifts it off.
    /// </remarks>
    private void TopUpWaterLife()
    {
        if (_creatureRenderer is null || _herd is null) return;

        var living = 0;
        foreach (var creature in _herd.All)
            if (FamilyOf(creature.Kind) == CreatureFamily.Water) living++;

        if (living >= WaterLifeCount) return;

        var kinds = KindsOf(CreatureFamily.Water);
        if (kinds.Count == 0) return;

        _herd.Spawn(
            SolidForCreature, kinds, _player.Position, WaterLifeCount - living,
            where: (x, y, z) =>
                _registry[_streamer.World.GetBlock(x, y, z)].Fluid == FluidKind.Water,
            minRadius: 8f);
    }

    /// <summary>Puts a few harmless things in the dark under the ground.</summary>
    /// <remarks>
    /// ⛳ <b>The third answer, and the reason it had to exist.</b> Spawning was one axis — is this
    /// cell dark — so a bat and a cow were the same question asked twice, and the bat turned up in
    /// meadows. Where a creature lives takes <em>two</em> axes: how dark it is, and whether the cell
    /// can see the sky at all. The second is free, because sky light is already what a cell would
    /// get at noon regardless of what the clock says — it is exactly "is there a way out above you".
    /// </remarks>
    private void TopUpCaveLife()
    {
        if (_creatureRenderer is null || _herd is null) return;

        var living = 0;
        foreach (var creature in _herd.All)
            if (!creature.Hostile && FamilyOf(creature.Kind) == CreatureFamily.Cave) living++;

        if (living >= CaveLifeCount) return;

        var kinds = KindsOf(CreatureFamily.Cave);
        if (kinds.Count == 0) return;

        _herd.Spawn(
            SolidForCreature, kinds, _player.Position, CaveLifeCount - living,
            where: Buried, minRadius: 8f);
    }

    /// <summary>How many cave things are kept about. Fewer than a herd: they are atmosphere.</summary>
    private const int CaveLifeCount = 3;

    /// <summary>
    /// True where a cell is dark AND has no way to the sky above it.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Raw sky light, not the scaled kind <see cref="Dark"/> uses.</b> The scaled one answers
    /// "is it dark here now", which at midnight is true of an open field; the raw one answers "could
    /// the sun ever reach here", which is the question about where a place <em>is</em> rather than
    /// what time it is. Using the scaled value would put bats in fields at night, which is precisely
    /// the fault this exists to fix.
    /// </remarks>
    private bool Buried(int x, int y, int z) =>
        SpawnRules.Buried(_streamer.World.GetLight(x, y, z));

    /// <summary>Keeps a herd of animals about, wherever there is ground.</summary>
    private void TopUpBeasts()
    {
        if (_creatureRenderer is null || _herd is null) return;

        var beasts = 0;
        foreach (var creature in _herd.All) if (!creature.Hostile) beasts++;
        if (beasts >= HerdSize) return;

        // The beasts, not the hostiles: the first thing anybody meets in a field should be a cow.
        // ⛳ Each one carries the size its own mesh came out at, because that is the box a blow has
        // to land inside — measured off the model rather than written down beside it.
        var kinds = KindsOf(CreatureFamily.Beast);
        if (kinds.Count == 0) return;

        var before = _herd.Count;
        _herd.Spawn(SolidForCreature, kinds, _player.Position, HerdSize - beasts);

        if (_herd.Count == before) return;
        Console.WriteLine($"creatures   {beasts + _herd.Count - before} of {HerdSize} standing, {kinds.Count} kinds to draw from");
    }

    /// <summary>
    /// And puts hostiles wherever the light has gone.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>Darkness rather than the clock, which is the whole of the design.</b> A cave at
    /// noon is as dangerous as a field at midnight, and a well-lit field at midnight is not dangerous
    /// at all — so a torch is a decision about safety rather than about seeing, and the night is
    /// something a player can build their way out of. Asking the clock instead would make lighting a
    /// house pointless and a mineshaft free.</para>
    /// <para>⚠ <b>Never within twelve blocks.</b> Something appearing beside you is a jump scare
    /// rather than a threat, and it is also unfair — there is nothing to react to.</para>
    /// </remarks>
    private void TopUpHostiles(float dt)
    {
        if (_creatureRenderer is null || _herd is null) return;

        // ⛔ ON ITS OWN CLOCK, ROLLED FRESH, AND THAT IS THE FIX. This used to run every second and
        // refill the whole deficit in one call — a set point with infinite gain — so a night arrived
        // complete rather than building, kill one and it was back before the body had faded, and a
        // player learned the beat within a minute. Reported as "really aggressive at night".
        _hostileTry -= dt;
        if (_hostileTry > 0f) return;

        _hostileTry = SpawnRules.NextAttempt(_spawnRoll.NextDouble());

        // ⚠ By FAMILY, not by the Hostile flag: a farwalker is born calm (see Fierce) and still
        // takes a bed in the night's own count, or the dark would fill with them unboundedly.
        var hostiles = 0;
        foreach (var creature in _herd.All)
            if (FamilyOf(creature.Kind) == CreatureFamily.Hostile) hostiles++;
        if (hostiles >= SpawnRules.HostileCap) return;

        // And an attempt that fires does not always place anything. The chance falls to nothing as
        // the night fills, so the first hour of dark is quiet and clearing your doorstep buys quiet
        // back rather than being answered on the next tick.
        if (_spawnRoll.NextDouble() > SpawnRules.Pressure(hostiles, SpawnRules.HostileCap)) return;

        // ⛳ The drowned is not in the open-ground pool — it has its own door below, because a
        // drowned standing in a midnight meadow is a zombie wearing the wrong colours. Two axes,
        // the bat's own lesson: what the dark is, and what it is the dark OF.
        var kinds = KindsOf(CreatureFamily.Hostile);
        kinds.RemoveAll(kind => kind.Name == "drowned");
        if (kinds.Count == 0) return;

        var room = Math.Min(SpawnRules.HostileCap - hostiles, SpawnRules.HostileBatch);
        var want = 1 + _spawnRoll.Next(room);

        _herd.Spawn(
            SolidForCreature, kinds, _player.Position, want,
            where: Dark, minRadius: SpawnRules.HostileMinRadius);

        TopUpDrowned();
    }

    /// <summary>How many of the sea's own are kept about when there is dark water to hold them.</summary>
    private const int DrownedCount = 2;

    /// <summary>Puts the drowned where it belongs: standing IN dark water, never on a lawn.</summary>
    private void TopUpDrowned()
    {
        if (_creatureRenderer is null || _herd is null) return;

        var living = 0;
        foreach (var creature in _herd.All) if (creature.Kind == "drowned") living++;
        if (living >= DrownedCount) return;

        if (KindFor("drowned") is not { } kind) return;

        _herd.Spawn(
            SolidForCreature, [kind], _player.Position, DrownedCount - living,
            where: (x, y, z) =>
                _registry[_streamer.World.GetBlock(x, y, z)].Fluid == FluidKind.Water
                && Dark(x, y, z),
            minRadius: SpawnRules.HostileMinRadius);
    }

    /// <summary>True where a creature's feet would stand in the dark.</summary>
    /// <remarks>
    /// ⚠ <b>Sky light and block light both, and taken as the brighter.</b> A torch has to actually
    /// clear the ground round it, and sunlight has to actually clear a field — a test on either one
    /// alone leaves the other kind of light doing nothing.
    /// </remarks>
    private bool Dark(int x, int y, int z) =>
        SpawnRules.Dark(_streamer.World.GetLight(x, y, z), Daylight);

    /// <summary>
    /// How much of the day is on, 0 at night to 1 with the sun properly up.
    /// </summary>
    /// <remarks>
    /// ⚠ Off the sun's <em>elevation</em> rather than off the clock, which is the same call the sky
    /// gradient makes — sunrise and sunset are then one rule read twice rather than two numbers that
    /// can disagree about when the day starts.
    /// </remarks>
    private float Daylight => Math.Clamp(_skyState.SunDirection.Y * 4f, 0f, 1f);

    /// <summary>True where a cell is standing in real, unobstructed daylight.</summary>
    private bool Sunlit(int x, int y, int z) =>
        Daylight > 0.85f
        && LightValue.Sky(_streamer.World.GetLight(x, y, z)) >= LightValue.Max;

    /// <summary>Every kind of one family we can actually draw, with the size its own mesh has.</summary>
    /// <summary>Which family a creature belongs to, by name. Beast when nothing claims it.</summary>
    private static CreatureFamily FamilyOf(string kind)
    {
        foreach (var entry in CreatureSet.All)
            if (entry.Name == kind) return entry.Family;

        return CreatureFamily.Beast;
    }

    private List<SpawnKind> KindsOf(CreatureFamily family)
    {
        var kinds = new List<SpawnKind>();
        if (_creatureRenderer is null) return kinds;

        foreach (var kind in CreatureSet.All)
        {
            if (kind.Family != family) continue;
            if (!_creatureRenderer.TryMeasure(kind.Name, out var size)) continue;
            kinds.Add(new SpawnKind(kind.Name, size, Fierce(kind.Name), CreatureSet.MoveFor(kind.Name)));
        }

        return kinds;
    }

    /// <summary>
    /// Whether one of this kind comes at people from the moment it stands.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>A retaliator is never born angry, whatever door it came through.</b> The farwalker
    /// spawns with the night's own — it is a thing of the dark — and stands there harming nobody
    /// until struck, which is the wolf's contract wearing a taller body. The night cap counts it
    /// all the same (see <see cref="TopUpHostiles"/>), or the dark would fill with them past any
    /// bound.
    /// </remarks>
    private static bool Fierce(string kind) =>
        FamilyOf(kind) == CreatureFamily.Hostile && !CreatureVitals.Retaliates(kind);

    /// <summary>One saved kind stood back up, or null for one this build has no mesh for.</summary>
    private SpawnKind? KindFor(string kind)
    {
        if (_creatureRenderer is null || !_creatureRenderer.TryMeasure(kind, out var size))
            return null;

        return new SpawnKind(kind, size, Fierce(kind), CreatureSet.MoveFor(kind));
    }

    private void BuildWorld()
    {
        var registry = new BlockRegistry();
        var ids = StarterBlocks.Register(registry);
        registry.Seal();
        _registry = registry;
        _ids = ids;
        _fluidTable = new FluidTable(registry);
        _waterlogging = new Waterlogging(registry);
        _startup.Mark("blocks");

        // The item layer sits on top of the block layer and never the other way round, which is why
        // it is built here rather than beside it: everything it needs is an id the blocks have
        // already handed out.
        _items = StarterItems.Register(registry);
        _dropTable = StarterItems.Drops(registry, _items);
        _creatureDropTable = StarterItems.Creatures(_items);
        _book = StarterRecipes.Build(_items);
        _furnaces = new FurnaceBank(_items, _book);
        _chests = new ChestBank(_items);
        (_smelterLighting, _smelterCooling) = StarterBlocks.SmelterStates(registry);
        _smelterKind = StarterBlocks.SmelterKinds(registry);
        foreach (var (slab, whole) in StarterBlocks.SlabMerges(registry)) _slabMerge[slab.Value] = whole;
        foreach (var (from, to) in StarterBlocks.Toggles(registry)) _toggle[from.Value] = to;
        foreach (var (lower, upper) in StarterBlocks.TallPairs(registry)) _tallUpper[lower.Value] = upper;
        _supports = new SupportTable(registry);
        _litCask = registry.ByName(Blastcask.Lit).Id;

        // Each pressed button's idle form, for the spring back — the press itself rides the
        // toggle table, which deliberately has no return row for a momentary thing.
        _buttonIdle = new ushort[registry.Count];
        foreach (var form in StarterBlocks.AttachedForms("button"))
            _buttonIdle[registry.ByName(form + "_pressed").Id.Value] = registry.ByName(form).Id.Value;
        _startup.Mark("items and recipes");

        // ⛳ Here, and not a line earlier: the silhouette has to be cut from the picture that will
        // actually be drawn, which means after a pack has had its say about the textures, and it is
        // keyed on items, which means after there are any.
        _itemRenderer.BuildSprites(_items, _textures.Tiles, _textures.Size);
        Console.WriteLine(
            $"sprites     {_itemRenderer.SpriteCount} flat items extruded, "
            + $"{_itemRenderer.SpriteQuads:N0} quads");
        _startup.Mark("item sprites");

        var generator = new TerrainGenerator(_seed, ids, _options.OceanCoverage);

        // --chunks used to size a fixed box; it now sets how far the world is kept loaded around
        // the viewer, which is the same dial pointed at a world that no longer has edges. The
        // saved view distance is the same dial again, and the command line beats it when given.
        var viewRadius = _options.ChunksGiven
            ? Math.Max(2, _options.ChunksAcross / 2)
            : _settings.ViewDistance;

        _viewRadius = viewRadius;

        // The pack's colormaps if it ships them, ours otherwise — so an imported pack's grass is
        // the colour its author chose, not the colour we would have chosen for it.
        var tinter = new BlockTinter(
            new ClimateField(_seed), _textures.GrassMap, _textures.FoliageMap);

        _streamer = new WorldStreamer(registry, generator, viewRadius, tinter: tinter)
        {
            // What lets a fence know it has a neighbour. Handed over rather than built inside the
            // streamer, so a world with nothing that connects pays nothing for the pass.
            Connections = StarterBlocks.Connections(registry),

            // ⛳ And what makes water move. Owned out here rather than by the streamer, because the
            // tick rate is a game decision and the per-frame budget is a frame-time one; what the
            // streamer contributes is the two moments the flow cannot see for itself — a chunk
            // arriving, which is where a stalled fall resumes, and a block being edited.
            Fluids = new FluidEngine(registry),

            // And what makes a lever mean something. Every block edit runs the wiring pass; the
            // gates think on their own tick below, at a rate that is a game decision too.
            Signals = new SignalPass(registry),

            // And what keeps the track joined up when a rail lands or leaves.
            Rails = new RailTable(registry),
        };

        _signalTable = _streamer.Signals!.Table;
        _railTable = _streamer.Rails!;
        _cartSystem = new CartSystem(_railTable);

        var reach = viewRadius * Chunk.Size;
        _fogEnd = MathF.Min(reach * 0.90f, 700f);
        _fogStart = _fogEnd * 0.55f;
        _camera.FarPlane = _fogEnd + 200f;

        _player = new PlayerBody(registry);
        _vitals = new PlayerVitals(registry);
        _particles = new ParticleSystem(registry);
        _growth = new Growth(registry);
        _leafBlock = ids.Leaves;
        _inventory = new Inventory(_items);
        _equipment = new Equipment(_items);
        _drops = new DroppedItems(registry, _items);
        _solid = registry.BuildSolidTable();

        // Two grids, both alive for the whole session rather than made when a screen opens. What is
        // laid out in the hands stays laid out while a player goes and fetches the missing plank,
        // which is what every game in this space does and what anybody would expect.
        _handGrid = new CraftingGrid(_book, _items, 2, 2, CraftStation.Hand);
        _benchGrid = new CraftingGrid(_book, _items, 3, 3, CraftStation.Bench);

        // Nothing in the pockets. That is the design — punch wood, make planks, make a pickaxe —
        // and the starting kit that used to be here was scaffolding for the days when a slab could
        // be obtained no other way. The audit walks the whole tree from here every run.

        _outlines = new (Vector3, Vector3)[registry.Count];
        for (var id = 0; id < registry.Count; id++) _outlines[id] = registry[(ushort)id].Model.Outline;

        // A ray stops at anything but air and open water. Water is the exception that matters: you
        // can neither stand on it nor break it, so a ray fired across a lake has to reach the bed or
        // nothing underwater could ever be mined. Everything else is something a player expects to
        // be able to point at — bedrock they cannot break, leaves and plants they walk through.
        // Derived from the flags rather than from a list of names, so a new plant is targetable the
        // day it is registered instead of the day somebody remembers this line.
        _targetable = new bool[registry.Count];
        for (var id = 1; id < registry.Count; id++)
        {
            var type = registry[(ushort)id];
            _targetable[id] = type.Solid || !type.Unbreakable;
        }

        _spawnPoint = new Vector3(0.5f, generator.SurfaceHeight(0, 0) + 3f, 0.5f);
        _player.Teleport(_spawnPoint);

        if (_options.BenchSeconds > 0)
        {
            // The benchmark flies a scripted path; a walking body would fight it for the camera.
            _walking = false;
            SetUpBench(generator, viewRadius);
        }
        else
        {
            _camera.Position = _player.EyePosition;
            _camera.Pitch = -8f;

            // ⛳ The menu, over the world it is about to hand over. Not for the UI check, which opens
            // its own screens in turn and would find this one already up, and not for a timed play,
            // which is meant to be somebody playing.
            if (!_options.UiCheck && _options.PlaySeconds <= 0 && _options.ShotPath is null)
                OpenStartScreen(generator, viewRadius);
        }

        _animator.Reset(_camera.Yaw);
        _viewPosition = _camera.Position;
        _viewForward = _camera.Forward;

        // ⛳ Before the streamer is told where the viewer is, and the ordering is a requirement
        // rather than a preference. A saved edit is held for the chunk it belongs to and generation
        // is what takes delivery of it, so an edit read after its chunk has already been generated
        // has missed its moment. See VoxelWorld.Restore.
        LoadWorld();
        _startup.Mark("world opened");

        // Prime the pipeline before the first frame so the viewer does not open inside an empty
        // world, then let the render loop take delivery of the rest as it arrives.
        _streamer.Update(_camera.Position);
        _startup.Mark("first chunks queued");

        Console.WriteLine($"seed        {_seed}");
        Console.WriteLine($"view        {viewRadius} chunks ({reach} blocks), streaming");
        Console.WriteLine($"ocean       {generator.OceanCoverage * 100:F0}% of surface at or below sea level {TerrainGenerator.SeaLevel}");
        Console.WriteLine();

        if (_bench is not null)
        {
            Console.WriteLine($"bench       {_options.BenchSeconds:F0} s at {BenchPath.BlocksPerSecond:F0} blocks/s on a circle of r={_benchPath!.Radius:F0}, "
                            + $"after the world settles");
            Console.WriteLine();
            return;
        }

        // Read out of the bindings rather than typed, so it cannot go stale the way it just did —
        // it said Esc released the mouse for a while after Esc had stopped doing that.
        var keys = _settings.Keys;

        Console.WriteLine(
            $"{keys.Describe(GameAction.MoveForward)} move, {keys.Describe(GameAction.Jump)} jump, "
            + $"{keys.Describe(GameAction.Sneak)} sneak, {keys.Describe(GameAction.Sprint)} sprint");
        Console.WriteLine("Hold left to mine, right to place — the arm swings and the swing takes the block");
        Console.WriteLine(
            $"{keys.Describe(GameAction.OpenInventory)} inventory and crafting, "
            + $"{keys.Describe(GameAction.OpenOptions)} options — controls, video, audio, world");
        Console.WriteLine(
            $"{keys.Describe(GameAction.ToggleWireframe)} wireframe, "
            + $"{keys.Describe(GameAction.ToggleCulling)} culling, "
            + $"{keys.Describe(GameAction.ToggleFly)} walk/fly, "
            + $"{keys.Describe(GameAction.ToggleView)} view");
    }

    /// <summary>
    /// Works out which world this is, and reads enough of it to build the right one.
    /// </summary>
    /// <remarks>
    /// <para>Only the header, which is the first section and costs one file open. It carries the
    /// seed, and the seed has to be known before the generator, the climate field and the cloud
    /// sheet are made out of it — so this runs before all three rather than the world being built
    /// and then corrected.</para>
    /// <para><b>The seed names the world unless somebody names it themselves.</b> That is what makes
    /// closing the window and opening it again come back to the same place, which is the whole point
    /// of saving on quit and would not happen if every launch invented a name.</para>
    /// <para>⚠ <b>A benchmark and a UI check keep nothing and load nothing.</b> A measured flight
    /// over somebody's world is not the fixed path it reports itself as, and a check that writes a
    /// save file has reached out of its own process and changed something.</para>
    /// </remarks>
    /// <summary>
    /// The one world every instrument works in, so none of them ever opens somebody's own.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>WRITTEN BECAUSE THE INSTRUMENTS WERE PLAYING THE USER'S SAVE.</b> The bench and the
    /// ui-check have kept nothing and loaded nothing since the day saves landed — but <c>--play</c>
    /// exists precisely to close the window the way a player does, which means it <em>saves on
    /// quit</em>, and with no <c>--world</c> given it fell through to the same default name a
    /// double-click opens. Every timing run and every save-on-quit check has therefore been loading
    /// somebody's world, playing in it and writing it back.
    /// <para>⛳ It also fixes the thing the user actually noticed. Naming a different world per test
    /// run — <c>--world creature-test</c>, <c>--seed q1</c> — left ten worlds in their saves folder
    /// across two sessions, which reads from the game as a save list breeding by itself. One name,
    /// always the same one, and the list is theirs again.</para>
    /// <para>⚠ An explicit <c>--world</c> still wins, because otherwise there would be no way to
    /// point an instrument at a real world on purpose — but it has to be typed.</para>
    /// </remarks>
    private void OpenWorld()
    {
        _seed = _options.Seed;

        if (_options.BenchSeconds > 0 || _options.UiCheck) return;

        // Anything that drives the game itself rather than being played. ⛳ The rule lives in
        // WorldSave.NameFor with a check beside it, rather than here where nothing could see it.
        var instrument = _options.PlaySeconds > 0 || _options.ShotPath is not null;

        _worldName = WorldSave.NameFor(
            _options.WorldName, instrument,
            _options.SeedGiven, _options.SeedText ?? _options.Seed.ToString());

        if (instrument)
            Console.WriteLine($"world       instrument run, working in '{_worldName}' rather than a real world");

        var path = WorldSave.PathFor(_worldName);

        if (!File.Exists(path))
        {
            Console.WriteLine($"world       '{_worldName}' is new, and is written when you close the window");
            return;
        }

        if (!WorldSave.TryReadHeader(path, out var header))
        {
            // Named rather than refused. A file that will not open is still somebody's world as far
            // as they are concerned, and starting a fresh one silently over the top of it is worse
            // than anything that can be said about it.
            Console.Error.WriteLine(
                $"driftwood: '{_worldName}' would not open, so nothing has been loaded from it — "
                + "move the file aside before playing under that name or it will be written over");
            return;
        }

        _seed = WorldSeed.Parse(header.Seed);
        _playedBefore = header.Played;
        _savedDayTime = header.DayTime;
        _loadedWorld = true;

        // ⚠ Said out loud, because a --seed that appears to do nothing is otherwise a mystery.
        if (_options.SeedGiven && _seed.Value != _options.Seed.Value)
            Console.WriteLine(
                $"world       '{_worldName}' already exists and is made of seed {_seed}, "
                + $"so --seed {_options.Seed} is not being used");
    }

    /// <summary>
    /// Reads the world into the session that has just been built for it.
    /// </summary>
    /// <remarks>
    /// Everything it fills already exists: the streamer's world, the furnace and chest banks, the
    /// pockets, what is worn, health and breath, and which recipes have already been announced.
    /// Nothing here makes anything, which is what keeps a loaded world and a new one the same
    /// session in every other respect.
    /// </remarks>
    private void LoadWorld()
    {
        if (!_loadedWorld) return;

        var state = CaptureState();
        var missing = new List<string>();

        if (WorldSave.Read(WorldSave.PathFor(_worldName), _registry, _items, state, missing) is { } fault)
        {
            // The header opened and the body did not. Refusing to carry on loses the session;
            // carrying on and then saving over it loses the world. So: play, and keep nothing.
            Console.Error.WriteLine(
                $"driftwood: '{_worldName}' would not load: {fault}. "
                + "Nothing will be written over it this session.");
            _loadedWorld = false;
            _worldName = "";
            return;
        }

        _player.Teleport(state.Position);
        _spawnPoint = state.Position;
        _camera.Yaw = state.Yaw;
        _camera.Pitch = state.Pitch;
        _camera.Position = _player.EyePosition;
        _clock.SetTime(state.DayTime);

        // The herd does not exist yet — it is built on the first top-up after spawn — so the
        // save's animals wait here for it.
        _savedCreatures.Clear();
        _savedCreatures.AddRange(state.Creatures);

        // The carts stand back on the track exactly where they were; nobody is aboard, which is
        // also how they were written.
        _cartSystem.All.Clear();
        _ridingCart = null;
        foreach (var (cx, cy, cz, t, velocity) in state.Carts)
            _cartSystem.All.Add(new Cart { X = cx, Y = cy, Z = cz, T = t, Velocity = velocity });

        // ⚠ Brought up to date rather than left to work itself out. Poll announces everything the
        // pockets can pay for that has not been announced, so a world loaded with a full inventory
        // fires a notice for every one of them in the first frame unless it is primed first.
        _unlocks.Poll(_book, _inventory, _justUnlocked);
        _justUnlocked.Clear();

        Console.WriteLine(
            $"world       '{_worldName}' loaded — {_streamer.World.Edits.Count} changes, "
            + $"{_furnaces.Count} furnaces, {_chests.Count} chests, "
            + $"{_unlocks.Announced} recipes already announced, played {Spoken(_playedBefore)}");

        // Not a failure. A block this build no longer has leaves its cell as the generator made it,
        // which is the least surprising thing that can happen — and saying which ones is the
        // difference between that and a hole somebody finds a week later.
        if (missing.Count > 0)
            Console.Error.WriteLine(
                $"driftwood: {missing.Count} things this build no longer has were left out: "
                + string.Join(", ", missing.Take(6)));
    }

    /// <summary>Everything worth keeping, as it stands right now.</summary>
    /// <remarks>
    /// A record of references rather than a copy, so it costs nothing to take and is only ever read
    /// on the thread that owns all of it.
    /// </remarks>
    private WorldState CaptureState()
    {
        var state = new WorldState(
            _seed.ToString(), _items, _streamer.World, _furnaces, _chests,
            _inventory, _equipment, _vitals, _unlocks)
        {
            // Where the body is, not where the camera is. The camera is over the shoulder in two of
            // the three views, and loading into it would put the player a few blocks behind
            // themselves — and, over enough saves, through the wall behind them.
            Position = _player.Position,
            Yaw = _camera.Yaw,
            Pitch = _camera.Pitch,
            Played = _playedBefore + _elapsed,
            DayTime = _clock.TimeOfDay,
        };

        if (_herd is not null) state.Creatures.AddRange(_herd.Capture());

        foreach (var cart in _cartSystem.All)
            state.Carts.Add((cart.X, cart.Y, cart.Z, cart.T, cart.Velocity));

        return state;
    }

    /// <summary>
    /// Writes the world down.
    /// </summary>
    /// <remarks>
    /// The write is atomic inside <see cref="WorldSave"/>, so an interruption leaves the previous
    /// save whole rather than a truncated file where a world used to be. A failure is reported and
    /// is never a reason to refuse to close the window.
    /// </remarks>
    /// <returns>True when something was written.</returns>
    private bool SaveWorld(string why)
    {
        if (_worldName.Length == 0 || _streamer is null || _items is null) return false;

        _sinceSave = 0;

        // The step before this one, kept. Reported and never fatal: a world that will not write
        // because its backup would not is worse than a world with no backup.
        if (WorldSave.Backup(_worldName) is { } spare)
            Console.Error.WriteLine($"driftwood: could not keep a copy of '{_worldName}': {spare}");

        var state = CaptureState();
        _saveFault = WorldSave.Write(_worldName, state);

        if (_saveFault is not null)
        {
            Console.Error.WriteLine($"driftwood: saving '{_worldName}' {why} failed: {_saveFault}");
            return false;
        }

        Console.WriteLine(
            $"world       '{_worldName}' saved {why} — {state.World.Edits.Count} changes, "
            + $"played {Spoken(state.Played)}");

        // The list is on screen while a save-by-hand happens, so it has to follow.
        if (OnTab(GameTab.Saves)) ReadSavesFolder();
        if (OnTab(GameTab.Packs)) ReadPacksFolder();

        return true;
    }

    /// <summary>
    /// Writes the world every so often, once something has happened worth writing.
    /// </summary>
    /// <remarks>
    /// <para>⚠ <b>The clock is not reset when there is nothing to save</b>, and that is deliberate.
    /// Resetting it would mean a world stood still in for ten minutes and then built on saves ten
    /// minutes after the first block, rather than at the first block. As written, the period is a
    /// floor on how often a save can happen and not a delay before one can.</para>
    /// <para>Both flags are asked. Picking something up can announce a recipe without changing a
    /// single block, so a world that watched only itself would say there was nothing to write.</para>
    /// <para><b>An unlock is not its own trigger.</b> The task called for one, and it would fire
    /// twenty times in the first two minutes of a new world — the tree announces itself as fast as a
    /// player picks things up. Making the world dirty is the useful half of that trigger, and the
    /// period is what stops it being churn.</para>
    /// </remarks>
    /// <summary>Everything burning near the player, found slowly and fed every frame.</summary>
    /// <remarks>
    /// ⛳ <b>Two rates on purpose.</b> A flame lasts a third of a second, so it has to be fed every
    /// frame or it reads as a stutter — but <em>finding</em> the fires means sweeping the cells round
    /// the player, and doing that sixty times a second to place four particles is the wrong shape.
    /// Sweeping twice a second and emitting from what the sweep found is the same picture for a
    /// fortieth of the cost.
    /// </remarks>
    private void StepFires(float dt)
    {
        if (!_spawned) return;

        _fires ??= new Fires(_registry);
        _fires.Update(_streamer.World, _viewPosition, dt);

        // ⛳ Crops grow and fields wet and dry over the chunks that are actually MESHED, which is the
        // set the player can see. Growing a field nobody is near is work nobody watches, and the
        // mesh dictionary is already exactly that set — no second list to keep in step.
        //
        // ⚠ Rebuilt each frame rather than cached. It is a few hundred entries into a list that
        // keeps its capacity, and a cached copy is one more thing that can disagree with what is
        // loaded — which for a growth tick means growing crops in a chunk that has been unloaded.
        _growthChunks.Clear();
        foreach (var pos in _meshes.Keys)
            _growthChunks.Add((pos.X * Chunk.Size, pos.Y * Chunk.Size, pos.Z * Chunk.Size));

        _growth.Update(_streamer.World, _growthChunks, dt, _growthRandom);
        _fires.Emit(_particles, StarterBlocks.LayerFlame, StarterBlocks.LayerSmoke, dt);
    }

    private Fires? _fires;

    /// <summary>Ticks a second of the flow, and no more than a frame can pay for.</summary>
    /// <remarks>
    /// <para>⛳ <b>Five times a second, not sixty.</b> A fluid that settles the instant a wall comes
    /// down does not read as a fluid, it reads as the world changing shape — the whole point of
    /// breaking a block beside a river is watching the water find its way in.</para>
    /// <para><b>And a bounded number of cells per tick, which is what keeps a flood from being a
    /// frame hitch.</b> A cave system filling takes many ticks, which is exactly what it should look
    /// like, and the cost of a frame is a number rather than a cliff nobody meets until they break
    /// the wrong wall. The engine keeps its own queue, so the remainder is not lost — it is next
    /// tick's work.</para>
    /// <para>The cells it moved go to the streamer, which books their light and their mesh. It does
    /// <em>not</em> route them through the block-edit path: the flow has already written them, and
    /// that path would log every one of them as something to save.</para>
    /// </remarks>
    private void StepFluid(float dt)
    {
        const float TickSeconds = 0.2f;
        const int CellsPerTick = 256;

        if (!_walking || !_spawned) return;

        _fluidClock += dt;
        if (_fluidClock < TickSeconds) return;

        // Clamped rather than accumulated, so a stall does not spend the next frame catching up on
        // a second of ticks at once.
        _fluidClock = MathF.Min(_fluidClock - TickSeconds, TickSeconds);

        _streamer.StepFluid(CellsPerTick, _fluidMoved);
    }

    private float _fluidClock;

    /// <summary>Reused between ticks, so a settling river allocates nothing.</summary>
    private readonly List<(int X, int Y, int Z)> _fluidMoved = [];

    private SignalTable _signalTable = null!;
    private RailTable _railTable = null!;
    private CartSystem _cartSystem = null!;

    /// <summary>The cart under the player, or null on foot. The first ridden thing in the game.</summary>
    private Cart? _ridingCart;

    private float _signalClock;

    /// <summary>A plain accumulating clock for the button springs. Never wraps in a session.</summary>
    private double _signalNow;

    /// <summary>Pressed buttons and when each springs back.</summary>
    private readonly List<(int X, int Y, int Z, double When)> _buttonReleases = [];

    /// <summary>Plates something is standing on, re-checked every signal tick.</summary>
    private readonly HashSet<(int X, int Y, int Z)> _platesDown = [];

    private readonly HashSet<(int X, int Y, int Z)> _platesNow = [];

    /// <summary>
    /// The signal tick: gates think, buttons spring back, and plates follow the feet on them.
    /// </summary>
    /// <remarks>
    /// <para>⛳ Ten a second, and gates ONLY here — an inverter feeding itself is a contradiction as
    /// an equation and a clock as a machine, and the tick is what makes it the second one. The
    /// wire pass itself already ran inside whatever edit caused it.</para>
    /// <para>⚠ The plate scan asks where bodies ARE rather than keeping a list of plates, so a
    /// world loaded with somebody saved standing on one works itself out: the body is still there,
    /// the scan finds it, and stepping off releases it.</para>
    /// </remarks>
    private void StepSignals(double now, float dt)
    {
        if (!_walking || !_spawned) return;

        _signalClock += dt;
        if (_signalClock < 0.1f) return;
        _signalClock = MathF.Min(_signalClock - 0.1f, 0.1f);

        _streamer.TickSignals();

        // Buttons spring back — unless what is there stopped being a pressed button meanwhile.
        for (var i = _buttonReleases.Count - 1; i >= 0; i--)
        {
            var (x, y, z, when) = _buttonReleases[i];
            if (now < when) continue;

            _buttonReleases.RemoveAt(i);

            // Mined, or replaced, since it was pressed: nothing to spring back.
            var id = _streamer.World.GetBlock(x, y, z);
            if (!_signalTable.IsPressedButton(id.Value)) continue;

            _streamer.EditBlock(x, y, z, new BlockId(_buttonIdle[id.Value]));
            PlaySound(_registry[id], SoundEvent.Place, new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), 0.5f);
        }

        // Plates: the cells feet stand in, the player's and every creature's.
        _platesNow.Clear();
        NoteFoot(_player.Position);
        if (_herd is { } herd)
            foreach (var creature in herd.All)
                NoteFoot(creature.Position);

        foreach (var cell in _platesNow)
        {
            if (_platesDown.Contains(cell)) continue;

            var id = _streamer.World.GetBlock(cell.X, cell.Y, cell.Z).Value;
            if (_registry[id].Name != "pressure_plate") continue;

            _streamer.EditBlock(cell.X, cell.Y, cell.Z, _registry.ByName("pressure_plate_on").Id);
            PlaySound(
                _registry[id], SoundEvent.Place,
                new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f), 0.5f);
        }

        _platesDown.RemoveWhere(cell =>
        {
            if (_platesNow.Contains(cell)) return false;

            var id = _streamer.World.GetBlock(cell.X, cell.Y, cell.Z).Value;
            if (_registry[id].Name == "pressure_plate_on")
                _streamer.EditBlock(cell.X, cell.Y, cell.Z, _registry.ByName("pressure_plate").Id);
            return true;
        });

        foreach (var cell in _platesNow)
            if (_registry[_streamer.World.GetBlock(cell.X, cell.Y, cell.Z)].Name == "pressure_plate_on")
                _platesDown.Add(cell);

        // What the wiring switched on its own — a door swung by a wire — gets its voice here.
        foreach (var (x, y, z, id) in _streamer.SignalSwitched)
        {
            var swung = _registry[_waterlogging.DryOf(id)].Name;
            var at = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);

            // ⛳ The second ignition door: a powered wire lit a cask. The sink is one-way in the
            // table, so the fuse starting here can never be un-started by the wire going dark.
            if (swung == Blastcask.Lit) LightFuse(x, y, z, Blastcask.FuseSeconds);

            if (swung.Contains("door", StringComparison.Ordinal))
                _audio?.Play(
                    Pick(swung.EndsWith("_open", StringComparison.Ordinal)
                        ? ActionSounds.DoorOpen
                        : ActionSounds.DoorClose),
                    at, 0.8f, Wobble());
            else
                PlaySound(_registry[id], SoundEvent.Place, at, 0.6f);
        }
        _streamer.SignalSwitched.Clear();

        void NoteFoot(Vector3 position)
        {
            var cell = (
                X: (int)MathF.Floor(position.X),
                Y: (int)MathF.Floor(position.Y),
                Z: (int)MathF.Floor(position.Z));
            _platesNow.Add(cell);
        }
    }

    /// <summary>Each pressed button's idle form, for the spring back.</summary>
    private ushort[] _buttonIdle = null!;

    /// <summary>
    /// Rolls the carts, drops any whose rail was mined out from under them, and keeps a rider in
    /// the seat.
    /// </summary>
    private void StepCarts(float dt)
    {
        if (!_walking || !_spawned) return;

        var homeless = _cartSystem.Step(_streamer.World, dt);

        if (homeless is not null)
        {
            foreach (var cart in homeless)
            {
                var at = new Vector3(cart.X + 0.5f, cart.Y + 0.5f, cart.Z + 0.5f);
                _drops.Drop(new ItemStack(_items.ByName("cart").Id, 1), at);
                if (_ridingCart == cart) _ridingCart = null;
            }
        }

        if (_ridingCart is not { } riding) return;

        // The seat: the body parks on the cart and the camera follows the eyes as it always does.
        var form = _railTable.FormOf(
            _streamer.World.GetBlock(riding.X, riding.Y, riding.Z).Value);
        if (form == RailForm.None) return;

        _player.Teleport(riding.Position(form) + new Vector3(0f, 0.35f, 0f));
    }

    private void StepAutosave(double dt)
    {
        // Nobody is playing yet, so there is nothing to keep and the flight would otherwise write
        // the world out every two minutes for as long as the menu was left up.
        if (_worldName.Length == 0 || _bench is not null || _atStartScreen) return;

        _sinceSave += dt;
        if (_sinceSave < AutosaveSeconds) return;
        if (!_streamer.World.Changed && !_unlocks.Dirty) return;

        _autosaves++;
        SaveWorld("automatically");
    }

    /// <summary>
    /// Puts the menu up over the world, and sets the camera flying round it.
    /// </summary>
    /// <remarks>
    /// The body is not stepped and the mouse is not taken for looking, so nothing the player does
    /// here moves anybody. The flight is <see cref="BenchPath"/>, which already flies a circle that
    /// follows the ground for the benchmark — a slower one, because the benchmark is sized to turn
    /// the whole loaded set over and this is meant to be looked at.
    /// </remarks>
    private void OpenStartScreen(TerrainGenerator generator, int viewRadius)
    {
        _atStartScreen = true;
        _walking = false;
        _menuTime = 0;

        // Close in, because the point is the world rather than the horizon — and low, so the
        // terrain has a silhouette against the sky rather than being a map seen from above.
        _menuPath = new BenchPath(viewRadius * Chunk.Size * 0.55f, TerrainGenerator.SeaLevel, generator.SurfaceHeight);

        var (position, yaw, pitch) = _menuPath.At(0);
        _camera.Position = position;
        _camera.Yaw = yaw;
        _camera.Pitch = pitch;

        ShowStartMenu();

        // Beside the other startup lines, because "the menu says there are no saved worlds" and
        // "there are no saved worlds" look identical from the front and have completely different
        // causes. One line settles which, and the folder is on it because that is the next question.
        Console.WriteLine(
            $"menu        {_saved.Count} world{(_saved.Count == 1 ? "" : "s")} in {WorldSave.Folder}");

        // ⛔ Named one by one, because this is the case the line above cannot express: a folder with
        // files in it that the menu is about to describe as empty. One line each, with the reason,
        // so "there is nothing saved" and "there is something saved I cannot open" are never again
        // the same sentence.
        foreach (var bad in _unreadable)
            Console.Error.WriteLine($"menu        cannot read '{bad.File}' - {bad.Why}");
    }

    /// <summary>
    /// Puts the menu itself up, without touching the camera.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="OpenStartScreen"/> so the UI check can open the menu on a world it is
    /// already standing in, the way it opens every other screen. A check that had to fly a camera to
    /// look at a panel would be measuring the flight.
    /// </remarks>
    private void ShowStartMenu()
    {
        // ⛔ THE FOLDER, HERE, EVERY TIME THE MENU COMES UP. It was read only when the saves *tab*
        // was opened, so at boot the list was the empty one the field starts as and the menu said
        // "none saved yet" to somebody who had saved and closed the game a minute earlier. Their
        // world was there and had loaded — the row above even said so — but the one line they were
        // reading was a field nothing had filled in.
        ReadSavesFolder();

        _hudScreen.Kind = HudScreenKind.Start;
        _hudScreen.TabNames = [];
        _hudScreen.Tab = 0;
        _hudScreen.Selected = 0;
        _hudScreen.Scroll = 0;
        _startListing = false;

        TakeThePointer();
        RefreshScreen();
        ShowSelectedRow();
    }

    /// <summary>Hands the world over: the menu goes, the body wakes up, the mouse looks again.</summary>
    private void StartPlaying()
    {
        _atStartScreen = false;
        _menuPath = null;
        _walking = true;

        _hudScreen.Kind = HudScreenKind.None;
        _hudScreen.TabNames = [];
        _hudScreen.Rows.Clear();

        // Back where the save left them, or the spawn on a new world — either way not where the
        // camera has been flying, which is somewhere over the hills.
        _player.Teleport(_spawnPoint);
        _camera.Position = _player.EyePosition;
        _spawned = false;

        SetMouseCaptured(true);
    }

    /// <summary>
    /// Saves what is open and starts again pointed at another world.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>A relaunch rather than a rebuild, and it is the honest trade.</b> Swapping worlds
    /// in place means tearing down a <see cref="WorldStreamer"/> that owns a pool of workers and a
    /// lighting thread, releasing every uploaded mesh, and rebuilding half of
    /// <see cref="BuildWorld"/> — under a live session, for something that happens once. Starting
    /// again does the same thing with nothing to get wrong, and the world is written on the way out
    /// either way.</para>
    /// <para>⚠ <b>The rest of the command line is carried over.</b> A <c>--pack</c> or a
    /// <c>--skin</c> is a choice about the installation rather than about the world, and losing it
    /// on the way through the menu would read as the pack having been forgotten. <c>--seed</c> is
    /// dropped on purpose: the world being opened has its own.</para>
    /// </remarks>
    /// <param name="seed">
    /// A seed typed into the menu's box, for a world being made rather than opened. ⚠ Never passed
    /// for a world that already exists: the header carries its own and would refuse this one.
    /// </param>
    private void OpenAnotherWorld(string name, string seed = "")
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            Console.Error.WriteLine("driftwood: cannot find this program on disk, so another world cannot be opened");
            return;
        }

        SaveWorld("before opening another world");

        var carried = new List<string>();
        var was = Environment.GetCommandLineArgs();

        for (var i = 1; i < was.Length; i++)
        {
            // These three say which world, and this is the one place that decides that now.
            if (was[i] is "--world" or "--seed" or "--play") { i++; continue; }
            carried.Add(was[i]);
        }

        carried.Add("--world");
        carried.Add(name);

        if (seed.Length > 0)
        {
            carried.Add("--seed");
            carried.Add(seed);
        }

        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var argument in carried) start.ArgumentList.Add(argument);

        try
        {
            Process.Start(start);
            _stopRequested = true;
        }
        catch (Exception fault)
        {
            Console.Error.WriteLine($"driftwood: could not open '{name}': {fault.Message}");
        }
    }

    /// <summary>
    /// A world name nothing on disk is under yet.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Checked against the folder rather than against the list the screen is showing.</b> The
    /// list is read when the menu is arrived at; the folder is what a name would collide with, and
    /// a collision here would open somebody's world under the words "make another world".
    /// </remarks>
    private static string NextWorldName()
    {
        for (var n = 2; n < 1000; n++)
        {
            var name = $"world-{n}";
            if (!File.Exists(WorldSave.PathFor(name))) return name;
        }

        return "world-new";
    }

    /// <summary>A span of seconds as a person would say it.</summary>
    private static string Spoken(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{span.Minutes}m {span.Seconds}s";
    }

    /// <summary>
    /// Points the camera at the start of the measured flight and hands it to a fresh collector.
    /// </summary>
    /// <remarks>
    /// The circle is sized off the streaming radii rather than picked: the flight has to end up
    /// further from its start than the drop radius, or the loaded set merely drifts and no chunk is
    /// ever forgotten and fetched again. Half the streaming pipeline would go unmeasured, and it is
    /// the half that runs when a player doubles back — which is most of the time. At the default
    /// 15 s and 48 blocks/s that is 720 blocks of arc, a 640-block chord, comfortably past the
    /// 352-block drop radius.
    /// </remarks>
    private void SetUpBench(TerrainGenerator generator, int viewRadius)
    {
        const int DropMarginChunks = 3;   // matches WorldStreamer's drop radius
        var radius = (viewRadius + DropMarginChunks) * Chunk.Size * 1.25f;

        _benchPath = new BenchPath(radius, TerrainGenerator.SeaLevel, generator.SurfaceHeight);
        _bench = new FrameBench(_options.BenchSeconds);

        var (position, yaw, pitch) = _benchPath.At(0);
        _camera.Position = position;
        _camera.Yaw = yaw;
        _camera.Pitch = pitch;
    }

    /// <summary>
    /// Takes delivery of finished meshes and releases the buffers of chunks that streamed out.
    /// Upload count is capped per frame; see <see cref="MaxUploadsPerFrame"/>.
    /// </summary>
    private void PumpStreaming()
    {
        _streamer.Update(_camera.Position);
        _streamer.PromoteReadyChunks();

        while (_streamer.TryDequeueDropped(out var dropped))
        {
            if (!_meshes.Remove(dropped, out var stale)) continue;
            _totalVertices -= stale.VertexCount;
            _totalTriangles -= stale.IndexCount / 3;
            stale.Dispose();
        }

        for (var i = 0; i < _maxUploadsPerFrame; i++)
        {
            if (!_streamer.TryDequeueMesh(out var data)) break;
            _uploadsThisFrame++;

            // A remesh replaces the previous buffers for that chunk rather than leaking them.
            if (_meshes.Remove(data.Position, out var previous))
            {
                _totalVertices -= previous.VertexCount;
                _totalTriangles -= previous.IndexCount / 3;
                previous.Dispose();
            }

            _meshes[data.Position] = new ChunkMeshGpu(_gl, data);
            _totalVertices += data.VertexCount;
            _totalTriangles += data.TriangleCount;
        }
    }

    private void OnKeyDown(Key key)
    {
        // The controls tab is listening. It takes the key raw — before it is looked up — because
        // the whole point is to bind whatever was pressed, including the key that already does
        // something else.
        if (_rebinding is { } waiting)
        {
            FinishRebind(waiting, key);
            return;
        }

        // A screen takes the keyboard while it is open, and gives it back on the way out. Letting
        // the world keep reading keys underneath is how a player closes an inventory by walking
        // into a wall.
        if (_hudScreen.IsOpen && ScreenKey(key)) return;

        if (_keys.ActionFor(key) is not { } action) return;

        switch (action)
        {
            // What this character is carrying and can make. The bench a player always has is two by
            // two, in their own hands; anything wider needs a real one, and the screen says so by
            // simply not listing it.
            case GameAction.OpenInventory:
                if (_bench is not null) break;
                OpenPlayer(PlayerTab.Items, atBench: false, default);
                break;

            // What this installation is set to. Opening it gives the mouse back, which is one
            // gesture in every game in this space and is why nothing else needs to.
            case GameAction.OpenOptions:
                if (_bench is not null) break;
                OpenGame(GameTab.Controls);
                break;

            case GameAction.ToggleWireframe:
                _wireframe = !_wireframe;
                _gl.PolygonMode(TriangleFace.FrontAndBack, _wireframe ? PolygonMode.Line : PolygonMode.Fill);
                break;

            // Toggling culling must change the chunk count in the title and nothing on screen.
            // If anything pops in or out, the planes are wrong.
            case GameAction.ToggleCulling:
                _frustumCulling = !_frustumCulling;
                break;

            // Leaving the fly camera in reach. It is how terrain gets looked at, and a bug you can
            // only reach by walking to it is a bug you look at twice.
            case GameAction.ToggleFly:
                if (_bench is not null) break;
                ToggleFly();
                break;

            // First, over the shoulder, then facing. The middle one is the reason the model exists;
            // the third is how you look at your own skin, and every game in the genre has it.
            case GameAction.ToggleView:
                if (_bench is not null) break;
                CycleView();
                break;

            // ⛳ Trading the hands, which is what makes the offhand a place a player keeps something
            // rather than a slot they filled once. A torch and a pickaxe swap constantly.
            case GameAction.SwapHands:
                if (_bench is not null) break;
                SwapHands();
                break;

            // Holding the clock still. A sky is judged by eye at a particular hour, and waiting
            // twenty minutes for the one you wanted to look at again is how a colour ramp ends up
            // checked at noon and nowhere else.
            case GameAction.HoldClock:
                _clock.Running = !_clock.Running;
                break;

            // Winding it. A tenth of a day a press, which walks dawn to dusk in five.
            case GameAction.WindClock:
                _clock.SetTime(_clock.TimeOfDay + 0.1f);
                _skyState = _clock.Now;
                break;

            // The hand. The number row picks directly and the wheel walks it, which is where every
            // hand in the genre lives and where a player will look for it without being told.
            case >= GameAction.Slot1 and <= GameAction.Slot9:
                SelectHandSlot(action - GameAction.Slot1);
                break;
        }
    }

    private void ToggleFly()
    {
        _walking = !_walking;
        if (!_walking) return;

        _player.Teleport(_camera.Position - new Vector3(0f, _player.CurrentEyeHeight, 0f));
        _spawned = false;
    }

    private void CycleView() => _view = _view switch
    {
        ViewMode.First => ViewMode.ThirdBehind,
        ViewMode.ThirdBehind => ViewMode.ThirdFacing,
        _ => ViewMode.First,
    };

    /// <summary>
    /// Handles a key while a screen is open. Returns true when the screen took it.
    /// </summary>
    /// <remarks>
    /// Arrow keys, because the hands are already there — they are this player's movement keys, and
    /// while a screen is up nothing is moving. Enter acts, shift makes it act as many times as it
    /// can, and both E and escape close.
    /// </remarks>
    /// <summary>
    /// A character somebody typed, when there is a box for it to go into.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Separate from the key handler and it has to be.</b> A key is a place on a keyboard; a
    /// character is what the platform decided that key produced, after the layout and the modifiers
    /// and any dead key before it. Typing into a box from the key events would mean writing that
    /// table out again, and getting it wrong for everybody whose keyboard is not this one.
    /// </remarks>
    /// <summary>Hands the keyboard to a box.</summary>
    private void StartTyping(TextField field, Action<bool>? done = null)
    {
        _hudScreen.Typing = field;
        _typingWas = field.Text;
        _typingDone = done;
        field.End();
        RefreshScreen();
    }

    /// <summary>
    /// Takes it back, either keeping what was typed or putting back what was there.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Escape puts the old text back rather than merely closing the box.</b> A box that keeps
    /// what was typed whichever way it was left has no way out that undoes anything, and escape is
    /// where everybody looks for that.
    /// </remarks>
    private void StopTyping(bool accept)
    {
        var field = _hudScreen.Typing;
        var done = _typingDone;

        if (!accept && field is not null) field.Text = _typingWas;

        _hudScreen.Typing = null;
        _typingDone = null;
        _typingWas = "";

        done?.Invoke(accept);
        RefreshScreen();
        ShowSelectedRow();
    }

    private void OnCharTyped(char typed)
    {
        if (_hudScreen.Typing is not { } field) return;
        if (!field.Insert(typed)) return;

        RefreshScreen();
    }

    /// <summary>
    /// The keys a box takes while it has the keyboard, and the two that give it back.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Everything else is swallowed.</b> A screen is full of single-letter shortcuts and the
    /// letters are exactly what somebody is typing, so while a box is open the screen must not hear
    /// any of them — B cannot fold out the recipe book in the middle of a word. Escape abandons and
    /// enter accepts, which are the only two ways out and are the two every box in every program
    /// has.
    /// </remarks>
    private bool TypingKey(TextField field, Key key)
    {
        switch (key)
        {
            case Key.Escape:
                StopTyping(accept: false);
                return true;

            case Key.Enter or Key.KeypadEnter:
                StopTyping(accept: true);
                return true;

            case Key.Backspace: field.Backspace(); break;
            case Key.Delete: field.Delete(); break;
            case Key.Left: field.Left(); break;
            case Key.Right: field.Right(); break;
            case Key.Home: field.Home(); break;
            case Key.End: field.End(); break;

            case Key.V when _input.IsKeyPressed(Key.ControlLeft) || _input.IsKeyPressed(Key.ControlRight):
                // Through the field's own gate, so a pasted newline or slash is refused exactly as
                // a typed one is. The first line only: this is one line of text.
                var pasted = _input.Clipboard;
                var stop = pasted.IndexOfAny(['\r', '\n']);
                field.Insert(stop < 0 ? pasted : pasted[..stop]);
                break;
        }

        RefreshScreen();
        return true;
    }

    private bool ScreenKey(Key key)
    {
        // Before anything else. While a box has the keyboard it has all of it.
        if (_hudScreen.Typing is { } typing) return TypingKey(typing, key);

        var many = _input.IsKeyPressed(Key.ShiftLeft) || _input.IsKeyPressed(Key.ShiftRight);
        // The menu is a list of rows exactly as a settings tab is, so it wants the same keys —
        // up and down to pick, enter to act. It simply has no tabs to walk.
        var tabbed = _hudScreen.Kind is HudScreenKind.Player or HudScreenKind.Game or HudScreenKind.Start;

        // On a panel of squares the arrows drive the pointer itself rather than a separate
        // selection, so there is one cursor and not two things arguing about what is picked out.
        // Enter is then simply a left click where it is, which is also what a gamepad will want.
        if (_hudScreen.IsContainer)
        {
            switch (key)
            {
                case Key.Up: MovePointer(0, -1); return true;
                case Key.Down: MovePointer(0, 1); return true;
                case Key.Left: MovePointer(-1, 0); return true;
                case Key.Right: MovePointer(1, 0); return true;

                // The book, on the key that has always opened one.
                case Key.B:
                    _hudScreen.BookOut = !_hudScreen.BookOut;
                    RefreshScreen();
                    return true;

                case Key.Enter or Key.KeypadEnter or Key.Space:
                    ScreenClick(MouseButton.Left);
                    RefreshScreen();
                    return true;

                // The other gesture: half a stack, then one at a time. The same thing the right
                // button does, because there is no second button on a keyboard.
                case Key.Backspace:
                    ScreenClick(MouseButton.Right);
                    RefreshScreen();
                    return true;
            }
        }

        switch (key)
        {
            // Whichever key opened it closes it, and escape always does — a screen you cannot back
            // out of with escape is one people press escape at twice and then alt-tab out of.
            case Key.Escape:
                CloseScreen();
                return true;

            case var _ when _keys.ActionFor(key) is GameAction.OpenInventory or GameAction.OpenOptions:
                CloseScreen();
                return true;

            // Tab walks the tabs, which is where a hand already is and what every other program
            // does with it. Shift walks back.
            case Key.Tab when tabbed && _hudScreen.TabNames.Length > 1:
                var count = _hudScreen.TabNames.Length;
                if (_hudScreen.Kind == HudScreenKind.Game) _tabRow[_hudScreen.Tab] = _hudScreen.Selected;
                _hudScreen.Tab = (_hudScreen.Tab + (many ? count - 1 : 1)) % count;
                if (_hudScreen.Kind == HudScreenKind.Game) _hudScreen.Selected = _tabRow[_hudScreen.Tab];
                _hudScreen.Scroll = 0;

                // Arriving at the list is when it is worth walking the folder, and the only other
                // way in is OpenGame. Missing this is how a tab shows an empty list until it is
                // opened a second time.
                if (OnTab(GameTab.Saves)) ReadSavesFolder();
        if (OnTab(GameTab.Packs)) ReadPacksFolder();

                RefreshScreen();
                ShowSelectedRow();
                return true;

            case Key.Enter or Key.KeypadEnter or Key.Space:
                if (tabbed) ActivateRow();
                return true;

            case Key.Left or Key.A:
                Step(-1, horizontal: true);
                return true;

            case Key.Right or Key.D:
                Step(1, horizontal: true);
                return true;

            case Key.Up or Key.W:
                Step(-1, horizontal: false);
                return true;

            case Key.Down or Key.S:
                Step(1, horizontal: false);
                return true;

            // The bar still picks, so a player can choose what to feed a furnace without closing it.
            case >= Key.Number1 and <= Key.Number9:
                SelectHandSlot(key - Key.Number1);
                RefreshScreen();
                return true;
        }

        return false;

        void Step(int by, bool horizontal)
        {
            // On a settings tab, up and down pick a line and left and right change it. Headings are
            // skipped rather than selectable, so holding down never lands on one.
            if (!horizontal)
            {
                MoveRow(Math.Sign(by));
                return;
            }

            AdjustRow(Math.Sign(by));
        }
    }

    /// <summary>
    /// A click on the open screen, wherever the pointer is.
    /// </summary>
    /// <remarks>
    /// <para>Every gesture in this method is one a player in this genre already has in their hands:
    /// left picks a stack up and puts it down, right takes half and then lays it out one at a time,
    /// and shift sends a slot to the other half of the inventory. None of it is invented, and that
    /// is the point — a screen whose rules have to be learned is a screen that gets closed.</para>
    /// <para>The keyboard is not replaced by any of it. Arrows and enter still walk the same
    /// screens, because that is how this project's player already navigates and a screen that only
    /// works with a mouse would be a step backwards from what shipped.</para>
    /// </remarks>
    private void ScreenClick(MouseButton button)
    {
        var many = _input.IsKeyPressed(Key.ShiftLeft) || _input.IsKeyPressed(Key.ShiftRight);
        var at = _layout.At(_hudScreen.Pointer.X, _hudScreen.Pointer.Y);

        // A click that lands on something says so, quietly and under whatever the something
        // itself has to say. Bare panel stays silent, so mis-clicks read as misses.
        if (at is not null)
            _audio?.Play(Pick(ActionSounds.Click), _viewPosition, 0.3f, 1f);

        // The layout is a frame behind, so on the one frame after a screen changes it still
        // describes the one that was there. Squares belonging to a panel nobody is looking at are
        // ignored rather than acted on: a single frame of nothing happening, instead of a stack
        // moving on a screen that is not showing it.
        if (at is { Kind: ZoneKind.Slot or ZoneKind.Recipe } && !_hudScreen.IsContainer) return;

        // Clicked away from everything while holding something: put it down in the world. The one
        // way to get rid of a stack on purpose, and the answer to "how do I close this".
        if (at is null)
        {
            if (button != MouseButton.Left || _hudScreen.Carried.IsEmpty) return;

            _drops.Drop(_hudScreen.Carried, _player.Position + new Vector3(0f, 1f, 0f), 0.4f);
            _hudScreen.Carried = ItemStack.Empty;
            PlaySound(SoundMaterial.Wood, SoundEvent.Place, _viewPosition, 0.4f);
            return;
        }

        switch (at.Value.Kind)
        {
            case ZoneKind.Tab:
                if (_hudScreen.Kind == HudScreenKind.Game) _tabRow[_hudScreen.Tab] = _hudScreen.Selected;
                _hudScreen.Tab = at.Value.Index;
                if (_hudScreen.Kind == HudScreenKind.Game) _hudScreen.Selected = _tabRow[_hudScreen.Tab];
                _hudScreen.Scroll = 0;
                if (OnTab(GameTab.Saves)) ReadSavesFolder();
        if (OnTab(GameTab.Packs)) ReadPacksFolder();
                _shown.Clear();
                RefreshScreen();
                ShowSelectedRow();
                return;

            // The whole track, not a thumb with two arrows on the ends. Clicking anywhere on it puts
            // that share of the list on screen, and holding the button drags it.
            case ZoneKind.Scrollbar:
                if (button != MouseButton.Left) return;
                _draggingScrollbar = true;
                DragScrollbar(at.Value);
                return;

            case ZoneKind.Row:
                _hudScreen.Selected = at.Value.Index;

                // Left acts on the row the way enter does; right walks a setting the other way,
                // which is what the left and right arrows already do to it.
                if (button == MouseButton.Left) ActivateRow();
                else if (button == MouseButton.Right) AdjustRow(-1);
                RefreshScreen();
                return;

            // Clicking in a box is how everybody starts typing in one, and it is the same act as
            // pressing enter on its row — so it goes through the same door.
            case ZoneKind.Field:
                if (button != MouseButton.Left) return;
                _hudScreen.Selected = at.Value.Index;
                ActivateRow();
                RefreshScreen();
                return;

            case ZoneKind.Button:
                switch ((ScreenButton)at.Value.Index)
                {
                    case ScreenButton.Book:
                        _hudScreen.BookOut = !_hudScreen.BookOut;
                        break;

                    case ScreenButton.PageBack:
                        _hudScreen.BookPage = Math.Max(0, _hudScreen.BookPage - 1);
                        break;

                    case ScreenButton.PageForward:
                        var pages = Math.Max(1,
                            (_hudScreen.Recipes.Count + ScreenLayout.BookPage - 1) / ScreenLayout.BookPage);
                        _hudScreen.BookPage = Math.Min(pages - 1, _hudScreen.BookPage + 1);
                        break;
                }

                RefreshScreen();
                return;

            case ZoneKind.Recipe when _hudScreen.Kind == HudScreenKind.Stonecutter:
                // A cut is only ever picked. Nothing is spent until the result is taken, so one
                // click is safe here where it would not be in the book.
                if (at.Value.Index < _hudScreen.Cuts.Count) _hudScreen.Cut = at.Value.Index;
                RefreshScreen();
                return;

            // ⛳ A fire's book loads the fire rather than a grid, which is the same gesture one
            // station over: pick a row, click it again, and what it needs comes out of the pockets
            // and onto the flame. A book you can read and not act on, in a screen where every other
            // book acts, reads as a list that has not been wired up.
            case ZoneKind.Recipe when _hudScreen.Kind == HudScreenKind.Furnace:
                if (_hudScreen.Selected == at.Value.Index) LoadFire(at.Value.Index, many);
                else _hudScreen.Selected = at.Value.Index;
                RefreshScreen();
                return;

            case ZoneKind.Recipe:
                // The first click picks a recipe out; a click on the one already picked lays it
                // into the grid. Selecting and acting on one press would make a mis-click move
                // things out of the pockets, and shift makes as many as it can outright.
                if (many) CraftSelected(at.Value.Index, all: true);
                else if (_hudScreen.Selected == at.Value.Index) LayOut(at.Value.Index);
                else _hudScreen.Selected = at.Value.Index;
                RefreshScreen();
                return;

            case ZoneKind.Slot:
                ClickSlot(at.Value, button, many);
                RefreshScreen();
                return;
        }
    }

    /// <summary>True while the left button is down on the scrollbar.</summary>
    private bool _draggingScrollbar;

    /// <summary>Puts the pointer's share of the track on screen.</summary>
    private void DragScrollbar(Zone track)
    {
        var lines = ScreenLayout.MenuLines(LayoutHeight);
        var span = Math.Max(0, _hudScreen.Rows.Count - lines);
        if (span == 0) return;

        // Measured from the middle of where the thumb would be rather than from its top, so the
        // list does not jump by half a thumb the moment the button goes down on it.
        var thumb = MathF.Max(10f, MathF.Round(track.H * lines / _hudScreen.Rows.Count));
        var travel = MathF.Max(1f, track.H - thumb - 4f);
        var along = (_hudScreen.Pointer.Y - track.Y - thumb * 0.5f) / travel;

        ScrollRows((int)MathF.Round(along * span));
    }

    /// <summary>One click on one square, whatever that square is a square of.</summary>
    private void ClickSlot(Zone zone, MouseButton button, bool many)
    {
        var carried = _hudScreen.Carried;
        var giving = zone.Role is SlotRole.Result or SlotRole.Smelted or SlotRole.Cut;

        // A square that only gives is a different gesture entirely. There is nothing to put into
        // it, taking from it spends the arrangement rather than a slot, and shift-clicking it is
        // the "make as many as I can afford" every player in this genre reaches for.
        if (giving)
        {
            TakeFromResult(zone, many);
            return;
        }

        // ⛳ A hand in the grid ends the repeat. Everything below this line changes the arrangement
        // by hand, and re-laying a remembered recipe over the top of what somebody has just put
        // there themselves is the screen arguing with them.
        if (zone.Role == SlotRole.Craft) _laidOut = null;

        var half = button == MouseButton.Right;

        // Shift sends a whole slot somewhere sensible without the cursor being involved at all.
        if (many && button == MouseButton.Left && carried.IsEmpty)
        {
            SweepSlot(zone);
            return;
        }

        if (carried.IsEmpty)
        {
            var lifted = half ? TakeHalfFrom(zone) : TakeAllFrom(zone);
            if (lifted.IsEmpty) return;

            _hudScreen.Carried = lifted;
            PlaySound(MaterialOf(lifted), SoundEvent.Place, _viewPosition, 0.35f);
            return;
        }

        var over = half ? PutOneInto(zone, carried) : PutInto(zone, carried);
        if (over == carried) return;

        _hudScreen.Carried = over;
        PlaySound(MaterialOf(carried), SoundEvent.Place, _viewPosition, 0.35f);
    }

    private ItemStack TakeAllFrom(Zone zone) => zone.Role switch
    {
        SlotRole.Pocket => _inventory.TakeAll(zone.Index),
        SlotRole.Craft => _hudScreen.Grid?.TakeAll(zone.Index) ?? ItemStack.Empty,
        SlotRole.Equip => _equipment.TakeAll((EquipSlot)zone.Index),
        SlotRole.Smelting or SlotRole.Fuel => EmptyFurnaceSlot(zone.Role),
        SlotRole.Stored => TakeStored(zone.Index, half: false),
        SlotRole.Cutting => TakeCutting(),
        _ => ItemStack.Empty,
    };

    /// <summary>Lifts the rock off a stonecutter's bed, and forgets what it was going to become.</summary>
    private ItemStack TakeCutting()
    {
        var lifted = _hudScreen.Cutting;
        if (lifted.IsEmpty) return ItemStack.Empty;

        _hudScreen.Cutting = ItemStack.Empty;
        RefreshCuts();
        return lifted;
    }

    private ItemStack TakeHalfFrom(Zone zone) => zone.Role switch
    {
        SlotRole.Pocket => _inventory.TakeHalf(zone.Index),
        SlotRole.Craft => _hudScreen.Grid?.TakeHalf(zone.Index) ?? ItemStack.Empty,
        SlotRole.Stored => TakeStored(zone.Index, half: true),
        _ => TakeAllFrom(zone),
    };

    private ItemStack PutInto(Zone zone, ItemStack carried) => zone.Role switch
    {
        SlotRole.Pocket => _inventory.PutInto(zone.Index, carried),
        SlotRole.Craft => _hudScreen.Grid?.Put(zone.Index, carried) ?? carried,
        SlotRole.Equip => _equipment.Put((EquipSlot)zone.Index, carried),
        SlotRole.Smelting or SlotRole.Fuel => FeedFurnace(zone.Role, carried),
        SlotRole.Stored => PutStored(zone.Index, carried, one: false),
        SlotRole.Cutting => PutCutting(carried),
        _ => carried,
    };

    /// <summary>
    /// Lays a rock on the bed, and refuses anything the saw has nothing to do with.
    /// </summary>
    /// <remarks>
    /// Refusing rather than accepting-and-offering-nothing, so a slot that takes a stack is a slot
    /// that will do something with it. A stonecutter holding a log with an empty list beside it
    /// reads as broken.
    /// </remarks>
    private ItemStack PutCutting(ItemStack carried)
    {
        if (carried.IsEmpty) return carried;
        if (!_book.Offers(CraftStation.Stonecutter, carried.Item).Any()) return carried;

        var there = _hudScreen.Cutting;

        if (there.IsEmpty || there.Matches(carried))
        {
            _hudScreen.Cutting = there.Merge(carried, _items[carried.Item].MaxStack, out var over);
            RefreshCuts();
            return over;
        }

        _hudScreen.Cutting = carried;
        RefreshCuts();
        return there;
    }

    private ItemStack PutOneInto(Zone zone, ItemStack carried) => zone.Role switch
    {
        SlotRole.Pocket => _inventory.PutOne(zone.Index, carried),
        SlotRole.Craft => _hudScreen.Grid?.PutOne(zone.Index, carried) ?? carried,
        SlotRole.Stored => PutStored(zone.Index, carried, one: true),
        _ => PutInto(zone, carried),
    };

    /// <summary>Lifts one chest slot, all of it or half.</summary>
    private ItemStack TakeStored(int slot, bool half)
    {
        if (_hudScreen.Stored is not { } chest) return ItemStack.Empty;

        var there = chest.Contents[slot];
        if (there.IsEmpty) return ItemStack.Empty;

        if (!half)
        {
            chest.Contents[slot] = ItemStack.Empty;
            return there;
        }

        var taken = (there.Count + 1) / 2;
        chest.Contents[slot] = there.Minus(taken);
        return there with { Count = taken };
    }

    /// <summary>
    /// Puts what is carried into one chest slot, and hands back whatever would not go.
    /// </summary>
    /// <remarks>
    /// The same three gestures every other slot in the game answers: drop what fits onto a matching
    /// stack, swap with what is there when it does not match, and put down exactly one on a right
    /// click. Written out rather than shared with <see cref="Inventory"/> because a chest is not an
    /// inventory — it has no selected slot, no bar and no sweep — and the day it grows a lid the two
    /// would have had to come apart again.
    /// </remarks>
    private ItemStack PutStored(int slot, ItemStack carried, bool one)
    {
        if (_hudScreen.Stored is not { } chest || carried.IsEmpty) return carried;

        var there = chest.Contents[slot];
        var cap = _items[carried.Item].MaxStack;

        if (one)
        {
            if (!there.IsEmpty && !there.Matches(carried)) return carried;
            if (!there.IsEmpty && there.Count >= cap) return carried;

            chest.Contents[slot] = there.IsEmpty ? carried with { Count = 1 } : there with { Count = there.Count + 1 };
            return carried.MinusOne();
        }

        if (there.IsEmpty)
        {
            chest.Contents[slot] = carried;
            return ItemStack.Empty;
        }

        if (!there.Matches(carried))
        {
            chest.Contents[slot] = carried;
            return there;
        }

        chest.Contents[slot] = there.Merge(carried, cap, out var over);
        return over;
    }

    /// <summary>Shift-click: the bar and the backpack trade, and a grid empties into the pockets.</summary>
    private void SweepSlot(Zone zone)
    {
        switch (zone.Role)
        {
            // With a chest open the pockets empty into it, which is what a chest is for. Without
            // one, the bar and the backpack trade, which is what they did before there was anywhere
            // else for a stack to go.
            case SlotRole.Pocket when _hudScreen.Stored is { } into:
                var moving = _inventory.TakeAll(zone.Index);
                if (moving.IsEmpty) return;

                var back = _chests.Add(into, moving);
                if (!back.IsEmpty) _inventory.PutInto(zone.Index, back);
                if (back.Count != moving.Count) PlaySound(SoundMaterial.Wood, SoundEvent.Place, _viewPosition, 0.3f);
                return;

            case SlotRole.Pocket:
                if (_inventory.Sweep(zone.Index)) PlaySound(SoundMaterial.Wood, SoundEvent.Place, _viewPosition, 0.3f);
                return;

            case SlotRole.Craft when _hudScreen.Grid is { } grid:
                Spill(grid.TakeAll(zone.Index));
                return;

            case SlotRole.Equip:
                Spill(_equipment.TakeAll((EquipSlot)zone.Index));
                return;

            case SlotRole.Smelting or SlotRole.Fuel:
                Spill(TakeAllFrom(zone));
                return;

            // Shift moves a whole slot between the chest and the pockets, whichever way round it is
            // — which is what everybody reaches for and the only reason a chest is quicker than
            // dragging twenty seven stacks by hand.
            case SlotRole.Stored when _hudScreen.Stored is not null:
                Spill(TakeAllFrom(zone));
                return;
        }
    }

    /// <summary>
    /// Taking what the arrangement made: once on a click, or until it runs out on a shift-click.
    /// </summary>
    /// <remarks>
    /// What comes out goes onto the cursor when it can, because that is where a click puts things
    /// and because it lets a player drop it straight into a bar slot. A shift-click makes as many as
    /// the squares will pay for and sends them to the pockets, which is the one case where wanting
    /// it on the cursor makes no sense at all.
    /// </remarks>
    private void TakeFromResult(Zone zone, bool many)
    {
        // The saw. One rock off the bed for one of whatever was picked, and a shift-click keeps
        // cutting until the bed is empty — which is the whole reason to carry a stack to one.
        if (zone.Role == SlotRole.Cut)
        {
            if (_hudScreen.Cut < 0 || _hudScreen.Cut >= _hudScreen.Cuts.Count) return;

            var cut = _hudScreen.Cuts[_hudScreen.Cut];
            var sawn = 0;

            do
            {
                if (_hudScreen.Cutting.IsEmpty) break;

                _hudScreen.Cutting = _hudScreen.Cutting.MinusOne();
                Spill(cut.Result);
                sawn++;
            }
            while (many);

            if (sawn == 0) return;

            RefreshCuts();
            PlaySound(SoundMaterial.Stone, SoundEvent.Break, _viewPosition, 0.5f);
            return;
        }

        if (zone.Role == SlotRole.Smelted)
        {
            var taken = EmptyFurnaceSlot(SlotRole.Smelted);
            if (taken.IsEmpty) return;

            if (many || !_hudScreen.Carried.IsEmpty) Spill(taken);
            else _hudScreen.Carried = taken;

            PlaySound(SoundMaterial.Metal, SoundEvent.Place, _viewPosition, 0.5f);
            return;
        }

        if (_hudScreen.Grid is not { } grid || grid.Result.IsEmpty) return;

        var made = 0;
        var material = MaterialOf(grid.Result);

        // Bounded even when asked for as many as possible. Sixty four logs against a one-log recipe
        // is sixty four crafts, and any of them that will not fit ends up on the floor.
        for (var i = 0; i < (many ? 64 : 1); i++)
        {
            var result = grid.TakeResult();
            if (result.IsEmpty) break;

            made++;

            // A plain click with a free hand puts it on the cursor, which is where a click puts
            // things and is what lets it be dropped straight into a bar slot. Everything else goes
            // to the pockets — including a shift-click, where wanting sixty of them on the cursor
            // makes no sense at all.
            if (!many && _hudScreen.Carried.IsEmpty) _hudScreen.Carried = result;
            else
            {
                if (_hudScreen.Carried.Matches(result))
                    _hudScreen.Carried = _hudScreen.Carried.Merge(result, _items[result.Item].MaxStack, out result);

                Spill(result);
            }

            // ⛳ AND LAY IT OUT AGAIN, HERE, INSIDE THE LOOP. Taking the result spends the
            // arrangement, so without this the grid is left empty and a second door is another trip
            // to the book — which is exactly what was reported. Inside rather than after, because a
            // shift-click asks for as many as possible and "as many as possible" was one: the
            // second pass found an empty grid and stopped.
            //
            // It ends of its own accord the moment the pockets can no longer pay, and it does
            // nothing at all when the grid was filled by hand rather than from a recipe.
            if (_laidOut is { } again && !LayOut(again, quiet: true)) break;
        }

        if (made > 0) PlaySound(material, SoundEvent.Place, _viewPosition, 0.7f);
    }

    /// <summary>Lifts one of the furnace's three slots out, leaving it empty.</summary>
    private ItemStack EmptyFurnaceSlot(SlotRole role)
    {
        if (!_furnaces.TryGet(_station.X, _station.Y, _station.Z, out var furnace)) return ItemStack.Empty;

        switch (role)
        {
            case SlotRole.Smelting:
                var input = furnace.Input;
                furnace.Input = ItemStack.Empty;
                return input;

            case SlotRole.Fuel:
                var fuel = furnace.Fuel;
                furnace.Fuel = ItemStack.Empty;
                return fuel;

            default:
                var output = furnace.Output;
                furnace.Output = ItemStack.Empty;
                return output;
        }
    }

    /// <summary>
    /// Puts something into a furnace, or refuses it.
    /// </summary>
    /// <remarks>
    /// Fuel that will not burn and ore that will not smelt are refused rather than accepted and sat
    /// on, so a slot that takes something is a slot that is going to use it.
    /// </remarks>
    private ItemStack FeedFurnace(SlotRole role, ItemStack carried)
    {
        if (carried.IsEmpty) return carried;
        if (!_furnaces.TryGet(_station.X, _station.Y, _station.Z, out var furnace)) return carried;
        if (role == SlotRole.Smelting && _book.SmeltFor(carried.Item) is null) return carried;
        if (role == SlotRole.Fuel && _items[carried.Item].BurnSeconds <= 0f) return carried;

        var there = role == SlotRole.Smelting ? furnace.Input : furnace.Fuel;
        ItemStack put, back;

        if (there.IsEmpty || there.Matches(carried))
        {
            put = there.Merge(carried, _items[carried.Item].MaxStack, out back);
        }
        else
        {
            put = carried;
            back = there;
        }

        if (role == SlotRole.Smelting) furnace.Input = put; else furnace.Fuel = put;
        return back;
    }

    /// <summary>
    /// Walks the pointer to the nearest square in a direction.
    /// </summary>
    /// <remarks>
    /// Nearest by geometry rather than by a hand-written table of which square is above which. The
    /// panel is not a rectangle — a column of worn slots down one side, a two-by-two up in the
    /// corner, three rows of nine underneath — and an adjacency table for that shape is a table
    /// somebody has to redo every time a square moves. Scoring how far a candidate is along the
    /// wanted direction against how far it strays across it gets the obvious answer everywhere, and
    /// costs one pass over a list of forty.
    /// </remarks>
    private void MovePointer(int dx, int dy)
    {
        var from = _hudScreen.Pointer;
        var best = -1;
        var bestScore = float.MaxValue;

        for (var i = 0; i < _layout.Zones.Count; i++)
        {
            var zone = _layout.Zones[i];

            // Squares, the recipes in the book, and the buttons — everything on a container screen
            // a press can land on, so the arrows reach all of it and a gamepad will too.
            if (zone.Kind is not (ZoneKind.Slot or ZoneKind.Recipe or ZoneKind.Button)) continue;

            var toX = zone.CentreX - from.X;
            var toY = zone.CentreY - from.Y;

            var along = toX * dx + toY * dy;
            var across = MathF.Abs(toX * dy - toY * dx);

            // Two units of a square's pitch, so a step never crosses the whole panel to find
            // something marginally more aligned.
            if (along < 1f) continue;

            var score = along + across * 3f;
            if (score >= bestScore) continue;

            bestScore = score;
            best = i;
        }

        if (best < 0) return;

        _hudScreen.Pointer = new Vector2(_layout.Zones[best].CentreX, _layout.Zones[best].CentreY);
        _hudScreen.Hovered = _layout.Zones[best];

        // The window's own pointer goes with it, so picking a square with the arrows and then
        // reaching for the mouse does not teleport the cursor back to where it was left.
        var scale = HudRenderer.ScaleFor(_window.Size.Y);
        _input.MoveTo(_hudScreen.Pointer * scale);
    }

    /// <summary>How many recipes a row of the book holds. The same number the overlay lays out.</summary>
    private const int RecipeColumns = 10;

    /// <summary>How fast looking around was before anybody could change it. 100% means this.</summary>
    private const float ShippedSensitivity = 0.12f;

    private void MoveRow(int by)
    {
        if (_hudScreen.Rows.Count == 0) return;

        var at = _hudScreen.Selected;
        for (var step = 0; step < _hudScreen.Rows.Count; step++)
        {
            at = Math.Clamp(at + by, 0, _hudScreen.Rows.Count - 1);
            if (!_hudScreen.Rows[at].Heading) break;

            // A heading at the very end would trap the walk, so give up where it started.
            if (at == 0 || at == _hudScreen.Rows.Count - 1) return;
        }

        if (at != _hudScreen.Selected) DisarmDelete();

        _hudScreen.Selected = at;
        ShowSelectedRow();
    }

    /// <summary>The names across the top of each screen. Lower case, because the font has both.</summary>
    private static readonly string[] PlayerTabNames =
        [.. Enum.GetNames<PlayerTab>().Select(n => n.ToLowerInvariant())];

    private static readonly string[] GameTabNames =
        [.. Enum.GetNames<GameTab>().Select(n => n.ToLowerInvariant())];

    private void OpenPlayer(PlayerTab tab, bool atBench, (int X, int Y, int Z) at)
    {
        _hudScreen.Kind = HudScreenKind.Player;
        _hudScreen.TabNames = PlayerTabNames;
        _hudScreen.Tab = (int)tab;
        _hudScreen.Selected = 0;
        _hudScreen.Grid = _handGrid;
        _laidOut = null;
        _atBench = atBench;
        _station = at;
        _shown.Clear();
        StopHands();
        TakeThePointer();
        RefreshScreen();
    }

    /// <summary>A bench: three by three, and the player's own pockets under it.</summary>
    private void OpenBench(int x, int y, int z)
    {
        _hudScreen.Kind = HudScreenKind.Bench;
        _hudScreen.TabNames = [];
        _hudScreen.Tab = 0;
        _hudScreen.Grid = _benchGrid;
        _laidOut = null;
        _atBench = true;
        _station = (x, y, z);
        _shown.Clear();
        StopHands();
        TakeThePointer();
        RefreshScreen();
    }

    /// <summary>
    /// Lets go of the look and puts the pointer in the middle of the screen.
    /// </summary>
    /// <remarks>
    /// Every screen wants a pointer, not just the settings — that was the thing missing. The
    /// system's own cursor is hidden rather than shown, because the one that gets drawn is ours: it
    /// scales with the interface, lands on the same pixel grid and is the same arrow on every
    /// machine. The window's own cursor is still moved to the middle so the two agree about where
    /// the pointer is the moment the screen closes again.
    /// </remarks>
    private void TakeThePointer()
    {
        var middle = new Vector2(_window.Size.X * 0.5f, _window.Size.Y * 0.5f);
        _input.MoveTo(middle);
        _hudScreen.Pointer = middle / HudRenderer.ScaleFor(_window.Size.Y);
        _hudScreen.Hovered = null;
        ApplyCursorMode();
    }

    /// <summary>
    /// Opens the settings, and lets go of the mouse while it is up.
    /// </summary>
    /// <remarks>
    /// The pointer is unambiguously wanted on a settings screen and unambiguously not wanted in the
    /// world, so the two travel together. It is taken back on the way out, which means a player who
    /// opens the options and closes them again is looking exactly where they were.
    /// </remarks>
    private void OpenGame(GameTab tab)
    {
        _hudScreen.Kind = HudScreenKind.Game;
        _hudScreen.TabNames = GameTabNames;
        _hudScreen.Tab = (int)tab;
        _hudScreen.Selected = _tabRow[(int)tab];
        _hudScreen.Grid = null;
        _laidOut = null;
        _hudScreen.Scroll = 0;
        _hudScreen.Recipes.Clear();
        _hudScreen.Payable.Clear();

        // Once, here, rather than in the row builder that runs every frame the screen is up.
        if (tab == GameTab.Saves) ReadSavesFolder();
        if (tab == GameTab.Packs) ReadPacksFolder();

        StopHands();
        TakeThePointer();
        RefreshScreen();
        ShowSelectedRow();
    }

    /// <summary>Puts the hands down. A screen opening must not leave a swing half-taken.</summary>
    private void StopHands()
    {
        _holdingBreak = false;
        _holdingPlace = false;
        _mining.Cancel();
    }

    private void OpenFurnace(int x, int y, int z)
    {
        _hudScreen.Kind = HudScreenKind.Furnace;
        _hudScreen.TabNames = [];
        _hudScreen.Tab = 0;
        _hudScreen.Grid = null;
        _laidOut = null;
        _station = (x, y, z);
        _holdingBreak = false;
        _holdingPlace = false;
        _mining.Cancel();

        // ⛔ Emptied here, like every other opener. The book is built once per opening and only
        // rebuilt when this is empty, so a list carried over from the last station is the list that
        // gets drawn — and the three fires do not share one: a smoker's page and a furnace's page
        // differ by every recipe that is not food. Left out, walking from a bench to a furnace would
        // put the bench's whole book beside the flame.
        _shown.Clear();
        _hudScreen.Selected = 0;
        _hudScreen.BookPage = 0;

        // ⛔ A fire opens with its book OUT, always. The book IS the fire's menu — the whole
        // fault it was built for was that nowhere in the game said a fire cooks meat, and
        // opening folded-away is the same silence one undiscoverable toggle later. The button
        // beside the squares folds it for whoever wants the plain three.
        _hudScreen.BookOut = true;

        _furnaces.Open(x, y, z);
        TakeThePointer();
        RefreshScreen();
    }

    /// <summary>A stonecutter: a rock on the bed, and everything that rock can be cut into.</summary>
    private void OpenStonecutter(int x, int y, int z)
    {
        _hudScreen.Kind = HudScreenKind.Stonecutter;
        _hudScreen.TabNames = [];
        _hudScreen.Tab = 0;
        _hudScreen.Grid = null;
        _laidOut = null;
        _hudScreen.Cutting = ItemStack.Empty;
        _hudScreen.Cuts.Clear();
        _hudScreen.Cut = -1;
        _station = (x, y, z);
        StopHands();
        TakeThePointer();
        RefreshScreen();
    }

    /// <summary>
    /// Works out what the rock on the bed could become, keeping the choice where it can be kept.
    /// </summary>
    /// <remarks>
    /// The picked cut survives a change of rock only if the same cut is still on offer. Swapping
    /// stone for deepstone and finding the selection silently moved to something else is how a
    /// player ends up with a stack of the wrong slab.
    /// </remarks>
    private void RefreshCuts()
    {
        var was = _hudScreen.Cut >= 0 && _hudScreen.Cut < _hudScreen.Cuts.Count
            ? _hudScreen.Cuts[_hudScreen.Cut]
            : null;

        _hudScreen.Cuts.Clear();
        foreach (var offer in _book.Offers(CraftStation.Stonecutter, _hudScreen.Cutting.Item))
        {
            if (_hudScreen.Cuts.Count >= ScreenLayout.CutOffers) break;
            _hudScreen.Cuts.Add(offer);
        }

        _hudScreen.Cut = was is null ? -1 : _hudScreen.Cuts.IndexOf(was);
        if (_hudScreen.Cut < 0 && _hudScreen.Cuts.Count > 0) _hudScreen.Cut = 0;
    }

    /// <summary>A chest: twenty seven slots, and the player's own pockets under them.</summary>
    private void OpenChest(int x, int y, int z)
    {
        _hudScreen.Kind = HudScreenKind.Chest;
        _hudScreen.TabNames = [];
        _hudScreen.Tab = 0;
        _hudScreen.Grid = null;
        _laidOut = null;
        _hudScreen.Stored = _chests.Open(x, y, z);
        _station = (x, y, z);
        StopHands();
        TakeThePointer();
        RefreshScreen();

        _audio?.Play(
            Pick(LidOf(x, y, z, opening: true)),
            new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), 0.6f, Wobble());
    }

    /// <summary>A chest's lid or a barrel's, told apart by the block actually standing there.</summary>
    private string[] LidOf(int x, int y, int z, bool opening)
    {
        var barrel = _registry[_streamer.World.GetBlock(x, y, z)]
            .Name.Contains("barrel", StringComparison.Ordinal);
        return opening
            ? barrel ? ActionSounds.BarrelOpen : ActionSounds.ChestOpen
            : barrel ? ActionSounds.BarrelClose : ActionSounds.ChestClose;
    }

    /// <summary>True when the game screen is open on one particular tab.</summary>
    private bool OnTab(GameTab tab) =>
        _hudScreen.Kind == HudScreenKind.Game && _hudScreen.Tab == (int)tab;

    /// <summary>
    /// Closes the screen, and writes the settings out if anything in it changed.
    /// </summary>
    /// <remarks>
    /// On close rather than on every keystroke. A slider walked from 0 to 100 is a hundred writes
    /// of the same file, and the moment a player is actually finished with a setting is the moment
    /// they leave the screen.
    /// </remarks>
    private void CloseScreen()
    {
        // ⛳ The menu is not a screen over a game; it is what there is instead of one. Escape on it
        // does nothing, and escape on the options opened from it comes back to it rather than
        // dropping somebody into a world they have not said they want to play — which would be a
        // player standing in the world with no idea how they got there.
        if (_hudScreen.Kind == HudScreenKind.Start) return;

        if (_atStartScreen && _hudScreen.Kind == HudScreenKind.Game)
        {
            _tabRow[_hudScreen.Tab] = _hudScreen.Selected;
            _rebinding = null;
            if (_settingsDirty) { _settings.Save(); _settingsDirty = false; }

            // The same way in as every other, so the folder is read here too rather than only on
            // the path that happened to be thought about.
            ShowStartMenu();
            return;
        }

        if (_hudScreen.Kind == HudScreenKind.Game) _tabRow[_hudScreen.Tab] = _hudScreen.Selected;

        // The lid coming down, before the screen forgets which container it was open on.
        if (_hudScreen.Kind == HudScreenKind.Chest && _station is var (cx, cy, cz))
        {
            _audio?.Play(
                Pick(LidOf(cx, cy, cz, opening: false)),
                new Vector3(cx + 0.5f, cy + 0.5f, cz + 0.5f), 0.6f, Wobble());
        }

        // Nothing is left nowhere. A stack on the cursor is in neither the pockets nor the grid nor
        // the world, so a screen that shut while one was held would simply delete it — and what is
        // laid out in a bench's three by three belongs to the player, not to the bench. Both go back
        // into the pockets, and whatever will not fit lands on the floor rather than being swallowed,
        // which is the one rule this inventory has never broken.
        Spill(_hudScreen.Carried);
        _hudScreen.Carried = ItemStack.Empty;

        if (_hudScreen.Kind == HudScreenKind.Bench)
            foreach (var left in _benchGrid.Empty(_inventory)) Spill(left);

        // A stonecutter keeps nothing either. What is on its bed is the player's rock, not the
        // station's, so it goes back with everything else rather than being left on a table.
        Spill(_hudScreen.Cutting);
        _hudScreen.Cutting = ItemStack.Empty;
        _hudScreen.Cuts.Clear();
        _hudScreen.Cut = -1;

        _rebinding = null;
        _hudScreen.Kind = HudScreenKind.None;
        _hudScreen.Grid = null;
        _laidOut = null;

        // The chest keeps what is in it — that is the whole point of one — but the screen lets go
        // of it, so a shift-click on a pocket goes back to trading with the bar.
        _hudScreen.Stored = null;
        _hudScreen.Hovered = null;
        _shown.Clear();
        _hudScreen.Recipes.Clear();
        _hudScreen.Payable.Clear();
        _hudScreen.Rows.Clear();
        _layout.Clear();

        // The look goes back where it was, so a player who opened a screen and shut it again is
        // looking exactly where they were.
        ApplyCursorMode();

        if (!_settingsDirty) return;
        _settingsDirty = false;

        if (!_settings.Save()) Console.Error.WriteLine("driftwood: could not write the settings file");
    }

    /// <summary>Puts a stack back in the pockets, and on the floor in front of the player if it will not fit.</summary>
    private void Spill(ItemStack stack)
    {
        if (stack.IsEmpty) return;

        var left = _inventory.Add(stack);
        if (!left.IsEmpty) _drops.Drop(left, _player.Position + new Vector3(0f, 1f, 0f), 0.4f);
    }

    /// <summary>
    /// Rebuilds whatever the open screen shows.
    /// </summary>
    /// <remarks>
    /// The recipe list is built once per opening and only its affordability is recomputed, because
    /// a list that changes length as a player picks things up would move the selection out from
    /// under them on the frame they pressed enter. The settings rows are rebuilt outright, because
    /// they are cheap and because a value has to be able to change under its own label.
    /// </remarks>
    /// <summary>What sort of fire the open station is, asked of the block standing there.</summary>
    /// <remarks>
    /// ⛔ <b>Off the world, never stored beside the screen</b> — the same rule the cooking tick
    /// follows and for the same reason. Which fire this is is a property of the block somebody built,
    /// so a copy kept anywhere else is a copy that can disagree with it; a campfire smothered while
    /// its screen is open has to stop being a campfire immediately.
    /// </remarks>
    private FurnaceKind OpenFireKind() =>
        _smelterKind[_streamer.World.GetBlock(_station.X, _station.Y, _station.Z).Value];

    /// <summary>
    /// The page of everything the fire in front of the player will work.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>Lit means "you are carrying what this needs", exactly as it does at a bench.</b>
    /// The rest are listed dim rather than hidden, because a book that shows only what you can
    /// already afford answers "what now" and never answers "what for" — and "what for" is the entire
    /// question a player has when they first stand in front of a furnace.</para>
    /// <para>⚠ <b>Built once per opening</b>, like the bench's, so the list cannot change length
    /// under a player between the frame they aim at a row and the frame they click it.</para>
    /// </remarks>
    private void RefreshSmeltBook()
    {
        var kind = OpenFireKind();

        if (_shown.Count == 0)
            foreach (var smelt in _book.SmeltsAt(kind))
                _shown.Add(smelt.AsShown());

        _hudScreen.Recipes.Clear();
        _hudScreen.Recipes.AddRange(_shown);

        _hudScreen.Payable.Clear();
        foreach (var recipe in _shown)
            _hudScreen.Payable.Add(_book.CanPay(_inventory, recipe));

        _hudScreen.Selected = Math.Clamp(_hudScreen.Selected, 0, Math.Max(0, _shown.Count - 1));

        var pages = Math.Max(1, (_shown.Count + ScreenLayout.BookPage - 1) / ScreenLayout.BookPage);
        _hudScreen.BookPage = Math.Clamp(_hudScreen.BookPage, 0, pages - 1);
    }

    private void RefreshScreen()
    {
        if (_hudScreen.Kind == HudScreenKind.None) return;

        _hudScreen.Footer = FooterHint();

        // The squares read themselves — what is in a slot is asked of the inventory as it is drawn,
        // so there is no list here that could fall out of step with one. The book beside them does
        // need a list, and what is on it depends on how wide the grid is: a bench lends three.
        if (_hudScreen.IsContainer)
        {
            // ⛳⛳ A FIRE'S BOOK IS WHAT IT SMELTS. Reported by the user — "I'm not seeing any recipes
            // for food when i look in the furnace" — and there was no list at all: a furnace opened
            // three squares and nothing else, so the only way to learn that a fire cooks meat was to
            // already know. Every smelt worked; not one was ever named where a player could look.
            //
            // ⛔ It is exactly the fault the bench branch below was written to fix, and its own note
            // says it: a thing that is absent and a thing that does not exist look identical.
            if (_hudScreen.Kind == HudScreenKind.Furnace)
            {
                RefreshSmeltBook();
                return;
            }

            // A station with no grid has no book beside it: a chest has nothing to arrange, and a
            // stonecutter's list is its own and is built from what is on its bed.
            if (_hudScreen.Grid is null)
            {
                _hudScreen.Recipes.Clear();
                _hudScreen.Payable.Clear();
                return;
            }

            // ⛔⛔ THE BOOK USED TO LIST ONLY WHAT THIS GRID COULD WORK, and that is how a player
            // concludes something is impossible. Reported directly: they had the iron, the smooth
            // stone and a furnace, and the blast furnace "never actually became available to make".
            // Every one of the 195 recipes crafts correctly when handed its ingredients — measured,
            // with --recipes — so nothing was wrong with the recipe. It was simply not in the list,
            // because a two-by-two cannot work a three-by-three, and a thing that is absent and a
            // thing that does not exist look identical.
            //
            // ⛳ So the book shows what this grid works, AND anything else the pockets could already
            // pay for — dim, with its own line saying where to go. That is the whole missing sentence:
            // "you can afford this; walk to a bench." A recipe you cannot yet afford and cannot make
            // here stays out, because listing all 195 in a two-by-two is a different way of saying
            // nothing.
            if (_shown.Count == 0)
            {
                foreach (var recipe in _book.Recipes)
                    if (recipe.WorkedAt(_hudScreen.Grid.Station, _hudScreen.Grid.Width)) _shown.Add(recipe);

                foreach (var recipe in _book.Recipes)
                {
                    if (recipe.WorkedAt(_hudScreen.Grid.Station, _hudScreen.Grid.Width)) continue;
                    if (_book.CanPay(_inventory, recipe)) _shown.Add(recipe);
                }
            }

            // Built once per opening and only its affordability recomputed. A list that changed
            // length as things were picked up would move the selection out from under a player on
            // the frame they clicked it.
            _hudScreen.Recipes.Clear();
            _hudScreen.Recipes.AddRange(_shown);

            // ⚠ Lit means "you can make this, here, now" — so a recipe you can afford and cannot
            // work at this grid is dim, exactly like one you cannot afford. Its tooltip is what
            // tells the two apart, and that is the line that says where to go.
            _hudScreen.Payable.Clear();
            foreach (var recipe in _shown)
                _hudScreen.Payable.Add(
                    recipe.WorkedAt(_hudScreen.Grid.Station, _hudScreen.Grid.Width)
                    && _book.CanPay(_inventory, recipe));

            _hudScreen.Selected = Math.Clamp(_hudScreen.Selected, 0, Math.Max(0, _shown.Count - 1));

            var pages = Math.Max(1, (_shown.Count + ScreenLayout.BookPage - 1) / ScreenLayout.BookPage);
            _hudScreen.BookPage = Math.Clamp(_hudScreen.BookPage, 0, pages - 1);
            return;
        }

        BuildRows();
        _hudScreen.Selected = Math.Clamp(_hudScreen.Selected, 0, Math.Max(0, _hudScreen.Rows.Count - 1));
        if (_hudScreen.Rows.Count > 0 && _hudScreen.Rows[_hudScreen.Selected].Heading) MoveRow(1);
    }

    private string FooterHint()
    {
        if (_rebinding is { } waiting)
            return $"press a key for {GameActions.Label(waiting)}, or escape to leave it alone";

        var close = _settings.Keys.Primary(
            _hudScreen.Kind == HudScreenKind.Game ? GameAction.OpenOptions : GameAction.OpenInventory);

        if (_hudScreen.IsContainer)
            return _hudScreen.BookOut
                ? "click a recipe to lay it out, shift-click to make it, "
                  + $"b shuts the book, {close} closes"
                : "click takes and puts, right click halves, shift moves it across, "
                  + $"b opens the recipe book, {close} closes";

        var wheel = _hudScreen.Rows.Count > ScreenLayout.MenuLines(LayoutHeight) ? ", wheel scrolls" : "";

        return _hudScreen.Kind switch
        {
            // No "closes" — there is nothing behind it to go back to yet.
            HudScreenKind.Start => $"up and down pick, enter chooses{wheel}",
            HudScreenKind.Game when _atStartScreen =>
                $"up and down pick, left and right change it, tab changes tab{wheel}, "
                + $"{close} goes back to the menu",
            HudScreenKind.Player =>
                $"arrows pick, enter makes one, shift and enter makes as many as it can, {close} closes",
            _ when OnTab(GameTab.Controls) =>
                $"up and down pick, enter listens for a key, left clears it{wheel}, {close} closes",
            _ => $"up and down pick, left and right change it, tab changes tab{wheel}, {close} closes",
        };
    }

    /// <summary>
    /// The four things a start screen offers, or the list of worlds it folds out into.
    /// </summary>
    /// <remarks>
    /// <b>The first row says what it will actually do.</b> A world that was loaded is carried on
    /// with, and one that was just made is started — the same button either way, because it is the
    /// same act, but calling a resumed world "new game" is how somebody loses one.
    /// </remarks>
    private void BuildStartRows()
    {
        if (_startListing)
        {
            _hudScreen.Rows.Add(new MenuRow(
                (_saved.Count == 1 ? "1 world" : $"{_saved.Count} worlds")
                + (_unreadable.Count > 0 ? $", {_unreadable.Count} unreadable" : ""),
                Heading: true));

            if (_saved.Count == 0 && _unreadable.Count == 0)
                _hudScreen.Rows.Add(new MenuRow("none yet", "", Note: SavesFolderNote));

            foreach (var world in _saved)
            {
                var when = world.Saved.ToLocalTime();
                _hudScreen.Rows.Add(new MenuRow(
                    world.Name == _worldName ? $"{world.Name}  (open)" : world.Name,
                    $"{world.PlayedFor} · {when:d MMM HH:mm}",
                    Note: world.Name == _worldName
                        ? "this is the one that is open — enter plays it"
                        : $"{world.Edits} changes, seed {world.Seed}. Enter opens it"));
            }

            // ⛔ On the screen, not only in a log. A file that will not read is somebody's world and
            // the folder is where they will go looking for it, so the row carries the file's own
            // name and what stopped it rather than quietly leaving the list one shorter.
            foreach (var bad in _unreadable)
                _hudScreen.Rows.Add(new MenuRow(
                    bad.File, "cannot read", Note: $"{bad.Why}. {SavesFolderNote}"));

            _hudScreen.Rows.Add(new MenuRow("back", "", Note: "enter returns to the menu"));
            return;
        }

        _hudScreen.Rows.Add(new MenuRow("Driftwood", Heading: true));

        _hudScreen.Rows.Add(_loadedWorld
            ? new MenuRow("carry on", _worldName, Note: $"played {Spoken(_playedBefore)}")
            : new MenuRow("start a world", _worldName.Length > 0 ? _worldName : "not being kept",
                Note: $"a new one, seed {_seed}"));

        _hudScreen.Rows.Add(new MenuRow(
            "make another world", NextWorldName(),
            Note: _seedBox.Empty
                ? "a fresh seed, kept under its own name — this one stays where it is"
                : $"seed '{_seedBox.Text}', kept under its own name — this one stays where it is"));

        // Under the row it belongs to, because a seed is a thing about the world being made and
        // means nothing to any of the others.
        _hudScreen.Rows.Add(new MenuRow(
            "seed", Edits: _seedBox,
            Note: "type a word to make the same world again — leave it empty for one nobody has seen"));
        // ⛔ "none saved yet" is the sentence the user read when their world was on disk all along,
        // so it now only appears when the folder really is empty — and when it is not empty but
        // nothing in it opened, the row says that instead of the same four words.
        _hudScreen.Rows.Add(new MenuRow(
            "open a world",
            _saved.Count > 0 ? $"{_saved.Count} saved"
            : _unreadable.Count > 0 ? $"{_unreadable.Count} cannot be read"
            : "none saved yet",
            Note: _saved.Count == 0 && _unreadable.Count == 0 ? SavesFolderNote : ""));
        _hudScreen.Rows.Add(new MenuRow("options", "", Note: "keys, picture, sound"));
        _hudScreen.Rows.Add(new MenuRow("quit", ""));
    }

    /// <summary>What the open settings tab is showing, rebuilt from what is actually set.</summary>
    private void BuildRows()
    {
        _hudScreen.Rows.Clear();

        if (_hudScreen.Kind == HudScreenKind.Start)
        {
            BuildStartRows();
            return;
        }

        // ⛔ A visible way out, because escape is not one. It works, and the user's verdict on that
        // was "if hitting the esc key is the only way to get out of the settings area that's not
        // intuitive enough for most users" — which is right: a settings screen reached from a menu
        // is somewhere people look for a Back, not somewhere they think to press escape. It is the
        // first row on every tab so it is in the same place whichever one they wandered into.
        if (_atStartScreen) _hudScreen.Rows.Add(new MenuRow(BackToMenu, "", Note: "or press escape"));

        switch ((GameTab)_hudScreen.Tab)
        {
            case GameTab.Controls:
                var group = "";
                foreach (var action in GameActions.All)
                {
                    var heading = GameActions.GroupOf(action);
                    if (heading != group)
                    {
                        group = heading;
                        _hudScreen.Rows.Add(new MenuRow(heading, Heading: true));
                    }

                    var listening = _rebinding == action;
                    _hudScreen.Rows.Add(new MenuRow(
                        GameActions.Label(action),
                        listening ? "press a key" : _settings.Keys.Describe(action)));
                }
                break;

            case GameTab.Video:
                // What is loaded is only worth saying when it is not what the row already says.
                // On a normal launch the two are the same number, so "8 chunks / 8 loaded now" was
                // a reading of itself — and the whole note only means anything after a change.
                var pending = _viewRadius == _settings.ViewDistance
                    ? "how far the world is kept loaded around you"
                    : $"{_viewRadius} loaded now; the new distance applies next time the game opens";

                _hudScreen.Rows.Add(new MenuRow("picture", Heading: true));
                _hudScreen.Rows.Add(new MenuRow(
                    "view distance", $"{_settings.ViewDistance} chunks", Note: pending));
                _hudScreen.Rows.Add(new MenuRow("field of view", $"{_settings.FieldOfView}"));
                _hudScreen.Rows.Add(new MenuRow("fullscreen", OnOff(_settings.Fullscreen)));
                _hudScreen.Rows.Add(new MenuRow(
                    "wait for the display", OnOff(_settings.VSync),
                    Note: "smoother, and the frame counter stops meaning anything"));

                // ⛳ Uncapped, this draws about 5,000 frames a second — twenty-eight thrown away for
                // every one a 175 Hz display can show, in fans and heat and battery.
                _hudScreen.Rows.Add(new MenuRow(
                    "frame limit",
                    _settings.FrameCap <= 0 ? "as fast as it can" : $"{_settings.FrameCap} a second",
                    Note: _settings.VSync
                        ? "ignored while the display is being waited for, which is already a limit"
                        : "match your display; drawing more than it can show is heat and nothing else"));

                _hudScreen.Rows.Add(new MenuRow("notices", Heading: true));
                _hudScreen.Rows.Add(new MenuRow(
                    "new recipe notices", OnOff(_settings.RecipeNotices),
                    Note: "said once ever, not once a session"));
                _hudScreen.Rows.Add(new MenuRow(
                    "forget what has been said", $"{_unlocks.Announced} remembered",
                    Note: "enter forgets them, and the whole tree announces itself again"));

                _hudScreen.Rows.Add(new MenuRow("looking at things", Heading: true));
                _hudScreen.Rows.Add(new MenuRow("wireframe", OnOff(_wireframe)));
                _hudScreen.Rows.Add(new MenuRow(
                    "frustum culling", OnOff(_frustumCulling),
                    Note: "turning it off must change the chunk count and nothing on screen"));
                break;

            case GameTab.Audio:
                _hudScreen.Rows.Add(new MenuRow("sound", Heading: true));
                _hudScreen.Rows.Add(new MenuRow("volume", $"{_settings.Volume}"));
                _hudScreen.Rows.Add(new MenuRow("mute", OnOff(_settings.Mute)));
                break;

            case GameTab.Saves:
                _hudScreen.Rows.Add(new MenuRow("this world", Heading: true));
                _hudScreen.Rows.Add(new MenuRow(
                    "name", _worldName.Length > 0 ? _worldName : "not being kept"));
                _hudScreen.Rows.Add(new MenuRow("played", Spoken(_playedBefore + _elapsed)));
                _hudScreen.Rows.Add(new MenuRow(
                    "save now",
                    _saveFault is null ? $"{_streamer.World.Edits.Count} changes" : "the last one failed",
                    Note: _saveFault ?? "enter writes it; closing the window writes it anyway"));
                _hudScreen.Rows.Add(new MenuRow(
                    "last saved",
                    _streamer.World.Changed || _unlocks.Dirty
                        ? $"{(int)_sinceSave}s ago, with changes since"
                        : $"{(int)_sinceSave}s ago, up to date",
                    Note: $"written by itself every {(int)AutosaveSeconds}s when anything has changed, "
                        + $"and on dying — {_autosaves} so far this session. "
                        + $"The {WorldSave.Backups} states before this one are kept beside it"));

                _hudScreen.Rows.Add(new MenuRow(
                    (_saved.Count == 1 ? "1 world on this machine" : $"{_saved.Count} worlds on this machine")
                    + (_unreadable.Count > 0 ? $", {_unreadable.Count} unreadable" : "")
                    + (_deleted is not null ? $" — {_deleted}" : ""),
                    Heading: true));

                if (_saved.Count == 0 && _unreadable.Count == 0)
                {
                    _hudScreen.Rows.Add(new MenuRow("none yet", "", Note: SavesFolderNote));
                    break;
                }

                // Same as the menu's list: a file that will not open is said out loud here rather
                // than leaving the count one short with nothing to explain it.
                foreach (var bad in _unreadable)
                    _hudScreen.Rows.Add(new MenuRow(
                        bad.File, "cannot read", Note: $"{bad.Why}. {SavesFolderNote}"));

                foreach (var world in _saved)
                {
                    // Local time, because a save is a thing that happened to the person reading it.
                    // The header keeps UTC so the ordering survives moving a save between machines.
                    var when = world.Saved.ToLocalTime();
                    var open = world.Name == _worldName;

                    // ⚠ The armed row says so where its date was, because that is the line a player
                    // is already reading — a warning in the note strip under the list is a warning
                    // beside the thing rather than on it.
                    var armed = _deleteArmed == world.Name;

                    _hudScreen.Rows.Add(new MenuRow(
                        open ? $"{world.Name}  (open)" : world.Name,
                        armed ? "delete it? enter again" : $"{world.PlayedFor} · {when:d MMM HH:mm}",
                        Note: armed
                            ? $"this throws away {world.Name} and the {WorldSave.Backups} states kept "
                              + "beside it, for good. Anything else puts it back"
                            : open
                                ? $"the world you are in, so it cannot be thrown away from here. "
                                  + SavesFolderNote
                                : $"{world.Edits} changes, seed {world.Seed}. "
                                  + "Enter asks to throw it away; open it from the start screen"));
                }
                break;

            case GameTab.Packs:
                BuildPackRows();
                break;

            default:
                var p = _walking ? _player.Position : _camera.Position;

                _hudScreen.Rows.Add(new MenuRow("this world", Heading: true));
                _hudScreen.Rows.Add(new MenuRow(
                    "name", _worldName.Length > 0 ? _worldName : "not being kept"));
                _hudScreen.Rows.Add(new MenuRow("seed", _seed.ToString()));
                _hudScreen.Rows.Add(new MenuRow("where you are", $"{p.X:F0} {p.Y:F0} {p.Z:F0}"));
                _hudScreen.Rows.Add(new MenuRow(
                    "loaded", $"{_meshes.Count} chunks, {_drawnChunks} drawn"));

                _hudScreen.Rows.Add(new MenuRow("time", Heading: true));
                _hudScreen.Rows.Add(new MenuRow(
                    "hour", ClockFace(_clock.TimeOfDay), Note: "left and right wind it"));
                _hudScreen.Rows.Add(new MenuRow("clock", _clock.Running ? "running" : "held"));

                _hudScreen.Rows.Add(new MenuRow("body", Heading: true));
                _hudScreen.Rows.Add(new MenuRow("moving", _walking ? "walking" : "flying"));
                _hudScreen.Rows.Add(new MenuRow("camera", _view switch
                {
                    ViewMode.First => "first person",
                    ViewMode.ThirdBehind => "over the shoulder",
                    _ => "facing you",
                }));
                _hudScreen.Rows.Add(new MenuRow(
                    "mouse speed", $"{_settings.MouseSensitivity}"));
                break;
        }
    }

    private static string OnOff(bool value) => value ? "on" : "off";

    /// <summary>
    /// Nudges whatever is selected. Left is -1 and right is +1.
    /// </summary>
    /// <param name="activated">
    /// True when this came from enter or a left click rather than from a direction.
    /// </param>
    /// <remarks>
    /// <b>A row that throws something away must not answer to a direction.</b> Left and right are
    /// how a player walks along every other setting to see what it does, and "forget what has been
    /// said" wiped the whole persisted record on either of them, with no confirmation and nothing
    /// to undo it with. It answers to enter alone now, which is what its own note says.
    /// </remarks>
    private void AdjustRow(int by, bool activated = false)
    {
        if (_hudScreen.Selected < 0 || _hudScreen.Selected >= _hudScreen.Rows.Count) return;

        var label = _hudScreen.Rows[_hudScreen.Selected].Label;

        switch ((GameTab)_hudScreen.Tab)
        {
            case GameTab.Controls:
                // Left is the only thing a direction can mean on a key: take it off.
                if (by >= 0 || ActionAtRow() is not { } action) break;
                _settings.Keys.Bind(action, "");
                AfterRebind();
                break;

            case GameTab.Video:
                switch (label)
                {
                    case "view distance": _settings.ViewDistance = Nudge(_settings.ViewDistance, by, 2, 32); break;
                    case "field of view": _settings.FieldOfView = Nudge(_settings.FieldOfView, by * 5, 50, 110); break;
                    case "fullscreen": _settings.Fullscreen = !_settings.Fullscreen; break;
                    case "wait for the display": _settings.VSync = !_settings.VSync; break;

                    // ⚠ Steps of five past sixty, and off the bottom rather than at it: a limiter
                    // nudged one frame at a time from 175 to uncapped is twenty-three presses.
                    case "frame limit": _settings.FrameCap = NudgeCap(_settings.FrameCap, by); break;

                    case "new recipe notices": _settings.RecipeNotices = !_settings.RecipeNotices; break;
                    // Enter only. A direction is how a player browses, not how they throw
                    // something away that cannot be got back.
                    case "forget what has been said":
                        if (!activated) return;
                        _unlocks.Forget();
                        return;
                    case "wireframe":
                        _wireframe = !_wireframe;
                        _gl.PolygonMode(TriangleFace.FrontAndBack, _wireframe ? PolygonMode.Line : PolygonMode.Fill);
                        return;
                    case "frustum culling": _frustumCulling = !_frustumCulling; return;
                }
                break;

            case GameTab.Audio:
                switch (label)
                {
                    case "volume": _settings.Volume = Nudge(_settings.Volume, by * 5, 0, 100); break;
                    case "mute": _settings.Mute = !_settings.Mute; break;
                }
                break;

            default:
                switch (label)
                {
                    // The world tab is mostly read-outs; these three are the dials that make it
                    // possible to look at something at a particular hour without waiting for it.
                    // Enter only, and for the same reason the destructive row above is: left and
                    // right are how every other row on this screen is browsed, and a save fired by
                    // walking past it would write over the world on the way to the next setting.
                    case "save now":
                        if (activated) SaveWorld("by hand");
                        return;
                    case "hour":
                        _clock.SetTime(_clock.TimeOfDay + by / 24f);
                        _skyState = _clock.Now;
                        return;
                    case "clock": _clock.Running = !_clock.Running; return;
                    case "moving": ToggleFly(); return;
                    case "camera": CycleView(); return;
                    case "mouse speed":
                        _settings.MouseSensitivity = Nudge(_settings.MouseSensitivity, by * 10, 10, 400);
                        break;
                    default: return;
                }
                break;
        }

        _settingsDirty = true;
        ApplySettings();
    }

    /// <summary>The pack actually being worn this run, resolved from the setting or the switch.</summary>
    private string? _packPath;

    /// <summary>
    /// Works out which pack this run wears, and remembers a good one given on the command line.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b><c>--pack</c> wins, and installs itself.</b> Somebody who has typed a path once
    /// should not have to type it again, so a pack pointed at from the command line is copied onto
    /// the shelf and remembered — the same courtesy <c>--creature-geometry</c> already does.</para>
    /// <para>⛔ <b>A setting naming a pack that has gone falls back to our own art and SAYS SO.</b>
    /// Silently wearing the default is how somebody spends an evening wondering why their pack
    /// stopped working; one line on the console is the whole difference.</para>
    /// </remarks>
    private void ResolvePack()
    {
        if (!string.IsNullOrWhiteSpace(_options.PackPath))
        {
            _packPath = _options.PackPath;

            if (PackLibrary.Install(_options.PackPath, out _) is { } added
                && !string.Equals(_settings.TexturePack, added.Name, StringComparison.Ordinal))
            {
                _settings.TexturePack = added.Name;
                _settings.Save();
            }

            Console.WriteLine($"pack        {_packPath} (given on the command line)");
            return;
        }

        // ⛔ The instruments wear the game's own art unless a pack is asked for by name. The gate
        // went red on a correct build the day a pack was picked in the options screen: --ui-check
        // reads colours off the real screen, and a pack repaints exactly the pixels it reads. The
        // player's choice stays in their settings; a check is not the player.
        if (_options.UiCheck || _options.ShotPath is not null)
        {
            _packPath = null;
            Console.WriteLine("pack        the game's own art (a self-check ignores the settings; --pack still applies)");
            return;
        }

        var wanted = _settings.TexturePack;
        if (string.IsNullOrWhiteSpace(wanted))
        {
            _packPath = null;
            return;
        }

        _packPath = PackLibrary.PathOf(wanted);

        Console.WriteLine(_packPath is null
            ? $"pack        '{wanted}' is no longer on the shelf — wearing Driftwood's own art"
            : $"pack        {wanted}");
    }

    /// <summary>The box a path is pasted into to put a pack on the shelf.</summary>
    private readonly TextField _packBox = new(240);

    /// <summary>The Explorer window that finds a pack, so nobody has to know where one lives.</summary>
    private readonly NativeFilePicker _packPicker = new();

    /// <summary>The two rows that open it, named once so the row and the switch cannot drift apart.</summary>
    private const string BrowseFileRow = "browse for a pack";
    private const string BrowseFolderRow = "browse for a folder";

    /// <summary>What the shelf held when it was last read, and what the last import said.</summary>
    private IReadOnlyList<PackLibrary.Entry> _packs = [];
    private string _packNote = "";

    /// <summary>What the pack being worn carries that we have nothing to put it on.</summary>
    private PackCoverage.Summary? _packTally;

    private void ReadPacksFolder()
    {
        _packs = PackLibrary.List();

        // ⛳ Only for the pack actually WORN, and only when the tab is opened. It reads the
        // archive's index rather than any pixels, so a six-hundred-megabyte pack costs about what a
        // small one does — but doing it for every pack on the shelf on every refresh would not.
        _packTally = null;
        if (_packPath is not { } worn) return;

        try
        {
            _packTally = PackCoverage.Tally(worn);
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _packTally = null;
        }
    }

    /// <summary>
    /// The shelf: our own art, then everything installed, then the box that adds one.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>Every row says what the thing IS</b>, not just that it exists — "Bedrock, 512px"
    /// rather than a filename. That is the only line on the screen that tells a player whether the
    /// file they downloaded is the file they meant, and it costs one open per pack.</para>
    /// <para>⛔ <b>A pack that will not open is a ROW WITH A REASON, never a gap.</b> Same fault the
    /// saves list had and the same fix: "nothing here" and "something here I cannot read" were four
    /// identical words, and the second is far likelier with files people download.</para>
    /// <para>⚠ <b>Applying is a relaunch, and the note says so before it happens.</b> The texture
    /// array is built once at startup and half the game holds layer numbers into it; rebuilding it
    /// under a live session is the change <see cref="OpenAnotherWorld"/> already declined to make for
    /// the world, for the same reasons.</para>
    /// </remarks>
    private void BuildPackRows()
    {
        var wearing = _settings.TexturePack;

        _hudScreen.Rows.Add(new MenuRow(
            _packs.Count == 1 ? "1 pack installed" : $"{_packs.Count} packs installed", Heading: true));

        _hudScreen.Rows.Add(new MenuRow(
            "Driftwood's own art",
            wearing.Length == 0 ? "worn" : "",
            Note: "Drawn in code, and the only set guaranteed to cover everything in the game"));

        foreach (var pack in _packs)
        {
            _hudScreen.Rows.Add(new MenuRow(
                pack.Name,
                !pack.Readable ? "cannot read"
                    : string.Equals(pack.Name, wearing, StringComparison.OrdinalIgnoreCase) ? "worn"
                    : "",
                Note: pack.Readable
                    ? $"{pack.Kind}. Enter wears it — the game restarts to build the textures"
                    : $"{pack.Kind}. Enter takes it off the shelf"));
        }

        // ⛳ WHAT IT CARRIES THAT WE HAVE NOTHING FOR, which the user asked for by name. The walk
        // has existed since the material pass as --pack-coverage, which is to say it existed for me
        // and not for anybody playing. Only for the pack being worn: it is a content-planning
        // question about the art in front of you, not a survey of the shelf.
        if (_packTally is { } tally && tally.Art > 0)
        {
            var share = tally.Covered * 100.0 / tally.Art;

            _hudScreen.Rows.Add(new MenuRow("what it covers", Heading: true));
            _hudScreen.Rows.Add(new MenuRow(
                "art in this pack", $"{tally.Art:N0} pictures",
                Note: $"{tally.Covered} of them land on something in the game ({share:F0}%). "
                    + "The rest is art for things we have not built yet"));

            foreach (var gap in tally.Biggest)
                _hudScreen.Rows.Add(new MenuRow(
                    gap.Label, $"{gap.Files - gap.Covered} unused",
                    Note: gap.Covered == 0
                        ? $"{gap.Files} files and nothing in the game wears any of them"
                        : $"{gap.Files} files, {gap.Covered} of them used"));
        }

        _hudScreen.Rows.Add(new MenuRow("add one", Heading: true));

        // ⛳ WHATEVER THE LAST IMPORT SAID, ON WHICHEVER ROW YOU ARE STANDING ON. A note is drawn
        // for the selected row only, and the row somebody is on after an import is the one they
        // pressed — so pinning the answer to the typed-path row alone means browsing for a pack
        // succeeds or fails in silence.
        string Said(string otherwise) => _packNote.Length > 0 ? _packNote : otherwise;

        if (NativeFilePicker.Available)
        {
            _hudScreen.Rows.Add(new MenuRow(
                BrowseFileRow, _packPicker.Busy ? "choosing" : "",
                Note: Said($"Opens a file browser. {PackLibrary.FilterLabel} — whichever one you "
                         + "downloaded, wherever it landed")));

            _hudScreen.Rows.Add(new MenuRow(
                BrowseFolderRow, _packPicker.Busy ? "choosing" : "",
                Note: Said("Opens a folder browser, for a pack that has already been unzipped — "
                         + "pick the folder with pack.mcmeta or manifest.json in it")));
        }

        _hudScreen.Rows.Add(new MenuRow(
            "from a path", Edits: _packBox,
            Note: Said("A folder, a .zip, a .mcpack or a .mcaddon. Enter opens the box, enter again "
                     + "copies it onto the shelf — Java, pre-flattening Java and Bedrock all read")));

        _hudScreen.Rows.Add(new MenuRow(
            "the shelf", "", Note: $"Packs live in {PackLibrary.Folder} — dropping one in there by "
                               + "hand works just as well"));
    }

    /// <summary>
    /// Opens the Explorer window, and says so on the row while it is up.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The game keeps running behind it.</b> The chooser is on a thread of its own, so nothing
    /// here waits for it — the answer is collected by <see cref="TakePickedPack"/> whenever it turns
    /// up, which may be a second later or a minute later.
    /// </remarks>
    private void BrowseForPack(NativeFilePicker.Want want)
    {
        if (_packPicker.Busy) return;

        var opened = _packPicker.Ask(
            want,
            _window.Native?.Win32?.Hwnd ?? nint.Zero,
            want == NativeFilePicker.Want.Folder ? "Choose a texture pack folder" : "Choose a texture pack",
            PackLibrary.FilterLabel,
            PackLibrary.FilterSpec);

        _packNote = opened
            ? "choosing — the window is in front of the game"
            : "the file browser would not open; paste the path in below instead";

        RefreshScreen();
    }

    /// <summary>Collects what the chooser came back with, if it has come back.</summary>
    /// <remarks>
    /// ⛔ <b>The path does NOT go through the box.</b> <see cref="TextField"/> keeps only what the
    /// font can draw — 95 glyphs, ASCII 32 to 126 — and its setter drops the rest silently. A pack
    /// under a folder with an accent in its name would arrive correct from Windows, come out of the
    /// box as a path that does not exist, and be refused with "there is nothing at that path",
    /// naming the one thing that was not wrong.
    /// </remarks>
    private void TakePickedPack()
    {
        if (!_packPicker.TryTake(out var picked, out var why)) return;

        if (picked is null)
        {
            // Cancelling is the commonest answer and means nothing went wrong. Only a real failure
            // gets to say anything.
            _packNote = why.Length > 0 ? $"could not choose a file: {why}" : "";
            RefreshScreen();
            return;
        }

        ImportPack(picked);
    }

    /// <summary>Puts the pack named on this row on, or takes an unreadable one off the shelf.</summary>
    private void ChoosePack(string label)
    {
        if (label == "Driftwood's own art")
        {
            WearPack("");
            return;
        }

        foreach (var pack in _packs)
        {
            if (!string.Equals(pack.Name, label, StringComparison.Ordinal)) continue;

            // ⛳ An unreadable row is the only way to get rid of a bad file without leaving the game,
            // which is the whole reason it is listed rather than skipped.
            if (!pack.Readable)
            {
                _packNote = PackLibrary.Remove(pack.Name)
                    ? $"'{pack.Name}' taken off the shelf"
                    : $"'{pack.Name}' could not be removed — it may be open in something else";

                ReadPacksFolder();
                RefreshScreen();
                return;
            }

            WearPack(pack.Name);
            return;
        }
    }

    /// <summary>Remembers the choice and starts again wearing it.</summary>
    /// <remarks>
    /// ⚠ <b>A relaunch, exactly as switching worlds is, and for the same reason.</b> The texture
    /// array is built once at startup, uploaded to the card, and every block, item and particle in
    /// the game holds a layer number into it; rebuilding that under a live session means tearing
    /// down and re-uploading everything while a world is streaming into it. Started again, the
    /// setting is simply read at the top like any other.
    /// </remarks>
    private void WearPack(string name)
    {
        if (string.Equals(_settings.TexturePack, name, StringComparison.Ordinal))
        {
            _packNote = name.Length == 0 ? "already wearing our own art" : $"already wearing '{name}'";
            RefreshScreen();
            return;
        }

        _settings.TexturePack = name;
        _settingsDirty = true;
        _settings.Save();

        // ⛔ The command line is REBUILT without --pack rather than added to. A run started with
        // --pack on it would otherwise carry that pack forward for ever and the screen would appear
        // to do nothing at all — which is the same shape of bug as a setting that never applies.
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            _packNote = "cannot find this program on disk, so it cannot start again";
            RefreshScreen();
            return;
        }

        if (!_atStartScreen) SaveWorld("before changing the texture pack");

        var carried = new List<string>();
        var was = Environment.GetCommandLineArgs();

        for (var i = 1; i < was.Length; i++)
        {
            if (was[i] == "--pack") { i++; continue; }
            carried.Add(was[i]);
        }

        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var argument in carried) start.ArgumentList.Add(argument);

        try
        {
            Process.Start(start);
            _stopRequested = true;
        }
        catch (Exception fault)
        {
            _packNote = $"could not start again: {fault.Message}";
            RefreshScreen();
        }
    }

    /// <summary>Takes a path — typed or chosen — and tries to put what is there on the shelf.</summary>
    /// <remarks>
    /// ⚠ <b>The path is a parameter rather than read off the box</b>, because the chooser's answer
    /// must never be laundered through a field that keeps only what the font can draw.
    /// </remarks>
    private void ImportPack(string from)
    {
        var entry = PackLibrary.Install(from, out var why);

        if (entry is { } added)
        {
            _packNote = $"'{added.Name}' added — {added.Kind}. Enter on its row to wear it";
            _packBox.Clear();
        }
        else
        {
            // ⛔ The reason, said where the attempt was made. An import that fails silently is a
            // player pressing the same key harder.
            _packNote = $"could not add that: {why}";
        }

        ReadPacksFolder();
        RefreshScreen();
    }

    /// <summary>Enter, on a settings row. Toggles what toggles and listens for a key on a binding.</summary>
    private void ActivateRow()
    {
        // A row with a box on it hands over the keyboard, whatever screen it is on and whatever
        // else that row's label would otherwise have meant. One rule, in front of all of them.
        if (_hudScreen.Selected >= 0 && _hudScreen.Selected < _hudScreen.Rows.Count
            && _hudScreen.Rows[_hudScreen.Selected].Edits is { } box)
        {
            // ⛳ The pack box is the one that DOES something when it is accepted rather than merely
            // remembering what was typed — a path is not a setting, it is an instruction.
            StartTyping(box, box == _packBox ? kept => { if (kept) ImportPack(_packBox.Text); } : null);
            return;
        }

        if (_hudScreen.Kind == HudScreenKind.Start)
        {
            ChooseOnStartScreen();
            return;
        }

        // Before the tab dispatch, because it is on every tab and none of them owns it.
        if (_atStartScreen
            && _hudScreen.Selected >= 0
            && _hudScreen.Selected < _hudScreen.Rows.Count
            && _hudScreen.Rows[_hudScreen.Selected].Label == BackToMenu)
        {
            CloseScreen();
            return;
        }

        if (OnTab(GameTab.Controls))
        {
            if (ActionAtRow() is { } action) _rebinding = action;
            RefreshScreen();
            return;
        }

        if (OnTab(GameTab.Packs)
            && _hudScreen.Selected >= 0
            && _hudScreen.Selected < _hudScreen.Rows.Count)
        {
            var row = _hudScreen.Rows[_hudScreen.Selected];
            if (row.Heading) return;

            // ⛔ The two browse rows are taken out BEFORE the fall-through, and by the same names
            // the rows were built from. Everything left on this tab is the name of a pack, and a
            // row reaching ChoosePack that is not one quietly does nothing whatever.
            switch (row.Label)
            {
                case BrowseFileRow: BrowseForPack(NativeFilePicker.Want.File); return;
                case BrowseFolderRow: BrowseForPack(NativeFilePicker.Want.Folder); return;
                case "the shelf": return;
                default: ChoosePack(row.Label); return;
            }
        }

        // ⛳ Taken out BEFORE the fall-through, the same way the pack rows are, because everything
        // left on this tab below its second heading is the name of a world and none of them is a
        // setting AdjustRow could do anything sensible with.
        if (OnTab(GameTab.Saves)
            && _hudScreen.Selected >= 0
            && _hudScreen.Selected < _hudScreen.Rows.Count
            && !_hudScreen.Rows[_hudScreen.Selected].Heading
            && WorldAtRow() is { } world)
        {
            AskToDelete(world);
            return;
        }

        AdjustRow(1, activated: true);
    }

    /// <summary>
    /// The world the selected saves row names, or null when the row is not one.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Asked of the LIST, never of the row's words.</b> The rows above the worlds are readings
    /// ("save now", "played"), and the rows for files that would not open carry a <em>file</em> name
    /// rather than a world name — this exact confusion once let enter on an unreadable row relaunch
    /// the game pointed at a world named after the file, quietly making a new one. A row is a world
    /// when there is a header behind it.
    /// </remarks>
    private string? WorldAtRow()
    {
        var label = _hudScreen.Rows[_hudScreen.Selected].Label.Replace("  (open)", "");
        return _saved.Any(w => w.Name == label) ? label : null;
    }

    /// <summary>
    /// Enter on a world: asks the first time, throws it away the second.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The open world is refused here rather than in <see cref="WorldSave.Delete"/></b>, which
    /// knows about files and has no business knowing what is being played. Deleting it would not
    /// even work — the autosave writes it straight back two minutes later, so the row would come
    /// back on its own and look like a bug in the list.
    /// </remarks>
    private void AskToDelete(string world)
    {
        if (world == _worldName)
        {
            DisarmDelete();
            ShowSelectedRow();
            return;
        }

        if (_deleteArmed != world)
        {
            _deleteArmed = world;
            RefreshScreen();
            ShowSelectedRow();
            return;
        }

        var removed = WorldSave.Delete(world);
        _deleted = removed < 0
            ? $"{world} was already gone"
            : $"threw away {world} and {removed - 1} of its backups";

        Console.WriteLine($"saves       {_deleted}");

        DisarmDelete();
        ReadSavesFolder();

        // The list is one row shorter, so whatever was under the cursor is now something else.
        _hudScreen.Selected = Math.Max(0, _hudScreen.Selected - 1);
        RefreshScreen();
        ShowSelectedRow();
    }

    /// <summary>
    /// What enter does on the menu.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Read off the row's own label, not off its position.</b> The list of worlds is as long as
    /// the saves folder is, so an index would mean counting past a heading whose presence depends on
    /// how many worlds there are — and the row that "quit" is on would move the first time somebody
    /// saved a second world.
    /// </remarks>
    private void ChooseOnStartScreen()
    {
        if (_hudScreen.Selected < 0 || _hudScreen.Selected >= _hudScreen.Rows.Count) return;
        var row = _hudScreen.Rows[_hudScreen.Selected];
        if (row.Heading) return;

        if (_startListing)
        {
            if (row.Label == "back" || row.Label == "none yet")
            {
                _startListing = false;
                _hudScreen.Selected = 1;
                _hudScreen.Scroll = 0;
                RefreshScreen();
                ShowSelectedRow();
                return;
            }

            // The mark the open world's row carries is not part of its name.
            var chosen = row.Label.Replace("  (open)", "");

            // ⛔ Asked of the list, not of the row's words. The rows for files that would not open
            // carry a file name rather than a world name, and enter on one of those used to reach
            // OpenAnotherWorld — which would relaunch the game pointed at a world of that name and
            // quietly make a new one. A row is openable when there is a header behind it.
            if (chosen != _worldName && !_saved.Any(w => w.Name == chosen))
            {
                ShowSelectedRow();
                return;
            }

            if (chosen == _worldName) StartPlaying();
            else OpenAnotherWorld(chosen);
            return;
        }

        switch (row.Label)
        {
            case "carry on":
            case "start a world":
                StartPlaying();
                return;

            case "make another world":
                // A name nothing is under yet, and whatever was typed into the box beneath — or no
                // seed at all, so the new run draws its own random one.
                OpenAnotherWorld(NextWorldName(), _seedBox.Text);
                return;

            case "seed":
                StartTyping(_seedBox);
                return;

            case "open a world":
                ReadSavesFolder();
                _startListing = true;
                _hudScreen.Selected = 0;
                _hudScreen.Scroll = 0;
                RefreshScreen();
                ShowSelectedRow();
                return;

            case "options":
                // The world keeps flying underneath. Escape comes back here rather than to a world
                // nobody has asked to play yet — see CloseScreen.
                OpenGame(GameTab.Controls);
                return;

            case "quit":
                _stopRequested = true;
                return;
        }
    }

    /// <summary>Which action the selected controls row is for, counting past the headings.</summary>
    private GameAction? ActionAtRow()
    {
        if (!OnTab(GameTab.Controls)) return null;

        var seen = 0;
        for (var i = 0; i < _hudScreen.Rows.Count; i++)
        {
            if (_hudScreen.Rows[i].Heading) continue;
            if (i == _hudScreen.Selected) return GameActions.All[seen];
            seen++;
        }

        return null;
    }

    /// <summary>Takes the key the player pressed while the controls tab was listening.</summary>
    /// <remarks>
    /// Escape backs out rather than binding, because escape is how everything else in the game
    /// backs out and a player who changed their mind will press it. That costs the ability to bind
    /// escape itself, which is a fair trade — and it is still bindable by hand in the file.
    /// </remarks>
    private void FinishRebind(GameAction action, Key key)
    {
        _rebinding = null;

        if (key != Key.Escape && InputMap.NameOf(key) is { Length: > 0 } name)
        {
            _settings.Keys.Bind(action, name);
            AfterRebind();
        }

        RefreshScreen();
    }

    private void AfterRebind()
    {
        _keys.Rebuild(_settings.Keys);
        _settingsDirty = true;
        RefreshScreen();
    }

    private static int Nudge(int value, int by, int min, int max) => Math.Clamp(value + by, min, max);

    /// <summary>
    /// Walks the frame limit in fives, with "as fast as it can" off the bottom of the range.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Uncapped sits BELOW thirty rather than above the top.</b> Putting it past the highest
    /// number makes "no limit" the thing a player arrives at by asking for more and more frames,
    /// which is true and reads as a limit of one thousand; putting it under the lowest makes it what
    /// you get by turning the limiter off, which is what it is.
    /// </remarks>
    private static int NudgeCap(int cap, int by)
    {
        if (cap <= 0) return by > 0 ? 30 : 0;

        var next = cap + by * 5;
        return next < 30 ? 0 : Math.Min(next, 1000);
    }

    /// <summary>
    /// Puts the settings into effect, for the ones that can take effect while the game is running.
    /// </summary>
    /// <remarks>
    /// View distance is the exception and says so on its own row. The streamer's radius is fixed
    /// when it is built and changing it means throwing every loaded chunk away, which is a worse
    /// answer than a line of text.
    /// </remarks>
    private void ApplySettings()
    {
        _camera.FovDegrees = _settings.FieldOfView;

        // A percentage of the rate the game shipped with, not an absolute. The number on screen
        // means "how much faster than out of the box", which is the only reading of it a player can
        // check against their own hand.
        _camera.MouseSensitivity = ShippedSensitivity * (_settings.MouseSensitivity / 100f);
        _window.VSync = _settings.VSync;

        // ⛔ The frame limit is NOT set here. IWindow.FramesPerSecond paces Silk's own Run loop and
        // this game drives its own, so setting it looks exactly like a working limiter and does
        // nothing whatever — measured at 12,571 fps against a limit of 175. It is enforced in
        // PaceFrame, which is inside the loop that actually exists.

        var wanted = _settings.Fullscreen ? WindowState.Fullscreen : WindowState.Normal;
        if (_window.WindowState != wanted) _window.WindowState = wanted;

        if (_audio is not null)
            _audio.MasterVolume = _settings.Mute ? 0f : _settings.Volume / 100f;
    }

    /// <summary>
    /// Takes a recipe's ingredients out of the pockets and lays them into the grid.
    /// </summary>
    /// <remarks>
    /// <para><b>This is what a recipe book is for.</b> Not a button that makes the thing — a book
    /// that arranges it, so the result appears in the same square a player would have filled by
    /// hand and lands two squares from the pockets it is going into. It also teaches: the grid is
    /// left holding the answer, so the next one can be made without opening the book at all.</para>
    /// <para>Whatever was already in the grid goes back to the pockets first, so laying a second
    /// recipe over a first cannot leave a stray plank in a corner and quietly match nothing.</para>
    /// </remarks>
    private void LayOut(int index)
    {
        if (index < 0 || index >= _shown.Count) return;
        LayOut(_shown[index]);
    }

    /// <summary>
    /// The recipe the grid is currently holding, so taking the result can lay it out again.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>From the user, 2026-08-05:</b> <i>"once i'm on a door i should be able to keep clicking
    /// the door to get multiple copies of it, the way it is now i have to click on the recipe each
    /// and every time"</i>. Making ten doors was ten trips to the book and ten trips back to the
    /// result square. Held here rather than on the grid because it is a property of <em>how the grid
    /// came to hold what it holds</em>, which the grid itself has no way to know — put the same
    /// planks in by hand and there is no recipe to repeat, only an arrangement.
    /// </remarks>
    private Recipe? _laidOut;

    /// <param name="quiet">True when this is a repeat, which must not make the sound again.</param>
    /// <returns>True when the grid is now holding it.</returns>
    private bool LayOut(Recipe recipe, bool quiet = false)
    {
        if (_hudScreen.Grid is not { } grid) return false;
        if (!recipe.WorkedAt(grid.Station, grid.Width)) return false;
        if (!_book.CanPay(_inventory, recipe)) return false;

        foreach (var left in grid.Empty(_inventory)) Spill(left);

        // Shaped recipes are laid where they were written, in the top left of whatever grid this
        // is; shapeless ones fill in reading order, because they have no arrangement to honour.
        var cell = 0;
        for (var y = 0; y < recipe.Height; y++)
        for (var x = 0; x < recipe.Width; x++)
        {
            if (recipe.At(x, y) is not { } want) continue;

            // Whichever member of the tag is actually being carried. Fewest of it first, so paying
            // for a plank recipe tidies the odd single rather than breaking into a full stack.
            var pick = ItemId.None;
            var fewest = int.MaxValue;

            foreach (var member in want.Members)
            {
                var have = _inventory.CountOf(member);
                if (have <= 0 || have >= fewest) continue;
                pick = member;
                fewest = have;
            }

            if (pick.IsNone) continue;
            if (_inventory.Take(pick, 1) != 1) continue;

            cell = recipe.Shapeless ? cell : y * grid.Width + x;
            Spill(grid.Put(cell, new ItemStack(pick, 1)));
            cell++;
        }

        _laidOut = recipe;
        if (!quiet) PlaySound(SoundMaterial.Wood, SoundEvent.Place, _viewPosition, 0.4f);
        return true;
    }

    /// <summary>
    /// Puts what a smelt needs onto the fire, out of the pockets.
    /// </summary>
    /// <param name="all">
    /// True to fill the input square as far as the pockets and the stack will allow, rather than
    /// putting on a single one.
    /// </param>
    /// <remarks>
    /// <para>⛳ <b>The fire's answer to laying a recipe into a grid.</b> A furnace has one input
    /// square and there is nothing to arrange, so "lay it out" is simply "put the right thing in".
    /// </para>
    /// <para>⛔ <b>Whatever was already in there comes back to the pockets first.</b> Silently
    /// stacking a raw potato on top of a raw steak is not possible — they are different items — so
    /// without this the click would do nothing at all and look broken; and quietly destroying what
    /// was in there is worse than either.</para>
    /// <para>⚠ <b>The member actually carried is what goes in</b>, exactly as the grid does it: a
    /// smelt named against a tag has several things that satisfy it and only one of them is in
    /// somebody's pocket.</para>
    /// </remarks>
    private void LoadFire(int index, bool all)
    {
        if (index < 0 || index >= _shown.Count) return;
        if (_shown[index].At(0, 0) is not { } want) return;

        var fire = _furnaces.Open(_station.X, _station.Y, _station.Z);

        // Whichever member of the tag is in the pockets, fewest of it first — the grid's own rule,
        // so paying tidies an odd single rather than breaking into a full stack.
        var pick = ItemId.None;
        var fewest = int.MaxValue;

        foreach (var member in want.Members)
        {
            var have = _inventory.CountOf(member);
            if (have <= 0 || have >= fewest) continue;
            pick = member;
            fewest = have;
        }

        if (pick.IsNone)
        {
            Notice("none of that in your pockets", _items[want.Members[0]]);
            return;
        }

        // Anything already on the fire comes off before anything else goes on.
        if (!fire.Input.IsEmpty && fire.Input.Item.Value != pick.Value)
        {
            var left = _inventory.Add(fire.Input);
            if (!left.IsEmpty && left.Count == fire.Input.Count)
            {
                Notice("no room to take that off first", _items[fire.Input.Item]);
                return;
            }

            fire.Input = left;
            if (!fire.Input.IsEmpty) return;
        }

        var room = _items[pick].MaxStack - fire.Input.Count;
        if (room <= 0)
        {
            Notice("the fire is full", _items[pick]);
            return;
        }

        var wanted = all ? Math.Min(room, _inventory.CountOf(pick)) : 1;
        var took = _inventory.Take(pick, wanted);
        if (took <= 0) return;

        fire.Input = fire.Input.IsEmpty
            ? new ItemStack(pick, took)
            : new ItemStack(pick, fire.Input.Count + took);

        Notice("on the fire", _items[pick]);
        PlaySound(SoundMaterial.Wood, SoundEvent.Place, _viewPosition, 0.4f);
    }

    /// <summary>Makes the selected recipe outright, straight into the pockets.</summary>
    private void CraftSelected(int index, bool all)
    {
        if (index < 0 || index >= _shown.Count) return;

        var recipe = _shown[index];
        var many = all;
        var made = 0;

        // Bounded even when asked for as many as possible: a bar of logs against a one-log recipe
        // is a few hundred crafts, and every one of them that will not fit goes on the floor.
        for (var i = 0; i < (many ? 64 : 1); i++)
        {
            if (!_book.Craft(_inventory, recipe, out var result)) break;

            var left = _inventory.Add(result);
            if (!left.IsEmpty) _drops.Drop(left, _player.Position + new Vector3(0f, 1f, 0f), 0.4f);
            made++;
        }

        if (made > 0) PlaySound(MaterialOf(recipe.Result), SoundEvent.Place, _viewPosition, 0.7f);
        RefreshScreen();
    }

    /// <summary>
    /// Ages the notices, and puts up a new one when something has become makeable.
    /// </summary>
    /// <remarks>
    /// <para>A burst becomes one notice rather than six. The first pickaxe opens four recipes at
    /// once and stacking them would fill the corner with a list nobody reads — the one that names
    /// itself and counts the rest says the same thing and can be taken in at a glance.</para>
    /// <para>The whole trigger is a diff of what the pockets can pay for. Nothing here knows what
    /// coal is for; picking one up changes the answer and this notices.</para>
    /// </remarks>
    private void StepToasts(float dt)
    {
        for (var i = _toasts.Count - 1; i >= 0; i--)
        {
            _toasts[i].Age += dt;
            if (!_toasts[i].Gone) continue;

            _toasts.RemoveAt(i);
            _audio?.Play(Pick(ActionSounds.ToastOut), _viewPosition, 0.3f, Wobble());
        }

        if (_bench is not null || !_settings.RecipeNotices) return;
        if (!_unlocks.Poll(_book, _inventory, _justUnlocked) || _justUnlocked.Count == 0) return;

        // ⚠ Not written out here any more. What has been announced belongs to the world rather than
        // to the installation, so it travels in the save and goes down with the next one — which is
        // also why an unlock is one of the things worth autosaving on.
        var first = _justUnlocked[0];
        var line = _justUnlocked.Count == 1
            ? first.Name
            : $"{first.Name} and {_justUnlocked.Count - 1} more";

        _toasts.Add(new Toast(
            "you can now make", line, _items[first.Result.Item].IconLayer, ToastSeconds));

        // Oldest first out. Three is what fits down the corner without meeting the hearts.
        while (_toasts.Count > MaxToasts) _toasts.RemoveAt(0);

        _audio?.Play(Pick(ActionSounds.ToastIn), _viewPosition, 0.4f, Wobble());
    }

    /// <summary>The last block a "you need a better pickaxe" notice was raised about.</summary>
    private ushort _warnedAbout;

    /// <summary>
    /// Says what a block wants, when what is in hand is not it.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>Asked for by the user, and it is the half the tier ladder was missing.</b> Before
    /// this the gate was entirely silent: you swung at gold ore with a stone pickaxe and either got
    /// nothing after seven seconds or, now, waited fifteen — with nothing anywhere saying why either
    /// happened. A rule a player cannot see is a rule that reads as a bug.</para>
    /// <para>⚠ <b>Once per KIND of block, not per swing.</b> A notice on every blow is a wall of the
    /// same sentence for as long as somebody holds the button, and the message is worth exactly one
    /// reading. Remembering the last block warned about also means walking from a gold seam to a
    /// stormglass one says the new thing, which is when it is worth saying again.</para>
    /// <para>⚠ The tool it names is <see cref="MiningRules.NeededFor"/>'s answer — the cheapest thing
    /// that would bring it up — rather than the best, because "go and make a stone pickaxe" is
    /// actionable and "you need diamond" is not.</para>
    /// </remarks>
    private void WarnTooHard(BlockType block)
    {
        if (!_settings.RecipeNotices || _warnedAbout == block.Id.Value) return;
        _warnedAbout = block.Id.Value;

        if (MiningRules.NeededFor(block, _items) is not { } needs) return;

        var refused = MiningRules.TooHard(block, _inventory.HeldType);

        _toasts.Add(new Toast(
            refused ? "too hard to break" : "will not come up",
            needs.Label, needs.IconLayer, ToastSeconds));

        while (_toasts.Count > MaxToasts) _toasts.RemoveAt(0);

        _audio?.Play(Pick(ActionSounds.ToastIn), _viewPosition, 0.4f, Wobble());
    }

    // ── The world's own noises: fires crackling, lava muttering, caves being caves ─────────────

    /// <summary>Seconds until the next look around for something audible.</summary>
    private float _untilAmbient = 3f;

    /// <summary>Seconds until the underground is allowed another of its noises.</summary>
    private float _untilCaveSound = 40f;

    /// <summary>The ambience's own stream, so its pattern owes nothing to anyone else's rolls.</summary>
    private readonly Random _ambientRandom = new(0x616D6221);

    /// <summary>What a block id crackles like, or null for silence. Built once from the names.</summary>
    private Dictionary<ushort, string[]>? _crackles;

    /// <summary>
    /// Plays the noises the world makes on its own: every few seconds the nearest lit fire
    /// crackles in its own voice, lava mutters and pops, and — rarely, and only buried in the
    /// dark — the underground says something.
    /// </summary>
    /// <remarks>
    /// <para>One-shots on a timer rather than looping emitters, which is the genre's own approach
    /// and fits an engine whose voices are a fixed pool: a loop per furnace would spend the pool
    /// on scenery. The scan is a small box around the player every few seconds — thousands of
    /// array reads, which is nothing, and it needs no registry of placed fires that a chunk
    /// unload would have to be taught to clean.</para>
    /// <para>⚠ The cave murmurs key on <em>stored sky light</em>, which is what a cell would get
    /// at noon — so a dark house at night stays quiet and a cave at noon does not, the same
    /// distinction the spawner draws and for the same reason.</para>
    /// </remarks>
    private void StepAmbience(float dt)
    {
        if (_audio is null || !_walking || !_spawned || _bench is not null) return;

        _untilAmbient -= dt;
        _untilCaveSound -= dt;

        if (_untilAmbient <= 0f)
        {
            _untilAmbient = 2.5f + (float)_ambientRandom.NextDouble() * 2.5f;
            _crackles ??= BuildCrackleTable();

            var feet = _player.Position;
            var px = (int)MathF.Floor(feet.X);
            var py = (int)MathF.Floor(feet.Y);
            var pz = (int)MathF.Floor(feet.Z);

            string[]? nearestFire = null;
            var nearestFireAt = Vector3.Zero;
            var nearestFireSq = float.MaxValue;
            var lavaCells = 0;
            var lavaAt = Vector3.Zero;

            const int Reach = 10;
            const int ReachY = 6;
            for (var y = py - ReachY; y <= py + ReachY; y++)
            for (var z = pz - Reach; z <= pz + Reach; z++)
            for (var x = px - Reach; x <= px + Reach; x++)
            {
                var id = _streamer.World.GetBlock(x, y, z);
                if (id.IsAir) continue;

                if (_crackles.TryGetValue(id.Value, out var voice))
                {
                    var at = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
                    var distSq = Vector3.DistanceSquared(at, feet);
                    if (distSq < nearestFireSq)
                    {
                        nearestFireSq = distSq;
                        nearestFire = voice;
                        nearestFireAt = at;
                    }
                }
                else if (id == _ids.Lava)
                {
                    // Reservoir-sampled so the pop wanders over a pool instead of always coming
                    // from its lowest corner.
                    lavaCells++;
                    if (_ambientRandom.Next(lavaCells) == 0)
                        lavaAt = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
                }
            }

            if (nearestFire is not null)
                _audio.Play(Pick(nearestFire), nearestFireAt, 0.55f, Wobble());

            if (lavaCells > 0)
            {
                var rumble = _ambientRandom.Next(4) == 0;
                _audio.Play(
                    Pick(rumble ? ActionSounds.LavaAmbience : ActionSounds.LavaPop),
                    lavaAt, rumble ? 0.5f : 0.65f, Wobble());
            }
        }

        if (_untilCaveSound <= 0f)
        {
            _untilCaveSound = 35f + (float)_ambientRandom.NextDouble() * 55f;

            var eye = _camera.Position;
            var buried = LightValue.Sky(_streamer.World.GetLight(
                (int)MathF.Floor(eye.X), (int)MathF.Floor(eye.Y), (int)MathF.Floor(eye.Z))) == 0;

            if (buried)
                _audio.Play(Pick(ActionSounds.CaveAmbience), _viewPosition, 0.5f, 1f);
        }
    }

    /// <summary>Every lit fire in the registry, matched to its own recording set by name.</summary>
    private Dictionary<ushort, string[]> BuildCrackleTable()
    {
        var table = new Dictionary<ushort, string[]>();
        for (ushort id = 1; id < _registry.Count; id++)
        {
            var name = _registry[id].Name;
            if (!name.EndsWith("_lit", StringComparison.Ordinal)) continue;

            table[id] = name.Contains("blast", StringComparison.Ordinal) ? ActionSounds.BlastFurnaceCrackle
                : name.Contains("smoker", StringComparison.Ordinal) ? ActionSounds.SmokerCrackle
                : name.Contains("campfire", StringComparison.Ordinal) ? ActionSounds.CampfireCrackle
                : ActionSounds.FurnaceCrackle;
        }
        return table;
    }

    /// <summary>Tops the herd up, walks it on, lets it be heard, and buries whatever died.</summary>
    private void StepCreatures(float dt)
    {
        TopUpCreatures(dt);
        if (_herd is null) return;

        // ⚠ The body's feet, not the eye. A hostile aims at where somebody is standing, and giving
        // it the camera would make it chase a point two blocks in the air in third person.
        // ⛔ The herd is told which cells actually exist. A restored animal can stand a long way
        // from wherever the player loads in, and unloaded space reads as air — stepped there it
        // falls through the floor its chunk will contain. Frozen instead, until the world arrives.
        _herd.Update(
            dt, SolidForCreature, _walking ? _player.Position : null, Sunlit,
            known: (x, y, z) => _streamer.World.TryGetChunk(ChunkPos.FromWorld(x, y, z), out _),
            water: (x, y, z) => _registry[_streamer.World.GetBlock(x, y, z)].Fluid == FluidKind.Water);

        foreach (var blow in _herd.TakeAttacks())
        {
            _vitals.Hurt(blow.HalfHearts);

            _audio?.Play(Pick(CreatureSounds.Blows), blow.Position, 0.62f, Wobble());

            var cry = CreatureSounds.AngryFor(blow.Kind);
            if (cry.Length > 0) _audio?.Play(Pick(cry), blow.Position, 0.8f, 1.1f);
        }

        foreach (var blast in _herd.TakeBlasts()) Detonate(blast.Position);

        // A blink is a soft pop and a wisp at both ends — the departure matters as much as the
        // arrival, because "where did it go" starts from where it was.
        foreach (var blink in _herd.TakeBlinks())
        {
            _audio?.Play(Pick(CreatureSounds.Blinks), blink.To, 0.6f, Wobble());
            _particles.DeathPuff(blink.From + new Vector3(0f, 1.4f, 0f), 1.1f, StarterBlocks.LayerSmoke, 6);
            _particles.DeathPuff(blink.To + new Vector3(0f, 1.4f, 0f), 1.1f, StarterBlocks.LayerSmoke, 6);
        }

        // A birth is its parents' voice pitched small, and a flutter where the calf stands.
        foreach (var birth in _herd.TakeBirths())
        {
            var voice = CreatureSounds.VoicesFor(birth.Kind);
            if (voice.Length > 0) _audio?.Play(Pick(voice), birth.Position, 0.7f, 1.3f);

            _particles.Puff(_registry[_ids.Emberbloom], birth.Position + new Vector3(0f, 0.4f, 0f), 12);
        }

        foreach (var creature in _herd.All)
        {
            // The hiss, on the frame the fuse catches. The pack's fuse recording runs longer than
            // the fuse itself, which is the right way round: the blast interrupts it.
            if (creature.FuseLit)
                _audio?.Play(Pick(CreatureSounds.Fuses), creature.Middle, 0.9f, 1f);

            // ⛳ The sun's work made visible: one that has stood in daylight past most of its
            // grace is alight, flame licking over the whole of its box with a wisp of smoke —
            // which is how a player reads why the thing chasing them is losing health. Lit a
            // shade before the first tick lands, the way anything catches before it chars.
            if (creature.Alive && creature.Burning >= CreatureHerd.ScorchSeconds * 0.6f
                && Random.Shared.NextDouble() < 9.0 * dt)
            {
                var (low, high) = creature.Bounds();
                var lick = new Vector3(
                    low.X + (float)Random.Shared.NextDouble() * (high.X - low.X),
                    low.Y + (float)Random.Shared.NextDouble() * (high.Y - low.Y) * 0.9f,
                    low.Z + (float)Random.Shared.NextDouble() * (high.Z - low.Z));

                _particles.Flame(lick, 0.55f, StarterBlocks.LayerFlame);

                if (Random.Shared.NextDouble() < 0.3)
                    _particles.Smoke(lick + new Vector3(0f, 0.25f, 0f), 0.4f, StarterBlocks.LayerSmoke);
            }

            if (creature.Shed)
            {
                // ⛳ Laid where it stands rather than thrown out of it, and with no scatter — a hen
                // that walks off leaving an egg rolling across a field is a comedy, not a farm.
                foreach (var laid in _creatureDropTable.Roll(
                             creature.Kind, DropTrigger.Shed, null, creature.Shorn, Random.Shared))
                {
                    _drops.Drop(laid, creature.Position + new Vector3(0f, 0.2f, 0f), 0.15f);
                }

                var lay = CreatureSounds.ShedFor(creature.Kind);
                if (lay.Length > 0) _audio?.Play(Pick(lay), creature.Position, 0.5f, Wobble());
            }

            if (!creature.Spoke) continue;

            var voices = CreatureSounds.VoicesFor(creature.Kind);
            if (voices.Length == 0) continue;

            // ⚠ From the animal's own head rather than its feet, and pitched a little differently
            // every time. Six animals of one kind playing one recording in unison is the tell that
            // it is one recording; a few percent of pitch either way is what makes them individuals.
            _audio?.Play(
                Pick(voices), creature.Position + new Vector3(0f, 0.8f, 0f), 0.7f,
                0.92f + (float)Random.Shared.NextDouble() * 0.16f);
        }

        foreach (var death in _herd.TakeDead())
        {
            _audio?.Play(Pick(CreatureSounds.Deaths), death.Position, 0.68f, Wobble());

            // ⚠ Its own voice, one last time. A kind with a real death recording uses it; the rest
            // low the ordinary voice pitched down. The impact says a blow landed; what says which
            // animal it was is the recording it has been lowing with all afternoon.
            var lastCry = CreatureSounds.DeathCryFor(death.Kind);
            if (lastCry.Length > 0)
            {
                _audio?.Play(Pick(lastCry), death.Position, 0.9f, Wobble());
            }
            else
            {
                var voice = CreatureSounds.VoicesFor(death.Kind);
                if (voice.Length > 0) _audio?.Play(Pick(voice), death.Position, 0.9f, 0.72f);
            }

            // ⛳ And a puff where it was, sized off the animal's own model rather than a constant —
            // a chicken and a cow should not leave the same cloud. Half a body up, because a corpse
            // is on the ground and the cloud is the thing that was standing there.
            var body = _creatureRenderer is not null
                    && _creatureRenderer.TryMeasure(death.Kind, out var measured)
                ? MathF.Max(measured.Y, 0.4f)
                : 1f;

            _particles.DeathPuff(
                death.Position + new Vector3(0f, body * 0.5f, 0f),
                MathF.Min(body, 2.2f),
                StarterBlocks.LayerSmoke);

            // ⛳ What it leaves. ⚠ The kill roll is asked with nothing in hand: what a blow was
            // struck with is already spent — it decided how many blows it took — and a table that
            // read the tool here would hand out more leather to whoever happened to be holding a
            // sword when the last one landed. ⛔ A young one leaves NOTHING: a calf that dropped
            // leather would make the nursery the farm, and killing calves must not pay.
            if (death.Grown)
            {
                foreach (var left in _creatureDropTable.Roll(
                             death.Kind, DropTrigger.Killed, null, death.Shorn, Random.Shared))
                {
                    _drops.Drop(left, death.Position);
                }
            }
        }
    }

    /// <summary>One blast landing on the world: the crater, the drops, the hurt, the noise.</summary>
    /// <remarks>
    /// <para>⛳ <b>The shape is Core's</b> (<see cref="Explosion"/>); this walks the carved cells
    /// through the ordinary edit machinery — relight, particles, the support pass — which is
    /// exactly what already happens when a player mines the same cells one at a time.</para>
    /// <para>⚠ <b>Some rubble survives, most does not.</b> A crater that handed back every block
    /// it ate would make the crawler a mining tool; one that swallowed everything would cost a
    /// player their wall AND the stone it was built from. Forty percent is punishment that leaves
    /// something to rebuild with.</para>
    /// </remarks>
    /// <summary>Starts a cell's fuse burning — every ignition door funnels through here.</summary>
    private void LightFuse(int x, int y, int z, float seconds)
    {
        _fuses.Light((x, y, z), seconds);
        _audio?.Play(
            Pick(CreatureSounds.Fuses), new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), 0.9f, Wobble());
    }

    /// <summary>Burns the cask fuses down and detonates whatever ran out.</summary>
    /// <remarks>
    /// ⛳ <b>The cask cell is cleared BEFORE its blast is applied</b>, or the crater's own sweep
    /// would find the cask still standing and light it again — a self-chain with no end. Mining
    /// the lit cask defuses it: the fuse asks its block still stands every frame, and a fuse
    /// whose block is gone dies with nothing to show for it.
    /// </remarks>
    private void StepFuses(float dt)
    {
        if (!_walking || !_spawned || _fuses.Count == 0) return;

        _burnedDown.Clear();
        _fuses.Update(
            dt,
            cell => _streamer.World.GetBlock(cell.X, cell.Y, cell.Z) == _litCask,
            _burnedDown);

        foreach (var (x, y, z) in _burnedDown)
        {
            _streamer.EditBlock(x, y, z, BlockId.Air);
            Detonate(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
        }
    }

    private void Detonate(Vector3 centre)
    {
        _audio?.Play(Pick(CreatureSounds.Explosions), centre, 1f, Wobble());

        var carved = Explosion.Carve(centre, (x, y, z) =>
        {
            var id = _streamer.World.GetBlock(x, y, z);
            if (id == BlockId.Air) return null;

            var type = _registry[id];

            // A waterlogged block declares a fluid and is not one: the blast carves it like its
            // dry form. Without the second clause every wet fence in the crater would shrug.
            if (type.Unbreakable || (type.Fluid != FluidKind.None && !type.Waterlogged)) return null;

            return type.Hardness;
        });

        foreach (var (x, y, z) in carved)
        {
            var was = _streamer.World.GetBlock(x, y, z);
            if (was == BlockId.Air) continue;   // the support pass may have dropped it already

            var at = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);

            // ⛳ A cask in the crater is LIT, not scattered — the chain is the whole point of
            // laying a line of them. One already burning has its fuse hurried instead, because
            // Light shortens and never lengthens.
            if (Blastcask.IsCask(_registry[was].Name))
            {
                if (was != _litCask) _streamer.EditBlock(x, y, z, _litCask);
                LightFuse(x, y, z, Blastcask.ChainSeconds);
                continue;
            }

            // A station in the crater spills what it held, exactly as mining it does.
            foreach (var spilled in _furnaces.Remove(x, y, z)) _drops.Drop(spilled, at);
            foreach (var spilled in _chests.Remove(x, y, z)) _drops.Drop(spilled, at);

            // The cell keeps the water a wet block stood in — a blast under the sea leaves sea.
            _streamer.EditBlock(x, y, z, _waterlogging.Remains(was));
            _particles.Burst(_registry[was], x, y, z);

            if (Random.Shared.NextDouble() < 0.4)
            {
                var left = _dropTable.Of(was);
                if (!left.IsEmpty) _drops.Drop(left, at);
            }
        }

        foreach (var (x, y, z) in carved) ShedUnsupported(x, y, z);

        _particles.DeathPuff(centre, 2.4f, StarterBlocks.LayerSmoke);

        // Whoever was standing in it — the player from their middle, every animal from its own.
        var hurt = Explosion.HurtAt(centre, _player.Position + new Vector3(0f, 0.9f, 0f));
        if (hurt > 0) _vitals.Hurt(hurt);

        if (_herd is not null)
        {
            foreach (var creature in _herd.All)
            {
                if (!creature.Alive) continue;

                var toll = Explosion.HurtAt(centre, creature.Middle);
                if (toll > 0) _herd.Hurt(creature, toll, centre);
            }
        }
    }

    /// <summary>
    /// Offers an animal what is in hand, when it is that animal's courting food.
    /// </summary>
    /// <remarks>
    /// ⛳ The pairings and the calf are Core's (<see cref="Breeding"/>, the herd's own Court); this
    /// is the gesture and the noise. ⚠ The right food at the wrong moment CLAIMS the click and says
    /// why — wheat swallowed silently by a resting cow, or worse, placed as a block through it,
    /// would be the fire-book fault again: a working mechanic read as a missing one.
    /// </remarks>
    private bool FeedCreature(Creature quarry)
    {
        if (_herd is null || _inventory.HeldType is not { } held) return false;
        if (!Breeding.Takes(quarry.Kind, held.Name)) return false;

        var result = _herd.Feed(quarry, held.Name);

        if (result == CreatureHerd.FeedResult.Refused)
        {
            if (Environment.TickCount64 - _fullNoticeAt > 2000)
            {
                _fullNoticeAt = Environment.TickCount64;
                Notice(quarry.LovedFor > 0f ? "already courting" : "not ready again yet", held);
            }

            return true;
        }

        _inventory.SpendHeld();

        _audio?.Play(Pick(CreatureSounds.Meals), quarry.Middle, 0.5f, Wobble());
        var voice = CreatureSounds.VoicesFor(quarry.Kind);
        if (voice.Length > 0) _audio?.Play(Pick(voice), quarry.Middle, 0.6f, 1.05f);

        // A warm flutter over its head — the emberbloom's reds, which is as near as the particle
        // system comes to hearts.
        _particles.Puff(
            _registry[_ids.Emberbloom], quarry.Middle + new Vector3(0f, 0.5f, 0f),
            result == CreatureHerd.FeedResult.Courting ? 10 : 5);

        return true;
    }

    /// <summary>
    /// Takes what a live animal will give up, if what is in hand is the thing that takes it.
    /// </summary>
    /// <returns>True when the click was spent on the animal, so nothing else may answer it.</returns>
    /// <remarks>
    /// ⚠ <b>Whether it can be sheared is asked before the roll, not after it.</b> A shearing that
    /// rolls nothing still has to consume the click, make its noise and mark the animal — otherwise
    /// a player who shears a sheep on an unlucky frame is told nothing happened at all, and clicks
    /// again, and the sheep they already sheared refuses them.
    /// </remarks>
    private bool HarvestCreature(Creature quarry)
    {
        var held = _inventory.HeldType;
        if (!_creatureDropTable.CanHarvest(quarry.Kind, held, quarry.Shorn)) return false;

        foreach (var taken in _creatureDropTable.Roll(
                     quarry.Kind, DropTrigger.Harvested, held, quarry.Shorn, Random.Shared))
        {
            _drops.Drop(taken, quarry.Middle);
        }

        quarry.Shorn = true;
        quarry.Regrows = CreatureVitals.RegrowSeconds;

        _audio?.Play(Pick(CreatureSounds.Shears), quarry.Middle, 0.55f, Wobble());

        var voice = CreatureSounds.VoicesFor(quarry.Kind);
        if (voice.Length > 0) _audio?.Play(Pick(voice), quarry.Middle, 0.6f, 1.14f);

        if (held is { IsTool: true } && _inventory.WearHeld())
            _audio?.Play(Pick(ActionSounds.ToolBreaks), _viewPosition, 0.7f, Wobble());

        return true;
    }

    /// <summary>
    /// Eats what is in hand, and says whether any of it landed.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Eating FEEDS now, and no longer heals.</b> That is the change hunger is for: a meal
    /// fills the food bar and the food bar is what mends, so the loop is eat to stay fed, stay fed to
    /// mend — rather than food being a potion. <see cref="ItemType.Feeds"/> has always been the
    /// number; what it counts changed from half-hearts to half-drumsticks.
    /// ⚠ <b>Refused at a full bar rather than swallowed.</b> <see cref="PlayerVitals.Eat"/> answers
    /// with what it actually took, so this is one question rather than a second copy of the clamp,
    /// and a roast eaten at nineteen of twenty stays in the pocket instead of being spent for one.
    /// </remarks>
    /// <summary>When the "not hungry yet" notice last went up, so holding the button is not a wall of it.</summary>
    private long _fullNoticeAt = long.MinValue;

    private bool EatHeld()
    {
        // The main hand first, then the other one — the order placing already uses, and it is
        // what makes sword-and-steak the loop it is meant to be: the blade stays pointed at the
        // dark while the meal comes out of the off hand.
        var offhand = _equipment[EquipSlot.Offhand];

        var (food, fromOffhand) = _inventory.HeldType is { IsFood: true } main
            ? (main, false)
            : !offhand.IsEmpty && _items[offhand.Item] is { IsFood: true } spare
                ? (spare, true)
                : ((ItemType?)null, false);

        if (food is null) return false;

        if (_vitals.Eat(food.Feeds) == 0)
        {
            // ⛔ A refusal that says why, which this did not — and a fresh spawn starts with the
            // bar FULL, so anybody trying their first cooked steak straight out of the furnace
            // was told nothing at all and reported eating as not existing. Zero means full AND
            // unhurt now — the overflow mends — so the words say both. The cooldown is what
            // keeps a held button from stacking three copies of the same sentence.
            if (Environment.TickCount64 - _fullNoticeAt > 2000)
            {
                _fullNoticeAt = Environment.TickCount64;
                Notice("full up and unhurt", food);
            }

            return false;
        }

        if (fromOffhand) SpendOffhand();
        else _inventory.SpendHeld();

        _audio?.Play(Pick(CreatureSounds.Meals), _viewPosition, 0.45f, Wobble());

        // The end of a good meal, only when it tops the bar out — a burp per bite is a comedy.
        if (_vitals.Food >= PlayerVitals.MaxFood)
            _audio?.Play(Pick(ActionSounds.Burp), _viewPosition, 0.4f, Wobble());

        return true;
    }

    /// <summary>Lands one blow on an animal, with whatever is in hand behind it.</summary>
    /// <remarks>
    /// ⚠ <b>The tool wears from a swing that connects, and only from one that connects.</b> Same rule
    /// as mining, and for the same reason: a sword that blunted itself on thin air would be a sword
    /// that a player learns to stop swinging.
    /// </remarks>
    private void StrikeCreature(Creature quarry)
    {
        if (_herd is null) return;

        var damage = Combat.DamageOf(_inventory.HeldType);
        var before = quarry.Health;

        _herd.Hurt(quarry, damage, _viewPosition);

        // Still ringing from the last blow, so nothing happened and nothing should be heard.
        if (quarry.Health == before) return;

        // The gains here are levelled against the VOICES rather than picked. The user's animal
        // recordings were normalised to about 0.84 and play at 0.7, so a voice lands near 0.59; the
        // pack's impacts all peak at a flat 1.00, so the same loudness is a lower gain. Measured
        // with --audio-check, which prints every peak: a punch at 0.8 was a third louder than the
        // cow it was landing on.
        _audio?.Play(Pick(CreatureSounds.Blows), quarry.Middle, 0.62f, Wobble());

        // Its voice, raised. ⚠ The hurt cry where a creature has one and its ordinary voice where
        // it does not — a table with a hole in it is quieter than a wrong entry.
        var cry = CreatureSounds.HurtFor(quarry.Kind);
        if (cry.Length > 0 && quarry.Alive) _audio?.Play(Pick(cry), quarry.Middle, 0.85f, Wobble());

        if (_inventory.HeldType is { IsTool: true } && _inventory.WearHeld())
            _audio?.Play(Pick(ActionSounds.ToolBreaks), _viewPosition, 0.7f, Wobble());
    }

    /// <summary>One of a handful of recordings of the same thing, so a run of them is not a loop.</summary>
    private static string Pick(string[] clips) => clips[Random.Shared.Next(clips.Length)];

    /// <summary>A few percent of pitch either way — what stops two of one clip reading as one clip.</summary>
    private static float Wobble() => 0.92f + (float)Random.Shared.NextDouble() * 0.16f;

    /// <summary>Advances every furnace and swaps the block under any whose flame changed.</summary>
    private void StepFurnaces(float dt)
    {
        if (_furnaces.Count == 0) return;

        _furnaces.Update(dt, _relit, (x, y, z) => _smelterKind[_streamer.World.GetBlock(x, y, z).Value]);

        foreach (var (x, y, z, lit) in _relit)
        {
            // ⛳ A table keyed on the block, not a search through one family's four facings. Which
            // way it faces and which family it belongs to are both carried by the id, and the table
            // answers both at once — an IndexOf over the furnaces answered -1 for a blast furnace,
            // so the day a second family arrived every one of them would have gone on burning with
            // nothing to say why.
            var here = _streamer.World.GetBlock(x, y, z);
            var to = (lit ? _smelterLighting : _smelterCooling)[here.Value];
            if (to.IsAir) continue;

            _streamer.EditBlock(x, y, z, to);
        }
    }

    /// <summary>The time of day as a clock face, since 0.62 of a day means nothing to anybody.</summary>
    private static string ClockFace(float time)
    {
        var minutes = (int)MathF.Round(time * 24f * 60f) % (24 * 60);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    private void SelectHandSlot(int slot) => _inventory.Select(slot);

    private void OnScroll(float wheelY)
    {
        if (wheelY == 0f) return;

        // Over a list, the wheel is the list's. Three lines a notch, which is what a wheel means
        // everywhere else on the machine.
        if (_hudScreen.Kind == HudScreenKind.Game)
        {
            ScrollRows(_hudScreen.Scroll - Math.Sign(wheelY) * 3);
            return;
        }

        _inventory.Scroll(-Math.Sign(wheelY));
    }

    /// <summary>Moves the settings list's window, held inside what there is to look at.</summary>
    private void ScrollRows(int to) =>
        _hudScreen.Scroll = Math.Clamp(
            to, 0, Math.Max(0, _hudScreen.Rows.Count - ScreenLayout.MenuLines(LayoutHeight)));

    /// <summary>How tall the screen is in layout units — what the overlay lays everything out in.</summary>
    private float LayoutHeight => _window.Size.Y / HudRenderer.ScaleFor(_window.Size.Y);

    /// <summary>
    /// Scrolls the list so the picked row is on it.
    /// </summary>
    /// <remarks>
    /// Only when it has fallen off, and only far enough. Re-centring on every step makes the whole
    /// list slide under a player who pressed down once, which reads as the selection standing still
    /// and everything else moving.
    /// </remarks>
    private void ShowSelectedRow()
    {
        if (_hudScreen.Kind != HudScreenKind.Game) return;

        var lines = ScreenLayout.MenuLines(LayoutHeight);

        // A heading sits above the row it heads, so pulling one extra line into view when scrolling
        // up keeps the group's name with it rather than just above the top edge.
        if (_hudScreen.Selected < _hudScreen.Scroll + 1) ScrollRows(_hudScreen.Selected - 1);
        else if (_hudScreen.Selected >= _hudScreen.Scroll + lines) ScrollRows(_hudScreen.Selected - lines + 1);
    }

    /// <summary>
    /// Break on the left button, place on the right, both by swinging at it.
    /// </summary>
    /// <remarks>
    /// The button does not edit the world. It starts a swing, and the swing edits the world — see
    /// <see cref="PlayerAnimator.TakeStrikes"/>. That indirection is the whole fix: a block used to
    /// vanish the instant a mouse event arrived, with nothing on screen having moved, and holding
    /// the button did nothing at all. Now the arm comes down and the block goes with it, at the
    /// animation's pace, for as long as the button is held.
    /// </remarks>
    private void OnMouseDown(MouseButton button)
    {
        if (_bench is not null) return;

        // A screen takes the buttons. It used to swallow them, which is the whole of the fault
        // behind "the menu opens and I cannot do anything with it": the pointer was let go of, the
        // system's arrow appeared over a screen full of squares, and every click landed on nothing.
        if (_hudScreen.IsOpen)
        {
            ScreenClick(button);
            return;
        }

        // A click into a released cursor takes the mouse back rather than editing the world —
        // otherwise clicking the window to focus it digs a hole in whatever was under the crosshair.
        if (!_mouseCaptured)
        {
            SetMouseCaptured(true);
            return;
        }

        switch (button)
        {
            case MouseButton.Left:
                _holdingBreak = true;
                _lastStrikeWasBreak = true;
                _animator.Strike();
                break;

            case MouseButton.Right:
                _holdingPlace = true;
                _lastStrikeWasBreak = false;
                _animator.Strike();
                break;
        }
    }

    private void OnMouseUp(MouseButton button)
    {
        switch (button)
        {
            case MouseButton.Left: _holdingBreak = false; _draggingScrollbar = false; break;
            case MouseButton.Right: _holdingPlace = false; break;
        }
    }

    private void SetMouseCaptured(bool captured)
    {
        _mouseCaptured = captured;
        ApplyCursorMode();

        // Letting the cursor go stops the mining. A button-up outside the window never arrives, so
        // without this, releasing the mouse mid-swing leaves the player digging forever.
        if (!captured) _holdingBreak = _holdingPlace = false;
    }

    /// <summary>
    /// What the window does with the pointer: swallow it, hide it, or leave it alone.
    /// </summary>
    /// <remarks>
    /// <para>Three states rather than two, and the third is the one that was missing. Playing takes
    /// the pointer outright — <c>Raw</c>, no acceleration, no edge to hit. A screen <em>hides</em>
    /// it: the position is still real window coordinates, which is what makes hit testing a
    /// division rather than a running total of deltas, and the arrow that gets drawn is ours.</para>
    /// <para>Ours because a desktop cursor over an interface like this is the one thing on screen
    /// that is not pixel art — it is anti-aliased, it is whatever theme the machine is wearing, and
    /// it is drawn at a size that has nothing to do with anything under it.</para>
    /// </remarks>
    private void ApplyCursorMode()
    {
        _input.SetCursor(
            _hudScreen.IsOpen ? CursorMode.Hidden
            : _mouseCaptured ? CursorMode.Raw
            : CursorMode.Normal);

        _haveMouseAnchor = false;
    }

    private void OnMouseMove(Vector2 position)
    {
        // Under a screen the position is real window coordinates, so the pointer is simply where
        // the mouse is, divided down into layout units. No accumulation, nothing to drift, and
        // nothing to re-anchor when the window is resized under it.
        if (_hudScreen.IsOpen)
        {
            var scale = HudRenderer.ScaleFor(_window.Size.Y);
            _hudScreen.Pointer = new Vector2(
                Math.Clamp(position.X / scale, 0f, _window.Size.X / scale),
                Math.Clamp(position.Y / scale, 0f, _window.Size.Y / scale));

            // A drag follows the pointer wherever it went, including off the end of the track —
            // which is the whole point of a drag, and is why it is not simply another click.
            if (_draggingScrollbar)
            {
                foreach (var zone in _layout.Zones)
                    if (zone.Kind == ZoneKind.Scrollbar) DragScrollbar(zone);
            }

            _haveMouseAnchor = false;
            return;
        }

        // Playing, the report is a raw delta stream and absolute coordinates mean nothing. Track
        // the difference from the previous report and re-anchor whenever the mode changes, so
        // switching between the two never snaps the view.
        if (!_haveMouseAnchor)
        {
            _lastMousePos = position;
            _haveMouseAnchor = true;
            return;
        }

        var delta = position - _lastMousePos;
        _lastMousePos = position;

        if (_mouseCaptured) _camera.ApplyMouseDelta(delta.X, delta.Y);
    }

    private void OnUpdate(double dt)
    {
        // ⛔ CAPPED, AND THE FIRST UPDATE IS WHY. The window's clock starts when the window does, so
        // the first dt handed over covers everything before it — the textures, the shaders, the
        // first chunks — and measured, that is about ten seconds. Uncapped it went into played time
        // (a five-second session recorded ten), wound the sun on, drifted the clouds, and would have
        // stepped the body through a swept collision ten seconds wide the moment the spawn chunk
        // arrived a frame later.
        //
        // A quarter of a second is well past any frame this game has ever taken and well short of
        // anything that could be called playing. The benchmark is unaffected: it measures with
        // Stopwatch timestamps and its own clock, never with this.
        dt = Math.Min(dt, MaxStep);

        // ⚠ Held still for the check, which reads a moving title off the framebuffer and would
        // otherwise sample a different frame of it every run.
        // ⚠ Held still for the check, which reads a moving title and a turning page off the
        // framebuffer and would otherwise sample a different frame of each every run. The check
        // moves it on purpose, once, to prove the page is turning at all — see "book turn".
        if (_options.UiCheck) _hudScreen.Drift = _uiDrift;
        else _hudScreen.Drift += (float)dt;

        // The caret's own clock. Held at a lit moment for the check, for the same reason the title
        // is: a blink sampled off the framebuffer is a blink that is there half the time.
        _hudScreen.Clock = _options.UiCheck ? 0.2f : _hudScreen.Clock + (float)dt;

        // A frame is measured from the top of one update to the top of the next, so the swap and
        // the driver's share of the wait are inside it. Timing only the callbacks would report a
        // renderer that never stalls while the player watches it stutter.
        var now = Stopwatch.GetTimestamp();
        if (_frameOpen) CloseBenchFrame(now);
        _frameStart = now;
        _frameOpen = true;
        _uploadsThisFrame = 0;

        if (_benchPath is not null)
        {
            // The path clock is the measured time itself, so the camera is exactly where the
            // report says it was. It holds at the start until the world has finished arriving.
            var (position, yaw, pitch) = _benchPath.At(_benchWarmingUp ? 0 : _bench?.ElapsedSeconds ?? 0);
            _camera.Position = position;
            _camera.Yaw = yaw;
            _camera.Pitch = pitch;
        }
        else if (_atStartScreen && _menuPath is not null)
        {
            // A quarter of the benchmark's speed. That path is sized to turn the whole loaded set
            // over inside fifteen seconds, which is a great deal faster than anything worth looking
            // at while deciding whether to press start.
            _menuTime += dt * 0.25;
            var (position, yaw, pitch) = _menuPath.At(_menuTime);
            _camera.Position = position;
            _camera.Yaw = yaw;
            _camera.Pitch = pitch;
        }
        else if (_walking)
        {
            StepPlayer((float)dt);
        }
        else
        {
            _camera.Update((float)dt, _input);
        }

        _elapsed += dt;

        // Closed the way a player closes it, so the exit path under test is the one that ships.
        // ⛳ Frames counted through the ORDINARY loop, which --bench cannot report because it is
        // deliberately never capped. A limiter that silently does nothing is exactly the sort of
        // setting this project keeps finding, and the only thing that tells one from a working one
        // is a rate measured on the path a player actually runs.
        //
        // ⛔⛔ COUNTED AFTER THE WARM-UP, and the first version of this was NOT — it averaged over the
        // whole run and reported 154 against a limit of 175 on a build the user could see holding a
        // steady 175 on screen. The opening seconds are chunk generation, meshing and uploads, which
        // is the slowest the game ever runs; a mean taken across them measures the loading and calls
        // it the frame rate. --bench has always separated its own warm-up for exactly this reason and
        // this line did not. The user's eyes were right and the instrument was wrong.
        if (_elapsed >= WarmUpSeconds)
        {
            if (_playedFrames == 0) _steadyFrom = _elapsed;
            _playedFrames++;
        }

        if (_options.PlaySeconds > 0 && _elapsed >= _options.PlaySeconds)
        {
            _stopRequested = true;

            var steady = _elapsed - _steadyFrom;
            Console.WriteLine(
                $"played      {_playedFrames:N0} frames over {steady:F1}s once the world had arrived "
                + $"— {(steady > 0 ? _playedFrames / steady : 0):F0} fps against a limit of "
                + (_settings.VSync ? "the display"
                   : _settings.FrameCap <= 0 ? "none" : $"{_settings.FrameCap}"));
            Console.Out.Flush();
        }

        _clock.Advance((float)dt);
        _skyState = _clock.Now;
        _audio?.SetListener(_viewPosition, _viewForward);

        UpdateTarget();
        StepAnimation((float)dt);
        StepFootfall((float)dt);
        StepVitals((float)dt);
        StepFluid((float)dt);
        _signalNow += dt;
        StepSignals(_signalNow, (float)dt);
        StepFuses((float)dt);
        StepLeaffall((float)dt);
        StepFurnaces((float)dt);
        StepCreatures((float)dt);
        StepCarts((float)dt);
        StepToasts((float)dt);
        StepAmbience((float)dt);
        StepAutosave(dt);
        _particles.Update(_streamer.World, (float)dt);
        StepFires((float)dt);

        // ⛳ The file chooser's answer, whenever it arrives. It runs on a thread of its own so the
        // world keeps drawing behind it, which means nothing here waits — this is one volatile read
        // a frame and the only place the picked path crosses back onto the game's thread.
        TakePickedPack();

        StepArmour();

        // What a screen can afford changes as the world hands things over, so it is recomputed
        // rather than only rebuilt on a keypress: a stack flying into the bar while the book is
        // open should light up what it just made possible.
        if (_hudScreen.Kind == HudScreenKind.Player) RefreshScreen();

        // What the pointer is over, from the layout the overlay built last frame. A frame behind on
        // purpose — the alternative is laying the whole screen out twice — and at sixty a second
        // nothing about a highlight is visible one frame late.
        _hudScreen.Hovered = _hudScreen.IsOpen
            ? _layout.At(_hudScreen.Pointer.X, _hudScreen.Pointer.Y)
            : null;

        // Collected from the middle of the body rather than the feet, so a stack lying against a
        // wall is still reachable from the other side of it.
        var collector = _bench is null && _walking && _spawned
            ? _player.Position + new Vector3(0f, PlayerBody.Height * 0.5f, 0f)
            : (Vector3?)null;

        var picked = _drops.Update(_streamer.World, (float)dt, collector, collector is null ? null : _inventory);
        if (picked > 0 && collector is { } where)
            _audio?.Play(Pick(ActionSounds.Pickup), where, 0.45f, Wobble());
        PlaceCamera();
        PumpStreaming();

        // Injected fault, off unless --stall asked for it. Burns CPU rather than sleeping, so it
        // stands in for work the game might one day do rather than for a thread that yielded.
        if (_options.StallMs > 0 && _bench is not null && !_benchWarmingUp && _bench.Recorded % 200 == 199)
        {
            var until = Stopwatch.GetTimestamp() + (long)(_options.StallMs * Stopwatch.Frequency / 1000.0);
            while (Stopwatch.GetTimestamp() < until) { }
        }

        _updateMs = (Stopwatch.GetTimestamp() - now) * TicksToMs;

        _titleTimer += dt;
        _framesSinceTitle++;
        if (_titleTimer >= 0.25)
        {
            _fps = _framesSinceTitle / _titleTimer;
            _titleTimer = 0;
            _framesSinceTitle = 0;

            var p = _camera.Position;
            var queued = _streamer.PendingGenerate + _streamer.PendingLight + _streamer.PendingMesh;
            _window.Title = _bench is not null
                ? (_benchWarmingUp
                    ? $"Driftwood bench — settling ({_streamer.PendingGenerate + _streamer.PendingLight + _streamer.PendingMesh} queued) | {_fps:F0} fps"
                    : $"Driftwood bench — {_bench.ElapsedSeconds:F1}/{_bench.DurationSeconds:F0} s | {_fps:F0} fps | "
                      + $"{_drawnChunks}/{_meshes.Count} drawn")
                : $"Driftwood — {_fps:F0} fps | seed {_seed} | "
                  + $"xyz {p.X:F0} {p.Y:F0} {p.Z:F0} | "
                  + $"{ClockFace(_clock.TimeOfDay)}{(_clock.Running ? "" : " held")} | "
                  + $"holding {_inventory.Selected + 1}. "
                  + (_inventory.HeldType is not { } held
                      ? "nothing"
                      : held.Durability > 0
                          ? $"{held.Label} ({held.Durability - _inventory.Held.Damage} left)"
                          : $"{held.Label} x{_inventory.Held.Count}")
                  + " | "
                  + $"{_drawnChunks}/{_meshes.Count} drawn, {_drawnTriangles:N0} tris"
                  + (queued > 0 ? $" | {queued} queued" : "")
                  + (_frustumCulling ? "" : " | CULLING OFF");
        }
    }

    /// <summary>
    /// Advances the pose, works the target loose, and places on each swing that came round.
    /// </summary>
    /// <remarks>
    /// Breaking and placing are paced differently on purpose. Placing is instant and happens once
    /// per swing, because putting a block down is one motion. Breaking is work: the block gives way
    /// when enough of it has been done, which for soft ground is inside a single swing and for ore
    /// is a great many. Both keep the arm moving, so either way something on screen is causing it.
    /// </remarks>
    private void StepAnimation(float dt)
    {
        if (_bench is not null) return;

        var stood = _walking ? _player.Position : _camera.Position;
        var sneaking = _walking && _player.Sneaking;

        // ⛳ THE COST OF THE SHIELD, and it is deliberately not a number. A raised shield is a hand
        // holding a board in front of you: while it is up nothing is mined, nothing is struck and
        // nothing is placed. That is a price a player feels the instant they pay it, where a
        // movement penalty is a multiplier nobody notices and every check would have to be
        // calibrated against.
        var blocking = _vitals.ShieldRaised;

        _animator.Update(
            dt, stood, _camera.Yaw, PlayerBody.WalkSpeed, sneaking,
            !blocking && (_holdingBreak || _holdingPlace),
            blocking);

        // ⚠ Taken and then discarded rather than not taken. The animator counts swings whether or
        // not anybody asks for them, so a shield held up for a minute would otherwise release a
        // minute's worth of blows the moment it came down.
        var strikes = _animator.TakeStrikes();
        if (blocking) strikes = 0;

        // A click fast enough to be released inside one frame still registered its intent when it
        // went down, so fall back to that rather than dropping the swing on the floor.
        var placing = _holdingPlace || (!_holdingBreak && !_lastStrikeWasBreak);

        for (; strikes > 0 && placing; strikes--)
        {
            UseOrPlace();
            UpdateTarget();
        }

        // ⛳ A blow at an animal, and it spends the swing. The arm is the clock for this exactly as
        // it is for mining — a strike is one blow whatever the frame rate, and the creature's own
        // cooldown is the second half of that so a fast arm cannot whittle a cow down in a frame.
        if (strikes > 0 && !placing && _creatureTarget is { } quarry)
        {
            StrikeCreature(quarry);
            strikes--;
        }

        var target = _target is { } hit ? _registry[_streamer.World.GetBlock(hit.X, hit.Y, hit.Z)] : null;
        var cell = _target is { } at ? (at.X, at.Y, at.Z) : ((int, int, int)?)null;

        // Chips fly off the face the blow lands on, not off the block as a whole. Once per swing
        // rather than every frame, so the spray keeps the arm's rhythm instead of being a hose.
        if (strikes > 0 && !placing && target is not null && _target is { } struck)
        {
            var middle = new Vector3(struck.X + 0.5f, struck.Y + 0.5f, struck.Z + 0.5f);

            // ⛳ A SWING THAT BOUNCES OFF, and the tone is the whole message. Two rungs under is a
            // refusal rather than a very long wait — no progress, no chips, and nothing spent — so
            // the only thing left to say it with is the sound, and the user's own idea was to say it
            // by dropping the pitch. ⛔ It must still make a NOISE: a swing that does nothing and is
            // silent is indistinguishable from a game that has stopped taking input.
            if (MiningRules.TooHard(target, _inventory.HeldType))
            {
                PlaySound(target, SoundEvent.Hit, middle, 0.65f, pitch: 0.55f);
                WarnTooHard(target);
            }
            else
            {
                _particles.Chip(target, struck.X, struck.Y, struck.Z, struck.Face);
                PlaySound(target, SoundEvent.Hit, middle, 0.55f);

                // And the quieter half of the same lesson: it WILL come up, one rung under, and it
                // is going to take a while. Said once so a player knows the wait is the rule rather
                // than a stutter.
                if (!MiningRules.CanHarvest(target, _inventory.HeldType)) WarnTooHard(target);
            }
        }

        if (!_mining.Update(dt, target, cell, !blocking && _holdingBreak, _inventory.HeldType)) return;

        // The burst goes before the block does. Reading the type after BreakTarget gets air.
        if (target is not null && cell is { } broken)
        {
            var centre = new Vector3(broken.Item1 + 0.5f, broken.Item2 + 0.5f, broken.Item3 + 0.5f);
            _particles.Burst(target, broken.Item1, broken.Item2, broken.Item3);
            PlaySound(target, SoundEvent.Break, centre);

            // What the block leaves depends on what took it. Below the tier line it still comes
            // apart and leaves nothing, which is the whole reason to go and make a pickaxe.
            _drops.Drop(_dropTable.Harvest(target, _inventory.HeldType), centre);

            // ⛳ A RIPE CROP LEAVES ITS SEED AS WELL AS ITS HARVEST, and that is what makes a field
            // repay itself rather than being spent. BlockDrops is one item per block by design, so
            // the second one is here — the first honest reason this game has had for a block to
            // leave two things.
            //
            // ⚠ One to three, so a field grows slowly rather than doubling every harvest. At exactly
            // one it can only ever break even and a bad roll ends the farm; at a guaranteed two it
            // fills a chest by the third season.
            if (_growth.IsRipe(target.Id))
            {
                _drops.Drop(
                    new ItemStack(_items.ByName("seeds").Id, 1 + _growthRandom.Next(3)), centre);

                // A ripe crop coming up sounds like a harvest, on top of the plant's own rustle.
                _audio?.Play(Pick(ActionSounds.Harvest), centre, 0.6f, Wobble());
            }

            // A tool that did the work wears from it. Only the tool: a bare hand is free, and so is
            // a plank held like a club, which is why this asks the item rather than the swing.
            if (_inventory.HeldType is { IsTool: true } && _inventory.WearHeld())
                _audio?.Play(Pick(ActionSounds.ToolBreaks), _viewPosition, 0.7f, Wobble());
        }

        BreakTarget();
        UpdateTarget();
    }

    /// <summary>
    /// Kicks dust out of the ground under a walking or landing player.
    /// </summary>
    /// <remarks>
    /// Taken from what is under the feet rather than from a fixed colour, so crossing from grass
    /// onto sand changes the colour of the dust without anything here knowing either exists. A
    /// landing is the same effect with more of it, keyed off the fall the body already measured.
    /// </remarks>
    private void StepFootfall(float dt)
    {
        if (_bench is not null || !_walking) return;

        // The body clears its fall the instant it lands, so the distance has to be kept here while
        // it is still in the air or there is nothing left to read on the frame it touches down.
        var onGround = _player.OnGround;
        if (!onGround) _fallInAir = MathF.Max(_fallInAir, _player.FallDistance);

        var landed = onGround && !_wasOnGround ? _fallInAir : 0f;
        if (onGround) _fallInAir = 0f;
        _wasOnGround = onGround;

        var under = _streamer.World.GetBlock(
            (int)MathF.Floor(_player.Position.X),
            (int)MathF.Floor(_player.Position.Y - 0.1f),
            (int)MathF.Floor(_player.Position.Z));

        // A ladder has its own rungs, on the walking cadence but measured up the wall. Asked
        // before the ground test because a body on a ladder has nothing under its feet.
        if (_player.OnLadder && !_player.OnGround)
        {
            _stepDistance += MathF.Abs(_player.Velocity.Y) * dt;
            if (_stepDistance >= 1.4f)
            {
                _stepDistance = 0f;
                _audio?.Play(Pick(ActionSounds.LadderStep), _player.Position, 0.5f, Wobble());
            }
            return;
        }

        // Swimming strokes likewise: covered distance in all three axes, because a dive straight
        // down is as much swimming as a crossing.
        if (_player.InWater && !_player.OnGround)
        {
            _stepDistance += _player.Velocity.Length() * dt;
            if (_stepDistance >= 2.4f)
            {
                _stepDistance = 0f;
                _audio?.Play(Pick(ActionSounds.SwimStroke), _player.Position, 0.45f, Wobble());
            }
            return;
        }

        if (under.IsAir) { _stepDistance = 0f; return; }
        var type = _registry[under];

        // A landing worth noticing, sized by how far it fell.
        if (landed > 1.2f)
        {
            _particles.Puff(type, _player.Position, Math.Min(4 + (int)landed * 2, 20), MathF.Min(landed / 3f, 2.4f));
            PlaySound(type, SoundEvent.Step, _player.Position, MathF.Min(0.6f + landed * 0.15f, 1.4f));
            _stepDistance = 0f;
            return;
        }

        // Otherwise a scuff every stride, measured in distance rather than in time so it keeps
        // pace with the legs whatever the frame rate is.
        if (!_player.OnGround) return;

        var speed = new Vector2(_player.Velocity.X, _player.Velocity.Z).Length();
        _stepDistance += speed * dt;
        if (_stepDistance < 2.1f) return;

        _stepDistance = 0f;
        if (speed <= 0.5f) return;

        _particles.Puff(type, _player.Position, 2, 0.4f);
        PlaySound(type, SoundEvent.Step, _player.Position, _player.Sneaking ? 0.18f : 0.45f);
    }

    /// <summary>
    /// Puts the render camera where the current view mode wants it, without letting it into a wall.
    /// </summary>
    /// <remarks>
    /// <see cref="FlyCamera.Position"/> stays the eye in every mode, which is what keeps aiming
    /// honest: a ray cast from a camera four blocks behind the player would target whatever the
    /// boom happened to be pointing through, and would reach round corners.
    /// </remarks>
    private void PlaceCamera()
    {
        var eye = _camera.Position;
        var look = _camera.Forward;

        // Nothing to stand behind while flying, and the fly camera is an inspection tool — pulling
        // it back from a body that is not being simulated would only make terrain harder to look at.
        if (_view == ViewMode.First || _bench is not null || !_walking)
        {
            _viewPosition = eye;
            _viewForward = look;
            return;
        }

        var boom = _view == ViewMode.ThirdBehind ? -look : look;
        var reach = CameraBoom.Reach(_streamer.World, _solid, eye, boom, CameraBoom.Distance);

        _viewPosition = eye + boom * reach;
        _viewForward = _view == ViewMode.ThirdBehind ? look : -look;
    }

    /// <summary>Reads the baked light where the player is standing, for shading the model.</summary>
    private EntityLight SampleLight(Vector3 at)
    {
        var x = (int)MathF.Floor(at.X);
        var y = (int)MathF.Floor(at.Y);
        var z = (int)MathF.Floor(at.Z);

        // Unloaded space reads as pitch dark, and a model blacked out because its chunk has not
        // arrived looks like a bug in the lighting. Daylight is the better wrong answer.
        if (!_streamer.World.TryGetChunk(ChunkPos.FromWorld(x, y, z), out _))
            return new EntityLight(1f, Vector3.Zero);

        var packed = _streamer.World.GetLight(x, y, z);
        var block = new Vector3(LightValue.Red(packed), LightValue.Green(packed), LightValue.Blue(packed))
            / LightValue.Max;

        // The carried light reaches bodies exactly as it reaches walls — same source, same
        // falloff — so a cow beside your lantern is lit by it and your own arm most of all.
        if (_heldGlow != Vector3.Zero)
        {
            block = Vector3.Max(
                block,
                _heldGlow * MathF.Max(0f, 1f - Vector3.Distance(at, HeldGlowPos) / HeldGlowRange));
        }

        return new EntityLight(LightValue.Sky(packed) / (float)LightValue.Max, block);
    }

    /// <summary>
    /// Lets the odd leaf go from a canopy near the player.
    /// </summary>
    /// <remarks>
    /// <para>Sampled rather than scanned. Finding every leaf block with air under it inside the
    /// view would be a search over millions of cells for an effect that wants one particle every
    /// second or two; throwing a handful of darts at the neighbourhood and using whichever land on
    /// a canopy edge costs nothing and looks the same.</para>
    /// <para>Only leaves with nothing under them, so the leaf falls out of the underside of a crown
    /// rather than out of the middle of one and straight into the leaf below it.</para>
    /// </remarks>
    private void StepLeaffall(float dt)
    {
        if (_bench is not null || !_spawned) return;

        _untilLeaf -= dt;
        if (_untilLeaf > 0f) return;

        // A leaf every second or so when there is a wood to shed one, and nothing at all when
        // there is not — the darts simply miss over open ground.
        _untilLeaf = 0.6f + (float)_soundPick.NextDouble() * 1.1f;

        const int Darts = 24;
        const int Reach = 22;

        var eye = _viewPosition;
        for (var i = 0; i < Darts; i++)
        {
            var x = (int)MathF.Floor(eye.X) + _soundPick.Next(-Reach, Reach + 1);
            var y = (int)MathF.Floor(eye.Y) + _soundPick.Next(-4, 13);
            var z = (int)MathF.Floor(eye.Z) + _soundPick.Next(-Reach, Reach + 1);

            var here = _streamer.World.GetBlock(x, y, z);
            if (here.Value != _leafBlock.Value) continue;
            if (!_streamer.World.GetBlock(x, y - 1, z).IsAir) continue;

            _particles.Leaf(_registry[here], new Vector3(x + 0.5f, y, z + 0.5f));
            return;
        }
    }

    /// <summary>
    /// Advances health and breath, and puts the player back on their feet when they run out.
    /// </summary>
    /// <remarks>
    /// Only while walking. The fly camera is an inspection tool and a tool that can drown you is a
    /// worse one, so nothing is simulated for it.
    /// ⛔⛔ <b>But the bar used to STAY WHERE IT WAS, and that is a bar that lies.</b> Reported by the
    /// user: the bubbles were <i>"not reducing while you're in the water"</i> and <i>"should disappear
    /// entirely when you leave the water but they don't"</i>. Both are the same state — air stopped
    /// half spent the moment the body stopped being stepped, so it never went down, never came back,
    /// and the row sat on screen for the rest of the session showing a number that had stopped meaning
    /// anything. The lungs are simply full while nothing is being simulated, which is the honest
    /// reading of "you cannot drown right now" and which the bar already knows how to hide on.
    /// </remarks>
    private void StepVitals(float dt)
    {
        if (_bench is not null || !_walking || !_spawned)
        {
            _vitals.CatchBreath();
            return;
        }

        var what = _vitals.Update(_streamer.World, _player, dt);

        // ⛳ What the armour was asked to stand up to, and what that cost it. Read here rather than
        // where each blow lands because there are three places a blow comes from — a fall, the
        // world, a creature — and the wear rule belongs in one of them, not three.
        var (armourHit, shieldHit) = _vitals.TakeWear();

        if (Armour.Wear(_equipment, _items, armourHit) > 0)
            PlaySound(SoundMaterial.Stone, SoundEvent.Break, _viewPosition, 0.7f);

        if (Armour.WearShield(_equipment, _items, shieldHit))
            PlaySound(SoundMaterial.Wood, SoundEvent.Break, _viewPosition, 0.7f);

        // A hurt is voiced by what caused it now: a gasp going under, a cry in the fire, a thud
        // sized and surfaced by the landing. A creature's blow is already voiced where it lands,
        // and a hunger pang by the bar, so neither needs a second noise here.
        if (what.Hurt > 0)
        {
            switch (what.Cause)
            {
                case VitalsCause.Drown:
                    _audio?.Play(Pick(ActionSounds.DrownGasp), _viewPosition, 0.85f, Wobble());
                    break;

                case VitalsCause.Burn:
                    _audio?.Play(Pick(ActionSounds.BurnHurt), _viewPosition, 0.85f, Wobble());
                    break;

                // A sting, not a wound: the ordinary impact set, quiet and pitched up.
                case VitalsCause.Prick:
                    _audio?.Play(Pick(CreatureSounds.Blows), _viewPosition, 0.5f, 1.15f);
                    break;

                case VitalsCause.Fall:
                {
                    var under = _streamer.World.GetBlock(
                        (int)MathF.Floor(_player.Position.X),
                        (int)MathF.Floor(_player.Position.Y - 0.1f),
                        (int)MathF.Floor(_player.Position.Z));

                    // The pack records hard landings per surface; a short fall is one thud
                    // whatever it hit.
                    var landing = what.Hurt >= 3 && !under.IsAir
                        ? ActionSounds.FallBigFor(_registry[under].Sounds)
                        : what.Hurt >= 3 ? ActionSounds.FallBig : ActionSounds.FallSmall;

                    _audio?.Play(Pick(landing), _player.Position, 0.9f, Wobble());
                    break;
                }
            }
        }

        // The head crossing the surface, both ways, and each breath bubble giving up as the air
        // runs down. The bubble count is the bar's own arithmetic, so the pop keeps time with
        // what the player watches.
        if (_vitals.Submerged != _wasSubmerged)
        {
            _audio?.Play(
                Pick(_vitals.Submerged ? ActionSounds.Submerge : ActionSounds.Surface),
                _viewPosition, 0.6f, Wobble());
            _wasSubmerged = _vitals.Submerged;
        }

        var bubbles = (int)MathF.Ceiling(6f * _vitals.Breath / PlayerVitals.MaxBreath);
        if (_vitals.Submerged && bubbles < _lastBubbleCount)
            _audio?.Play(Pick(ActionSounds.BubblePop), _viewPosition, 0.5f, Wobble());
        _lastBubbleCount = bubbles;

        if (!what.Died) return;

        _player.Teleport(_spawnPoint);
        _vitals.Restore();

        // ⛳ Dying is the one moment a player most wants the last few minutes to have been kept, and
        // the least likely to have thought about it. The copy taken alongside is the step before the
        // death, which is the only thing that makes it recoverable.
        SaveWorld("after dying");
    }

    /// <summary>What <see cref="Equipment.Version"/> read when the armour was last counted.</summary>
    private int _wornVersion = -1;

    /// <summary>
    /// Keeps the vitals' idea of what is being worn current, and holds the shield up.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Counted only when what is worn CHANGES.</b> Five lookups a frame would be nothing, but
    /// the version is already there and using it means the answer cannot silently be stale — which
    /// is the failure a running total would have, since armour changes on a screen, on a load, on a
    /// death and when a piece finally gives out.
    /// </remarks>
    private void StepArmour()
    {
        if (_wornVersion != _equipment.Version)
        {
            _wornVersion = _equipment.Version;
            _vitals.ArmourPoints = Armour.PointsOf(_equipment, _items);
        }

        // ⛔ Only while actually playing. A key held down under an open screen is a key somebody is
        // typing, and a shield raised by the fly camera would be armour on a thing with no body.
        _vitals.ShieldShare = Armour.ShieldInHand(_equipment, _items);
        _vitals.ShieldRaised =
            _walking && _spawned && !_hudScreen.IsOpen && _bench is null
            && _vitals.ShieldShare > 0f
            && (_shotGuard || _keys.Held(_input, GameAction.RaiseShield));
    }

    /// <summary>Which material each worn slot has on, as an index, or −1 for bare.</summary>
    private readonly int[] _wornMaterials = [-1, -1, -1, -1];

    /// <summary>
    /// Reads what is worn into that array, once per change, and hands it to the renderer.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>An index rather than the item.</b> The renderer holds one sheet per material and knows
    /// nothing about pockets or item ids; the whole of what it needs is "which of the five, or none",
    /// and passing anything richer would put the item registry inside a draw call.
    /// </remarks>
    private ReadOnlySpan<int> WornMaterials()
    {
        for (var slot = 0; slot < _wornMaterials.Length; slot++)
        {
            _wornMaterials[slot] = -1;

            var stack = _equipment.At(slot);
            if (stack.IsEmpty) continue;

            // ⛔ The whole name, not a prefix of it. "Does it start with the material" happens to
            // work for these five and would put a stormglass helmet on somebody wearing a
            // stormglass lamp the day anything else is named after a material — and the tell would
            // be a piece of armour appearing on a player carrying no armour at all.
            var name = _items[stack.Item].Name;
            var piece = Armour.Pieces[slot];

            for (var m = 0; m < Armour.Materials.Length; m++)
            {
                if (name != Armour.ItemName(Armour.Materials[m], piece)) continue;
                _wornMaterials[slot] = m;
                break;
            }
        }

        return _wornMaterials;
    }

    /// <summary>Plays one of a material's sounds for one situation, at a point in the world.</summary>
    private void PlaySound(BlockType type, SoundEvent which, Vector3 at, float volume = 1f, float pitch = 1f) =>
        PlaySound(type.Sounds, which, at, volume, pitch);

    /// <summary>
    /// What one thing sounds like, for the things that are not blocks.
    /// </summary>
    /// <remarks>
    /// An item that puts a block down sounds like that block; everything else sounds like timber,
    /// which is what the pickup clips in the library actually are. Keyed on the material rather
    /// than on a block so a stick and an ingot have an answer at all.
    /// </remarks>
    private SoundMaterial MaterialOf(ItemStack stack)
    {
        if (stack.IsEmpty) return SoundMaterial.Wood;

        var block = _items[stack.Item].PlainBlock;
        return block.IsAir ? SoundMaterial.Wood : _registry[block].Sounds;
    }

    /// <param name="pitch">
    /// A multiplier over the small random wobble every sound gets. ⛳ One for everything except the
    /// swing that bounces off a block too hard to break: the user's own idea was to say that with
    /// TONE rather than with a new recording, and a hit dropped most of an octave reads as the rock
    /// refusing the tool. A silent swing would read as the game having stopped.
    /// </param>
    private void PlaySound(
        SoundMaterial material, SoundEvent which, Vector3 at, float volume = 1f, float pitch = 1f)
    {
        if (_audio is null) return;

        var names = MaterialSounds.For(material, which);
        if (names.Count == 0) return;

        _audio.Play(
            names[_soundPick.Next(names.Count)],
            at,
            volume,
            (0.92f + (float)_soundPick.NextDouble() * 0.16f) * pitch);
    }

    /// <summary>
    /// What a chip of debris standing at a point is multiplied by.
    /// </summary>
    /// <remarks>
    /// The same rule the chunk shader uses, minus the directional term: a spinning fleck of stone
    /// has no face to catch the sun with, so it takes the sky's ambient and half its direct light
    /// and leaves it there. What matters is that it is gated on the same baked sky value everything
    /// else is, so a burst in a cave is dark and a burst on a hillside is not.
    /// </remarks>
    private Vector3 ParticleLight(Vector3 at)
    {
        var light = SampleLight(at);
        var daylight = (_skyState.SkyAmbient + _skyState.SunColor * 0.5f) * light.Sky;
        return Vector3.Max(Vector3.Max(daylight, light.Block), NightFloor);
    }

    /// <summary>The box the shape in one cell occupies, for the overlays that wrap it.</summary>
    private (Vector3 Min, Vector3 Max) OutlineOf(int x, int y, int z) =>
        _outlines[_streamer.World.GetBlock(x, y, z).Value];

    /// <summary>Finds what is under the crosshair — a creature if one is nearer, otherwise a block.</summary>
    /// <remarks>
    /// ⛔ <b>An animal in the way takes the aim off the block behind it.</b> Both are asked, and the
    /// nearer wins: without that, a player swinging at a cow standing against a cliff mines the cliff
    /// and hits the cow at the same time, and holding the button digs a tunnel through whatever the
    /// animal happens to be standing in front of. The reach differs too — see <see cref="Combat"/>.
    /// </remarks>
    private void UpdateTarget()
    {
        _target = null;
        _creatureTarget = null;

        if (_bench is not null) return;

        var blockAt = BlockRay.TryCast(
            _streamer.World, _targetable, _camera.Position, _camera.Forward, Reach, out var hit)
            ? hit.Distance
            : float.PositiveInfinity;

        // ⚠ Only as far as the block, and never further than a swing goes. Passing the block's own
        // distance in as the ceiling is what makes "the nearer wins" one comparison rather than two
        // answers to be reconciled afterwards.
        var creature = _herd?.Pick(
            _camera.Position, _camera.Forward, MathF.Min(blockAt, Combat.Reach), out _);

        if (creature is not null) { _creatureTarget = creature; return; }
        if (!float.IsInfinity(blockAt)) _target = hit;
    }

    /// <summary>Removes the targeted block, and empties it first if it was holding anything.</summary>
    private void BreakTarget()
    {
        // A cart under the crosshair comes apart before any block behind it — it is nearer.
        if (_cartSystem.Pick(_streamer.World, _camera.Position, _camera.Forward, Combat.Reach) is { } cart)
        {
            _cartSystem.All.Remove(cart);
            if (_ridingCart == cart) _ridingCart = null;

            var at = new Vector3(cart.X + 0.5f, cart.Y + 0.5f, cart.Z + 0.5f);
            _drops.Drop(new ItemStack(_items.ByName("cart").Id, 1), at);
            return;
        }

        if (_target is not { } hit) return;

        // A furnace comes apart with its contents on the floor rather than taking them with it.
        // The state lives beside the world rather than in the cell, so nothing else would ever
        // clean it up — and a player who mines one mid-smelt has lost both the ore and the coal.
        var centre = new Vector3(hit.X + 0.5f, hit.Y + 0.5f, hit.Z + 0.5f);
        foreach (var spilled in _furnaces.Remove(hit.X, hit.Y, hit.Z)) _drops.Drop(spilled, centre);
        foreach (var spilled in _chests.Remove(hit.X, hit.Y, hit.Z)) _drops.Drop(spilled, centre);

        // The cell keeps the water a wet block stood in — breaking a wet fence is a hole in the
        // water, not a hole in the sea.
        _streamer.EditBlock(
            hit.X, hit.Y, hit.Z,
            _waterlogging.Remains(_streamer.World.GetBlock(hit.X, hit.Y, hit.Z)));
        ShedUnsupported(hit.X, hit.Y, hit.Z);

        // ⛳ Digging is work, and it is most of what a player does. Charged here rather than per
        // frame of swinging, so a block that took eight seconds and one that took one cost the same
        // — the hunger is in the block coming out, not in how stubborn it was.
        _vitals.Spend(PlayerVitals.EffortPerBlockMined);

        // Standing in front of a furnace that is no longer there.
        if (_hudScreen.IsOpen && _station == (hit.X, hit.Y, hit.Z)) CloseScreen();
    }

    /// <summary>
    /// Takes down anything an edit left with nothing to hold on to, and leaves it on the floor.
    /// </summary>
    /// <remarks>
    /// The pass itself is in Core and knows nothing about items; what it hands back is a list of
    /// cells and what was in them, which is exactly the point where the two layers meet. A torch
    /// whose wall is mined has to become a torch on the ground, not vanish — and not stay hanging
    /// in the air either, which is what it did before this existed.
    /// </remarks>
    private void ShedUnsupported(int x, int y, int z)
    {
        _fallen.Clear();
        if (_supports.Shed(_streamer.World, x, y, z, _fallen) == 0) return;

        foreach (var (fx, fy, fz, was) in _fallen)
        {
            // Core wrote the air directly, so the light and mesh work still has to be booked. Going
            // back through EditBlock would re-run the whole pass once per block that came down.
            _streamer.TouchBlock(fx, fy, fz);

            // ⛔ And the FLOW has to be woken, which nothing here ever did: a reed shed at the
            // waterline left a dry hole beside the sea for ever, and a shed wet block wrote its
            // water and nothing asked the neighbours. Same urgency as the swing that caused it.
            _streamer.Fluids?.Touch(fx, fy, fz, urgent: true);

            var at = new Vector3(fx + 0.5f, fy + 0.5f, fz + 0.5f);
            var type = _registry[was];

            var left = _dropTable.Of(was);
            if (!left.IsEmpty) _drops.Drop(left, at);

            _particles.Burst(type, fx, fy, fz);
            PlaySound(type, SoundEvent.Break, at, 0.7f);
        }
    }

    /// <summary>
    /// Puts a block against the face being looked at, unless the player is standing there.
    /// </summary>
    /// <remarks>
    /// The occupancy test is not a nicety. Without it the first thing anyone does is place a block
    /// into their own feet, which leaves them inside solid geometry with the collision resolver
    /// having no free direction to push them out of — the classic way to get stuck in a voxel game.
    /// </remarks>
    /// <summary>
    /// What the right button means, in the order the three answers claim it.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Three things want this button and the order between them is the whole of the rule.</b>
    /// An animal under the crosshair takes it first, because it is what is being aimed at and
    /// shearing is a use. Then the world, which is where a bench opening beats a plank being laid.
    /// Eating is last and only when nothing else wanted it — right-clicking a chest with a chicken
    /// leg in hand opens the chest, which is the behaviour anybody would expect and is not what a
    /// flat "food is eaten on right click" gives you.
    /// </remarks>
    private void UseOrPlace()
    {
        if (_creatureTarget is { } quarry && (FeedCreature(quarry) || HarvestCreature(quarry))) return;

        // ⛔ EATING IS LAST IN FACT NOW, not only in the comment above. It used to run second,
        // gated on "is the aimed block interactive" — the wrong question, because planting and
        // sowing are not the block's own Use: a hungry player right-clicking farmland with a
        // carrot ATE the seed they meant to plant. PlaceOnTarget answers whether the world took
        // the click, and only a click the world had no use for becomes a meal.
        if (PlaceOnTarget()) return;

        EatHeld();
    }

    /// <summary>
    /// Boards the cart under the crosshair, or puts the held one down on a rail. True when it did.
    /// </summary>
    private bool TryCartUse()
    {
        if (_ridingCart is not null) return false;

        // Boarding first: a click on a cart means the cart whatever is in hand.
        if (_cartSystem.Pick(_streamer.World, _camera.Position, _camera.Forward, 5f) is { } cart)
        {
            _ridingCart = cart;
            return true;
        }

        if (_inventory.HeldType is not { Name: "cart" }) return false;
        if (_target is not { } hit) return false;

        // On the rail that was struck — a cart is used on track, never on ground.
        if (!_railTable.IsRail(_streamer.World.GetBlock(hit.X, hit.Y, hit.Z).Value)) return false;

        _cartSystem.Place(hit.X, hit.Y, hit.Z);
        _inventory.SpendHeld();
        PlaySound(
            _registry[_streamer.World.GetBlock(hit.X, hit.Y, hit.Z)], SoundEvent.Place,
            new Vector3(hit.X + 0.5f, hit.Y + 0.5f, hit.Z + 0.5f), 0.8f);
        return true;
    }

    /// <summary>
    /// A bucket filled from a source, or emptied into a space. True when it did something.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Before the target is even consulted</b>, because the crosshair passes through fluid — it
    /// has to, or nothing under water could be mined — so a bucket aimed at a lake would reach the
    /// sand under it. It walks the same line with its own rule about what stops it.
    /// </remarks>
    private bool UseBucket()
    {
        var held = _inventory.HeldType;
        if (held is null) return false;

        var eye = _camera.Position;
        var forward = _camera.Forward;

        if (held.Name == "bucket")
        {
            if (!Buckets.TrySource(
                    _streamer.World, _fluidTable, _targetable, eye, forward, out var from, out var kind))
                return false;

            if (Buckets.Filled(kind) is not { } becomes) return false;

            // A wet block gives up its water and stands dry; a bare source empties to air. For
            // seagrass the dry form IS air — lifting the water lifts the plant, the genre's rule.
            var scooped = _streamer.World.GetBlock(from.X, from.Y, from.Z);
            _streamer.EditBlock(
                from.X, from.Y, from.Z,
                _waterlogging.IsWet(scooped) ? _waterlogging.DryOf(scooped) : BlockId.Air);
            _inventory.SpendHeld();
            _inventory.Add(new ItemStack(_items.ByName(becomes).Id, 1));

            _audio?.Play(
                Pick(kind == FluidKind.Lava ? ActionSounds.BucketFillLava : ActionSounds.BucketFillWater),
                new Vector3(from.X + 0.5f, from.Y + 0.5f, from.Z + 0.5f), 0.8f, Wobble());
            return true;
        }

        var pouring = Buckets.Holds(held.Name);
        if (pouring == FluidKind.None) return false;

        // Into the cell the crosshair would build in, which is the one a player is looking at.
        if (_target is not { } aim) return false;

        // ⛳ Water poured AT a block that can hold it goes INTO it (#96): the struck cell wets,
        // rather than a source landing in the cell in front of it.
        if (pouring == FluidKind.Water
            && _waterlogging.TryWet(_streamer.World.GetBlock(aim.X, aim.Y, aim.Z), out var wetted))
        {
            _streamer.EditBlock(aim.X, aim.Y, aim.Z, wetted);
            _inventory.SpendHeld();
            _inventory.Add(new ItemStack(_items.ByName("bucket").Id, 1));

            _audio?.Play(
                Pick(ActionSounds.BucketEmptyWater),
                new Vector3(aim.X + 0.5f, aim.Y + 0.5f, aim.Z + 0.5f), 0.8f, Wobble());
            return true;
        }

        var (px, py, pz) = aim.Adjacent;
        if (!_registry[_streamer.World.GetBlock(px, py, pz)].Replaceable) return false;

        var source = pouring == FluidKind.Water ? _ids.Water : _ids.Lava;
        _streamer.EditBlock(px, py, pz, source);

        _inventory.SpendHeld();
        _inventory.Add(new ItemStack(_items.ByName("bucket").Id, 1));

        _audio?.Play(
            Pick(pouring == FluidKind.Lava ? ActionSounds.BucketEmptyLava : ActionSounds.BucketEmptyWater),
            new Vector3(px + 0.5f, py + 0.5f, pz + 0.5f), 0.8f, Wobble());
        return true;
    }

    /// <summary>The farming rules, built once against the registry the world is using.</summary>
    private Growth _growth = null!;

    private readonly List<(int X, int Y, int Z)> _growthChunks = new(1024);

    /// <summary>
    /// The growth tick's own stream, seeded off the world.
    /// </summary>
    /// <remarks>
    /// ⚠ Its own rather than shared with the spawner or the herd. Two systems drawing from one
    /// stream means the pattern of one depends on how often the other asked, which is a thing that
    /// cannot be reproduced from a seed — and every other random source in this project is derived
    /// for exactly that reason.
    /// </remarks>
    private readonly Random _growthRandom = new(0x6C726F70);

    /// <summary>
    /// Turns ground over with a hoe: dirt or grass becomes a field.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Only with the sky above it.</b> A field under a block is a field a crop can never be
    /// planted in — the crop needs the cell overhead — so tilling one is a wasted swing that looks
    /// like the hoe not working. Refusing it is the difference between a rule and a mystery.
    /// </remarks>
    private bool UseHoe(RayHit hit)
    {
        if (_inventory.HeldType is not { Tool: ToolClass.Hoe }) return false;

        var here = _streamer.World.GetBlock(hit.X, hit.Y, hit.Z);
        if (here != _ids.Dirt && here != _ids.Grass) return false;

        var above = _streamer.World.GetBlock(hit.X, hit.Y + 1, hit.Z);
        if (!_registry[above].Replaceable) return false;

        _streamer.EditBlock(hit.X, hit.Y, hit.Z, _registry.ByName("farmland").Id);

        // ⚠ A hoe wears out tilling, exactly as a pickaxe wears out digging. Same call the mining
        // path makes, same snap when it goes — without it the one tool in the game with a single
        // purpose would be the one tool that lasts forever.
        if (_inventory.WearHeld())
            _audio?.Play(Pick(ActionSounds.ToolBreaks), _viewPosition, 0.7f, Wobble());

        _audio?.Play(
            Pick(ActionSounds.Till),
            new Vector3(hit.X + 0.5f, hit.Y + 1f, hit.Z + 0.5f), 0.7f, Wobble());

        return true;
    }

    /// <summary>Puts a seed into tilled ground.</summary>
    /// <remarks>
    /// ⚠ Into the cell ABOVE the field, which is where a crop stands. Planting into the farmland
    /// itself would replace the ground the crop needs to grow out of — and the growth rule looks
    /// down for its farmland, so it would never advance.
    /// </remarks>
    private bool PlantSeed(RayHit hit)
    {
        // ⛔ WHAT THE GAME GROWS IS ASKED OF THE TABLE, not written in here. This compared against
        // "seeds" and planted WheatName(0) — a rule about the crop set living in the renderer — so
        // the root crops would have grown, dropped and been edible while being unplantable.
        if (_inventory.HeldType is not { } held) return false;
        if (StarterBlocks.SownBy(held.Name) is not { } seedling) return false;

        var ground = _streamer.World.GetBlock(hit.X, hit.Y, hit.Z);
        if (!_growth.IsFarmland(ground)) return false;

        var above = _streamer.World.GetBlock(hit.X, hit.Y + 1, hit.Z);
        if (!_registry[above].Replaceable) return false;

        var planted = _registry.ByName(seedling).Id;
        _streamer.EditBlock(hit.X, hit.Y + 1, hit.Z, planted);

        _inventory.SpendHeld();
        PlaySound(
            _registry[planted], SoundEvent.Place,
            new Vector3(hit.X + 0.5f, hit.Y + 1.5f, hit.Z + 0.5f), 0.6f);
        return true;
    }

    /// <summary>Puts a berry bush down on plain grass or dirt, the way seeds go into tilled ground.</summary>
    /// <remarks>
    /// ⛳ A gate of its own rather than a Places on the berry, because what a berry becomes depends
    /// on the GROUND — over soil it is a bush, anywhere else it is a meal. The rule of what plants
    /// on what is StarterBlocks.SownOnSoil's, for the sowing rule's reason.
    /// </remarks>
    private bool PlantBush(RayHit hit)
    {
        if (_inventory.HeldType is not { } held) return false;
        if (StarterBlocks.SownOnSoil(held.Name) is not { } sprout) return false;

        var ground = _streamer.World.GetBlock(hit.X, hit.Y, hit.Z);
        if (ground != _ids.Grass && ground != _ids.Dirt) return false;

        var above = _streamer.World.GetBlock(hit.X, hit.Y + 1, hit.Z);
        if (!_registry[above].Replaceable) return false;

        var planted = _registry.ByName(sprout).Id;
        _streamer.EditBlock(hit.X, hit.Y + 1, hit.Z, planted);

        _inventory.SpendHeld();
        PlaySound(
            _registry[planted], SoundEvent.Place,
            new Vector3(hit.X + 0.5f, hit.Y + 1.5f, hit.Z + 0.5f), 0.6f);
        return true;
    }

    /// <summary>Cuts a face into a pumpkin where it stands, turned toward whoever cut it.</summary>
    /// <remarks>
    /// ⛳ An act on the world, not a recipe — the shears do it where the pumpkin is, the way a hoe
    /// tills where the ground is. Which way the face looks is which way the carver was looking,
    /// reversed, because a face is cut into the side being faced.
    /// </remarks>
    private bool CarvePumpkin(RayHit hit)
    {
        if (_inventory.HeldType is not { Name: "shears" }) return false;
        if (_registry[_streamer.World.GetBlock(hit.X, hit.Y, hit.Z)].Name != "pumpkin") return false;

        // The facing whose outward normal points most nearly back at the carver.
        var carved = StarterBlocks.CarvedPumpkins(_registry, lit: false);
        var best = 0;
        var bestDot = float.MaxValue;

        for (var i = 0; i < Placeable.Facings.Length; i++)
        {
            var (nx, _, nz) = Faces.Normals[Placeable.Facings[i]];
            var dot = _camera.Forward.X * nx + _camera.Forward.Z * nz;
            if (dot < bestDot)
            {
                bestDot = dot;
                best = i;
            }
        }

        _streamer.EditBlock(hit.X, hit.Y, hit.Z, carved[best]);

        var here = new Vector3(hit.X + 0.5f, hit.Y + 0.5f, hit.Z + 0.5f);
        _audio?.Play(Pick(ActionSounds.PumpkinCarve), here, 0.8f, Wobble());

        if (_inventory.HeldType is { IsTool: true } && _inventory.WearHeld())
            _audio?.Play(Pick(ActionSounds.ToolBreaks), _viewPosition, 0.7f, Wobble());

        return true;
    }

    /// <summary>Picks the fruit off a ripe bush — the rules are <see cref="Foraging"/>'s, in Core.</summary>
    /// <remarks>
    /// ⚠ What the pockets cannot hold lands on the ground, never in the void — the inventory rule
    /// every other payout follows.
    /// </remarks>
    private void PickBerries(int x, int y, int z)
    {
        var block = _streamer.World.GetBlock(x, y, z);
        if (!Foraging.TryPick(_registry, block, _growthRandom.NextDouble(), out var becomes, out var count))
            return;

        _streamer.EditBlock(x, y, z, becomes);

        var here = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
        var kept = _inventory.Add(new ItemStack(_items.ByName("berries").Id, count));
        if (!kept.IsEmpty) _drops.Drop(kept, here);

        _audio?.Play(Pick(ActionSounds.BerryPick), here, 0.8f, Wobble());
    }

    /// <summary>Hurries a growing crop along.</summary>
    /// <remarks>
    /// ⛳ <b>One stage per pinch and no roll.</b> The reference rolls a chance, which means a player
    /// watching their bone meal do nothing three times in a row has learned that bone meal does
    /// nothing. Here it always moves it on, and the cost is that a full crop takes three.
    /// </remarks>
    private bool UseBonemeal(RayHit hit)
    {
        if (_inventory.HeldType is not { } held || held.Name != "bonemeal") return false;

        var crop = _streamer.World.GetBlock(hit.X, hit.Y, hit.Z);
        if (!_growth.IsCrop(crop) || _growth.IsRipe(crop)) return false;

        _streamer.EditBlock(hit.X, hit.Y, hit.Z, _growth.Next(crop));
        _inventory.SpendHeld();

        _particles.Puff(_registry[crop], new Vector3(hit.X + 0.5f, hit.Y + 0.5f, hit.Z + 0.5f), 8);
        PlaySound(
            _registry[crop], SoundEvent.Place,
            new Vector3(hit.X + 0.5f, hit.Y + 0.5f, hit.Z + 0.5f), 0.6f);
        return true;
    }

    /// <summary>
    /// Mends what is in hand on the anvil, out of the pockets, and wears the anvil doing it.
    /// </summary>
    /// <remarks>
    /// <para>⛳⛳ <b>No screen, and that is a design decision rather than a shortcut.</b> Every other
    /// station in this game has one because it has something to ARRANGE or to choose between — a
    /// grid, a fuel, a list of cuts. An anvil has neither: there is exactly one damaged thing you
    /// care about, it is the one in your hand, and there is exactly one metal that will mend it.
    /// A two-slot window would be two drag gestures to express a fact the game already knows.</para>
    /// <para>⛳ <b>And the material comes out of the pockets rather than a slot</b>, which is what
    /// makes it one gesture: walk up, right-click, done. <c>Repair.Mend</c> spends only what it
    /// needs, so there is nothing to over-pay by accident either.</para>
    /// <para>⚠ A screen is still worth building the day an anvil does more than one thing — renaming,
    /// combining two worn tools, enchantments. It would then have something to choose between, which
    /// is the test.</para>
    /// </remarks>
    /// <summary>
    /// Puts a lit campfire out with a shovel.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>The shovel's first job that is not digging</b>, and it exists because right-click on a
    /// lit campfire now means "cook" — so the toggle that used to put one out had nowhere left to
    /// live. The reference's own answer, and better than a modifier key: a fire is smothered with a
    /// spadeful of earth, which is a thing a player can guess.
    /// ⚠ Asked before <see cref="PlantSeed"/> and the rest for the same reason the hoe is: these are
    /// all things done TO a block with a tool, and the block's own Use would otherwise take the click
    /// first and start cooking with a shovel.
    /// </remarks>
    private bool SmotherFire(RayHit hit)
    {
        if (_inventory.HeldType is not { Tool: ToolClass.Shovel }) return false;

        var struck = _streamer.World.GetBlock(hit.X, hit.Y, hit.Z);
        if (_registry[struck].Use != BlockUse.Campfire) return false;
        if (!_toggle.TryGetValue(struck.Value, out var out_)) return false;

        // ⚠ Whatever was on it comes off rather than burning up with the fire, exactly as a furnace
        // spills when it is mined. A player who smothers a fire mid-cook has lost the time, not the
        // meat.
        var here = new Vector3(hit.X + 0.5f, hit.Y + 0.5f, hit.Z + 0.5f);
        foreach (var spilled in _furnaces.Remove(hit.X, hit.Y, hit.Z)) _drops.Drop(spilled, here);

        _streamer.EditBlock(hit.X, hit.Y, hit.Z, out_);
        _audio?.Play(Pick(ActionSounds.FireOut), here, 0.8f, Wobble());
        PlaySound(_registry[out_], SoundEvent.Place, here, 0.5f);
        return true;
    }

    /// <summary>The composter's stage ids in fill order, resolved once.</summary>
    private BlockId[]? _composterIds;

    /// <summary>
    /// Feeds the bin, or empties it — the rules are <see cref="Composting"/>'s, in Core.
    /// </summary>
    /// <remarks>
    /// ⛳ A helping is consumed whether or not the level rises, which is the table's own gamble;
    /// the two outcomes sound different so a spent helping never reads as a swallowed click. A
    /// click with nothing compostable in hand does nothing at all — the bin is not a screen and
    /// has nothing to say about a pickaxe.
    /// </remarks>
    private void UseComposter(int x, int y, int z)
    {
        _composterIds ??= StarterBlocks.Composters(_registry);

        var here = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
        var stage = Array.IndexOf(_composterIds, _streamer.World.GetBlock(x, y, z));
        if (stage < 0) return;

        if (stage >= StarterBlocks.ComposterStages)
        {
            _streamer.EditBlock(x, y, z, _composterIds[0]);

            var meal = new ItemStack(_items.ByName("bonemeal").Id, Composting.Yield);
            var kept = _inventory.Add(meal);
            if (!kept.IsEmpty) _drops.Drop(kept, here + new Vector3(0f, 0.6f, 0f));

            _audio?.Play(Pick(ActionSounds.ComposterEmpty), here, 0.7f, Wobble());
            return;
        }

        if (_inventory.HeldType is not { } held) return;
        if (Composting.Fill(held.Name, stage, _growthRandom.NextDouble()) is not { } after) return;

        _inventory.SpendHeld();

        if (after != stage)
        {
            _streamer.EditBlock(x, y, z, _composterIds[after]);

            var done = after >= StarterBlocks.ComposterStages;
            _audio?.Play(
                Pick(done ? ActionSounds.ComposterReady : ActionSounds.ComposterRaise),
                here, 0.65f, Wobble());
        }
        else
        {
            _audio?.Play(Pick(ActionSounds.ComposterFill), here, 0.6f, Wobble());
        }
    }

    private void UseAnvil(int x, int y, int z)
    {
        var here = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
        var held = _inventory.Held;

        // ⛔ EVERY REFUSAL SAYS WHY. An anvil that does nothing when clicked is an anvil a player
        // decides is broken — and there are four separate reasons it might, none of which is
        // guessable from the outside.
        if (held.IsEmpty || _items[held.Item].Durability <= 0)
        {
            Notice("nothing in hand to mend", held.IsEmpty ? null : _items[held.Item]);
            return;
        }

        var type = _items[held.Item];

        if (held.Damage <= 0)
        {
            Notice("not damaged", type);
            return;
        }

        var material = Repair.MaterialFor(_items, type);
        if (material.IsNone)
        {
            Notice("cannot be mended", type);
            return;
        }

        var carried = _inventory.CountOf(material);
        if (carried <= 0)
        {
            Notice("none of its metal carried", _items[material]);
            return;
        }

        var mended = Repair.Mend(_items, held, new ItemStack(material, carried), out var spent);
        if (spent <= 0) return;

        _inventory.Take(material, spent);
        _inventory.SetHeld(mended);

        // ⛔ THE ANVIL WEARS, and this is the only place it can: an anvil is a block, and a block has
        // no durability field to spend. Its wear IS its id, so mending steps it along its own three
        // stages and the last one takes it away entirely.
        WearAnvil(x, y, z);

        _audio?.Play(Pick(ActionSounds.AnvilUse), here, 0.9f, Wobble());
        Notice($"mended for {spent} {_items[material].Label}", type);
    }

    /// <summary>Puts a short line on screen with a picture beside it.</summary>
    /// <remarks>
    /// ⚠ Through the toast strip rather than a new mechanism, so it wears the same style, obeys the
    /// same "notices off" setting, and cannot pile up — the cap is the strip's own.
    /// </remarks>
    private void Notice(string what, ItemType? about)
    {
        _toasts.Add(new Toast(
            what, about?.Label ?? "", about?.IconLayer ?? StarterBlocks.LayerAnvilTop, ToastSeconds));

        while (_toasts.Count > MaxToasts) _toasts.RemoveAt(0);
    }

    /// <summary>Steps an anvil one stage nearer gone, and removes it at the end.</summary>
    /// <remarks>
    /// ⚠ <b>By NAME through the stage table, not by adding one to an id.</b> The six anvil blocks are
    /// registered stage-major and two facings apart, so "the next id" is the same anvil turned
    /// sideways — which would read as an anvil that spins when you use it and never wears at all.
    /// </remarks>
    private void WearAnvil(int x, int y, int z)
    {
        var here = _registry[_streamer.World.GetBlock(x, y, z)];

        for (var stage = 0; stage < StarterBlocks.AnvilStages; stage++)
        for (var axis = 0; axis < 2; axis++)
        {
            if (here.Name != StarterBlocks.AnvilName(stage, axis == 0)) continue;

            if (stage + 1 >= StarterBlocks.AnvilStages)
            {
                _streamer.EditBlock(x, y, z, BlockId.Air);
                _particles.Burst(here, x, y, z);
                Notice("the anvil broke", null);
                return;
            }

            _streamer.EditBlock(
                x, y, z, _registry.ByName(StarterBlocks.AnvilName(stage + 1, axis == 0)).Id);
            return;
        }
    }

    private bool PlaceOnTarget()
    {
        // ⛳ The cart, before everything: using one is boarding it, whatever is in hand, and a
        // cart in hand aimed at a rail becomes a cart on it. Both reach past the crosshair the
        // way the bucket does, because neither a cart nor the rail under water is a full block.
        if (TryCartUse()) return true;

        // A bucket is used on the world rather than placed into it, and it reaches things the
        // crosshair cannot — so it is asked before anything that needs a target at all.
        if (UseBucket()) return true;

        if (_target is not { } hit) return false;

        // ⛳ The things done TO the world with something in hand rather than built onto it: a hoe
        // turns ground over, a seed goes into the ground it turned, a berry goes into plain grass,
        // and bone meal hurries what is growing there. All asked before the block's own Use,
        // because the ground has no Use of its own and would otherwise fall through to being
        // built on.
        if (UseHoe(hit) || SmotherFire(hit) || PlantSeed(hit) || PlantBush(hit)
            || CarvePumpkin(hit) || UseBonemeal(hit))
            return true;

        // Using comes before building. A block that does something answers the right button itself,
        // so a bench cannot be buried under the plank a player meant to open it with — and what it
        // does is something the block says rather than something this works out from its name.
        var struck = _registry[_streamer.World.GetBlock(hit.X, hit.Y, hit.Z)];
        switch (struck.Use)
        {
            case BlockUse.Bench:
                OpenBench(hit.X, hit.Y, hit.Z);
                return true;

            case BlockUse.Furnace:
                OpenFurnace(hit.X, hit.Y, hit.Z);
                return true;

            case BlockUse.Chest:
                OpenChest(hit.X, hit.Y, hit.Z);
                return true;

            case BlockUse.Stonecutter:
                OpenStonecutter(hit.X, hit.Y, hit.Z);
                return true;

            case BlockUse.Anvil:
                UseAnvil(hit.X, hit.Y, hit.Z);
                return true;

            case BlockUse.Composter:
                UseComposter(hit.X, hit.Y, hit.Z);
                return true;

            case BlockUse.Berries:
                PickBerries(hit.X, hit.Y, hit.Z);
                return true;

            // ⛳⛳ THE CAMPFIRE OPENS THE FIRE SCREEN NOW, on the user's instruction — "we'll need the
            // same thing for the campfire". It deliberately had none, on the anvil's argument that
            // one thing on the fire and one thing it becomes is nothing to arrange; but that argument
            // was about ARRANGING, and what was actually missing was the BOOK — nowhere in the game
            // said a fire cooks meat. The screen is the furnace's own, which is where the book lives.
            // ⚠ A shovel still puts it out, and SmotherFire is asked before this.
            case BlockUse.Campfire:
                OpenFurnace(hit.X, hit.Y, hit.Z);
                return true;

            case BlockUse.Toggle when _toggle.TryGetValue(struck.Id.Value, out var other):
                _streamer.EditBlock(hit.X, hit.Y, hit.Z, other);

                // ⛳ The first ignition door: a right click lit a cask. The toggle table has no
                // return row, so there is no clicking a fuse back out.
                if (other == _litCask) LightFuse(hit.X, hit.Y, hit.Z, Blastcask.FuseSeconds);

                // A button is momentary: the press books its own spring back, and the signal tick
                // is what honours it.
                if (_signalTable.IsPressedButton(other.Value))
                    _buttonReleases.Add((hit.X, hit.Y, hit.Z, _signalNow + 1.0));

                // Both halves or neither. A door whose top stays shut is not a half-open door, and
                // whichever half was struck has to open the whole thing — which is why the block
                // names its other half rather than the caller working out which end this was.
                if (struck.PartnerFace >= 0)
                {
                    var (px, py, pz) = Faces.Normals[struck.PartnerFace];
                    var half = _registry[_streamer.World.GetBlock(hit.X + px, hit.Y + py, hit.Z + pz)];

                    if (half.PartnerFace == Placeable.Opposite(struck.PartnerFace)
                        && _toggle.TryGetValue(half.Id.Value, out var otherHalf))
                        _streamer.EditBlock(hit.X + px, hit.Y + py, hit.Z + pz, otherHalf);
                }

                // A door swings rather than being put down, and opening and closing are
                // different recordings. Which way it went is what the toggle landed on; the
                // name test covers doors and trapdoors both, and anything else that toggles
                // keeps its material's own voice.
                // ⚠ Read through the DRY name: a wet trapdoor's own name ends "_waterlogged",
                // which would fail the "_open" suffix test and play the shut sound on opening.
                var swung = _registry[_waterlogging.DryOf(other)].Name;
                if (swung.Contains("door", StringComparison.Ordinal))
                {
                    _audio?.Play(
                        Pick(swung.EndsWith("_open", StringComparison.Ordinal)
                            ? ActionSounds.DoorOpen
                            : ActionSounds.DoorClose),
                        new Vector3(hit.X + 0.5f, hit.Y + 0.5f, hit.Z + 0.5f), 0.8f, Wobble());
                }
                else
                {
                    PlaySound(
                        _registry[other], SoundEvent.Place,
                        new Vector3(hit.X + 0.5f, hit.Y + 0.5f, hit.Z + 0.5f), 0.7f);
                }
                return true;
        }

        // A slab laid on a matching slab fills the cell rather than starting a second one above it.
        // Genre-standard, and the reason placement cannot simply test for air: what is already
        // there sometimes decides what happens, and until now it only ever decided whether to give
        // up. Merging into the cell that was struck, not the one beside it.
        // ⚠ The struck slab is compared through its DRY form — a wet slab is still that slab, and
        // filling it squeezes the water out, which is the genre's rule for a cell with no room left.
        if (_inventory.HeldType is { Places: { } holding }
            && _slabMerge.TryGetValue(_streamer.World.GetBlock(hit.X, hit.Y, hit.Z).Value, out var whole)
            && Array.IndexOf(
                holding.Variants,
                _waterlogging.DryOf(_streamer.World.GetBlock(hit.X, hit.Y, hit.Z))) >= 0)
        {
            _streamer.EditBlock(hit.X, hit.Y, hit.Z, whole);
            _inventory.SpendHeld();
            PlaySound(
                _registry[whole], SoundEvent.Place,
                new Vector3(hit.X + 0.5f, hit.Y + 0.5f, hit.Z + 0.5f), 0.85f);
            return true;
        }

        var (x, y, z) = hit.Adjacent;

        // Air builds as it always has; a fluid cell can be built INTO now (#96). The crosshair
        // passes through fluid to whatever is behind it, so the cell this reaches through water is
        // the one in front of the sea floor or the wall — exactly where a dock post goes.
        var occupant = _registry[_streamer.World.GetBlock(x, y, z)];
        if (!_streamer.World.GetBlock(x, y, z).IsAir && !occupant.Replaceable) return false;

        // ⛳ THE MAIN HAND FIRST, THEN THE OTHER ONE — which is what makes a torch in the offhand and
        // a pickaxe in the main hand the loop everybody wants. Asked in that order and never both:
        // a player holding two placeable things means the one they are pointing with.
        // ⚠ Which hand pays is decided HERE and carried to the spend below. Spending the main hand
        // for a block that came out of the other one is the obvious way to get this wrong, and it
        // would read as the offhand being infinite.
        var fromOffhand = false;

        if (_inventory.HeldType is not { Places: { } held })
        {
            // ⚠ A meal in the POINTING hand keeps the click. The offhand fallback exists for the
            // pickaxe-and-torch loop, and reaching past a held steak to place a torch would turn
            // "eat" into "build" by way of the other hand — the click falls through to EatHeld.
            if (_inventory.HeldType is { IsFood: true }) return false;

            var spare = _equipment[EquipSlot.Offhand];
            if (spare.IsEmpty || _items[spare.Item].Places is not { } offhandPlaces) return false;

            held = offhandPlaces;
            fromOffhand = true;
        }

        // Where in the target cell the ray landed, which is what decides a slab's half. Taken from
        // the ray rather than from which face was struck: clicking a block's top lands at the floor
        // of the cell above, its underside at the ceiling of the cell below, and a side wherever
        // the crosshair was, so one number already answers all three.
        var landing = _camera.Position + _camera.Forward * hit.Distance;
        var height = Math.Clamp(landing.Y - y, 0f, 1f);

        if (!held.TryResolve(hit.Face, height, _camera.Forward, out var block)) return false;

        // ⛳ What the cell already holds decides what the placement becomes (#96):
        //  - a WATER SOURCE wets anything that has a wet form, and the water survives inside it;
        //  - flowing water is built over dry — only a source waterlogs, the genre's own line;
        //  - an always-wet plant (seagrass) goes nowhere BUT a source: its water IS the cell's;
        //  - anything with no wet form must fill the cell to displace fluid — a seawall works, a
        //    torch standing in the sea does not.
        if (_registry[block].Waterlogged)
        {
            if (!(occupant.Fluid == FluidKind.Water && occupant.FluidSource)) return false;
        }
        else if (occupant.Fluid != FluidKind.None)
        {
            if (occupant is { Fluid: FluidKind.Water, FluidSource: true }
                && _waterlogging.TryWet(block, out var wet))
                block = wet;
            else if (!_registry[block].Model.IsFullCube)
                return false;
        }

        // What holds it up is the block's own answer, not the item's. A torch put against a wall
        // resolved to a different block from one put on the floor, and that block already says
        // which way it is fixed — asking the item again would be a second copy of the same rule
        // with nothing keeping the two in step.
        var support = _registry[block].SupportFace;
        if (support >= 0)
        {
            var (sx, sy, sz) = Faces.Normals[support];
            var against = _registry[_streamer.World.GetBlock(x + sx, y + sy, z + sz)];

            // Down means anything a foot would rest on, which is what lets a torch stand on a slab.
            // Any other direction means a whole face to fix to, or a torch hangs off a fence post.
            var enough = Placeable.NeedsFirmSupport(support)
                ? against.Solid && against.Model.IsFullCube
                : against.Solid;

            if (!enough) return false;
        }

        // A door is two cells and one thing, so the cell above has to be free before either is
        // written. Checked here rather than in the placement rule because it is a question about
        // the world, and Core's rules are deliberately answerable without one.
        var tall = held.IsTall && _tallUpper.TryGetValue(block.Value, out var upper);
        if (held.IsTall)
        {
            if (!tall) return false;
            if (!_streamer.World.GetBlock(x, y + 1, z).IsAir) return false;
        }

        if (_walking)
        {
            var probe = _streamer.World;
            var before = probe.GetBlock(x, y, z);
            probe.SetBlock(x, y, z, block);
            var blocked = _player.Collides(probe, _player.Position);
            probe.SetBlock(x, y, z, before);
            if (blocked) return false;
        }

        _streamer.EditBlock(x, y, z, block);
        if (tall) _streamer.EditBlock(x, y + 1, z, _tallUpper[block.Value]);

        if (fromOffhand) SpendOffhand();
        else _inventory.SpendHeld();

        PlaySound(_registry[block], SoundEvent.Place, new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), 0.85f);
        return true;
    }

    /// <summary>Takes one off what is in the other hand, emptying the slot when it runs out.</summary>
    private void SpendOffhand()
    {
        var stack = _equipment[EquipSlot.Offhand];
        if (stack.IsEmpty) return;

        var left = stack.Count - 1;

        // ⚠ Taken out and put back rather than mutated in place: ItemStack is a value and Equipment
        // bumps its Version on write, which is what the HUD and the recipe toasts watch. A count
        // changed behind its back is a pocket that empties on screen one action late.
        _equipment.TakeAll(EquipSlot.Offhand);
        if (left > 0) _equipment.Put(EquipSlot.Offhand, stack with { Count = left });
    }

    /// <summary>
    /// Trades what is in the two hands. The rule itself is Core's, so the audit can run it.
    /// </summary>
    private void SwapHands() => _equipment.SwapWithHeld(_inventory);

    /// <summary>
    /// Turns key state into a movement wish, advances the body, and puts the camera in its head.
    /// </summary>
    /// <remarks>
    /// The wish direction comes from where the camera is looking, flattened. Pitch must not feed
    /// into it or looking at your feet walks you into the floor and looking up walks you into the
    /// sky, which is exactly what a fly camera should do and exactly what a body should not.
    /// </remarks>
    private void StepPlayer(float dt)
    {
        // Nothing is simulated until the chunk holding the spawn has arrived. Unloaded space reads
        // as air, so a body stepped before its floor exists is already falling by the time it does.
        if (!_spawned)
        {
            var feet = ChunkPos.FromWorld(
                (int)MathF.Floor(_player.Position.X),
                (int)MathF.Floor(_player.Position.Y),
                (int)MathF.Floor(_player.Position.Z));

            if (!_streamer.World.TryGetChunk(feet, out _))
            {
                _camera.Position = _player.EyePosition;
                return;
            }

            _spawned = true;
        }

        var yaw = float.DegreesToRadians(_camera.Yaw);
        var flat = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        var right = new Vector3(-flat.Z, 0f, flat.X);

        // ⛳ In a fluid, forward follows the whole of where you look — pitch included — so
        // looking down and pressing forward dives. On land it stays flat: walking downhill is
        // not walking into the ground. Strafing stays flat everywhere; nobody side-strokes.
        var forward = _player.InWater || _player.InLava ? _camera.Forward : flat;

        // A screen has the keyboard, so the body stands still — but it is still stepped, because
        // stopping the simulation would leave a player who opened a bench in mid-air hanging there.
        if (_hudScreen.IsOpen)
        {
            _player.Step(_streamer.World, dt, Vector3.Zero, false, false, false);
            _camera.Position = _player.EyePosition;
            return;
        }

        // ⛳ Riding: the cart owns the body's position and the keys mean the cart. Forward pushes
        // along wherever the player is LOOKING projected on the track — so "press toward where I
        // want to go" is the whole of driving — back brakes, and sneak steps off beside the rail.
        if (_ridingCart is { } riding)
        {
            if (_keys.Held(_input, GameAction.Sneak))
            {
                var offAt = _player.Position + new Vector3(0.8f, 0.2f, 0f);
                _ridingCart = null;
                _player.Teleport(offAt);
            }
            else
            {
                var form = _railTable.FormOf(
                    _streamer.World.GetBlock(riding.X, riding.Y, riding.Z).Value);

                if (form != RailForm.None)
                {
                    var along = RailForms.Heading(form, riding.T);
                    var look = Vector3.Dot(new Vector3(flat.X, 0f, flat.Z), along) >= 0f ? 1f : -1f;

                    if (_keys.Held(_input, GameAction.MoveForward))
                        riding.Velocity += look * 4f * dt;
                    if (_keys.Held(_input, GameAction.MoveBack))
                        riding.Velocity -= MathF.Sign(riding.Velocity) * MathF.Min(MathF.Abs(riding.Velocity), 6f * dt);
                }

                _camera.Position = _player.EyePosition;
                return;
            }
        }

        var wish = Vector3.Zero;
        if (_keys.Held(_input, GameAction.MoveForward)) wish += forward;
        if (_keys.Held(_input, GameAction.MoveBack)) wish -= forward;
        if (_keys.Held(_input, GameAction.MoveRight)) wish += right;
        if (_keys.Held(_input, GameAction.MoveLeft)) wish -= right;

        var jump = _keys.Held(_input, GameAction.Jump);
        var sneak = _keys.Held(_input, GameAction.Sneak);
        var sprint = _keys.Held(_input, GameAction.Sprint) && !sneak;

        _player.Step(_streamer.World, dt, wish, jump, sneak, sprint);

        // A body that has fallen out of the world goes back to where it started rather than
        // falling forever. It can only happen where the ground has not streamed in yet.
        if (_player.Position.Y < -8f)
        {
            _player.Teleport(_spawnPoint);
            _spawned = false;
        }

        _camera.Position = _player.EyePosition;
    }

    /// <summary>Books the frame that just ended and stops the run once the flight is over.</summary>
    private void CloseBenchFrame(long now)
    {
        _frameOpen = false;
        if (_bench is null) return;

        var frameMs = (now - _frameStart) * TicksToMs;

        if (_benchWarmingUp)
        {
            WarmUp(frameMs);
            return;
        }

        _bench.Add(new FrameSample(
            FrameMs: frameMs,
            UpdateMs: _updateMs,
            RenderMs: _renderMs,
            DrawnChunks: _drawnChunks,
            LoadedChunks: _meshes.Count,
            Triangles: _drawnTriangles,
            Uploads: _uploadsThisFrame,
            QueueDepth: _streamer.PendingGenerate + _streamer.PendingLight + _streamer.PendingMesh,
            ReadyBacklog: _streamer.ReadyMeshes));

        if (!_bench.Complete) return;

        var context = new FrameBench.Context(
            Seed: _seed.Value,
            ViewRadius: _viewRadius,
            Path: _benchPath!,
            UploadCap: _maxUploadsPerFrame,
            VSync: _options.VSync,
            Workers: _streamer.WorkerCount,
            WarmupFrames: _benchWarmupFrames,
            WarmupSeconds: _benchWarmupMs / 1000.0,
            WarmupPeakMs: _benchWarmupPeakMs,
            WarmupSettled: _benchSettled);

        var result = _bench.Finish(context);
        Console.WriteLine(result.Report);
        _exitCode = result.Passed ? 0 : 1;

        _bench = null;
        _stopRequested = true;
    }

    /// <summary>
    /// Holds at the start of the path until the streaming pipeline goes quiet, then hands over to
    /// measurement.
    /// </summary>
    /// <remarks>
    /// Waiting on the pipeline rather than on a frame count is the difference between measuring a
    /// loaded world and measuring whatever happened to have arrived. On this machine the empty-world
    /// frame rate is high enough that a fixed 120-frame warm-up expired in a fifth of a second, long
    /// before the first chunk was drawn.
    /// </remarks>
    private void WarmUp(double frameMs)
    {
        _benchWarmupFrames++;
        _benchWarmupMs += frameMs;
        if (frameMs > _benchWarmupPeakMs) _benchWarmupPeakMs = frameMs;

        var quiet = _streamer.PendingGenerate == 0
                 && _streamer.PendingLight == 0
                 && _streamer.PendingMesh == 0
                 && _streamer.ReadyMeshes == 0
                 && _meshes.Count > 0;

        _benchQuietFrames = quiet ? _benchQuietFrames + 1 : 0;

        if (_benchWarmupFrames < BenchMinWarmupFrames) return;

        if (_benchQuietFrames >= BenchQuietFramesNeeded)
        {
            _benchSettled = true;
            _benchWarmingUp = false;
        }
        else if (_benchWarmupMs >= BenchWarmupTimeoutMs)
        {
            // Reported as a failed check rather than swallowed: a run that never settled is
            // measuring a different thing and the numbers should not be quoted as if it had.
            _benchWarmingUp = false;
        }
    }

    private void OnRender(double _)
    {
        var renderStart = Stopwatch.GetTimestamp();
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        var size = _window.FramebufferSize;
        var aspect = size.Y > 0 ? size.X / (float)size.Y : 1f;

        // Culling and fog run against where the camera actually is, not where the player is: in
        // third person those are four blocks apart, and using the eye would cull the chunk the
        // camera has backed into.
        var view = FlyCamera.View(_viewPosition, _viewForward);
        var projection = _camera.Projection(aspect);
        var viewProj = view * projection;
        var frustum = Frustum.FromViewProjection(viewProj);

        // Before anything else, with no depth. Everything in the frame is nearer than the sky.
        _sky.Draw(_skyState, _viewForward, _camera.FovDegrees, aspect);

        // Before the chunk pass binds the array and draws with it. Ticked from the world's own clock
        // rather than an accumulator, so a stall leaves the water where the clock says rather than
        // where the frames got to.
        _animatedTextures.Update(_elapsed);

        ReadHeldGlow();

        _chunkShader.Use();
        _chunkShader.SetMatrix4("uViewProj", viewProj);
        _chunkShader.SetVec3("uCameraPos", _viewPosition);
        _chunkShader.SetVec3("uFogColor", _skyState.Horizon);
        _chunkShader.SetVec3("uSunDir", _skyState.SunDirection);
        _chunkShader.SetVec3("uSunColor", _skyState.SunColor);
        _chunkShader.SetVec3("uSkyAmbient", _skyState.SkyAmbient);
        _chunkShader.SetVec3("uGroundAmbient", _skyState.GroundAmbient);
        _chunkShader.SetVec3("uNightFloor", NightFloor);
        _chunkShader.SetInt("uBlocks", 0);
        _blockTextures.Bind();
        _chunkShader.SetFloat("uFogStart", _fogStart);
        _chunkShader.SetFloat("uFogEnd", _fogEnd);
        _chunkShader.SetFloat("uTime", FlickerClock);
        _chunkShader.SetVec3("uHeldPos", _spawned ? HeldGlowPos : Vector3.Zero);
        _chunkShader.SetVec3("uHeldLight", _heldGlow);
        _chunkShader.SetFloat("uHeldRange", HeldGlowRange);

        _chunkShader.SetFloat("uAlpha", 1f);

        var drawn = 0;
        var triangles = 0;
        _wetChunks.Clear();

        foreach (var mesh in _meshes.Values)
        {
            if (!_frustumCulling) { }
            else if (!frustum.IntersectsBox(mesh.BoundsMin, mesh.BoundsMax)) continue;

            _chunkShader.SetVec3("uChunkOrigin", mesh.Origin);
            _chunkShader.SetVec3Array("uTint", mesh.TintPalette);
            mesh.Draw();
            drawn++;
            triangles += mesh.IndexCount / 3;

            if (mesh.HasTranslucent) _wetChunks.Add(mesh);
        }

        DrawWater();

        _drawnChunks = drawn;
        _drawnTriangles = triangles;

        // Both overlays wrap the shape rather than the cell. A full-cube outline around a slab or a
        // torch is the tell that the game still thinks in cubes, and it is on screen constantly.
        if (_target is { } hit && !_wireframe)
        {
            var (min, max) = OutlineOf(hit.X, hit.Y, hit.Z);
            _outline.Draw(viewProj, new Vector3(hit.X, hit.Y, hit.Z), min, max);
        }

        // Keyed off the mining state's own cell rather than the crosshair's, so cracking is never
        // drawn on a block that is not the one being worked loose.
        if (_mining.Target is { } cell && _mining.Stage >= 0 && !_wireframe)
        {
            var (min, max) = OutlineOf(cell.X, cell.Y, cell.Z);
            _cracks.Draw(viewProj, new Vector3(cell.X, cell.Y, cell.Z), min, max, _mining.Stage);
        }

        // Debris and dropped stacks while depth still describes the world, so a chip is hidden by
        // the hill it flew behind. The texture array is re-bound because the cracking pass owns
        // unit zero by now.
        _blockTextures.Bind();
        _itemRenderer.Draw(_drops, _registry, _items, viewProj, at => ParticleLight(at));
        DrawCookingFood(viewProj);
        _particleRenderer.Draw(
            _particles, viewProj, _viewPosition, _viewForward,
            at => ParticleLight(at), _skyState.Horizon, _fogStart, _fogEnd);

        // Blended, and after the world has written its depth so terrain occludes them. Before the
        // player, not after: drawing the held arm clears the depth buffer so it can sit in front of
        // everything, and anything depth-tested after that clear is testing against an empty buffer
        // — clouds drawn there pass in front of the hillside they are behind.
        _clouds.Draw(
            viewProj, frustum, _viewPosition,
            CloudTint * MathF.Max(_skyState.SunColor.X + _skyState.SkyAmbient.X, 0.18f),
            _skyState.Horizon, _fogStart, _fogEnd, (float)_elapsed);

        DrawPlayer(viewProj, projection, view);

        // Over everything, in screen space. The benchmark flies itself and wants a clean picture.
        if (_bench is null)
        {
            _furnaces.TryGet(_station.X, _station.Y, _station.Z, out var open);
            _hudScreen.Burning = _hudScreen.Kind == HudScreenKind.Furnace ? open : null;

            _hud.Draw(
                _blockTextures, _items, _inventory, _equipment, _vitals,
                _hudScreen, _layout, _toasts, size.X, size.Y);
        }

        if (_options.UiCheck) RunUiCheck(size);
        if (_options.ShotPath is not null) RunShots(size);

        _renderMs = (Stopwatch.GetTimestamp() - renderStart) * TicksToMs;

        // ⛳ Here rather than at the end of loading, because "the world is on screen" is the moment
        // a player is actually waiting for and it is later than the moment the loading code stops.
        // Reports once and then costs a bool.
        _startup.Report("first frame drawn");
    }

    /// <summary>How many frames the shot run has drawn.</summary>
    private int _shotFrame;

    /// <summary>
    /// Holds a thing, in a named view, and writes the frame to a file.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>The instrument this whole area was missing.</b> A tile could be looked at with
    /// <c>--icon-sheet</c>; the same tile <em>in a fist</em> could not, and that is where a
    /// projection, a swing, a grip and two entirely different arm poses are added on top of it. So
    /// the held pickaxe shipped as a cube wearing its picture on six faces — visibly two pickaxes,
    /// pointed at the player — and the only eye that ever saw it was the user's, in the game.</para>
    /// <para>Everything about it is the real path: the real world, the real camera, the real grip
    /// and the real swing clock. It sets what is held and which way the camera sits, and reads the
    /// front buffer. Nothing here is a special-case renderer, because a special-case renderer is a
    /// thing that can be right while the game is wrong.</para>
    /// </remarks>
    /// <param name="Creature">
    /// Stand the body beside the nearest animal and look at it. ⚠ Without this the picture is of
    /// wherever the shot placement happened to leave the camera, and an animal twenty blocks behind
    /// it is a picture of an empty field that looks exactly like a renderer drawing nothing.
    /// </param>
    /// <param name="Wears">
    /// A material to put a whole set of on, and a shield in the other hand. ⛔ The plates are
    /// geometry hung off the body's joints wearing sheets painted in code, and every part of that is
    /// checkable headlessly while none of it says whether there is a suit of armour on the screen —
    /// which is the same argument the creatures needed a picture for.
    /// </param>
    private readonly record struct Shot(
        int Frame, string Name, string Item, ViewMode View, bool Strike, ShotScreen Screen = ShotScreen.None,
        bool Creature = false, string Wears = "", string Offhand = "", bool Guard = false);

    /// <summary>Which interface, if any, is up when the picture is taken.</summary>
    private enum ShotScreen
    {
        None,
        Player,
        Book,
        Bench,
        Furnace,
        Chest,
    }

    /// <summary>
    /// What gets photographed. Order matters only in that a strike needs frames to travel through.
    /// </summary>
    /// <remarks>
    /// A pickaxe for the shape everybody knows, a sword for a long straight thing whose angle in the
    /// fist is the whole question, a torch because it has to point up and away rather than lie along
    /// a leg, and a block because it is the other mesh entirely.
    /// </remarks>
    private static readonly Shot[] Shots =
    [
        new(70, "1-first-pickaxe", "stone_pickaxe", ViewMode.First, false),
        new(78, "2-first-pickaxe-swing", "stone_pickaxe", ViewMode.First, true),
        new(86, "3-first-sword", "stone_sword", ViewMode.First, false),
        new(94, "4-first-torch", "torch", ViewMode.First, false),
        new(102, "5-first-block", "stone", ViewMode.First, false),
        new(112, "6-third-pickaxe", "stone_pickaxe", ViewMode.ThirdBehind, false),
        new(120, "7-third-pickaxe-swing", "stone_pickaxe", ViewMode.ThirdBehind, true),
        new(128, "8-third-torch", "torch", ViewMode.ThirdBehind, false),
        new(136, "9-third-block", "stone", ViewMode.ThirdBehind, false),
        new(144, "10-facing-pickaxe", "stone_pickaxe", ViewMode.ThirdFacing, false),
        new(152, "11-facing-sword", "stone_sword", ViewMode.ThirdFacing, false),

        // ⛳ And the screens, for the same reason the hand is here. Every square, every well and
        // every icon in this game is drawn in code against a grid measured off a pack's own sheet,
        // and the only way to look at any of it was to start the game and open it.
        new(164, "12-player", "stone", ViewMode.First, false, ShotScreen.Player),

        // ⛳ AND THE SAME SCREEN WEARING SOMETHING, which is the only frame that shows the figure
        // doing its job. The bare one above is a doll in a skin and looks identical whether the
        // player is in rags or a full set — which was the complaint. This one has plate on all four
        // slots, a sword in the hand and a shield in the other, so every path the window has is on
        // screen at once.
        // ⛔ ITS OWN FRAME. This first went in at 176, which 13-book already owns — two shots on one
        // frame means the second one's setup runs before the picture is taken, so this was
        // photographed after the book shot had cleared the armour off. The doll came out bare, which
        // looks exactly like the feature not working.
        new(170, "12b-player-armoured", "iron_sword", ViewMode.First, false, ShotScreen.Player,
            Wears: "iron"),
        new(176, "13-book", "stone", ViewMode.First, false, ShotScreen.Book),
        new(188, "14-bench", "stone", ViewMode.First, false, ShotScreen.Bench),
        new(200, "15-furnace", "stone", ViewMode.First, false, ShotScreen.Furnace),
        new(212, "16-chest", "stone", ViewMode.First, false, ShotScreen.Chest),

        // ⛳ And an animal, which is the same argument one more time: a creature is a skeleton read
        // off somebody else's disk, wearing a net off somebody else's pack, posed by a matrix chain
        // and a bind pose. Every part of that is checkable headlessly and none of it says whether
        // there is a cow on the screen.
        new(260, "17-creature", "stone", ViewMode.First, false, ShotScreen.None, Creature: true),
        new(300, "18-creature-third", "stone", ViewMode.ThirdBehind, false, ShotScreen.None, Creature: true),

        // ⛳ A suit of armour, from behind and from the front, in two materials. Two because the
        // sheets are painted per material and one picture proves one material — and leather over an
        // iron set is also the pair that shows whether the leggings really do sit under the
        // chestplate rather than through it.
        new(320, "19-armour-iron", "iron_sword", ViewMode.ThirdBehind, false, Wears: "iron"),
        new(332, "20-armour-iron-facing", "iron_sword", ViewMode.ThirdFacing, false, Wears: "iron"),
        new(344, "21-armour-leather", "stone_pickaxe", ViewMode.ThirdFacing, false, Wears: "leather"),

        // ⛳ THE OTHER HAND, which had no picture at all and is the reason it went unlooked-at for a
        // session. A torch first, because a lowered offhand is the ordinary case and a torch is the
        // thing whose angle in a fist is most obviously wrong when it is wrong. Then the guard, from
        // the front and from behind: a raised shield is the whole point of the feature and the two
        // views answer different questions — whether the board faces the way the player does, and
        // whether the arm has actually come across the chest rather than swung out beside it.
        new(356, "22-offhand-torch", "iron_sword", ViewMode.ThirdFacing, false, Offhand: "torch"),
        new(368, "23-guard-facing", "iron_sword", ViewMode.ThirdFacing, false, Wears: "iron", Guard: true),
        new(380, "24-guard-behind", "iron_sword", ViewMode.ThirdBehind, false, Wears: "iron", Guard: true),
    ];

    /// <summary>
    /// A pocketful of things worth photographing: rock, worked rock, a shape, a tool, a light.
    /// </summary>
    /// <remarks>
    /// ⚠ Deliberately not one item repeated. The question a picture of a slot answers is whether a
    /// block, a slab cut from the same block, a tool and a torch are told apart in a square the size
    /// of a fingernail — and a screen full of identical stone answers it for none of them.
    /// </remarks>
    private static readonly string[] ScreenShow =
    [
        "stone", "stone_slab", "stone_bricks", "driftoak_log", "driftoak_planks",
        "sand", "gravel", "clay", "glass", "bricks",
        "coal", "iron_ingot", "gold_ingot", "stick", "torch",
        "stone_pickaxe", "iron_axe", "gold_shovel", "stormglass_sword", "bench",
        "furnace", "chest", "ladder", "door", "lantern",
    ];

    /// <summary>
    /// Frames of streaming allowed before the body is put somewhere worth photographing.
    /// </summary>
    /// <remarks>
    /// Half a second. The world arrives on worker threads, so "the spawn chunk is here" is a long
    /// way from "the ground for forty blocks around is here and lit" — and the placement search
    /// reads the loaded world on purpose, because what it finds has to be what will be drawn.
    /// </remarks>
    private const int PlaceFrame = 30;

    /// <summary>Frames before a shot that its item is put in hand and its view set.</summary>
    /// <remarks>
    /// Four, so the body has settled and — for the ones that want it — a strike begun on the setup
    /// frame is a third of the way through its arc by the time the picture is taken. That is the
    /// part of a swing worth looking at: the top of the arc is where a grip that has come loose has
    /// travelled furthest from the fist.
    /// </remarks>
    private const int ShotLead = 4;

    /// <summary>Frames between walking the body to an animal and photographing it.</summary>
    /// <remarks>
    /// Half a second, against the four frames every other shot needs, because this one moves the
    /// body rather than what is in its hand. The streamer follows the player, so the ground at the
    /// new spot has to be generated, meshed and uploaded before there is anything under the animal.
    /// </remarks>
    private const int CreatureLead = 30;

    /// <summary>
    /// Puts a whole set of one material on, and a shield up, or takes everything off.
    /// </summary>
    /// <remarks>
    /// ⚠ Cleared first, every time, rather than only when a material is named. A set left on from
    /// the shot before would put iron boots in the picture of the leather one, and the two would be
    /// three frames apart in the same folder.
    /// </remarks>
    /// <summary>
    /// Holds the guard up for a picture, since the real one answers to a key nobody is pressing.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The shot used to set <c>ShieldRaised</c> directly and it was overwritten on the next
    /// frame</b> — the flag is recomputed every tick from the key, so a shot that assigns it is
    /// assigning to something with a live owner. Which is also why the shield the armour shots have
    /// carried since they were written has only ever been photographed hanging at the side.
    /// </remarks>
    private bool _shotGuard;

    private void WearForShot(string material, string offhand, bool guard)
    {
        _equipment.Clear();
        _shotGuard = guard;

        foreach (var piece in Armour.Pieces)
        {
            if (material.Length == 0) break;

            var name = $"{material}_{piece.Name}";
            if (_items.TryByName(name, out var type))
                _equipment.Restore(piece.Slot, new ItemStack(type.Id, 1));
        }

        // What goes in the other hand: whatever the shot named, or a shield by default so a suit of
        // armour is photographed with the thing it is worn beside.
        var other = offhand.Length > 0 ? offhand : material.Length > 0 ? Armour.ShieldName : "";

        if (other.Length > 0 && _items.TryByName(other, out var held))
            _equipment.Restore(EquipSlot.Offhand, new ItemStack(held.Id, 1));
    }

    private void RunShots(Vector2D<int> size)
    {
        _shotFrame++;

        // Nothing until the body is standing on real ground: third person draws no model before
        // that, so an early picture would be of an empty world and would look like a fault.
        if (!_spawned) { _shotFrame = 0; return; }

        // ⛔ AND NOTHING UNTIL IT IS STANDING SOMEWHERE THE HAND CAN BE SEEN. The first run of this
        // came back with two black bands across every frame — a tree the spawn point happened to be
        // inside — and the second with the hearts flashing, because the same spawn was underwater.
        // A picture of a held tool taken against the inside of a trunk answers nothing, and worse,
        // it answers it convincingly.
        //
        // ⚠ AND NOT ON THE FIRST FRAME EITHER. Placing as soon as the spawn chunk lands read a world
        // that was still arriving: unloaded space answers "air" to every question, so the search
        // found a column with nothing above it and stood in a hole, and the light there was still
        // zero — which came out as one shot of a pickaxe two thirds as bright as the next one, in
        // the same run, from the same code. A held thing that is dark in one picture and lit in the
        // next is exactly the sort of thing that gets read as a shading fault.
        if (_shotFrame == PlaceFrame) StandInTheOpen();
        if (_shotFrame <= PlaceFrame) return;

        foreach (var shot in Shots)
        {
            if (_shotFrame == shot.Frame - ShotLead)
            {
                _inventory.Clear();

                if (shot.Screen == ShotScreen.None)
                {
                    _inventory.Add(new ItemStack(_items.ByName(shot.Item).Id, 1));
                }
                else
                {
                    foreach (var name in ScreenShow)
                    {
                        if (_items.TryByName(name, out var type))
                            _inventory.Add(new ItemStack(type.Id, type.MaxStack > 1 ? 17 : 1));
                    }
                }

                _inventory.Select(0);
                _view = shot.View;
                if (shot.Strike) _animator.Strike();
                OpenForShot(shot.Screen);
                WearForShot(shot.Wears, shot.Offhand, shot.Guard);
            }

            // ⛔ A LONG way ahead of the picture, and four frames is nowhere near enough. Walking the
            // body somewhere else moves the streamer's centre with it, and until the chunks round
            // the new spot have been generated, meshed and uploaded the world there is not drawn at
            // all — which came out as a photograph of animals standing in an empty blue sky with one
            // stone block in the corner. The creatures were right; the ground had not arrived.
            if (shot.Creature && _shotFrame == shot.Frame - CreatureLead) StandBesideACreature();

            if (_shotFrame == shot.Frame) WriteShot(size, shot.Name);
        }

        if (_shotFrame >= Shots[^1].Frame + ShotLead)
        {
            Console.WriteLine($"shots       {Shots.Length} written to {_options.ShotPath}");
            _window.Close();
        }
    }

    /// <summary>
    /// Stands the body a few paces from the nearest animal, looking at it.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Placed to the animal's south and turned to face it</b> rather than dropped on top of it:
    /// a camera inside a cow photographs the inside of a cow, which is black and reads as nothing
    /// being drawn at all. Three blocks back is far enough to see a whole one and near enough that
    /// it fills the frame. The line is printed whether or not there was an animal, because "no cow
    /// in the picture" and "no cow in the world" are the same photograph.
    /// </remarks>
    private void StandBesideACreature()
    {
        if (_herd is null || _herd.Count == 0)
        {
            Console.WriteLine("shots       no creature to stand beside — the picture is of an empty field");
            return;
        }

        var creature = _herd.All[0];
        var stand = creature.Position + new Vector3(0f, 0f, 3f);

        _player.Teleport(stand);
        _camera.Position = _player.EyePosition;
        _camera.Yaw = -90f;      // Facing −z, which is where the animal is from here.
        _camera.Pitch = -10f;
        _animator.Reset(_camera.Yaw);

        Console.WriteLine(
            $"shots       standing at {stand.X:F1} {stand.Y:F1} {stand.Z:F1}, "
            + $"looking at a {creature.Kind} 3 blocks away");
    }

    /// <summary>Puts up the screen a shot wants, or takes down whatever is up.</summary>
    /// <remarks>
    /// Through the same doors a player uses — <see cref="OpenPlayer"/>, <see cref="OpenBench"/> and
    /// the rest — rather than by setting the kind directly. A screen opened by hand here would be a
    /// screen in a state no player can reach, and the picture would be of that.
    /// </remarks>
    private void OpenForShot(ShotScreen screen)
    {
        if (screen == ShotScreen.None)
        {
            if (_hudScreen.IsOpen) CloseScreen();
            return;
        }

        // A block of its own for the stations to be opened against, put down and taken back after,
        // because a furnace screen with no furnace behind it has nothing to draw a flame from.
        var at = ((int)MathF.Floor(_player.Position.X), (int)MathF.Floor(_player.Position.Y) + 2,
                  (int)MathF.Floor(_player.Position.Z) + 1);

        switch (screen)
        {
            case ShotScreen.Player:
                OpenPlayer(PlayerTab.Items, atBench: false, default);
                break;

            case ShotScreen.Book:
                OpenPlayer(PlayerTab.Items, atBench: false, default);
                _hudScreen.BookOut = true;
                RefreshScreen();
                break;

            case ShotScreen.Bench:
                OpenBench(at.Item1, at.Item2, at.Item3);
                break;

            case ShotScreen.Furnace:
                _furnaces.Open(at.Item1, at.Item2, at.Item3);
                OpenFurnace(at.Item1, at.Item2, at.Item3);
                break;

            case ShotScreen.Chest:
                _chests.Open(at.Item1, at.Item2, at.Item3);
                OpenChest(at.Item1, at.Item2, at.Item3);
                break;
        }
    }

    /// <summary>
    /// Moves the body to somewhere with sky above it and nothing right in front of it.
    /// </summary>
    /// <remarks>
    /// Asks the world that is loaded rather than the generator that made it, so what it finds is
    /// what will actually be drawn. A column qualifies when its top is above the waterline, there
    /// is head-room over it, and the eight blocks the camera will be looking through are empty —
    /// the last of those is the whole point, because a clear column at the foot of a cliff is still
    /// a picture of a cliff.
    /// </remarks>
    private void StandInTheOpen()
    {
        var world = _streamer.World;
        var yaw = float.DegreesToRadians(_camera.Yaw);
        var ahead = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));

        for (var radius = 0; radius <= 40; radius += 4)
        for (var step = 0; step < (radius == 0 ? 1 : 12); step++)
        {
            var angle = step * MathF.Tau / 12f;
            var x = (int)MathF.Round(_player.Position.X + MathF.Cos(angle) * radius);
            var z = (int)MathF.Round(_player.Position.Z + MathF.Sin(angle) * radius);

            var top = -1;
            for (var y = 120; y > TerrainGenerator.SeaLevel; y--)
            {
                if (world.GetBlock(x, y, z) == BlockId.Air) continue;
                top = y;
                break;
            }

            if (top < 0) continue;

            // ⛔ And it has to be LIT. Sky light is what says "this is outdoors and the world here
            // has finished arriving" in one question — a cave roof, a spot under a tree and a chunk
            // that is still streaming all answer zero, and all three make a dark photograph of a
            // tool that is not dark.
            if (LightValue.Sky(world.GetLight(x, top + 1, z)) < LightValue.Max) continue;

            // Head-room, and a clear line of sight for the length of the frame.
            var clear = true;
            for (var up = 1; up <= 3 && clear; up++)
                clear = world.GetBlock(x, top + up, z) == BlockId.Air;

            for (var out_ = 1; out_ <= 8 && clear; out_++)
            {
                var at = new Vector3(x, top + 2, z) + ahead * out_;
                clear = world.GetBlock((int)MathF.Round(at.X), (int)at.Y, (int)MathF.Round(at.Z)) == BlockId.Air;
            }

            if (!clear) continue;

            _player.Teleport(new Vector3(x + 0.5f, top + 1f, z + 0.5f));
            _camera.Position = _player.EyePosition;
            _camera.Pitch = 0f;
            _animator.Reset(_camera.Yaw);
            _vitals.Restore();
            return;
        }
    }

    /// <summary>Campfires with something on them that were drawn on the last frame.</summary>
    public int CookingDrawn { get; private set; }

    /// <summary>
    /// How high above a campfire's cell its dinner sits.
    /// </summary>
    /// <remarks>
    /// ⚠ Measured against <see cref="BlockModel.Campfire"/>, not guessed: the logs stand 8 of 16 tall
    /// and the flame planes run from 4 to 14, so anything under about a half is inside the timber and
    /// anything over about 0.8 is floating over the fire rather than cooking on it.
    /// </remarks>
    private const float FoodOnFire = 0.58f;

    /// <summary>
    /// Draws whatever is cooking on a campfire, on the campfire.
    /// </summary>
    /// <remarks>
    /// <para>⛳⛳ <b>The missing half of campfire cooking, and the reason it read as not working at
    /// all.</b> Putting meat on a fire changed nothing whatever on the screen — the campfire has no
    /// screen by design, so the only feedback was a toast that scrolled away — and a player who walks
    /// up, right-clicks, sees the world unchanged and walks off has been told, as far as they can
    /// tell, that the fire is scenery. Everything underneath was already working: it cooks in twenty
    /// seconds and hands back a cooked steak. Nothing said so.</para>
    /// <para>⛳ <b>The raw thing turns into the cooked thing in front of you</b>, because
    /// <see cref="Furnace.Output"/> is drawn the moment it exists. That is the whole loop made
    /// visible: put it on, watch it, take it off.</para>
    /// <para>⚠ <b>Only a campfire</b>. A furnace and a smoker are closed boxes with a screen that
    /// already shows their contents and a gauge for the burn; a thing floating out of a furnace's
    /// chimney would be a bug, not feedback.</para>
    /// </remarks>
    private void DrawCookingFood(Matrix4x4 viewProj)
    {
        CookingDrawn = 0;

        foreach (var (at, fire) in _furnaces.All)
        {
            // ⛔ Asked of the BLOCK, exactly as the cooking tick is, rather than of anything stored
            // beside the fire. A campfire that has been smothered is an ordinary campfire block, and
            // its dinner must stop hanging in the air the instant the flame goes out.
            var here = _streamer.World.GetBlock(at.X, at.Y, at.Z);
            if (_smelterKind[here.Value] != FurnaceKind.Campfire) continue;

            // What is done, or failing that what is still cooking. Nothing at all is the common case.
            var showing = !fire.Output.IsEmpty ? fire.Output : fire.Input;
            if (showing.IsEmpty) continue;

            var type = _items[showing.Item];
            var middle = new Vector3(at.X + 0.5f, at.Y + FoodOnFire, at.Z + 0.5f);

            // ⚠ Lying down, not standing up. Every item in this game is drawn upright — in a fist, on
            // the floor, in a slot — and a steak standing on its edge in a fire reads as a signpost.
            // A quarter turn about x lays a flat sprite onto the flame like something on a grill.
            var scale = 0.22f * (type.DrawsAsBlock ? 1f : 1.55f);
            var model = Matrix4x4.CreateScale(scale)
                      * Matrix4x4.CreateRotationX(MathF.PI * 0.5f)
                      * Matrix4x4.CreateRotationY((float)_elapsed * 0.9f)
                      * Matrix4x4.CreateTranslation(middle);

            _itemRenderer.DrawInHand(viewProj, model, type, _registry, ParticleLight(middle));
            CookingDrawn++;
        }
    }

    /// <summary>Reads the frame off the front buffer and writes it, right way up.</summary>
    private unsafe void WriteShot(Vector2D<int> size, string name)
    {
        var width = size.X;
        var height = size.Y;
        var raw = new byte[width * height * 4];

        fixed (byte* p = raw)
            _gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, p);

        // ⚠ GL counts rows up from the bottom and a PNG counts them down from the top. Skip this
        // and every picture the instrument produces is upside down, which is a fine way to spend an
        // afternoon deciding a grip is inverted.
        var flipped = new byte[raw.Length];
        var stride = width * 4;
        for (var row = 0; row < height; row++)
            Array.Copy(raw, (height - 1 - row) * stride, flipped, row * stride, stride);

        var folder = _options.ShotPath!;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{name}.png");
        File.WriteAllBytes(path, Png.Encode(new Image(width, height, flipped)));

        // ⚠ The light goes in the line, because the first run of this produced one picture of a
        // pickaxe visibly darker than the next picture of a sword, from the same code, and there is
        // no way to tell "the grip is showing me its shaded side" from "this frame was lit less"
        // by looking. A number settles it; a picture cannot.
        var lit = HandLight(SampleLight(_camera.Position));
        Console.WriteLine(
            $"shot        {name,-24} {width}x{height}   light {lit.X:F2} {lit.Y:F2} {lit.Z:F2}"
            + $"   {_playerRenderer?.PlatesDrawn ?? 0} plates");
    }

    /// <summary>How many frames the check has drawn, for its own timing.</summary>
    private int _uiCheckFrame;

    /// <summary>Bubbles the breath bar put on screen when the ui-check took its air away.</summary>
    private int _breathBubbles = -1;

    private Vector2 _breathAt;

    /// <summary>
    /// The whole frame at half a lungful, then twice at a full one.
    /// </summary>
    /// <remarks>
    /// <para>⛔⛔ <b>THE WHOLE FRAME, deliberately, and NOT the rectangle the bar says it used.</b>
    /// <see cref="HudRenderer.LastBubbles"/> is a counter the drawing method increments itself: it
    /// proves quads were appended and says nothing whatever about a pixel arriving. Reading a patch
    /// at <see cref="_breathAt"/> would be barely better — it would inherit the very layout
    /// arithmetic under suspicion, and a bar drawn off the bottom of the screen would come back
    /// "nothing there" indistinguishably from a bar that was never drawn at all.</para>
    /// <para>⛳ So the question asked is the widest one there is: <b>did ANY pixel anywhere on the
    /// screen change when the air was taken away.</b> It needs no constant, cannot be fooled by a
    /// wrong position, and the bounding box of whatever did change says where the bar actually
    /// went — which is the thing three sessions of reading the code could not settle.</para>
    /// <para>⚠ <b>Three reads, because two cannot tell a bubble from a cloud.</b> The overlay is
    /// drawn over a live world with clouds drifting and a sun moving, so <em>some</em> pixels differ
    /// between any two frames. The second pair is taken with the air already full and is the noise
    /// floor the first pair has to beat.</para>
    /// </remarks>
    private byte[]? _frameHalfAir;
    private byte[]? _frameFullAir;
    private byte[]? _frameFullAgain;

    /// <summary>Pixels that changed when the air went, and how many change anyway.</summary>
    private int _breathPixels = -1;
    private int _breathNoise = -1;

    /// <summary>Where on the screen those pixels were, in framebuffer coordinates.</summary>
    private (int X0, int Y0, int X1, int Y1) _breathBox = (-1, -1, -1, -1);

    /// <summary>The water layer as it was ten frames ago, for the check that it moves.</summary>
    private byte[]? _waterBefore;

    /// <summary>True once the water on the card has been seen to differ from itself.</summary>
    private bool _waterMoved;

    /// <summary>The whole back buffer as it stands, right now, in the card's own row order.</summary>
    private unsafe byte[] ReadFrame(Vector2D<int> size)
    {
        var raw = new byte[size.X * size.Y * 4];
        fixed (byte* p = raw)
            _gl.ReadPixels(0, 0, (uint)size.X, (uint)size.Y, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        return raw;
    }

    /// <summary>
    /// Wipes the world off the frame, draws the overlay alone on flat black, and reads that.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>The world had to go, and the first version of this check is why.</b> Comparing two
    /// ordinary frames put <b>28,505</b> changed pixels on the board before the air was touched at
    /// all — clouds drift, the sun moves, water animates — and six bubbles are worth about seven
    /// hundred. The signal was inside the noise, so the honest answer was not a bigger threshold but
    /// a quieter background.</para>
    /// <para>⛳ <b>It is still the real <see cref="HudRenderer.Draw"/>, with real GL state, on the real
    /// back buffer.</b> Nothing is simulated and no arithmetic is re-implemented; the only thing taken
    /// away is everything that was never being measured. Two of these frames taken with the same
    /// vitals must come back <em>identical</em>, which is what makes the third read a control rather
    /// than a hope.</para>
    /// </remarks>
    private byte[] HudOnlyFrame(Vector2D<int> size)
    {
        _gl.ClearColor(0f, 0f, 0f, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _furnaces.TryGet(_station.X, _station.Y, _station.Z, out var open);
        _hudScreen.Burning = _hudScreen.Kind == HudScreenKind.Furnace ? open : null;

        _hud.Draw(
            _blockTextures, _items, _inventory, _equipment, _vitals,
            _hudScreen, _layout, _toasts, size.X, size.Y);

        return ReadFrame(size);
    }

    /// <summary>
    /// Whether taking the air away changed anything on the screen, and where.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>The noise floor is measured rather than assumed.</b> Two frames of the same live world
    /// never come back identical, so a bare "some pixels differ" would be satisfied by a drifting
    /// cloud and would have called this bar working for as long as anybody cared to look. What is
    /// claimed is that the air is worth <em>more</em> change than a frame is worth on its own.
    /// </remarks>
    private void JudgeBreath()
    {
        if (_frameHalfAir is null || _frameFullAir is null || _frameFullAgain is null) return;
        if (_frameHalfAir.Length != _frameFullAir.Length) return;
        if (_frameFullAir.Length != _frameFullAgain.Length) return;

        _breathNoise = Differ(_frameFullAir, _frameFullAgain, out _);
        _breathPixels = Differ(_frameHalfAir, _frameFullAir, out _breathBox);

        var box = _breathBox.X0 < 0
            ? "nowhere"
            : $"x {_breathBox.X0}..{_breathBox.X1}, y {_breathBox.Y0}..{_breathBox.Y1} from the bottom";

        Console.WriteLine(
            $"ui-check    breath px  {_breathPixels} pixels changed when the air went, against "
            + $"{_breathNoise} between two frames that did not; they were at {box}");
        Console.Out.Flush();
    }

    /// <summary>Pixels two captured frames disagree on. Counted exactly, never averaged, and the
    /// bounding box comes off the same walk. Shared by the dry-land breath check and the dive;
    /// <paramref name="within"/> confines the count to one rectangle in buffer coordinates —
    /// without it, the dive's diff counted the hotbar the fire check had refilled and proved
    /// nothing about bubbles at all.</summary>
    private int Differ(
        byte[] a, byte[] b, out (int, int, int, int) bounds,
        (int X0, int Y0, int X1, int Y1)? within = null)
    {
        var width = _frameWidth;
        var count = 0;
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;

        for (var p = 0; p < a.Length; p += 4)
        {
            if (a[p] == b[p] && a[p + 1] == b[p + 1] && a[p + 2] == b[p + 2]) continue;

            var pixel = p / 4;
            var x = pixel % width;
            var y = pixel / width;

            if (within is { } box
                && (x < box.X0 || x > box.X1 || y < box.Y0 || y > box.Y1)) continue;

            count++;
            if (x < x0) x0 = x;
            if (y < y0) y0 = y;
            if (x > x1) x1 = x;
            if (y > y1) y1 = y;
        }

        bounds = count == 0 ? (-1, -1, -1, -1) : (x0, y0, x1, y1);
        return count;
    }

    /// <summary>The width the captured frames were read at, for turning an offset into a position.</summary>
    private int _frameWidth = 1;

    /// <summary>What share of the water layer changed, and the control that says the read is real.</summary>
    private void JudgeWater()
    {
        if (_waterBefore is null) return;

        var after = _blockTextures.ReadLayer(StarterBlocks.LayerWater);
        if (after.Length != _waterBefore.Length) return;

        // ⛔ COUNTED PIXEL FOR PIXEL, NEVER AVERAGED. A travelling swell keeps very nearly the same
        // mean brightness however far along it is, so a mean is blind to exactly the thing being
        // measured — the same trap the turning-block check fell into and was rewritten for.
        var moved = 0;
        var pixels = after.Length / 4;
        for (var p = 0; p < pixels; p++)
        {
            var at = p * 4;
            if (after[at] != _waterBefore[at]
                || after[at + 1] != _waterBefore[at + 1]
                || after[at + 2] != _waterBefore[at + 2])
            {
                moved++;
            }
        }

        // ⚠ Three percent, not the twenty the first version asked for. Ours is a swell that moves
        // most of the tile; a real pack's water is a far subtler ripple, and Vintage's failed a
        // working animation on the threshold alone. What makes a small number mean something here is
        // not its size but the control below — these are exact byte comparisons, so there is no
        // noise for a low bar to let through, and a layer nobody animates has to come back identical.
        var share = moved * 100 / pixels;
        if (share < 3) return;

        // ⛔ And the control, only once it matters: a layer nobody animates has to come back the same
        // twice running. If stone "moves" too then the read is handing back something other than the
        // layer it was asked for, and the water number is measuring the reader rather than the water.
        var stone = _blockTextures.ReadLayer(StarterBlocks.LayerStone);
        var stoneAgain = _blockTextures.ReadLayer(StarterBlocks.LayerStone);
        var stoneStill = stone.AsSpan().SequenceEqual(stoneAgain);

        _waterMoved = stoneStill;

        Console.WriteLine(
            $"ui-check    water      {share}% of the layer changed on the card by frame "
            + $"{_uiCheckFrame}, stone {(stoneStill ? "unchanged" : "CHANGED")}, "
            + $"{_animatedTextures.Uploads} uploads over {_elapsed:F2}s");
        Console.Out.Flush();
    }

    /// <summary>Worlds the check writes to disk so the menu has something real to go and find.</summary>
    private static readonly string[] CheckWorlds = ["ui-check-alpha", "ui-check-beta"];

    /// <summary>True once the menu has found both of them by itself.</summary>
    private bool _foundPlanted;

    /// <summary>
    /// A file that is not a world, written beside them on purpose.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Named so it sorts BEFORE the two good ones</b>, and that is not cosmetic. The failure
    /// this guards against is a reader that gives up at the first file it cannot open; with the bad
    /// file last, such a reader still returns both worlds and the check passes while being unable to
    /// reproduce the bug at all. It has to come first for the control to mean anything.
    /// </remarks>
    private const string BrokenWorld = "ui-check-0-broken";

    /// <summary>True once the menu has said out loud that it could not read that one.</summary>
    private bool _reportedBroken;

    /// <summary>True if the two good worlds survived being listed beside a file that will not read.</summary>
    private bool _goodSurvivedTheBad;

    /// <summary>
    /// Writes two worlds, so opening the menu has a folder to read rather than a field to trust.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Real files, and that is the point.</b> Handing the screen a list in memory tests that it
    /// can draw one; only a file on disk tests that it goes and looks. They are removed again as
    /// soon as the list has been measured, and named so that one left behind by a crashed check is
    /// obviously not somebody's world.
    /// </remarks>
    private void PlantWorldsForCheck()
    {
        foreach (var name in CheckWorlds)
        {
            var state = new WorldState(
                "1234", _items, new VoxelWorld(_registry),
                new FurnaceBank(_items, _book), new ChestBank(_items),
                new Inventory(_items), new Equipment(_items),
                new PlayerVitals(_registry), new RecipeUnlocks());

            if (WorldSave.Write(name, state) is { } fault)
                Console.Error.WriteLine($"ui-check    could not plant '{name}': {fault}");
        }

        // ⛔ AND ONE THAT IS NOT A WORLD, WHICH IS THE HALF NOTHING WAS ASKING. A file the header
        // reader cannot open used to be dropped from the list without a word — the exact shape of
        // "there is a world here and the game says there is nothing". Two things have to hold with
        // this sitting in the folder: the good ones are still listed, and the bad one is named.
        try { File.WriteAllBytes(WorldSave.PathFor(BrokenWorld), "not a world at all"u8.ToArray()); }
        catch (Exception fault)
        {
            Console.Error.WriteLine($"ui-check    could not plant the broken file: {fault.Message}");
        }
    }

    private static void RemovePlantedWorlds()
    {
        foreach (var name in CheckWorlds.Append(BrokenWorld))
        {
            try { File.Delete(WorldSave.PathFor(name)); } catch (Exception) { }
            for (var slot = 1; slot <= WorldSave.Backups; slot++)
                try { File.Delete(WorldSave.BackupPath(name, slot)); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Opens each screen in turn and reads the pixels back off the framebuffer.
    /// </summary>
    /// <remarks>
    /// <para>The whole point is that it reads the <em>result</em>. Everything short of this proves
    /// the geometry was built, and geometry was being built correctly all the way through a fault
    /// where nothing arrived on screen — panels counted, glyphs counted, no exception, and a black
    /// window. A count of quads cannot tell "not drawn" from "drawn somewhere else"; a pixel can.
    /// </para>
    /// <para>Sampled where the panel is, and again in a corner where it is not, so the check has
    /// its own control: if both read the same the screen is not covering what it claims to, and if
    /// the middle never changes at all nothing is being drawn there whatever the counters say.</para>
    /// </remarks>
    private unsafe void RunUiCheck(Vector2D<int> size)
    {
        // ⛔ NOTHING UNTIL THE BODY IS STANDING ON REAL GROUND, which is the same guard the shot
        // harness has and for the same reason. What follows is a script of frame numbers, and the
        // sixty frames it allowed for the world to arrive were a guess about a worker pool rather
        // than a fact about one. On a machine that had just finished a publish — cold cache, the
        // binary still being scanned — the world had not arrived by frame sixty, the screens were
        // opened over nothing, and eight assertions fired at once naming everything except the
        // reason. It passed five times out of five when run again by hand, which is what a race
        // looks like from the outside.
        // ⛔ NOTHING UNTIL THE BODY IS STANDING ON REAL GROUND, which is the same guard the shot
        // harness has and for the same reason. What follows is a script of frame numbers, and the
        // sixty frames it allowed for the world to arrive were a guess about a worker pool rather
        // than a fact about one.
        //
        // ⚠ HONESTLY: this did not reproduce. The gate failed once, immediately after a publish,
        // with eight assertions firing at once and every sample reading sky — a screen script that
        // ran over a world that was not there. Five runs by hand afterwards passed, and starving the
        // chunk uploads to one a frame did not bring it back either. So this is the precondition
        // that failure implies rather than a fix measured against it, and if it returns, the thing
        // to find out first is whether the frames had been reached at all.
        if (!_spawned) { _uiCheckFrame = 0; return; }

        _uiCheckFrame++;

        // ⚠ Watched every frame until it moves rather than compared across a fixed gap, and the two
        // versions before this one both got it wrong in the same way. Two reads ten frames apart is
        // 0.09 s on this machine and a frame of water is held for 0.125 s, so the check failed a
        // working animation by looking twice inside one frame. Widening the window to seventy frames
        // fixed it here and still went red intermittently, because frames are not time: the run that
        // failed spent most of its early frames loading and covered barely a tenth of a second
        // between the two ends of the window. The deadline is now most of the check's whole life,
        // which is seconds however fast the frames go.
        if (_uiCheckFrame is > 20 and < 320 && !_waterMoved) JudgeWater();

        // ⛳ Polled rather than scheduled, for the same reason the water is. The tooltip probe waits
        // for the pocket to be laid out and then for the box to be drawn; four fixed frames worked
        // until the day the world got three times taller and stopped lining up with them.
        if (_tipStage is > 0 and < 5 && _uiCheckFrame < 118) StepTooltipProbe(size);

        // ⚠ Polled every frame and ended by the clock rather than by a frame number, then the window
        // is closed from here. Written into the switch below it would be a window in FRAMES around a
        // claim about SECONDS, which at five thousand frames a second measures nothing at all.
        if (_airStage > 0)
        {
            StepAirProbe(size);
            if (_airStage == 5) { JudgeUi(); _window.Close(); }
        }

        // A few frames to let the world stream in, then each screen in turn.
        switch (_uiCheckFrame)
        {
            // ⛔ THE WATER, OFF THE CARD, TWICE. Everything short of this proves the animator decided
            // to upload something: that the strip was read, that a frame changed, that WriteLayer was
            // called. None of it proves a texel on the card moved — an upload aimed at the wrong
            // layer, the wrong mip level or a texture nobody binds does all of the above and leaves
            // a still lake. Read the layer, wait, read it again, and compare it to itself.
            case 20: _waterBefore = _blockTextures.ReadLayer(StarterBlocks.LayerWater); break;

            // ⛔⛔ THE BREATH BAR, WHICH A USER REPORTED AS NOT SHOWING AT ALL. Every part of it
            // passed on its own — the sheet loads, the tile has a hundred and thirteen texels of ink,
            // the layer number is right, the call is in the draw — and not one of those is the same
            // claim as QUADS REACHING THE SCREEN. It is also the only bar nothing had ever looked at,
            // because seeing it means being underwater and this whole script runs on dry land.
            // ⛳ Breath is dropped through Restore rather than by drowning a body: the bar's guard is
            // "submerged OR short of air", and short of air is enough to make it draw — so this needs
            // no lake, no swimming and no waiting for a body to sink.
            // ⚠ Before frame 61, because every frame after that has a screen open and the bars are
            // deliberately not drawn under one.
            case 50: _vitals.Restore(PlayerVitals.MaxHealth, PlayerVitals.MaxBreath / 2); break;

            // ⛔⛔ AND THE ONLY MEASUREMENT THAT SETTLES IT: the frame itself, before and after the
            // air comes back. Everything above this line is the renderer being asked about its own
            // work. This is the screen being asked instead.
            case 52:
                _breathBubbles = _hud.LastBubbles;
                _breathAt = _hud.LastBubbleAt;
                _frameWidth = size.X;
                _frameHalfAir = HudOnlyFrame(size);
                _vitals.Restore(PlayerVitals.MaxHealth, PlayerVitals.MaxBreath);
                break;

            case 54: _frameFullAir = HudOnlyFrame(size); break;

            // ⛔ The control, and it is the strong kind: the same vitals drawn twice on the same flat
            // background have to come back pixel for pixel identical. Any difference at all here
            // means the read or the draw is unrepeatable, and the number above it means nothing.
            case 56:
                _frameFullAgain = HudOnlyFrame(size);
                JudgeBreath();
                break;

            case 60: SampleUi(size, "no screen"); break;

            case 61:
                OpenPlayer(PlayerTab.Items, atBench: false, default);

                // ⛳ A block and the slab cut from it, in two pockets. They wear the SAME tile — a
                // slab's icon layer is its own particle layer, which is the rock — so the only thing
                // that can tell them apart in a square is the shape being drawn. That is why it is a
                // pair and not a slab on its own: "a slab drew something" is true of the flat tile
                // it used to draw, and was, for every shaped block in the game.
                _inventory.Add(new ItemStack(_items.ByName("stone").Id, 1));
                _inventory.Add(new ItemStack(_items.ByName("stone_slab").Id, 1));
                RefreshScreen();
                break;

            case 90:
                SampleUi(size, "items");
                ProbeSquares();
                SampleFigure(size);
                SampleWell(size, "book well before");
                SampleHeldIcon(size, "stone", "icon block");
                SampleHeldIcon(size, "stone_slab", "icon slab");

                // The top fifth of each square: where a block's own solid reaches and a slab's,
                // being half a block high, cannot.
                SampleHeldIcon(size, "stone", "icon block top", 0f, 0.2f);
                SampleHeldIcon(size, "stone_slab", "icon slab top", 0f, 0.2f);
                _inventory.Clear();
                break;

            // ⛳ THE TOOLTIP, AND THE GUTTER BESIDE IT. A stone goes in the first pocket, the pointer
            // is put on it, and the frame after that the box is read back. Then the pointer moves two
            // units left into the gap between that square and the next — where the layout has no zone
            // at all — and the same rectangle is read again. One arm alone passes a build that draws
            // a tooltip everywhere; it is the pair that says it is reading the square.
            case 91:
                _inventory.Add(new ItemStack(_items.ByName("stone_pickaxe").Id, 1));
                RefreshScreen();
                _tipStage = 1;
                break;

            case 120:
                SampleUi(size, "book");
                SampleWell(size, "book well after");
                ProbeBook();
                break;

            // ⛳ The same square of the same page, twice, with the clock moved between them. A block
            // turning is the one thing here no still frame can show, and a page that had quietly
            // stopped turning would look exactly like one that never did.
            // ⛔ THREE POINTS ROUND A WHOLE TURN, NOT TWO, and a narrow sprite is why. How much a
            // square changes between two angles depends on where in the turn those two angles fall:
            // a solid filling its square moves a lot from anywhere, but a torch — an upright stick
            // on a transparent tile — barely moves at all between two angles that happen to face
            // much the same way, and read 3% while turning perfectly well. A third of a turn apart,
            // three times, and the largest of the three is the answer: no starting angle can hide
            // in all of them.
            case 121: _turns.Add(CaptureRecipes(size)); break;
            case 122: _uiDrift = StillDrift + TurnStep; break;
            case 123: _turns.Add(CaptureRecipes(size)); break;
            case 124: _uiDrift = StillDrift + TurnStep * 2f; break;
            case 125: _turns.Add(CaptureRecipes(size)); JudgeTurn(); _uiDrift = StillDrift; break;

            case 126: CloseScreen(); OpenBench(0, 0, 0); break;
            case 150: SampleUi(size, "bench"); ProbeSquares(); break;

            // A chest with something in it, because an empty one draws the same twenty seven wells
            // whether or not anything reaches a slot — the icon in the corner is the part that
            // proves a stored stack is being read from where the screen says it is.
            case 151:
                CloseScreen();
                OpenChest(0, 0, 0);
                _chests.Add(_hudScreen.Stored!, new ItemStack(_items.ByName("driftoak_planks").Id, 7));
                break;

            case 180: SampleUi(size, "chest"); ProbeSquares(); SampleStored(size); break;

            // A stonecutter with a rock on its bed, so the list has something in it. An empty one
            // draws the same two wells whether or not a single cut was ever found.
            case 181:
                CloseScreen();
                OpenStonecutter(0, 0, 0);
                _hudScreen.Cutting = new ItemStack(_items.ByName("stone").Id, 4);
                RefreshCuts();
                RefreshScreen();
                break;

            case 210: SampleUi(size, "cutter"); ProbeSquares(); ProbeCuts(); break;

            case 211: CloseScreen(); OpenGame(GameTab.Controls); break;
            case 240: SampleUi(size, "game"); ProbeRows("top"); SampleTitle(size); break;

            case 241: ScrollRows(int.MaxValue); break;
            case 250: ProbeRows("bottom"); break;

            // The list of worlds, with two in it.
            //
            // ⚠ Planted rather than read off the disk, and both halves of that matter. What is being
            // checked is that a world becomes a row carrying its name, its played time and its date
            // — and a check reading the real folder would say something different on every machine,
            // including nothing at all on one that has never been played. It would also be a check
            // whose subject a player could delete.
            case 251:
                CloseScreen();
                OpenGame(GameTab.Saves);
                _saved =
                [
                    new SaveHeader("driftwood", "1234",
                        new DateTime(2026, 8, 5, 14, 32, 0, DateTimeKind.Utc), 11_530, 0.4f, 812),
                    new SaveHeader("saltmarsh", "9911",
                        new DateTime(2026, 8, 1, 9, 5, 0, DateTimeKind.Utc), 240, 0.1f, 3),
                ];
                RefreshScreen();
                break;

            case 280: SampleUi(size, "saves"); ProbeRows("saves"); break;

            // The menu, opened on a world the check is already standing in rather than by flying a
            // camera at one — the flight is not what is being looked at.
            //
            // ⛔ TWO REAL WORLDS ON DISK, AND THE LIST EMPTIED FIRST. This used to lean on the two
            // planted into _saved by the saves tab above, which were still sitting there when the
            // menu opened — so the check watched the menu draw a list it had been handed and never
            // once asked it to go and find one. The menu did not, at boot, and said "none saved yet"
            // to somebody who had saved and closed the game a minute before. The setup was the bug.
            case 281:
                CloseScreen();
                PlantWorldsForCheck();
                _saved = [];
                _unreadable = [];
                ShowStartMenu();
                break;

            case 300: SampleUi(size, "start"); ProbeRows("start"); break;

            case 301: _startListing = true; RefreshScreen(); break;
            case 310:
                ProbeRows("start list");
                _foundPlanted = CheckWorlds.All(name => _saved.Any(w => w.Name == name));

                // The row carrying the broken file's own name, off the rows that were built —
                // asking _unreadable would only prove the reader filled a list, not that anybody
                // reading the screen is told.
                _reportedBroken = _hudScreen.Rows.Any(r => r.Label == $"{BrokenWorld}.dws");

                // ⛔ The other half, and the one a "does it report the bad file" check on its own
                // would miss entirely: a folder with one unreadable file in it must still list the
                // worlds that are fine. A reader that gave up at the first fault would pass the
                // line above and lose somebody's worlds.
                _goodSurvivedTheBad = _saved.Count >= CheckWorlds.Length;

                Console.WriteLine(
                    $"ui-check    saves folder {_saved.Count} readable, {_unreadable.Count} not"
                    + (_unreadable.Count > 0 ? $" ({string.Join(", ", _unreadable.Select(u => u.File))})" : ""));
                Console.Out.Flush();

                RemovePlantedWorlds();
                break;

            // The seed box: focused, sampled empty, typed into, sampled again.
            //
            // ⚠ A PAIR, and it has to be. A box draws a sunken frame whether or not one character
            // ever reaches it, so "there are pixels where the box is" is true of a box that takes
            // nothing at all. The two samples are the same point on the same screen with the only
            // difference being that something was typed.
            case 311:
                // ⚠ Rebuilt BEFORE the row is looked for. The rows still held the list of worlds
                // from the step above, so the box was hunted for on a screen that did not have one
                // and the check reported that nothing was typed into it.
                _startListing = false;
                RefreshScreen();

                _hudScreen.Selected = SeedRow();
                ActivateRow();
                RefreshScreen();
                break;

            case 320: SampleField(size, "seed empty"); break;

            case 321:
                foreach (var c in "driftwood") OnCharTyped(c);
                break;

            case 330:
                SampleField(size, "seed typed");
                _typedSeed = _seedBox.Text;
                StopTyping(accept: false);
                break;

            // ⛔⛔ THE FIRE'S BOOK, WHICH IS THE THING A USER HAD TO REPORT: "I'm not seeing any
            // recipes for food when i look in the furnace." There was no list at all, and no screen
            // in this script had ever opened a furnace — the eight it walks are the eight that had
            // one. A page counted here is a page that exists; the sample beside it is a page that
            // arrived.
            case 331: CloseScreen(); OpenFireForCheck(); break;

            case 340:
                SampleUi(size, "furnace");
                ProbeFireBook(size);

                // ⛳ Started here and POLLED to its own end, because what follows is measured in
                // seconds of game time and this script is written in frames.
                CloseScreen();
                _airStage = 1;
                break;
        }
    }

    /// <summary>What the fire's book came to: rows laid out, and rows that reached the screen.</summary>
    private (int Page, int Foods, int Drawn, bool Loaded) _uiFire = (-1, -1, -1, false);

    private int _airStage;
    private double _airDeadline;
    private int _airFrom = -1;
    private int _airTo = -1;
    private double _airTook = -1;

    /// <summary>
    /// Watches breath come back to full through the client's own loop, in seconds of game time.
    /// </summary>
    /// <remarks>
    /// <para>⛔⛔ <b>Reported by the user after the first fix: the bubbles <i>"should disappear
    /// entirely when you leave the water but they don't"</i>.</b> The bar is hidden by breath reaching
    /// its maximum, so a refill that stalls even one tick short leaves it on screen for the rest of
    /// the session — and every check in the project was green, because the audit drives
    /// <c>PlayerVitals.Update</c> directly in a pool it builds itself and never once goes through
    /// <c>StepVitals</c>, the frame loop, or the client's own <c>dt</c>.</para>
    /// <para>⛳ <b>Watched in SECONDS, polled, never over a fixed number of frames.</b> At the five
    /// thousand frames a second this runs at, forty frames is eight milliseconds and a tick of air is
    /// sixteen — a frame-counted window would measure the frame rate and call a stalled bar healthy.
    /// </para>
    /// </remarks>
    private void StepAirProbe(Vector2D<int> size)
    {
        switch (_airStage)
        {
            // Half a lungful, put in the way a save does, then left alone on dry land.
            case 1:
                _vitals.Restore(PlayerVitals.MaxHealth, PlayerVitals.MaxBreath / 2);
                _airFrom = _vitals.Breath;
                _airDeadline = _elapsed + 6.0;
                _airStage = 2;
                break;

            case 2 when _vitals.Breath >= PlayerVitals.MaxBreath || _elapsed >= _airDeadline:
                _airTo = _vitals.Breath;
                _airTook = _elapsed - (_airDeadline - 6.0);

                Console.WriteLine(
                    $"ui-check    air back   {_airFrom} to {_airTo} of {PlayerVitals.MaxBreath} in "
                    + $"{_airTook:F2}s of game time, submerged {_vitals.Submerged}");
                Console.Out.Flush();

                // ⛳⛳ AND NOW THE HALF NOTHING HAS EVER DRIVEN: a head actually under water, in the
                // real client, through the real frame loop. Every drowning check in the project builds
                // its own pool and calls PlayerVitals.Update by hand — which is the shape of check
                // this project has been caught by twice.
                Sink();
                _airDeadline = _elapsed + 0.25;
                _airStage = 30;
                break;

            // ⛔⛔ THE PIXELS, MID-DIVE — the union the user's third report exposed. One check dives
            // through the real loop and never looks at the screen; another reads the screen with a
            // lungful forced by hand and never dives. Both green, and a player still saw nothing.
            // This is the pair together: submerge at FULL air, and the bar must appear on screen.
            case 30 when _elapsed >= _airDeadline:
                _frameDivedFull = HudOnlyFrame(size);

                // ⛔ Confined to the strip the renderer says the bubbles occupy — the whole-frame
                // diff counted the hotbar the fire check had refilled and read forty-nine
                // thousand pixels of nothing to do with air. A margin of four covers the tremble.
                if (_frameFullAir is { } dry && dry.Length == _frameDivedFull.Length)
                    _bubbleAppearPixels = Differ(dry, _frameDivedFull, out _, BubbleRowBuffer(size));

                _airDivedFrom = _vitals.Breath;
                _airDeadline = _elapsed + 3.0;
                _airStage = 3;
                break;

            case 3 when _elapsed >= _airDeadline:
                _airDivedTo = _vitals.Breath;
                _airSubmerged = _vitals.Submerged;

                // And the same screen three seconds later: fewer bubbles, read as pixels.
                if (_frameDivedFull is { } divedFull)
                {
                    var drained = HudOnlyFrame(size);
                    if (divedFull.Length == drained.Length)
                        _bubbleDrainPixels = Differ(divedFull, drained, out _, BubbleRowBuffer(size));
                }

                Console.WriteLine(
                    $"ui-check    air under  {_airDivedFrom} to {_airDivedTo} of "
                    + $"{PlayerVitals.MaxBreath} after 3s under water, submerged {_airSubmerged}; "
                    + $"bubbles appeared over {_bubbleAppearPixels} px, drained over {_bubbleDrainPixels} px");
                Console.Out.Flush();

                // ⛳⛳ AND THE STATE THE USER WAS ACTUALLY IN: a body that is not being simulated,
                // still under water, with the air already half spent. The bar used to freeze exactly
                // there and stay for the session. Held for a real second of game time, because the
                // claim is about what happens over time and this loop runs at thousands of frames.
                _walking = false;
                _airDeadline = _elapsed + 1.0;
                _airStage = 4;
                break;

            case 4 when _elapsed >= _airDeadline:
                _airUnsimulated = _vitals.Breath;
                _walking = true;
                _airStage = 5;

                Console.WriteLine(
                    $"ui-check    air unsim  {_airDivedTo} to {_airUnsimulated} of "
                    + $"{PlayerVitals.MaxBreath} after a second under water with the body not stepped");
                Console.Out.Flush();
                break;
        }
    }

    private int _airDivedFrom = -1;
    private int _airDivedTo = -1;
    private int _airUnsimulated = -1;
    private bool _airSubmerged;

    /// <summary>The screen as the head went under at full air, and how much of it changed.</summary>
    private byte[]? _frameDivedFull;
    private int _bubbleAppearPixels = -1;
    private int _bubbleDrainPixels = -1;

    /// <summary>The bubble strip in buffer coordinates: renderer's own layout units scaled to the
    /// window, y flipped to the framebuffer's bottom-up rows, four pixels of tremble margin.</summary>
    private (int X0, int Y0, int X1, int Y1) BubbleRowBuffer(Vector2D<int> size)
    {
        var scale = HudRenderer.ScaleFor(size.Y);
        var row = _hud.LastBubbleRow;

        return (
            (int)(row.X0 * scale) - 4,
            size.Y - 1 - (int)(row.Y1 * scale) - 4,
            (int)(row.X1 * scale) + 4,
            size.Y - 1 - (int)(row.Y0 * scale) + 4);
    }

    /// <summary>Campfire dinners drawn, read while the fire was still standing.</summary>
    private int _cookingSeen = -1;

    /// <summary>Whether a fire opened with its book out, and whether it had a button to fold it.</summary>
    private bool _fireBookOut;
    private bool _fireBookButton;

    /// <summary>Frames drawn through the ordinary loop, for the frame limit to be measured against.</summary>
    private long _playedFrames;

    /// <summary>When counting started, which is not when the session did.</summary>
    private double _steadyFrom;

    /// <summary>
    /// Seconds of a session that are loading rather than playing, and are not the frame rate.
    /// </summary>
    /// <remarks>
    /// ⚠ Generous on purpose. Streaming keeps working long after the first frame is drawn, and a
    /// warm-up that ends too early puts the tail of it back into the number it was meant to keep out.
    /// </remarks>
    private const double WarmUpSeconds = 4.0;

    /// <summary>
    /// Buries the body in a sealed box of water, so the head is genuinely under.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Sealed, and the shell is why.</b> Water dropped into open air is a cell the flow engine
    /// quite correctly drains, so a probe that placed a few blocks and looked a second later would be
    /// measuring how fast a puddle disappears. Stone all the way round gives it nowhere to go.
    /// </remarks>
    private void Sink()
    {
        var at = _player.Position;
        var (x, y, z) = ((int)MathF.Floor(at.X), (int)MathF.Floor(at.Y), (int)MathF.Floor(at.Z));

        var stone = _registry.ByName("stone").Id;
        var water = _registry.ByName("water").Id;

        for (var dy = -1; dy <= 4; dy++)
        for (var dz = -2; dz <= 2; dz++)
        for (var dx = -2; dx <= 2; dx++)
        {
            var shell = dy is -1 or 4 || dz is -2 or 2 || dx is -2 or 2;
            _streamer.EditBlock(x + dx, y + dy, z + dz, shell ? stone : water);
        }
    }

    /// <summary>
    /// Builds a real furnace beside the player and opens it, with something to cook in hand.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>A real block, not just the screen.</b> Which fire this is is read off the cell every
    /// time it is asked, so opening the furnace screen over a cell holding stone would silently
    /// measure the default kind — and would go on passing on a build where no smelter block ever
    /// resolved to anything.
    /// </remarks>
    private void OpenFireForCheck()
    {
        var at = ((int)MathF.Floor(_player.Position.X) + 2, (int)MathF.Floor(_player.Position.Y),
                  (int)MathF.Floor(_player.Position.Z));

        _streamer.EditBlock(at.Item1, at.Item2, at.Item3, _registry.ByName("furnace_north").Id);

        // ⛳ A lit campfire with dinner on it, one cell over, so the thing drawn ON a fire is at
        // least walked. ⚠ This proves the enumeration, the kind lookup and the transform run — it is
        // a COUNTER, not a pixel, and it is not the same claim as the breath bar's. Whether a steak
        // on a campfire reads as a steak on a campfire is the user's eyes, as it always is here.
        var fire = (at.Item1, at.Item2, at.Item3 + 2);
        _streamer.EditBlock(fire.Item1, fire.Item2, fire.Item3, _registry.ByName("campfire_x_lit").Id);
        _furnaces.Open(fire.Item1, fire.Item2, fire.Item3).Input =
            new ItemStack(_items.ByName("raw_beef").Id, 1);

        _inventory.Clear();
        _inventory.Add(new ItemStack(_items.ByName("raw_beef").Id, 3));

        OpenFurnace(at.Item1, at.Item2, at.Item3);

        // ⛔ NOT `_hudScreen.BookOut = true` any more. The opener puts the book out itself, and
        // this check setting the field is exactly how a furnace with NO BOOK BUTTON stayed
        // green for a session: it proved the book renders when out, and never that a player has
        // any control that gets it out. The user reported the fires "not showing recipes" a
        // second time before the layout was found to carry no toggle at all.
        RefreshScreen();
    }

    /// <summary>
    /// What the fire's book actually put on the screen, counted and then read off the card.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>Three separate claims, because the first two are the ones that were already true
    /// while the feature did not exist.</b> That the page has rows is a fact about a list; that the
    /// rows are laid out is a fact about <c>ScreenLayout</c>; and neither is the same claim as a
    /// picture of a steak being on the screen. The last one is read out of the framebuffer, which is
    /// the distinction this project paid for twice in one day.</para>
    /// <para>⚠ <b>Sampled against the panel beside it rather than against a remembered colour.</b>
    /// A recipe square that drew nothing reads as the book's own backing, and the book's backing is
    /// not a number anybody should be writing down here.</para>
    /// </remarks>
    private unsafe void ProbeFireBook(Vector2D<int> size)
    {
        var page = 0;
        var drawn = 0;
        var scale = HudRenderer.ScaleFor(size.Y);

        // ⚠ Outside the loop. A stackalloc inside one is a frame that grows with every row of the
        // page, which is a real overflow on a long book rather than a style note.
        Span<byte> middle = stackalloc byte[4];
        Span<byte> beside = stackalloc byte[4];

        foreach (var zone in _layout.Zones)
        {
            if (zone.Kind != ZoneKind.Recipe) continue;
            if (zone.Index >= _hudScreen.Recipes.Count) continue;
            page++;

            // The middle of the square, in the framebuffer's own bottom-up rows.
            var x = (int)((zone.X + zone.W * 0.5f) * scale);
            var y = (int)((zone.Y + zone.H * 0.5f) * scale);
            if (x < 0 || y < 0 || x >= size.X || y >= size.Y) continue;

            fixed (byte* p = middle)
                _gl.ReadPixels(x, size.Y - 1 - y, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);

            // A few pixels above the square, which is the book's own backing and never an icon.
            var above = Math.Clamp(y - (int)(zone.H * scale), 0, size.Y - 1);
            fixed (byte* p = beside)
                _gl.ReadPixels(x, size.Y - 1 - above, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);

            if (middle[0] != beside[0] || middle[1] != beside[1] || middle[2] != beside[2]) drawn++;
        }

        // Something edible on the page, which is the word the user used.
        var foods = 0;
        foreach (var recipe in _hudScreen.Recipes)
            if (_items[recipe.Result.Item].Feeds > 0) foods++;

        // ⛳ AND THAT CLICKING ONE LOADS THE FIRE. A book you can read and not act on is a list that
        // has not been wired up, and nothing else in this script goes down the LoadFire path.
        var pick = -1;
        for (var i = 0; i < _hudScreen.Recipes.Count; i++)
            if (_hudScreen.Payable[i]) { pick = i; break; }

        var loaded = false;
        if (pick >= 0)
        {
            _hudScreen.Selected = pick;
            LoadFire(pick, all: false);
            loaded = _furnaces.TryGet(_station.X, _station.Y, _station.Z, out var fire) && !fire.Input.IsEmpty;
        }

        _uiFire = (page, foods, drawn, loaded);

        // ⛔ Read HERE, while the campfire still exists. The air probe that runs after this seals the
        // player into a box of stone and water and buries the fire doing it, so taken at the end this
        // read zero and fired — a check measuring a subject another check had already demolished.
        _cookingSeen = CookingDrawn;

        // The two claims the user's second report exposed as missing: the fire opened SHOWING
        // its book (the opener's own doing, not this script's), and the screen carries the
        // button a player folds it with.
        _fireBookOut = _hudScreen.BookOut;
        _fireBookButton = false;
        foreach (var zone in _layout.Zones)
            if (zone.Kind == ZoneKind.Button && zone.Index == (int)ScreenButton.Book)
                _fireBookButton = true;

        Console.WriteLine(
            $"ui-check    fire book  {page} rows laid out of {_hudScreen.Recipes.Count}, {foods} of them "
            + $"edible, {drawn} reached the screen; clicking one "
            + (loaded ? "put it on the fire" : "PUT NOTHING ON THE FIRE"));
        Console.Out.Flush();
    }

    /// <summary>Which row of the menu carries the seed box, found by its label.</summary>
    private int SeedRow()
    {
        for (var i = 0; i < _hudScreen.Rows.Count; i++)
            if (_hudScreen.Rows[i].Edits is not null) return i;

        return -1;
    }

    /// <summary>What the box was holding after the check typed into it.</summary>
    private string _typedSeed = "";

    /// <summary>Where the check holds the drifting clock, so every run samples the same frame.</summary>
    private const float StillDrift = 0.8f;

    private float _uiDrift = StillDrift;

    /// <summary>The middle of the first recipe square, pixel for pixel.</summary>
    /// <remarks>
    /// ⛔ <b>Kept whole rather than averaged, and the average is why.</b> "Did this move" was first
    /// asked as a mean over the square, which changed by <b>one unit</b> across fifty-seven degrees
    /// of turn — and would have changed by about that much if the block had been standing still and
    /// something else had twitched. A turning solid keeps very nearly the same average brightness
    /// however far round it is: the mean is blind to exactly the thing being measured. Counting the
    /// pixels that actually differ is not.
    /// </remarks>
    private unsafe List<byte[]> CaptureRecipes(Vector2D<int> size)
    {
        var patches = new List<byte[]>();
        var scale = HudRenderer.ScaleFor(size.Y);

        foreach (var zone in _layout.Zones)
        {
            if (zone.Kind != ZoneKind.Recipe) continue;

            var w = Math.Max(1, (int)(zone.W * scale * 0.6f));
            var h = Math.Max(1, (int)(zone.H * scale * 0.6f));
            var x = (int)((zone.X + zone.W * 0.2f) * scale);
            var y = (int)((zone.Y + zone.H * 0.2f) * scale);

            if (x < 0 || y < 0 || x + w > size.X || y + h > size.Y) { patches.Add([]); continue; }

            var patch = new byte[w * h * 4];
            fixed (byte* p = patch)
                _gl.ReadPixels(
                    x, size.Y - 1 - (y + h), (uint)w, (uint)h,
                    (GLEnum)PixelFormat.Rgba, (GLEnum)PixelType.UnsignedByte, p);

            patches.Add(patch);
        }

        return patches;
    }

    /// <summary>Every recipe square of the page, once per third of a turn.</summary>
    private readonly List<List<byte[]>> _turns = [];

    /// <summary>Seconds of drift that turn a block by a third of a revolution.</summary>
    private const float TurnStep = 2f * MathF.PI / 3f / 0.55f;

    /// <summary>The share of the least-changed square. Below zero when it was never measured.</summary>
    private float _turnMoved = -1f;

    /// <summary>
    /// Compares every square of the page against itself before the clock moved.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>EVERY square, and the worst of them is the answer.</b> The first version watched one,
    /// which is fine for "does the page turn" and useless for the thing the user actually caught —
    /// that <em>some</em> of it turned. A torch and a ladder sat still among the turning blocks and
    /// the check was perfectly happy, because it was looking at a block. What is claimed is that
    /// nothing on the page is standing still, so the quietest square is the one that has to answer.
    /// </remarks>
    private void JudgeTurn()
    {
        if (_turns.Count < 2) return;

        var squares = _turns[0].Count;
        foreach (var round in _turns) if (round.Count != squares) return;

        var worst = 1f;
        var said = new List<string>();

        for (var slot = 0; slot < squares; slot++)
        {
            var most = 0f;

            // The largest change between any two of the three, so a slot cannot pass by standing
            // still and cannot fail by being sampled twice at the same-looking angle.
            for (var i = 0; i < _turns.Count; i++)
            for (var j = i + 1; j < _turns.Count; j++)
                most = MathF.Max(most, Moved(_turns[i][slot], _turns[j][slot]));

            worst = MathF.Min(worst, most);

            // ⚠ Every one of them named, not only the quietest. The zones and the recipes are two
            // lists, and a check that reads a name out of one by an index into the other is a check
            // that can send somebody to look at the wrong square.
            var name = slot < _hudScreen.Recipes.Count
                ? _items[_hudScreen.Recipes[slot].Result.Item].Name
                : "?";

            said.Add($"{name} {most * 100f:F0}%");
        }

        _turnMoved = squares == 0 ? -1f : worst;

        Console.WriteLine($"ui-check    book turn  round a whole turn: {string.Join(", ", said)}");
        Console.Out.Flush();
    }

    /// <summary>The share of two patches whose pixels differ enough to be a different picture.</summary>
    private static float Moved(byte[] a, byte[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0f;

        var moved = 0;
        for (var i = 0; i < b.Length; i += 4)
        {
            // Eight levels, so a rounding difference in one channel is not a moving block.
            if (Math.Abs(b[i] - a[i]) > 8
                || Math.Abs(b[i + 1] - a[i + 1]) > 8
                || Math.Abs(b[i + 2] - a[i + 2]) > 8)
                moved++;
        }

        return (float)moved / (b.Length / 4);
    }

    /// <summary>
    /// Reads the square a named item is sitting in, averaged over the whole of it.
    /// </summary>
    /// <param name="fromY">Top of the strip to read, as a fraction of the square.</param>
    /// <param name="toY">Bottom of it.</param>
    /// <remarks>
    /// <para>The pocket is found by looking for the item rather than by assuming which slot
    /// <see cref="Inventory.Add"/> chose, and the square comes off the layout the renderer laid
    /// down — the two ways this could quietly measure the wrong place.</para>
    /// <para>⛔ <b>A strip rather than the whole square, and the control is what forced it.</b> The
    /// first version compared a stone block's square against a stone slab's and asked that they
    /// differ — which they did, and which they <em>also did</em> in a build with the shape put back
    /// to a flat tile: a flat tile fills the square evenly and reads bright, three shaded faces read
    /// darker. The check passed the broken build for a reason that had nothing to do with the claim.
    /// The top of the square is the discriminating place: a slab drawn as a solid has nothing up
    /// there at all, and a slab drawn as a flat tile has rock.</para>
    /// </remarks>
    private unsafe void SampleHeldIcon(
        Vector2D<int> size, string item, string what, float fromY = 0f, float toY = 1f)
    {
        var wanted = _items.ByName(item).Id;

        var slot = -1;
        for (var i = 0; i < Inventory.Slots; i++)
            if (_inventory[i].Item.Value == wanted.Value) { slot = i; break; }

        if (slot < 0 || _layout.Find(SlotRole.Pocket, slot) is not { } zone)
        {
            Console.Error.WriteLine($"ui-check    {what}: '{item}' is in no square that was drawn");
            return;
        }

        SampleZone(size, zone, what, fromY, toY);
    }

    /// <summary>Averages the pixels of one zone the renderer laid down, or a patch of it.</summary>
    /// <remarks>
    /// ⚠ <b>The patch matters more than it looks.</b> A square is mostly its own well: the icon in
    /// it is inset and, when it is turning, scaled to seven tenths again — so an average over the
    /// whole square is three parts unchanging bevel to one part the thing being measured, and a real
    /// change in the icon arrives here as one or two units. Sample where the thing is.
    /// </remarks>
    private unsafe void SampleZone(
        Vector2D<int> size, Zone zone, string what,
        float fromY = 0f, float toY = 1f, float fromX = 0f, float toX = 1f)
    {
        var scale = HudRenderer.ScaleFor(size.Y);

        var tall = (int)(zone.H * scale);
        var top = (int)(tall * fromY);
        var bottom = (int)(tall * toY);

        var wide = (int)(zone.W * scale);
        var left = (int)(wide * fromX);
        var right = (int)(wide * toX);

        long r = 0, g = 0, b = 0;
        var taken = 0;

        Span<byte> px = stackalloc byte[4];
        for (var dy = top; dy < bottom; dy++)
        for (var dx = left; dx < right; dx++)
        {
            var x = (int)(zone.X * scale) + dx;
            var y = (int)(zone.Y * scale) + dy;
            if (x < 0 || y < 0 || x >= size.X || y >= size.Y) continue;

            fixed (byte* p = px)
                _gl.ReadPixels(x, size.Y - 1 - y, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);

            r += px[0];
            g += px[1];
            b += px[2];
            taken++;
        }

        if (taken == 0) return;

        _uiSamples[what] = ((byte)(r / taken), (byte)(g / taken), (byte)(b / taken));
        Console.WriteLine($"ui-check    {what,-17} rgb {r / taken,3} {g / taken,3} {b / taken,3} over {taken} pixels");
        Console.Out.Flush();
    }

    /// <summary>
    /// Reads the pixels inside the seed box, off the zone the renderer actually laid down.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Off the layout, not off the same constants the box was drawn from.</b> A sample worked
    /// out again from the panel width and the row height is a sample that agrees with the renderer
    /// until either is edited, and then agrees with nothing. The zone is built as the box is drawn.
    /// <para>Averaged over the box's left half rather than read at one point, because a single pixel
    /// between two letters is background in a box that is drawing perfectly.</para>
    /// </remarks>
    /// <summary>
    /// Puts the pointer somewhere and reads back where a tooltip would be.
    /// </summary>
    /// <param name="onto">A square to point at, or null to point at the gutter between two.</param>
    /// <remarks>
    /// ⛔ <b>Always run as a PAIR, and the gutter arm is the one that matters.</b> "A tooltip
    /// appeared" is equally true of a build that draws one everywhere the pointer goes, so the
    /// second arm points at the gap between two squares — where the layout has no zone at all — and
    /// asserts the same rectangle stays panel-coloured. One arm alone tests nothing.
    /// <para>⚠ Sampled at the offset the renderer actually places the box at, and only the top left
    /// of it: a tooltip is mostly its own dark fill, so averaging the whole box would read dark
    /// whatever was written in it, and averaging none of it reads whichever letter it landed on.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Aims the pointer at one square, or at the gutter between two.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>A frame before the sample</b>, because the check runs after the draw: a pointer moved and
    /// read in the same call is read against the frame it was drawn without.
    /// </remarks>
    /// <summary>
    /// Walks the tooltip probe, a stage per frame, waiting on the layout rather than on a count.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>It used to be four fixed frames and it went red on a correct build.</b> The layout is
    /// built as the overlay draws, so pointing at a pocket two frames after opening a screen works
    /// only while the frames happen to line up — and the day the world became three times taller,
    /// they stopped. A window measured in frames measures the frame rate; this waits for the pocket
    /// to <em>exist</em> and then for the box to be drawn, which is what it was always asking.
    /// </remarks>
    private void StepTooltipProbe(Vector2D<int> size)
    {
        switch (_tipStage)
        {
            case 1:
                // Not laid out yet. Try again next frame; the guard on the caller gives up eventually.
                if (PointAt(SlotRole.Pocket, 0)) _tipStage = 2;
                break;

            case 2:
                SampleTooltip(size, "tip on a slot");
                _tipStage = 3;
                break;

            case 3:
                if (PointAt(SlotRole.Pocket, 0, gutter: true)) _tipStage = 4;
                break;

            case 4:
                SampleTooltip(size, "tip on a gutter");

                _inventory.Clear();
                _hudScreen.Pointer = Vector2.Zero;
                _hudScreen.Hovered = null;
                _hudScreen.BookOut = true;
                RefreshScreen();

                _tipStage = 5;
                break;
        }
    }

    /// <summary>0 before the probe, 5 once it is done. Never a frame number.</summary>
    private int _tipStage;

    /// <returns>False when the layout has no such square yet, so the caller can wait.</returns>
    private bool PointAt(SlotRole role, int index, bool gutter = false)
    {
        var first = _layout.Find(role, index);
        if (first is not { } a) return false;

        if (gutter)
        {
            // ⚠ The gap between two squares, found off the layout rather than worked out from the
            // pitch — two units of panel that no zone claims, and the only honest "over nothing".
            var second = _layout.Find(role, index + 1);
            if (second is not { } b) return false;

            _hudScreen.Pointer = new Vector2((a.X + a.W + b.X) * 0.5f, a.CentreY);
        }
        else
        {
            _hudScreen.Pointer = new Vector2(a.CentreX, a.CentreY);
        }

        _hudScreen.Hovered = _layout.At(_hudScreen.Pointer.X, _hudScreen.Pointer.Y);
        return true;
    }

    /// <summary>Whether a box was laid out at each of the two points, and what colour it was.</summary>
    private readonly Dictionary<string, bool> _uiTipDrawn = new(StringComparer.Ordinal);

    private unsafe void SampleTooltip(Vector2D<int> size, string what)
    {
        // ⛳ WHERE THE BOX ACTUALLY IS, off the renderer rather than worked out again from the
        // offset it usually sits at. A tooltip flips to the other side of the pointer near an edge,
        // and the first version of this sampled twelve units down and right unconditionally — which
        // for a bottom-row pocket is bare panel. It read 64 against the gutter's 66 and passed, on a
        // two-count difference that was two samples of the same panel.
        var box = _hudScreen.TipBox;
        _uiTipDrawn[what] = box.Z > 0f;

        if (box.Z <= 0f)
        {
            _uiSamples[what] = default;
            Console.WriteLine($"ui-check    {what,-17} no box laid out");
            Console.Out.Flush();
            return;
        }

        var scale = HudRenderer.ScaleFor(size.Y);

        // The top strip of it, where the title is, rather than the whole box — most of a tooltip is
        // its own fill and an average over all of it reads the fill whatever is written there.
        var x0 = (int)(box.X * scale);
        var y0 = (int)(box.Y * scale);
        var x1 = (int)((box.X + box.Z) * scale);
        var y1 = (int)((box.Y + MathF.Min(box.W, 14f)) * scale);

        long r = 0, g = 0, b = 0;
        var taken = 0;

        Span<byte> px = stackalloc byte[4];
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            if (x < 0 || y < 0 || x >= size.X || y >= size.Y) continue;

            fixed (byte* p = px)
                _gl.ReadPixels(x, size.Y - 1 - y, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);

            r += px[0];
            g += px[1];
            b += px[2];
            taken++;
        }

        if (taken == 0) return;

        _uiSamples[what] = ((byte)(r / taken), (byte)(g / taken), (byte)(b / taken));

        var zoneName = _hudScreen.Hovered is { } over ? $"{over.Kind}/{over.Role}" : "nothing";
        Console.WriteLine(
            $"ui-check    {what,-17} rgb {r / taken,3} {g / taken,3} {b / taken,3} "
            + $"over {zoneName}, box {box.Z:F0}x{box.W:F0}");
        Console.Out.Flush();
    }

    private unsafe void SampleField(Vector2D<int> size, string what)
    {
        var zone = _layout.Zones.FirstOrDefault(z => z.Kind == ZoneKind.Field);
        if (zone.Kind != ZoneKind.Field)
        {
            Console.Error.WriteLine($"ui-check    {what}: no box was laid out at all");
            return;
        }

        var scale = HudRenderer.ScaleFor(size.Y);
        var x0 = (int)(zone.X * scale);
        var x1 = (int)((zone.X + zone.W * 0.5f) * scale);
        var y0 = (int)(zone.Y * scale);
        var y1 = (int)((zone.Y + zone.H) * scale);

        long r = 0, g = 0, b = 0;
        var taken = 0;

        Span<byte> px = stackalloc byte[4];
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            if (x < 0 || y < 0 || x >= size.X || y >= size.Y) continue;

            fixed (byte* p = px)
                _gl.ReadPixels(x, size.Y - 1 - y, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);

            r += px[0];
            g += px[1];
            b += px[2];
            taken++;
        }

        if (taken == 0) return;

        _uiSamples[what] = ((byte)(r / taken), (byte)(g / taken), (byte)(b / taken));
        Console.WriteLine($"ui-check    {what,-17} rgb {r / taken,3} {g / taken,3} {b / taken,3} over {taken} pixels");
        Console.Out.Flush();
    }

    /// <summary>
    /// Which rows of a capped list are actually on screen and reachable.
    /// </summary>
    /// <remarks>
    /// The list is a window onto something longer, and the two ways that goes wrong are opposite:
    /// a window that shows more than it was capped at is a panel running off the bottom again, and
    /// one that cannot be scrolled to the end is a row nobody can ever reach. Both are asked here,
    /// off the zones the renderer actually laid down — the rows a click can land on, not the rows
    /// the list holds.
    /// </remarks>
    private void ProbeRows(string where)
    {
        int seen = 0, lowest = int.MaxValue, highest = -1;

        foreach (var zone in _layout.Zones)
        {
            if (zone.Kind != ZoneKind.Row) continue;
            seen++;
            lowest = Math.Min(lowest, zone.Index);
            highest = Math.Max(highest, zone.Index);
        }

        _uiRows[where] = (seen, lowest, highest, _hudScreen.Rows.Count);
        _uiRowLabels[where] = [.. _hudScreen.Rows.Select(r => r.Label)];

        Console.WriteLine(
            $"ui-check    rows {where,-6} {seen} of {_hudScreen.Rows.Count} on screen, "
            + $"{lowest}..{highest}, {ScreenLayout.MenuLines(LayoutHeight)} lines at a time");
        Console.Out.Flush();
    }

    /// <summary>What the capped list showed, at the top and at the bottom.</summary>
    private readonly Dictionary<string, (int Seen, int Lowest, int Highest, int Total)> _uiRows = [];

    /// <summary>
    /// The labels each measured screen was actually carrying.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Because a row count is not a claim about what is on the row.</b> "Folding the world list
    /// out replaces the menu rather than adding to it" was checked as <c>listRows != menuRows</c>,
    /// which is true of the wrong screen as often as the right one — it went red the day a machine
    /// happened to have a number of worlds that made the two counts equal, while the screen was
    /// perfectly correct. What the claim actually is: the list has worlds on it and does not still
    /// have "quit" on it.
    /// </remarks>
    private readonly Dictionary<string, List<string>> _uiRowLabels = [];

    /// <summary>
    /// Reads the pixel where the recipe book's page sits, whether or not it is out.
    /// </summary>
    /// <remarks>
    /// Deliberately the same point both times, computed from where the book <em>would</em> be —
    /// which is why the panel's own origin is not used: it moves when the book appears, and a
    /// sample that moves with what it is measuring cannot tell that anything happened.
    /// </remarks>
    private unsafe void SampleWell(Vector2D<int> size, string what)
    {
        var scale = HudRenderer.ScaleFor(size.Y);
        var zoom = ScreenLayout.ZoomFor(size.X / scale, size.Y / scale, bookOut: true);

        // Where the pair sits when the book is out, worked out from the window rather than read
        // off a layout that may currently describe a panel on its own.
        var pairLeft = MathF.Round(
            (size.X / scale - (ScreenLayout.PanelWidth + ScreenLayout.BookWidth + ScreenLayout.BookGap) * zoom) * 0.5f);
        var top = MathF.Round((size.Y / scale - ScreenLayout.PanelHeight * zoom) * 0.5f);

        var wx = (int)((pairLeft + (ScreenLayout.BookWell.X + ScreenLayout.BookWell.W * 0.5f) * zoom) * scale);
        var wy = (int)((top + (ScreenLayout.BookWell.Y + ScreenLayout.BookWell.H * 0.5f) * zoom) * scale);

        Span<byte> px = stackalloc byte[4];
        fixed (byte* p = px)
            _gl.ReadPixels(wx, size.Y - 1 - wy, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);

        _uiSamples[what] = (px[0], px[1], px[2]);

        Console.WriteLine($"ui-check    {what,-17} rgb {px[0],3} {px[1],3} {px[2],3}");
        Console.Out.Flush();
    }

    /// <summary>
    /// That the book folds out beside the panel rather than over it, and lays a recipe out.
    /// </summary>
    /// <remarks>
    /// Two things worth insisting on and neither is visible from a screenshot. The book must sit
    /// clear of the panel — a book overlapping the pockets is a book covering the thing it is
    /// there to fill — and clicking a recipe must actually move ingredients out of the pockets and
    /// into the grid, which is the whole reason the two are on one screen.
    /// </remarks>
    private void ProbeBook()
    {
        var entries = 0;
        var overlapping = 0;

        foreach (var zone in _layout.Zones)
        {
            if (zone.Kind != ZoneKind.Recipe) continue;
            entries++;

            // The book hangs to the LEFT. Anything of it reaching past the panel's own left edge
            // is drawn over the pockets it exists to fill.
            if (zone.X + zone.W > _layout.OriginX) overlapping++;
        }

        // Something to pay with. A new world starts with empty pockets, so on an empty inventory
        // nothing is payable and the whole path below would be skipped without ever running —
        // a check that quietly measures nothing, which is the failure this project keeps finding.
        _inventory.Add(new ItemStack(_items.ByName("driftoak_log").Id, 8));
        RefreshScreen();

        var payable = -1;
        for (var i = 0; i < _hudScreen.Payable.Count; i++)
            if (_hudScreen.Payable[i]) { payable = i; break; }

        var laid = 0;
        if (payable >= 0)
        {
            _hudScreen.Selected = payable;
            LayOut(payable);

            for (var i = 0; i < (_hudScreen.Grid?.Cells ?? 0); i++)
                if (!(_hudScreen.Grid?[i] ?? ItemStack.Empty).IsEmpty) laid++;
        }

        // And what the arrangement makes, which is the point of laying it out at all.
        var makes = _hudScreen.Grid?.Result ?? ItemStack.Empty;

        // ⛳ AND THAT TAKING IT LAYS THE SAME RECIPE OUT AGAIN. Reported from the game: making ten
        // doors was ten trips back to the book, because taking the result spent the arrangement and
        // left the grid empty. Taken through the real click path rather than by calling LayOut a
        // second time, or this would be checking that a function called twice runs twice.
        //
        // ⚠ The pair is "it refilled" and "it stopped when the logs ran out". Refilling for ever
        // would be items out of nowhere, and that reads as generosity rather than as a fault.
        var again = 0;
        var stopped = false;

        if (payable >= 0 && _layout.Find(SlotRole.Result, 0) is { } well)
        {
            ClickSlot(well, MouseButton.Left, many: false);
            _hudScreen.Carried = ItemStack.Empty;

            for (var i = 0; i < (_hudScreen.Grid?.Cells ?? 0); i++)
                if (!(_hudScreen.Grid?[i] ?? ItemStack.Empty).IsEmpty) again++;

            // Eight logs, one a craft, so the ninth take has nothing left to lay out.
            for (var take = 0; take < 12; take++)
            {
                ClickSlot(well, MouseButton.Left, many: false);
                _hudScreen.Carried = ItemStack.Empty;
            }

            stopped = (_hudScreen.Grid?.Result ?? ItemStack.Empty).IsEmpty;
        }

        _uiBook = (entries, overlapping, laid, payable, _shown.Count, !makes.IsEmpty, again, stopped);

        Console.WriteLine(
            $"ui-check    book       {entries} recipes on the page of {_shown.Count}, "
            + $"{overlapping} over the panel; laying out '{(payable >= 0 ? _shown[payable].Name : "nothing")}' "
            + $"filled {laid} squares and makes "
            + (makes.IsEmpty ? "nothing" : $"{makes.Count} {_items[makes.Item].Name}")
            + $"; taking it laid {again} squares out again and ran out after "
            + (stopped ? "the logs did" : "NOTHING — it is still making them"));
        Console.Out.Flush();

        // Put it back the way it was found, so the screens after this one are measured on the same
        // empty pockets every other check here assumes.
        foreach (var left in _hudScreen.Grid?.Empty(_inventory) ?? []) _ = left;
        _inventory.Clear();
    }

    private (int Entries, int Overlapping, int Laid, int Payable, int Total, bool Makes,
             int Again, bool Stopped) _uiBook;

    /// <summary>
    /// Puts the pointer on a square and asks the layout what it is over.
    /// </summary>
    /// <remarks>
    /// The other half of the round trip the audit walks. That one proves the arithmetic; this one
    /// proves the arithmetic is what the running game is actually using — the layout built by the
    /// renderer this frame, on this window, at this zoom, hit-tested through the same call a click
    /// goes through. A layout that is correct in Core and never reached by the host would pass every
    /// check in the audit and still be a screen nothing can be clicked on.
    /// </remarks>
    private void ProbeSquares()
    {
        var hits = 0;
        var misses = new List<string>();

        foreach (var zone in _layout.Zones)
        {
            if (zone.Kind != ZoneKind.Slot) continue;

            _hudScreen.Pointer = new Vector2(zone.CentreX, zone.CentreY);
            var over = _layout.At(_hudScreen.Pointer.X, _hudScreen.Pointer.Y);

            if (over is { } landed && landed.Role == zone.Role && landed.Index == zone.Index) hits++;
            else misses.Add($"{zone.Role} {zone.Index}");
        }

        _uiProbes[_hudScreen.Kind.ToString()] = (hits, misses.Count);

        Console.WriteLine(
            $"ui-check    {_hudScreen.Kind,-10} {hits} squares answered for their own middle"
            + (misses.Count == 0 ? "" : $", {misses.Count} did not: {string.Join(", ", misses.Take(3))}"));
        Console.Out.Flush();
    }

    /// <summary>What the running game's own layout answered, per screen.</summary>
    private readonly Dictionary<string, (int Hits, int Misses)> _uiProbes = [];

    /// <summary>
    /// Reads the pixel where the figure's chest ought to be, and one just outside the window.
    /// </summary>
    /// <remarks>
    /// The figure is a texture patch on a quad, and every way that goes wrong looks the same from
    /// the outside — a sheet that never uploaded, uv normalised against the stored size instead of
    /// sixty four, a batch flushed against the wrong binding — and all of them leave the window
    /// empty rather than raising anything. The inset it sits in is a known dark colour, so "the
    /// chest is not the inset" is a real question with a real answer. Sampled with a control just
    /// outside the figure's own box, which must still BE the inset.
    /// </remarks>
    private unsafe void SampleFigure(Vector2D<int> size)
    {
        var scale = HudRenderer.ScaleFor(size.Y);

        // Panel coordinates: the doll stands centred in the figure window, and its torso spans
        // model y 12..24, which lands about a third of the way down. Asked of the layout rather
        // than worked out here, so the sample follows the panel if it ever moves.
        (byte R, byte G, byte B) Read(float panelX, float panelY)
        {
            var wx = (int)((_layout.OriginX + panelX * _layout.Zoom) * scale);
            var wy = (int)((_layout.OriginY + panelY * _layout.Zoom) * scale);

            Span<byte> px = stackalloc byte[4];
            fixed (byte* p = px)
                _gl.ReadPixels(wx, size.Y - 1 - wy, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);

            return (px[0], px[1], px[2]);
        }

        var chest = Read(ScreenLayout.Figure.X + 25f, ScreenLayout.Figure.Y + 32f);

        // ⛔ The bare corner, not the foot of the window. The figure HOLDS whatever the player
        // holds, drawn by its hand near the lower-left — so a probe down there read the held
        // item's icon and called the inset light on a correct screen the moment the ui-check's
        // player picked up a block. The top-left corner is bare by the window's own geometry:
        // the cut-out stands centred with a nine-unit margin and nothing else is drawn that high.
        var backdrop = Read(ScreenLayout.Figure.X + 3f, ScreenLayout.Figure.Y + 4f);

        _uiSamples["figure"] = chest;
        _uiSamples["figure backdrop"] = backdrop;

        Console.WriteLine(
            $"ui-check    figure     chest rgb {chest.R,3} {chest.G,3} {chest.B,3}   "
            + $"its own backdrop rgb {backdrop.R,3} {backdrop.G,3} {backdrop.B,3}");
        Console.Out.Flush();
    }

    /// <summary>
    /// Reads a filled chest slot and an empty one beside it.
    /// </summary>
    /// <remarks>
    /// ⚠ The pair is the check. A chest's twenty seven squares are drawn from the panel whether or
    /// not a single one of them reaches the chest, so "there is a well there" says nothing at all —
    /// what has to differ is the slot holding something against the slot holding nothing, which is
    /// the one comparison that cannot pass on a screen that never read the chest.
    /// </remarks>
    private unsafe void SampleStored(Vector2D<int> size)
    {
        var scale = HudRenderer.ScaleFor(size.Y);

        (byte R, byte G, byte B) ReadSlot(int index)
        {
            if (_layout.Find(SlotRole.Stored, index) is not { } zone) return default;

            var wx = (int)(zone.CentreX * scale);
            var wy = (int)(zone.CentreY * scale);

            Span<byte> px = stackalloc byte[4];
            fixed (byte* p = px)
                _gl.ReadPixels(wx, size.Y - 1 - wy, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);

            return (px[0], px[1], px[2]);
        }

        var filled = ReadSlot(0);
        var empty = ReadSlot(Chest.Slots - 1);

        _uiSamples["chest slot"] = filled;
        _uiSamples["chest empty"] = empty;

        Console.WriteLine(
            $"ui-check    chest      a full slot reads rgb {filled.R,3} {filled.G,3} {filled.B,3}, "
            + $"an empty one {empty.R,3} {empty.G,3} {empty.B,3}");
        Console.Out.Flush();
    }

    /// <summary>What the stonecutter offered, and whether the pick reads differently from the rest.</summary>
    private (int Offers, bool Picked, bool Makes) _uiCuts;

    /// <summary>
    /// Asks the stonecutter what a rock is offering, and that picking one changes what it makes.
    /// </summary>
    /// <remarks>
    /// The list is the station. A stonecutter that draws two wells and no offers is a stonecutter
    /// nothing can ever be taken out of, and the panel looks identical either way — so the count is
    /// asserted, and so is the result slot actually holding the thing the pick names.
    /// </remarks>
    private void ProbeCuts()
    {
        var offers = 0;
        foreach (var zone in _layout.Zones)
            if (zone.Kind == ZoneKind.Recipe && zone.Index < _hudScreen.Cuts.Count) offers++;

        var picked = _hudScreen.Cut >= 0 && _hudScreen.Cut < _hudScreen.Cuts.Count;
        var makes = picked && !_hudScreen.Cuts[_hudScreen.Cut].Result.IsEmpty;

        _uiCuts = (offers, picked, makes);

        Console.WriteLine(
            $"ui-check    cutter     stone offers {offers} cuts, picked "
            + (picked ? $"'{_hudScreen.Cuts[_hudScreen.Cut].Name}'" : "nothing")
            + $", which makes {(makes ? _items[_hudScreen.Cuts[_hudScreen.Cut].Result.Item].Name : "nothing")}");
        Console.Out.Flush();
    }

    /// <summary>
    /// Reads the title's own timber, and the gap inside a letter beside it.
    /// </summary>
    /// <remarks>
    /// <para>⚠ The pair is the check, for the same reason the chest's is. A title drawn in the wrong
    /// place or not at all leaves the backdrop showing, and a backdrop is a colour — so what has to
    /// be true is that a cell the word FILLS reads differently from a cell the word LEAVES EMPTY.
    /// </para>
    /// <para>⛔ <b>Both points come from the RENDERER now, and that is a fix rather than a tidy-up.</b>
    /// This used to rebuild the letter grid from the same constants the renderer lays it out from,
    /// which sounds equivalent and is not: <b>every letter bobs and leans on its own phase</b>, up to
    /// a little over half a cell, and the sample knew nothing about either. It read past the timber
    /// it named, onto the backdrop, and passed for as long as whatever was behind the word happened
    /// to be brownish — then went red on a build where nothing about the title had changed at all
    /// and the world behind it was darker. Third time this project has been caught sampling where a
    /// thing usually is; see <see cref="HudScreen.TipBox"/> for the second.</para>
    /// </remarks>
    private unsafe void SampleTitle(Vector2D<int> size)
    {
        var scale = HudRenderer.ScaleFor(size.Y);
        var cell = HudRenderer.TitleCell(size.X / scale);

        (byte R, byte G, byte B) At(Vector2 at)
        {
            var px = (int)(at.X * scale);
            var py = (int)(at.Y * scale);

            Span<byte> pixel = stackalloc byte[4];
            fixed (byte* p = pixel)
                _gl.ReadPixels(px, size.Y - 1 - py, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);

            return (pixel[0], pixel[1], pixel[2]);
        }

        // Where the word was actually drawn last frame — with the bob and the lean already in it.
        var ink = _hudScreen.TitleInk;
        var air = _hudScreen.TitleGap;

        _uiSamples["title wood"] = ink.X >= 0f ? At(ink) : default;
        _uiSamples["title gap"] = air.X >= 0f ? At(air) : default;

        var wood = _uiSamples["title wood"];
        var gap = _uiSamples["title gap"];

        Console.WriteLine(
            $"ui-check    title      {TitleArt.Cells} cells at {cell:F0}, timber rgb {wood.R,3} {wood.G,3} "
            + $"{wood.B,3} at {ink.X:F0},{ink.Y:F0} against a gap of {gap.R,3} {gap.G,3} {gap.B,3} "
            + $"at {air.X:F0},{air.Y:F0}");
        Console.Out.Flush();
    }

    /// <summary>What each sample read, so the run can judge itself rather than only report.</summary>
    private readonly Dictionary<string, (byte R, byte G, byte B)> _uiSamples = [];

    /// <summary>True when the check ran and something it insists on was not true.</summary>
    public bool UiCheckFailed { get; private set; }

    /// <summary>
    /// Judges the samples. An instrument that only prints is one somebody has to remember to read.
    /// </summary>
    private void JudgeUi()
    {
        var faults = new List<string>();

        // ⛳ THE PACK CHOOSER'S INTERFACE, and it is judged here because this is the client's gate
        // and --audit cannot see a Win32 anything from Core. It builds a real chooser and works it
        // without showing one, so it costs a few milliseconds and needs nobody to click. The thing
        // it catches — a wrong interface id, or a method missing from a COM vtable — compiles,
        // starts, and looks exactly like a button somebody forgot to wire up.
        faults.AddRange(NativeFilePicker.SelfTest(out var chooser));
        Console.WriteLine($"ui-check    file chooser  {chooser}");

        (byte R, byte G, byte B) Read(string key) =>
            _uiSamples.TryGetValue(key, out var v) ? v : default;

        var bare = Read("no screen");
        var items = Read("items");
        var book = Read("book");
        var bench = Read("bench");
        var chestPanel = Read("chest");
        var cutter = Read("cutter");
        var game = Read("game");
        var world = Read("no screen corner");

        // The crosshair sits at the exact middle of an untouched frame and is nearly white. If the
        // middle reads as sky, the overlay is not reaching the screen at all — which is the fault
        // this whole instrument was built for, and it looked like working software from every angle
        // except this one.
        if (bare.R < 200 || bare.G < 200 || bare.B < 200)
            faults.Add($"no crosshair at the centre of a plain frame — read {bare.R} {bare.G} {bare.B}");

        // A screen darkens what is behind it, so the middle must not still be whatever it was.
        if (items == bare) faults.Add("opening the items screen changed nothing on screen");
        if (bench == bare) faults.Add("opening a bench changed nothing on screen");
        if (chestPanel == bare) faults.Add("opening a chest changed nothing on screen");
        if (game == bare) faults.Add("opening the game screen changed nothing on screen");

        // A chest slot with something in it must not read as one without. Both are wells drawn from
        // the panel, so this is the only sample that says the screen is reading the chest at all.
        var storedFull = Read("chest slot");
        var storedEmpty = Read("chest empty");

        if (storedFull == storedEmpty)
            faults.Add($"a chest slot holding seven planks reads the same as an empty one, "
                     + $"{storedFull.R} {storedFull.G} {storedFull.B}");

        if (cutter == bare) faults.Add("opening a stonecutter changed nothing on screen");

        // ⛔ THE TOOLTIP AND THE GUTTER BESIDE IT, and the second is the whole check. The pointer is
        // put on a pocket holding a pickaxe, then two units left into the gap between that square
        // and the next, where the layout has no zone at all. "A box appeared" is only worth
        // believing alongside "and none appeared where there is nothing".
        if (!_uiTipDrawn.GetValueOrDefault("tip on a slot"))
            faults.Add("hovering a pocket holding a stone pickaxe drew no tooltip");

        if (_uiTipDrawn.GetValueOrDefault("tip on a gutter"))
            faults.Add("a tooltip was drawn over the bare panel between two squares");

        // ⚠ And it has to be the DARK box the palette names rather than merely something. A tooltip
        // drawn in the panel's own tone is one nobody can see, and a box that was laid out proves
        // nothing about whether it can be read.
        var onSlot = Read("tip on a slot");
        var panel = Read("items");

        if (_uiTipDrawn.GetValueOrDefault("tip on a slot") && onSlot.R >= panel.R)
            faults.Add($"the tooltip is no darker than the panel it sits on, "
                     + $"{onSlot.R} against {panel.R}");

        // ⛳ And grey, per the rule the whole interface is drawn to: no channel may stray from the
        // others. A cast of two hundredths is invisible written down and is exactly how a set of
        // panels stops reading as one thing.
        else if (Math.Abs(onSlot.R - onSlot.B) > 6 || Math.Abs(onSlot.R - onSlot.G) > 6)
            faults.Add($"the tooltip has a colour cast, {onSlot.R} {onSlot.G} {onSlot.B}");

        // The title has to be there, and has to be made of something. A cell the word fills reading
        // the same as a cell it leaves empty is a title that is not drawn, drawn somewhere else, or
        // drawn in one flat colour — and all three look identical from every other angle.
        var timber = Read("title wood");
        var behindIt = Read("title gap");

        if (timber == behindIt)
            faults.Add($"the title's timber reads the same as the gap inside its own letters, "
                     + $"{timber.R} {timber.G} {timber.B}");
        else if (timber.R <= timber.B)
            faults.Add($"the title reads {timber.R} {timber.G} {timber.B}, which is not wood");

        foreach (var fault in TitleArt.Validate()) faults.Add(fault);

        // ⛔ The one assumption RawInput rests on, checked rather than believed. If the two
        // libraries' key numbers ever stop agreeing, every key in the game silently becomes a
        // different key — not a crash, a game where W walks left.
        foreach (var fault in RawInput.KeyNumbersMatch()) faults.Add(fault);

        // The station IS the list. Two wells and no offers is a stonecutter nothing comes out of,
        // and it draws exactly the same as one that works.
        if (_uiCuts.Offers == 0) faults.Add("a stonecutter offered nothing at all for a block of stone");
        if (!_uiCuts.Picked) faults.Add("a stonecutter with offers on it had none of them picked");
        if (!_uiCuts.Makes) faults.Add("the cut a stonecutter had picked makes nothing");

        _ = book;

        // The book: read where the book's own well is, before and after folding it out. The middle
        // of the window is the wrong place to ask — the panel shifts right when the book appears
        // but the middle stays panel either way, so that sample said "nothing changed" about a book
        // that was drawing perfectly. Ask where the thing actually is.
        var wellBefore = Read("book well before");
        var wellAfter = Read("book well after");

        if (wellBefore == wellAfter)
            faults.Add($"nothing appeared where the book goes — {wellAfter.R} {wellAfter.G} {wellAfter.B} either way");

        if (_uiBook.Entries == 0) faults.Add($"the book drew no recipes at all, of {_uiBook.Total}");
        if (_uiBook.Overlapping > 0) faults.Add($"{_uiBook.Overlapping} of the book's recipes are drawn over the panel");

        // And that it does the one thing it is for. Given something to pay with, a payable recipe
        // must exist, laying it out must fill squares, and those squares must make something.
        if (_uiBook.Payable < 0) faults.Add("nothing was payable with eight logs in the pockets");
        else if (_uiBook.Laid == 0) faults.Add("laying a recipe out of the book filled no squares");
        else if (!_uiBook.Makes) faults.Add("the squares the book laid out make nothing");

        // ⛳ And that a second one is another click on the result rather than another trip to the
        // book. Reported from the game: making ten doors was ten journeys back to the recipe.
        else if (_uiBook.Again == 0)
            faults.Add(
                "taking the result left the grid empty, so making a second one means going back to "
                + "the book for the same recipe again");

        // The other half. A grid that refills for ever is items out of nowhere, and it would read
        // as the feature working rather than as the fault it is.
        else if (!_uiBook.Stopped)
            faults.Add("the grid kept laying the recipe out after the pockets were empty");

        // The container panel is centred, so the middle of the window is inside it — and it is drawn
        // in the same neutral grey the options are. A middle that still reads dark is the backdrop
        // with no panel on it, which is what a panel laid out off the edge looks like from here.
        foreach (var (name, read) in
                 new[] { ("items", items), ("bench", bench), ("chest", chestPanel), ("cutter", cutter) })
        {
            if (read.R < 40) faults.Add($"the {name} panel's middle reads {read.R} {read.G} {read.B}, too dark for a panel");

            var cast = Math.Max(read.R, Math.Max(read.G, read.B)) - Math.Min(read.R, Math.Min(read.G, read.B));
            if (cast > 12) faults.Add($"the {name} panel reads {read.R} {read.G} {read.B}, which is not grey");
        }

        // The figure. Its window is a known dark inset, so a chest that still reads as the inset is
        // a skin patch that never arrived — and the control beside it must still read as the inset,
        // or the sample is not where it says it is and the judgement above means nothing.
        var chest = Read("figure");
        var behind = Read("figure backdrop");

        if (chest == behind)
            faults.Add($"the figure's chest reads the same as its own backdrop, {chest.R} {chest.G} {chest.B}");

        if (behind.R > 70 || behind.G > 70 || behind.B > 80)
            faults.Add($"the figure's window is not the dark inset it is drawn in — read {behind.R} {behind.G} {behind.B}");

        // The capped list. A controls tab with twenty eight rows in it must show a dozen and no
        // more, must start at the top, and must scroll all the way to the last one — the row that
        // cannot be reached is the whole reason a cap needs a scrollbar rather than just a cap.
        var lines = ScreenLayout.MenuLines(LayoutHeight);

        if (_uiRows.TryGetValue("top", out var atTop))
        {
            if (atTop.Total <= lines) faults.Add($"the controls tab is only {atTop.Total} rows, so nothing here was tested");
            if (atTop.Seen > lines) faults.Add($"{atTop.Seen} rows are on screen where {lines} fit");
            if (atTop.Lowest > 1) faults.Add($"the list opens at row {atTop.Lowest} rather than the top");
        }
        else
        {
            faults.Add("the controls tab was never measured at the top");
        }

        if (_uiRows.TryGetValue("bottom", out var atEnd))
        {
            if (atEnd.Highest < atEnd.Total - 1)
                faults.Add($"scrolled to the end the list stops at row {atEnd.Highest} of {atEnd.Total - 1}");
            if (atEnd.Seen > lines) faults.Add($"{atEnd.Seen} rows are on screen at the end where {lines} fit");
        }
        else
        {
            faults.Add("the controls tab was never measured at the end");
        }

        // The list of worlds. Two were planted, so the tab has to be five rows about this world plus
        // two headings plus one row per world, and every one of them has to be a row the layout knows
        // about — a list that draws its headings and drops its entries is the failure worth naming,
        // because a screen with the right shape and no worlds on it reads as "no worlds".
        var saves = Read("saves");
        if (saves == bare) faults.Add("opening the saves tab changed nothing on screen");

        if (_uiRows.TryGetValue("saves", out var savesRows))
        {
            // Written out as numbers rather than recomputed from the builder, which would be the
            // builder agreeing with itself. Two headings, four lines about this world, and one row
            // per planted world; a tab that drew its own furniture and dropped the list comes to
            // six and four, which is a screen that reads as "no worlds" and is the fault worth
            // naming.
            const int Rows = 8;
            const int Selectable = 6;   // the two headings are read-outs, not rows to land on

            if (savesRows.Total != Rows)
                faults.Add(
                    $"the saves tab built {savesRows.Total} rows where two headings, four lines "
                    + $"about this world and two worlds is {Rows}");

            if (savesRows.Seen != Selectable)
                faults.Add(
                    $"{savesRows.Seen} of the saves tab's rows reached the layout where {Selectable} "
                    + $"should have, and all of them fit in the {lines} lines there are");
        }
        else
        {
            faults.Add("the saves tab was never measured");
        }

        // The menu. Four choices and a heading, and folding the list out has to replace them with
        // the worlds rather than adding to them — a menu that grew instead of swapping would keep
        // "quit" on screen under a list of worlds, and enter on it would still quit.
        var start = Read("start");
        if (start == bare) faults.Add("the start menu changed nothing on screen");

        if (_uiRows.TryGetValue("start", out var menu))
        {
            // The name, then: carry on / make another world / seed / open a world / options / quit.
            const int Choices = 7;
            const int Selectable = 6;

            if (menu.Total != Choices)
                faults.Add($"the start menu built {menu.Total} rows where it offers {Choices}");
            if (menu.Seen != Selectable)
                faults.Add($"{menu.Seen} of the menu's rows can be landed on where {Selectable} should be");
        }
        else
        {
            faults.Add("the start menu was never measured");
        }

        // ⛔ THE ONE THAT MATTERS, AND THE ONE THAT WAS MISSING. Two worlds were written to disk and
        // the list in memory emptied before the menu was opened, so the only way these names can be
        // in it is if the menu went and read the folder. It did not, and the first thing a player
        // saw after saving and closing the game was "none saved yet".
        if (!_foundPlanted)
            faults.Add(
                "the menu did not find the worlds that are on disk — it is showing whatever was "
                + "last left in its list rather than reading the folder when it opens");

        // ⛔ THE SILENT HALF. A file that will not read was dropped from the list with no word on
        // the screen, in the log, or anywhere else — so "there are no saved worlds" and "there is a
        // saved world I cannot open" were the same four words. The list has to name it.
        if (!_reportedBroken)
            faults.Add(
                $"a file in the saves folder that is not a world ('{BrokenWorld}.dws') was left out "
                + "of the list without a word — an unreadable world reads to a player as no world");

        if (!_goodSurvivedTheBad)
            faults.Add(
                "a file in the saves folder that is not a world took the readable worlds with it — "
                + "one bad file must cost its own row and nothing else");

        // ⛳ THE PAGE TURNS. The same patch of the same square at two points on the clock, compared
        // pixel for pixel — the only way a still frame can say anything about a moving one, and a
        // page that had quietly stopped turning looks exactly like one that never did.
        //
        // ⛳ THE BAR IS SET FROM BOTH ENDS, MEASURED, rather than picked. A square whose block is
        // held at one angle reads 0% — the threshold is eight levels, so nothing at all moves. The
        // quietest square that genuinely IS turning is a torch at 12%: its picture is a narrow
        // upright stick on a transparent tile, so most of the patch is well and stays well however
        // far round it goes. A full cube reads 37 to 46. Five per cent is clear of one end and a
        // long way under the other.
        if (_turnMoved < 0f) faults.Add("the turning page was never compared at three moments");
        else if (_turnMoved < 0.05f)
            faults.Add(
                $"the stillest square on the page changed by only {_turnMoved * 100f:F0}% anywhere in a "
                + "whole turn, so something on it is not turning - the line above says which");

        // ⛳ A SLAB IS HALF AS TALL AS THE BLOCK IT IS CUT FROM, SO IT REACHES LESS FAR UP ITS
        // SQUARE. Both wear the same tile, so nothing but the shape can move this number, and the
        // block beside it is the control — it is a cube either way, so it reads the same in every
        // build and any change here is the slab's.
        //
        // ⛔ THE FIRST VERSION OF THIS CHECK PASSED THE BROKEN BUILD. It compared the two whole
        // squares and asked that they differ, which they did — a flat tile fills a square evenly and
        // reads 112, three shaded faces read 85 — for a reason with nothing to do with the claim.
        // Measured: with the shape drawn the slab's top strip is 62 against the block's 73; with it
        // put back to a flat tile it is 102, which is brighter than the block rather than darker.
        if (_uiSamples.TryGetValue("icon block top", out var blockTop)
            && _uiSamples.TryGetValue("icon slab top", out var slabTop))
        {
            if (slabTop.G >= blockTop.G)
                faults.Add(
                    $"the top of a stone slab's square reads {slabTop.G} against a whole block's "
                    + $"{blockTop.G} — a slab is half as tall and cannot reach as far up it, so a "
                    + "shaped block is being drawn as one flat tile of the rock it is made of");
        }
        else
        {
            faults.Add("the block-against-slab pair was never sampled");
        }

        // ⛔ THE PAIR, AND WITHOUT IT NEITHER HALF MEANS ANYTHING. An empty box and a box with a
        // word in it are the same sunken frame in the same place, so a check that only asked
        // whether pixels arrived would pass a box that refuses every character. What is being
        // asked is that typing CHANGED it — and the count is asked separately, because the same
        // sample would also stay put if the typing went somewhere else entirely.
        if (_typedSeed != "driftwood")
            faults.Add(
                $"typing 'driftwood' into the seed box left '{_typedSeed}' — the characters are not "
                + "reaching the field the screen is showing");

        if (_uiSamples.TryGetValue("seed empty", out var blank)
            && _uiSamples.TryGetValue("seed typed", out var written))
        {
            if (blank == written)
                faults.Add(
                    "the seed box read the same before and after a word was typed into it, so what "
                    + "is on screen is not what the field is holding");
        }
        else
        {
            faults.Add("the seed box was never sampled");
        }

        if (_uiRows.TryGetValue("start list", out var folded))
        {
            // A heading, at least the two planted, and the way back. At least, because the machine
            // running this may have worlds of its own and they are not the check's to care about.
            const int Least = 4;

            if (folded.Total < Least)
                faults.Add(
                    $"the menu's list of worlds built {folded.Total} rows where a heading, the two "
                    + $"worlds put on disk for it and a way back is at least {Least}");

            // ⛔ What is on the rows, not how many there are. A count comparison said "it replaced
            // rather than added" and went red on a machine whose own worlds happened to make the two
            // totals equal — a number that varies with the folder is not a claim about the screen.
            var listed = _uiRowLabels.GetValueOrDefault("start list", []);
            var buttons = _uiRowLabels.GetValueOrDefault("start", []);

            // The control. Without it "the list does not offer quit" passes on a list of nothing.
            if (!buttons.Contains("quit"))
                faults.Add("the menu itself was not carrying 'quit', so the pair below compares nothing");

            foreach (var button in new[] { "quit", "options", "make another world" })
                if (listed.Contains(button))
                    faults.Add(
                        $"the menu's list of worlds still has '{button}' on it — folding the list "
                        + "out added to the menu instead of replacing it, and enter on that row acts");

            foreach (var name in CheckWorlds)
                if (!listed.Contains(name))
                    faults.Add($"'{name}' is on disk and the menu's list of worlds does not name it");
        }
        else
        {
            faults.Add("the menu's list of worlds was never measured");
        }

        // Belt and braces: a check that failed before reaching the removal must not leave two
        // worlds nobody made sitting in somebody's list.
        RemovePlantedWorlds();

        // And that the running game's own layout answers for its own squares. Every one of them.
        foreach (var (screen, probe) in _uiProbes)
        {
            if (probe.Misses > 0) faults.Add($"{probe.Misses} squares on the {screen} screen did not answer for themselves");

            // ⚠ Every container panel carries the player's own pockets and then adds its own
            // squares, so that is the claim: all the pockets, and at least one thing of its own.
            // It was a flat forty, which is a number no panel is actually made of — a stonecutter
            // has thirty-eight and was failing for being small rather than for being wrong.
            if (probe.Hits < Inventory.Slots + 1)
                faults.Add($"the {screen} screen laid out {probe.Hits} squares, fewer than the "
                         + $"{Inventory.Slots} pockets and one of its own");
        }

        // And the options panel is neutral grey by design, so its middle has no colour cast.
        var spread = Math.Max(game.R, Math.Max(game.G, game.B)) - Math.Min(game.R, Math.Min(game.G, game.B));
        if (spread > 12)
            faults.Add($"the options panel reads {game.R} {game.G} {game.B}, which is not the grey it is drawn in");

        // ⛔ THE CONTROL: every middle sampled above has to be a panel and every corner has to be
        // the world behind it, or nothing here is comparing two different things and every
        // judgement is on the same pixels.
        //
        // ⚠ It used to ask whether the corner of a plain frame was SKY, and that was a race rather
        // than a check. Whether a hillside has arrived in the top-left of the frame sixty frames in
        // is up to the streaming threads, so it passed *because the world had not finished
        // loading* and went red the moment this machine got there first — a check whose green is
        // the incomplete state. Worse, it read one pixel at frame sixty against panels sampled two
        // hundred frames later, so the two were never even in the same picture.
        //
        // ⛳ The corner and the middle are read from the SAME FRAME, so this cannot race at all,
        // and it says the load-bearing thing directly: a panel that reached the corner, or a
        // ReadPixels that answered the same for everything, is what makes the samples worthless.
        // What the corner happens to be a picture of — sky, a hill, a tree, night — is not the
        // claim and never was.
        foreach (var (screen, middle) in (ReadOnlySpan<(string, (byte R, byte G, byte B))>)
                 [("items", items), ("book", book), ("bench", bench), ("chest", chestPanel),
                  ("cutter", cutter), ("game", game)])
        {
            var outside = Read($"{screen} corner");
            if (middle != outside) continue;

            faults.Add(
                $"the {screen} screen reads {middle.R} {middle.G} {middle.B} in its middle and the same in "
                + "the corner of the frame — the panel covers what it is being measured against");
        }

        // And on a plain frame the two are the crosshair and the world, which are never the same.
        if (world == bare)
            faults.Add($"a plain frame's corner reads the same as its crosshair, {world.R} {world.G} {world.B}");

        if (!_waterMoved)
            faults.Add("the water layer on the card did not change, or the read cannot tell layers apart");

        // ⛔ The bar a user had to tell us about. Half a lungful has to put bubbles on the screen,
        // and "the tile is fine" is not that claim — see the note on frame 50.
        if (_breathBubbles <= 0)
            faults.Add("half a lungful of air drew no bubbles at all, so the breath bar is invisible");
        else
            Console.WriteLine(
                $"ui-check    breath     {_breathBubbles} bubbles on half a lungful, first at "
                + $"{_breathAt.X:F0},{_breathAt.Y:F0}");

        // ⛔⛔ AND THE SAME QUESTION ASKED OF THE SCREEN. The line above is the renderer counting its
        // own quads, which is exactly the claim that was written down as proof and was not — see
        // JudgeBreath. A check that could not take its measurement is a fault, not a pass.
        if (_breathPixels < 0)
            faults.Add("the breath bar was never measured against the framebuffer at all");
        else if (_breathNoise > 0)
            faults.Add(
                $"the overlay drawn twice from the same vitals differed by {_breathNoise} pixels, so "
                + "the framebuffer read is not repeatable and the breath measurement means nothing");
        else if (_breathPixels <= 0)
            faults.Add(
                "taking the air away changed nothing whatever on the screen, so the bubbles are "
                + "being built and never drawn");

        // ⛔⛔ THE FIRE'S BOOK, reported by the user as "I'm not seeing any recipes for food when i
        // look in the furnace" — and there was no list at all. Three claims, because the first two
        // were already satisfiable while nothing appeared: a page exists, it is laid out, and it
        // reached the card.
        if (_uiFire.Page < 0)
            faults.Add("the furnace's recipe book was never measured at all");
        else if (_uiFire.Page == 0)
            faults.Add("a furnace opened with no recipes beside it, so nothing says what a fire is for");
        else if (_uiFire.Foods == 0)
            faults.Add("a furnace's book names nothing edible, which is the one thing it is looked at for");
        else if (_uiFire.Drawn == 0)
            faults.Add(
                $"{_uiFire.Page} recipes were laid out beside the furnace and not one of them put a "
                + "pixel on the screen");
        else if (!_uiFire.Loaded)
            faults.Add("clicking a recipe in the fire's book put nothing on the fire");

        // ⚠ A COUNTER, and said as one. It proves the campfire's dinner was enumerated, its kind
        // resolved and its transform built without throwing — not that a steak is on the screen.
        // The breath bar next to it is the measurement that earned the stronger word.
        // ⛔ Read where the fire still EXISTS. Taken at the end it read zero and fired, correctly:
        // the air probe seals the player into a box of stone and water and buries the campfire doing
        // it. A check that runs after another one has demolished its subject is measuring the wrong
        // thing, and the honest answer is to read it before rather than to loosen it.
        if (_cookingSeen <= 0)
            faults.Add("a lit campfire with meat on it drew nothing on the fire");

        if (!_fireBookOut)
            faults.Add("a fire opened with its book folded away, which is the silence the book was built to end");

        if (!_fireBookButton)
            faults.Add("the furnace screen has no book button — a player has no way to open the fire's book");

        // The dive, as pixels. Six bubbles measured ~700 changed pixels when this instrument was
        // built and one bubble ~115, so the gates sit far under both — what they refuse is the
        // bar not arriving at all, which three user reports say is what actually happens.
        if (_bubbleAppearPixels is >= 0 and < 200)
            faults.Add($"submerging at full air changed {_bubbleAppearPixels} pixels where the bubbles go — the bar did not appear");

        if (_bubbleDrainPixels is >= 0 and < 60)
            faults.Add($"three seconds under water changed {_bubbleDrainPixels} pixels of the bar — the bubbles did not diminish");

        // ── Air, driven through the client's own loop rather than through a fixture ──────────────
        //
        // ⛔⛔ EVERY DROWNING CHECK IN THIS PROJECT BUILDS ITS OWN POOL AND CALLS PlayerVitals.Update
        // BY HAND. None of them goes through StepVitals, the frame loop, or the client's own dt —
        // which is the shape of check this project has now been caught by three times.
        if (_airTo < PlayerVitals.MaxBreath)
            faults.Add(
                $"breath stopped at {_airTo} of {PlayerVitals.MaxBreath} on dry land after "
                + $"{_airTook:F1}s, so the bubbles never leave the screen");

        if (!_airSubmerged)
            faults.Add("a head sealed inside a box of water was not submerged, so nothing was measured");
        else if (_airDivedTo >= _airDivedFrom)
            faults.Add(
                $"three seconds under water took breath from {_airDivedFrom} to {_airDivedTo} — it is "
                + "not being spent at all");

        // ⛳ AND THE STATE THE USER WAS IN. A body that is not being simulated used to leave the air
        // exactly where it stood: not going down under water, not coming back on land, and the row
        // stuck on screen showing a number that had stopped meaning anything.
        if (_airUnsimulated < PlayerVitals.MaxBreath)
            faults.Add(
                $"with the body not being stepped the air sat at {_airUnsimulated} of "
                + $"{PlayerVitals.MaxBreath} under water, so the bar freezes half spent and stays");

        UiCheckFailed = faults.Count > 0;

        Console.WriteLine();
        if (faults.Count == 0)
        {
            Console.WriteLine(
                "OK  the overlay reaches the screen: crosshair, eight screens, panels in grey, "
                + $"{_uiProbes.Sum(p => p.Value.Hits)} squares answering for their own middles");
        }
        else
        {
            foreach (var fault in faults) Console.WriteLine($"FAULT  {fault}");
        }

        Console.Out.Flush();
    }

    private unsafe void SampleUi(Vector2D<int> size, string what)
    {
        // Where the panel's own body is, and a corner it never reaches. Read in the framebuffer's
        // own coordinates, which count up from the bottom.
        var midX = size.X / 2;
        var midY = size.Y / 2;

        Span<byte> middle = stackalloc byte[4];
        Span<byte> corner = stackalloc byte[4];

        fixed (byte* p = middle)
            _gl.ReadPixels(midX, midY, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        fixed (byte* p = corner)
            _gl.ReadPixels(4, size.Y - 5, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);

        _uiSamples[what] = (middle[0], middle[1], middle[2]);
        _uiSamples[$"{what} corner"] = (corner[0], corner[1], corner[2]);

        Console.WriteLine(
            $"ui-check    {what,-10} middle rgb {middle[0],3} {middle[1],3} {middle[2],3}   "
            + $"top-left corner rgb {corner[0],3} {corner[1],3} {corner[2],3}");
        Console.Out.Flush();
    }

    /// <summary>
    /// Draws whichever part of the player this view mode can see: all of them, or just the arm.
    /// </summary>
    /// <summary>Chunks with water in them, collected as the opaque pass walks them.</summary>
    private readonly List<ChunkMeshGpu> _wetChunks = [];

    /// <summary>How much of what is behind it a lake keeps. Deep enough to read, clear enough to see through.</summary>
    private const float WaterAlpha = 0.72f;

    /// <summary>
    /// The second pass: water, sorted back to front, tested against depth but not writing it.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>Carried since the first spike and it came due the moment water flowed.</b> A flat
    /// lake gets away with being drawn in the opaque pass; a river at half a dozen depths, seen
    /// through itself, does not — that is exactly the case an unsorted opaque pass gets visibly
    /// wrong.</para>
    /// <para><b>Per chunk, not per quad.</b> A sort of every water face every frame is correct and
    /// costs a sort every frame; a chunk is 32 blocks across, so the only case it gets wrong is two
    /// water surfaces in one chunk seen through each other at a shallow angle. Measure before paying
    /// for the other one.</para>
    /// <para>⛔ <b>Depth-tested and not depth-written</b>, or the near surface of a lake hides the far
    /// one and the whole point is lost. Culling comes off too: from under the water you are looking
    /// at the back of every quad above you.</para>
    /// <para>⚠ <b>And every bit of state is put back.</b> Every pass in this renderer restores what
    /// it changed, and this one runs in the middle of the frame with the outline, the cracks, the
    /// particles and the clouds still to come.</para>
    /// </remarks>
    private void DrawWater()
    {
        if (_wetChunks.Count == 0) return;

        var eye = _viewPosition;
        _wetChunks.Sort((a, b) =>
            Vector3.DistanceSquared(b.BoundsMin + HalfChunk, eye)
                .CompareTo(Vector3.DistanceSquared(a.BoundsMin + HalfChunk, eye)));

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.CullFace);

        _chunkShader.SetFloat("uAlpha", WaterAlpha);

        foreach (var mesh in _wetChunks)
        {
            _chunkShader.SetVec3("uChunkOrigin", mesh.Origin);
            _chunkShader.SetVec3Array("uTint", mesh.TintPalette);
            mesh.DrawTranslucent();
        }

        _chunkShader.SetFloat("uAlpha", 1f);

        _gl.Enable(EnableCap.CullFace);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
    }

    private static readonly Vector3 HalfChunk = new(Chunk.Size * 0.5f);

    /// <summary>Blocks from the hand at which a carried light has faded to nothing.</summary>
    /// <remarks>
    /// A little under a placed lantern's own fifteen-block flood: a carried flame swings and
    /// gutters, and reading slightly smaller in the hand than on a hook is what makes hanging
    /// one up still worth doing.
    /// </remarks>
    private const float HeldGlowRange = 12f;

    /// <summary>The carried light's colours this frame, 0..1 per channel. Zero for dark hands.</summary>
    private Vector3 _heldGlow;

    /// <summary>Where the carried light hangs — the chest, whichever view the camera took.</summary>
    private Vector3 HeldGlowPos => _player.Position + new Vector3(0f, 1.4f, 0f);

    /// <summary>What the hands are shedding this frame: the brighter of the two, per channel.</summary>
    /// <remarks>
    /// ⛳ The RULE is Core's (<see cref="HeldGlow"/>: an item sheds what its block emits), so the
    /// audit holds it still; this is only the per-frame read of both hands. The offhand counts,
    /// which is the whole reason to carry a torch there.
    /// </remarks>
    private void ReadHeldGlow()
    {
        if (!_spawned) { _heldGlow = Vector3.Zero; return; }

        var off = _equipment[EquipSlot.Offhand];
        var packed = LightValue.MaxBlock(
            HeldGlow.Of(_inventory.HeldType, _registry),
            HeldGlow.Of(off.IsEmpty ? null : _items[off.Item], _registry));

        _heldGlow = new Vector3(
            LightValue.Red(packed), LightValue.Green(packed), LightValue.Blue(packed))
            / LightValue.Max;
    }

    /// <summary>
    /// The clock the firelight sway runs on: the world's own elapsed time, wrapped.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Wrapped at 300π, and the number is doing work.</b> sin() of a large float steps
    /// visibly once the argument outgrows the mantissa, so the clock has to wrap — and it has to
    /// wrap where every sway frequency completes a whole number of turns, or the flame jumps
    /// once per wrap. At 300π a frequency f has made f·150 full turns, an integer for every
    /// one-decimal frequency the shaders use (2.6 → 390, 7.1 → 1065). Off the world clock, not a
    /// frame counter, for the water animation's own reason.
    /// </remarks>
    private float FlickerClock => (float)(_elapsed % (300.0 * Math.PI));

    private void DrawPlayer(Matrix4x4 viewProj, Matrix4x4 projection, Matrix4x4 view)
    {
        // Neither the benchmark nor the menu has anybody in the world. ⚠ The menu matters for a
        // second reason: the held arm clears the depth buffer (see §8), so drawing it under a
        // camera that is nowhere near a body would leave the panel over a cleared frame.
        if (_bench is not null || _atStartScreen) return;

        var sky = new SkyParams(
            _skyState.SunDirection, _skyState.SunColor, _skyState.SkyAmbient, _skyState.GroundAmbient,
            NightFloor, _skyState.Horizon, _fogStart, _fogEnd, FlickerClock);

        var light = SampleLight(_camera.Position);

        // ⛳ The animals, before the player and before the held arm — the arm clears the depth
        // buffer, so anything drawn into the world after it is drawn over the top of the world.
        // Each one is lit where it is standing rather than where the camera is, which is what makes
        // a cow in a wood darker than one in a field.
        if (_creatureRenderer is not null && _herd is not null)
        {
            foreach (var creature in _herd.All)
            {
                // ⚠ A burning fuse swells the whole animal a quarter over its last moment — the
                // one visual tell the hiss has, readable from behind where the face is not.
                _creatureRenderer.Draw(
                    viewProj, _viewPosition, sky, SampleLight(creature.Position + new Vector3(0f, 0.6f, 0f)),
                    creature.Kind, creature.Position, creature.Yaw,
                    creature.HurtFor / CreatureHerd.HurtSeconds, creature.TippedOver,
                    creature.Scale * (1f + 0.25f * creature.FuseFraction));
            }
        }

        // The carts, through the same shader — each lit where it stands, yawed the way its own
        // stretch of track runs.
        if (_creatureRenderer is not null)
        {
            foreach (var cart in _cartSystem.All)
            {
                var form = _railTable.FormOf(
                    _streamer.World.GetBlock(cart.X, cart.Y, cart.Z).Value);
                if (form == RailForm.None) continue;

                var at = cart.Position(form);
                var heading = RailForms.Heading(form, cart.T);
                var yawDeg = float.RadiansToDegrees(MathF.Atan2(heading.Z, heading.X));

                _creatureRenderer.Draw(
                    viewProj, _viewPosition, sky,
                    SampleLight(at + new Vector3(0f, 0.4f, 0f)),
                    "cart", at, yawDeg);
            }
        }

        // Third person only means anything when there is a body to stand behind, which is the same
        // condition PlaceCamera uses to decide whether to run the boom out. The two have to agree,
        // or the camera pulls back from a player it is not drawing.
        if (_view != ViewMode.First && _walking)
        {
            if (_spawned)
            {
                var pose = _animator.Pose(_camera.Yaw, _camera.Pitch);
                _playerRenderer.DrawWorld(
                    viewProj, _viewPosition, sky, light, _player.Position, pose, WornMaterials());

                // ⛳ And what they are carrying, in the world, against the same arm. Third person
                // showed empty hands until now — no tool, no torch, nothing — which the user called
                // a big part of the game to be missing.
                if (_inventory.HeldType is { } carried)
                {
                    _blockTextures.Bind();
                    _itemRenderer.DrawInHand(
                        viewProj,
                        _playerRenderer.HeldWorldTransform(
                            _player.Position, pose, !carried.DrawsAsBlock,
                            _itemRenderer.HoldPoint(carried)),
                        carried, _registry, HandLight(light));
                }

                // ⛳ And the other hand, which had a pocket on the interface and nothing on the body.
                // A raised shield turned aside half of every blow that got past the plate and cost
                // its holder every swing and every block placed — and read on the model as somebody
                // standing perfectly still. It is the one part of the armour work nobody could see.
                var offhand = _equipment[EquipSlot.Offhand];
                if (!offhand.IsEmpty)
                {
                    var other = _items[offhand.Item];
                    _blockTextures.Bind();
                    _itemRenderer.DrawInHand(
                        viewProj,
                        _playerRenderer.OffhandWorldTransform(
                            _player.Position, pose, !other.DrawsAsBlock,
                            _itemRenderer.HoldPoint(other), _vitals.ShieldRaised),
                        other, _registry, HandLight(light));
                }
            }

            return;
        }

        // The arm goes over the world rather than into it. It sits centimetres from the eye, so
        // against a real depth buffer it would be buried in the first block walked up to.
        _gl.Clear(ClearBufferMask.DepthBufferBit);

        // Holding something means seeing the thing, not the limb carrying it. The arm is drawn only
        // when the hands are empty, where it is the only thing on screen that can show a swing
        // happening at all.
        if (_inventory.HeldType is { } held)
        {
            DrawHeldItem(projection, light, held);
            DrawOffhandItem(projection, light);
            return;
        }

        // The sun has to arrive in the same space the geometry is in, or the arm lights from a
        // fixed corner of the screen and swings through its own shading as the player turns.
        _playerRenderer.DrawViewModel(
            projection, Vector3.TransformNormal(_skyState.SunDirection, view), sky, light,
            _animator.Swinging, _animator.SwingProgress);

        DrawOffhandItem(projection, light);
    }

    /// <summary>
    /// Puts whatever is in the OTHER hand on screen, on the far side of the view.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>Drawn in both branches above, which is the bug this closes.</b> A raised shield
    /// turned aside half of every blow that got past the plate and cost its holder every swing — and
    /// in first person, the view a player is actually in, it appeared nowhere at all. The pocket on
    /// the HUD lit up and the world showed nothing.</para>
    /// <para>⛳ <b>The thing and not the limb</b>, exactly as the main hand does it: an item in that
    /// hand is what you look at, and an empty offhand draws nothing rather than parking a bare left
    /// arm in the corner of the screen for the whole game.</para>
    /// <para>⚠ It does not swing. The swing belongs to the hand that strikes, and passing the main
    /// hand's progress in here would send a torch through the same arc as the sword beside it.</para>
    /// </remarks>
    private void DrawOffhandItem(Matrix4x4 projection, EntityLight light)
    {
        var stack = _equipment[EquipSlot.Offhand];
        if (stack.IsEmpty) return;

        var other = _items[stack.Item];

        _blockTextures.Bind();
        _itemRenderer.DrawInHand(
            projection,
            _playerRenderer.OffhandTransform(!other.DrawsAsBlock, _itemRenderer.HoldPoint(other)),
            other,
            _registry,
            HandLight(light));
    }

    /// <summary>
    /// Puts whatever is in hand on screen, where the fist of the view model would be.
    /// </summary>
    /// <remarks>
    /// The transform comes from the arm rather than from a second set of numbers here, even though
    /// the arm itself is not drawn: the arm is what defines where a hand is and how it travels
    /// through a swing, and a tool animated from its own copy of those numbers drifts out of the
    /// grip the first time either is dialled.
    /// </remarks>
    private void DrawHeldItem(Matrix4x4 projection, EntityLight light, ItemType held)
    {
        _blockTextures.Bind();
        _itemRenderer.DrawInHand(
            projection,
            _playerRenderer.HeldTransform(
                _animator.Swinging ? _animator.SwingProgress : 0f, !held.DrawsAsBlock,
                _itemRenderer.HoldPoint(held)),
            held,
            _registry,
            HandLight(light));
    }

    /// <summary>
    /// What a held thing is lit by: the cell the player is standing in, sun and block light together.
    /// </summary>
    /// <remarks>
    /// One sample rather than a face-by-face light, because a held item is a few centimetres across
    /// and every one of its faces is in the same cell. The self-shading in the item shader is what
    /// keeps it from reading as a flat silhouette.
    /// </remarks>
    private Vector3 HandLight(EntityLight light) =>
        HeldGrip.HandLight(
            light.Block, light.Sky * _skyState.SunColor.X + _skyState.SkyAmbient.X);

    private void OnResize(Vector2D<int> size) => _gl.Viewport(size);

    private void Shutdown()
    {
        if (_shutdown) return;
        _shutdown = true;

        // ⛳ First, and before the streamer is torn down — it owns the world being written. Closing
        // the window is the one save every player makes and the only one they never think about,
        // so it comes before anything that could throw on the way out.
        //
        // ⚠ Not from the menu. Quitting without ever pressing start must leave the world exactly as
        // it was found, and a new world nobody played must not appear in the list as one they did.
        // ⚠ And not for the shot instrument either. It teleports the body, empties the pockets and
        // fills them with whatever it is photographing — none of which is anybody's game, and all of
        // which would be written into a world file with a name in the list on the menu.
        if (!_atStartScreen && _options.ShotPath is null) SaveWorld("on quit");

        // Stop the workers before tearing down GL state, or a mesh can land in the queue after
        // the context it was destined for is gone.
        _streamer?.Dispose();

        foreach (var mesh in _meshes.Values) mesh.Dispose();
        _meshes.Clear();
        _chunkShader?.Dispose();
        _outline?.Dispose();
        _sky?.Dispose();
        _clouds?.Dispose();
        _particleRenderer?.Dispose();
        _itemRenderer?.Dispose();
        _hud?.Dispose();
        _audio?.Dispose();
        _blockTextures?.Dispose();
        _playerRenderer?.Dispose();
        _creatureRenderer?.Dispose();
        _cracks?.Dispose();
    }

    public void Dispose()
    {
        Shutdown();
        _input?.Dispose();
        _window.Dispose();
    }
}
