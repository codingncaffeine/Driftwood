using System.Diagnostics;
using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;
using Driftwood.Core.Items;
using Driftwood.Core.Net;
using Driftwood.Core.Saves;
using Driftwood.Core.World;

namespace Driftwood.Core.Diagnostics;

/// <summary>
/// <c>--net-check</c>: a real host and real guests over TCP on this machine, headless.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Through the shipped pieces end to end</b>: the TCP transport, the session, the save's
/// own writer and reader for the world a guest receives, and the guest records on disk. Nothing is
/// faked except the host's game, which is a world with a few known edits, a chest and a sign.</para>
/// <para>Each claim below is checked by what ARRIVED on the other side, never by a counter the
/// sender set.</para>
/// </remarks>
public static class NetCheck
{
    public static (bool Passed, string Report) Run(string gameVersion)
    {
        var faults = new List<string>();
        var said = new List<string>();
        var folder = Path.Combine(Path.GetTempPath(), $"driftwood-netcheck-{Environment.ProcessId}");

        try
        {
            Directory.CreateDirectory(folder);
            Check(gameVersion, folder, faults, said);
        }
        catch (Exception error)
        {
            faults.Add($"the check itself threw: {error}");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }

        var report = string.Join('\n', said.Select(line => "  " + line))
                     + "\n" + (faults.Count == 0
                         ? "net-check   PASSED"
                         : "net-check   FAILED\n" + string.Join('\n', faults.Select(f => "  ✗ " + f)));
        return (faults.Count == 0, report);
    }

    private static void Check(string gameVersion, string folder, List<string> faults, List<string> said)
    {
        var registry = new BlockRegistry();
        StarterBlocks.Register(registry);
        var items = StarterItems.Register(registry);
        var book = StarterRecipes.Build(items);
        var build = BuildIdentity.Of(gameVersion, registry, items);

        var stone = registry.ByName("stone").Id;
        var torch = registry.ByName("torch").Id;
        var planks = registry.ByName("driftoak_planks").Id;

        // The host's world: three edits, a chest with something in it, a sign.
        var hostState = NewState(registry, items, book, "netcheck-seed");
        hostState.World.SetBlock(1, 65, 1, stone);
        hostState.World.SetBlock(2, 65, 1, torch);
        hostState.World.SetBlock(-5, 70, 9, planks);
        hostState.Chests.Open(0, 65, 0).Contents[3] = items.Stack("brick", 17);
        hostState.Signs.Set(3, 65, 3, ["hello", "", "guest", ""]);
        hostState.Pockets.Add(items.Stack("paper", 5)); // the host's own — must NOT reach a guest

        var game = new FakeHostWorld(hostState, registry);
        var records = new GuestRecords(Path.Combine(folder, "netcheck.players"));
        using var listener = TcpTransport.Listen(0);
        using var host = new CoopHost(listener, game, records, build, "Host");
        said.Add($"host listening on port {listener.Port}");

        var clock = Stopwatch.StartNew();
        double Now() => clock.Elapsed.TotalSeconds;

        void PumpUntil(Func<bool> done, string what, params CoopGuest[] guests)
        {
            var until = Now() + 10;
            while (!done())
            {
                host.Poll(Now(), 0.01f);
                foreach (var guest in guests) guest.Poll(Now());
                if (Now() > until)
                {
                    faults.Add($"timed out waiting for: {what}");
                    throw new TimeoutException(what);
                }
                Thread.Sleep(2);
            }
        }

        CoopGuest Dial(string key, string name, BuildIdentity as_, string code = "")
        {
            var link = TcpTransport.ConnectAsync($"127.0.0.1:{listener.Port}", CancellationToken.None).GetAwaiter().GetResult();
            return new CoopGuest(link, new HelloMessage(Protocol.Version, as_.GameVersion, as_.Registry, key, name, code));
        }

        var keyA = new string('a', 32);
        var keyB = new string('b', 32);

        try
        {
            // 1. A wrong build is refused with a reason, before anything about the world is sent.
            using (var wrong = Dial(new string('c', 32), "Wrong", build with { GameVersion = "0.0.0" }))
            {
                PumpUntil(() => wrong.Ended is not null, "a wrong version to be refused", wrong);
                if (wrong.Welcome is not null) faults.Add("a guest on another version was sent the world");
                if (!wrong.Ended!.Contains("0.0.0")) faults.Add($"the refusal did not name the version: {wrong.Ended}");
                said.Add($"wrong version refused: \"{wrong.Ended}\"");
            }
            using (var wrong = Dial(new string('c', 32), "Wrong", build with { Registry = "000000000000000000000000" }))
            {
                PumpUntil(() => wrong.Ended is not null, "a different block list to be refused", wrong);
                if (wrong.Welcome is not null) faults.Add("a guest with other blocks was sent the world");
                said.Add("different block list refused");
            }

            // 1b. Join codes: what a player types is read as a code only when it is one, and a world
            // opened with a code turns away a guest without it or with another, with a reason.
            var code = JoinCodes.New();
            if (JoinCodes.Read(JoinCodes.Show(code)) != code || JoinCodes.Read(JoinCodes.Show(code).ToLowerInvariant()) != code)
                faults.Add($"the code {JoinCodes.Show(code)} does not read back as itself");
            foreach (var address in new[] { "192.168.1.20", "pine", "myhost:47790", "[::1]:47790", "localhost", "12345678", "pine-4821" })
                if (JoinCodes.Read(address) is { } mistaken) faults.Add($"the address '{address}' was taken for the code {mistaken}");
            host.Code = code;
            using (var none = Dial(new string('d', 32), "NoCode", build))
            {
                PumpUntil(() => none.Ended is not null, "a guest without the code to be refused", none);
                if (none.Welcome is not null) faults.Add("a guest without the code was sent the world");
                said.Add($"no code refused: \"{none.Ended}\"");
            }
            var other = code == "23456789" ? "98765432" : "23456789";
            using (var wrongCode = Dial(new string('d', 32), "WrongCode", build, JoinCodes.Show(other)))
            {
                PumpUntil(() => wrongCode.Ended is not null, "a guest with another code to be refused", wrongCode);
                if (wrongCode.Welcome is not null) faults.Add("a guest with the wrong code was sent the world");
                said.Add($"wrong code refused: \"{wrongCode.Ended}\"");
            }
            using (var right = Dial(new string('e', 32), "RightCode", build, JoinCodes.Show(code).ToLowerInvariant()))
            {
                PumpUntil(() => right.Welcome is not null || right.Ended is not null, "a guest with the code to be let in", right);
                if (right.Welcome is null) faults.Add($"a guest with the right code was turned away: {right.Ended}");
                else said.Add($"the right code ({JoinCodes.Show(code)}, typed in lower case) let in");
                right.Leave(TimeSpan.FromSeconds(1));
            }
            PumpUntil(() => host.Players.Count == 0, "the coded guest to leave");
            host.Code = null;

            // 2. A first visit: the world arrives, the host's character does not.
            using var guestA = Dial(keyA, "Ash", build);
            PumpUntil(() => guestA.Welcome is not null || guestA.Ended is not null, "guest A's welcome", guestA);
            if (guestA.Welcome is null)
            {
                faults.Add($"guest A was not let in: {guestA.Ended}");
                return;
            }

            var welcome = guestA.Welcome;
            if (welcome.Seed != "netcheck-seed") faults.Add($"the seed arrived as '{welcome.Seed}'");
            if (welcome.Character.Length != 0) faults.Add("a first visit arrived with a character already");
            if (!welcome.Roster.Any(entry => entry.Id == 0 && entry.Name == "Host"))
                faults.Add("the roster did not name the host");

            var guestState = NewState(registry, items, book, welcome.Seed);
            var missing = new List<string>();
            if (WorldSave.Read(Protocol.Unpack(welcome.World), registry, items, guestState, missing) is { } readFault)
                faults.Add($"the guest could not read the world it was sent: {readFault}");
            if (guestState.World.Edits.Count != 3) faults.Add($"{guestState.World.Edits.Count} edits arrived of 3");
            if (!guestState.World.Edits.TryGetValue((-5, 70, 9), out var arrived) || arrived != planks)
                faults.Add("the planks at (-5,70,9) did not arrive");
            if (guestState.Chests.TryGet(0, 65, 0, out var chest) is false || chest.Contents[3].Count != 17)
                faults.Add("the chest's bricks did not arrive");
            if (!guestState.Signs.All.Any(sign => sign.Key == (3, 65, 3) && sign.Value[2] == "guest"))
                faults.Add("the sign did not arrive");
            if (guestState.Pockets.CountOf(items.ByName("paper").Id) != 0)
                faults.Add("the HOST's pockets were handed to the guest");
            said.Add($"guest A welcomed: {welcome.World.Length:N0} packed bytes, 3/3 edits, chest, sign, no host pockets");

            // 3. A second guest: both see each other, poses and cells pass between them via the host.
            using var guestB = Dial(keyB, "Birch", build);
            PumpUntil(() => guestB.Welcome is not null && guestA.Players.ContainsKey(guestB.MyId),
                "guest B to arrive and A to hear of it", guestA, guestB);
            if (!guestB.Players.ContainsKey(guestA.MyId)) faults.Add("guest B's roster did not include guest A");

            guestA.SendPose(new PoseMessage(0, new Vector3(10, 66, 12), 1.5f, -0.2f, PoseFlags.Sneaking, "torch"));
            PumpUntil(() => guestB.Players[guestA.MyId].Latest is not null, "A's pose to reach B", guestA, guestB);
            var poseAtB = guestB.Players[guestA.MyId].Latest!.Value;
            if (poseAtB.Position != new Vector3(10, 66, 12) || poseAtB.Held != "torch" || poseAtB.Flags != PoseFlags.Sneaking)
                faults.Add($"A's pose arrived at B as {poseAtB}");
            if (!host.Players.TryGetValue(guestA.MyId, out var aAtHost) || aAtHost.Latest is null)
                faults.Add("the host did not hear A's pose");

            guestA.SendCells([(11, 66, 12, planks.Value)]);
            PumpUntil(() => guestB.CellsHeard.Count > 0, "A's placed block to reach B", guestA, guestB);
            var cell = guestB.CellsHeard.Dequeue();
            if (cell != (11, 66, 12, planks.Value)) faults.Add($"A's block arrived at B as {cell}");
            if (hostState.World.GetBlock(11, 66, 12) != planks && !hostState.World.Edits.ContainsKey((11, 66, 12)))
                faults.Add("the host's world did not take A's block");
            if (guestA.CellsHeard.Count != 0) faults.Add("A's own block was echoed back to A");

            // Out of reach is refused by the host, and therefore never reaches B. ⚠ Followed by an
            // in-reach block from the SAME guest: one link is ordered, so once B has the second, the
            // far one has been decided — waiting on anything else races the host's poll.
            guestA.SendCells([(500, 66, 500, planks.Value)]);
            guestA.SendCells([(13, 66, 12, torch.Value)]);
            var heard = new List<(int X, int Y, int Z, ushort Block)>();
            PumpUntil(() =>
            {
                while (guestB.CellsHeard.TryDequeue(out var one)) heard.Add(one);
                return heard.Any(c => c.X == 13);
            }, "A's second block to reach B", guestA, guestB);
            if (heard.Any(c => c.X == 500) || hostState.World.Edits.ContainsKey((500, 66, 500)))
                faults.Add("a block placed far out of reach was accepted and passed on");

            host.SendCells([(2, 66, 2, torch.Value)]);
            PumpUntil(() => guestB.CellsHeard.Count > 0 && guestA.CellsHeard.Count > 0, "the host's own block to reach both", guestA, guestB);
            if (guestB.CellsHeard.Dequeue() != (2, 66, 2, torch.Value)) faults.Add("the host's block arrived wrong");
            said.Add("poses and blocks pass guest → host → guest; out-of-reach refused; no echo");

            // 4. A's character is kept by the host and handed back on the next visit.
            var aState = NewState(registry, items, book, welcome.Seed);
            aState.Pockets.Add(items.Stack("brick", 42));
            aState.Position = new Vector3(33.5f, 71f, -8.5f);
            guestA.SendCharacter(WorldSave.WriteBytes("netcheck", aState, SaveParts.Character));
            PumpUntil(() => records.Find(keyA) is not null, "the host to keep A's character", guestA, guestB);
            records.Flush();
            if (!File.Exists(Path.Combine(folder, "netcheck.players", keyA + ".dwc")))
                faults.Add("A's character was not written beside the world");

            // 5. A leaves: B hears it.
            var aId = guestA.MyId;
            guestA.Dispose();
            PumpUntil(() => !guestB.Players.ContainsKey(aId), "B to hear A leave", guestB);
            if (!guestB.Notices.Any(n => n.Contains("Ash left"))) faults.Add("B was not told that Ash left");

            // 6. A comes back on the same key — fresh process memory, records read from disk.
            var reread = new GuestRecords(Path.Combine(folder, "netcheck.players"));
            if (reread.Find(keyA) is null) faults.Add("A's character was not read back from disk");

            using var again = Dial(keyA, "Ash", build);
            PumpUntil(() => again.Welcome is not null || again.Ended is not null, "A's second welcome", again, guestB);
            if (again.Welcome is null)
            {
                faults.Add($"A was not let back in: {again.Ended}");
                return;
            }

            var back = NewState(registry, items, book, again.Welcome.Seed);
            if (WorldSave.Read(Protocol.Unpack(again.Welcome.World), registry, items, back, missing) is { } f1)
                faults.Add($"the second world would not read: {f1}");
            if (WorldSave.Read(again.Welcome.Character, registry, items, back, missing) is { } f2)
                faults.Add($"A's kept character would not read: {f2}");
            if (back.Pockets.CountOf(items.ByName("brick").Id) != 42)
                faults.Add($"A came back with {back.Pockets.CountOf(items.ByName("brick").Id)} bricks of 42");
            if (Vector3.Distance(back.Position, new Vector3(33.5f, 71f, -8.5f)) > 0.01f)
                faults.Add($"A came back at {back.Position}");
            if (!back.Signs.All.Any()) faults.Add("reading the character on top of the world wiped the world's sign");
            if (!back.World.Edits.ContainsKey((11, 66, 12))) faults.Add("the world A rejoined did not have A's block");
            said.Add("A's character kept by the host on disk and handed back on rejoin (42 bricks, position)");

            // 7. The same key twice at once is refused.
            using (var twin = Dial(keyA, "Ash again", build))
            {
                PumpUntil(() => twin.Ended is not null || twin.Welcome is not null, "a second Ash to be refused", twin, again, guestB);
                if (twin.Welcome is not null) faults.Add("the same player key was let in twice");
            }
        }
        catch (TimeoutException) { }
    }

    private static WorldState NewState(BlockRegistry registry, ItemRegistry items, RecipeBook book, string seed) =>
        new(seed, items, new VoxelWorld(registry), new FurnaceBank(items, book), new ChestBank(items),
            new Inventory(items), new Equipment(items), new PlayerVitals(registry), new RecipeUnlocks());

    /// <summary>The host's game, reduced to a world and a reach rule.</summary>
    private sealed class FakeHostWorld(WorldState state, BlockRegistry registry) : ICoopWorld
    {
        public string WorldName => "netcheck";
        public string Seed => state.Seed;
        public int TerrainVersion => state.TerrainVersion;
        public float DayTime => 0.25f;
        public Vector3 Spawn => new(0.5f, 66f, 0.5f);

        public byte[] WriteWorld() => WorldSave.WriteBytes(WorldName, state, SaveParts.World);

        public IReadOnlyList<(int X, int Y, int Z, ushort Block)> AcceptCells(
            int guest, Vector3 guestAt, IReadOnlyList<(int X, int Y, int Z, ushort Block)> cells)
        {
            var kept = new List<(int, int, int, ushort)>();
            foreach (var cell in cells)
            {
                if (!CoopRules.InReach(guestAt, cell.X, cell.Y, cell.Z) || cell.Block >= registry.Count) continue;
                state.World.SetBlock(cell.X, cell.Y, cell.Z, new BlockId(cell.Block));
                kept.Add(cell);
            }
            return kept;
        }
    }
}
