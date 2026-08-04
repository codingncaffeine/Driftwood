using System.Diagnostics;
using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Gen;
using Driftwood.Core.Meshing;
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
    private readonly List<ChunkMeshGpu> _meshes = [];
    private readonly FlyCamera _camera = new();

    private Vector2 _lastMousePos;
    private bool _haveMouseAnchor;
    private bool _mouseCaptured = true;
    private bool _wireframe;

    private double _titleTimer;
    private int _framesSinceTitle;
    private double _fps;

    private int _totalVertices;
    private int _totalTriangles;
    private float _fogStart;
    private float _fogEnd;

    private static readonly Vector3 SkyColor = new(0.55f, 0.69f, 0.86f);

    public ClientHost(ClientOptions options)
    {
        _options = options;

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
        _window.Closing += OnClosing;
    }

    public void Run() => _window.Run();

    private void OnLoad()
    {
        _gl = GL.GetApi(_window);
        _input = _window.CreateInput();
        _keyboard = _input.Keyboards[0];
        _mouse = _input.Mice[0];

        _keyboard.KeyDown += OnKeyDown;
        _mouse.MouseMove += OnMouseMove;
        SetMouseCaptured(true);

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
        var world = new VoxelWorld(registry);

        var across = _options.ChunksAcross;
        var half = across / 2;
        var chunksTall = TerrainGenerator.WorldHeight / Chunk.Size;

        var positions = new List<ChunkPos>(across * across * chunksTall);
        for (var cy = 0; cy < chunksTall; cy++)
        for (var cz = -half; cz < across - half; cz++)
        for (var cx = -half; cx < across - half; cx++)
            positions.Add(new ChunkPos(cx, cy, cz));

        var total = Stopwatch.StartNew();

        // Create every chunk on this thread first: the dictionary is not concurrent, and the
        // parallel pass below must only ever touch chunks that already exist.
        var chunks = new Chunk[positions.Count];
        for (var i = 0; i < positions.Count; i++)
            chunks[i] = world.GetOrCreateChunk(positions[i]);

        var genWatch = Stopwatch.StartNew();
        Parallel.For(0, chunks.Length, i => generator.GenerateChunk(chunks[i]));
        genWatch.Stop();

        // Trees write across chunk seams, so decoration is single-threaded for now.
        var decorWatch = Stopwatch.StartNew();
        var minBlock = -half * Chunk.Size;
        var maxBlock = (across - half) * Chunk.Size - 1;
        generator.DecorateRegion(world, minBlock, minBlock, maxBlock, maxBlock);
        decorWatch.Stop();

        var meshWatch = Stopwatch.StartNew();
        var meshed = new ChunkMeshData?[positions.Count];
        Parallel.For(
            0,
            positions.Count,
            () => new ChunkMesher(registry),
            (i, _, mesher) =>
            {
                meshed[i] = mesher.Build(world, positions[i]);
                return mesher;
            },
            _ => { });
        meshWatch.Stop();

        var uploadWatch = Stopwatch.StartNew();
        foreach (var data in meshed)
        {
            if (data is null) continue;
            _meshes.Add(new ChunkMeshGpu(_gl, data));
            _totalVertices += data.VertexCount;
            _totalTriangles += data.TriangleCount;
        }
        uploadWatch.Stop();
        total.Stop();

        var extent = across * Chunk.Size;
        _fogEnd = MathF.Min(extent * 0.45f, 700f);
        _fogStart = _fogEnd * 0.55f;
        _camera.FarPlane = _fogEnd + 200f;

        // Drop the camera above the terrain at the centre of the generated box.
        var surface = generator.SurfaceHeight(0, 0);
        _camera.Position = new Vector3(0f, surface + 28f, 0f);
        _camera.Pitch = -22f;

        Console.WriteLine($"seed        {_options.Seed}");
        Console.WriteLine($"world       {extent}x{TerrainGenerator.WorldHeight}x{extent} blocks, {positions.Count} chunks");
        Console.WriteLine($"ocean       {generator.OceanCoverage * 100:F0}% of surface at or below sea level {TerrainGenerator.SeaLevel}");
        Console.WriteLine($"generate    {genWatch.ElapsedMilliseconds} ms");
        Console.WriteLine($"decorate    {decorWatch.ElapsedMilliseconds} ms");
        Console.WriteLine($"mesh        {meshWatch.ElapsedMilliseconds} ms  ({_meshes.Count} chunks with geometry)");
        Console.WriteLine($"upload      {uploadWatch.ElapsedMilliseconds} ms");
        Console.WriteLine($"total       {total.ElapsedMilliseconds} ms");
        Console.WriteLine($"geometry    {_totalVertices:N0} verts, {_totalTriangles:N0} tris");
        Console.WriteLine();
        Console.WriteLine("WASD move, Space/Ctrl up-down, Shift boost, Alt slow, Esc release mouse, F1 wireframe");
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
        _camera.Update((float)dt, _keyboard);

        _titleTimer += dt;
        _framesSinceTitle++;
        if (_titleTimer >= 0.25)
        {
            _fps = _framesSinceTitle / _titleTimer;
            _titleTimer = 0;
            _framesSinceTitle = 0;

            var p = _camera.Position;
            _window.Title =
                $"Driftwood — {_fps:F0} fps | seed {_options.Seed} | " +
                $"xyz {p.X:F0} {p.Y:F0} {p.Z:F0} | " +
                $"{_meshes.Count} chunks, {_totalTriangles:N0} tris";
        }
    }

    private void OnRender(double _)
    {
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        var size = _window.FramebufferSize;
        var aspect = size.Y > 0 ? size.X / (float)size.Y : 1f;

        _chunkShader.Use();
        _chunkShader.SetMatrix4("uViewProj", _camera.ViewProjection(aspect));
        _chunkShader.SetVec3("uCameraPos", _camera.Position);
        _chunkShader.SetVec3("uFogColor", SkyColor);
        _chunkShader.SetFloat("uFogStart", _fogStart);
        _chunkShader.SetFloat("uFogEnd", _fogEnd);
        _chunkShader.SetVec3Array("uPalette", StarterBlocks.PaletteRgb);

        foreach (var mesh in _meshes)
        {
            _chunkShader.SetVec3("uChunkOrigin", mesh.Origin);
            mesh.Draw();
        }
    }

    private void OnResize(Vector2D<int> size) => _gl.Viewport(size);

    private void OnClosing()
    {
        foreach (var mesh in _meshes) mesh.Dispose();
        _meshes.Clear();
        _chunkShader.Dispose();
    }

    public void Dispose()
    {
        _input?.Dispose();
        _window.Dispose();
    }
}
