using System.Diagnostics;
using System.Security.Cryptography;
using Driftwood.Core.Net;

namespace Driftwood.Core.Diagnostics;

/// <summary>
/// <c>--eos-check</c>: the Epic meeting place end to end, for the build it runs in. Needs the network.
/// </summary>
/// <remarks>
/// <para>Loads the Epic module beside the program the way the game does, signs two players in (each
/// with a device identity of its own, in a temporary folder), lists a world as one, finds it as the
/// other, connects them over Epic's P2P and sends whole messages both ways — a few megabytes, as a
/// world on join is — checking every byte that ARRIVED. Exit 0 only when every step works.</para>
/// </remarks>
public static class EosCheck
{
    private const string HostName = "Eos Check Host";

    /// <summary>The host side, in this process; the guest is a child process (see <see cref="RunGuest"/>).</summary>
    /// <remarks>⛳ Epic keeps a device identity per <c>XDG_DATA_HOME</c> (<c>.eos_sdk/keychain</c>), read
    /// per process, so two players in ONE process are the same player (seen 2026-11-04). The guest
    /// therefore runs as a second process with its own data folder — which is also how two machines play.</remarks>
    public static (bool Passed, string Report) Run(string gameVersion, string configFolder)
    {
        var faults = new List<string>();
        var said = new List<string>();
        var folder = Path.Combine(Path.GetTempPath(), $"driftwood-eoscheck-{Environment.ProcessId}");
        IMeetingPlace? place = null;
        Process? guest = null;
        try
        {
            place = MeetingPlaces.Start(configFolder, Path.Combine(folder, "host-cache"), HostName, out var why);
            if (place is null) faults.Add($"the Epic module did not start: {why}");
            else guest = Host(gameVersion, configFolder, folder, place, faults, said);
        }
        catch (Exception error)
        {
            faults.Add($"the check itself threw: {error.GetBaseException().Message}");
        }
        finally
        {
            try { if (guest is { HasExited: false }) guest.Kill(entireProcessTree: true); } catch { }
            place?.Dispose();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }

        return Report(faults, said);
    }

    private static (bool, string) Report(List<string> faults, List<string> said) =>
        (faults.Count == 0, string.Join('\n', said.Select(line => "  " + line))
                            + "\n" + (faults.Count == 0
                                ? "eos-check   PASSED"
                                : "eos-check   FAILED\n" + string.Join('\n', faults.Select(f => "  ✗ " + f))));

    private static Process? Host(string gameVersion, string configFolder, string folder, IMeetingPlace place,
        List<string> faults, List<string> said)
    {
        var clock = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (!place.WhenReady(timeout.Token).GetAwaiter().GetResult())
        {
            faults.Add($"the host could not sign in: {place.Fault}");
            return null;
        }

        said.Add($"host signed in after {clock.Elapsed.TotalSeconds:F1} s");
        var world = $"eoscheck-{Environment.ProcessId}-{Random.Shared.Next(100000)}";
        if (place.OpenRoomAsync(world, HostName, 1, gameVersion).GetAwaiter().GetResult() is { } openFault)
        {
            faults.Add($"listing the world failed: {openFault}");
            return null;
        }

        using var link = place.Listen();

        // The guest: this same program, a data folder of its own (so its own Epic identity).
        var guestData = Path.Combine(folder, "guest-data");
        Directory.CreateDirectory(guestData);
        var start = SelfStart();
        start.ArgumentList.Add("--eos-check-guest");
        start.ArgumentList.Add(world);
        start.ArgumentList.Add(place.MyAddress!);
        start.Environment["XDG_DATA_HOME"] = guestData;
        start.Environment["DRIFTWOOD_EOS_CONFIG"] = MeetingPlaces.FindConfig(configFolder);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        var guest = Process.Start(start)!;
        var guestSaid = new System.Collections.Concurrent.ConcurrentQueue<string>();
        guest.OutputDataReceived += (_, line) => { if (line.Data is { } text) guestSaid.Enqueue(text); };
        guest.ErrorDataReceived += (_, line) => { if (line.Data is { } text && !text.StartsWith("epic-sdk")) guestSaid.Enqueue(text); };
        guest.BeginOutputReadLine();
        guest.BeginErrorReadLine();

        // Answer the guest: its two messages are echoed with a 1 MB reply and twenty small unreliable ones.
        int? guestPeer = null;
        byte[]? heardBig = null, heardThree = null;
        var answered = false;
        var gone = false;
        var nextSmall = TimeSpan.Zero;
        var smallSent = 0;
        var until = clock.Elapsed + TimeSpan.FromSeconds(120);
        while (!guest.HasExited && clock.Elapsed < until)
        {
            while (link.TryPoll(out var happened))
            {
                switch (happened.Kind)
                {
                    case NetEventKind.Connected: guestPeer = happened.Peer; break;
                    case NetEventKind.Disconnected: gone = true; break;
                    case NetEventKind.Message when heardBig is null: heardBig = happened.Data; break;
                    case NetEventKind.Message: heardThree ??= happened.Data; break;
                }
            }

            if (!answered && heardThree is not null && guestPeer is { } peer)
            {
                answered = true;
                // The reply carries a digest of what arrived, so the guest can say whether its 3 MB came whole.
                var reply = new byte[1024 * 1024 + 5];
                SHA256.HashData(heardBig!).CopyTo(reply, 0);
                new Random(11).NextBytes(reply.AsSpan(32));
                link.Send(peer, reply, Delivery.Reliable);
            }

            // Then poses' worth of unreliable messages, twenty a second, the way a game sends them:
            // while the reply is still queued they are dropped on purpose, so they come in a stream.
            if (answered && guestPeer is { } posePeer && clock.Elapsed > nextSmall)
            {
                nextSmall = clock.Elapsed + TimeSpan.FromMilliseconds(50);
                link.Send(posePeer, Small(), Delivery.Unreliable);
                smallSent++;
            }

            Thread.Sleep(2);
        }

        // A moment for the leave to land after the guest's process ends.
        var leaveUntil = clock.Elapsed + TimeSpan.FromSeconds(15);
        while (!gone && clock.Elapsed < leaveUntil)
        {
            while (link.TryPoll(out var happened)) if (happened.Kind == NetEventKind.Disconnected) gone = true;
            Thread.Sleep(10);
        }

        guest.WaitForExit(5000);
        said.Add($"host sent {smallSent} unreliable messages");
        while (guestSaid.TryDequeue(out var line)) said.Add("guest: " + line.Trim());

        if (!guest.HasExited) faults.Add("the guest did not finish in two minutes");
        else if (guest.ExitCode != 0) faults.Add($"the guest failed (exit {guest.ExitCode}) — its lines above say why");
        if (guestPeer is null) faults.Add("the host never heard the guest connect");
        if (heardThree is null || !heardThree.AsSpan().SequenceEqual(new byte[] { 7, 7, 7 }))
            faults.Add("the message after the big one did not reach the host whole and in order");
        if (!gone) faults.Add("the host never heard the guest leave");
        else said.Add("the host heard the guest leave");
        place.CloseRoom();
        return guest;
    }

    /// <summary>The unreliable test message: fixed bytes, so the guest knows them.</summary>
    private static byte[] Small()
    {
        var small = new byte[200];
        new Random(5).NextBytes(small);
        return small;
    }

    /// <summary>This program again: the apphost, or <c>dotnet Driftwood.dll</c> in a dev build.</summary>
    private static ProcessStartInfo SelfStart()
    {
        var exe = Environment.ProcessPath!;
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(exe) == "dotnet")
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        return start;
    }

    /// <summary>
    /// <c>--eos-check-guest WORLD HOST</c>: the guest half. Finds the listed world, connects, sends
    /// 3 MB and then three bytes, and checks the host's reply and its unreliable messages. Exit 0 = all good.
    /// </summary>
    public static int RunGuest(string gameVersion, string configFolder, string world, string hostAddress)
    {
        var cache = Path.Combine(Path.GetTempPath(), $"driftwood-eoscheck-guest-{Environment.ProcessId}");
        var place = MeetingPlaces.Start(configFolder, cache, "Eos Check Guest", out var why);
        if (place is null) { Console.WriteLine($"FAIL the Epic module did not start: {why}"); return 1; }
        var failed = false;
        void Fault(string what) { failed = true; Console.WriteLine("FAIL " + what); }

        try
        {
            var clock = Stopwatch.StartNew();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (!place.WhenReady(timeout.Token).GetAwaiter().GetResult()) { Fault($"sign-in: {place.Fault}"); return 1; }
            Console.WriteLine($"signed in after {clock.Elapsed.TotalSeconds:F1} s");
            if (place.MyAddress == hostAddress) { Fault("the guest has the host's Epic identity"); return 1; }

            MeetingRoom? room = null;
            var searches = 0;
            while (room is null && clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                searches++;
                room = place.FindRoomsAsync(timeout.Token).GetAwaiter().GetResult().FirstOrDefault(r => r.World == world);
                if (room is null) Thread.Sleep(1000);
            }

            if (room is null) { Fault($"never found the listed world ({searches} searches)"); return 1; }
            Console.WriteLine($"found '{room.World}' by {room.HostName}, {room.Players} player(s), v{room.Version}, after {searches} search(es)");
            if (room.HostAddress != hostAddress) Fault("the listing does not carry the host's address");
            if (room.HostName != HostName || room.Players != 1 || room.Version != gameVersion) Fault($"the listing's details are wrong: {room}");

            var connectAt = clock.Elapsed;
            using var connecting = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var link = place.ConnectAsync(room.HostAddress, connecting.Token).GetAwaiter().GetResult();
            Console.WriteLine($"P2P link up in {(clock.Elapsed - connectAt).TotalSeconds:F1} s");

            var big = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
            var sendAt = clock.Elapsed;
            link.Send(INetTransport.HostPeer, big, Delivery.Reliable);
            link.Send(INetTransport.HostPeer, [7, 7, 7], Delivery.Reliable);

            byte[]? reply = null;
            var small = Small();
            var heardSmall = 0;
            var until = clock.Elapsed + TimeSpan.FromSeconds(60);
            while ((reply is null || heardSmall < 10) && clock.Elapsed < until)
            {
                while (link.TryPoll(out var happened))
                {
                    if (happened.Kind == NetEventKind.Disconnected) Fault($"the link dropped: {happened.Reason}");
                    if (happened.Kind != NetEventKind.Message) continue;
                    if (happened.Data!.Length == 1024 * 1024 + 5) reply = happened.Data;
                    else if (happened.Data.AsSpan().SequenceEqual(small)) heardSmall++;
                }

                Thread.Sleep(2);
            }

            // Stragglers among the unreliable ones, briefly.
            var straggle = clock.Elapsed + TimeSpan.FromMilliseconds(500);
            while (clock.Elapsed < straggle)
            {
                while (link.TryPoll(out var happened))
                    if (happened.Kind == NetEventKind.Message && happened.Data!.AsSpan().SequenceEqual(small)) heardSmall++;
                Thread.Sleep(5);
            }

            if (reply is null) Fault("the host's 1 MB reply never arrived");
            else
            {
                if (!reply.AsSpan(0, 32).SequenceEqual(SHA256.HashData(big))) Fault("the host did not receive the 3 MB whole");
                var expected = new byte[1024 * 1024 + 5 - 32];
                new Random(11).NextBytes(expected);
                if (!reply.AsSpan(32).SequenceEqual(expected)) Fault("the host's 1 MB reply arrived wrong");
            }

            if (heardSmall == 0) Fault("no unreliable message arrived");
            var seconds = (clock.Elapsed - sendAt).TotalSeconds;
            Console.WriteLine($"3 MB there and 1 MB back, then {heardSmall} unreliable, in {seconds:F1} s");
            link.Drain(TimeSpan.FromSeconds(1));
        }
        catch (Exception error)
        {
            Fault($"threw: {error.GetBaseException().Message}");
        }
        finally
        {
            place.Dispose();
            try { Directory.Delete(cache, recursive: true); } catch (IOException) { }
        }

        return failed ? 1 : 0;
    }
}
