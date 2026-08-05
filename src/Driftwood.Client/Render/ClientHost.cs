using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Driftwood.Client.Diagnostics;
using Driftwood.Client.Audio;
using Driftwood.Core.Audio;
using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;
using Driftwood.Core.Gen;
using Driftwood.Core.Items;
using Driftwood.Core.Lighting;
using Driftwood.Core.Meshing;
using Driftwood.Core.Particles;
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

    /// <summary>Arm width, or null to read it out of the sheet.</summary>
    public ArmStyle? Arms { get; init; }

    /// <summary>Seconds of flight to measure; 0 runs the game normally.</summary>
    public double BenchSeconds { get; init; }

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
    private IInputContext _input = null!;
    private IKeyboard _keyboard = null!;
    private IMouse _mouse = null!;

    private Shader _chunkShader = null!;
    private SkyRenderer _sky = null!;
    private CloudRenderer _clouds = null!;

    /// <summary>The day/night cycle, and the one place the sky's colours come from.</summary>
    private SkyClock _clock = null!;
    private SkyState _skyState;

    /// <summary>Seconds since the world opened, which is what makes the clouds drift.</summary>
    private double _elapsed;
    private readonly Dictionary<ChunkPos, ChunkMeshGpu> _meshes = [];
    private readonly FlyCamera _camera = new();
    private WorldStreamer _streamer = null!;
    private int _viewRadius;

    private PlayerBody _player = null!;
    private BlockOutline _outline = null!;
    private BlockTextureArray _blockTextures = null!;
    private BlockTextureSet.Result _textures = null!;
    private bool[] _targetable = null!;

    private PlayerRenderer _playerRenderer = null!;
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

    /// <summary>How far a player can reach to break or place. Genre-standard.</summary>
    private const float Reach = 5f;

    /// <summary>Everything that can be carried, and what each block leaves behind.</summary>
    private ItemRegistry _items = null!;
    private BlockDrops _dropTable = null!;

    /// <summary>Everything that can be made, and every furnace in the world.</summary>
    private RecipeBook _book = null!;
    private FurnaceBank _furnaces = null!;

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

    /// <summary>The four facings of the furnace, unlit and lit, for swapping between them.</summary>
    private BlockId[] _furnaceCold = null!;
    private BlockId[] _furnaceHot = null!;

    /// <summary>Each half-slab and the whole block a second one laid on it makes.</summary>
    private readonly Dictionary<ushort, BlockId> _slabMerge = [];

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

        while (!_window.IsClosing && !_stopRequested)
        {
            _window.DoEvents();
            _window.DoUpdate();
            _window.DoRender();
        }

        _window.DoEvents();
        Shutdown();
        return UiCheckFailed ? 1 : _exitCode;
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

        // A full mip chain is a third again on top of the base level.
        const double WithMips = 4.0 * 4.0 / 3.0;

        var maxSide = _gl.GetInteger(GLEnum.MaxTextureSize);
        var maxLayers = _gl.GetInteger(GLEnum.MaxArrayTextureLayers);
        var layers = StarterBlocks.LayerCount;

        if (maxLayers > 0 && layers > maxLayers)
            Console.Error.WriteLine($"driftwood: {layers} texture layers but the card takes {maxLayers}");

        var affordable = (int)Math.Sqrt(Budget / (layers * WithMips));

        // Down to a power of two. Every pack is painted at one, and a mip chain built from an
        // awkward size loses a level to rounding at the bottom.
        var ceiling = 16;
        while (ceiling * 2 <= affordable && ceiling * 2 <= maxSide) ceiling *= 2;

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
        _gl = GL.GetApi(_window);
        ApplyWindowIcon();

        // Whatever the player changed last time, before anything reads a key or a field of view.
        // A bad file costs the setting it names and nothing else — see GameSettings.Load.
        _settings = GameSettings.Load();
        _keys = new InputMap(_settings.Keys);

        // A command line still wins over a saved setting for the run it is on, without writing
        // itself into the file — starting once with --vsync should not turn it on for good.
        if (_options.VSync) _settings.VSync = true;
        if (_options.Mute) _settings.Mute = true;

        // What earlier sessions already announced. Achievements fire once ever, not once a launch.
        _unlocks.Restore();

        Console.WriteLine($"settings    {GameSettings.Path}");

        _input = _window.CreateInput();
        _keyboard = _input.Keyboards[0];
        _mouse = _input.Mice[0];

        _keyboard.KeyDown += OnKeyDown;
        _mouse.MouseMove += OnMouseMove;
        _mouse.MouseDown += OnMouseDown;
        _mouse.MouseUp += OnMouseUp;
        _mouse.Scroll += OnScroll;

        // The benchmark flies itself, so it leaves the cursor alone; stealing the mouse for a
        // measurement run is rude and changes nothing about what is measured.
        SetMouseCaptured(_options.BenchSeconds <= 0);

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

        // The benchmark opens no device at all. It flies a scripted path and hears nothing worth
        // hearing, and a measurement run that makes noise on somebody's machine is rude twice over.
        if (_options.BenchSeconds <= 0 && !_options.Mute)
        {
            _audio = new AudioEngine(new SoundLibrary(SoundLibrary.FindRoot()));
            Console.WriteLine($"sound       {_audio.Summary}");
        }

        _clock = new SkyClock(_options.StartTime, _options.DayLength);
        _skyState = _clock.Now;
        _sky = new SkyRenderer(_gl);

        var cloudField = new CloudField(_options.Seed, _options.PackPath);
        _clouds = new CloudRenderer(_gl, cloudField.Build());
        Console.WriteLine(
            $"clouds      {cloudField.Summary}, {cloudField.Coverage * 100:F0}% cover, "
            + $"{_clouds.QuadCount:N0} quads over {CloudField.Period:F0} blocks");

        var ceiling = TextureCeiling();
        _textures = BlockTextureSet.Build(_options.PackPath, _options.TextureSize, ceiling);
        _blockTextures = new BlockTextureArray(_gl, _textures.Tiles, _textures.Size);
        Console.WriteLine($"textures    {_textures.Summary}");

        var skin = PlayerSkin.Build(_options.SkinPath, _options.Arms);
        _playerRenderer = new PlayerRenderer(_gl, skin);
        _hud.SetSkin(skin);
        Console.WriteLine($"skin        {skin.Summary}");

        // The same size the block tiles came out at, whatever decided it — cracks are laid over a
        // block face and a crack chain at a different resolution is visible as a crack chain.
        var cracks = CrackTextures.Build(_options.PackPath, _textures.Size);
        _cracks = new BlockCracks(_gl, cracks);
        Console.WriteLine($"cracks      {cracks.Summary}");

        BuildWorld();

        // Last, because it wants the camera, the window and the audio device to all exist. Doing
        // it here rather than in half a dozen constructors also means there is exactly one place
        // that turns a setting into an effect, which is the place to look when one does nothing.
        ApplySettings();
    }

    private void BuildWorld()
    {
        var registry = new BlockRegistry();
        var ids = StarterBlocks.Register(registry);
        registry.Seal();
        _registry = registry;

        // The item layer sits on top of the block layer and never the other way round, which is why
        // it is built here rather than beside it: everything it needs is an id the blocks have
        // already handed out.
        _items = StarterItems.Register(registry);
        _dropTable = StarterItems.Drops(registry, _items);
        _book = StarterRecipes.Build(_items);
        _furnaces = new FurnaceBank(_items, _book);
        _furnaceCold = StarterBlocks.Furnaces(registry, lit: false);
        _furnaceHot = StarterBlocks.Furnaces(registry, lit: true);
        foreach (var (slab, whole) in StarterBlocks.SlabMerges(registry)) _slabMerge[slab.Value] = whole;

        var generator = new TerrainGenerator(_options.Seed, ids, _options.OceanCoverage);

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
            new ClimateField(_options.Seed), _textures.GrassMap, _textures.FoliageMap);

        _streamer = new WorldStreamer(registry, generator, viewRadius, tinter: tinter)
        {
            // What lets a fence know it has a neighbour. Handed over rather than built inside the
            // streamer, so a world with nothing that connects pays nothing for the pass.
            Connections = StarterBlocks.Connections(registry),
        };

        var reach = viewRadius * Chunk.Size;
        _fogEnd = MathF.Min(reach * 0.90f, 700f);
        _fogStart = _fogEnd * 0.55f;
        _camera.FarPlane = _fogEnd + 200f;

        _player = new PlayerBody(registry);
        _vitals = new PlayerVitals(registry);
        _particles = new ParticleSystem(registry);
        _leafBlock = ids.Leaves;
        _inventory = new Inventory(_items);
        _equipment = new Equipment(_items);
        _drops = new DroppedItems(registry, _items);
        _solid = registry.BuildSolidTable();

        // Two grids, both alive for the whole session rather than made when a screen opens. What is
        // laid out in the hands stays laid out while a player goes and fetches the missing plank,
        // which is what every game in this space does and what anybody would expect.
        _handGrid = new CraftingGrid(_book, _items, 2, 2);
        _benchGrid = new CraftingGrid(_book, _items, 3, 3);

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
        }

        _animator.Reset(_camera.Yaw);
        _viewPosition = _camera.Position;
        _viewForward = _camera.Forward;

        // Prime the pipeline before the first frame so the viewer does not open inside an empty
        // world, then let the render loop take delivery of the rest as it arrives.
        _streamer.Update(_camera.Position);

        Console.WriteLine($"seed        {_options.Seed}");
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

    private void OnKeyDown(IKeyboard keyboard, Key key, int _)
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
    private bool ScreenKey(Key key)
    {
        var many = _keyboard.IsKeyPressed(Key.ShiftLeft) || _keyboard.IsKeyPressed(Key.ShiftRight);
        var tabbed = _hudScreen.Kind is HudScreenKind.Player or HudScreenKind.Game;

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
        var many = _keyboard.IsKeyPressed(Key.ShiftLeft) || _keyboard.IsKeyPressed(Key.ShiftRight);
        var at = _layout.At(_hudScreen.Pointer.X, _hudScreen.Pointer.Y);

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
        var giving = zone.Role is SlotRole.Result or SlotRole.Smelted;

        // A square that only gives is a different gesture entirely. There is nothing to put into
        // it, taking from it spends the arrangement rather than a slot, and shift-clicking it is
        // the "make as many as I can afford" every player in this genre reaches for.
        if (giving)
        {
            TakeFromResult(zone, many);
            return;
        }

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
        _ => ItemStack.Empty,
    };

    private ItemStack TakeHalfFrom(Zone zone) => zone.Role switch
    {
        SlotRole.Pocket => _inventory.TakeHalf(zone.Index),
        SlotRole.Craft => _hudScreen.Grid?.TakeHalf(zone.Index) ?? ItemStack.Empty,
        _ => TakeAllFrom(zone),
    };

    private ItemStack PutInto(Zone zone, ItemStack carried) => zone.Role switch
    {
        SlotRole.Pocket => _inventory.PutInto(zone.Index, carried),
        SlotRole.Craft => _hudScreen.Grid?.Put(zone.Index, carried) ?? carried,
        SlotRole.Equip => _equipment.Put((EquipSlot)zone.Index, carried),
        SlotRole.Smelting or SlotRole.Fuel => FeedFurnace(zone.Role, carried),
        _ => carried,
    };

    private ItemStack PutOneInto(Zone zone, ItemStack carried) => zone.Role switch
    {
        SlotRole.Pocket => _inventory.PutOne(zone.Index, carried),
        SlotRole.Craft => _hudScreen.Grid?.PutOne(zone.Index, carried) ?? carried,
        _ => PutInto(zone, carried),
    };

    /// <summary>Shift-click: the bar and the backpack trade, and a grid empties into the pockets.</summary>
    private void SweepSlot(Zone zone)
    {
        switch (zone.Role)
        {
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
            if (!many && _hudScreen.Carried.IsEmpty)
            {
                _hudScreen.Carried = result;
                continue;
            }

            if (_hudScreen.Carried.Matches(result))
                _hudScreen.Carried = _hudScreen.Carried.Merge(result, _items[result.Item].MaxStack, out result);

            Spill(result);
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
        _mouse.Position = _hudScreen.Pointer * scale;
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
        _mouse.Position = middle;
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
        _hudScreen.Scroll = 0;
        _hudScreen.Recipes.Clear();
        _hudScreen.Payable.Clear();
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
        _station = (x, y, z);
        _holdingBreak = false;
        _holdingPlace = false;
        _mining.Cancel();
        _furnaces.Open(x, y, z);
        TakeThePointer();
        RefreshScreen();
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
        if (_hudScreen.Kind == HudScreenKind.Game) _tabRow[_hudScreen.Tab] = _hudScreen.Selected;

        // Nothing is left nowhere. A stack on the cursor is in neither the pockets nor the grid nor
        // the world, so a screen that shut while one was held would simply delete it — and what is
        // laid out in a bench's three by three belongs to the player, not to the bench. Both go back
        // into the pockets, and whatever will not fit lands on the floor rather than being swallowed,
        // which is the one rule this inventory has never broken.
        Spill(_hudScreen.Carried);
        _hudScreen.Carried = ItemStack.Empty;

        if (_hudScreen.Kind == HudScreenKind.Bench)
            foreach (var left in _benchGrid.Empty(_inventory)) Spill(left);

        _rebinding = null;
        _hudScreen.Kind = HudScreenKind.None;
        _hudScreen.Grid = null;
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
    private void RefreshScreen()
    {
        if (_hudScreen.Kind == HudScreenKind.None) return;

        _hudScreen.Footer = FooterHint();

        // The squares read themselves — what is in a slot is asked of the inventory as it is drawn,
        // so there is no list here that could fall out of step with one. The book beside them does
        // need a list, and what is on it depends on how wide the grid is: a bench lends three.
        if (_hudScreen.IsContainer)
        {
            if (_hudScreen.Kind == HudScreenKind.Furnace || _hudScreen.Grid is null)
            {
                _hudScreen.Recipes.Clear();
                _hudScreen.Payable.Clear();
                return;
            }

            if (_shown.Count == 0)
            {
                foreach (var recipe in _book.Recipes)
                    if (!recipe.NeedsBench || _hudScreen.Grid.Width >= 3) _shown.Add(recipe);
            }

            // Built once per opening and only its affordability recomputed. A list that changed
            // length as things were picked up would move the selection out from under a player on
            // the frame they clicked it.
            _hudScreen.Recipes.Clear();
            _hudScreen.Recipes.AddRange(_shown);

            _hudScreen.Payable.Clear();
            foreach (var recipe in _shown) _hudScreen.Payable.Add(_book.CanPay(_inventory, recipe));

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
            HudScreenKind.Player =>
                $"arrows pick, enter makes one, shift and enter makes as many as it can, {close} closes",
            _ when OnTab(GameTab.Controls) =>
                $"up and down pick, enter listens for a key, left clears it{wheel}, {close} closes",
            _ => $"up and down pick, left and right change it, tab changes tab{wheel}, {close} closes",
        };
    }

    /// <summary>What the open settings tab is showing, rebuilt from what is actually set.</summary>
    private void BuildRows()
    {
        _hudScreen.Rows.Clear();

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

            default:
                var p = _walking ? _player.Position : _camera.Position;

                _hudScreen.Rows.Add(new MenuRow("this world", Heading: true));
                _hudScreen.Rows.Add(new MenuRow("seed", _options.Seed.ToString()));
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
                    case "new recipe notices": _settings.RecipeNotices = !_settings.RecipeNotices; break;
                    // Enter only. A direction is how a player browses, not how they throw
                    // something away that cannot be got back.
                    case "forget what has been said":
                        if (!activated) return;
                        _unlocks.Forget();
                        _unlocks.Persist();
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

    /// <summary>Enter, on a settings row. Toggles what toggles and listens for a key on a binding.</summary>
    private void ActivateRow()
    {
        if (OnTab(GameTab.Controls))
        {
            if (ActionAtRow() is { } action) _rebinding = action;
            RefreshScreen();
            return;
        }

        AdjustRow(1, activated: true);
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
        if (_hudScreen.Grid is not { } grid) return;
        if (index < 0 || index >= _shown.Count) return;

        var recipe = _shown[index];
        if (recipe.NeedsBench && grid.Width < 3) return;
        if (!_book.CanPay(_inventory, recipe)) return;

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
            if (_toasts[i].Gone) _toasts.RemoveAt(i);
        }

        if (_bench is not null || !_settings.RecipeNotices) return;
        if (!_unlocks.Poll(_book, _inventory, _justUnlocked) || _justUnlocked.Count == 0) return;

        // Written out as it happens rather than on the way out, because an achievement nobody
        // recorded is one the game will cheerfully award again next time, and a crash is exactly
        // the session somebody would rather not repeat the whole tutorial after.
        _unlocks.Persist();

        var first = _justUnlocked[0];
        var line = _justUnlocked.Count == 1
            ? first.Name
            : $"{first.Name} and {_justUnlocked.Count - 1} more";

        _toasts.Add(new Toast(
            "you can now make", line, _items[first.Result.Item].IconLayer, ToastSeconds));

        // Oldest first out. Three is what fits down the corner without meeting the hearts.
        while (_toasts.Count > MaxToasts) _toasts.RemoveAt(0);

        PlaySound(SoundMaterial.Glass, SoundEvent.Place, _viewPosition, 0.35f);
    }

    /// <summary>Advances every furnace and swaps the block under any whose flame changed.</summary>
    private void StepFurnaces(float dt)
    {
        if (_furnaces.Count == 0) return;

        _furnaces.Update(dt, _relit);

        foreach (var (x, y, z, lit) in _relit)
        {
            // Which way it faces is carried by the id, so the swap has to find the facing first —
            // otherwise a furnace lights up pointing a different way than it did unlit.
            var here = _streamer.World.GetBlock(x, y, z);
            var from = lit ? _furnaceCold : _furnaceHot;
            var to = lit ? _furnaceHot : _furnaceCold;

            var facing = Array.IndexOf(from, here);
            if (facing < 0) continue;

            _streamer.EditBlock(x, y, z, to[facing]);
        }
    }

    /// <summary>The time of day as a clock face, since 0.62 of a day means nothing to anybody.</summary>
    private static string ClockFace(float time)
    {
        var minutes = (int)MathF.Round(time * 24f * 60f) % (24 * 60);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    private void SelectHandSlot(int slot) => _inventory.Select(slot);

    private void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        if (wheel.Y == 0f) return;

        // Over a list, the wheel is the list's. Three lines a notch, which is what a wheel means
        // everywhere else on the machine.
        if (_hudScreen.Kind == HudScreenKind.Game)
        {
            ScrollRows(_hudScreen.Scroll - Math.Sign(wheel.Y) * 3);
            return;
        }

        _inventory.Scroll(-Math.Sign(wheel.Y));
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
    private void OnMouseDown(IMouse mouse, MouseButton button)
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

    private void OnMouseUp(IMouse mouse, MouseButton button)
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
        _mouse.Cursor.CursorMode =
            _hudScreen.IsOpen ? CursorMode.Hidden
            : _mouseCaptured ? CursorMode.Raw
            : CursorMode.Normal;

        _haveMouseAnchor = false;
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
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
        else if (_walking)
        {
            StepPlayer((float)dt);
        }
        else
        {
            _camera.Update((float)dt, _keyboard);
        }

        _elapsed += dt;
        _clock.Advance((float)dt);
        _skyState = _clock.Now;
        _audio?.SetListener(_viewPosition, _viewForward);

        UpdateTarget();
        StepAnimation((float)dt);
        StepFootfall((float)dt);
        StepVitals((float)dt);
        StepLeaffall((float)dt);
        StepFurnaces((float)dt);
        StepToasts((float)dt);
        _particles.Update(_streamer.World, (float)dt);

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
            PlaySound(MaterialOf(_inventory.Held), SoundEvent.Place, where, 0.5f);
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
                : $"Driftwood — {_fps:F0} fps | seed {_options.Seed} | "
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

        _animator.Update(
            dt, stood, _camera.Yaw, PlayerBody.WalkSpeed, sneaking, _holdingBreak || _holdingPlace);

        var strikes = _animator.TakeStrikes();

        // A click fast enough to be released inside one frame still registered its intent when it
        // went down, so fall back to that rather than dropping the swing on the floor.
        var placing = _holdingPlace || (!_holdingBreak && !_lastStrikeWasBreak);

        for (; strikes > 0 && placing; strikes--)
        {
            PlaceOnTarget();
            UpdateTarget();
        }

        var target = _target is { } hit ? _registry[_streamer.World.GetBlock(hit.X, hit.Y, hit.Z)] : null;
        var cell = _target is { } at ? (at.X, at.Y, at.Z) : ((int, int, int)?)null;

        // Chips fly off the face the blow lands on, not off the block as a whole. Once per swing
        // rather than every frame, so the spray keeps the arm's rhythm instead of being a hose.
        if (strikes > 0 && !placing && target is not null && _target is { } struck)
        {
            _particles.Chip(target, struck.X, struck.Y, struck.Z, struck.Face);
            PlaySound(target, SoundEvent.Hit, new Vector3(struck.X + 0.5f, struck.Y + 0.5f, struck.Z + 0.5f), 0.55f);
        }

        if (!_mining.Update(dt, target, cell, _holdingBreak, _inventory.HeldType)) return;

        // The burst goes before the block does. Reading the type after BreakTarget gets air.
        if (target is not null && cell is { } broken)
        {
            var centre = new Vector3(broken.Item1 + 0.5f, broken.Item2 + 0.5f, broken.Item3 + 0.5f);
            _particles.Burst(target, broken.Item1, broken.Item2, broken.Item3);
            PlaySound(target, SoundEvent.Break, centre);

            // What the block leaves depends on what took it. Below the tier line it still comes
            // apart and leaves nothing, which is the whole reason to go and make a pickaxe.
            _drops.Drop(_dropTable.Harvest(target, _inventory.HeldType), centre);

            // A tool that did the work wears from it. Only the tool: a bare hand is free, and so is
            // a plank held like a club, which is why this asks the item rather than the swing.
            if (_inventory.HeldType is { IsTool: true } && _inventory.WearHeld())
                PlaySound(SoundMaterial.Wood, SoundEvent.Break, _viewPosition, 0.7f);
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
        return new EntityLight(
            LightValue.Sky(packed) / (float)LightValue.Max,
            new Vector3(LightValue.Red(packed), LightValue.Green(packed), LightValue.Blue(packed))
                / LightValue.Max);
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
    /// worse one; nothing is simulated for it and the bar stays where it was.
    /// </remarks>
    private void StepVitals(float dt)
    {
        if (_bench is not null || !_walking || !_spawned) return;

        var what = _vitals.Update(_streamer.World, _player, dt);

        // A hurt has to be felt. There is no player voice in the sound pack, so the ground the
        // blow was taken on stands in for one — which is at least the right material.
        if (what.Hurt > 0)
        {
            var under = _streamer.World.GetBlock(
                (int)MathF.Floor(_player.Position.X),
                (int)MathF.Floor(_player.Position.Y - 0.1f),
                (int)MathF.Floor(_player.Position.Z));

            if (!under.IsAir)
                PlaySound(_registry[under], SoundEvent.Break, _player.Position, 0.8f);
        }

        if (!what.Died) return;

        _player.Teleport(_spawnPoint);
        _vitals.Restore();
    }

    /// <summary>Plays one of a material's sounds for one situation, at a point in the world.</summary>
    private void PlaySound(BlockType type, SoundEvent which, Vector3 at, float volume = 1f) =>
        PlaySound(type.Sounds, which, at, volume);

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

    private void PlaySound(SoundMaterial material, SoundEvent which, Vector3 at, float volume = 1f)
    {
        if (_audio is null) return;

        var names = MaterialSounds.For(material, which);
        if (names.Count == 0) return;

        _audio.Play(
            names[_soundPick.Next(names.Count)],
            at,
            volume,
            0.92f + (float)_soundPick.NextDouble() * 0.16f);
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

    /// <summary>Finds the block under the crosshair, if any is within reach.</summary>
    private void UpdateTarget()
    {
        _target = _bench is not null
            ? null
            : BlockRay.TryCast(_streamer.World, _targetable, _camera.Position, _camera.Forward, Reach, out var hit)
                ? hit
                : null;
    }

    /// <summary>Removes the targeted block, and empties it first if it was holding anything.</summary>
    private void BreakTarget()
    {
        if (_target is not { } hit) return;

        // A furnace comes apart with its contents on the floor rather than taking them with it.
        // The state lives beside the world rather than in the cell, so nothing else would ever
        // clean it up — and a player who mines one mid-smelt has lost both the ore and the coal.
        var centre = new Vector3(hit.X + 0.5f, hit.Y + 0.5f, hit.Z + 0.5f);
        foreach (var spilled in _furnaces.Remove(hit.X, hit.Y, hit.Z)) _drops.Drop(spilled, centre);

        _streamer.EditBlock(hit.X, hit.Y, hit.Z, BlockId.Air);

        // Standing in front of a furnace that is no longer there.
        if (_hudScreen.IsOpen && _station == (hit.X, hit.Y, hit.Z)) CloseScreen();
    }

    /// <summary>
    /// Puts a block against the face being looked at, unless the player is standing there.
    /// </summary>
    /// <remarks>
    /// The occupancy test is not a nicety. Without it the first thing anyone does is place a block
    /// into their own feet, which leaves them inside solid geometry with the collision resolver
    /// having no free direction to push them out of — the classic way to get stuck in a voxel game.
    /// </remarks>
    private void PlaceOnTarget()
    {
        if (_target is not { } hit) return;

        // Using comes before building. A block that does something answers the right button itself,
        // so a bench cannot be buried under the plank a player meant to open it with. The general
        // machinery for this — a block deciding what a right click means — is still ahead of us;
        // this is the narrow version the two blocks that exist need.
        var struck = _registry[_streamer.World.GetBlock(hit.X, hit.Y, hit.Z)];
        if (struck.Interactive)
        {
            if (struck.Name == "bench") OpenBench(hit.X, hit.Y, hit.Z);
            else OpenFurnace(hit.X, hit.Y, hit.Z);
            return;
        }

        // A slab laid on a matching slab fills the cell rather than starting a second one above it.
        // Genre-standard, and the reason placement cannot simply test for air: what is already
        // there sometimes decides what happens, and until now it only ever decided whether to give
        // up. Merging into the cell that was struck, not the one beside it.
        if (_inventory.HeldType is { Places: { } holding }
            && _slabMerge.TryGetValue(_streamer.World.GetBlock(hit.X, hit.Y, hit.Z).Value, out var whole)
            && Array.IndexOf(holding.Variants, _streamer.World.GetBlock(hit.X, hit.Y, hit.Z)) >= 0)
        {
            _streamer.EditBlock(hit.X, hit.Y, hit.Z, whole);
            _inventory.SpendHeld();
            PlaySound(
                _registry[whole], SoundEvent.Place,
                new Vector3(hit.X + 0.5f, hit.Y + 0.5f, hit.Z + 0.5f), 0.85f);
            return;
        }

        var (x, y, z) = hit.Adjacent;
        if (!_streamer.World.GetBlock(x, y, z).IsAir) return;

        if (_inventory.HeldType is not { Places: { } held }) return;

        // Where in the target cell the ray landed, which is what decides a slab's half. Taken from
        // the ray rather than from which face was struck: clicking a block's top lands at the floor
        // of the cell above, its underside at the ceiling of the cell below, and a side wherever
        // the crosshair was, so one number already answers all three.
        var landing = _camera.Position + _camera.Forward * hit.Distance;
        var height = Math.Clamp(landing.Y - y, 0f, 1f);

        if (!held.TryResolve(hit.Face, height, _camera.Forward, out var block)) return;
        if (held.NeedsFloor && !_solid[_streamer.World.GetBlock(x, y - 1, z).Value]) return;

        if (_walking)
        {
            var probe = _streamer.World;
            var before = probe.GetBlock(x, y, z);
            probe.SetBlock(x, y, z, block);
            var blocked = _player.Collides(probe, _player.Position);
            probe.SetBlock(x, y, z, before);
            if (blocked) return;
        }

        _streamer.EditBlock(x, y, z, block);
        _inventory.SpendHeld();
        PlaySound(_registry[block], SoundEvent.Place, new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), 0.85f);
    }

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
        var forward = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        var right = new Vector3(-forward.Z, 0f, forward.X);

        // A screen has the keyboard, so the body stands still — but it is still stepped, because
        // stopping the simulation would leave a player who opened a bench in mid-air hanging there.
        if (_hudScreen.IsOpen)
        {
            _player.Step(_streamer.World, dt, Vector3.Zero, false, false, false);
            _camera.Position = _player.EyePosition;
            return;
        }

        var wish = Vector3.Zero;
        if (_keys.Held(_keyboard, GameAction.MoveForward)) wish += forward;
        if (_keys.Held(_keyboard, GameAction.MoveBack)) wish -= forward;
        if (_keys.Held(_keyboard, GameAction.MoveRight)) wish += right;
        if (_keys.Held(_keyboard, GameAction.MoveLeft)) wish -= right;

        var jump = _keys.Held(_keyboard, GameAction.Jump);
        var sneak = _keys.Held(_keyboard, GameAction.Sneak);
        var sprint = _keys.Held(_keyboard, GameAction.Sprint) && !sneak;

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
            Seed: _options.Seed.Value,
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

        var drawn = 0;
        var triangles = 0;
        foreach (var mesh in _meshes.Values)
        {
            if (!_frustumCulling) { }
            else if (!frustum.IntersectsBox(mesh.BoundsMin, mesh.BoundsMax)) continue;

            _chunkShader.SetVec3("uChunkOrigin", mesh.Origin);
            _chunkShader.SetVec3Array("uTint", mesh.TintPalette);
            mesh.Draw();
            drawn++;
            triangles += mesh.IndexCount / 3;
        }

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

        _renderMs = (Stopwatch.GetTimestamp() - renderStart) * TicksToMs;
    }

    /// <summary>How many frames the check has drawn, for its own timing.</summary>
    private int _uiCheckFrame;

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
        _uiCheckFrame++;

        // A few frames to let the world stream in, then each screen in turn.
        switch (_uiCheckFrame)
        {
            case 60: SampleUi(size, "no screen"); break;
            case 61: OpenPlayer(PlayerTab.Items, atBench: false, default); break;
            case 90:
                SampleUi(size, "items");
                ProbeSquares();
                SampleFigure(size);
                SampleWell(size, "book well before");
                break;

            case 91: _hudScreen.BookOut = true; RefreshScreen(); break;

            case 120:
                SampleUi(size, "book");
                SampleWell(size, "book well after");
                ProbeBook();
                break;

            case 121: CloseScreen(); OpenBench(0, 0, 0); break;
            case 150: SampleUi(size, "bench"); ProbeSquares(); break;

            case 151: CloseScreen(); OpenGame(GameTab.Controls); break;
            case 180: SampleUi(size, "game"); ProbeRows("top"); break;

            case 181: ScrollRows(int.MaxValue); break;
            case 190: ProbeRows("bottom"); break;

            case 191: JudgeUi(); _window.Close(); break;
        }
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

        Console.WriteLine(
            $"ui-check    rows {where,-6} {seen} of {_hudScreen.Rows.Count} on screen, "
            + $"{lowest}..{highest}, {ScreenLayout.MenuLines(LayoutHeight)} lines at a time");
        Console.Out.Flush();
    }

    /// <summary>What the capped list showed, at the top and at the bottom.</summary>
    private readonly Dictionary<string, (int Seen, int Lowest, int Highest, int Total)> _uiRows = [];

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

        _uiBook = (entries, overlapping, laid, payable, _shown.Count, !makes.IsEmpty);

        Console.WriteLine(
            $"ui-check    book       {entries} recipes on the page of {_shown.Count}, "
            + $"{overlapping} over the panel; laying out '{(payable >= 0 ? _shown[payable].Name : "nothing")}' "
            + $"filled {laid} squares and makes "
            + (makes.IsEmpty ? "nothing" : $"{makes.Count} {_items[makes.Item].Name}"));
        Console.Out.Flush();

        // Put it back the way it was found, so the screens after this one are measured on the same
        // empty pockets every other check here assumes.
        foreach (var left in _hudScreen.Grid?.Empty(_inventory) ?? []) _ = left;
        _inventory.Clear();
    }

    private (int Entries, int Overlapping, int Laid, int Payable, int Total, bool Makes) _uiBook;

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
        var backdrop = Read(ScreenLayout.Figure.X + 3f, ScreenLayout.Figure.Y + 60f);

        _uiSamples["figure"] = chest;
        _uiSamples["figure backdrop"] = backdrop;

        Console.WriteLine(
            $"ui-check    figure     chest rgb {chest.R,3} {chest.G,3} {chest.B,3}   "
            + $"its own backdrop rgb {backdrop.R,3} {backdrop.G,3} {backdrop.B,3}");
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

        (byte R, byte G, byte B) Read(string key) =>
            _uiSamples.TryGetValue(key, out var v) ? v : default;

        var bare = Read("no screen");
        var items = Read("items");
        var book = Read("book");
        var bench = Read("bench");
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
        if (game == bare) faults.Add("opening the game screen changed nothing on screen");

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

        // The container panel is centred, so the middle of the window is inside it — and it is drawn
        // in the same neutral grey the options are. A middle that still reads dark is the backdrop
        // with no panel on it, which is what a panel laid out off the edge looks like from here.
        foreach (var (name, read) in new[] { ("items", items), ("bench", bench) })
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

        // And that the running game's own layout answers for its own squares. Every one of them.
        foreach (var (screen, probe) in _uiProbes)
        {
            if (probe.Misses > 0) faults.Add($"{probe.Misses} squares on the {screen} screen did not answer for themselves");
            if (probe.Hits < 40) faults.Add($"the {screen} screen only laid out {probe.Hits} squares");
        }

        // And the options panel is neutral grey by design, so its middle has no colour cast.
        var spread = Math.Max(game.R, Math.Max(game.G, game.B)) - Math.Min(game.R, Math.Min(game.G, game.B));
        if (spread > 12)
            faults.Add($"the options panel reads {game.R} {game.G} {game.B}, which is not the grey it is drawn in");

        // The control: a corner of a plain frame must still be the world, or the samples are not
        // measuring what they claim and every judgement above is on the same wrong pixels.
        if (world.B <= world.R)
            faults.Add($"the corner of a plain frame is not sky — read {world.R} {world.G} {world.B}");

        UiCheckFailed = faults.Count > 0;

        Console.WriteLine();
        if (faults.Count == 0)
        {
            Console.WriteLine(
                "OK  the overlay reaches the screen: crosshair, four screens, panels in grey, "
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
    private void DrawPlayer(Matrix4x4 viewProj, Matrix4x4 projection, Matrix4x4 view)
    {
        if (_bench is not null) return;

        var sky = new SkyParams(
            _skyState.SunDirection, _skyState.SunColor, _skyState.SkyAmbient, _skyState.GroundAmbient,
            NightFloor, _skyState.Horizon, _fogStart, _fogEnd);

        var light = SampleLight(_camera.Position);

        // Third person only means anything when there is a body to stand behind, which is the same
        // condition PlaceCamera uses to decide whether to run the boom out. The two have to agree,
        // or the camera pulls back from a player it is not drawing.
        if (_view != ViewMode.First && _walking)
        {
            if (_spawned)
            {
                _playerRenderer.DrawWorld(
                    viewProj, _viewPosition, sky, light,
                    _player.Position, _animator.Pose(_camera.Yaw, _camera.Pitch));
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
            return;
        }

        // The sun has to arrive in the same space the geometry is in, or the arm lights from a
        // fixed corner of the screen and swings through its own shading as the player turns.
        _playerRenderer.DrawViewModel(
            projection, Vector3.TransformNormal(_skyState.SunDirection, view), sky, light,
            _animator.Swinging, _animator.SwingProgress);
    }

    /// <summary>
    /// Puts whatever is in hand on screen, where the fist of the view model would be.
    /// </summary>
    /// <remarks>
    /// The transform comes from the arm rather than from a second set of numbers here, even though
    /// the arm itself is not drawn: the arm is what defines where a hand is and how it travels
    /// through a swing, and a tool animated from its own copy of those numbers drifts out of the
    /// grip the first time either is dialled. A block is held small and square; a tool is held
    /// bigger, because it is a flat card and reads as nothing edge-on.
    /// </remarks>
    private void DrawHeldItem(Matrix4x4 projection, EntityLight light, ItemType held)
    {
        var flat = !held.DrawsAsCube;
        var size = flat ? 0.62f : 0.40f;

        _blockTextures.Bind();
        _itemRenderer.DrawInHand(
            projection,
            PlayerRenderer.HeldTransform(
                _animator.Swinging ? _animator.SwingProgress : 0f, size, flat),
            held,
            _registry,
            light.Block + new Vector3(light.Sky * _skyState.SunColor.X + _skyState.SkyAmbient.X));
    }

    private void OnResize(Vector2D<int> size) => _gl.Viewport(size);

    private void Shutdown()
    {
        if (_shutdown) return;
        _shutdown = true;

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
        _cracks?.Dispose();
    }

    public void Dispose()
    {
        Shutdown();
        _input?.Dispose();
        _window.Dispose();
    }
}
