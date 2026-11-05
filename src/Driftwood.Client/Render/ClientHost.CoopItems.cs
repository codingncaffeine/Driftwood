using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;
using Driftwood.Core.Items;
using Driftwood.Core.Net;
using Driftwood.Core.Magic;
using Driftwood.Core.Players;
using Driftwood.Core.Physics;

namespace Driftwood.Client.Render;

/// <summary>
/// Co-op N5, things: the ground, the containers and the kills, shared.
/// </summary>
/// <remarks>
/// <para>⛳ <b>The host owns all three.</b> Every stack on the ground lies on the host's; a guest sees
/// them through a <see cref="DropMirror"/> and asks for one it walks into, and what a guest throws is
/// laid on the host's ground. A container is LENT whole to the one guest who opens it (see
/// <see cref="ContainerLeases"/>) and handed back as the guest last wrote it. A creature's death is
/// shared with every guest who struck it.</para>
/// </remarks>
public sealed partial class ClientHost
{
    /// <summary>Host: the guests who struck each living creature, by its runtime id.</summary>
    private readonly Dictionary<string, HashSet<int>> _guestContributions = new(StringComparer.Ordinal);

    /// <summary>Guest: the host's ground.</summary>
    private DropMirror? _dropMirror;
    private readonly List<uint> _askScratch = [];
    private int _killsShared;

    /// <summary>Guest: the container the host has lent us, the one asked for, and what was last written back.</summary>
    private ContainerAt? _guestLease;
    private ContainerAt? _guestAsking;
    private byte[]? _leaseSent;
    private double _sinceLeaseWrite;
    private bool _openingLent;

    /// <summary>How often a guest writes a lent container back while its screen is open (only when it changed).</summary>
    private const double LeaseWriteInterval = 0.1;

    /// <summary>
    /// On a guest, opening a container asks the host for it; the screen opens when the host lends it.
    /// </summary>
    /// <returns>True when the asking is all that happens now.</returns>
    private bool AskHostFor(ContainerKind kind, int x, int y, int z)
    {
        if (!IsGuest || _openingLent || _coopGuest is not { Ended: null } guest) return false;
        var at = new ContainerAt(kind, x, y, z);
        if (_guestAsking == at) return true;
        FinishLease(guest);
        _guestAsking = at;
        guest.SendContainerOpen(at);
        return true;
    }

    /// <summary>On a host: a container lent to a guest stays shut here until it comes back.</summary>
    private bool LentAway(int x, int y, int z)
    {
        if (_coopHost is not { } host || host.Leases.HolderOf((x, y, z)) is not { } holder) return false;
        var who = host.Players.TryGetValue(holder, out var them) ? them.Name : "A friend";
        PushToast("In use", $"{who} is using it");
        return true;
    }

    /// <summary>
    /// The one door every station's spill goes through. On a guest it does nothing: the guest's copy
    /// is only what it was last sent, and the host spills its own when the cell reaches it.
    /// </summary>
    private void SpillStation(int x, int y, int z, Vector3 at)
    {
        if (IsGuest)
        {
            // Breaking the one this guest holds: what was moved in the last moment goes back BEFORE the
            // cell does, so the host spills it too (both ride one ordered link).
            if (_guestLease?.Cell == (x, y, z) && _coopGuest is { } guest) FinishLease(guest);
            return;
        }
        _coopHost?.ContainerGone((x, y, z));
        foreach (var spilled in _furnaces.Remove(x, y, z)) _drops.Drop(spilled, at);
        foreach (var spilled in _brewing.Remove(x, y, z)) _drops.Drop(spilled, at);
        foreach (var spilled in EmptyChestAt(x, y, z)) _drops.Drop(spilled, at);
    }

    /// <summary>
    /// Which stations of a sort this machine ticks: on a host every one not lent out, on a guest only
    /// the one lent to it. False when there is nothing to tick at all.
    /// </summary>
    private bool StationFilter(ContainerKind kind, out Func<(int X, int Y, int Z), bool>? include)
    {
        include = null;
        if (IsGuest)
        {
            if (_guestLease is not { } lent || lent.Kind != kind) return false;
            var cell = lent.Cell;
            include = c => c == cell;
            return true;
        }
        if (_coopHost is { } host && host.Leases.Count > 0) include = c => !host.Leases.IsLent(c);
        return true;
    }

    /// <summary>Host: the host's own screen is open on this container.</summary>
    private bool HostScreenOn((int X, int Y, int Z) cell) =>
        _hudScreen.Kind is HudScreenKind.Chest or HudScreenKind.Furnace or HudScreenKind.BrewingStand && _station == cell;

    /// <summary>Whether the block at a container's cell is that sort of container.</summary>
    private bool HoldsContainer(ContainerAt at)
    {
        var use = _registry[_streamer.World.GetBlock(at.X, at.Y, at.Z)].Use;
        return at.Kind switch
        {
            ContainerKind.Chest => use == BlockUse.Chest,
            ContainerKind.Furnace => use is BlockUse.Furnace or BlockUse.Campfire,
            _ => use == BlockUse.BrewingStand,
        };
    }

    private static HudScreenKind ScreenFor(ContainerKind kind) => kind switch
    {
        ContainerKind.Chest => HudScreenKind.Chest,
        ContainerKind.Furnace => HudScreenKind.Furnace,
        _ => HudScreenKind.BrewingStand,
    };

    private bool ScreenOnLease(ContainerAt at) => _hudScreen.Kind == ScreenFor(at.Kind) && _station == at.Cell;

    /// <summary>Host: a creature died; every guest who struck it shares the kill.</summary>
    private void CreditGuests(CreatureDeath death)
    {
        if (!_guestContributions.Remove(death.RuntimeId, out var guests) || _coopHost is not { } host) return;
        foreach (var peer in guests)
            host.CreditKill(peer, new KillMessage(death.RuntimeId, death.Kind, death.Grown, death.Hostile));
    }

    /// <summary>Host: forgets the strikers of creatures that left without dying (despawned, unloaded).</summary>
    private void PruneGuestContributions()
    {
        if (_guestContributions.Count == 0 || _herd is null) return;
        var alive = new HashSet<string>(_herd.All.Select(c => c.RuntimeId), StringComparer.Ordinal);
        foreach (var id in _guestContributions.Keys.Where(id => !alive.Contains(id)).ToList()) _guestContributions.Remove(id);
    }

    /// <summary>Guest: a kill shared by the host — the same credit the host's own kills earn.</summary>
    private void TakeKillCredit(KillMessage kill)
    {
        _killsShared++;
        Achieve(AchievementEvents.Kill, kill.Kind);
        var reward = CharacterRewards.Creature(kill.Kind, kill.Grown, kill.Hostile);
        if (reward.Empty) return;
        SettleCharacterReward(
            $"creature:{kill.RuntimeId}", reward, XpSource.Creature,
            $"{kill.Kind} defeated", StarterBlocks.LayerAnvilTop);
    }

    /// <summary>
    /// Guest: the host's ground, shown and asked for. The pickup itself is heard as a
    /// <see cref="Msg.Given"/> in <see cref="StepCoop"/>, so this never reports one.
    /// </summary>
    private int StepGuestDrops(float dt, Vector3? collector)
    {
        if (_coopGuest is not { } guest) return 0;
        _dropMirror ??= new DropMirror();
        while (guest.DropsHeard.TryDequeue(out var set)) _dropMirror.Heard(_elapsed, set);
        _dropMirror.Step(_elapsed, dt, collector, stack => _inventory.CanAdd(stack with { Count = 1 }), _drops, _askScratch);
        foreach (var id in _askScratch) guest.SendPickUp(id);
        return 0;
    }

    /// <summary>Guest: answers to container opens, containers taken back, and the lent one written back as it changes.</summary>
    private void StepGuestContainers(CoopGuest guest)
    {
        while (guest.GrantsHeard.TryDequeue(out var grant))
        {
            if (_guestAsking == grant.At) _guestAsking = null;
            if (grant.Contents is not { } contents)
            {
                PushToast("Cannot open that", grant.Refusal);
                continue;
            }

            // Asked, then something else was opened (or the player walked off) before the answer came:
            // handed straight back untouched.
            if (_hudScreen.IsOpen || _guestLease is not null)
            {
                guest.SendContainerWrite(grant.At, contents, closing: true);
                continue;
            }

            switch (grant.At.Kind)
            {
                case ContainerKind.Chest: ContainerCodec.Read(contents, _chests.Open(grant.At.X, grant.At.Y, grant.At.Z)); break;
                case ContainerKind.Furnace: ContainerCodec.Read(contents, _furnaces.Open(grant.At.X, grant.At.Y, grant.At.Z)); break;
                default: ContainerCodec.Read(contents, _brewing.Open(grant.At.X, grant.At.Y, grant.At.Z)); break;
            }
            _guestLease = grant.At;
            _leaseSent = contents;
            _sinceLeaseWrite = 0;

            _openingLent = true;
            try
            {
                switch (grant.At.Kind)
                {
                    case ContainerKind.Chest: OpenChest(grant.At.X, grant.At.Y, grant.At.Z); break;
                    case ContainerKind.Furnace: OpenFurnace(grant.At.X, grant.At.Y, grant.At.Z); break;
                    default: OpenBrewing(grant.At.X, grant.At.Y, grant.At.Z); break;
                }
            }
            finally
            {
                _openingLent = false;
            }
        }

        while (guest.ShutsHeard.TryDequeue(out var shut))
        {
            if (_guestLease is not { } held || held.Cell != shut.Cell) continue;
            // Gone from the host's world, its contents on the floor there: nothing to write back.
            _guestLease = null;
            _leaseSent = null;
            if (ScreenOnLease(held)) CloseScreen();
        }

        if (_guestLease is not { } lease) return;
        if (!ScreenOnLease(lease))
        {
            FinishLease(guest);
            return;
        }
        if (Due(ref _sinceLeaseWrite, LeaseWriteInterval, _coopFrameDt)) WriteLease(guest, lease, closing: false);
    }

    /// <summary>Guest: hands the lent container back as it stands. Once its screen shuts, or on leaving.</summary>
    private void FinishLease(CoopGuest guest)
    {
        if (_guestLease is not { } lease) return;
        WriteLease(guest, lease, closing: true);
        _guestLease = null;
        _leaseSent = null;
    }

    private void WriteLease(CoopGuest guest, ContainerAt lease, bool closing)
    {
        byte[]? now = lease.Kind switch
        {
            ContainerKind.Chest => _chests.TryGet(lease.X, lease.Y, lease.Z, out var chest) ? ContainerCodec.Write(chest) : null,
            ContainerKind.Furnace => _furnaces.TryGet(lease.X, lease.Y, lease.Z, out var furnace) ? ContainerCodec.Write(furnace) : null,
            _ => _brewing.TryGet(lease.X, lease.Y, lease.Z, out var stand) ? ContainerCodec.Write(stand) : null,
        };
        now ??= _leaseSent;
        if (now is null) return;
        if (!closing && _leaseSent is not null && now.AsSpan().SequenceEqual(_leaseSent)) return;
        guest.SendContainerWrite(lease, now, closing);
        _leaseSent = now;
    }
}

/// <summary>
/// ⚠ Instruments only: <c>DRIFTWOOD_COOP_PROBE=1</c> on a guest walks N5 once in a real game against a
/// real host — throws three sticks (they land on the host's ground and come back through the mirror
/// as the guest stands in them), places a chest, puts a stick in it through the lent screen, shuts it,
/// and opens it again to find the stick the host kept. Every step prints a "co-op probe" line;
/// <c>scratch/mp/coop-smoke.sh</c> (PROBE=1) reads them.
/// </summary>
public sealed partial class ClientHost
{
    private static readonly bool s_coopProbe = Environment.GetEnvironmentVariable("DRIFTWOOD_COOP_PROBE") == "1";
    private int _probeStage;
    private double _probeFor;
    private (int X, int Y, int Z) _probeChest;

    private void StepCoopProbe(double dt)
    {
        if (!s_coopProbe || !IsGuest || !_spawned || _probeStage < 0) return;
        _probeFor += dt;
        var stick = _items.ByName("stick").Id;

        void Next(int stage, string? said = null)
        {
            if (said is not null) Console.WriteLine($"co-op probe {said} ({_probeFor:F1}s)");
            _probeStage = stage;
            _probeFor = 0;
        }

        void Fail(string why)
        {
            Console.WriteLine($"co-op probe FAILED: {why}");
            _probeStage = -1;
        }

        switch (_probeStage)
        {
            case 0 when _probeFor > 4:
                // ⚠ A first visit stands a block from the host, whose own magnet would take the throw:
                // six blocks off first, dropped from a little above so it lands on whatever is there.
                _player.Position += new Vector3(6f, 3f, 0f);
                Next(10, $"stepped away from the host to {_player.Position}");
                break;

            case 10 when _probeFor > 3:
                _inventory.Add(_items.Stack("stick", 3));
                _drops.Drop(_items.Stack("stick", 3), _player.Position + new Vector3(0f, 1f, 0f), 0.2f);
                Next(1, $"threw 3 sticks, holding {_inventory.CountOf(stick)}");
                break;

            case 1:
                if (_inventory.CountOf(stick) >= 6) Next(2, "the thrown sticks came back through the host");
                else if (_probeFor > 10) Fail($"the thrown sticks never came back (holding {_inventory.CountOf(stick)}, mirror shows {_dropMirror?.Count ?? 0})");
                break;

            case 2:
            {
                var feet = _player.Position;
                var x = (int)MathF.Floor(feet.X) + 2;
                var z = (int)MathF.Floor(feet.Z);
                var y = (int)MathF.Floor(feet.Y);
                while (!_streamer.World.GetBlock(x, y, z).IsAir && y < feet.Y + 4) y++;
                _probeChest = (x, y, z);
                _streamer.EditBlock(x, y, z, _registry.ByName("chest_north").Id);
                Next(3, $"placed a chest at {_probeChest}");
                break;
            }

            case 3 when _probeFor > 1.5:
                OpenChest(_probeChest.X, _probeChest.Y, _probeChest.Z);
                Next(4);
                break;

            case 4:
                if (_guestLease is { } held && ScreenOnLease(held) && _hudScreen.Stored is { } chest)
                {
                    chest.Contents[0] = new ItemStack(stick, 1);
                    Next(5, "the host lent the chest; put a stick in");
                }
                else if (_probeFor > 8) Fail("the host never lent the chest");
                break;

            case 5 when _probeFor > 0.5:
                CloseScreen();
                Next(6, "shut the chest");
                break;

            case 6 when _probeFor > 1.5:
                // Forget this machine's copy (handed back frames ago), so only the host's answer can put the stick back.
                if (_chests.TryGet(_probeChest.X, _probeChest.Y, _probeChest.Z, out var mine)) mine.Contents[0] = ItemStack.Empty;
                OpenChest(_probeChest.X, _probeChest.Y, _probeChest.Z);
                Next(7);
                break;

            case 7:
                if (_guestLease is { } again && ScreenOnLease(again) && _hudScreen.Stored is { } kept)
                {
                    if (kept.Contents[0].Item.Value == stick.Value && kept.Contents[0].Count == 1)
                        Console.WriteLine("co-op probe PASSED: the chest came back from the host with the stick in it");
                    else Fail($"the chest came back from the host holding {kept.Contents[0]}");
                    CloseScreen();
                    _probeStage = -1;
                }
                else if (_probeFor > 8) Fail("the host never lent the chest a second time");
                break;
        }
    }
}
