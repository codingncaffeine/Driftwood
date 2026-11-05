using System.Numerics;
using Driftwood.Core.Items;

namespace Driftwood.Core.Net;

/// <summary>
/// A container's contents as bytes (N5): what the host hands a guest who opens it, and what the guest
/// hands back as it changes. The container's own fields in order, stacks by this build's item numbers.
/// </summary>
public static class ContainerCodec
{
    public static byte[] Write(Chest chest) => Bytes(w =>
    {
        foreach (var stack in chest.Contents) w.Write(stack);
    });

    public static byte[] Write(Furnace furnace) => Bytes(w =>
    {
        w.Write(furnace.Input);
        w.Write(furnace.Fuel);
        w.Write(furnace.Output);
        w.Write(furnace.BurnLeft);
        w.Write(furnace.BurnTotal);
        w.Write(furnace.Progress);
        w.Write(furnace.Takes);
    });

    public static byte[] Write(BrewingStand stand) => Bytes(w =>
    {
        foreach (var bottle in stand.Bottle) w.Write(bottle);
        w.Write(stand.Ingredient);
        w.Write(stand.Fuel);
        w.Write(stand.FuelLeft);
        w.Write(stand.Progress);
    });

    public static void Read(byte[] bytes, Chest into) => Read(bytes, r =>
    {
        for (var i = 0; i < Chest.Slots; i++) into.Contents[i] = r.ReadStack();
    });

    public static void Read(byte[] bytes, Furnace into) => Read(bytes, r =>
    {
        into.Input = r.ReadStack();
        into.Fuel = r.ReadStack();
        into.Output = r.ReadStack();
        into.BurnLeft = Finite(r.ReadSingle());
        into.BurnTotal = Finite(r.ReadSingle());
        into.Progress = Finite(r.ReadSingle());
        into.Takes = Finite(r.ReadSingle());
    });

    public static void Read(byte[] bytes, BrewingStand into) => Read(bytes, r =>
    {
        for (var i = 0; i < BrewingStand.Bottles; i++) into.Bottle[i] = r.ReadStack();
        into.Ingredient = r.ReadStack();
        into.Fuel = r.ReadStack();
        into.FuelLeft = Math.Clamp(r.ReadInt32(), 0, BrewingStand.BrewsPerDust);
        into.Progress = Finite(r.ReadSingle());
    });

    /// <summary>Every stack in a container's bytes, whatever its sort — for the checks to count.</summary>
    public static int CountOf(ContainerKind kind, byte[] bytes, ItemId item)
    {
        var stacks = kind switch
        {
            ContainerKind.Chest => Chest.Slots,
            ContainerKind.Furnace => 3,
            _ => BrewingStand.Bottles + 2,
        };
        var total = 0;
        using var r = new BinaryReader(new MemoryStream(bytes));
        for (var i = 0; i < stacks; i++)
        {
            var stack = r.ReadStack();
            if (!stack.IsEmpty && stack.Item.Value == item.Value) total += stack.Count;
        }
        return total;
    }

    private static float Finite(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1e6f) : 0f;

    private static byte[] Bytes(Action<BinaryWriter> fields)
    {
        using var buffer = new MemoryStream();
        using (var w = new BinaryWriter(buffer)) fields(w);
        return buffer.ToArray();
    }

    private static void Read(byte[] bytes, Action<BinaryReader> fields)
    {
        using var r = new BinaryReader(new MemoryStream(bytes));
        fields(r);
    }
}

/// <summary>The host's side of the ground (N5): what is near a guest, nearest first.</summary>
public static class CoopDrops
{
    public static void Near(DroppedItems drops, Vector3 at, float radius, List<DropState> into)
    {
        into.Clear();
        var reach = radius * radius;
        foreach (var item in drops.Live)
        {
            // One flying at the host is already the host's: a guest would only ask for it in vain.
            if (item.Collecting > 0f || item.Id == 0) continue;
            if (Vector3.DistanceSquared(item.Position, at) <= reach) into.Add(new DropState(item.Id, item.Stack, item.Position, item.Delay <= 0f));
        }

        if (into.Count <= DropsMessage.MaxPerMessage) return;
        into.Sort((a, b) => Vector3.DistanceSquared(a.Position, at).CompareTo(Vector3.DistanceSquared(b.Position, at)));
        into.RemoveRange(DropsMessage.MaxPerMessage, into.Count - DropsMessage.MaxPerMessage);
    }
}

/// <summary>
/// The guest's side of the ground (N5): the host's stacks, shown through this machine's own
/// <see cref="DroppedItems"/> so they draw exactly as the host's do, and asked for when walked into.
/// </summary>
/// <remarks>
/// <para>⛳ A stack is asked for once; while the ask is out it flies in and is hidden, and if the host
/// has not taken it off the ground within <see cref="AskAgainAfter"/> (somebody else got there first,
/// or it was too fresh) it is shown again and may be asked for again.</para>
/// <para>Drawn a little in the past, between the two sets either side, like the creatures.</para>
/// </remarks>
public sealed class DropMirror
{
    public const double Delay = 0.2;
    public const double AskAgainAfter = 1.5;

    private readonly Dictionary<uint, Seen> _seen = [];
    private readonly HashSet<uint> _inSet = [];
    private readonly List<DroppedItem> _shown = [];
    private uint _sequence;
    private bool _any;

    public int Count => _seen.Count;
    public int SetsHeard { get; private set; }
    public int Asked { get; private set; }

    public void Heard(double now, DropsMessage set)
    {
        if (_any && unchecked((int)(set.Sequence - _sequence)) <= 0) return;
        _any = true;
        _sequence = set.Sequence;
        SetsHeard++;

        _inSet.Clear();
        foreach (var drop in set.Drops)
        {
            _inSet.Add(drop.Id);
            if (!_seen.TryGetValue(drop.Id, out var seen)) _seen[drop.Id] = seen = new Seen(now);
            seen.Stack = drop.Stack;
            seen.Ready = drop.Ready;
            seen.Poses.Add((now, drop.Position));
            if (seen.Poses.Count > 6) seen.Poses.RemoveAt(0);
        }

        foreach (var id in _seen.Keys.Where(id => !_inSet.Contains(id)).ToList()) _seen.Remove(id);
    }

    /// <summary>
    /// Places every stack, flies in the ones asked for, and says which to ask for now.
    /// </summary>
    /// <param name="collector">The player's middle, or null when not collecting (a screen is open, not spawned).</param>
    /// <param name="fits">Whether at least one of a stack would go into the pockets.</param>
    public void Step(double now, float dt, Vector3? collector, Func<ItemStack, bool> fits, DroppedItems show, List<uint> ask)
    {
        ask.Clear();
        _shown.Clear();
        foreach (var (id, seen) in _seen)
        {
            seen.Age += dt;
            var at = seen.Sample(now - Delay);

            if (seen.AskedAt is { } asked)
            {
                if (now - asked < AskAgainAfter)
                {
                    // Flying in while the host decides; gone from sight once it arrives.
                    if (collector is not { } to || seen.Flown >= 1f) continue;
                    seen.Flown = MathF.Min(1f, seen.Flown + dt * 5f);
                    _shown.Add(Item(id, seen, Vector3.Lerp(at, to, seen.Flown), seen.Flown));
                    continue;
                }
                seen.AskedAt = null;
                seen.Flown = 0f;
            }

            if (collector is { } c && seen.Ready && seen.SinceHeard(now) < 1.0 && fits(seen.Stack)
                && Vector3.DistanceSquared(at, c) <= DroppedItems.MagnetRadius * DroppedItems.MagnetRadius)
            {
                seen.AskedAt = now;
                ask.Add(id);
                Asked++;
            }

            _shown.Add(Item(id, seen, at, 0f));
        }

        show.Show(_shown);
    }

    private static DroppedItem Item(uint id, Seen seen, Vector3 at, float collecting) => new()
    {
        Id = id,
        Position = at,
        Stack = seen.Stack,
        Age = seen.Age,
        Collecting = collecting,
        Grounded = true,
    };

    private sealed class Seen(double first)
    {
        public ItemStack Stack;
        public bool Ready;
        public float Age;
        public double? AskedAt;
        public float Flown;
        public readonly List<(double At, Vector3 Position)> Poses = [];
        private readonly double _first = first;

        public double SinceHeard(double now) => Poses.Count == 0 ? now - _first : now - Poses[^1].At;

        public Vector3 Sample(double at)
        {
            if (at <= Poses[0].At) return Poses[0].Position;
            for (var i = Poses.Count - 1; i > 0; i--)
            {
                var (t0, p0) = Poses[i - 1];
                var (t1, p1) = Poses[i];
                if (at < t0) continue;
                if (at >= t1) return p1;
                return Vector3.Lerp(p0, p1, (float)((at - t0) / Math.Max(t1 - t0, 1e-6)));
            }
            return Poses[^1].Position;
        }
    }
}

/// <summary>
/// The host's lending of containers (N5): one holder per container at a time, the host's own screen
/// included, so no two players ever move the same stack.
/// </summary>
/// <remarks>
/// ⛳ <b>Lent, not shared.</b> Industry co-op games move every slot click through the host; Driftwood's
/// container screens edit their container directly, so instead the whole container is lent to the
/// guest who opened it — the guest's screen works on it unchanged (a lent furnace even smelts on the
/// guest's machine) — and the host takes it back, as the guest last wrote it, when the screen shuts
/// or the guest leaves. While it is lent nobody else may open it, and the host does not tick it.
/// </remarks>
public sealed class ContainerLeases
{
    private readonly Dictionary<(int X, int Y, int Z), (int Peer, ContainerKind Kind)> _lent = [];

    public int Count => _lent.Count;

    public int? HolderOf((int X, int Y, int Z) cell) => _lent.TryGetValue(cell, out var held) ? held.Peer : null;

    public bool IsLent((int X, int Y, int Z) cell) => _lent.ContainsKey(cell);

    public bool Holds(int peer, ContainerAt at) =>
        _lent.TryGetValue(at.Cell, out var held) && held.Peer == peer && held.Kind == at.Kind;

    public void Lend(int peer, ContainerAt at) => _lent[at.Cell] = (peer, at.Kind);

    public bool Return((int X, int Y, int Z) cell) => _lent.Remove(cell);

    /// <summary>Everything a guest held, handed back (it left).</summary>
    public List<ContainerAt> ReturnAll(int peer)
    {
        var back = _lent.Where(pair => pair.Value.Peer == peer)
            .Select(pair => new ContainerAt(pair.Value.Kind, pair.Key.X, pair.Key.Y, pair.Key.Z)).ToList();
        foreach (var at in back) _lent.Remove(at.Cell);
        return back;
    }
}
