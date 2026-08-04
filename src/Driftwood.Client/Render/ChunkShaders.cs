namespace Driftwood.Client.Render;

/// <summary>
/// GLSL for the chunk pass. Kept inline while it is this small; it moves to files once the
/// shading model grows past a single program.
/// </summary>
public static class ChunkShaders
{
    /// <summary>
    /// Unpacks the vertex word and shades from face direction and baked ambient occlusion.
    /// Positions arrive chunk-local, so the chunk's origin rides in a uniform rather than being
    /// baked into every vertex.
    /// </summary>
    public const string Vertex = """
        #version 330 core

        layout(location = 0) in vec3 aPos;
        layout(location = 1) in uint aPacked;

        uniform mat4 uViewProj;
        uniform vec3 uChunkOrigin;
        uniform vec3 uCameraPos;
        uniform vec3 uPalette[64];
        uniform float uFogStart;
        uniform float uFogEnd;

        out vec3 vColor;
        out float vFog;

        // Directional keying stands in for real lighting: sky-facing brightest, ground-facing
        // darkest, the two side axes slightly apart so corners read without any light data.
        const float kFaceLight[6] = float[6](0.78, 0.78, 1.00, 0.52, 0.92, 0.86);

        // Ambient occlusion ramp, 0 = fully enclosed corner.
        const float kAo[4] = float[4](0.46, 0.68, 0.86, 1.00);

        void main()
        {
            vec3 world = uChunkOrigin + aPos;
            gl_Position = uViewProj * vec4(world, 1.0);

            int face  = int(aPacked & 7u);
            int ao    = int((aPacked >> 3) & 3u);
            int layer = int((aPacked >> 8) & 0xFFFFu);

            vColor = uPalette[layer] * kFaceLight[face] * kAo[ao];

            float d = length(world - uCameraPos);
            vFog = clamp((d - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);
        }
        """;

    public const string Fragment = """
        #version 330 core

        in vec3 vColor;
        in float vFog;

        uniform vec3 uFogColor;

        out vec4 FragColor;

        void main()
        {
            FragColor = vec4(mix(vColor, uFogColor, vFog), 1.0);
        }
        """;
}
