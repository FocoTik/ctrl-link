using System.Net;
using System.Text;

namespace CtrlLink.Core.Tests;

/// History persistence, Clear history, speed sampling, transfer end / cancel, and the address shown in the Devices graph.
public class HistoryTests
{
    static Engine Open(string dir) => new(new EngineOptions(Path.Combine(dir, "state.json"), Path.Combine(dir, "recv"), new NoProtect(), Advertise: false));

    [Fact]
    public async Task History_PersistsAcrossRestartsWithLimits()
    {
        var dir = Directory.CreateTempSubdirectory("ctrllink-hist-").FullName;
        try
        {
            var e = Open(dir);
            e.Start();
            var big = new string('x', 40_000) + "😀";
            e.Send(new Outgoing.TextClip("hello"));
            e.Send(new Outgoing.UrlClip("https://example.com"));
            e.Send(new Outgoing.TextClip(big));
            await T.Until(() => e.Clips.Count == 3, "three clips");
            e.Dispose();   // waits for the history write

            Assert.True(File.Exists(Path.Combine(dir, "history.json")));
            var again = Open(dir);
            Assert.Equal(3, again.Clips.Count);
            Assert.Equal(ClipKind.Url, again.Clips.Single(c => c.Text == "https://example.com").Kind);
            var capped = again.Clips.Single(c => c.Text!.StartsWith("xxx"));
            Assert.Equal(HistoryFile.TextCap, Encoding.UTF8.GetByteCount(capped.Text!));
            again.Dispose();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void History_TrimDropsOldAndKeeps300Newest()
    {
        var now = DateTimeOffset.Now;
        var list = Enumerable.Range(0, 400).Select(i => new ClipRecord($"{i}", ClipKind.Text, null, now.AddMinutes(-i), "t", [])).ToList();
        list.Add(new ClipRecord("old", ClipKind.Text, null, now.AddDays(-15), "t", []));
        var kept = HistoryFile.Trim(list, now);
        Assert.Equal(300, kept.Count);
        Assert.Equal("0", kept[0].Id);
        Assert.DoesNotContain(kept, c => c.Id == "old");
        Assert.Equal("ab", HistoryFile.Cap("ab"));
        var emoji = string.Concat(Enumerable.Repeat("😀", 9000));   // 36 000 bytes
        var cut = HistoryFile.Cap(emoji);
        Assert.True(Encoding.UTF8.GetByteCount(cut) <= HistoryFile.TextCap);
        Assert.False(char.IsHighSurrogate(cut[^1]));
    }

    [Fact]
    public async Task ClearHistory_DeletesListCountsAndOwnTempCopiesOnly()
    {
        var dir = Directory.CreateTempSubdirectory("ctrllink-hist-").FullName;
        var tmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Ctrl Link")).FullName;
        var own = Path.Combine(tmpDir, $"clip-test-{Guid.NewGuid():N}.png");
        var user = Path.Combine(dir, "photo.png");
        File.WriteAllBytes(own, [1, 2, 3]);
        File.WriteAllBytes(user, [1, 2, 3]);
        try
        {
            var e = Open(dir);
            e.Start();
            e.Send(new Outgoing.ImageClip(own));
            e.Send(new Outgoing.FilesClip([user]));
            await T.Until(() => e.Clips.Count == 2, "two clips");
            Assert.Equal(2, e.CountsOn(DateOnly.FromDateTime(DateTime.Now)).Values.Sum());
            e.ClearHistory();
            await T.Until(() => e.Clips.Count == 0, "cleared");
            Assert.Empty(e.CountsOn(DateOnly.FromDateTime(DateTime.Now)));
            Assert.False(File.Exists(own));
            Assert.True(File.Exists(user));   // not ours: never deleted
            e.Dispose();
            Assert.False(File.Exists(Path.Combine(dir, "history.json")));
            var again = Open(dir);
            Assert.Empty(again.Clips);
            again.Dispose();
        }
        finally
        {
            File.Delete(own);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void OwnTemp_OnlyMatchesTheAppsTempCopies()
    {
        var tmp = Path.GetFullPath(Path.GetTempPath());
        var img = Path.Combine(tmp, "Ctrl Link", "clip-20260101-120000-000.png");
        Assert.Equal(img, Engine.OwnTemp(tmp, img));
        var txtDir = Path.Combine(tmp, "CtrlLink-" + Guid.NewGuid());
        Assert.Equal(txtDir, Engine.OwnTemp(tmp, Path.Combine(txtDir, "Clipboard.txt")));
        Assert.Null(Engine.OwnTemp(tmp, Path.Combine(tmp, "Ctrl Link", "report.pdf")));
        Assert.Null(Engine.OwnTemp(tmp, Path.Combine(tmp, "other", "clip-1.png")));
        Assert.Null(Engine.OwnTemp(tmp, Path.Combine(tmp, "CtrlLink-notaguid", "Clipboard.txt")));
        Assert.Null(Engine.OwnTemp(tmp, "/Users/someone/Downloads/Ctrl Link/clip-1.png"));
    }

    [Fact]
    public void TransferMeter_SamplesOncePerSecondAndSmoothsRate()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var m = new TransferMeter(t0);
        Assert.Equal(0, m.Rate);
        Assert.False(m.Sample(t0.AddMilliseconds(500), 500, 10_000));   // under a second: no sample
        Assert.True(m.Sample(t0.AddSeconds(1), 1000, 10_000));
        Assert.Equal(1000, m.Rate);
        Assert.Equal(new SpeedSample(0.1, 1000), m.Speeds[^1]);
        Assert.True(m.Sample(t0.AddSeconds(2), 4000, 10_000));
        Assert.Equal(2000, m.Rate);   // 0.5 × 3000 + 0.5 × 1000
        Assert.Equal(new SpeedSample(0.4, 3000), m.Speeds[^1]);
        Assert.Equal(new SpeedSample(0, 0), m.Speeds[0]);   // the graph starts at the origin
        for (var i = 3; i < 700; i++) m.Sample(t0.AddSeconds(i), 4000, 10_000);
        Assert.Equal(TransferMeter.MaxSamples, m.Speeds.Count);
    }

    [Fact]
    public async Task Transfer_EndsAtHundredThenLeaves_AndSenderCancelStopsBothSides()
    {
        using TestEngine a = new("A"), b = new("B");
        await T.PairByUri(a, b);
        var finished = false;
        b.E.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IEngine.Transfers) && b.E.Transfers.FirstOrDefault() is { Finished: true } t && t.Done == t.Total) finished = true;
        };
        a.E.Send(new Outgoing.FilesClip([T.TempFile(3 * 1024 * 1024, "mid.bin")]));
        await T.Next(b, "file");
        Assert.True(finished || b.E.Transfers.Any(t => t.Finished), "receiver showed 100 % before leaving");
        await T.Until(() => b.E.Transfers.Count == 0 && a.E.Transfers.Count == 0, "transfer list emptied");

        // The sender cancels: both sides stop and nothing lands in the received folder.
        var before = Directory.GetFiles(b.Recv).Length;
        var cancelled = false;
        a.E.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IEngine.Transfers) && !cancelled && a.E.Transfers.FirstOrDefault() is { Incoming: false, Done: > 0 } t)
            {
                cancelled = true;
                a.E.CancelTransfer(t.Id);
            }
        };
        a.E.Send(new Outgoing.FilesClip([T.TempFile(48 * 1024 * 1024, "huge.bin")]));
        await T.Until(() => cancelled, "transfer started");
        await T.Until(() => a.E.Transfers.Count == 0 && b.E.Transfers.Count == 0, "both sides stopped");
        await Task.Delay(300);
        Assert.Equal(before, Directory.GetFiles(b.Recv).Length);
        Assert.True(a.Online(b));
    }

    [Fact]
    public void ShownIp_PrefersIPv4AndHidesLinkLocalAndLoopback()
    {
        Assert.Equal("192.168.1.21", NetInfo.ShownIp(IPAddress.Parse("fe80::1%4"), IPAddress.Parse("192.168.1.21")));
        Assert.Equal("192.168.1.21", NetInfo.ShownIp(IPAddress.Parse("::ffff:192.168.1.21")));
        Assert.Null(NetInfo.ShownIp(IPAddress.Parse("fe80::1c2b:3aff:fe11:2233%7"), IPAddress.Loopback, IPAddress.IPv6Loopback, null));
        Assert.Null(NetInfo.ShownIp(IPAddress.Parse("169.254.10.2")));
        Assert.Equal("2001:db8::5", NetInfo.ShownIp(IPAddress.Parse("2001:DB8::5"), IPAddress.Parse("fe80::2")));
        Assert.Null(NetInfo.ShownIp());
        Assert.DoesNotContain("%", NetInfo.LocalIPv4() ?? "");
    }
}
