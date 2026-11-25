using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

namespace CtrlLink.Core.Tests;

sealed class NoProtect : ISecretProtector
{
    public byte[] Protect(byte[] plain) => [.. plain.Select(b => (byte)(b ^ 0x5A))]; // not identity, so tests see it was used
    public byte[] Unprotect(byte[] sealedBytes) => Protect(sealedBytes);
}

/// An Engine in a temp dir that records everything it emits.
sealed class TestEngine : IDisposable
{
    public readonly string Dir = Directory.CreateTempSubdirectory("ctrllink-test-").FullName;
    public readonly Engine E;
    public readonly ConcurrentQueue<Received> Got = new();
    public readonly ConcurrentQueue<Note> Notes = new();

    public TestEngine(string name, bool advertise = false, int port = 0)
    {
        E = new Engine(new EngineOptions(Path.Combine(Dir, "state.json"), Path.Combine(Dir, "recv"), new NoProtect(),
            Advertise: advertise, Port: port, RetryDelaySeconds: 0.5, DefaultDeviceName: name));
        E.Receive += Got.Enqueue;
        E.Notify += Notes.Enqueue;
        E.Start();
    }

    public string Recv => Path.Combine(Dir, "recv");
    public IPEndPoint Ep => new(IPAddress.Loopback, E.Port!.Value);
    public bool Online(TestEngine other) => E.Peers.Any(p => p.Id == other.E.DeviceId && p.Online);

    public void Dispose()
    {
        E.Dispose();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

static class T
{
    public static async Task Until(Func<bool> cond, string what, double seconds = 15)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!cond())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("timed out waiting for " + what);
            await Task.Delay(25);
        }
    }

    public static async Task<Received> Next(TestEngine e, string what, double seconds = 15)
    {
        Received? r = null;
        await Until(() => e.Got.TryDequeue(out r), what, seconds);
        return r!;
    }

    public static string LocalUri(string uri) => Regex.Replace(uri, "h=[^&]*", "h=127.0.0.1");

    public static async Task PairByUri(TestEngine host, TestEngine joiner)
    {
        host.E.OpenPairingWindow();
        await Until(() => host.E.PairingWindow is not null, "pairing window");
        joiner.E.JoinWithUri(LocalUri(host.E.PairingWindow!.Uri));
        await Until(() => joiner.E.Join is JoinState.Done or JoinState.Failed, "join");
        Assert.IsType<JoinState.Done>(joiner.E.Join);
        await Until(() => host.Online(joiner) && joiner.Online(host), "session");
    }

    public static async Task PairByCode(TestEngine host, TestEngine joiner)
    {
        host.E.OpenPairingWindow();
        await Until(() => host.E.PairingWindow is not null, "pairing window");
        joiner.E.JoinWithCode(host.E.PairingWindow!.Code, host.Ep);
        await Until(() => host.E.PairingWindow?.Request is not null, "pair request");
        await Until(() => joiner.E.Join is JoinState.Confirm, "joiner SAS");
        Assert.Equal(((JoinState.Confirm)joiner.E.Join).Sas, host.E.PairingWindow!.Request!.Sas);
        host.E.AnswerPairRequest(true);
        await Until(() => joiner.E.Join is JoinState.Done, "join done");
        await Until(() => host.Online(joiner) && joiner.Online(host), "session");
        Assert.Null(host.E.PairingWindow);
    }

    public static string TempFile(int size, string name)
    {
        var p = Path.Combine(Directory.CreateTempSubdirectory("ctrllink-src-").FullName, name);
        var b = new byte[size];
        Random.Shared.NextBytes(b);
        File.WriteAllBytes(p, b);
        return p;
    }

    public static string? Which(string cmd) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .SelectMany(d => new[] { Path.Combine(d, cmd), Path.Combine(d, cmd + ".exe") }).FirstOrDefault(File.Exists);

    public static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "protocol", "PROTOCOL.md"))) return d.FullName;
        throw new DirectoryNotFoundException("repo root");
    }
}

sealed class NodeFactAttribute : FactAttribute
{
    public NodeFactAttribute() { if (T.Which("node") is null) Skip = "node not found"; }
}

/// macOS mDNS interop; filter out with `--filter Category!=mDNS` on CI.
sealed class DnsSdFactAttribute : FactAttribute
{
    public DnsSdFactAttribute() { if (T.Which("dns-sd") is null || !OperatingSystem.IsMacOS()) Skip = "dns-sd (macOS) not found"; }
}
