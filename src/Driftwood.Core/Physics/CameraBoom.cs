using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.World;

namespace Driftwood.Core.Physics;

/// <summary>
/// How far back the third-person camera can sit before the world gets in the way.
/// </summary>
/// <remarks>
/// <para>A boom that ignores terrain is not a small problem. Back into a hillside and the camera
/// ends up inside rock, which means inside the back faces of every chunk around it: the world turns
/// inside out and the player is looking at the sky through the ground. Shortening the boom is the
/// standard answer and it is standard because nothing else works — you cannot cull your way out of
/// being inside geometry.</para>
/// <para>Eight rays rather than one, offset to the corners of a small box, because a single centre
/// ray slides through the gap where two blocks meet at an edge and reports open air on both sides
/// of a wall it is passing through the corner of.</para>
/// </remarks>
public static class CameraBoom
{
    /// <summary>How far back the camera sits when nothing is in the way.</summary>
    public const float Distance = 4f;

    /// <summary>Half-width of the box the boom is swept as. Small: this is a near plane, not a body.</summary>
    public const float Radius = 0.12f;

    /// <summary>Gap kept between the camera and whatever stopped it, so the near plane stays clear.</summary>
    public const float Padding = 0.12f;

    private static readonly Vector3[] Corners = BuildCorners();

    private static Vector3[] BuildCorners()
    {
        var corners = new Vector3[8];
        var i = 0;
        for (var x = -1; x <= 1; x += 2)
        for (var y = -1; y <= 1; y += 2)
        for (var z = -1; z <= 1; z += 2)
            corners[i++] = new Vector3(x, y, z) * Radius;

        return corners;
    }

    /// <summary>
    /// How far along <paramref name="direction"/> the camera may travel from the eye.
    /// </summary>
    /// <remarks>
    /// Unloaded chunks read as air, which is the right answer here: a boom that stopped dead at the
    /// edge of the loaded world would snap the camera in whenever streaming fell behind.
    /// </remarks>
    public static float Reach(VoxelWorld world, bool[] solid, Vector3 eye, Vector3 direction, float desired)
    {
        var reach = desired;

        foreach (var corner in Corners)
        {
            if (!BlockRay.TryCast(world, solid, eye + corner, direction, desired, out var hit)) continue;
            reach = MathF.Min(reach, hit.Distance - Padding);
        }

        return MathF.Max(reach, 0f);
    }

    /// <summary>
    /// Proves the boom both reaches its full length in the open and gives way to a wall, and that
    /// where it ends up is never inside anything solid.
    /// </summary>
    /// <remarks>
    /// Two-sided on purpose. A boom that always returned zero would keep the camera out of every
    /// wall in the game and be completely useless, and a floor-only check passes it happily.
    /// </remarks>
    public static List<string> SelfTest(BlockRegistry registry, BlockId wall)
    {
        var faults = new List<string>();
        var solid = registry.BuildSolidTable();
        var eye = new Vector3(0.5f, 40.5f, 0.5f);

        var open = new VoxelWorld(registry);
        var openReach = Reach(open, solid, eye, Vector3.UnitX, Distance);
        if (MathF.Abs(openReach - Distance) > 1e-3f)
            faults.Add($"open air stopped the boom at {openReach:F2} instead of {Distance:F2}");

        // A wall two blocks out, wide enough that every corner ray meets it rather than sliding past.
        var walled = new VoxelWorld(registry);
        for (var y = 38; y <= 43; y++)
        for (var z = -3; z <= 3; z++)
            walled.SetBlock(2, y, z, wall);

        var walledReach = Reach(walled, solid, eye, Vector3.UnitX, Distance);

        // The wall's near face is 1.5 blocks out. Anything at or past that is inside it; anything
        // under about a block means the boom is collapsing when it should only be shortening.
        if (walledReach is not (> 1.0f and < 1.5f))
            faults.Add($"wall at 1.5 blocks shortened the boom to {walledReach:F2}, wanted 1.0-1.5");

        var camera = eye + Vector3.UnitX * walledReach;
        if (solid[walled.GetBlock(
                (int)MathF.Floor(camera.X), (int)MathF.Floor(camera.Y), (int)MathF.Floor(camera.Z)).Value])
        {
            faults.Add($"camera ended up inside the wall at {camera.X:F2},{camera.Y:F2},{camera.Z:F2}");
        }

        // Backed right up against a block: the boom has to collapse rather than go negative.
        var pressed = new VoxelWorld(registry);
        for (var y = 38; y <= 43; y++)
        for (var z = -3; z <= 3; z++)
            pressed.SetBlock(1, y, z, wall);

        var pressedReach = Reach(pressed, solid, eye, Vector3.UnitX, Distance);
        if (pressedReach < 0f || pressedReach > 0.5f)
            faults.Add($"pressed against a block the boom read {pressedReach:F2}, wanted 0.0-0.5");

        return faults;
    }
}
