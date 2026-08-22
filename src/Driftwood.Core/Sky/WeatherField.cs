using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Lighting;
using Driftwood.Core.World;

namespace Driftwood.Core.Sky;

/// <summary>
/// Where every drop of rain and flake of snow is this instant, round the eye — and which of them
/// exist at all, because precipitation only falls where the sky can see the cell.
/// </summary>
/// <remarks>
/// <para>⛳ <b>The user, 2026-08-22: "weather like snow or rain should not be visible below the
/// surface — unless there's a crater or something, then obviously that's below the surface but
/// you'd still see snow there like irl."</b> So the rule is sky exposure per CELL, not height: a
/// drop exists where the stored sky light is full, which is exactly a cell with nothing but air
/// between it and the sky — a pit, a crater or a ravine open overhead still rains, a cave, a
/// canopy or a roof does not. The light engine already maintains that fact for every loaded cell
/// and every edit, so there is no second map to keep in step with the world.</para>
/// <para>⛳ <b>Placed on the CPU rather than hashed in the vertex shader</b>, which is what the
/// renderer did before: the rule needs the world, and a shader cannot ask it. The cost is 768
/// light reads a frame, and the prize is that the same code answers a headless check with no
/// window at all — and that the renderer's count of drops is a count of drops, not of points.</para>
/// </remarks>
public static class WeatherField
{
    /// <summary>How many drops surround the eye when the sky is open everywhere.</summary>
    public const int ParticleCount = 768;

    /// <summary>The box round the eye the drops live in, in cells.</summary>
    public static readonly Vector3 Extent = new(56f, 38f, 56f);

    /// <summary>
    /// Fills <paramref name="into"/> with the world position of every drop that exists this
    /// instant, and answers how many there are.
    /// </summary>
    /// <param name="exposed">Whether a cell is open to the sky. The game answers with the light
    /// engine; a check answers with whatever fixture it built.</param>
    public static int Fill(
        Span<Vector3> into, in WeatherState weather, Vector3 eye, float time, long seed,
        Func<int, int, int, bool> exposed)
    {
        if (!weather.Active) return 0;

        var snow = weather.Kind == Precipitation.Snow;
        var speed = snow ? 3.4f : 25f;
        var salt = Math.Abs(seed % 8_191);
        var count = 0;

        for (var i = 0; i < ParticleCount && count < into.Length; i++)
        {
            var id = i + salt;
            var rx = Hash(id * 17.13f);
            var ry = Hash(id * 41.71f + 3.1f);
            var rz = Hash(id * 73.37f + 9.7f);

            var ox = (rx - 0.5f) * Extent.X;
            var oy = (ry - 0.5f) * Extent.Y;
            var oz = (rz - 0.5f) * Extent.Z;

            // Falling: the column wraps round the box so a drop that leaves the bottom re-enters
            // the top, at the speed of its kind.
            oy = Mod(oy - time * speed + Extent.Y / 2f, Extent.Y) - Extent.Y / 2f;

            if (snow)
            {
                ox += MathF.Sin(time * 0.8f + id) * 1.8f;
                oz += MathF.Cos(time * 0.6f + id * 0.7f) * 1.4f;
            }

            var world = eye + new Vector3(ox, oy, oz);
            if (!exposed(Floor(world.X), Floor(world.Y), Floor(world.Z))) continue;

            into[count++] = world;
        }

        return count;
    }

    /// <summary>
    /// The game's own answer to "is this cell open to the sky": an AIR cell with full stored sky
    /// light, which the engine gives only to a cell with nothing but air straight up to the sky —
    /// not the hour's brightness, so it is as true at midnight as at noon.
    /// </summary>
    /// <remarks>
    /// ⚠ Air as well as lit, measured: the engine stores full sky light in the topmost solid cell
    /// of a column too (the face it lights), so light alone put two drops a frame inside the rock.
    /// And a drop has no business inside water, a leaf or glass either.
    /// </remarks>
    public static bool SkyExposed(VoxelWorld world, int x, int y, int z) =>
        world.GetBlock(x, y, z).IsAir && LightValue.Sky(world.GetLight(x, y, z)) >= LightValue.Max;

    private static int Floor(float v) => (int)MathF.Floor(v);

    private static float Mod(float a, float b) => a - b * MathF.Floor(a / b);

    private static float Hash(float n)
    {
        var v = MathF.Sin(n) * 43_758.5453f;
        return v - MathF.Floor(v);
    }

    /// <summary>
    /// Builds a plain, a roofed cave under it and an open pit in it, lights them with the real
    /// engine, and checks where the weather falls — positive control first.
    /// </summary>
    /// <remarks>
    /// ⛔ A check that only asserted "nothing underground" passes a build that draws no weather at
    /// all. The open field is the positive control and has to come up full; the pit is the user's
    /// crater and has to rain; the cave is the complaint and has to be dry.
    /// </remarks>
    public static List<string> SelfTest(BlockRegistry registry, out string detail)
    {
        var faults = new List<string>();

        var world = new VoxelWorld(registry);
        var stone = registry.ByName("stone").Id;
        var rain = new WeatherState(Precipitation.Rain, 1f);
        Span<Vector3> drops = stackalloc Vector3[ParticleCount];

        // A slab of stone from y 40 to y 70 across a wide plain; the eye will stand on top, in a
        // cave dug into it, and in a pit cut into it from above.
        const int Half = 64;
        for (var x = -Half; x <= Half; x++)
        for (var z = -Half; z <= Half; z++)
        for (var y = 40; y <= 70; y++)
            world.SetBlock(x, y, z, stone);

        // The cave: a 9×5×9 room with its floor at y 50, roofed by twenty cells of rock.
        for (var x = -4; x <= 4; x++)
        for (var z = -4; z <= 4; z++)
        for (var y = 51; y <= 55; y++)
            world.SetBlock(x, y, z, BlockId.Air);

        // The pit: a 7×7 shaft from the surface down to y 61, open to the sky. Placed past the open
        // field's 28-cell box (rain in the pit is real rain, not a fault there) and with the slab
        // running well past the pit's own box, because air past the slab's edge counts as sky.
        for (var x = 32; x <= 38; x++)
        for (var z = 32; z <= 38; z++)
        for (var y = 61; y <= 70; y++)
            world.SetBlock(x, y, z, BlockId.Air);

        new LightEngine(registry).LightAll(world);
        bool Exposed(int x, int y, int z) => SkyExposed(world, x, y, z);

        // Positive control: the eye on the open plain. Every drop above the ground exists, and
        // that is nearly all of them — the box is 38 tall and the eye stands 1.6 over the surface.
        var open = Fill(drops, rain, new Vector3(0.5f, 72.6f, 0.5f), 3.7f, 12_345L, Exposed);
        if (open < ParticleCount * 0.45f)
            faults.Add($"an open field in the rain has {open} drops of {ParticleCount}");

        var belowGround = 0;
        var where = "";
        for (var i = 0; i < open; i++)
        {
            if (drops[i].Y >= 71f) continue;
            belowGround++;
            var (cx, cy, cz) = (Floor(drops[i].X), Floor(drops[i].Y), Floor(drops[i].Z));
            where = $"first at {cx},{cy},{cz} = {registry[world.GetBlock(cx, cy, cz)].Name}, sky {LightValue.Sky(world.GetLight(cx, cy, cz))}";
        }
        if (belowGround > 0)
            faults.Add($"{belowGround} drops fell inside solid rock under the open field ({where})");

        // The complaint: the eye in the cave. Not one drop under the surface, at any time of the
        // fall — the ones the box reaches ABOVE the plain are real rain, out of sight behind rock.
        var inCave = 0;
        for (var t = 0f; t < 4f; t += 0.37f)
        {
            var n = Fill(drops, rain, new Vector3(0.5f, 52.6f, 0.5f), t, 12_345L, Exposed);
            for (var i = 0; i < n; i++) if (drops[i].Y < 71f) inCave++;
        }
        if (inCave != 0)
            faults.Add($"{inCave} drops fell in a roofed cave over eleven instants");

        // The user's crater: the eye at the bottom of the pit. Drops exist in its column above the
        // floor, none in the rock round it.
        var inPit = 0;
        var inRock = 0;
        for (var t = 0f; t < 4f; t += 0.37f)
        {
            var n = Fill(drops, rain, new Vector3(35.5f, 62.6f, 35.5f), t, 12_345L, Exposed);
            for (var i = 0; i < n; i++)
            {
                var inShaft = drops[i].X >= 32f && drops[i].X < 39f && drops[i].Z >= 32f && drops[i].Z < 39f;
                if (drops[i].Y < 71f && inShaft) inPit++;
                if (drops[i].Y < 71f && !inShaft) inRock++;
            }
        }
        if (inPit == 0)
            faults.Add("no drop fell into an open pit, which rains like anywhere else");
        if (inRock != 0)
            faults.Add($"{inRock} drops fell inside the rock round the pit");

        // And clear weather is no weather, whatever the sky.
        if (Fill(drops, new WeatherState(Precipitation.Clear, 0f), new Vector3(0.5f, 72.6f, 0.5f), 1f, 1L, Exposed) != 0)
            faults.Add("drops fell out of a clear sky");

        detail = $"open field {open}/{ParticleCount} drops, roofed cave 0, open pit {inPit} over eleven instants, rock 0";
        return faults;
    }
}
