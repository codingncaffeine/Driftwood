using System.Diagnostics;
using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;
using Driftwood.Core.Gen;
using Driftwood.Core.Lighting;
using Driftwood.Core.Projectiles;
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
            CheckStreaming(gameVersion, folder, faults, said);
            CheckSpawnsAndShots(faults, said);
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

            // 3b. N5 creatures: A is told about the cow beside it and not the far zombie, sees it move,
            // lands a blow on it, is refused one out of reach, is hurt by the host, and sees it go.
            CheckCreatures(game, host, guestA, Now, PumpUntil, faults, said);

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

    private static void CheckCreatures(
        FakeHostWorld game, CoopHost host, CoopGuest guest, Func<double> now,
        Action<Func<bool>, string, CoopGuest[]> pumpUntil, List<string> faults, List<string> said)
    {
        var cowKind = new SpawnKind("cow", new Vector3(0.9f, 1.4f, 0.9f));
        var zombieKind = new SpawnKind("zombie", new Vector3(0.6f, 1.9f, 0.6f), Hostile: true);
        var cow = game.Herd.SpawnAt(cowKind, new Vector3(12.5f, 66f, 12.5f), 90f);
        var far = game.Herd.SpawnAt(zombieKind, new Vector3(300.5f, 66f, 300.5f));

        var mirrorHerd = new CreatureHerd(1);
        var mirror = new CreatureMirror(kind => kind == "cow" ? cowKind : kind == "zombie" ? zombieKind : null);
        bool Mirrored(Func<bool> done)
        {
            while (guest.CreaturesHeard.TryDequeue(out var set)) mirror.Heard(now(), set, mirrorHerd);
            mirror.Step(now() + 1.0);     // a second on: wherever the newest set put them
            return done();
        }

        pumpUntil(() => Mirrored(() => mirror.Count > 0), "the cow beside A to be mirrored", [guest]);
        var seen = mirrorHerd.All.FirstOrDefault(one => one.Kind == "cow");
        if (mirror.Count != 1 || seen is null)
            faults.Add($"A mirrored {mirror.Count} creatures ({string.Join(", ", mirrorHerd.All.Select(one => one.Kind))}); want the cow alone, not the far zombie");
        else if (Vector3.Distance(seen.Position, cow.Position) > 0.01f) faults.Add($"A's cow stands at {seen.Position}, the host's at {cow.Position}");

        cow.Position += new Vector3(3f, 0f, 0f);
        pumpUntil(() => Mirrored(() => seen is not null && Vector3.Distance(seen.Position, cow.Position) < 0.01f),
            "A's cow to follow the host's", [guest]);

        var id = mirror.IdOf(seen!) ?? -1;
        var health = cow.Health;
        guest.SendStrike(new StrikeMessage(id, 3, new Vector3(10f, 67.6f, 12f)));
        pumpUntil(() => Mirrored(() => cow.Health != health && seen!.Health == cow.Health), "A's blow to land and its result to come back", [guest]);
        if (health - cow.Health != 3) faults.Add($"A's blow of 3 took {health - cow.Health}");

        // Out of reach: the zombie is 400 blocks off. Followed by a fair blow so the order settles it.
        var farHealth = far.Health;
        var farId = game.Roster.IdOf(far);
        guest.SendStrike(new StrikeMessage(farId, 3, new Vector3(10f, 67.6f, 12f)));
        cow.HurtFor = 0f;
        var landed = host.StrikesLanded;
        guest.SendStrike(new StrikeMessage(id, 1, new Vector3(10f, 67.6f, 12f)));
        pumpUntil(() => host.StrikesLanded > landed, "A's second fair blow to land", [guest]);
        if (far.Health != farHealth) faults.Add("a blow from 400 blocks away landed");
        if (host.StrikesRefused < 1) faults.Add("the out-of-reach blow was not counted as refused");

        // Ranged: fifty blocks off, a hand's blow is refused and an arrow's lands. Melee first, so
        // once the ranged one has landed the melee one has been decided (one ordered link).
        cow.Position = new Vector3(60.5f, 66f, 12.5f);
        cow.HurtFor = 0f;
        var refused = host.StrikesRefused;
        landed = host.StrikesLanded;
        health = cow.Health;
        guest.SendStrike(new StrikeMessage(id, 2, new Vector3(10f, 67.6f, 12f)));
        guest.SendStrike(new StrikeMessage(id, 2, new Vector3(59f, 67f, 12f), Ranged: true));
        pumpUntil(() => host.StrikesLanded > landed, "A's arrow from fifty blocks to land", [guest]);
        if (host.StrikesRefused != refused + 1 || health - cow.Health != 2)
            faults.Add($"from fifty blocks: {host.StrikesRefused - refused} refused (want the hand's 1), {health - cow.Health} taken (want the arrow's 2)");

        // The one door: a blow on the guest's mirror goes to the host through the herd's proxy.
        mirrorHerd.HurtProxy = (creature, halfHearts, from) =>
        {
            guest.SendStrike(new StrikeMessage(mirror.IdOf(creature) ?? -1, halfHearts, from, Ranged: true));
            return true;
        };
        pumpUntil(() => Mirrored(() => seen!.Position.X > 59f), "A's cow to follow it out to fifty blocks", [guest]);
        cow.HurtFor = 0f;
        seen!.HurtFor = 0f;
        health = cow.Health;
        if (!mirrorHerd.Hurt(seen, 1, new Vector3(58f, 67f, 12f))) faults.Add("a blow on the guest's mirror was not taken by its proxy");
        pumpUntil(() => cow.Health != health, "a blow on A's mirror to reach the host's cow", [guest]);
        said.Add("creatures: a hand's blow from fifty blocks refused, an arrow's landed; a blow on the guest's mirror reaches the host through the herd's own door");

        host.HurtGuest(guest.MyId, new HurtMessage("zombie", 5, cow.Position));
        pumpUntil(() => guest.HurtHeard.Count > 0, "the host's hurt to reach A", [guest]);
        if (guest.HurtHeard.Dequeue() is not { Kind: "zombie", HalfHearts: 5 }) faults.Add("A's hurt arrived wrong");

        game.Herd.Remove(cow);
        pumpUntil(() => Mirrored(() => mirror.Count == 0), "the cow to leave A's mirror once the host's is gone", [guest]);
        if (mirrorHerd.All.Count != 0) faults.Add($"{mirrorHerd.All.Count} creatures left in A's herd after the host's went");
        game.Herd.Remove(far);

        // The herd chases, and hurts, the nearest player: a zombie beside a guest and far from the host.
        var arena = new CreatureHerd(3);
        var biter = arena.SpawnAt(zombieKind, new Vector3(0.5f, 65f, 0.5f));
        static bool Floor(int x, int y, int z) => y < 65;
        var targets = new List<int>();
        for (var t = 0; t < 400 && targets.Count == 0; t++)
        {
            arena.Update(1f / 20f, Floor, player: new Vector3(200.5f, 65f, 0.5f), others: [new Vector3(2.5f, 65f, 0.5f)]);
            foreach (var blow in arena.TakeAttacks()) targets.Add(blow.Target);
        }
        if (targets.Count == 0) faults.Add($"a zombie two blocks from a guest never struck (it stands at {biter.Position})");
        else if (targets.Any(t => t != 1)) faults.Add($"a zombie beside a guest struck target(s) {string.Join(",", targets)}; want 1 (the guest)");
        else said.Add("creatures: A sees the cow by it and not the far zombie, follows it, lands a blow, is refused one out of reach, takes the host's hurt, sees it go; a zombie strikes the guest beside it");
    }

    private static WorldState NewState(BlockRegistry registry, ItemRegistry items, RecipeBook book, string seed) =>
        new(seed, items, new VoxelWorld(registry), new FurnaceBank(items, book), new ChestBank(items),
            new Inventory(items), new Equipment(items), new PlayerVitals(registry), new RecipeUnlocks());

    /// <summary>The host's game, reduced to a world and a reach rule.</summary>
    private sealed class FakeHostWorld(WorldState state, BlockRegistry registry) : ICoopWorld
    {
        public CreatureHerd Herd { get; } = new(5);
        public CreatureRoster Roster { get; } = new();

        public void CreaturesNear(Vector3 at, float radius, List<CreatureState> into) => Roster.Near(Herd.All, at, radius, into);

        public bool StrikeCreature(int guest, Vector3 guestAt, StrikeMessage strike) =>
            CoopRules.LandStrike(Herd, Roster, guestAt, strike, out _);

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

        // This host's world is not streamed: nothing to sum, nothing to mend (the streamed host below is).
        public bool TryTerrainSum(ChunkPos pos, out ulong sum)
        {
            sum = 0;
            return false;
        }

        public bool IsLoaded(ChunkPos pos) => false;

        public ushort[]? CurrentBlocks(ChunkPos pos) => null;
    }

    /// <summary>
    /// N4, through real streamers: a host world exists round a guest standing far beyond the host's
    /// own ring, a guest's block lands there (even in a chunk the host has not made yet), and a guest
    /// whose generator disagrees has its chunks mended with the host's while one that agrees is left alone.
    /// </summary>
    /// <remarks>
    /// Guest A plants <see cref="TerrainFault"/> (its terrain is wrong on purpose); guest B does not.
    /// Every claim is read from the worlds themselves, never from the session's own counters alone.
    /// </remarks>
    private static void CheckStreaming(string gameVersion, string folder, List<string> faults, List<string> said)
    {
        var registry = new BlockRegistry();
        var ids = StarterBlocks.Register(registry);
        var items = StarterItems.Register(registry);
        var build = BuildIdentity.Of(gameVersion, registry, items);
        var planks = registry.ByName("driftoak_planks").Id;
        var seed = WorldSeed.Parse("netcheck-stream");

        TerrainGenerator Generator() => new(seed, ids, version: WorldAudit.TerrainVersion);
        var generator = Generator();

        Vector3 Ground(float x, float z) => new(x, generator.SurfaceHeight((int)MathF.Floor(x), (int)MathF.Floor(z)) + 1f, z);

        var hostAt = Ground(0.5f, 0.5f);
        var farAt = Ground(5000.5f, 40.5f);       // 156 chunks out: nowhere near the host's ring
        var nearAt = Ground(20.5f, 8.5f);         // guest B, beside the host
        var heldAt = Ground(-6000.5f, 300.5f);    // where nobody has been yet

        using var hostStreamer = new WorldStreamer(registry, Generator(), meshRadius: 2, workerCount: 2) { KeepTerrainSums = true };
        using var aStreamer = new WorldStreamer(registry, Generator(), meshRadius: 2, workerCount: 1)
            { KeepTerrainSums = true, ReportTerrainSums = true, TerrainFault = TerrainFault.Plant };
        using var bStreamer = new WorldStreamer(registry, Generator(), meshRadius: 2, workerCount: 1)
            { KeepTerrainSums = true, ReportTerrainSums = true };

        var game = new StreamerHostWorld(hostStreamer, registry, seed.ToString());
        var records = new GuestRecords(Path.Combine(folder, "stream.players"));
        using var listener = TcpTransport.Listen(0);
        using var host = new CoopHost(listener, game, records, build, "Host");

        CoopGuest Dial(string key, string name)
        {
            var link = TcpTransport.ConnectAsync($"127.0.0.1:{listener.Port}", CancellationToken.None).GetAwaiter().GetResult();
            return new CoopGuest(link, new HelloMessage(Protocol.Version, build.GameVersion, build.Registry, key, name));
        }

        using var guestA = Dial(new string('1', 32), "Far");
        using var guestB = Dial(new string('2', 32), "Near");

        var clock = Stopwatch.StartNew();
        double Now() => clock.Elapsed.TotalSeconds;
        var hostMeshesNearA = 0;
        var aMended = 0;
        var bMended = 0;
        var aPos = farAt;

        void Step()
        {
            host.Poll(Now(), 0.01f);
            guestA.Poll(Now());
            guestB.Poll(Now());

            var visitors = host.Players.Values.Where(p => p.Latest is not null).Select(p => p.Latest!.Value.Position).ToList();
            hostStreamer.SetVisitors(visitors);
            hostStreamer.Update(hostAt);
            hostStreamer.PromoteReadyChunks();
            while (hostStreamer.TryDequeueMesh(out var mesh))
            {
                var c = ChunkPos.FromWorld((int)aPos.X, 0, (int)aPos.Z);
                if (Math.Abs(mesh.Position.X - c.X) <= WorldStreamer.VisitorRadius + 2) hostMeshesNearA++;
            }
            while (hostStreamer.TryDequeueDropped(out _)) { }

            foreach (var (streamer, guest, at) in new[] { (aStreamer, guestA, aPos), (bStreamer, guestB, nearAt) })
            {
                if (guest.Welcome is null) continue;
                streamer.Update(at);
                streamer.PromoteReadyChunks();
                while (streamer.TryDequeueMesh(out _)) { }
                while (streamer.TryDequeueDropped(out _)) { }

                while (guest.ChunksHeard.TryDequeue(out var mended))
                    if (streamer.ReplaceChunk(mended.Pos, mended.Blocks) > 0)
                    {
                        if (guest == guestA) aMended++;
                        else bMended++;
                    }

                var sums = new List<(ChunkPos, ulong)>();
                while (sums.Count < 1024 && streamer.TryTakeNewSum(out var made)) sums.Add(made);
                if (sums.Count > 0) guest.SendSums(sums);
                guest.SendPose(new PoseMessage(0, at, 0f, 0f, PoseFlags.None, ""));
            }
            Thread.Sleep(5);
        }

        bool Until(Func<bool> done, string what, double seconds = 30)
        {
            var until = Now() + seconds;
            while (!done())
            {
                Step();
                if (Now() > until)
                {
                    faults.Add($"streaming: timed out waiting for: {what}");
                    return false;
                }
            }
            return true;
        }

        if (!Until(() => guestA.Welcome is not null && guestB.Welcome is not null, "both guests' welcomes")) return;

        // 1. The host makes — and lights — the world round a guest far beyond its own ring, but never meshes it.
        var aChunk = ChunkPos.FromWorld((int)MathF.Floor(farAt.X), (int)MathF.Floor(farAt.Y), (int)MathF.Floor(farAt.Z));
        if (!Until(() => hostStreamer.IsLit(aChunk), "the host to make and light the chunk the far guest stands in")) return;

        // ⚠ Lit is not the end of it: a mesh is promoted after the light, so the claim "never meshed"
        // is only worth anything once the host has nothing left in flight (the planted control proved it).
        Until(() => hostStreamer.PendingGenerate == 0 && hostStreamer.PendingLight == 0 && hostStreamer.PendingMesh == 0
                    && hostStreamer.ReadyMeshes == 0, "the host's streaming to settle round the far guest");
        for (var i = 0; i < 50; i++) Step();
        if (hostStreamer.IsMeshWanted(aChunk) || hostMeshesNearA > 0)
            faults.Add($"streaming: the host meshed {hostMeshesNearA} chunks round a guest it cannot see");
        said.Add($"streaming: host made + lit chunk {aChunk} round a guest 5000 blocks out ({hostStreamer.LoadedChunks} chunks loaded), meshed none of it");

        // 2. The far guest's block lands in the host's world.
        var place = ((int)MathF.Floor(farAt.X) + 1, (int)MathF.Floor(farAt.Y) + 1, (int)MathF.Floor(farAt.Z));
        guestA.SendCells([(place.Item1, place.Item2, place.Item3, planks.Value)]);
        if (Until(() => hostStreamer.World.GetBlock(place.Item1, place.Item2, place.Item3) == planks, "the far guest's block to land in the host's world"))
            said.Add("streaming: the far guest's planks are in the host's world");

        // 3. A cell in a chunk the host has never made is held, saved, and in place once the chunk is made.
        var heldCell = ((int)MathF.Floor(heldAt.X), (int)MathF.Floor(heldAt.Y) + 1, (int)MathF.Floor(heldAt.Z));
        var heldChunk = ChunkPos.FromWorld(heldCell.Item1, heldCell.Item2, heldCell.Item3);
        if (hostStreamer.IsGenerated(heldChunk)) faults.Add("streaming: the held cell's chunk was made before anybody went there");
        game.AcceptCells(1, heldAt, [(heldCell.Item1, heldCell.Item2, heldCell.Item3, planks.Value)]);
        if (!hostStreamer.World.Edits.ContainsKey(heldCell)) faults.Add("streaming: a cell in a chunk the host has not made was not kept for the save");
        if (!hostStreamer.World.Changed) faults.Add("streaming: a held cell did not mark the world as needing a save");
        aPos = heldAt;
        if (Until(() => hostStreamer.IsGenerated(heldChunk), "the host to make the chunk the guest walked to"))
        {
            if (hostStreamer.World.GetBlock(heldCell.Item1, heldCell.Item2, heldCell.Item3) != planks)
                faults.Add("streaming: a held cell was not in place once its chunk was made");
            else said.Add("streaming: a cell in an unmade chunk was held, saved, and in place once the chunk was made");
        }

        // 4. Terrain sums: the guest with the wrong generator is mended, the other is left alone.
        var aHere = ChunkPos.FromWorld(heldCell.Item1, heldCell.Item2 - 1, heldCell.Item3);
        bool Agrees(WorldStreamer guest, ChunkPos pos) =>
            guest.CurrentBlocks(pos) is { } g && hostStreamer.CurrentBlocks(pos) is { } h && g.AsSpan().SequenceEqual(h);
        if (Until(() => Agrees(aStreamer, aHere) && aMended > 0, "the wrong-terrain guest's chunk to be mended to the host's"))
            said.Add($"streaming: wrong terrain mended — {aMended} chunks took the host's blocks; the guest's chunk {aHere} now equals the host's");
        // 4b. A mend never undoes a guest's own newest cell: blocks the host took before it applied that
        // cell are written over terrain only. Planted directly — the race is a narrow one by timing.
        var (ox, oy, oz) = aHere.Origin;
        var own = (ox + 3, oy + 3, oz + 3);
        var hostBlocks = hostStreamer.CurrentBlocks(aHere)!;
        var ownBlock = hostStreamer.World.GetBlock(own.Item1, own.Item2, own.Item3) == planks ? BlockId.Air : planks;
        aStreamer.EditBlock(own.Item1, own.Item2, own.Item3, ownBlock);
        aStreamer.World.SetFluid(ox + TerrainFault.Cell, oy + TerrainFault.Cell, oz + TerrainFault.Cell,
            hostBlocks[Chunk.Index(TerrainFault.Cell, TerrainFault.Cell, TerrainFault.Cell)] == 0 ? new BlockId(1) : BlockId.Air);
        aStreamer.ReplaceChunk(aHere, hostBlocks);
        if (aStreamer.World.GetBlock(own.Item1, own.Item2, own.Item3) != ownBlock)
            faults.Add("streaming: a mend undid the guest's own newest cell");
        else if (aStreamer.World.GetBlock(ox + TerrainFault.Cell, oy + TerrainFault.Cell, oz + TerrainFault.Cell).Value
                 != hostBlocks[Chunk.Index(TerrainFault.Cell, TerrainFault.Cell, TerrainFault.Cell)])
            faults.Add("streaming: a mend with the guest's own cell in it did not fix the terrain beside it");
        else said.Add("streaming: a mend fixes the terrain and keeps the guest's own newest cell");

        Until(() => host.SumsAgreed > 0, "some of the agreeing guest's sums to be checked");
        for (var i = 0; i < 200; i++) Step();
        if (bMended > 0) faults.Add($"streaming: {bMended} chunks of a guest whose terrain agrees were 'mended'");
        if (guestB.ChunksHeardTotal > 0) faults.Add($"streaming: the agreeing guest was sent {guestB.ChunksHeardTotal} chunks");
        var nearChunk = ChunkPos.FromWorld((int)nearAt.X, (int)nearAt.Y, (int)nearAt.Z);
        if (!Agrees(bStreamer, nearChunk)) faults.Add($"streaming: the agreeing guest's chunk {nearChunk} differs from the host's");
        said.Add($"streaming: sums {host.SumsAgreed}/{host.SumsChecked} agreed, {host.ChunksMended} mended (all the wrong guest's), {host.SumsUnchecked} too far to check; agreeing guest sent nothing");

        // 5. The far guest leaves: the host forgets the world round it.
        guestA.Leave(TimeSpan.FromSeconds(1));
        if (Until(() => !hostStreamer.IsGenerated(heldChunk), "the host to forget the world round a guest who left"))
            said.Add("streaming: a guest leaving lets the host forget its ring");
    }

    /// <summary>
    /// N5, the host's own systems for more than one player: the dark spawns round each, never within the
    /// near ring of any, under a cap per player; a creature's shot finds the nearest body on its line.
    /// </summary>
    private static void CheckSpawnsAndShots(List<string> faults, List<string> said)
    {
        // A dark flat floor everywhere, the audit's spawner world reduced.
        static bool Solid(int x, int y, int z) => y < 0;
        var world = new SpawnWorld(Solid, Solid, (x, y, z) => false, (x, y, z) => false,
            (x, y, z) => LightValue.Pack(0, 0, 0, 0), (x, z) => Biome.Meadow);
        var kinds = new Dictionary<string, SpawnKind>(StringComparer.Ordinal)
        {
            ["zombie"] = new("zombie", new Vector3(0.6f, 1.95f, 0.6f), true),
        };

        var host = new Vector3(0.5f, 0f, 0.5f);
        var guest = new Vector3(1000.5f, 0f, 0.5f);
        var beside = new Vector3(40.5f, 0f, 0.5f);   // a second guest inside the host's disc
        var spawner = new MonsterSpawner(11);
        int nearHost = 0, nearGuest = 0, tooNear = 0, total = 0;
        for (var frame = 0; frame < 2000 && total < MonsterSpawner.CapFor(3); frame++)
        {
            foreach (var placed in spawner.Step(0.05f, world, host, 0f, total, kinds, others: [guest, beside]))
            {
                total++;
                var p = placed.Position;
                if (Vector3.Distance(p, host) <= SpawnRules.SpawnRadius) nearHost++;
                if (Vector3.Distance(p, guest) <= SpawnRules.SpawnRadius) nearGuest++;
                foreach (var one in new[] { host, guest, beside })
                    if (Vector3.Distance(p, one) < SpawnRules.MinRadius) tooNear++;
            }
        }
        // The near ring of a guest standing in the host's disc, by numbers: the living count held at
        // nothing (the audit's way) so thousands land, and not one may land within 24 of either player.
        var crowd = new MonsterSpawner(12);
        var close = new Vector3(30.5f, 0f, 0.5f);
        var crowded = 0;
        for (var frame = 0; frame < 600; frame++)
            foreach (var placed in crowd.Step(0.05f, world, host, 0f, 0, kinds, others: [close]))
            {
                if (Vector3.Distance(placed.Position, close) < SpawnRules.MinRadius
                    || Vector3.Distance(placed.Position, host) < SpawnRules.MinRadius) crowded++;
            }
        tooNear += crowded;

        if (nearGuest == 0) faults.Add("spawns: nothing came out of the dark round a guest 1000 blocks from the host");
        if (tooNear > 0) faults.Add($"spawns: {tooNear} placed inside the near ring of a player");
        if (total > MonsterSpawner.CapFor(3)) faults.Add($"spawns: {total} placed over a cap of {MonsterSpawner.CapFor(3)}");
        if (total <= SpawnRules.MonsterCap) faults.Add($"spawns: three players were held to one player's cap ({total})");
        if (faults.Count == 0 || !faults[^1].StartsWith("spawns"))
            said.Add($"spawns: {total} for three players (cap {MonsterSpawner.CapFor(3)}), {nearHost} round the host, {nearGuest} round the far guest, none inside anyone's near ring ({crowd.Placed} more with a guest 30 blocks off: none within 24 of either)");

        // A skeleton's arrow along +x: a guest at 6 blocks, the host at 12 behind them, then the other way round.
        var registry = new BlockRegistry();
        StarterBlocks.Register(registry);
        var empty = new VoxelWorld(registry);
        // Loaded and empty along the line: a shot into unloaded space is retired unflown, as in the game.
        for (var cx = -1; cx <= 2; cx++)
        for (var cz = -1; cz <= 0; cz++)
            empty.GetOrCreateChunk(new ChunkPos(cx, 0, cz));
        (Vector3, Vector3) Box(float x) => (new Vector3(x - 0.3f, 0f, -0.3f), new Vector3(x + 0.3f, 1.8f, 0.3f));
        ProjectileImpact? Fly((Vector3, Vector3)? player, IReadOnlyList<(Vector3 Min, Vector3 Max)> guests)
        {
            var pool = new ProjectileSystem(registry);
            pool.Spawn(ProjectileKind.Arrow, ProjectileOwner.Creature, new Vector3(0f, 1f, 0f), Vector3.UnitX,
                ProjectileSystem.ArrowSpeed, 0f, 4, ShotTag.None);
            for (var t = 0; t < 100; t++)
            {
                pool.Update(empty, null, 0.02f, player, guests);
                if (pool.Impacts.Length > 0) return pool.Impacts[0];
            }
            return null;
        }
        var atGuest = Fly(Box(12f), [Box(30f), Box(6f)]);
        var atHost = Fly(Box(6f), [Box(12f)]);
        if (atGuest is not { Guest: 1, Player: false }) faults.Add($"shots: an arrow with a guest nearest on its line hit {atGuest}");
        else if (atHost is not { Guest: -1, Player: true }) faults.Add($"shots: an arrow with the host nearest on its line hit {atHost}");
        else if (HurtMessage.ForShot(ProjectileKind.Arrow, ShotTag.Slowing, 4) is not { } hurt
                 || HurtMessage.ShotOf(hurt.Kind) is not { Kind: ProjectileKind.Arrow, Tag: ShotTag.Slowing })
            faults.Add("shots: an arrow's hurt does not read back as an arrow with its tag");
        else said.Add("shots: a creature's arrow finds the nearest body on its line, guest or host, and a guest's hurt names the arrow and its tag");
    }

    /// <summary>A host whose world is a real streamer, answering cells the way the game does.</summary>
    private sealed class StreamerHostWorld(WorldStreamer streamer, BlockRegistry registry, string seed) : ICoopWorld
    {
        public string WorldName => "netcheck-stream";
        public string Seed => seed;
        public int TerrainVersion => WorldAudit.TerrainVersion;
        public float DayTime => 0.25f;
        public Vector3 Spawn => new(0.5f, 66f, 0.5f);

        /// <summary>Nothing: these guests check streaming, not the welcome (the first host above checks that).</summary>
        public byte[] WriteWorld() => [];

        public IReadOnlyList<(int X, int Y, int Z, ushort Block)> AcceptCells(
            int guest, Vector3 guestAt, IReadOnlyList<(int X, int Y, int Z, ushort Block)> cells)
        {
            var kept = new List<(int, int, int, ushort)>();
            foreach (var cell in cells)
            {
                if (!CoopRules.InReach(guestAt + new Vector3(0f, 1.6f, 0f), cell.X, cell.Y, cell.Z) || cell.Block >= registry.Count) continue;
                streamer.EditOrHold(cell.X, cell.Y, cell.Z, new BlockId(cell.Block));
                kept.Add(cell);
            }
            return kept;
        }

        public bool TryTerrainSum(ChunkPos pos, out ulong sum) => streamer.TryTerrainSum(pos, out sum);

        public bool IsLoaded(ChunkPos pos) => streamer.IsGenerated(pos);

        public ushort[]? CurrentBlocks(ChunkPos pos) => streamer.CurrentBlocks(pos);

        public void CreaturesNear(Vector3 at, float radius, List<CreatureState> into) => into.Clear();

        public bool StrikeCreature(int guest, Vector3 guestAt, StrikeMessage strike) => false;
    }
}
