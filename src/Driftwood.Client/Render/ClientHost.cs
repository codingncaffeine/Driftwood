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
        return _exitCode;
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
        _drops = new DroppedItems(registry, _items);
        _solid = registry.BuildSolidTable();

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

        Console.WriteLine("Arrows move (WASD also works), Space jump, Ctrl sneak, Shift sprint");
        Console.WriteLine("Hold left to mine, right to place — the arm swings and the swing takes the block");
        Console.WriteLine("Esc release mouse, F1 wireframe, F2 frustum culling, F3 walk/fly, F5 view");
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
                OpenPlayer(PlayerTab.Craft, atBench: false, default);
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
        var craft = _hudScreen.Kind == HudScreenKind.Player && _hudScreen.Tab == (int)PlayerTab.Craft;

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
                RefreshScreen();
                return true;

            case Key.Enter or Key.KeypadEnter or Key.Space:
                if (craft) CraftSelected(many);
                else if (tabbed) ActivateRow();
                else MoveFurnaceSlot();
                return true;

            case Key.Left or Key.A:
                Step(-1, horizontal: true);
                return true;

            case Key.Right or Key.D:
                Step(1, horizontal: true);
                return true;

            case Key.Up or Key.W:
                Step(craft ? -RecipeColumns : -1, horizontal: false);
                return true;

            case Key.Down or Key.S:
                Step(craft ? RecipeColumns : 1, horizontal: false);
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
            if (_hudScreen.Kind == HudScreenKind.Furnace)
            {
                _hudScreen.Slot = Math.Clamp(_hudScreen.Slot + Math.Sign(by), 0, 2);
                return;
            }

            if (craft)
            {
                if (_shown.Count == 0) return;
                _hudScreen.Selected = Math.Clamp(_hudScreen.Selected + by, 0, _shown.Count - 1);
                return;
            }

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
        _atBench = atBench;
        _station = at;
        _shown.Clear();
        StopHands();
        RefreshScreen();
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
        _hudScreen.Recipes.Clear();
        _hudScreen.Payable.Clear();
        StopHands();
        SetMouseCaptured(false);
        RefreshScreen();
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
        _hudScreen.Slot = 0;
        _station = (x, y, z);
        _holdingBreak = false;
        _holdingPlace = false;
        _mining.Cancel();
        _furnaces.Open(x, y, z);
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
        // The pointer goes back where it was. A player who opened the options and shut them again
        // is looking exactly where they were looking, which is the whole reason the two travel
        // together — and it is why closing takes the mouse back rather than leaving it loose.
        if (_hudScreen.Kind == HudScreenKind.Game)
        {
            _tabRow[_hudScreen.Tab] = _hudScreen.Selected;
            if (_bench is null) SetMouseCaptured(true);
        }

        _rebinding = null;
        _hudScreen.Kind = HudScreenKind.None;
        _shown.Clear();
        _hudScreen.Recipes.Clear();
        _hudScreen.Payable.Clear();
        _hudScreen.Rows.Clear();

        if (!_settingsDirty) return;
        _settingsDirty = false;

        if (!_settings.Save()) Console.Error.WriteLine("driftwood: could not write the settings file");
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

        if (_hudScreen.Kind == HudScreenKind.Furnace) return;

        if (_hudScreen.Kind == HudScreenKind.Player)
        {
            if (_shown.Count == 0)
            {
                foreach (var recipe in _book.Recipes)
                    if (!recipe.NeedsBench || _atBench) _shown.Add(recipe);
            }

            _hudScreen.Recipes.Clear();
            _hudScreen.Recipes.AddRange(_shown);

            _hudScreen.Payable.Clear();
            foreach (var recipe in _shown) _hudScreen.Payable.Add(_book.CanPay(_inventory, recipe));

            _hudScreen.Selected = Math.Clamp(_hudScreen.Selected, 0, Math.Max(0, _shown.Count - 1));
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

        return _hudScreen.Kind switch
        {
            HudScreenKind.Furnace => "left and right pick a slot, enter moves it, 1-9 picks from the bar",
            HudScreenKind.Player =>
                $"arrows pick, enter makes one, shift and enter makes as many as it can, {close} closes",
            _ when OnTab(GameTab.Controls) =>
                $"up and down pick, enter listens for a key, left clears it, tab changes tab, {close} closes",
            _ => $"up and down pick, left and right change it, tab changes tab, {close} closes",
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
                _hudScreen.Rows.Add(new MenuRow("picture", Heading: true));
                _hudScreen.Rows.Add(new MenuRow(
                    "view distance", $"{_settings.ViewDistance} chunks",
                    Note: $"takes effect next time the game opens; {_viewRadius} loaded now"));
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
                    Note: "enter to hear the whole tree announce itself again"));

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

    /// <summary>Nudges whatever is selected. Left is -1 and right is +1.</summary>
    private void AdjustRow(int by)
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
                    case "forget what has been said":
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

        AdjustRow(1);
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

    /// <summary>Makes one of the selected recipe, or as many as the pockets will pay for.</summary>
    private void CraftSelected(bool many)
    {
        if (_hudScreen.Selected < 0 || _hudScreen.Selected >= _shown.Count) return;

        var recipe = _shown[_hudScreen.Selected];
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
    /// Moves the held stack into the selected furnace slot, or takes that slot back into the hand.
    /// </summary>
    /// <remarks>
    /// The output slot only ever gives. Putting an ingot into the tray a furnace is about to fill
    /// would either be swallowed or block the smelt, and neither is what the player meant.
    /// </remarks>
    private void MoveFurnaceSlot()
    {
        if (!_furnaces.TryGet(_station.X, _station.Y, _station.Z, out var furnace)) return;

        var taking = _hudScreen.Slot == 2 || _inventory.Held.IsEmpty;

        if (taking)
        {
            ref var slot = ref _hudScreen.Slot == 0 ? ref furnace.Input
                : ref _hudScreen.Slot == 1 ? ref furnace.Fuel
                : ref furnace.Output;

            if (slot.IsEmpty) return;
            slot = _inventory.Add(slot);
            PlaySound(SoundMaterial.Wood, SoundEvent.Place, _viewPosition, 0.5f);
            return;
        }

        var held = _inventory.Held;
        var target = _hudScreen.Slot == 0 ? furnace.Input : furnace.Fuel;

        // Fuel that will not burn and ore that will not smelt are refused rather than accepted and
        // sat on, so a slot that takes something is a slot that is going to use it.
        if (_hudScreen.Slot == 0 && _book.SmeltFor(held.Item) is null) return;
        if (_hudScreen.Slot == 1 && _items[held.Item].BurnSeconds <= 0f) return;

        var merged = target.Merge(held, _items[held.Item].MaxStack, out var over);
        if (merged.Count == target.Count) return;

        if (_hudScreen.Slot == 0) furnace.Input = merged; else furnace.Fuel = merged;
        _inventory.SpendHeld(held.Count - over.Count);
        PlaySound(SoundMaterial.Wood, SoundEvent.Place, _viewPosition, 0.5f);
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
        _inventory.Scroll(-Math.Sign(wheel.Y));
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

        // A screen swallows the buttons rather than digging through itself.
        if (_hudScreen.IsOpen) return;

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
            case MouseButton.Left: _holdingBreak = false; break;
            case MouseButton.Right: _holdingPlace = false; break;
        }
    }

    private void SetMouseCaptured(bool captured)
    {
        _mouseCaptured = captured;
        _mouse.Cursor.CursorMode = captured ? CursorMode.Raw : CursorMode.Normal;
        _haveMouseAnchor = false;

        // Letting the cursor go stops the mining. A button-up outside the window never arrives, so
        // without this, releasing the mouse mid-swing leaves the player digging forever.
        if (!captured) _holdingBreak = _holdingPlace = false;
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        // Track deltas from the previous report rather than trusting absolute coordinates:
        // raw and normal cursor modes disagree about what Position means, and re-anchoring on
        // every mode change stops the view from snapping when capture is toggled.
        if (!_haveMouseAnchor)
        {
            _lastMousePos = position;
            _haveMouseAnchor = true;
            return;
        }

        var delta = position - _lastMousePos;
        _lastMousePos = position;

        // The view holds still under a screen. It is still tracked above, so closing the screen
        // does not snap the camera to wherever the cursor drifted while it was up.
        if (_mouseCaptured && !_hudScreen.IsOpen) _camera.ApplyMouseDelta(delta.X, delta.Y);
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
            if (struck.Name == "bench") OpenPlayer(PlayerTab.Craft, atBench: true, (hit.X, hit.Y, hit.Z));
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
            _hud.Draw(_blockTextures, _items, _inventory, _vitals, _hudScreen, _toasts, size.X, size.Y);
        }

        _renderMs = (Stopwatch.GetTimestamp() - renderStart) * TicksToMs;
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
