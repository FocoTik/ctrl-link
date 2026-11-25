using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CtrlLink.Core.Tests;

/// Two Engines in-process over loopback (Advertise=false).
public class EngineTests
{
    static async Task SendFile(TestEngine from, TestEngine to, Outgoing clip, string src, ClipKind kind)
    {
        from.E.Send(clip);
        var r = await T.Next(to, kind + " file", 30);
        Assert.Equal(kind, r.Kind);
        Assert.Single(r.Files);
        Assert.StartsWith(to.Recv, r.Files[0]);
        Assert.Equal(Path.GetFileName(src), Path.GetFileName(r.Files[0]));
        var bytes = File.ReadAllBytes(src);
        Assert.Equal(bytes, File.ReadAllBytes(r.Files[0]));
        Assert.Equal(SHA256.HashData(bytes), r.Fingerprint);
    }

    [Fact]
    public async Task PairByUri_ExchangeEveryKindBothWays()
    {
        using TestEngine a = new("Alpha"), b = new("Beta");
        await T.PairByUri(a, b);
        Assert.Equal("Alpha", b.E.Peers.Single().Name);

        a.E.Send(new Outgoing.TextClip("hello 👋"));
        var r = await T.Next(b, "text");
        Assert.Equal((ClipKind.Text, "hello 👋", "Alpha"), (r.Kind, r.Text, r.FromName));
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes("hello 👋")), r.Fingerprint);

        b.E.Send(new Outgoing.UrlClip("https://example.com/x"));
        r = await T.Next(a, "url");
        Assert.Equal((ClipKind.Url, "https://example.com/x"), (r.Kind, r.Text));

        var big = T.TempFile(3 * 1024 * 1024, "big file.bin");
        await SendFile(a, b, new Outgoing.FilesClip([big]), big, ClipKind.Files);
        await SendFile(b, a, new Outgoing.FilesClip([big]), big, ClipKind.Files);
        var png = T.TempFile(5000, "shot.png");
        await SendFile(a, b, new Outgoing.ImageClip(png), png, ClipKind.Image);
        await SendFile(b, a, new Outgoing.ImageClip(png), png, ClipKind.Image);
        a.E.Send(new Outgoing.ImageClip(png));
        Assert.Equal("shot (1).png", Path.GetFileName((await T.Next(b, "second image")).Files[0])); // clash naming

        // §6.4 loop prevention: content we just wrote is not sent back.
        b.E.MarkWritten(SHA256.HashData(Encoding.UTF8.GetBytes("echo")));
        b.E.Send(new Outgoing.TextClip("echo"));
        b.E.Send(new Outgoing.TextClip("fresh"));
        Assert.Equal("fresh", (await T.Next(a, "fresh")).Text);
        b.E.MarkWritten(SHA256.HashData(File.ReadAllBytes(png)));
        b.E.Send(new Outgoing.ImageClip(png));
        b.E.Send(new Outgoing.TextClip("after image"));
        Assert.Equal("after image", (await T.Next(a, "after image")).Text);

        // Large text goes as one .txt file (§6.1).
        var text = new string('x', 200 * 1024);
        a.E.Send(new Outgoing.TextClip(text));
        r = await T.Next(b, "large text");
        Assert.Equal(ClipKind.Files, r.Kind);
        Assert.Equal(text, File.ReadAllText(r.Files[0]));

        // History: counts per day, persisted without content.
        var today = DateOnly.FromDateTime(DateTime.Now);
        await T.Until(() => a.E.CountsOn(today).GetValueOrDefault(ClipKind.Files) >= 2, "counts");
        Assert.Equal(3, a.E.CountsOn(today)[ClipKind.Text]); // sent and received both count
        Assert.Equal(3, a.E.CountsOn(today)[ClipKind.Image]);
        Assert.Equal(1, a.E.CountsOn(today)[ClipKind.Url]);
        Assert.NotEmpty(a.E.Clips);
        Assert.DoesNotContain("hello", File.ReadAllText(Path.Combine(a.Dir, "state.json")));
        Assert.Contains(b.Notes, n => n is Note.Connected { Name: "Alpha" });
    }

    [Fact]
    public async Task PairByCode_AfterWrongCode()
    {
        using TestEngine a = new("Host"), b = new("Joiner");
        a.E.OpenPairingWindow();
        await T.Until(() => a.E.PairingWindow is not null, "window");
        var wrong = ((int.Parse(a.E.PairingWindow!.Code) + 1) % 1_000_000).ToString("D6");
        b.E.JoinWithCode(wrong, a.Ep);
        await T.Until(() => b.E.Join is JoinState.Failed, "bad code");
        Assert.Equal("Wrong code", ((JoinState.Failed)b.E.Join).Reason);
        Assert.Null(a.E.PairingWindow!.Request); // no user prompt for a bad MAC

        await T.PairByCode(a, b);
        a.E.Send(new Outgoing.TextClip("via code"));
        Assert.Equal("via code", (await T.Next(b, "text")).Text);
    }

    [Fact]
    public async Task HostRejectsAndWindowClosesAfterFiveFailures()
    {
        using TestEngine a = new("Host"), b = new("Joiner");
        a.E.OpenPairingWindow();
        await T.Until(() => a.E.PairingWindow is not null, "window");
        b.E.JoinWithCode(a.E.PairingWindow!.Code, a.Ep);
        await T.Until(() => a.E.PairingWindow?.Request is not null, "request");
        a.E.AnswerPairRequest(false);
        await T.Until(() => b.E.Join is JoinState.Failed, "rejected");
        Assert.Equal("Pairing was declined", ((JoinState.Failed)b.E.Join).Reason);

        var wrong = ((int.Parse(a.E.PairingWindow!.Code) + 1) % 1_000_000).ToString("D6");
        for (var i = 0; i < 5; i++)
        {
            b.E.ResetJoin();
            await T.Until(() => b.E.Join is JoinState.Idle, "reset");
            b.E.JoinWithCode(wrong, a.Ep);
            await T.Until(() => b.E.Join is JoinState.Failed, "bad code " + i);
        }
        await T.Until(() => a.E.PairingWindow is null, "window closed");
        b.E.ResetJoin();
        await T.Until(() => b.E.Join is JoinState.Idle, "reset");
        b.E.JoinWithCode(wrong, a.Ep);
        await T.Until(() => b.E.Join is JoinState.Failed, "closed");
        Assert.Equal("Pairing window is closed", ((JoinState.Failed)b.E.Join).Reason);
    }

    [Fact]
    public async Task OverLimitOffer_RejectThenAccept()
    {
        using TestEngine a = new("A"), b = new("B");
        await T.PairByUri(a, b);
        b.E.AutoAcceptLimit = 1000;
        var src = T.TempFile(4000, "doc.pdf");

        a.E.Send(new Outgoing.FilesClip([src]));
        await T.Until(() => b.E.Offers.Count == 1, "offer");
        var o = b.E.Offers[0];
        Assert.Equal((1, 4000L, "A"), (o.Count, o.Size, o.PeerName));
        Assert.Contains(b.Notes, n => n is Note.OfferNote);
        b.E.RejectOffer(o.Id);
        await T.Until(() => b.E.Offers.Count == 0, "offer gone");

        a.E.Send(new Outgoing.FilesClip([src]));
        await T.Until(() => b.E.Offers.Count == 1, "offer 2");
        b.E.AcceptOffer(b.E.Offers[0].Id);
        var r = await T.Next(b, "accepted file");
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(r.Files[0]));
        Assert.Single(Directory.GetFiles(b.Recv)); // the rejected one never arrived
    }

    [Fact]
    public async Task CancelMidTransfer_KeepsSessionAndDropsPartialFile()
    {
        using TestEngine a = new("A"), b = new("B");
        await T.PairByUri(a, b);
        var cancelled = false;
        b.E.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IEngine.Transfers) && !cancelled && b.E.Transfers.FirstOrDefault() is { Incoming: true, Done: > 0 } t)
            {
                cancelled = true;
                b.E.CancelTransfer(t.Id);
            }
        };
        var src = T.TempFile(32 * 1024 * 1024, "huge.bin");
        a.E.Send(new Outgoing.FilesClip([src]));
        await T.Until(() => cancelled, "transfer started");
        await T.Until(() => b.E.Transfers.Count == 0 && a.E.Transfers.Count == 0, "transfers ended");
        a.E.Send(new Outgoing.TextClip("still here"));
        Assert.Equal("still here", (await T.Next(b, "text after cancel")).Text);
        Assert.True(b.Online(a));
        Assert.False(Directory.Exists(b.Recv) && Directory.GetFiles(b.Recv).Length > 0);
    }

    [Fact]
    public async Task Unpair_RemovesBothSides()
    {
        using TestEngine a = new("A"), b = new("B");
        await T.PairByUri(a, b);
        a.E.Unpair(b.E.DeviceId);
        await T.Until(() => a.E.Peers.Count == 0 && b.E.Peers.Count == 0, "unpaired");
        await T.Until(() => b.Notes.Any(n => n is Note.Unpaired { Name: "A" }), "unpaired note");
    }

    [Fact]
    public void SafeNameAndUnique()
    {
        Assert.Equal("passwd", Session.SafeName("../../etc/passwd"));
        Assert.Equal("evil.exe", Session.SafeName(@"C:\Windows\evil.exe"));
        Assert.Equal("hidden", Session.SafeName("...hidden"));
        Assert.Equal("a_b_c_d", Session.SafeName("a:b\u0001c\u007fd"));
        Assert.Equal("q_x_.txt", Session.SafeName("q?x*.txt"));
        Assert.Equal("file", Session.SafeName(""));
        Assert.Equal("file", Session.SafeName(null));
        Assert.Equal("file", Session.SafeName("..."));
        Assert.Equal("_con.txt", Session.SafeName("con.txt"));
        Assert.Equal("name", Session.SafeName("name. "));
        var longName = Session.SafeName(string.Concat(Enumerable.Repeat("é", 150)) + ".txt");
        Assert.True(Encoding.UTF8.GetByteCount(longName) <= 200);
        Assert.Equal(100, longName.Length);
        Assert.Equal(200, Encoding.UTF8.GetByteCount(Session.SafeName(string.Concat(Enumerable.Repeat("😀", 60))))); // never splits a surrogate pair

        var dir = Directory.CreateTempSubdirectory().FullName;
        Assert.Equal(Path.Combine(dir, "a.txt"), Session.Unique(dir, "a.txt"));
        File.WriteAllText(Path.Combine(dir, "a.txt"), "");
        File.WriteAllText(Path.Combine(dir, "a (1).txt"), "");
        Assert.Equal(Path.Combine(dir, "a (2).txt"), Session.Unique(dir, "a.txt"));
        File.WriteAllText(Path.Combine(dir, "noext"), "");
        Assert.Equal(Path.Combine(dir, "noext (1)"), Session.Unique(dir, "noext"));
    }

    [Fact]
    public async Task Store_PersistsIdentityProtectsKeysAndKeepsBadFileAside()
    {
        string dir, id;
        using (TestEngine a = new("A"), b = new("B"))
        {
            await T.PairByUri(a, b);
            dir = a.Dir;
            id = a.E.DeviceId;
            var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "state.json"))).RootElement;
            Assert.Equal(id, json.GetProperty("deviceId").GetString());
            var stored = Convert.FromBase64String(json.GetProperty("pairings")[0].GetProperty("key").GetString()!);
            Assert.Equal(32, new NoProtect().Unprotect(stored).Length);
            a.E.Dispose();
            var again = new Engine(new EngineOptions(Path.Combine(dir, "state.json"), dir, new NoProtect(), Advertise: false));
            Assert.Equal(id, again.DeviceId);
            Assert.Equal("B", again.Peers.Single().Name);
            again.Dispose();
            File.WriteAllText(Path.Combine(dir, "state.json"), "{ not json");
            var fresh = new Engine(new EngineOptions(Path.Combine(dir, "state.json"), dir, new NoProtect(), Advertise: false, DefaultDeviceName: "PC"));
            Assert.NotEqual(id, fresh.DeviceId);
            Assert.Equal("PC", fresh.DeviceName);
            Assert.Equal("{ not json", File.ReadAllText(Directory.GetFiles(dir, "state.json.bad-*").Single()));
            fresh.Dispose();
        }
    }

    [Fact]
    public async Task PortFallbackAndVersionError()
    {
        using var a = new TestEngine("A");
        await T.Until(() => a.E.Port is not null, "port");
        using var b = new TestEngine("B", port: a.E.Port!.Value);
        await T.Until(() => b.E.Port is not null, "port b");
        Assert.NotEqual(a.E.Port, b.E.Port);

        using var c = new TcpClient();
        await c.ConnectAsync(a.Ep);
        var s = c.GetStream();
        var body = Encoding.UTF8.GetBytes("""{"t":"hello","v":2,"id":"x","nonce":"AA=="}""");
        await s.WriteAsync((byte[])[0, 0, 0, (byte)body.Length, .. body]);
        var len = new byte[4];
        await s.ReadExactlyAsync(len);
        var reply = new byte[len[3]];
        await s.ReadExactlyAsync(reply);
        Assert.Equal("""{"t":"error","reason":"version"}""", Encoding.UTF8.GetString(reply));
    }
}
