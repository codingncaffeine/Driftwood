using System.Numerics;
using Driftwood.Core.Textures;
using Silk.NET.OpenGL;

namespace Driftwood.Client.Render;

/// <summary>
/// The cracking drawn over a block being mined.
/// </summary>
/// <remarks>
/// <para>A thin shell swelled a fraction of a block outward from the cell being worked on. The
/// swell is what keeps it off the surface it is describing — coplanar with the block face it would
/// z-fight, and this is on screen during the most-watched action in the game.</para>
/// <para>Culling is turned off for the pass rather than the winding being got right. The shell is
/// wrapped around something opaque that has already written depth, so its far faces fail the depth
/// test anyway and only the near ones ever appear. Twelve triangles is not worth a class of bug
/// whose symptom is an invisible overlay.</para>
/// </remarks>
public sealed class BlockCracks : IDisposable
{
    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aUv;
        uniform mat4 uViewProj;
        uniform vec3 uOrigin;
        uniform vec3 uMin;
        uniform vec3 uMax;
        uniform float uSwell;
        out vec2 vUv;
        void main()
        {
            vec3 centre = (uMin + uMax) * 0.5;
            vec3 local = mix(uMin, uMax, aPos);
            gl_Position = uViewProj * vec4(uOrigin + centre + (local - centre) * (1.0 + uSwell), 1.0);
            vUv = aUv;
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2DArray uCracks;
        uniform float uStage;
        out vec4 FragColor;
        void main()
        {
            vec4 texel = texture(uCracks, vec3(vUv, uStage));
            if (texel.a < 0.02) discard;
            FragColor = texel;
        }
        """;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly BlockTextureArray _stages;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private readonly int _indexCount;

    public unsafe BlockCracks(GL gl, CrackTextures.Result cracks)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _stages = new BlockTextureArray(gl, cracks.Stages, cracks.Size);

        var vertices = new List<float>();
        var indices = new List<uint>();

        // Six faces, each a unit square in the cube's 0..1 space with uv running across it. The
        // texture is square and the block is a cube, so nothing here needs to know which face it is.
        Span<(Vector3 Origin, Vector3 Across, Vector3 Down)> faces =
        [
            (new Vector3(1, 1, 1), new Vector3(0, 0, -1), new Vector3(0, -1, 0)),   // +X
            (new Vector3(0, 1, 0), new Vector3(0, 0, 1), new Vector3(0, -1, 0)),    // -X
            (new Vector3(0, 1, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 1)),     // +Y
            (new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(0, 0, -1)),    // -Y
            (new Vector3(0, 1, 1), new Vector3(1, 0, 0), new Vector3(0, -1, 0)),    // +Z
            (new Vector3(1, 1, 0), new Vector3(-1, 0, 0), new Vector3(0, -1, 0)),   // -Z
        ];

        foreach (var (origin, across, down) in faces)
        {
            var first = (uint)(vertices.Count / 5);

            for (var corner = 0; corner < 4; corner++)
            {
                var u = corner is 1 or 2 ? 1f : 0f;
                var v = corner is 2 or 3 ? 1f : 0f;
                var p = origin + across * u + down * v;

                vertices.AddRange([p.X, p.Y, p.Z, u, v]);
            }

            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        _indexCount = indices.Count;

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        var vertexData = vertices.ToArray();
        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = vertexData)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertexData.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        var indexData = indices.ToArray();
        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* p = indexData)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indexData.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);

        var stride = (uint)(5 * sizeof(float));
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));

        _gl.BindVertexArray(0);
    }

    /// <summary>Draws one stage of cracking over the shape in the cell at <paramref name="blockOrigin"/>.</summary>
    /// <param name="min">The shape's own lower corner within the cell, in block units.</param>
    /// <param name="max">Its upper corner. A full cube is 0 to 1.</param>
    public unsafe void Draw(Matrix4x4 viewProj, Vector3 blockOrigin, Vector3 min, Vector3 max, int stage)
    {
        _shader.Use();
        _shader.SetMatrix4("uViewProj", viewProj);
        _shader.SetVec3("uOrigin", blockOrigin);
        _shader.SetVec3("uMin", min);
        _shader.SetVec3("uMax", max);
        _shader.SetFloat("uSwell", 0.006f);
        _shader.SetFloat("uStage", stage);
        _shader.SetInt("uCracks", 0);
        _stages.Bind();

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.CullFace);
        _gl.DepthMask(false);

        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)_indexCount, DrawElementsType.UnsignedInt, (void*)0);
        _gl.BindVertexArray(0);

        // Put the pipeline back exactly as it was found. The chunk pass depends on all three of
        // these and does not set them per frame.
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.CullFace);
        _gl.Disable(EnableCap.Blend);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        _stages.Dispose();
        _shader.Dispose();
    }
}
