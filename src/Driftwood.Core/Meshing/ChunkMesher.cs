using Driftwood.Core.Blocks;
using Driftwood.Core.Textures;
using Driftwood.Core.Lighting;
using Driftwood.Core.World;

namespace Driftwood.Core.Meshing;

/// <summary>
/// Builds renderable geometry for a chunk: culls hidden faces, bakes per-corner ambient
/// occlusion, and merges coplanar faces into the largest rectangles it can.
/// </summary>
/// <remarks>
/// <para>One instance per worker thread. It carries its own snapshot, mask and output buffers so
/// a steady-state remesh allocates nothing — meshing is the hot path of the engine and the thing
/// most likely to surface as a frame hitch.</para>
/// <para>Two paths, because blocks come in two kinds. A block whose model fills its cell takes the
/// greedy path: faces are collected into a per-slice mask and merged into the largest rectangles
/// that agree on texture, occlusion and light. That is nearly the whole world and it has to stay
/// fast. Everything else — a plant, a slab, anything with a shape — takes the per-block path, which
/// emits the model's quads where they stand and merges nothing.</para>
/// <para>A full cube may draw more than one coplanar pass. A grass block is a plain cube and then a
/// tinted cut-out over its four sides; each pass is masked and merged separately, so the overlay
/// costs one quad per wall rather than one per block. Passes are emitted in order and the shader
/// lifts each one a fraction of a block along its normal, which is what keeps the later pass in
/// front of the earlier one without depending on how the two happened to merge.</para>
/// </remarks>
public sealed class ChunkMesher
{
    private readonly bool[] _opaque;

    /// <summary>
    /// What a fluid hides, which identity cannot answer once a fluid has levels.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The cull test was <c>neighbour == here</c>, and it was right for exactly as long as
    /// water was one block.</b> Water level 7 against water level 6 are two different ids, so both
    /// of the faces between them get drawn: a double surface running the whole length of every
    /// river, wasted geometry, and — once water is in a sorted pass — a visible seam. Nothing in a
    /// block census, a vertex count or a face tally would ever have noticed.
    /// </remarks>
    private readonly FluidTable _fluids;

    private readonly ChunkSnapshot _snapshot = new();
    private readonly TintSource[] _tintSource;
    private readonly BlockModel[] _models;
    private readonly BlockRegistry.GreedyTables _greedy;
    private readonly BlockTinter? _tinter;

    /// <summary>The tint colours this chunk uses, interned as they are met.</summary>
    private readonly List<int> _tintPalette = new(ChunkVertex.MaxTints);

    /// <summary>Cells in one slice's mask.</summary>
    private const int SliceCells = Chunk.Size * Chunk.Size;

    /// <summary>
    /// Merge key per cell of the current slice, one plane per coplanar pass: 0 is empty, else
    /// (layer+1) &lt;&lt; 14 | tint &lt;&lt; 8 | packed AO.
    /// </summary>
    /// <remarks>
    /// Every pass of a slice is filled in one traversal, not one each. Occlusion, corner light and
    /// the cull test are the expensive part and none of them depend on which pass is being drawn —
    /// sweeping the slice again per pass made a grass-bearing chunk cost half as much again to mesh
    /// and computed the identical answer both times.
    /// </remarks>
    private readonly int[] _mask = new int[BlockModel.MaxPasses * SliceCells];

    /// <summary>
    /// The same slice's four corner light values, sixteen bits each, shared by every pass. Kept
    /// beside the key rather than inside it because sixty-four bits of light plus the layer no
    /// longer fit in one word, and hashing them down would let two differently-lit faces merge on a
    /// collision — a seam of wrong shading that no geometry check would ever notice.
    /// </summary>
    private readonly ulong[] _maskLight = new ulong[SliceCells];

    private ChunkVertex[] _vertices = new ChunkVertex[16 * 1024];
    private uint[] _indices = new uint[24 * 1024];
    private int _vertexCount;
    private int _indexCount;

    /// <summary>
    /// Indices for the see-through pass, held aside and appended after the opaque ones.
    /// </summary>
    /// <remarks>
    /// ⛳ Collected rather than emitted in place, because the two passes have to be contiguous for a
    /// draw call to be an offset and a count — and the mesher visits faces by direction, so wet and
    /// dry quads arrive interleaved. Both halves index the same vertex buffer.
    /// </remarks>
    private uint[] _late = new uint[4 * 1024];
    private int _lateCount;

    /// <summary>
    /// Which texture layers belong in the second pass, by layer id.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Keyed on the layer rather than on the block, because that is what a quad carries by the
    /// time it is emitted</b> — the greedy path has merged a whole rectangle by then and the block id
    /// is gone. It is exact: nothing but water draws water's tiles, and the table is built from the
    /// registry rather than from a list of layer numbers somebody has to keep up to date.
    /// </remarks>
    private readonly bool[] _lateLayer;

    /// <summary>Coplanar cube passes any block in the chunk being meshed actually draws.</summary>
    private int _passesPresent;

    /// <summary>Whether the chunk being meshed holds anything that is not a full cube.</summary>
    private bool _modelsPresent;

    /// <summary>Quads emitted by the last <see cref="Build"/>, merged and per-block together.</summary>
    public int LastQuadCount { get; private set; }

    /// <summary>
    /// Unit block faces covered by the last <see cref="Build"/>, summing width by height over
    /// every merged quad. Must equal <see cref="CountVisibleFaces"/> for the same chunk.
    /// </summary>
    public int LastCoveredFaces { get; private set; }

    /// <summary>Quads the last <see cref="Build"/> emitted from the per-block model path.</summary>
    public int LastModelQuads { get; private set; }

    /// <param name="tinter">
    /// Supplies climate colours. Null leaves every face untinted, which is what the headless
    /// geometry checks want — they are asking about faces, not about colour.
    /// </param>
    public ChunkMesher(BlockRegistry registry, BlockTinter? tinter = null)
    {
        _opaque = registry.BuildOpacityTable();
        _models = registry.BuildModelTable();
        _greedy = registry.BuildGreedyTables();
        _fluids = new FluidTable(registry);
        _tinter = tinter;

        _tintSource = new TintSource[registry.Count];
        for (var id = 0; id < registry.Count; id++)
            _tintSource[id] = registry[(ushort)id].Tint;

        // Every layer anything see-through draws, off the registry. Lava is deliberately not here: it
        // is opaque and emissive and belongs in the first pass, which is what let it ship before this.
        //
        // ⛔ THIS ASKED `Fluid == FluidKind.Water`, AND THAT WAS A DERIVED RULE. It picked out exactly
        // the right blocks for as long as water was the only thing in the game that blended — the
        // same shape as the drowning test that read water off three flags lava also satisfied.
        // Stained glass is the second thing that wants this pass and is not a fluid at all, so the
        // question is asked outright now. See BlockType.Translucent.
        _lateLayer = new bool[ushort.MaxValue];
        for (var id = 1; id < registry.Count; id++)
        {
            var type = registry[(ushort)id];
            if (type.Fluid != FluidKind.Water && !type.Translucent) continue;

            foreach (var quad in type.Model.Quads) _lateLayer[quad.Layer] = true;
        }
    }

    /// <summary>
    /// Finds this block's tint colour and returns its index in the chunk's palette.
    /// </summary>
    /// <remarks>
    /// Index 0 is always plain white, so an untinted face costs a multiply by one rather than a
    /// branch in the shader. Colours are quantised on the way in: climate is continuous, and
    /// without rounding, neighbouring blocks would each want their own entry and the palette would
    /// overflow within a few metres.
    /// </remarks>
    private int TintIndexFor(ushort block, int wx, int wy, int wz)
    {
        if (_tinter is null) return 0;

        var source = _tintSource[block];
        if (source == TintSource.None) return 0;

        var packed = _tinter.Quantised(source, wx, wy, wz);
        if (packed == 0xFFFFFF) return 0;

        for (var i = 0; i < _tintPalette.Count; i++)
            if (_tintPalette[i] == packed) return i;

        if (_tintPalette.Count >= ChunkVertex.MaxTints) return 0;

        _tintPalette.Add(packed);
        return _tintPalette.Count - 1;
    }

    /// <summary>
    /// Meshes one chunk. Returns null when the chunk contributes no geometry — either it is empty
    /// or every face is buried, both common enough underground that allocating an empty mesh for
    /// them would be wasteful.
    /// </summary>
    public ChunkMeshData? Build(VoxelWorld world, ChunkPos pos)
    {
        LastQuadCount = 0;
        LastCoveredFaces = 0;
        LastModelQuads = 0;

        if (!world.TryGetChunk(pos, out var chunk) || chunk.IsEmpty) return null;

        _snapshot.Capture(world, pos);
        SurveyShapes(chunk);
        _vertexCount = 0;
        _indexCount = 0;
        _lateCount = 0;
        _lateNow = false;

        // Index 0 is white so an untinted face needs no special case in the shader.
        _tintPalette.Clear();
        _tintPalette.Add(BlockTinter.NoTint);

        for (var face = 0; face < Faces.Count; face++)
            MeshDirection(face);

        if (_modelsPresent) MeshModels();

        if (_indexCount + _lateCount == 0) return null;

        // The see-through half, appended so the two passes are contiguous ranges of one buffer.
        var opaqueIndices = _indexCount;
        if (_lateCount > 0)
        {
            while (_indexCount + _lateCount > _indices.Length)
                Array.Resize(ref _indices, _indices.Length * 2);

            Array.Copy(_late, 0, _indices, _indexCount, _lateCount);
            _indexCount += _lateCount;
        }

        var palette = new float[_tintPalette.Count * 3];
        for (var i = 0; i < _tintPalette.Count; i++)
        {
            palette[i * 3] = ((_tintPalette[i] >> 16) & 0xFF) / 255f;
            palette[i * 3 + 1] = ((_tintPalette[i] >> 8) & 0xFF) / 255f;
            palette[i * 3 + 2] = (_tintPalette[i] & 0xFF) / 255f;
        }

        return new ChunkMeshData
        {
            Position = pos,
            Vertices = _vertices.AsSpan(0, _vertexCount).ToArray(),
            Indices = _indices.AsSpan(0, _indexCount).ToArray(),
            OpaqueIndexCount = opaqueIndices,
            TintPalette = palette,
        };
    }

    /// <summary>
    /// One linear pass over the chunk to find how much of the mesher it needs.
    /// </summary>
    /// <remarks>
    /// Cheap next to what it saves. The overlay pass costs a full mask sweep over every slice in
    /// every direction, and underground — most of the volume — nothing draws one. Reading 32k
    /// contiguous shorts to find that out takes a fraction of the sweep it skips.
    /// </remarks>
    private void SurveyShapes(Chunk chunk)
    {
        _passesPresent = 1;
        _modelsPresent = false;

        var raw = chunk.Raw;
        foreach (var id in raw)
        {
            if (id == 0) continue;
            if (!_greedy.FullCube[id]) { _modelsPresent = true; continue; }
            if (_greedy.PassCount[id] > _passesPresent) _passesPresent = _greedy.PassCount[id];
        }
    }

    /// <summary>
    /// Counts visible unit faces the simple way, one block at a time. Independent of the merge
    /// path, so it can be used to prove the merge neither dropped nor invented surface.
    /// </summary>
    /// <remarks>
    /// Counts cube passes only. The per-block path merges nothing, so there is no merge to check
    /// there, and folding its quads in would only make this number disagree with itself.
    /// </remarks>
    public int CountVisibleFaces(VoxelWorld world, ChunkPos pos)
    {
        if (!world.TryGetChunk(pos, out var chunk) || chunk.IsEmpty) return 0;

        _snapshot.Capture(world, pos);
        SurveyShapes(chunk);

        var count = 0;
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        for (var x = 0; x < Chunk.Size; x++)
        {
            var here = _snapshot.Get(x, y, z);
            if (here == 0 || !_greedy.FullCube[here]) continue;

            for (var face = 0; face < Faces.Count; face++)
            {
                var n = Faces.Normals[face];
                var neighbour = _snapshot.Get(x + n.X, y + n.Y, z + n.Z);
                if (Hidden(here, neighbour)) continue;

                for (var pass = 0; pass < _passesPresent; pass++)
                    if (_greedy.LayerFor(here, pass, face) != BlockModel.NoLayer) count++;
            }
        }

        return count;
    }

    /// <summary>
    /// True when the face between these two is not a surface anybody can see.
    /// </summary>
    /// <remarks>
    /// Three ways: something opaque stands in front of it, the neighbour is the identical block and
    /// the seam is interior (leaves against leaves), or both are the same fluid and the neighbour is
    /// at least as deep — which is the case identity used to cover and stopped covering the moment a
    /// fluid had more than one state. Asymmetric on purpose: a deep cell's face toward a shallow one
    /// is a real surface and has to be drawn.
    /// </remarks>
    private bool Hidden(ushort here, ushort neighbour) =>
        _opaque[neighbour] || neighbour == here || _fluids.HiddenBy(here, neighbour);

    private void MeshDirection(int face)
    {
        var n = Faces.Normals[face];
        var axis = n.X != 0 ? 0 : n.Y != 0 ? 1 : 2;

        // The two axes lying in this face's plane, taken in increasing order. The mask is indexed
        // [v, u] over them, and merged quads grow along the same pair.
        var au = axis == 0 ? 1 : 0;
        var av = axis == 2 ? 1 : 2;

        for (var slice = 0; slice < Chunk.Size; slice++)
        {
            BuildMask(face, axis, au, av, slice);

            // Passes in order, so a later one is drawn over the earlier one it shares a plane with.
            for (var pass = 0; pass < _passesPresent; pass++)
                MergeMask(face, pass, axis, au, av, slice);
        }
    }

    private void BuildMask(int face, int axis, int au, int av, int slice)
    {
        Array.Clear(_mask, 0, _passesPresent * SliceCells);
        Array.Clear(_maskLight);

        var n = Faces.Normals[face];
        var offsets = Faces.AoOffsets[face];
        var (ox, oy, oz) = _snapshot.Position.Origin;

        for (var v = 0; v < Chunk.Size; v++)
        for (var u = 0; u < Chunk.Size; u++)
        {
            var (x, y, z) = Compose(axis, au, av, slice, u, v);

            var here = _snapshot.Get(x, y, z);
            if (here == 0 || !_greedy.FullCube[here]) continue;

            var neighbour = _snapshot.Get(x + n.X, y + n.Y, z + n.Z);

            // Hidden behind something opaque, or an interior seam between two blocks of the same
            // see-through kind (water against water, leaves against leaves).
            if (Hidden(here, neighbour)) continue;

            var nx = x + n.X;
            var ny = y + n.Y;
            var nz = z + n.Z;

            var ao = 0;
            ulong light = 0;
            for (var c = 0; c < 4; c++)
            {
                ao |= AmbientOcclusion(offsets[c], nx, ny, nz) << (c * 2);
                light |= (ulong)CornerLight(offsets[c], nx, ny, nz) << (c * 16);
            }

            var cell = v * Chunk.Size + u;
            _maskLight[cell] = light;

            for (var pass = 0; pass < _passesPresent; pass++)
            {
                var layer = _greedy.LayerFor(here, pass, face);
                if (layer == BlockModel.NoLayer) continue;

                var tint = _greedy.TintedFor(here, pass, face)
                    ? TintIndexFor(here, ox + x, oy + y, oz + z)
                    : 0;

                _mask[pass * SliceCells + cell] = ((layer + 1) << 14) | (tint << 8) | ao;
            }
        }
    }

    /// <summary>
    /// Averages light over the four cells that touch this corner from the lit side of the face.
    /// </summary>
    /// <remarks>
    /// The same four cells ambient occlusion samples, for the same reason: a corner's brightness is
    /// what reaches it, and what reaches it is whatever is standing in the space around it. Taking
    /// the face's single neighbour instead gives every corner of a quad the same value, and light
    /// steps in visible blocks across a wall rather than sliding.
    /// <para>Opaque cells are skipped rather than averaged in as zero. They hold no light to give,
    /// and counting them would ring every wall corner with a dark halo that has nothing to do with
    /// how lit the wall is — occlusion is ambient occlusion's job and it is already doing it.</para>
    /// </remarks>
    private ushort CornerLight((int X, int Y, int Z)[] offsets, int nx, int ny, int nz)
    {
        int sky = 0, red = 0, green = 0, blue = 0, taken = 0;

        Accumulate(nx, ny, nz);
        for (var i = 0; i < 3; i++)
            Accumulate(nx + offsets[i].X, ny + offsets[i].Y, nz + offsets[i].Z);

        if (taken == 0) return 0;

        return LightValue.Pack(sky / taken, red / taken, green / taken, blue / taken);

        void Accumulate(int x, int y, int z)
        {
            if (_opaque[_snapshot.Get(x, y, z)]) return;

            var packed = _snapshot.GetLight(x, y, z);
            sky += LightValue.Sky(packed);
            red += LightValue.Red(packed);
            green += LightValue.Green(packed);
            blue += LightValue.Blue(packed);
            taken++;
        }
    }

    private int AmbientOcclusion((int X, int Y, int Z)[] offsets, int nx, int ny, int nz)
    {
        var side1 = _opaque[_snapshot.Get(nx + offsets[0].X, ny + offsets[0].Y, nz + offsets[0].Z)];
        var side2 = _opaque[_snapshot.Get(nx + offsets[1].X, ny + offsets[1].Y, nz + offsets[1].Z)];

        // Two blocking edges seal the corner; the diagonal behind them cannot lighten it.
        if (side1 && side2) return 0;

        var corner = _opaque[_snapshot.Get(nx + offsets[2].X, ny + offsets[2].Y, nz + offsets[2].Z)];
        return 3 - ((side1 ? 1 : 0) + (side2 ? 1 : 0) + (corner ? 1 : 0));
    }

    /// <summary>
    /// Sweeps the mask, growing each remaining cell into the widest run it can and then the
    /// tallest block of identical runs, emitting one quad per rectangle and clearing it as it goes.
    /// </summary>
    private void MergeMask(int face, int pass, int axis, int au, int av, int slice)
    {
        var plane = pass * SliceCells;

        for (var v = 0; v < Chunk.Size; v++)
        for (var u = 0; u < Chunk.Size;)
        {
            var key = _mask[plane + v * Chunk.Size + u];
            if (key == 0)
            {
                u++;
                continue;
            }

            var light = _maskLight[v * Chunk.Size + u];

            var width = 1;
            while (u + width < Chunk.Size && Matches(plane, v, u + width, key, light))
                width++;

            var height = 1;
            var grew = true;
            while (v + height < Chunk.Size && grew)
            {
                for (var i = 0; i < width; i++)
                {
                    if (Matches(plane, v + height, u + i, key, light)) continue;
                    grew = false;
                    break;
                }
                if (grew) height++;
            }

            for (var dv = 0; dv < height; dv++)
            for (var du = 0; du < width; du++)
                _mask[plane + (v + dv) * Chunk.Size + u + du] = 0;

            EmitQuad(face, pass, axis, au, av, slice, u, v, width, height, key, light);

            u += width;
        }
    }

    /// <summary>Two faces may only join when they agree on texture, occlusion and all four
    /// corner lights.</summary>
    private bool Matches(int plane, int v, int u, int key, ulong light)
    {
        var i = v * Chunk.Size + u;
        return _mask[plane + i] == key && _maskLight[i] == light;
    }

    private void EmitQuad(
        int face, int pass, int axis, int au, int av, int slice, int u, int v, int width, int height,
        int key, ulong light)
    {
        EnsureCapacity();

        var layer = (key >> 14) - 1;
        var tint = (key >> 8) & 0x3F;
        var corners = Faces.Corners[face];

        _lateNow = _lateLayer[layer];

        Span<int> ao = stackalloc int[4];
        for (var c = 0; c < 4; c++) ao[c] = (key >> (c * 2)) & 0x3;

        // Hoisted: a stackalloc inside the loop would not be released until the method returned,
        // so the frame would grow with every corner rather than being reused across them.
        Span<int> unit = stackalloc int[3];
        Span<int> p = stackalloc int[3];

        var baseIndex = (uint)_vertexCount;
        for (var c = 0; c < 4; c++)
        {
            var corner = corners[c];
            unit[0] = corner.X;
            unit[1] = corner.Y;
            unit[2] = corner.Z;

            // The corner's offset along the normal picks the near or far plane and stays a unit
            // step; its offsets in the plane stretch to the merged rectangle's extent.
            p[axis] = slice + unit[axis];
            p[au] = u + unit[au] * width;
            p[av] = v + unit[av] * height;

            var cornerLight = (ushort)((light >> (c * 16)) & 0xFFFF);
            _vertices[_vertexCount++] =
                ChunkVertex.Cube(p[0], p[1], p[2], face, ao[c], pass, layer, cornerLight, tint);
        }

        EmitIndices(baseIndex, ao[0] + ao[2] > ao[1] + ao[3]);

        LastQuadCount++;
        LastCoveredFaces += width * height;
    }

    /// <summary>
    /// Emits every quad of every block whose shape is not a cube, where it stands.
    /// </summary>
    /// <remarks>
    /// No merging, and none is possible: two crossed planes at forty-five degrees share no plane
    /// with anything, and a slab's top surface is not the same height as the block beside it. The
    /// count is what keeps this affordable — a field of tufts is a few hundred quads a chunk
    /// against the tens of thousands the terrain itself costs.
    /// </remarks>
    private void MeshModels()
    {
        var (ox, oy, oz) = _snapshot.Position.Origin;

        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        for (var x = 0; x < Chunk.Size; x++)
        {
            var here = _snapshot.Get(x, y, z);
            if (here == 0 || _greedy.FullCube[here]) continue;

            var quads = _models[here].Quads;
            foreach (var quad in quads)
            {
                if (quad.CullFace >= 0)
                {
                    var cn = Faces.Normals[quad.CullFace];
                    var beyond = _snapshot.Get(x + cn.X, y + cn.Y, z + cn.Z);
                    if (Hidden(here, beyond)) continue;
                }

                EmitModelQuad(quad, here, x, y, z, ox, oy, oz);
            }
        }
    }

    private void EmitModelQuad(ModelQuad quad, ushort block, int x, int y, int z, int ox, int oy, int oz)
    {
        EnsureCapacity();

        var tint = quad.Tinted ? TintIndexFor(block, ox + x, oy + y, oz + z) : 0;
        var face = quad.Shade ? quad.Face : ChunkVertex.UnshadedFace;

        _lateNow = _lateLayer[quad.Layer];

        Span<int> ao = stackalloc int[4];
        Span<ushort> light = stackalloc ushort[4];

        if (quad.Flush)
        {
            // Sitting on the boundary, so the face is lit by what is on the other side of it and
            // occluded by what stands around that cell — the same question a cube face asks.
            var n = Faces.Normals[quad.Face];
            var offsets = Faces.AoOffsets[quad.Face];
            int nx = x + n.X, ny = y + n.Y, nz = z + n.Z;

            for (var c = 0; c < 4; c++)
            {
                ao[c] = quad.Occlude ? AmbientOcclusion(offsets[c], nx, ny, nz) : 3;
                light[c] = CornerLight(offsets[c], nx, ny, nz);
            }
        }
        else
        {
            // Inside its own cell: a plant is lit by the air it stands in, and nothing around that
            // cell occludes a surface that is already inside it.
            var own = _snapshot.GetLight(x, y, z);
            for (var c = 0; c < 4; c++)
            {
                ao[c] = 3;
                light[c] = own;
            }
        }

        var baseIndex = (uint)_vertexCount;
        for (var c = 0; c < 4; c++)
        {
            var corner = quad.Corners[c];
            _vertices[_vertexCount++] = ChunkVertex.Model(
                x + corner.Position.X,
                y + corner.Position.Y,
                z + corner.Position.Z,
                face, ao[c], quad.Layer, light[c], tint, corner.U, corner.V);
        }

        EmitIndices(baseIndex, ao[0] + ao[2] > ao[1] + ao[3]);

        LastQuadCount++;
        LastModelQuads++;
    }

    /// <summary>
    /// Splits a quad along whichever diagonal keeps the shading gradient smooth.
    /// </summary>
    /// <remarks>
    /// Without this the split biases ambient occlusion corners one way, visible as a herringbone
    /// running across flat walls.
    /// </remarks>
    private void EmitIndices(uint baseIndex, bool flip)
    {
        if (_lateNow)
        {
            if (_lateCount + 6 > _late.Length) Array.Resize(ref _late, _late.Length * 2);
            Wind(_late, ref _lateCount);
            return;
        }

        Wind(_indices, ref _indexCount);

        void Wind(uint[] into, ref int at)
        {
            if (flip)
            {
                into[at++] = baseIndex + 1;
                into[at++] = baseIndex + 2;
                into[at++] = baseIndex + 3;
                into[at++] = baseIndex + 1;
                into[at++] = baseIndex + 3;
                into[at++] = baseIndex + 0;
            }
            else
            {
                into[at++] = baseIndex + 0;
                into[at++] = baseIndex + 1;
                into[at++] = baseIndex + 2;
                into[at++] = baseIndex + 0;
                into[at++] = baseIndex + 2;
                into[at++] = baseIndex + 3;
            }
        }
    }

    /// <summary>Set by whichever emitter is about to wind a quad. Read once, immediately.</summary>
    private bool _lateNow;

    /// <summary>Maps slice/u/v on an axis triple back to block coordinates.</summary>
    private static (int X, int Y, int Z) Compose(int axis, int au, int av, int slice, int u, int v)
    {
        Span<int> p = stackalloc int[3];
        p[axis] = slice;
        p[au] = u;
        p[av] = v;
        return (p[0], p[1], p[2]);
    }

    private void EnsureCapacity()
    {
        if (_vertexCount + 4 > _vertices.Length)
            Array.Resize(ref _vertices, _vertices.Length * 2);
        if (_indexCount + 6 > _indices.Length)
            Array.Resize(ref _indices, _indices.Length * 2);
    }
}
