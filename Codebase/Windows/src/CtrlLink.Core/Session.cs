using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CtrlLink.Core;

sealed record OutFile(Guid Id, string Path, long Size);

sealed class OutClip(List<OutFile> files)
{
    public readonly List<OutFile> Files = files;
    public bool Cancelled, Started;
    public readonly CancellationTokenSource Timeout = new();
}

sealed class InFile(Guid id, string name, string mime, long size, string tmp)
{
    public readonly Guid Id = id;
    public readonly string Name = name, Mime = mime, Tmp = tmp;
    public readonly long Size = size;
    public FileStream? Handle;
    public readonly IncrementalHash Hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    public long Got;
    public byte[]? Sha;
    public string? Final;
}

sealed class InClip(ClipKind kind, List<InFile> files)
{
    public readonly ClipKind Kind = kind;
    public readonly List<InFile> Files = files;
    public bool Accepted;
}

/// An established, encrypted session with one paired peer (§5–§6). Runs on the engine loop. Mirrors Mac's Session.swift.
sealed class Session(Engine engine, FrameConn conn, string peerId, bool isClient)
{
    public readonly string PeerId = peerId;
    public readonly bool IsClient = isClient;
    public readonly DateTimeOffset Started = DateTimeOffset.Now;
    DateTime lastRx = DateTime.UtcNow, lastPing = DateTime.MinValue;
    DateTime? probeAt;
    bool closed;
    readonly Dictionary<string, OutClip> outgoing = [];
    readonly Dictionary<string, InClip> incoming = [];
    readonly HashSet<Guid> dropped = []; // fileIds of cancelled/rejected clips: late chunks and `done` are ignored (§6.2)

    string Name => engine.NameOf(PeerId);

    public System.Net.IPAddress? Remote => conn.Remote;

    /// Files a peer may still fetch from us (offered or streaming); Clear history keeps them.
    public IEnumerable<string> PendingPaths => outgoing.Values.SelectMany(c => c.Files.Select(f => f.Path)).ToList();

    public async Task Run()
    {
        _ = Ticker();
        try
        {
            while (!closed)
            {
                var pt = await conn.ReadSealed();
                lastRx = DateTime.UtcNow;
                engine.Touched(PeerId);
                await Handle(pt);
            }
        }
        catch (Exception e)
        {
            if (!closed) J.Log($"session {PeerId} ended: {e.Message}");
        }
        Close();
    }

    async Task Ticker()
    {
        while (!closed)
        {
            await Task.Delay(1000);
            var now = DateTime.UtcNow;
            var silent = now - lastRx;
            if (silent > TimeSpan.FromSeconds(45)) { Close(); return; }
            if (probeAt is { } p && lastRx < p && now - p > TimeSpan.FromSeconds(5)) { Close(); return; } // network changed and peer is gone
            if (silent > TimeSpan.FromSeconds(15) && now - lastPing > TimeSpan.FromSeconds(15))
            {
                lastPing = now;
                _ = SendQuiet(new JsonObject { ["t"] = "ping" });
            }
        }
    }

    /// After a network change: ping now, and drop the session if nothing comes back within 5 s.
    public void Probe()
    {
        probeAt = DateTime.UtcNow;
        _ = SendQuiet(new JsonObject { ["t"] = "ping" });
    }

    public void Close()
    {
        if (closed) return;
        closed = true;
        conn.Dispose();
        foreach (var c in outgoing.Values) { c.Cancelled = true; c.Timeout.Cancel(); }
        outgoing.Clear();
        foreach (var id in incoming.Keys.ToList()) DropIncoming(id);
        engine.SessionEnded(this);
    }

    public Task Send(JsonObject m) => conn.WriteSealed(J.Control(m));

    async Task SendQuiet(JsonObject m)
    {
        try { await Send(m); } catch (Exception) { } // the read loop notices a dead connection
    }

    async Task Handle(byte[] pt)
    {
        switch (pt[0])
        {
            case 1:
                await Control(J.Parse(pt.AsSpan(1)) ?? throw new ProtocolException("bad control json"));
                break;
            case 2:
                if (pt.Length < 17) throw new ProtocolException("short chunk");
                await Chunk(new Guid(pt.AsSpan(1, 16), bigEndian: true), pt.AsMemory(17));
                break;
            default: throw new ProtocolException("unknown frame kind"); // §3
        }
    }

    async Task Control(JsonObject m)
    {
        var id = m.S("id") ?? "";
        switch (m.S("t"))
        {
            case "ping": await Send(new JsonObject { ["t"] = "pong" }); break;
            case "clip": Clip(m); break;
            case "accept": Stream(id); break;
            case "reject":
                if (outgoing.Remove(id, out var r)) r.Timeout.Cancel();
                break;
            case "done": await Done(m); break;
            case "cancel":
                if (outgoing.Remove(id, out var c)) { c.Cancelled = true; c.Timeout.Cancel(); }
                DropIncoming(id);
                break;
            case "unpair":
                var name = Name;
                engine.RemovePairing(PeerId);
                engine.Note(new Note.Unpaired(name));
                Close();
                break;
            case "error":
                if (m.S("reason") == "version") engine.Note(new Note.Version(Name));
                break;
            // ready, pong, unknown types (§7)
        }
    }

    /// Either side aborts a transfer (§6): the app's Cancel button.
    public void Cancel(string id)
    {
        if (outgoing.Remove(id, out var c)) { c.Cancelled = true; c.Timeout.Cancel(); engine.EndTransfer($"{PeerId}/{id}", false); }
        else if (incoming.ContainsKey(id)) DropIncoming(id);
        else return;
        _ = SendQuiet(new JsonObject { ["t"] = "cancel", ["id"] = id });
    }

    // ---- sending ----

    public void Offer(JsonObject m, List<OutFile> files)
    {
        if (files.Count > 0 && m.S("id") is { } id)
        {
            var c = outgoing[id] = new OutClip(files);
            _ = GiveUp(id, c);
        }
        _ = SendQuiet(m);
    }

    /// §6.2: no answer for 5 minutes → cancel.
    async Task GiveUp(string id, OutClip c)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(5), c.Timeout.Token); } catch (OperationCanceledException) { return; }
        if (outgoing.GetValueOrDefault(id) != c) return;
        outgoing.Remove(id);
        await SendQuiet(new JsonObject { ["t"] = "cancel", ["id"] = id });
    }

    void Stream(string id)
    {
        if (!outgoing.TryGetValue(id, out var c) || c.Started) return;
        c.Started = true;
        c.Timeout.Cancel();
        engine.loop.Go(async () =>
        {
            var key = $"{PeerId}/{id}";
            var ok = false;
            try
            {
                long total = c.Files.Sum(f => f.Size), sent = 0;
                var buf = new byte[262_144];
                foreach (var f in c.Files)
                {
                    await using var fs = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var fid = f.Id.ToByteArray(bigEndian: true);
                    int n;
                    while ((n = await fs.ReadAsync(buf)) > 0)
                    {
                        if (c.Cancelled || closed) return;
                        h.AppendData(buf, 0, n);
                        await conn.WriteSealed([2, .. fid, .. buf.AsSpan(0, n)]);
                        sent += n;
                        engine.Progress(key, id, Path.GetFileName(f.Path), PeerId, false, sent, total);
                    }
                    if (c.Cancelled || closed) return;
                    await Send(new JsonObject { ["t"] = "done", ["id"] = id, ["fileId"] = f.Id.ToString(), ["sha256"] = Convert.ToHexStringLower(h.GetHashAndReset()) });
                }
                ok = !c.Cancelled && !closed;
            }
            catch (Exception e)
            {
                J.Log($"send {id} failed: {e.Message}");
                await SendQuiet(new JsonObject { ["t"] = "cancel", ["id"] = id });
            }
            finally
            {
                engine.EndTransfer(key, ok);
                if (outgoing.GetValueOrDefault(id) == c) outgoing.Remove(id);
            }
        });
    }

    // ---- receiving ----

    void Clip(JsonObject m)
    {
        if (m.S("id") is not { } id || !Enum.TryParse<ClipKind>(m.S("kind"), true, out var kind) || m.S("kind") != Engine.KindName(kind)) return;
        if (kind is ClipKind.Text or ClipKind.Url)
        {
            if (m.S("text") is { } text)
                engine.Received(new Received(kind, text, [], null, SHA256.HashData(Encoding.UTF8.GetBytes(text)), Name), id);
            return;
        }
        if (incoming.ContainsKey(id) || m["files"] is not JsonArray list || list.Count == 0) return;
        var files = new List<InFile>();
        foreach (var n in list)
        {
            if (n is not JsonObject f || !Guid.TryParse(f.S("fileId"), out var fid) || f.L("size") is not { } size || size < 0) { files.Clear(); break; }
            files.Add(new InFile(fid, SafeName(f.S("name")), f.S("mime") ?? "", size, Path.Combine(Path.GetTempPath(), $"CtrlLink-{Guid.NewGuid()}.part")));
        }
        if (files.Count == 0 || (kind == ClipKind.Image && files.Count != 1))
        {
            _ = SendQuiet(new JsonObject { ["t"] = "reject", ["id"] = id });
            return;
        }
        incoming[id] = new InClip(kind, files);
        var total = files.Sum(f => f.Size);
        if (total <= engine.AutoAcceptLimit) Accept(id);
        else engine.AddOffer(new Offer(id, PeerId, Name, files.Count, total));
    }

    /// §6.3: last path component, `/ \ :` and control chars → `_`, no leading dots, ≤ 200 bytes, empty → `file`.
    /// Windows also forbids `< > " | ? *`, trailing dots/spaces and device names, so those are made safe too.
    internal static string SafeName(string? s)
    {
        var last = (s ?? "").Split('/', '\\').LastOrDefault(x => x.Length > 0) ?? "";
        var sb = new StringBuilder();
        foreach (var r in last.EnumerateRunes()) sb.Append(Rune.IsControl(r) || r.Value < 128 && ":<>\"|?*".Contains((char)r.Value) ? "_" : r.ToString());
        var n = sb.ToString().TrimStart('.');
        while (Encoding.UTF8.GetByteCount(n) > 200) n = n[..^(char.IsLowSurrogate(n[^1]) ? 2 : 1)];
        n = n.TrimEnd('.', ' ');
        var stem = n.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsAsciiDigit(stem[3])) n = "_" + n;
        return n.Length == 0 ? "file" : n;
    }

    /// §6.3: append " (1)", " (2)", … before the extension.
    internal static string Unique(string dir, string name)
    {
        var (stem, ext) = (Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
        var p = Path.Combine(dir, name);
        for (var i = 1; File.Exists(p) || Directory.Exists(p); i++) p = Path.Combine(dir, $"{stem} ({i}){ext}");
        return p;
    }

    public void Accept(string id)
    {
        if (!incoming.TryGetValue(id, out var c) || c.Accepted) return;
        try
        {
            foreach (var f in c.Files) f.Handle = new FileStream(f.Tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            J.Log($"accept {id}: {e.Message}");
            Reject(id);
            return;
        }
        c.Accepted = true;
        _ = SendQuiet(new JsonObject { ["t"] = "accept", ["id"] = id });
    }

    public void Reject(string id)
    {
        DropIncoming(id);
        _ = SendQuiet(new JsonObject { ["t"] = "reject", ["id"] = id });
    }

    void DropIncoming(string id)
    {
        if (!incoming.Remove(id, out var c)) return;
        dropped.UnionWith(c.Files.Select(f => f.Id));
        engine.EndTransfer($"{PeerId}/{id}", false);
        engine.RemoveOffer(id);
        foreach (var f in c.Files.Where(f => f.Final is null))
        {
            f.Handle?.Dispose();
            try { File.Delete(f.Tmp); } catch (IOException) { }
        }
    }

    async Task CancelIncoming(string id, string why)
    {
        J.Log($"cancel {id}: {why}");
        DropIncoming(id);
        engine.Note(new Note.Failed($"Transfer from {Name} failed"));
        await Send(new JsonObject { ["t"] = "cancel", ["id"] = id });
    }

    async Task Chunk(Guid fid, ReadOnlyMemory<byte> data)
    {
        if (dropped.Contains(fid)) return;
        // §6.2: a chunk for a fileId of no accepted clip closes the session (except late chunks of a cancelled clip).
        var (id, c) = incoming.FirstOrDefault(kv => kv.Value.Accepted && kv.Value.Files.Any(f => f.Id == fid));
        if (c?.Files.First(f => f.Id == fid) is not { Handle: { } h } f) throw new ProtocolException("chunk for unknown file");
        if (f.Got + data.Length > f.Size) { await CancelIncoming(id, "more data than announced"); return; }
        try { h.Write(data.Span); }
        catch (IOException e) { await CancelIncoming(id, e.Message); return; }
        f.Hasher.AppendData(data.Span);
        f.Got += data.Length;
        engine.Progress($"{PeerId}/{id}", id, f.Name, PeerId, true, c.Files.Sum(x => x.Got), c.Files.Sum(x => x.Size));
    }

    async Task Done(JsonObject m)
    {
        if (m.S("id") is not { } id || !incoming.TryGetValue(id, out var c) || !c.Accepted || !Guid.TryParse(m.S("fileId"), out var fid)
            || c.Files.FirstOrDefault(x => x.Id == fid) is not { Final: null } f) return;
        f.Handle?.Dispose();
        f.Handle = null;
        var sha = f.Hasher.GetHashAndReset();
        if (f.Got != f.Size || Convert.ToHexStringLower(sha) != m.S("sha256")?.ToLowerInvariant()) { await CancelIncoming(id, "hash mismatch"); return; }
        try
        {
            var dir = engine.ReceivedDir;
            Directory.CreateDirectory(dir);
            var dest = Unique(dir, f.Name);
            File.Move(f.Tmp, dest);
            f.Final = dest;
            f.Sha = sha;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await CancelIncoming(id, e.Message); return; }
        if (c.Files.Any(x => x.Final is null)) return;
        incoming.Remove(id);
        engine.EndTransfer($"{PeerId}/{id}", true);
        engine.Received(new Received(c.Kind, null, c.Files.Select(x => x.Final!).ToList(), c.Files[0].Mime, c.Files[0].Sha!, Name), id);
    }
}
