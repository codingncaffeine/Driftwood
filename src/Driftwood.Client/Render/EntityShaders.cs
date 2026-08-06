namespace Driftwood.Client.Render;

/// <summary>
/// GLSL for anything made of skinned boxes: the player model and the first-person arm.
/// </summary>
/// <remarks>
/// <para>Its own program rather than a branch in the chunk shader. The chunk pass takes packed
/// integer vertices, derives uvs from world position and reads a per-chunk tint palette; none of
/// that means anything to a model whose whole surface is one 64×64 sheet with real uvs on it.</para>
/// <para>The lighting maths, though, is deliberately the same arithmetic in the same order. A model
/// lit by its own rules is the thing that makes a character look pasted onto a world rather than
/// standing in it — it goes wrong most visibly at dusk and in caves, where the two models of light
/// disagree most. The only differences here are that light is sampled once for the whole model
/// rather than baked per vertex, and that there is no ambient occlusion term to apply.</para>
/// </remarks>
public static class EntityShaders
{
    public const string Vertex = """
        #version 330 core

        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;

        uniform mat4 uViewProj;
        uniform mat4 uModel;
        uniform vec3 uCameraPos;
        uniform vec3 uSunDir;
        uniform vec3 uSunColor;
        uniform vec3 uSkyAmbient;
        uniform vec3 uGroundAmbient;
        uniform vec3 uNightFloor;
        uniform float uSky;          // baked sunlight in the cell the model stands in, 0..1
        uniform vec3 uBlockLight;    // and its coloured block light
        uniform float uFogStart;
        uniform float uFogEnd;

        out vec3 vLight;
        out vec2 vUv;
        out float vFog;

        void main()
        {
            vec3 world = (uModel * vec4(aPos, 1.0)).xyz;
            gl_Position = uViewProj * vec4(world, 1.0);

            vec3 n = normalize(mat3(uModel) * aNormal);

            float upness = 0.5 + 0.5 * n.y;
            vec3 skyAmbient = mix(uGroundAmbient, uSkyAmbient, upness);
            vec3 sun = uSunColor * max(dot(n, uSunDir), 0.0);
            vec3 daylight = (skyAmbient + sun) * uSky;

            vLight = max(max(daylight, uBlockLight), uNightFloor);
            vUv = aUv;

            float d = length(world - uCameraPos);
            vFog = clamp((d - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);
        }
        """;

    public const string Fragment = """
        #version 330 core

        in vec3 vLight;
        in vec2 vUv;
        in float vFog;

        uniform sampler2D uSkin;
        uniform vec3 uFogColor;

        // How hard this thing has just been hit, 0..1. ⛳ An unset uniform is zero and zero is "not
        // hit", so the renderers that have nothing to say about it — the player, the held arm — say
        // nothing and get the right answer. A tint written as a multiply would have gone black.
        uniform float uHurt;

        out vec4 FragColor;

        void main()
        {
            vec4 texel = texture(uSkin, vUv);

            // Cutout rather than blending. A skin's overlay layer is mostly holes, and discarding
            // them keeps the whole model in the opaque pass where it needs no sorting against
            // itself — an arm passing in front of a torso is exactly the case blending gets wrong.
            if (texel.a < 0.5) discard;

            vec3 lit = texel.rgb * vLight;

            // ⚠ Mixed toward red AFTER the lighting, so a blow lands visibly on an animal standing
            // in a cave. Tinting the texture first would put the flash under the light and a hit in
            // the dark would be a slightly darker cow.
            lit = mix(lit, vec3(0.85, 0.12, 0.10), uHurt * 0.65);

            FragColor = vec4(mix(lit, uFogColor, vFog), 1.0);
        }
        """;
}
