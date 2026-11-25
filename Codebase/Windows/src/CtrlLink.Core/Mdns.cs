using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace CtrlLink.Core;

static class Net
{
    public sealed record Iface(IPAddress Addr, IPAddress Mask);

    /// Up, non-loopback, multicast-capable interfaces with an IPv4 address (link-local 169.254/16 skipped).
    public static List<Iface> Ifaces()
    {
        var list = new List<Iface>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback || !n.SupportsMulticast) continue;
            foreach (var u in n.GetIPProperties().UnicastAddresses)
                if (u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address) && u.Address.GetAddressBytes() is not [169, 254, ..])
                    list.Add(new(u.Address, u.IPv4Mask));
        }
        return list;
    }

    public static bool Private(IPAddress a) => a.GetAddressBytes() is [10, ..] or [192, 168, ..] or [172, >= 16 and < 32, ..];

    public static bool OnLink(Iface i, IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] x = i.Addr.GetAddressBytes(), y = a.GetAddressBytes(), m = i.Mask.GetAddressBytes();
        return Enumerable.Range(0, 4).All(k => (x[k] & m[k]) == (y[k] & m[k]));
    }

    /// For the QR `h=` list: private IPv4 first.
    public static List<string> LanAddresses() =>
        Ifaces().Select(i => i.Addr).Distinct().OrderBy(a => Private(a) ? 0 : 1).Select(a => a.ToString()).ToList();
}

/// Minimal mDNS / DNS-SD responder + browser (RFC 6762/6763) for `_ctrllink._tcp.local`, IPv4 only, no OS dependency.
/// Not done: probing before the first announcement, known-answer suppression, response delays.
/// A clash of instance names is resolved by renaming ourselves "name (2)" when another host's record shows up.
/// All members run on the engine loop.
sealed class Mdns : IDisposable
{
    public sealed record Found(string Instance, IReadOnlyDictionary<string, string> Txt, IPEndPoint Endpoint);

    internal sealed record Rr(string[] Name, int Type, uint Ttl, bool Flush = false, string[]? Target = null, int Port = 0,
        IReadOnlyDictionary<string, string>? Txt = null, IPAddress? Addr = null);

    internal sealed record Msg(int Id, bool Response, List<(string[] Name, int Type)> Questions, List<Rr> Records);

    internal const int A = 1, PTR = 12, TXT = 16, SRV = 33, ANY = 255;
    internal static readonly string[] Svc = ["_ctrllink", "_tcp", "local"];
    static readonly string[] Meta = ["_services", "_dns-sd", "_udp", "local"];
    static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);

    readonly Socket sock = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    readonly Action<Action> post;
    readonly Action changed;
    readonly string[] host;
    List<Net.Iface> ifaces = [];
    bool disposed;

    // responder
    string? baseName;
    string[]? inst;
    int port, suffix = 1;
    IReadOnlyDictionary<string, string> txt = new Dictionary<string, string>();

    // browser cache, keyed by instance label / host name
    static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    readonly Dictionary<string, DateTime> ptrs = new(Ci);
    readonly Dictionary<string, (string Host, int Port, DateTime Exp)> srvs = new(Ci);
    readonly Dictionary<string, (IReadOnlyDictionary<string, string> Txt, DateTime Exp)> txts = new(Ci);
    readonly Dictionary<string, Dictionary<IPAddress, DateTime>> addrs = new(Ci);
    DateTime nextQuery;
    double interval = 1;
    string lastSig = "";

    public IReadOnlyList<Found> Services { get; private set; } = [];

    public Mdns(string hostLabel, Action<Action> post, Action changed)
    {
        (this.post, this.changed, host) = (post, changed, [hostLabel, "local"]);
        // Windows 10/11 run their own mDNS responder on 5353 (and macOS its mDNSResponder). ReuseAddress, set before Bind,
        // lets our socket share the port with it; on macOS .NET also sets SO_REUSEPORT for this option.
        sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        sock.Bind(new IPEndPoint(IPAddress.Any, 5353));
        sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        JoinGroups();
        _ = ReceiveLoop();
    }

    void JoinGroups()
    {
        ifaces = Net.Ifaces();
        foreach (var i in ifaces)
            try { sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group.Address, i.Addr)); }
            catch (SocketException) { } // already joined
    }

    async Task ReceiveLoop()
    {
        var buf = new byte[9000];
        while (!disposed)
        {
            try
            {
                var r = await sock.ReceiveFromAsync(buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0)).ConfigureAwait(false);
                var data = buf[..r.ReceivedBytes];
                var from = (IPEndPoint)r.RemoteEndPoint;
                post(() => Handle(data, from));
            }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { } // e.g. ICMP port unreachable on Windows
        }
    }

    void Handle(byte[] b, IPEndPoint from)
    {
        if (disposed || Parse(b) is not { } m) return;
        if (m.Response) Learn(m.Records); else Answer(m, from);
    }

    // ---- responder ----

    public void Advertise(string name, int port, IReadOnlyDictionary<string, string> txt)
    {
        var label = name;
        while (Encoding.UTF8.GetByteCount(label) > 56) label = label[..^1]; // a label is ≤ 63 bytes, leave room for " (n)"
        if (label != baseName)
        {
            if (inst is not null) SendAll(bye: true);
            (baseName, suffix, inst) = (label, 1, [label, .. Svc]);
        }
        (this.port, this.txt) = (port, txt);
        Announce();
    }

    public void Refresh()
    {
        JoinGroups();
        if (inst is not null) Announce();
        (interval, nextQuery) = (1, DateTime.UtcNow);
    }

    void Announce()
    {
        SendAll(bye: false);
        post(async () =>
        {
            await Task.Delay(1000); // RFC 6762 §8.3: announce at least twice, one second apart
            if (!disposed && inst is not null) SendAll(bye: false);
        });
    }

    (Rr Ptr, Rr Srv, Rr Txt, Rr A) Records(IPAddress addr, bool legacy = false, bool bye = false)
    {
        uint T(uint d) => bye ? 0 : legacy ? 10 : d;
        return (new Rr(Svc, PTR, T(4500), Target: inst),
            new Rr(inst!, SRV, T(120), !legacy, host, port),
            new Rr(inst!, TXT, T(4500), !legacy, Txt: txt),
            new Rr(host, A, T(120), !legacy, Addr: addr));
    }

    void SendAll(bool bye)
    {
        foreach (var i in ifaces)
        {
            var (p, s, t, a) = Records(i.Addr, bye: bye);
            SendOn(i, Packet(0, [], [p, s, t, a], []));
        }
    }

    void SendOn(Net.Iface i, byte[] pkt)
    {
        try
        {
            sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, i.Addr.GetAddressBytes());
            sock.SendTo(pkt, Group);
        }
        catch (SocketException e) { J.Log($"mDNS send on {i.Addr}: {e.Message}"); }
    }

    void Answer(Msg m, IPEndPoint from)
    {
        if (inst is null || (ifaces.FirstOrDefault(i => Net.OnLink(i, from.Address)) ?? ifaces.FirstOrDefault()) is not { } iface) return;
        var legacy = from.Port != 5353; // RFC 6762 §6.7: legacy unicast query → unicast reply echoing id and question
        var (ptr, srv, txtRr, a) = Records(iface.Addr, legacy);
        List<Rr> ans = [], add = [];
        foreach (var (n, t) in m.Questions)
        {
            var any = t == ANY;
            if (Same(n, Svc) && (t == PTR || any)) { ans.Add(ptr); add.AddRange([srv, txtRr, a]); }
            else if (Same(n, Meta) && (t == PTR || any)) ans.Add(new Rr(Meta, PTR, legacy ? 10u : 4500, Target: Svc));
            else if (Same(n, inst))
            {
                if (t == SRV || any) { ans.Add(srv); add.Add(a); }
                if (t == TXT || any) ans.Add(txtRr);
            }
            else if (Same(n, host) && (t == A || any)) ans.Add(a);
        }
        if (ans.Count == 0) return;
        ans = ans.Distinct().ToList();
        var pkt = Packet(legacy ? m.Id : 0, legacy ? m.Questions : [], ans, add.Distinct().Except(ans).ToList());
        if (!legacy) SendOn(iface, pkt);
        else try { sock.SendTo(pkt, from); } catch (SocketException) { }
    }

    // ---- browser ----

    public void Tick()
    {
        var now = DateTime.UtcNow;
        Update();
        if (now < nextQuery) return;
        Query();
        interval = Math.Min(interval * 2, 60);
        nextQuery = now.AddSeconds(interval);
    }

    /// Asks for the service list plus whatever is still missing to resolve known instances.
    void Query()
    {
        List<(string[], int)> qs = [(Svc, PTR)];
        foreach (var i in ptrs.Keys)
        {
            string[] n = [i, .. Svc];
            if (!srvs.TryGetValue(i, out var s)) qs.Add((n, SRV));
            else if (!addrs.TryGetValue(s.Host, out var set) || set.Count == 0) qs.Add((s.Host.Split('.'), A));
            if (!txts.ContainsKey(i)) qs.Add((n, TXT));
        }
        var pkt = Packet(0, qs, [], []);
        foreach (var i in ifaces) SendOn(i, pkt);
    }

    void Learn(List<Rr> rrs)
    {
        var now = DateTime.UtcNow;
        var flushed = new HashSet<string>(Ci);
        var fresh = false;
        foreach (var rr in rrs)
        {
            var exp = now.AddSeconds(rr.Ttl);
            var i = rr.Name.Length == 4 && Same(rr.Name[1..], Svc) ? rr.Name[0] : null;
            switch (rr.Type)
            {
                case PTR when Same(rr.Name, Svc) && rr.Target is { Length: 4 } t && Same(t[1..], Svc):
                    if (rr.Ttl == 0) { ptrs.Remove(t[0]); srvs.Remove(t[0]); txts.Remove(t[0]); }
                    else { fresh |= !ptrs.ContainsKey(t[0]); ptrs[t[0]] = exp; }
                    break;
                case SRV when i is not null && rr.Target is not null:
                    var target = string.Join('.', rr.Target);
                    if (Clash(i, !Same(rr.Target, host))) return;
                    if (rr.Ttl == 0) srvs.Remove(i); else srvs[i] = (target, rr.Port, exp);
                    break;
                case TXT when i is not null && rr.Txt is not null:
                    if (Clash(i, rr.Txt.GetValueOrDefault("id") != txt.GetValueOrDefault("id"))) return;
                    if (rr.Ttl == 0) txts.Remove(i); else txts[i] = (rr.Txt, exp);
                    break;
                case A when rr.Addr is not null:
                    var h = string.Join('.', rr.Name);
                    if (!addrs.TryGetValue(h, out var set)) addrs[h] = set = [];
                    if (rr.Flush && flushed.Add(h)) set.Clear();
                    if (rr.Ttl == 0) set.Remove(rr.Addr); else set[rr.Addr] = exp;
                    break;
            }
        }
        if (fresh) Query();
        Update();
    }

    /// Someone else's record for our instance name: rename ourselves (no goodbye, that would delete theirs).
    bool Clash(string instance, bool foreign)
    {
        if (inst is null || !foreign || !Ci.Equals(instance, inst[0])) return false;
        suffix++;
        inst = [$"{baseName} ({suffix})", .. Svc];
        J.Log($"mDNS name clash, now advertising as {inst[0]}");
        Announce();
        return true;
    }

    void Update()
    {
        var now = DateTime.UtcNow;
        foreach (var k in ptrs.Where(x => x.Value < now).Select(x => x.Key).ToList()) ptrs.Remove(k);
        foreach (var k in srvs.Where(x => x.Value.Exp < now).Select(x => x.Key).ToList()) srvs.Remove(k);
        foreach (var k in txts.Where(x => x.Value.Exp < now).Select(x => x.Key).ToList()) txts.Remove(k);
        foreach (var set in addrs.Values)
            foreach (var k in set.Where(x => x.Value < now).Select(x => x.Key).ToList()) set.Remove(k);
        var list = new List<Found>();
        foreach (var i in ptrs.Keys)
            if (srvs.TryGetValue(i, out var s) && txts.TryGetValue(i, out var t) && addrs.TryGetValue(s.Host, out var set) && set.Count > 0)
                list.Add(new Found(i, t.Txt, new IPEndPoint(set.Keys.OrderBy(a => ifaces.Any(f => Net.OnLink(f, a)) ? 0 : Net.Private(a) ? 1 : 2).First(), s.Port)));
        var sig = string.Join("|", list.Select(f => $"{f.Instance} {f.Endpoint} {string.Join(",", f.Txt.Select(kv => kv.Key + "=" + kv.Value))}"));
        if (sig == lastSig) return;
        (lastSig, Services) = (sig, list);
        changed();
    }

    public void Dispose()
    {
        if (disposed) return;
        if (inst is not null) SendAll(bye: true);
        disposed = true;
        sock.Dispose();
    }

    // ---- wire format ----

    static bool Same(string[] a, string[]? b) => b is not null && a.Length == b.Length && a.Zip(b).All(p => Ci.Equals(p.First, p.Second));

    internal static byte[] Packet(int id, IReadOnlyCollection<(string[] Name, int Type)> qs, IReadOnlyCollection<Rr> ans, IReadOnlyCollection<Rr> add)
    {
        var w = new List<byte>();
        void U16(int v) { w.Add((byte)(v >> 8)); w.Add((byte)v); }
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); w.Add((byte)Math.Min(b.Length, 255)); w.AddRange(b.Take(255)); }
        void Name(string[] n) { foreach (var l in n) Str(l); w.Add(0); }
        U16(id);
        U16(ans.Count > 0 ? 0x8400 : 0); // response + authoritative
        U16(qs.Count); U16(ans.Count); U16(0); U16(add.Count);
        foreach (var (n, t) in qs) { Name(n); U16(t); U16(1); }
        foreach (var r in ans.Concat(add))
        {
            Name(r.Name); U16(r.Type); U16(r.Flush ? 0x8001 : 1); U16((int)(r.Ttl >> 16)); U16((int)r.Ttl);
            var at = w.Count;
            U16(0);
            switch (r.Type)
            {
                case PTR: Name(r.Target!); break;
                case SRV: U16(0); U16(0); U16(r.Port); Name(r.Target!); break;
                case TXT: foreach (var (k, v) in r.Txt!) Str($"{k}={v}"); break;
                case A: w.AddRange(r.Addr!.GetAddressBytes()); break;
            }
            var len = w.Count - at - 2;
            (w[at], w[at + 1]) = ((byte)(len >> 8), (byte)len);
        }
        return [.. w];
    }

    internal static Msg? Parse(byte[] b)
    {
        try
        {
            var p = 0;
            int U16() { var v = b[p] << 8 | b[p + 1]; p += 2; return v; }
            string[] Name()
            {
                var labels = new List<string>();
                int q = p, jumps = 0;
                var jumped = false;
                while (b[q] != 0)
                {
                    if ((b[q] & 0xC0) == 0xC0) // compression pointer
                    {
                        if (!jumped) p = q + 2;
                        if (++jumps > 16) throw new FormatException("pointer loop");
                        (jumped, q) = (true, (b[q] & 0x3F) << 8 | b[q + 1]);
                        continue;
                    }
                    if (b[q] > 63 || q + 1 + b[q] > b.Length) throw new FormatException("bad label");
                    labels.Add(Encoding.UTF8.GetString(b, q + 1, b[q]));
                    q += 1 + b[q];
                }
                if (!jumped) p = q + 1;
                return [.. labels];
            }
            int id = U16(), flags = U16(), qd = U16(), an = U16(), ns = U16(), ar = U16();
            if ((flags & 0x7800) != 0) return null; // opcode must be 0
            var qs = new List<(string[], int)>();
            for (var k = 0; k < qd; k++) { var n = Name(); qs.Add((n, U16())); U16(); }
            var rrs = new List<Rr>();
            for (var k = 0; k < an + ns + ar; k++)
            {
                var n = Name();
                int type = U16(), cls = U16();
                var ttl = (uint)U16() << 16 | (uint)U16();
                var len = U16();
                var end = p + len;
                if (end > b.Length) throw new FormatException("short record");
                var rr = new Rr(n, type, ttl, (cls & 0x8000) != 0);
                switch (type)
                {
                    case PTR: rr = rr with { Target = Name() }; break;
                    case SRV: U16(); U16(); rr = rr with { Port = U16(), Target = Name() }; break;
                    case A when len == 4: rr = rr with { Addr = new IPAddress(b.AsSpan(p, 4)) }; break;
                    case TXT:
                        var d = new Dictionary<string, string>(Ci);
                        while (p < end)
                        {
                            var s = Encoding.UTF8.GetString(b, p + 1, Math.Min(b[p], end - p - 1));
                            p += 1 + b[p];
                            var eq = s.IndexOf('=');
                            if (s.Length > 0) d.TryAdd(eq < 0 ? s : s[..eq], eq < 0 ? "" : s[(eq + 1)..]);
                        }
                        rr = rr with { Txt = d };
                        break;
                }
                p = end;
                rrs.Add(rr);
            }
            return new Msg(id, (flags & 0x8000) != 0, qs, rrs);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or FormatException or ArgumentException) { return null; }
    }
}
