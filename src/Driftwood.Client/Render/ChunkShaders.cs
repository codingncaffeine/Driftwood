namespace Driftwood.Client.Render;

/// <summary>
/// GLSL for the chunk pass. Kept inline while it is this small; it moves to files once the
/// shading model grows past a single program.
/// </summary>
public static class ChunkShaders
{
    /// <summary>
    /// Unpacks the vertex word and shades from baked light, ambient occlusion and face direction.
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
        uniform vec3 uSunDir;         // surface toward sun, normalised
        uniform vec3 uSunColor;       // colour and strength of direct sun at this time of day
        uniform vec3 uSkyAmbient;     // ambient arriving from above
        uniform vec3 uGroundAmbient;  // bounce arriving from below
        uniform vec3 uNightFloor;     // what a cell with no light at all is allowed to keep
        uniform vec3 uTint[64];       // this chunk's climate colours; entry 0 is white
        uniform float uFogStart;
        uniform float uFogEnd;

        out vec3 vLight;
        out vec3 vUvw;
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
            int tint  = int((aPacked0 >> 23) & 63u);
            int layer = int( aPacked1        & 0xFFFFu);

            // Baked light: sky in the low nibble, then red, green, blue.
            uint packedLight = aPacked1 >> 16;
            float sky   = float( packedLight        & 15u) / 15.0;
            vec3  block = vec3(
                float((packedLight >>  4) & 15u),
                float((packedLight >>  8) & 15u),
                float((packedLight >> 12) & 15u)) / 15.0;

            vec3 n = kNormals[face];

            // Sunlight reaching this corner is the baked visibility of the sky multiplied by how
            // squarely the face meets the sun, plus the sky's own ambient for the faces it misses.
            // Both are gated on the same baked term, which is what puts a cave in the dark while
            // the hillside above it stays lit.
            float upness = 0.5 + 0.5 * n.y;
            vec3 skyAmbient = mix(uGroundAmbient, uSkyAmbient, upness);
            vec3 sun = uSunColor * max(dot(n, uSunDir), 0.0);
            vec3 daylight = (skyAmbient + sun) * sky;

            // Block light and daylight do not add: a torch in a lit room is invisible, exactly as
            // it should be, and a torch in a cave is the only thing there. Taking the brighter of
            // the two per channel is what keeps a warm source reading warm against cold daylight
            // instead of washing to white wherever the two overlap.
            vec3 light = max(daylight, block);

            // Climate colour folds into the light term rather than being sent on separately: both
            // end up multiplying the texel, and doing it here costs one lookup a vertex instead of
            // one a fragment.
            vLight = max(light, uNightFloor) * kAo[ao] * uTint[tint];

            // Texture coordinates come from where the corner is in the world, projected onto the
            // two axes lying in its face. Nothing is stored per vertex: a merged quad spanning six
            // blocks lands on uv 0..6 and the sampler's repeat wrapping tiles it, which is exactly
            // what a wall of the same block should look like. Storing uvs would have cost eight
            // more bytes a vertex to say something the position already knows.
            vec2 uv;
            if (face == 0)      uv = vec2(-world.z, -world.y);   // +X
            else if (face == 1) uv = vec2( world.z, -world.y);   // -X
            else if (face == 2) uv = vec2( world.x,  world.z);   // +Y
            else if (face == 3) uv = vec2( world.x, -world.z);   // -Y
            else if (face == 4) uv = vec2( world.x, -world.y);   // +Z
            else                uv = vec2(-world.x, -world.y);   // -Z

            vUvw = vec3(uv, float(layer));

            float d = length(world - uCameraPos);
            vFog = clamp((d - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);
        }
        """;

    public const string Fragment = """
        #version 330 core

        in vec3 vLight;
        in vec3 vUvw;
        in float vFog;

        uniform sampler2DArray uBlocks;
        uniform vec3 uFogColor;

        out vec4 FragColor;

        void main()
        {
            vec4 texel = texture(uBlocks, vUvw);

            // Cutout, not blending. Leaves and vines are mostly holes; discarding them outright
            // keeps them in the opaque pass, where they need no sorting and still write depth.
            if (texel.a < 0.5) discard;

            FragColor = vec4(mix(texel.rgb * vLight, uFogColor, vFog), 1.0);
        }
        """;
}
