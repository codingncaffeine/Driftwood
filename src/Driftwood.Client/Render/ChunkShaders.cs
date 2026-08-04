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

        layout(location = 0) in uint aPacked0;
        layout(location = 1) in uint aPacked1;

        uniform mat4 uViewProj;
        uniform vec3 uChunkOrigin;
        uniform vec3 uCameraPos;
        uniform vec3 uPalette[64];
        uniform vec3 uSunDir;         // surface toward sun, normalised
        uniform vec3 uSunColor;
        uniform vec3 uSkyAmbient;     // ambient arriving from above
        uniform vec3 uGroundAmbient;  // bounce arriving from below
        uniform float uFogStart;
        uniform float uFogEnd;

        out vec3 vColor;
        out float vFog;

        const vec3 kNormals[6] = vec3[6](
            vec3( 1.0, 0.0, 0.0), vec3(-1.0, 0.0, 0.0),
            vec3( 0.0, 1.0, 0.0), vec3( 0.0,-1.0, 0.0),
            vec3( 0.0, 0.0, 1.0), vec3( 0.0, 0.0,-1.0));

        // Ambient occlusion ramp, 0 = fully enclosed corner.
        const float kAo[4] = float[4](0.42, 0.64, 0.84, 1.00);

        void main()
        {
            vec3 local = vec3(
                float( aPacked0        & 63u),
                float((aPacked0 >>  6) & 63u),
                float((aPacked0 >> 12) & 63u));

            vec3 world = uChunkOrigin + local;
            gl_Position = uViewProj * vec4(world, 1.0);

            int face  = int((aPacked0 >> 18) & 7u);
            int ao    = int((aPacked0 >> 21) & 3u);
            int layer = int( aPacked1        & 0xFFFFu);

            vec3 n = kNormals[face];

            // Hemisphere ambient: sky light from above, bounce from the ground below. This is
            // what keeps the two faces the sun cannot reach from collapsing into one flat tone,
            // which a plain N.L term would do.
            float upness = 0.5 + 0.5 * n.y;
            vec3 ambient = mix(uGroundAmbient, uSkyAmbient, upness);

            vec3 sun = uSunColor * max(dot(n, uSunDir), 0.0);

            vColor = uPalette[layer] * (ambient + sun) * kAo[ao];

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
