using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CtrlLink.Core;

sealed record Nearby(IPEndPoint Endpoint, string Name, string Pf, bool Pairing);

/// Ctrl Link protocol v1 engine (PROTOCOL.md). Mirrors Mac/Sources/CtrlLinkCore/Engine.swift.
/// All state lives on a private single-thread loop; the UI reads immutable snapshots and gets
/// events/PropertyChanged on the SynchronizationContext captured in Start().
public sealed partial class Engine : IEngine
{
    internal readonly Loop loop = new();
    readonly EngineOptions opt;
    readonly Store store;
    readonly StoreData data;
    List<Pairing> pairings;
    SynchronizationContext? ui;
    TcpListener? listener;
    Mdns? mdns;
    volatile bool disposed;
    int netGen;
    readonly Dictionary<string, Session> sessions = [];
    readonly HashSet<string> dialing = [], refused = [], online = []; // refused: peers that answered "unknown"
    readonly Dictionary<string, DateTimeOffset> lastSeen = [];
    readonly Dictionary<string, IPEndPoint> pairedEndpoints = []; // learned while joining, used without mDNS
    readonly List<byte[]> written = [];
    readonly Dictionary<string, Live> transferMap = [];
    readonly HistoryFile history;
    Task historyWrite = Task.CompletedTask;

    /// A running transfer on the loop: the published snapshot, its speed meter and when it was last republished.
    sealed class Live(Transfer t, TransferMeter meter)
    {
        public Transfer T = t;
        public readonly TransferMeter Meter = meter;
        public DateTime Published;
    }

    // Snapshots for the UI thread.
    volatile string receivedDir;
    int? port;
    IReadOnlyList<PeerStatus> peers = [];
    IReadOnlyList<ClipRecord> clips = [];
    PairWindow? window;
    JoinState join = new JoinState.Idle();
    IReadOnlyList<Transfer> transfers = [];
    IReadOnlyList<Offer> offers = [];
    internal IReadOnlyDictionary<string, Nearby> Nearby { get; private set; } = new Dictionary<string, Nearby>();

    public Engine(EngineOptions options)
    {
        opt = options;
        receivedDir = options.ReceivedDir;
        store = new Store(options.StorePath, options.Protector);
        (data, pairings) = store.Load(options.DefaultDeviceName ?? Environment.MachineName);
        history = new HistoryFile(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.StorePath))!, "history.json"));
        clips = history.Load();
        PeersChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<Received>? Receive;
    public event Action<Note>? Notify;

    public string DeviceId => data.DeviceId;
    public string DeviceName
    {
        get => data.DeviceName;
        set { data.DeviceName = value; loop.Run(() => { Save(); UpdateService(); Changed(nameof(DeviceName)); }); }
    }
    public bool AutoSend
    {
        get => data.AutoSend;
        set { data.AutoSend = value; loop.Run(() => { Save(); Changed(nameof(AutoSend)); }); }
    }
    public long AutoAcceptLimit
    {
        get => data.AutoAcceptLimit;
        set { data.AutoAcceptLimit = value; loop.Run(() => { Save(); Changed(nameof(AutoAcceptLimit)); }); }
    }
    /// Applies to files that finish arriving after the change. Not persisted: the app passes it in EngineOptions.
    public string ReceivedDir
    {
        get => receivedDir;
        set { receivedDir = value; Changed(nameof(ReceivedDir)); }
    }
    public int? Port => port;
    public IReadOnlyList<PeerStatus> Peers => peers;
    public IReadOnlyList<ClipRecord> Clips => clips;
    public PairWindow? PairingWindow => window;
    public JoinState Join => join;
    public IReadOnlyList<Transfer> Transfers => transfers;
    public IReadOnlyList<Offer> Offers => offers;

    // ---- UI plumbing ----

    void Ui(Action a)
    {
        if (ui is null) a(); else ui.Post(_ => a(), null);
    }

    void Changed(string p) => Ui(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p)));

    internal void Note(Note n) => Ui(() => Notify?.Invoke(n));

    void Save() => store.Save(data, pairings);

    void PeersChanged()
    {
        peers = pairings.Select(p => new PeerStatus(p.Id, p.Name, p.Pf, online.Contains(p.Id), lastSeen.TryGetValue(p.Id, out var d) ? d : null,
            NetInfo.ShownIp(sessions.GetValueOrDefault(p.Id)?.Remote, Nearby.GetValueOrDefault(p.Id)?.Endpoint.Address))).ToList();
        Changed(nameof(Peers));
    }

    internal void SetJoin(JoinState s) { join = s; Changed(nameof(Join)); }

    void SetWindow(PairWindow? w) { window = w; Changed(nameof(PairingWindow)); }

    // ---- lifecycle ----

    public void Start()
    {
        ui = SynchronizationContext.Current;
        loop.Run(() =>
        {
            StartListener(opt.Port);
            if (!opt.Advertise) return;
            try { mdns = new Mdns("ctrllink-" + DeviceId[..8], loop.Run, Browsed); }
            catch (Exception e) { J.Log("mDNS unavailable: " + e.Message); }
            UpdateService();
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            loop.Go(async () => { while (!disposed) { mdns?.Tick(); await Task.Delay(1000); } });
        });
        // Speed samples once a second, here and not in the view, so switching tabs or closing the flyout leaves no gap.
        loop.Go(async () => { while (!disposed) { await Task.Delay(1000); SampleSpeeds(DateTime.UtcNow); } });
    }

    void OnNetworkChanged(object? sender, EventArgs e) => loop.Go(async () =>
    {
        var g = ++netGen; // debounce: one change fires several events
        await Task.Delay(1000);
        if (g != netGen || disposed) return;
        J.Log("network changed");
        mdns?.Refresh();
        if (listener is null) StartListener(0);
        foreach (var s in sessions.Values) s.Probe();
    });

    void StartListener(int wanted)
    {
        try
        {
            var l = Socket.OSSupportsIPv6 ? new TcpListener(IPAddress.IPv6Any, wanted) : new TcpListener(IPAddress.Any, wanted);
            if (Socket.OSSupportsIPv6) l.Server.DualMode = true;
            l.Start();
            listener = l;
        }
        catch (SocketException e)
        {
            J.Log($"listen on {wanted}: {e.Message}");
            if (wanted != 0) StartListener(0); // §2: 47823 busy → any free port
            return;
        }
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Changed(nameof(Port));
        var l2 = listener;
        loop.Go(async () =>
        {
            while (!disposed)
            {
                var c = await l2.AcceptTcpClientAsync();
                c.NoDelay = true;
                loop.Go(() => Accept(c));
            }
        });
    }

    void UpdateService()
    {
        if (mdns is null || port is not int p) return;
        mdns.Advertise(DeviceName, p, new Dictionary<string, string>
        {
            ["v"] = "1", ["id"] = DeviceId, ["pf"] = opt.Platform, ["pair"] = hostWindow is null ? "0" : "1",
        });
    }

    void Browsed()
    {
        var found = new Dictionary<string, Nearby>();
        foreach (var f in mdns!.Services)
            if (f.Txt.GetValueOrDefault("id") is { } id && id != DeviceId)
                found[id] = new Nearby(f.Endpoint, f.Instance, Platform(f.Txt.GetValueOrDefault("pf")), f.Txt.GetValueOrDefault("pair") == "1");
        Nearby = found;
        PeersChanged();   // addresses for the Devices graph
        foreach (var id in found.Keys) ConsiderDial(id);
    }

    public void Dispose()
    {
        if (disposed) return;
        using var done = new ManualResetEventSlim();
        loop.Run(() =>
        {
            disposed = true;
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            CloseWindow();
            joinConn?.Dispose();
            listener?.Stop();
            foreach (var s in sessions.Values.ToList()) s.Close();
            mdns?.Dispose(); // sends goodbyes
            done.Set();
        });
        done.Wait(3000);
        historyWrite.Wait(3000);
        loop.Dispose();
    }

    // ---- connections ----

    async Task Accept(TcpClient c)
    {
        var fc = new FrameConn(c);
        using var wd = fc.Watchdog(100); // covers the 60 s pairing accept wait
        try
        {
            var m = await fc.ReadJson();
            if ((m.L("v") ?? 1) > 1)
            {
                await fc.WriteJson(new JsonObject { ["t"] = "error", ["reason"] = "version" });
                fc.Dispose();
                return;
            }
            switch (m.S("t"))
            {
                case "pair_req": await HostPair(fc, m); break;
                case "hello": await ServerHandshake(fc, m); break;
                default: fc.Dispose(); break;
            }
        }
        catch (Exception e)
        {
            J.Log("incoming connection: " + e.Message);
            fc.Dispose();
        }
    }

    IPEndPoint? EndpointFor(string id) => Nearby.TryGetValue(id, out var n) ? n.Endpoint : pairedEndpoints.GetValueOrDefault(id);

    /// §5.1 tie-break: lower deviceId dials now, the higher one after RetryDelaySeconds. `now`: right after joining (§4.2).
    void ConsiderDial(string id, bool now = false)
    {
        if (disposed || !pairings.Any(p => p.Id == id) || sessions.ContainsKey(id) || dialing.Contains(id) || refused.Contains(id) || EndpointFor(id) is null) return;
        dialing.Add(id);
        var delay = now || string.CompareOrdinal(DeviceId, id) < 0 ? 0 : opt.RetryDelaySeconds;
        loop.Go(async () =>
        {
            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay));
            if (disposed || sessions.ContainsKey(id) || EndpointFor(id) is not { } ep) { dialing.Remove(id); return; }
            try
            {
                await Dial(id, ep);
                dialing.Remove(id);
            }
            catch (Exception e)
            {
                J.Log($"dial {id}: {e.Message}");
                if (!Nearby.ContainsKey(id)) pairedEndpoints.Remove(id);
                else await Task.Delay(TimeSpan.FromSeconds(opt.RetryDelaySeconds));
                dialing.Remove(id);
                ConsiderDial(id);
            }
        });
    }

    async Task Dial(string id, IPEndPoint ep)
    {
        if (pairings.FirstOrDefault(x => x.Id == id) is not { } p) return;
        var fc = await FrameConn.Connect(ep);
        using var wd = fc.Watchdog(20);
        try
        {
            var cn = PCrypto.Random(32);
            await fc.WriteJson(new JsonObject { ["t"] = "hello", ["v"] = 1, ["id"] = DeviceId, ["nonce"] = Convert.ToBase64String(cn) });
            var r = await fc.ReadJson();
            if (r.S("t") == "unknown")
            {
                refused.Add(id);
                Note(new Note.Unknown(p.Name));
                throw new ProtocolException("peer does not know us");
            }
            if ((r.L("v") ?? 1) > 1 || r.S("t") == "error")
            {
                Note(new Note.Version(p.Name));
                throw new ProtocolException("version");
            }
            if (r.S("t") != "hello" || r.S("id") != id || r.B64("nonce", 32) is not { } sn) throw new ProtocolException("bad hello");
            var (c2s, s2c) = PCrypto.SessionKeys(p.Key, cn, sn);
            fc.SetKeys(c2s, 1, s2c, 2);
            await Establish(fc, id, true);
        }
        catch
        {
            fc.Dispose();
            throw;
        }
    }

    async Task ServerHandshake(FrameConn fc, JsonObject m)
    {
        if (m.S("id") is not { } id || pairings.FirstOrDefault(x => x.Id == id) is not { } p)
        {
            await fc.WriteJson(new JsonObject { ["t"] = "unknown" });
            fc.Dispose();
            return;
        }
        var cn = m.B64("nonce", 32) ?? throw new ProtocolException("bad hello");
        var sn = PCrypto.Random(32);
        await fc.WriteJson(new JsonObject { ["t"] = "hello", ["v"] = 1, ["id"] = DeviceId, ["nonce"] = Convert.ToBase64String(sn) });
        var (c2s, s2c) = PCrypto.SessionKeys(p.Key, cn, sn);
        fc.SetKeys(s2c, 2, c2s, 1);
        await Establish(fc, id, false);
    }

    /// Exchanges `ready` (proves both hold pairKey), then registers the session.
    async Task Establish(FrameConn fc, string id, bool isClient)
    {
        await fc.WriteSealed(J.Control(new JsonObject { ["t"] = "ready", ["name"] = DeviceName, ["pf"] = opt.Platform, ["app"] = IEngine.AppVersion }));
        var first = await fc.ReadSealed();
        if (first[0] != 1 || J.Parse(first.AsSpan(1)) is not { } r || r.S("t") != "ready") throw new ProtocolException("expected ready");
        var i = pairings.FindIndex(x => x.Id == id);
        if (i < 0) throw new ProtocolException("unpaired meanwhile");
        if (r.S("name") is { } n && r.S("pf") is { } pf && (pairings[i].Name != n || pairings[i].Pf != Platform(pf)))
        {
            pairings[i] = pairings[i] with { Name = n, Pf = Platform(pf) };
            Save();
        }
        var s = new Session(this, fc, id, isClient);
        if (sessions.TryGetValue(id, out var old))
        {
            // §5.1: keep the session whose client has the lower deviceId (same client → the newer one).
            string Client(Session x) => x.IsClient ? DeviceId : id;
            if (string.CompareOrdinal(Client(s), Client(old)) > 0) { fc.Dispose(); return; }
            sessions.Remove(id); // replaced, not a disconnect
            old.Close();
        }
        var wasOnline = !online.Add(id);
        sessions[id] = s;
        lastSeen[id] = DateTimeOffset.Now;
        PeersChanged();
        if (!wasOnline) Note(new Note.Connected(pairings[i].Name));
        J.Log($"session with {pairings[i].Name} ({(isClient ? "client" : "server")})");
        loop.Go(s.Run);
    }

    internal void SessionEnded(Session s)
    {
        if (!sessions.TryGetValue(s.PeerId, out var cur) || cur != s) return;
        sessions.Remove(s.PeerId);
        online.Remove(s.PeerId);
        lastSeen[s.PeerId] = DateTimeOffset.Now;
        PeersChanged();
        if (DateTimeOffset.Now - s.Started > TimeSpan.FromSeconds(10)) Note(new Note.Disconnected(NameOf(s.PeerId)));
        loop.Go(async () => { await Task.Delay(1000); ConsiderDial(s.PeerId); });
    }

    internal string NameOf(string id) => pairings.FirstOrDefault(p => p.Id == id)?.Name ?? id;

    internal void Touched(string id) => lastSeen[id] = DateTimeOffset.Now;

    // ---- pairings ----

    void AddPairing(Pairing p)
    {
        pairings.RemoveAll(x => x.Id == p.Id);
        pairings.Add(p);
        refused.Remove(p.Id);
        Save();
        PeersChanged();
    }

    public void Unpair(string peerId) => loop.Run(() =>
    {
        if (sessions.TryGetValue(peerId, out var s))
            loop.Go(async () =>
            {
                try { await s.Send(new JsonObject { ["t"] = "unpair" }); } finally { s.Close(); }
            });
        RemovePairing(peerId);
    });

    internal void RemovePairing(string id)
    {
        pairings.RemoveAll(x => x.Id == id);
        pairedEndpoints.Remove(id);
        Save();
        PeersChanged();
    }

    // ---- clips ----

    public void MarkWritten(byte[] fingerprint) => loop.Run(() =>
    {
        written.Add(fingerprint);
        if (written.Count > 20) written.RemoveAt(0);
    });

    bool IsWritten(byte[] fp) => written.Any(w => w.AsSpan().SequenceEqual(fp));

    public void Send(Outgoing clip) => loop.Go(async () =>
    {
        var id = Guid.NewGuid().ToString();
        var m = new JsonObject { ["t"] = "clip", ["id"] = id, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        List<string> paths = [];
        string? text = null;
        ClipKind kind;
        switch (clip)
        {
            case Outgoing.TextClip or Outgoing.UrlClip:
                var s = clip is Outgoing.TextClip t ? t.Text : ((Outgoing.UrlClip)clip).Url;
                if (IsWritten(SHA256.HashData(Encoding.UTF8.GetBytes(s)))) return; // §6.4: we wrote it ourselves
                kind = clip is Outgoing.UrlClip ? ClipKind.Url : ClipKind.Text;
                m["kind"] = KindName(kind);
                m["text"] = s;
                // §6.1: inline text max 192 KiB UTF-8 (and one frame), else one .txt file.
                if (Encoding.UTF8.GetByteCount(s) > 192 * 1024 || J.Bytes(m).Length + 1 + 16 > FrameConn.MaxFrame)
                {
                    var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CtrlLink-" + id)).FullName;
                    var p = Path.Combine(dir, "Clipboard.txt");
                    await File.WriteAllTextAsync(p, s);
                    m.Remove("text");
                    kind = ClipKind.Files;
                    m["kind"] = "files";
                    paths = [p];
                }
                else text = s;
                break;
            case Outgoing.ImageClip i: kind = ClipKind.Image; m["kind"] = "image"; paths = [i.PngPath]; break;
            case Outgoing.FilesClip f: kind = ClipKind.Files; m["kind"] = "files"; paths = [.. f.Paths]; break;
            default: return;
        }
        var files = paths.Where(File.Exists).Select(p => new OutFile(Guid.NewGuid(), p, new FileInfo(p).Length)).ToList();
        if (paths.Count > 0)
        {
            if (files.Count == 0) return;
            if (written.Count > 0 && IsWritten(await Task.Run(() => HashFile(files[0].Path)))) return;
            m["files"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject
            {
                ["fileId"] = f.Id.ToString(), ["name"] = Path.GetFileName(f.Path), ["mime"] = Mime(f.Path), ["size"] = f.Size,
            }).ToArray());
        }
        Record(new ClipRecord(id, kind, null, DateTimeOffset.Now, text, files.Select(f => f.Path).ToList()));
        foreach (var s in sessions.Values) s.Offer(m, files);
    });

    static byte[] HashFile(string path)
    {
        using var f = File.OpenRead(path);
        return SHA256.HashData(f);
    }

    static readonly Dictionary<string, string> Mimes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".heic"] = "image/heic",
        [".webp"] = "image/webp", [".bmp"] = "image/bmp", [".txt"] = "text/plain", [".pdf"] = "application/pdf",
        [".zip"] = "application/zip", [".mp4"] = "video/mp4", [".mov"] = "video/quicktime", [".mp3"] = "audio/mpeg",
    };

    static string Mime(string path) => Mimes.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");

    internal static string KindName(ClipKind k) => k.ToString().ToLowerInvariant();

    static string DayKey(DateOnly d) => d.ToString("yyyy-MM-dd");

    void Record(ClipRecord c)
    {
        // Counts first, then the list: anyone who sees the new clip in Clips also sees it in CountsOn.
        // Copy on write: CountsOn reads it from the UI thread.
        var d = new Dictionary<string, Dictionary<string, int>>(data.Daily ?? []);
        var key = DayKey(DateOnly.FromDateTime(c.Date.LocalDateTime));
        var day = new Dictionary<string, int>(d.GetValueOrDefault(key) ?? []);
        day[KindName(c.Kind)] = day.GetValueOrDefault(KindName(c.Kind)) + 1;
        d[key] = day;
        var cutoff = DayKey(DateOnly.FromDateTime(DateTime.Now.AddDays(-14)));
        foreach (var k in d.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList()) d.Remove(k);
        data.Daily = d;
        Save();
        clips = HistoryFile.Trim([HistoryFile.Capped(c), .. clips], DateTimeOffset.Now);
        Changed(nameof(Clips));
        SaveHistory();
    }

    int historyQueued;

    /// Writes the list off the loop, one write at a time. A burst of copies queues a single write of the newest list.
    void SaveHistory()
    {
        if (Interlocked.Exchange(ref historyQueued, 1) == 1) return;
        historyWrite = historyWrite.ContinueWith(_ =>
        {
            Interlocked.Exchange(ref historyQueued, 0);
            history.Save(clips);
        }, TaskScheduler.Default);
    }

    public void ClearHistory() => loop.Run(() =>
    {
        // Our own temporary copies (clipboard images in %TEMP%\CtrlLink, long text in %TEMP%\CtrlLink-<id>) go, unless a
        // peer hasn't fetched them yet (offer waiting for Accept, or mid-transfer). Received files are never touched.
        var tmp = Path.GetFullPath(Path.GetTempPath());
        var pending = sessions.Values.SelectMany(s => s.PendingPaths).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in clips.Where(c => c.From is null).SelectMany(c => c.Files).Select(Path.GetFullPath))
        {
            if (pending.Contains(p) || OwnTemp(tmp, p) is not { } target) continue;
            try
            {
                if (target == p) File.Delete(p);
                else Directory.Delete(target, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { J.Log("clear history: " + e.Message); }
        }
        clips = [];
        Changed(nameof(Clips));
        historyWrite = historyWrite.ContinueWith(_ => history.Delete(), TaskScheduler.Default);
        data.Daily = null;
        Save();
    });

    /// What to delete for a temp copy we made: the file itself in %TEMP%\CtrlLink\, or the whole %TEMP%\CtrlLink-<id>\
    /// folder the engine made for long text. Anything else (files the user copied, received files) → null.
    internal static string? OwnTemp(string tmp, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is null || !string.Equals(Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)) + Path.DirectorySeparatorChar,
                tmp.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        var name = Path.GetFileName(dir);
        if (name.Equals("Ctrl Link", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path).StartsWith("clip-", StringComparison.OrdinalIgnoreCase)) return path;
        return name.StartsWith("CtrlLink-", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(name["CtrlLink-".Length..], out _) ? dir : null;
    }

    public IReadOnlyDictionary<ClipKind, int> CountsOn(DateOnly day)
    {
        var out_ = new Dictionary<ClipKind, int>();
        foreach (var (k, n) in data.Daily?.GetValueOrDefault(DayKey(day)) ?? [])
            if (Enum.TryParse<ClipKind>(k, true, out var kind)) out_[kind] = n;
        return out_;
    }

    internal void Received(Received r, string id)
    {
        var c = new ClipRecord(id, r.Kind, r.FromName, DateTimeOffset.Now, r.Text, r.Files);
        Record(c);
        Ui(() => Receive?.Invoke(r));
        Note(new Note.Clip(r.FromName, r.Kind, c.Preview));
    }

    internal void AddOffer(Offer o)
    {
        offers = [.. offers, o];
        Changed(nameof(Offers));
        Note(new Note.OfferNote(o));
    }

    internal void RemoveOffer(string id)
    {
        if (!offers.Any(o => o.Id == id)) return;
        offers = offers.Where(o => o.Id != id).ToList();
        Changed(nameof(Offers));
    }

    public void AcceptOffer(string clipId) => loop.Run(() =>
    {
        if (offers.FirstOrDefault(o => o.Id == clipId) is not { } o) return;
        RemoveOffer(clipId);
        sessions.GetValueOrDefault(o.PeerId)?.Accept(clipId);
    });

    public void RejectOffer(string clipId) => loop.Run(() =>
    {
        if (offers.FirstOrDefault(o => o.Id == clipId) is not { } o) return;
        RemoveOffer(clipId);
        sessions.GetValueOrDefault(o.PeerId)?.Reject(clipId);
    });

    /// §6.2 cancel: stops our side at once (partial files deleted), tells the peer, and drops the transfer from the list.
    public void CancelTransfer(string clipId) => loop.Run(() =>
    {
        foreach (var s in sessions.Values) s.Cancel(clipId);
        foreach (var k in transferMap.Keys.Where(k => k.EndsWith("/" + clipId, StringComparison.Ordinal)).ToList()) EndTransfer(k, false);
    });

    /// key = "peerId/clipId"; Transfer.Id is the clip id the app passes to CancelTransfer.
    internal void Progress(string key, string clipId, string name, string peer, bool incoming, long done, long total)
    {
        var now = DateTime.UtcNow;
        if (!transferMap.TryGetValue(key, out var l))
            transferMap[key] = l = new Live(new Transfer(clipId, name, NameOf(peer), incoming, done, total, DateTimeOffset.Now, [new(0, 0)]), new TransferMeter(now));
        else if (l.T.Finished) return;
        else l.T = l.T with { Name = name, Done = done };
        if (now - l.Published < TimeSpan.FromMilliseconds(150)) return; // chunks arrive hundreds of times a second: throttle UI updates
        Publish(l, now);
    }

    void Publish(Live l, DateTime now)
    {
        l.Published = now;
        transfers = transferMap.Values.Select(x => x.T).ToList();
        Changed(nameof(Transfers));
    }

    internal void SampleSpeeds(DateTime now)
    {
        foreach (var l in transferMap.Values.Where(l => !l.T.Finished))
            if (l.Meter.Sample(now, l.T.Done, l.T.Total))
            {
                l.T = l.T with { Speeds = [.. l.Meter.Speeds], Rate = l.Meter.Rate };
                Publish(l, now);
            }
    }

    /// The transfer stopped. ok: shown at 100 % (Finished) for 1.2 s, then removed; otherwise removed at once.
    internal void EndTransfer(string key, bool ok)
    {
        if (!transferMap.TryGetValue(key, out var l) || l.T.Finished) return;
        if (!ok)
        {
            transferMap.Remove(key);
            Publish(l, DateTime.UtcNow);
            return;
        }
        var last = l.Meter.Speeds[^1].BytesPerSecond;
        l.T = l.T with { Done = l.T.Total, Finished = true, Speeds = [.. l.Meter.Speeds, new(1, last)], Rate = l.Meter.Rate };
        Publish(l, DateTime.UtcNow);
        loop.Go(async () =>
        {
            await Task.Delay(1200);
            if (transferMap.GetValueOrDefault(key) != l) return;
            transferMap.Remove(key);
            Publish(l, DateTime.UtcNow);
        });
    }

    /// §1: unknown platform values count as `other`.
    internal static string Platform(string? s) => s is "mac" or "android" or "windows" ? s : "other";
}
