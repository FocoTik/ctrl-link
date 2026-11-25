using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace CtrlLink.Core.Tests;

/// The Node reference peer (protocol/test-peer) as the other side.
sealed class NodePeer : IDisposable
{
    readonly Process p;
    readonly ConcurrentQueue<JsonObject> events = new();
    public readonly string State = Directory.CreateTempSubdirectory("ctrllink-node-").FullName;

    public NodePeer(params string[] args)
    {
        var psi = new ProcessStartInfo(T.Which("node")!) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])[Path.Combine(T.RepoRoot(), "protocol", "test-peer", "ctrllink-peer.mjs"), .. args,
                     "--state", State, "--no-mdns", "--json", "--port", "0", "--name", "Node Peer"]) psi.ArgumentList.Add(a);
        p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data is { } l && JsonNode.Parse(l) is JsonObject o) events.Enqueue(o); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { } l) Console.Error.WriteLine("node: " + l); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
    }

    public async Task<JsonObject> Wait(string ev, Func<JsonObject, bool>? match = null, double seconds = 15)
    {
        JsonObject? hit = null;
        await T.Until(() => (hit = events.FirstOrDefault(e => (string?)e["ev"] == ev && (match?.Invoke(e) ?? true))) is not null, "node " + ev, seconds);
        return hit!;
    }

    public void Line(string s) => p.StandardInput.WriteLine(s);

    public void Dispose()
    {
        try { p.Kill(true); p.WaitForExit(3000); } catch (InvalidOperationException) { }
        try { Directory.Delete(State, true); } catch (IOException) { }
    }
}

public class NodeInteropTests
{
    static async Task Exchange(TestEngine e, NodePeer n)
    {
        e.E.Send(new Outgoing.TextClip("hi node ✓"));
        await n.Wait("clip", c => (string?)c["text"] == "hi node ✓");

        n.Line("hello engine");
        var r = await T.Next(e, "text from node");
        Assert.Equal((ClipKind.Text, "hello engine", "Node Peer"), (r.Kind, r.Text, r.FromName));

        var src = T.TempFile(600_000, "to node.bin");
        e.E.Send(new Outgoing.FilesClip([src]));
        var f = await n.Wait("file", seconds: 30);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(src))), (string?)f["sha256"]);
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes((string)f["path"]!));

        var back = T.TempFile(700_000, "from node.bin");
        n.Line($"/file \"{back}\"");
        r = await T.Next(e, "file from node", 30);
        Assert.Equal(ClipKind.Files, r.Kind);
        Assert.Equal(File.ReadAllBytes(back), File.ReadAllBytes(r.Files[0]));
    }

    [NodeFact]
    public async Task EngineHosts_NodeJoinsByUri()
    {
        using var e = new TestEngine("Win Host");
        e.E.OpenPairingWindow();
        await T.Until(() => e.E.PairingWindow is not null, "window");
        using var n = new NodePeer("join", T.LocalUri(e.E.PairingWindow!.Uri), "--connect", $"127.0.0.1:{e.E.Port}");
        await n.Wait("paired", x => (string?)x["role"] == "joiner");
        await n.Wait("connected");
        await T.Until(() => e.E.Peers.Any(p => p.Online && p.Name == "Node Peer"), "engine sees node");
        await Exchange(e, n);
    }

    [NodeFact]
    public async Task NodeHosts_EngineJoinsByCode()
    {
        using var e = new TestEngine("Win Joiner");
        using var n = new NodePeer("host", "--auto-accept");
        var w = await n.Wait("pairing");
        e.E.JoinWithCode((string)w["code"]!, new IPEndPoint(IPAddress.Loopback, (int)w["port"]!));
        await T.Until(() => e.E.Join is JoinState.Done or JoinState.Failed, "join");
        Assert.IsType<JoinState.Done>(e.E.Join);
        var sas = await n.Wait("sas");
        Assert.Equal("code", (string?)sas["mode"]);
        await n.Wait("connected");
        await T.Until(() => e.E.Peers.Any(p => p.Online), "session");
        await Exchange(e, n);
    }
}
