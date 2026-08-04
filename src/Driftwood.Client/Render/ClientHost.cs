using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Driftwood.Client.Diagnostics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;
using Driftwood.Core.Gen;
using Driftwood.Core.Lighting;
using Driftwood.Core.Meshing;
using Driftwood.Core.Physics;
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

    /// <summary>Share of the surface at or below sea level, 0..0.9.</summary>
    public float OceanCoverage { get; init; } = TerrainGenerator.DefaultOceanCoverage;

    public bool VSync { get; init; }
    public int Width { get; init; } = 1600;
    public int Height { get; init; } = 900;

    /// <summary>A texture pack folder or .zip to import block textures from, or null for our own.</summary>
    public string? PackPath { get; init; }

    /// <summary>Tile resolution the texture array is built at.</summary>
    public int TextureSize { get; init; } = 16;

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

    /// <summary>What gets placed on right-click until an inventory decides otherwise.</summary>
    private BlockId _heldBlock;

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

    private static readonly Vector3 SkyColor = new(0.55f, 0.69f, 0.86f);

    // A fixed mid-morning sun. Becomes a moving light driven by the day/night clock at P9;
    // the shader already takes it as a direction so that change stays on this side.
    private static readonly Vector3 SunDirection = Vector3.Normalize(new Vector3(0.42f, 0.80f, 0.30f));
    private static readonly Vector3 SunColor = new Vector3(0.98f, 0.94f, 0.84f) * 0.62f;
    private static readonly Vector3 SkyAmbient = new(0.44f, 0.50f, 0.62f);
    private static readonly Vector3 GroundAmbient = new(0.22f, 0.20f, 0.17f);

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
        _input = _window.CreateInput();
        _keyboard = _input.Keyboards[0];
        _mouse = _input.Mice[0];

        _keyboard.KeyDown += OnKeyDown;
        _mouse.MouseMove += OnMouseMove;
        _mouse.MouseDown += OnMouseDown;
        _mouse.MouseUp += OnMouseUp;

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

        _textures = BlockTextureSet.Build(_options.PackPath, _options.TextureSize);
        _blockTextures = new BlockTextureArray(_gl, _textures.Tiles, _textures.Size);
        Console.WriteLine($"textures    {_textures.Summary}");

        var skin = PlayerSkin.Build(_options.SkinPath, _options.Arms);
        _playerRenderer = new PlayerRenderer(_gl, skin);
        Console.WriteLine($"skin        {skin.Summary}");

        var cracks = CrackTextures.Build(_options.PackPath, _options.TextureSize);
        _cracks = new BlockCracks(_gl, cracks);
        Console.WriteLine($"cracks      {cracks.Summary}");

        BuildWorld();
    }

    private void BuildWorld()
    {
        var registry = new BlockRegistry();
        var ids = StarterBlocks.Register(registry);
        registry.Seal();
        _registry = registry;

        var generator = new TerrainGenerator(_options.Seed, ids, _options.OceanCoverage);

        // --chunks used to size a fixed box; it now sets how far the world is kept loaded around
        // the viewer, which is the same dial pointed at a world that no longer has edges.
        var viewRadius = Math.Max(2, _options.ChunksAcross / 2);
        _viewRadius = viewRadius;

        // The pack's colormaps if it ships them, ours otherwise — so an imported pack's grass is
        // the colour its author chose, not the colour we would have chosen for it.
        var tinter = new BlockTinter(
            new ClimateField(_options.Seed), _textures.GrassMap, _textures.FoliageMap);

        _streamer = new WorldStreamer(registry, generator, viewRadius, tinter: tinter);

        var reach = viewRadius * Chunk.Size;
        _fogEnd = MathF.Min(reach * 0.90f, 700f);
        _fogStart = _fogEnd * 0.55f;
        _camera.FarPlane = _fogEnd + 200f;

        _player = new PlayerBody(registry);
        _heldBlock = ids.Planks;
        _solid = registry.BuildSolidTable();

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
        switch (key)
        {
            case Key.Escape:
                SetMouseCaptured(!_mouseCaptured);
                break;
            case Key.F1:
                _wireframe = !_wireframe;
                _gl.PolygonMode(TriangleFace.FrontAndBack, _wireframe ? PolygonMode.Line : PolygonMode.Fill);
                break;

            // Toggling culling must change the chunk count in the title and nothing on screen.
            // If anything pops in or out, the planes are wrong.
            case Key.F2:
                _frustumCulling = !_frustumCulling;
                break;

            // Leaving the fly camera in reach. It is how terrain gets looked at, and a bug you can
            // only reach by walking to it is a bug you look at twice.
            case Key.F3:
                if (_bench is not null) break;
                _walking = !_walking;
                if (_walking)
                {
                    _player.Teleport(_camera.Position - new Vector3(0f, _player.CurrentEyeHeight, 0f));
                    _spawned = false;
                }
                break;

            // First, over the shoulder, then facing. The middle one is the reason the model exists;
            // the third is how you look at your own skin, and every game in the genre has it.
            case Key.F5:
                if (_bench is not null) break;
                _view = _view switch
                {
                    ViewMode.First => ViewMode.ThirdBehind,
                    ViewMode.ThirdBehind => ViewMode.ThirdFacing,
                    _ => ViewMode.First,
                };
                break;
        }
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

        UpdateTarget();
        StepAnimation((float)dt);
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

        if (!_mining.Update(dt, target, cell, _holdingBreak)) return;

        BreakTarget();
        UpdateTarget();
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

    /// <summary>Finds the block under the crosshair, if any is within reach.</summary>
    private void UpdateTarget()
    {
        _target = _bench is not null
            ? null
            : BlockRay.TryCast(_streamer.World, _targetable, _camera.Position, _camera.Forward, Reach, out var hit)
                ? hit
                : null;
    }

    /// <summary>Removes the targeted block.</summary>
    private void BreakTarget()
    {
        if (_target is not { } hit) return;
        _streamer.EditBlock(hit.X, hit.Y, hit.Z, BlockId.Air);
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

        var (x, y, z) = hit.Adjacent;
        if (!_streamer.World.GetBlock(x, y, z).IsAir) return;

        if (_walking)
        {
            var probe = _streamer.World;
            var before = probe.GetBlock(x, y, z);
            probe.SetBlock(x, y, z, _heldBlock);
            var blocked = _player.Collides(probe, _player.Position);
            probe.SetBlock(x, y, z, before);
            if (blocked) return;
        }

        _streamer.EditBlock(x, y, z, _heldBlock);
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

        var wish = Vector3.Zero;
        if (_keyboard.IsKeyPressed(Key.Up) || _keyboard.IsKeyPressed(Key.W)) wish += forward;
        if (_keyboard.IsKeyPressed(Key.Down) || _keyboard.IsKeyPressed(Key.S)) wish -= forward;
        if (_keyboard.IsKeyPressed(Key.Right) || _keyboard.IsKeyPressed(Key.D)) wish += right;
        if (_keyboard.IsKeyPressed(Key.Left) || _keyboard.IsKeyPressed(Key.A)) wish -= right;

        var jump = _keyboard.IsKeyPressed(Key.Space);
        var sneak = _keyboard.IsKeyPressed(Key.ControlLeft);
        var sprint = _keyboard.IsKeyPressed(Key.ShiftLeft) && !sneak;

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

        _chunkShader.Use();
        _chunkShader.SetMatrix4("uViewProj", viewProj);
        _chunkShader.SetVec3("uCameraPos", _viewPosition);
        _chunkShader.SetVec3("uFogColor", SkyColor);
        _chunkShader.SetVec3("uSunDir", SunDirection);
        _chunkShader.SetVec3("uSunColor", SunColor);
        _chunkShader.SetVec3("uSkyAmbient", SkyAmbient);
        _chunkShader.SetVec3("uGroundAmbient", GroundAmbient);
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

        if (_target is { } hit && !_wireframe)
            _outline.Draw(viewProj, new Vector3(hit.X, hit.Y, hit.Z));

        // Keyed off the mining state's own cell rather than the crosshair's, so cracking is never
        // drawn on a block that is not the one being worked loose.
        if (_mining.Target is { } cell && _mining.Stage >= 0 && !_wireframe)
            _cracks.Draw(viewProj, new Vector3(cell.X, cell.Y, cell.Z), _mining.Stage);

        DrawPlayer(viewProj, projection, view);

        _renderMs = (Stopwatch.GetTimestamp() - renderStart) * TicksToMs;
    }

    /// <summary>
    /// Draws whichever part of the player this view mode can see: all of them, or just the arm.
    /// </summary>
    private void DrawPlayer(Matrix4x4 viewProj, Matrix4x4 projection, Matrix4x4 view)
    {
        if (_bench is not null) return;

        var sky = new SkyParams(
            SunDirection, SunColor, SkyAmbient, GroundAmbient, NightFloor, SkyColor, _fogStart, _fogEnd);

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

        // The sun has to arrive in the same space the geometry is in, or the arm lights from a
        // fixed corner of the screen and swings through its own shading as the player turns.
        _playerRenderer.DrawViewModel(
            projection, Vector3.TransformNormal(SunDirection, view), sky, light,
            _animator.Swinging, _animator.SwingProgress);
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
