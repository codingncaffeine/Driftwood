namespace Driftwood.Client.Render;

/// <summary>
/// GLSL for the chunk pass. Kept inline while it is this small; it moves to files once the
/// shading model grows past a single program.
/// </summary>
public static class ChunkShaders
{
    /// <summary>
    /// Unpacks the vertex words and shades from baked light, ambient occlusion and face direction.
    /// Positions arrive chunk-local, so the chunk's origin rides in a uniform rather than being
    /// baked into every vertex.
    /// </summary>
    public const string Vertex = """
        #version 330 core

        layout(location = 0) in uint aPacked0;
        layout(location = 1) in uint aPacked1;
        layout(location = 2) in uint aPacked2;

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
        uniform float uTime;          // the world's clock, wrapped host-side to keep sin() exact

        out vec3 vLight;
        out vec3 vUvw;
        out float vFog;
        out vec3 vWorld;
        out vec3 vShade;   // AO and climate tint alone, for light added per fragment
        out vec3 vNormal;
        out vec3 vSun;     // direct-sun color and sky visibility; angle is evaluated per fragment

        // Index 6 is the model format's "do not shade": a plant's two crossed planes face opposite
        // ways and must not come out one bright and one dark, so both are lit as if facing up.
        const vec3 kNormals[8] = vec3[8](
            vec3( 1.0, 0.0, 0.0), vec3(-1.0, 0.0, 0.0),
            vec3( 0.0, 1.0, 0.0), vec3( 0.0,-1.0, 0.0),
            vec3( 0.0, 0.0, 1.0), vec3( 0.0, 0.0,-1.0),
            vec3( 0.0, 1.0, 0.0), vec3( 0.0, 1.0, 0.0));

        // Ambient occlusion ramp, 0 = fully enclosed corner.
        const float kAo[4] = float[4](0.42, 0.64, 0.84, 1.00);

        const float kPositionScale = 1.0 / 64.0;   // steps per block, matching ChunkVertex
        const float kPositionBias  = 64.0;
        const float kUvScale       = 1.0 / 32.0;   // steps per tile

        // How far each later coplanar pass floats off the one under it. A grass block's tinted
        // fringe sits in exactly the same plane as the dirt beneath it, and which of two coplanar
        // quads wins the depth test comes down to rounding unless one of them is lifted. Two
        // thousandths of a block is a thirtieth of a texel — far below anything visible, and far
        // above the error in a plane equation.
        const float kCoplanarLift = 0.002;

        void main()
        {
            vec3 local = (vec3(
                float( aPacked0        & 4095u),
                float((aPacked0 >> 12) & 4095u),
                float( aPacked1        & 4095u)) - kPositionBias) * kPositionScale;

            int face  = int((aPacked0 >> 24) & 7u);
            int ao    = int((aPacked0 >> 27) & 3u);
            int pass  = int((aPacked0 >> 29) & 3u);
            int layer = int((aPacked1 >> 12) & 4095u);
            int tint  = int((aPacked1 >> 24) & 63u);
            bool explicitUv = ((aPacked1 >> 30) & 1u) != 0u;

            vec3 n = kNormals[face];
            vNormal = n;
            vec3 world = uChunkOrigin + local;
            gl_Position = uViewProj * vec4(world + n * (float(pass) * kCoplanarLift), 1.0);

            // Baked light: sky in the low nibble, then red, green, blue.
            uint packedLight = aPacked2 & 0xFFFFu;
            float sky   = float( packedLight        & 15u) / 15.0;
            vec3  block = vec3(
                float((packedLight >>  4) & 15u),
                float((packedLight >>  8) & 15u),
                float((packedLight >> 12) & 15u)) / 15.0;

            // Sunlight reaching this corner is the baked visibility of the sky multiplied by how
            // squarely the face meets the sun, plus the sky's own ambient for the faces it misses.
            // Both are gated on the same baked term, which is what puts a cave in the dark while
            // the hillside above it stays lit.
            float upness = 0.5 + 0.5 * n.y;
            vec3 skyAmbient = mix(uGroundAmbient, uSkyAmbient, upness);
            vec3 daylight = skyAmbient * sky;

            // ⛳ FIRELIGHT SWAYS AND DAYLIGHT DOES NOT. Two slow waves out of phase, sampled at
            // the corner's own position, dim the block-light term by up to a tenth — so the glow
            // a lantern throws breathes ALONG a wall rather than the room pulsing as one sheet,
            // and two lamps apart from each other waver apart. Dimming only, never brightening:
            // the baked value stays the ceiling, and the sun — being the other arm of the max
            // below — never moves at all. The light itself is still baked; only the shimmer is
            // computed, which is what makes this affordable where re-flooding light would not be.
            float sway = sin(uTime * 2.6 + world.x * 1.7 + world.z * 2.3)
                       + sin(uTime * 7.1 + world.y * 2.9 - world.x * 1.1);
            block *= 0.95 + 0.025 * sway;

            // Block light and daylight do not add: a torch in a lit room is invisible, exactly as
            // it should be, and a torch in a cave is the only thing there. Taking the brighter of
            // the two per channel is what keeps a warm source reading warm against cold daylight
            // instead of washing to white wherever the two overlap.
            vec3 light = max(daylight, block);

            // Climate colour folds into the light term rather than being sent on separately: both
            // end up multiplying the texel, and doing it here costs one lookup a vertex instead of
            // one a fragment.
            vLight = max(light, uNightFloor) * kAo[ao] * uTint[tint];
            vSun = uSunColor * sky * kAo[ao] * uTint[tint];

            // And the same two factors alone, for the carried light the fragment shader adds —
            // it has to sit under the same shade and tint as the baked light or a hand-lit
            // corner ignores its own occlusion.
            vShade = kAo[ao] * uTint[tint];
            vWorld = world;

            // A merged cube face takes its texture coordinates from where the corner is in the
            // world, projected onto the two axes lying in its face. Nothing is stored per vertex: a
            // quad spanning six blocks lands on uv 0..6 and the sampler's repeat wrapping tiles it,
            // which is exactly what a wall of the same block should look like.
            //
            // A model quad carries its own instead, because a shape has no such projection to fall
            // back on: a plant's planes are turned forty-five degrees, and a torch reads a strip
            // out of the middle of its tile. The six expressions below are also what the model
            // baker uses to work out where an element's default coordinates fall, so both answers
            // come from one statement of the convention.
            vec2 uv;
            if (explicitUv)     uv = vec2(float((aPacked2 >> 16) & 63u),
                                          float((aPacked2 >> 22) & 63u)) * kUvScale;
            else if (face == 0) uv = vec2(-world.z, -world.y);   // +X
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
        in vec3 vWorld;
        in vec3 vShade;
        in vec3 vNormal;
        in vec3 vSun;

        uniform sampler2DArray uBlocks;
        uniform sampler2DArray uNormals;
        uniform sampler2DArray uMaterials;
        uniform sampler2DArrayShadow uShadowMap;
        uniform sampler2D uOpaqueColor;
        uniform sampler2D uOpaqueDepth;
        uniform vec3 uFogColor;
        uniform vec3 uCameraPos;
        uniform float uAlpha;
        uniform vec3 uHeldPos;     // where the carried light is, in world space
        uniform vec3 uHeldLight;   // its colour and strength, zero when the hands are dark
        uniform float uHeldRange;  // blocks to nothing — one number, owned by the host
        uniform float uTime;       // shared with the vertex stage: same clock, same sway
        uniform vec3 uSunDir;
        uniform mat4 uShadow0;
        uniform mat4 uShadow1;
        uniform mat4 uShadow2;
        uniform vec3 uShadowSplits;
        uniform int uShadowEnabled;
        uniform int uMaterialsEnabled;
        uniform int uWaterPass;
        uniform int uWaterEffects;
        uniform vec2 uViewport;
        uniform mat4 uViewProj;
        uniform float uNear;
        uniform float uFar;

        out vec4 FragColor;

        float linearDepth(float depth)
        {
            float z = depth * 2.0 - 1.0;
            return (2.0 * uNear * uFar) / max(uFar + uNear - z * (uFar - uNear), 0.0001);
        }

        vec3 materialNormal(vec3 baseNormal)
        {
            if (uMaterialsEnabled == 0) return normalize(baseNormal);
            vec3 tangentNormal = texture(uNormals, vUvw).xyz * 2.0 - 1.0;
            vec3 q1 = dFdx(vWorld);
            vec3 q2 = dFdy(vWorld);
            vec2 s1 = dFdx(vUvw.xy);
            vec2 s2 = dFdy(vUvw.xy);
            vec3 tangent = q1 * s2.y - q2 * s1.y;
            vec3 bitangent = -q1 * s2.x + q2 * s1.x;
            if (dot(tangent, tangent) < 0.000001 || dot(bitangent, bitangent) < 0.000001)
                return normalize(baseNormal);
            mat3 frame = mat3(normalize(tangent), normalize(bitangent), normalize(baseNormal));
            return normalize(frame * tangentNormal);
        }

        float sunShadow(vec3 normal)
        {
            if (uShadowEnabled == 0) return 1.0;
            float distanceToEye = distance(vWorld, uCameraPos);
            int cascade = distanceToEye <= uShadowSplits.x ? 0
                        : distanceToEye <= uShadowSplits.y ? 1 : 2;
            vec4 lightClip = cascade == 0 ? uShadow0 * vec4(vWorld, 1.0)
                           : cascade == 1 ? uShadow1 * vec4(vWorld, 1.0)
                                          : uShadow2 * vec4(vWorld, 1.0);
            vec3 projected = lightClip.xyz / max(abs(lightClip.w), 0.00001) * 0.5 + 0.5;
            if (projected.z <= 0.0 || projected.z >= 1.0
                || any(lessThan(projected.xy, vec2(0.002)))
                || any(greaterThan(projected.xy, vec2(0.998)))) return 1.0;

            float bias = max(0.00020 * (1.0 - dot(normal, uSunDir)), 0.000035);
            float texel = 1.0 / float(textureSize(uShadowMap, 0).x);
            float visibility = 0.0;
            for (int y = -1; y <= 1; ++y)
            for (int x = -1; x <= 1; ++x)
                visibility += texture(uShadowMap,
                    vec4(projected.xy + vec2(x, y) * texel, float(cascade), projected.z - bias));
            return visibility / 9.0;
        }

        vec3 screenReflection(vec3 origin, vec3 direction, out float hit)
        {
            hit = 0.0;
            vec3 reflected = uFogColor;
            for (int step = 1; step <= 12; ++step)
            {
                vec3 point = origin + direction * (float(step) * 1.65);
                vec4 clip = uViewProj * vec4(point, 1.0);
                if (clip.w <= 0.0) break;
                vec3 ndc = clip.xyz / clip.w;
                vec2 uv = ndc.xy * 0.5 + 0.5;
                if (any(lessThan(uv, vec2(0.002))) || any(greaterThan(uv, vec2(0.998)))) break;
                float rayDepth = ndc.z * 0.5 + 0.5;
                float sceneDepth = texture(uOpaqueDepth, uv).r;
                float crossing = rayDepth - sceneDepth;
                if (crossing > 0.0002 && crossing < 0.018)
                {
                    reflected = texture(uOpaqueColor, uv).rgb;
                    hit = 1.0;
                    break;
                }
            }
            return reflected;
        }

        vec3 enhancedWater(vec3 tint)
        {
            vec2 screenUv = gl_FragCoord.xy / max(uViewport, vec2(1.0));
            float surface = linearDepth(gl_FragCoord.z);
            float behind = linearDepth(texture(uOpaqueDepth, screenUv).r);
            float thickness = clamp(behind - surface, 0.0, 48.0);

            float waveX = sin(vWorld.x * 0.31 + uTime * 1.23)
                        + sin(vWorld.z * 0.57 - uTime * 0.71);
            float waveZ = cos(vWorld.z * 0.27 + uTime * 0.93)
                        + cos(vWorld.x * 0.49 + uTime * 0.54);
            vec3 waveNormal = normalize(vec3(waveX * 0.075, 1.0, waveZ * 0.075));
            vec2 refractUv = clamp(screenUv + waveNormal.xz * min(0.014, thickness * 0.0012),
                                   vec2(0.001), vec2(0.999));
            vec3 behindColor = texture(uOpaqueColor, refractUv).rgb;

            vec3 absorption = exp(-vec3(0.11, 0.045, 0.022) * thickness);
            vec3 waterColor = mix(tint * vec3(0.36, 0.72, 0.90), tint, 0.28);
            vec3 refracted = behindColor * absorption + waterColor * (vec3(1.0) - absorption);

            vec3 eyeDirection = normalize(vWorld - uCameraPos);
            vec3 reflectedDirection = normalize(reflect(eyeDirection, waveNormal));
            float reflectionHit;
            vec3 reflection = screenReflection(vWorld + waveNormal * 0.04,
                                                reflectedDirection, reflectionHit);
            reflection = mix(uFogColor * 1.08, reflection, reflectionHit);
            float fresnel = 0.035 + 0.48 * pow(1.0 - max(dot(-eyeDirection, waveNormal), 0.0), 5.0);
            float shore = smoothstep(0.0, 1.35, thickness);
            return mix(behindColor, mix(refracted, reflection, fresnel), 0.25 + shore * 0.75);
        }

        void main()
        {
            vec4 texel = texture(uBlocks, vUvw);

            // Cutout, not blending. Leaves and vines are mostly holes; discarding them outright
            // keeps them in the opaque pass, where they need no sorting and still write depth.
            if (texel.a < 0.5) discard;

            if (uWaterPass != 0 && uWaterEffects != 0)
            {
                vec3 color = enhancedWater(texel.rgb * vShade);
                FragColor = vec4(mix(color, uFogColor, vFog), 1.0);
                return;
            }

            // ⛳ THE LIGHT CARRIED IN A HAND: a point of the held block's own colours, falling
            // linearly to nothing, breathing with the same firelight sway as everything else,
            // under the fragment's own occlusion and tint. Per FRAGMENT rather than per vertex,
            // because greedy quads span whole walls — a light evaluated only at a quad's corners
            // leaves the wall dark at its middle, which is exactly where you are standing.
            // It knows nothing of walls (no flood runs here), so a thin wall leaks a faint glow;
            // at this radius that is the trade every carried light in the genre makes.
            vec3 held = uHeldLight * clamp(1.0 - distance(vWorld, uHeldPos) / uHeldRange, 0.0, 1.0);
            float sway = sin(uTime * 2.6 + vWorld.x * 1.7 + vWorld.z * 2.3)
                       + sin(uTime * 7.1 + vWorld.y * 2.9 - vWorld.x * 1.1);
            held *= 0.95 + 0.025 * sway;

            vec3 normal = materialNormal(vNormal);
            float shadow = sunShadow(normal);
            float diffuse = max(dot(normal, uSunDir), 0.0);
            vec3 light = max(vLight, held * vShade) + vSun * diffuse * shadow;

            vec4 material = uMaterialsEnabled != 0
                ? texture(uMaterials, vUvw) : vec4(0.0, 0.74, 0.0, 1.0);
            float metalness = material.r;
            float roughness = clamp(material.g, 0.04, 1.0);
            float emissive = material.b;
            vec3 viewDirection = normalize(uCameraPos - vWorld);
            vec3 halfway = normalize(uSunDir + viewDirection);
            float exponent = mix(112.0, 3.0, roughness * roughness);
            float highlight = pow(max(dot(normal, halfway), 0.0), exponent)
                              * (1.0 - roughness * 0.72) * diffuse * shadow;
            vec3 f0 = mix(vec3(0.035), texel.rgb, metalness);
            vec3 color = texel.rgb * light + f0 * vSun * highlight
                         + texel.rgb * emissive * 1.65;

            // ⛳ Alpha belongs to the PASS, not to the tile. Water's own texture is fully opaque and
            // has to stay that way — the cutout discard above and the whole mip chain are built on
            // every block texture being one or the other — so what makes a lake see-through is which
            // pass drew it. The opaque pass sets this to one and pays nothing.
            FragColor = vec4(mix(color, uFogColor, vFog), uAlpha);
        }
        """;
}
