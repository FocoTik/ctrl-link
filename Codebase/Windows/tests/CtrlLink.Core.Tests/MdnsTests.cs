using System.Diagnostics;
using System.Net;

namespace CtrlLink.Core.Tests;

public class MdnsWireTests
{
    [Fact]
    public void PacketRoundTripsAndCompressedNamesParse()
    {
        string[] inst = ["My PC.x", .. Mdns.Svc], host = ["ctrllink-1234", "local"];
        var txt = new Dictionary<string, string> { ["v"] = "1", ["id"] = "abc", ["pair"] = "0" };
        var pkt = Mdns.Packet(0, [], [new(Mdns.Svc, Mdns.PTR, 4500, Target: inst), new(inst, Mdns.SRV, 120, true, host, 47823)],
            [new(inst, Mdns.TXT, 4500, true, Txt: txt), new(host, Mdns.A, 120, true, Addr: IPAddress.Parse("192.168.1.20"))]);
        var m = Mdns.Parse(pkt)!;
        Assert.True(m.Response);
        Assert.Equal(4, m.Records.Count);
        Assert.Equal(inst, m.Records[0].Target);
        Assert.Equal((47823, true), (m.Records[1].Port, m.Records[1].Flush));
        Assert.Equal("abc", m.Records[2].Txt!["id"]);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), m.Records[3].Addr);

        // A PTR whose target points back into the question name (0xC00C), as Bonjour sends it.
        byte[] q = [.. Mdns.Packet(0, [(Mdns.Svc, Mdns.PTR)], [], [])];
        q[2] = 0x84; // make it a response
        byte[] withAnswer = [.. q[..6], 0, 1, .. q[8..], 0xC0, 0x0C, 0, 12, 0, 1, 0, 0, 0x11, 0x94, 0, 5, 2, (byte)'h', (byte)'i', 0xC0, 0x0C];
        var r = Mdns.Parse(withAnswer)!;
        Assert.Equal(["hi", .. Mdns.Svc], r.Records.Single().Target!);
        Assert.Null(Mdns.Parse([0xC0, 0x0C, 1])); // garbage is rejected, not thrown
    }
}

/// Interop with Apple's mDNSResponder through `dns-sd`. Skipped where dns-sd is absent (Windows CI).
[Trait("Category", "mDNS")]
public class MdnsInteropTests
{
    static async Task<string> DnsSd(string until, double seconds, params string[] args)
    {
        var psi = new ProcessStartInfo("dns-sd") { RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            while (await p.StandardOutput.ReadLineAsync(cts.Token) is { } l)
            {
                lines.Add(l);
                if (string.Join("\n", lines).Contains(until)) break;
            }
        }
        catch (OperationCanceledException) { }
        finally { p.Kill(); }
        return string.Join("\n", lines);
    }

    [DnsSdFact]
    public async Task EngineAdvertisementIsVisibleToBonjour()
    {
        var name = "CSTest " + Guid.NewGuid().ToString()[..6];
        using var e = new TestEngine(name, advertise: true);
        await T.Until(() => e.E.Port is not null, "port");
        var browse = await DnsSd(name, 10, "-B", "_ctrllink._tcp", "local");
        Assert.Contains(name, browse);
        var resolve = await DnsSd("id=" + e.E.DeviceId, 10, "-L", name, "_ctrllink._tcp", "local");
        Assert.Contains($":{e.E.Port}", resolve);
        Assert.Contains("id=" + e.E.DeviceId, resolve);
        Assert.Contains("pair=0", resolve);
        var host = $"ctrllink-{e.E.DeviceId[..8]}.local";
        Assert.Contains(host, resolve);
        var addr = await DnsSd(" Add ", 10, "-G", "v4", host);
        Assert.Matches(@"Add\s+\S+\s+\d+\s+\S+\s+\d+\.\d+\.\d+\.\d+", addr);

        e.E.OpenPairingWindow(); // TXT pair flips to 1 and is re-announced
        resolve = await DnsSd("pair=1", 10, "-L", name, "_ctrllink._tcp", "local");
        Assert.Contains("pair=1", resolve);
    }

    [DnsSdFact]
    public async Task EngineBrowserFindsBonjourService()
    {
        var id = Guid.NewGuid().ToString();
        var psi = new ProcessStartInfo("dns-sd") { RedirectStandardOutput = true };
        foreach (var a in (string[])["-R", "CSPeer " + id[..6], "_ctrllink._tcp", "local", "45678", "v=1", $"id={id}", "pf=mac", "pair=1"]) psi.ArgumentList.Add(a);
        using var reg = Process.Start(psi)!;
        try
        {
            using var e = new TestEngine("CSBrowser", advertise: true);
            await T.Until(() => e.E.Nearby.ContainsKey(id), "browse result", 15);
            var n = e.E.Nearby[id];
            Assert.Equal((45678, "mac", true), (n.Endpoint.Port, n.Pf, n.Pairing));
        }
        finally { reg.Kill(); }
    }

    [Fact]
    public async Task AnswersLegacyUnicastQuery()
    {
        var name = "CSQuery " + Guid.NewGuid().ToString()[..6];
        using var e = new TestEngine(name, advertise: true);
        await T.Until(() => e.E.Port is not null, "port");
        using var u = new System.Net.Sockets.UdpClient(0);
        var q = Mdns.Packet(0x1234, [(Mdns.Svc, Mdns.PTR)], [], []);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            // Re-ask every 500 ms: the first query can arrive before the responder has joined the group, and nobody else may answer.
            await u.SendAsync(q, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353));
            using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            System.Net.Sockets.UdpReceiveResult got;
            try { got = await u.ReceiveAsync(wait.Token); }
            catch (OperationCanceledException) when (DateTime.UtcNow < deadline) { continue; }
            var reply = Mdns.Parse(got.Buffer);
            if (reply is not { Id: 0x1234 } || !reply.Records.Any(r => r.Target?[0] == name)) continue; // other responders answer too
            Assert.Contains(reply.Records, r => r.Type == Mdns.SRV && r.Port == e.E.Port && r.Ttl <= 10);
            Assert.Contains(reply.Records, r => r.Txt?.GetValueOrDefault("id") == e.E.DeviceId);
            return;
        }
    }

    [Fact]
    public async Task TwoEnginesFindEachOtherAndPairByCode()
    {
        using TestEngine a = new("CS Twin A", advertise: true), b = new("CS Twin B", advertise: true);
        await T.Until(() => a.E.Nearby.ContainsKey(b.E.DeviceId) && b.E.Nearby.ContainsKey(a.E.DeviceId), "mutual discovery", 15);
        Assert.Equal(b.E.Port, a.E.Nearby[b.E.DeviceId].Endpoint.Port);

        a.E.OpenPairingWindow(); // pair=1 is re-announced and seen by b's browser
        await T.Until(() => b.E.Nearby.GetValueOrDefault(a.E.DeviceId)?.Pairing == true, "pair=1", 10);
        b.E.JoinWithCode(a.E.PairingWindow!.Code);
        await T.Until(() => a.E.PairingWindow?.Request is not null, "request");
        a.E.AnswerPairRequest(true);
        await T.Until(() => a.Online(b) && b.Online(a), "session over mDNS address");
        await T.Until(() => b.E.Nearby.GetValueOrDefault(a.E.DeviceId)?.Pairing == false, "pair=0", 10);
        b.E.Dispose(); // goodbye (TTL 0) removes it from a's browser right away
        await T.Until(() => !a.E.Nearby.ContainsKey(b.E.DeviceId), "goodbye", 5);
    }
}
