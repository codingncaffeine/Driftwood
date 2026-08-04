using System.Diagnostics;
using System.Numerics;
using Driftwood.Client.Diagnostics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Gen;
using Driftwood.Core.Meshing;
using Driftwood.Core.Spatial;
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

    private void OnLoad()
    {
        _gl = GL.GetApi(_window);
        _input = _window.CreateInput();
        _keyboard = _input.Keyboards[0];
        _mouse = _input.Mice[0];

        _keyboard.KeyDown += OnKeyDown;
        _mouse.MouseMove += OnMouseMove;

        // The benchmark flies itself, so it leaves the cursor alone; stealing the mouse for a
        // measurement run is rude and changes nothing about what is measured.
        SetMouseCaptured(_options.BenchSeconds <= 0);

        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);
        _gl.ClearColor(SkyColor.X, SkyColor.Y, SkyColor.Z, 1f);

        _chunkShader = new Shader(_gl, ChunkShaders.Vertex, ChunkShaders.Fragment);

        BuildWorld();
    }

    private void BuildWorld()
    {
        var registry = new BlockRegistry();
        var ids = StarterBlocks.Register(registry);
        registry.Seal();

        var generator = new TerrainGenerator(_options.Seed, ids, _options.OceanCoverage);

        // --chunks used to size a fixed box; it now sets how far the world is kept loaded around
        // the viewer, which is the same dial pointed at a world that no longer has edges.
        var viewRadius = Math.Max(2, _options.ChunksAcross / 2);
        _viewRadius = viewRadius;
        _streamer = new WorldStreamer(registry, generator, viewRadius);

        var reach = viewRadius * Chunk.Size;
        _fogEnd = MathF.Min(reach * 0.90f, 700f);
        _fogStart = _fogEnd * 0.55f;
        _camera.FarPlane = _fogEnd + 200f;

        if (_options.BenchSeconds > 0) SetUpBench(generator, viewRadius);
        else
        {
            var surface = generator.SurfaceHeight(0, 0);
            _camera.Position = new Vector3(0f, surface + 28f, 0f);
            _camera.Pitch = -22f;
        }

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

        Console.WriteLine("Arrows move (WASD also works), Space/PgUp up, Ctrl/PgDn down, Shift boost, Alt slow");
        Console.WriteLine("Esc release mouse, F1 wireframe, F2 frustum culling");
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
        }
    }

    private void SetMouseCaptured(bool captured)
    {
        _mouseCaptured = captured;
        _mouse.Cursor.CursorMode = captured ? CursorMode.Raw : CursorMode.Normal;
        _haveMouseAnchor = false;
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
        else
        {
            _camera.Update((float)dt, _keyboard);
        }

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

        var viewProj = _camera.ViewProjection(aspect);
        var frustum = Frustum.FromViewProjection(viewProj);

        _chunkShader.Use();
        _chunkShader.SetMatrix4("uViewProj", viewProj);
        _chunkShader.SetVec3("uCameraPos", _camera.Position);
        _chunkShader.SetVec3("uFogColor", SkyColor);
        _chunkShader.SetVec3("uSunDir", SunDirection);
        _chunkShader.SetVec3("uSunColor", SunColor);
        _chunkShader.SetVec3("uSkyAmbient", SkyAmbient);
        _chunkShader.SetVec3("uGroundAmbient", GroundAmbient);
        _chunkShader.SetVec3("uNightFloor", NightFloor);
        _chunkShader.SetFloat("uFogStart", _fogStart);
        _chunkShader.SetFloat("uFogEnd", _fogEnd);
        _chunkShader.SetVec3Array("uPalette", StarterBlocks.PaletteRgb);

        var drawn = 0;
        var triangles = 0;
        foreach (var mesh in _meshes.Values)
        {
            if (!_frustumCulling) { }
            else if (!frustum.IntersectsBox(mesh.BoundsMin, mesh.BoundsMax)) continue;

            _chunkShader.SetVec3("uChunkOrigin", mesh.Origin);
            mesh.Draw();
            drawn++;
            triangles += mesh.IndexCount / 3;
        }

        _drawnChunks = drawn;
        _drawnTriangles = triangles;
        _renderMs = (Stopwatch.GetTimestamp() - renderStart) * TicksToMs;
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
    }

    public void Dispose()
    {
        Shutdown();
        _input?.Dispose();
        _window.Dispose();
    }
}
