using Driftwood.Core.World;

namespace Driftwood.Core.Blocks;

/// <summary>What a logic block computes from its inputs. None for everything that is not one.</summary>
public enum GateKind
{
    None = 0,

    /// <summary>Out when both sides are in.</summary>
    And,

    /// <summary>Out when either side is.</summary>
    Or,

    /// <summary>Out when exactly one side is.</summary>
    Xor,

    /// <summary>Out when the back is quiet — the inverter, and two of them are a repeater.</summary>
    Not,

    /// <summary>Set by the left, cleared by the right, remembering otherwise.</summary>
    Latch,
}

/// <summary>
/// Everything the signal pass needs to know about a block, read off the registry once.
/// </summary>
/// <remarks>
/// <para>Flat arrays by raw id, the <see cref="FluidTable"/> shape, for the same reason: the relax
/// asks these questions once per cell per wavefront and a dictionary would be most of its cost.</para>
/// <para>⛳ <b>Strength lives in the block id</b> — <c>tidewire_0..15</c> are sixteen registered
/// blocks — so wire SAVES through the name palette, remeshes through the ordinary path, and streams,
/// none of which a strength array beside the chunk would get without new machinery. The edit log is
/// a dictionary keyed on the cell, so a wire re-strengthing writes over its own entry rather than
/// growing the save; a component is bounded by wire somebody placed.</para>
/// </remarks>
public sealed class SignalTable
{
    /// <summary>A source's full strength, and the most a wire can carry.</summary>
    public const int Max = 15;

    /// <summary>Wire strength by id, or -1 for everything that is not wire.</summary>
    private readonly int[] _wire;

    /// <summary>Wire id by strength.</summary>
    private readonly ushort[] _wireFor = new ushort[Max + 1];

    /// <summary>True where the id is a source in its emitting state — a thrown lever, a held
    /// button, a stood-on plate.</summary>
    private readonly bool[] _sourceOn;

    private readonly GateKind[] _gateKind;

    /// <summary>The face a gate's output leaves by, in <see cref="Faces"/> order.</summary>
    private readonly int[] _gateFacing;

    private readonly bool[] _gateOn;

    /// <summary>A gate's other state — on for off, off for on.</summary>
    private readonly ushort[] _gateTwin;

    /// <summary>
    /// A sink's other state: a door's open form for its shut one, the lamp's lit for its dark.
    /// </summary>
    private readonly ushort[] _sinkTwin;

    /// <summary>True where the id IS a sink's powered form.</summary>
    private readonly bool[] _sinkPowered;

    private readonly bool[] _sink;

    /// <summary>
    /// Sinks whose visible state can also be chosen by hand. Unlike a lamp, a door follows a
    /// signal <em>edge</em>: an unchanged quiet wire must not undo the player's right click.
    /// </summary>
    private readonly bool[] _handToggle;

    private readonly bool[] _pressedButton;

    /// <summary>The way to a door's other half, or -1 — copied off the registry.</summary>
    private readonly int[] _partner;

    public SignalTable(BlockRegistry registry)
    {
        _wire = new int[registry.Count];
        _sourceOn = new bool[registry.Count];
        _gateKind = new GateKind[registry.Count];
        _gateFacing = new int[registry.Count];
        _gateOn = new bool[registry.Count];
        _gateTwin = new ushort[registry.Count];
        _sinkTwin = new ushort[registry.Count];
        _sinkPowered = new bool[registry.Count];
        _sink = new bool[registry.Count];
        _handToggle = new bool[registry.Count];
        _pressedButton = new bool[registry.Count];
        _partner = new int[registry.Count];

        Array.Fill(_wire, -1);

        for (var id = 0; id < registry.Count; id++)
        {
            var type = registry[(ushort)id];
            var name = type.Name;
            _partner[id] = type.PartnerFace;

            if (name.StartsWith("tidewire_", StringComparison.Ordinal)
                && int.TryParse(name.AsSpan("tidewire_".Length), out var strength))
            {
                _wire[id] = strength;
                _wireFor[strength] = (ushort)id;
                continue;
            }

            if (name.StartsWith("lever", StringComparison.Ordinal))
            {
                _sourceOn[id] = name.EndsWith("_on", StringComparison.Ordinal);
                continue;
            }

            if (name.StartsWith("button", StringComparison.Ordinal))
            {
                var pressed = name.EndsWith("_pressed", StringComparison.Ordinal);
                _sourceOn[id] = pressed;
                _pressedButton[id] = pressed;
                continue;
            }

            if (name.StartsWith("pressure_plate", StringComparison.Ordinal))
            {
                _sourceOn[id] = name.EndsWith("_on", StringComparison.Ordinal);
                continue;
            }

            if (name.StartsWith("gate_", StringComparison.Ordinal))
            {
                ReadGate(registry, (ushort)id, name);
                continue;
            }

            // ⛳ The booster answers the wire exactly as a lamp does: a sink pair by name. Rails
            // land after signals so a station is a lever, a wire and two boosters, with nothing
            // new taught to either side.
            if (name.StartsWith("powered_rail", StringComparison.Ordinal))
            {
                var boosting = name.EndsWith("_on", StringComparison.Ordinal);
                _sink[id] = true;
                _sinkPowered[id] = boosting;
                _sinkTwin[id] = registry
                    .ByName(boosting ? name[..^"_on".Length] : name + "_on").Id.Value;
                continue;
            }

            // ⛳ The blastcask is a sink with ONE direction: the cold cask answers power by
            // becoming the lit one, and the lit form is deliberately not a sink at all — a fuse
            // does not go out when the lever drops. The client hears the switch and lights the
            // fuse; what happens from there is Blastcask's file to say.
            if (name == Blastcask.Cold)
            {
                _sink[id] = true;
                _sinkPowered[id] = false;
                _sinkTwin[id] = registry.ByName(Blastcask.Lit).Id.Value;
                continue;
            }

            // The sinks: the tidelamp pair, and every door and trapdoor — whose powered state is
            // its open one. Wet trapdoors carry the same "_open" in their names, so the sea makes
            // no difference to a hinge.
            if (name.StartsWith("tidelamp", StringComparison.Ordinal))
            {
                var lit = name.EndsWith("_lit", StringComparison.Ordinal);
                _sink[id] = true;
                _sinkPowered[id] = lit;
                _sinkTwin[id] = registry.ByName(lit ? "tidelamp" : "tidelamp_lit").Id.Value;
                continue;
            }

            if ((name.StartsWith("door_", StringComparison.Ordinal)
                 || name.StartsWith("trapdoor_", StringComparison.Ordinal))
                && TwinByOpen(registry, name) is { } twin)
            {
                _sink[id] = true;
                _handToggle[id] = true;
                _sinkPowered[id] = name.Contains("_open", StringComparison.Ordinal);
                _sinkTwin[id] = twin;
            }
        }
    }

    /// <summary>gate_{kind}_{facing}[_on] → the three facts, off the name that registered it.</summary>
    private void ReadGate(BlockRegistry registry, ushort id, string name)
    {
        var on = name.EndsWith("_on", StringComparison.Ordinal);
        var stem = on ? name[..^"_on".Length] : name;

        var parts = stem.Split('_');
        if (parts.Length != 3) return;

        _gateKind[id] = parts[1] switch
        {
            "and" => GateKind.And,
            "or" => GateKind.Or,
            "xor" => GateKind.Xor,
            "not" => GateKind.Not,
            "latch" => GateKind.Latch,
            _ => GateKind.None,
        };
        if (_gateKind[id] == GateKind.None) return;

        _gateFacing[id] = parts[2] switch
        {
            "east" => Faces.PosX,
            "west" => Faces.NegX,
            "south" => Faces.PosZ,
            _ => Faces.NegZ,
        };
        _gateOn[id] = on;
        _gateTwin[id] = registry.ByName(on ? stem : stem + "_on").Id.Value;
    }

    /// <summary>The same name with "_open" the other way round, when that block exists.</summary>
    private static ushort? TwinByOpen(BlockRegistry registry, string name)
    {
        var twin = name.Contains("_open", StringComparison.Ordinal)
            ? name.Replace("_open", "", StringComparison.Ordinal)
            : InsertOpen(name);

        return registry.TryByName(twin, out var type) ? type.Id.Value : null;
    }

    /// <summary>Puts "_open" where the naming scheme keeps it: before any "_waterlogged".</summary>
    private static string InsertOpen(string name) =>
        name.EndsWith(Waterlogging.Suffix, StringComparison.Ordinal)
            ? name[..^Waterlogging.Suffix.Length] + "_open" + Waterlogging.Suffix
            : name + "_open";

    public int WireStrength(ushort id) => _wire[id];

    public BlockId WireFor(int strength) => new(_wireFor[Math.Clamp(strength, 0, Max)]);

    public bool IsWire(ushort id) => _wire[id] >= 0;

    public bool SourceOn(ushort id) => _sourceOn[id];

    public GateKind KindOf(ushort id) => _gateKind[id];

    public int GateFacing(ushort id) => _gateFacing[id];

    public bool GateOn(ushort id) => _gateOn[id];

    public BlockId GateTwin(ushort id) => new(_gateTwin[id]);

    public bool IsSink(ushort id) => _sink[id];

    public bool CanToggleByHand(ushort id) => _handToggle[id];

    public bool SinkPowered(ushort id) => _sinkPowered[id];

    public BlockId SinkTwin(ushort id) => new(_sinkTwin[id]);

    public bool IsPressedButton(ushort id) => _pressedButton[id];

    public int PartnerFace(ushort id) => _partner[id];

    /// <summary>How many wire strengths, live sources, lit gate forms and sinks the registry
    /// yields — the check that this table is not empty.</summary>
    public (int Wires, int Sources, int Gates, int Sinks) Census()
    {
        int wires = 0, sources = 0, gates = 0, sinks = 0;
        for (var id = 0; id < _wire.Length; id++)
        {
            if (_wire[id] >= 0) wires++;
            if (_sourceOn[id]) sources++;
            if (_gateKind[id] != GateKind.None && _gateOn[id]) gates++;
            if (_sink[id]) sinks++;
        }
        return (wires, sources, gates, sinks);
    }
}

/// <summary>
/// Makes signals travel: full strength at a source, one lost per wire cell, sinks following the
/// level, and gates computing on their own tick.
/// </summary>
/// <remarks>
/// <para>⛔ <b>A real queue over the component, never one ring</b> — the card's own warning. A
/// strength change is precisely the event that makes a further cell want to change; that is what
/// propagation is. The pass collects the connected wire component reachable from an edit, relaxes
/// it from every source feeding it, and writes only what moved. Bounded by the component, not the
/// world.</para>
/// <para>⛳ <b>Gates evaluate on the signal TICK, never inside the pass.</b> A NOT gate feeding its
/// own input is a contradiction as an equation and a clock as a machine; evaluating gates inline
/// would hang the pass on one, and ticking them makes it blink at tick rate instead. Wire alone is
/// monotone — strength only falls with distance from a source — so the relax terminates without
/// help.</para>
/// <para>⚠ Wire is floor-bound and connects on its own level in the four cardinals. It does not
/// climb; a step up is two runs and a gate re-emits at full strength, which is the repeater.</para>
/// </remarks>
public sealed class SignalPass
{
    private readonly SignalTable _table;

    private readonly Queue<(int X, int Y, int Z)> _frontier = [];
    private readonly Dictionary<(int X, int Y, int Z), int> _strength = [];
    private readonly HashSet<(int X, int Y, int Z)> _component = [];
    private readonly HashSet<(int X, int Y, int Z)> _dirtyGates = [];
    private readonly HashSet<(int X, int Y, int Z)> _followed = [];

    /// <summary>
    /// The last signal level seen by each hand-operated sink. Doors and trapdoors have one visible
    /// open bit serving two masters: the player's latch and a signal's edge. Remembering the level
    /// keeps an unchanged unpowered neighbourhood from immediately erasing a manual choice.
    /// </summary>
    private readonly Dictionary<(int X, int Y, int Z), bool> _handPower = [];

    /// <summary>How a changed cell reaches the world: light, mesh and fluid, but never back here.</summary>
    public delegate void Write(int x, int y, int z, BlockId id);

    public SignalPass(SignalTable table) => _table = table;

    public SignalPass(BlockRegistry registry) : this(new SignalTable(registry)) { }

    public SignalTable Table => _table;

    /// <summary>Gates waiting for the next tick to compute.</summary>
    public int DirtyGates => _dirtyGates.Count;

    /// <summary>Cells this pass has rewritten since it was made. For the instruments.</summary>
    public long Changed { get; private set; }

    private static readonly (int X, int Z)[] Sides = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>
    /// Remembers hand-operated sinks before an ordinary world edit changes their inputs.
    /// </summary>
    /// <remarks>
    /// This is the missing half of edge-triggered doors. Looking only after a lever was changed
    /// can say whether a door is powered now, but not whether that level actually changed. The
    /// pre-edit walk covers the old wire component as well as the edit's ring, including a lever
    /// fixed straight to a door with no wire between them.
    /// </remarks>
    public void CapturePoweredSinks(VoxelWorld world, int x, int y, int z)
    {
        _component.Clear();
        _frontier.Clear();
        _followed.Clear();

        Collect(world, x, y, z);
        for (var face = 0; face < Faces.Count; face++)
        {
            var (dx, dy, dz) = Faces.Normals[face];
            Collect(world, x + dx, y + dy, z + dz);
        }

        RememberAround(world, (x, y, z));
        for (var face = 0; face < Faces.Count; face++)
        {
            var (dx, dy, dz) = Faces.Normals[face];
            RememberAround(world, (x + dx, y + dy, z + dz));
        }
        foreach (var cell in _component) RememberAround(world, cell);
    }

    /// <summary>
    /// Reconsiders the wiring around one edit: the component's strengths, the sinks touching it,
    /// and which gates now want to think.
    /// </summary>
    /// <param name="switched">Appended with every sink that changed state, for the client's ears.</param>
    public void Update(
        VoxelWorld world, int x, int y, int z, Write write,
        List<(int X, int Y, int Z, BlockId Now)>? switched = null)
    {
        _component.Clear();
        _strength.Clear();
        _frontier.Clear();
        _followed.Clear();

        // The component: every wire cell reachable from the edit and its ring, walking wire to
        // wire on the same level in the four cardinals.
        Collect(world, x, y, z);
        for (var face = 0; face < Faces.Count; face++)
        {
            var (dx, dy, dz) = Faces.Normals[face];
            Collect(world, x + dx, y + dy, z + dz);
        }

        // Relax: every feed point offers what it has, and each wire cell keeps the best offer,
        // passing it on one weaker. Monotone from Max downward, so this terminates on its own.
        foreach (var cell in _component)
        {
            var fed = FeedAt(world, cell);
            if (fed <= 0) continue;

            _strength[cell] = fed;
            _frontier.Enqueue(cell);
        }

        while (_frontier.TryDequeue(out var cell))
        {
            var here = _strength.GetValueOrDefault(cell);
            if (here <= 1) continue;

            foreach (var (dx, dz) in Sides)
            {
                var next = (cell.X + dx, cell.Y, cell.Z + dz);
                if (!_component.Contains(next)) continue;
                if (_strength.GetValueOrDefault(next) >= here - 1) continue;

                _strength[next] = here - 1;
                _frontier.Enqueue(next);
            }
        }

        // Write back what moved, and remember everything worth re-asking afterwards.
        foreach (var cell in _component)
        {
            var want = _table.WireFor(_strength.GetValueOrDefault(cell));
            if (world.GetBlock(cell.X, cell.Y, cell.Z) == want) continue;

            write(cell.X, cell.Y, cell.Z, want);
            Changed++;
        }

        // Sinks and gates touching the component, or touching the edit itself — a lever laid
        // straight against a door needs no wire at all.
        FollowAround(world, (x, y, z), write, switched);
        for (var face = 0; face < Faces.Count; face++)
        {
            var (dx, dy, dz) = Faces.Normals[face];
            FollowAround(world, (x + dx, y + dy, z + dz), write, switched);
        }
        foreach (var cell in _component) FollowAround(world, cell, write, switched);
    }

    /// <summary>
    /// Computes every gate whose inputs changed since the last tick. Call at the signal rate.
    /// </summary>
    /// <returns>How many gates changed state.</returns>
    public int Tick(
        VoxelWorld world, Write write,
        List<(int X, int Y, int Z, BlockId Now)>? switched = null)
    {
        if (_dirtyGates.Count == 0) return 0;

        var thinking = _dirtyGates.ToArray();
        _dirtyGates.Clear();

        var flipped = 0;
        foreach (var (gx, gy, gz) in thinking)
        {
            var id = world.GetBlock(gx, gy, gz).Value;
            var kind = _table.KindOf(id);
            if (kind == GateKind.None) continue;   // mined since it was marked

            var facing = _table.GateFacing(id);
            var back = Placeable.Opposite(facing);
            var arms = Placeable.Hinges(facing);

            var left = InputAt(world, gx, gy, gz, arms[0]);
            var right = InputAt(world, gx, gy, gz, arms[1]);
            var rear = InputAt(world, gx, gy, gz, back);

            var want = kind switch
            {
                GateKind.And => left && right,
                GateKind.Or => left || right,
                GateKind.Xor => left ^ right,
                GateKind.Not => !rear,
                GateKind.Latch => left || (!right && _table.GateOn(id)),
                _ => _table.GateOn(id),
            };

            if (want == _table.GateOn(id)) continue;

            write(gx, gy, gz, _table.GateTwin(id));
            flipped++;
            Changed++;

            // The output side of a flipped gate is a fresh edit as far as the wiring goes.
            var (fx, fy, fz) = Faces.Normals[facing];
            Update(world, gx + fx, gy + fy, gz + fz, write, switched);
        }

        return flipped;
    }

    /// <summary>Adds every wire cell reachable from here to the component.</summary>
    private void Collect(VoxelWorld world, int x, int y, int z)
    {
        if (!_table.IsWire(world.GetBlock(x, y, z).Value)) return;
        if (!_component.Add((x, y, z))) return;

        _frontier.Enqueue((x, y, z));
        while (_frontier.TryDequeue(out var cell))
        {
            foreach (var (dx, dz) in Sides)
            {
                var next = (X: cell.X + dx, Y: cell.Y, Z: cell.Z + dz);
                if (_component.Contains(next)) continue;
                if (!_table.IsWire(world.GetBlock(next.X, next.Y, next.Z).Value)) continue;

                _component.Add(next);
                _frontier.Enqueue(next);
            }
        }
    }

    /// <summary>What the neighbours push into a wire cell: a source's full strength, or a gate's.</summary>
    private int FeedAt(VoxelWorld world, (int X, int Y, int Z) cell)
    {
        var best = 0;
        for (var face = 0; face < Faces.Count; face++)
        {
            var (dx, dy, dz) = Faces.Normals[face];
            var id = world.GetBlock(cell.X + dx, cell.Y + dy, cell.Z + dz).Value;

            if (_table.SourceOn(id)) best = SignalTable.Max;

            // A gate feeds the one cell its output faces — the cell we are standing in, which is
            // the case exactly when the gate's facing points back along the way we looked.
            if (_table.GateOn(id) && _table.KindOf(id) != GateKind.None
                && _table.GateFacing(id) == Placeable.Opposite(face))
                best = SignalTable.Max;
        }

        return best;
    }

    /// <summary>Whether one particular side drives this cell: live wire, a source, or a gate's out.</summary>
    private bool InputAt(VoxelWorld world, int x, int y, int z, int face)
    {
        var (dx, dy, dz) = Faces.Normals[face];
        var id = world.GetBlock(x + dx, y + dy, z + dz).Value;

        if (_table.WireStrength(id) > 0) return true;
        if (_table.SourceOn(id)) return true;

        return _table.GateOn(id) && _table.KindOf(id) != GateKind.None
            && _table.GateFacing(id) == Placeable.Opposite(face);
    }

    /// <summary>True when anything beside this cell drives it: live wire, a source, a gate's out.</summary>
    private bool PoweredAt(VoxelWorld world, int x, int y, int z)
    {
        for (var face = 0; face < Faces.Count; face++)
        {
            var (dx, dy, dz) = Faces.Normals[face];
            var id = world.GetBlock(x + dx, y + dy, z + dz).Value;

            if (_table.WireStrength(id) > 0) return true;
            if (_table.SourceOn(id)) return true;
            if (_table.GateOn(id) && _table.KindOf(id) != GateKind.None
                && _table.GateFacing(id) == Placeable.Opposite(face))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The level a sink follows. Either half can power a two-cell door, so both halves must see
    /// the same aggregate answer even when the lever is beside only the upper one.
    /// </summary>
    private bool SinkPoweredAt(VoxelWorld world, int x, int y, int z, ushort id)
    {
        if (PoweredAt(world, x, y, z)) return true;

        var partner = _table.PartnerFace(id);
        if (partner < 0) return false;

        var (px, py, pz) = Faces.Normals[partner];
        var other = world.GetBlock(x + px, y + py, z + pz).Value;
        if (_table.PartnerFace(other) != Placeable.Opposite(partner)) return false;

        return PoweredAt(world, x + px, y + py, z + pz);
    }

    /// <summary>Snapshots hand-operated sinks around one affected cell, once per pre-edit walk.</summary>
    private void RememberAround(VoxelWorld world, (int X, int Y, int Z) cell)
    {
        for (var face = 0; face < Faces.Count; face++)
        {
            var (dx, dy, dz) = Faces.Normals[face];
            var at = (X: cell.X + dx, Y: cell.Y + dy, Z: cell.Z + dz);
            if (!_followed.Add(at)) continue;

            var id = world.GetBlock(at.X, at.Y, at.Z).Value;
            if (_table.CanToggleByHand(id))
                _handPower[at] = SinkPoweredAt(world, at.X, at.Y, at.Z, id);
            else
                _handPower.Remove(at);
        }
    }

    /// <summary>Re-asks the sinks and gates around one cell, once per pass.</summary>
    private void FollowAround(
        VoxelWorld world, (int X, int Y, int Z) cell, Write write,
        List<(int X, int Y, int Z, BlockId Now)>? switched)
    {
        for (var face = 0; face < Faces.Count; face++)
        {
            var (dx, dy, dz) = Faces.Normals[face];
            var at = (X: cell.X + dx, Y: cell.Y + dy, Z: cell.Z + dz);
            if (!_followed.Add(at)) continue;

            var id = world.GetBlock(at.X, at.Y, at.Z).Value;

            if (!_table.CanToggleByHand(id)) _handPower.Remove(at);

            if (_table.KindOf(id) != GateKind.None)
            {
                _dirtyGates.Add(at);
                continue;
            }

            if (!_table.IsSink(id)) continue;

            var powered = SinkPoweredAt(world, at.X, at.Y, at.Z, id);

            // Lamps and powered rails continuously follow the level. A wooden door or trapdoor
            // instead follows a CHANGE of level and otherwise keeps the state a hand chose. With
            // no earlier observation, live power still opens it while quiet preserves a saved or
            // manually selected state.
            if (_table.CanToggleByHand(id))
            {
                var observed = _handPower.TryGetValue(at, out var before);
                _handPower[at] = powered;

                if ((observed && before == powered) || (!observed && !powered)) continue;
            }

            if (powered == _table.SinkPowered(id)) continue;

            Swap(world, at.X, at.Y, at.Z, id, write, switched);
        }
    }

    /// <summary>Moves a sink to its other state, and a door's other half with it.</summary>
    private void Swap(
        VoxelWorld world, int x, int y, int z, ushort id, Write write,
        List<(int X, int Y, int Z, BlockId Now)>? switched)
    {
        var twin = _table.SinkTwin(id);
        write(x, y, z, twin);
        Changed++;
        switched?.Add((x, y, z, twin));

        // Both halves or neither — the door rule, the same statement the click path makes.
        var partner = _table.PartnerFace(id);
        if (partner < 0) return;

        var (px, py, pz) = Faces.Normals[partner];
        var other = world.GetBlock(x + px, y + py, z + pz).Value;

        if (_table.PartnerFace(other) != Placeable.Opposite(partner)) return;
        if (!_table.IsSink(other)) return;
        if (_table.SinkPowered(other) == _table.SinkPowered(_table.SinkTwin(id).Value)) return;

        write(x + px, y + py, z + pz, _table.SinkTwin(other));
        _followed.Add((x + px, y + py, z + pz));
    }
}
