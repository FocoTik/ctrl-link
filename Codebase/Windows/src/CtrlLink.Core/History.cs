using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CtrlLink.Core;

/// History list on disk (history.json next to state.json, so the pairing keys aren't rewritten on every copy).
/// Same limits as the Mac and Android apps: 14 days, at most 300 clips, text capped at 32 KB (UTF-8) per clip.
sealed class HistoryFile(string path)
{
    public const int MaxItems = 300, TextCap = 32 * 1024;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);
    static readonly JsonSerializerOptions Opt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    readonly Lock gate = new();

    public string Path => path;

    public List<ClipRecord> Load()
    {
        try
        {
            if (!File.Exists(path)) return [];
            var list = JsonSerializer.Deserialize<List<ClipRecord>>(File.ReadAllBytes(path), Opt) ?? [];
            return Trim(list.Where(c => c is { Id: not null, Files: not null }).Select(Capped), DateTimeOffset.Now);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            J.Log("history unreadable: " + e.Message);
            return [];
        }
    }

    /// Writes a temp file and renames it over the old one, so a crash never leaves half a file. Thread-safe.
    public void Save(IReadOnlyList<ClipRecord> clips)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
                File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(clips, Opt));
                File.Move(path + ".tmp", path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { J.Log("cannot save history: " + e.Message); }
        }
    }

    public void Delete()
    {
        lock (gate)
        {
            try { File.Delete(path); File.Delete(path + ".tmp"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { J.Log("cannot delete history: " + e.Message); }
        }
    }

    /// Newest first, nothing older than 14 days, at most 300.
    public static List<ClipRecord> Trim(IEnumerable<ClipRecord> clips, DateTimeOffset now) =>
        [.. clips.Where(c => now - c.Date <= MaxAge).OrderByDescending(c => c.Date).Take(MaxItems)];

    public static ClipRecord Capped(ClipRecord c) => c.Text is { } t && Cap(t) is var k && k.Length != t.Length ? c with { Text = k } : c;

    /// The longest prefix of `t` that fits in 32 KB of UTF-8, never splitting a surrogate pair.
    public static string Cap(string t)
    {
        if (t.Length <= TextCap / 4 || Encoding.UTF8.GetByteCount(t) <= TextCap) return t;
        var n = Math.Min(t.Length, TextCap);
        while (Encoding.UTF8.GetByteCount(t.AsSpan(0, n)) > TextCap) n -= Math.Max(1, n / 16);
        if (n > 0 && char.IsHighSurrogate(t[n - 1])) n--;
        return t[..n];
    }
}

/// Speed samples for one transfer, taken once a second by the engine's ticker (not by the view), like Android's
/// TransferTracker. Not thread-safe: the engine loop owns it.
sealed class TransferMeter(DateTime start)
{
    public const int MaxSamples = 600;
    long lastDone;
    DateTime lastAt = start;
    public readonly List<SpeedSample> Speeds = [new(0, 0)];
    public double Rate;   // smoothed bytes/s, 0 until the first sample

    /// Adds a sample when at least a second has passed since the last one. Returns whether it did.
    public bool Sample(DateTime now, long done, long total)
    {
        var dt = (now - lastAt).TotalSeconds;
        if (dt < 0.95) return false;
        var bps = Math.Max(0, done - lastDone) / dt;
        lastDone = done;
        lastAt = now;
        Rate = Rate == 0 ? bps : 0.5 * bps + 0.5 * Rate;   // smooth the time-left estimate
        Speeds.Add(new SpeedSample(total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0, bps));
        if (Speeds.Count > MaxSamples) Speeds.RemoveRange(0, Speeds.Count - MaxSamples);
        return true;
    }
}

public static class NetInfo
{
    /// The address to show for a peer: an IPv4 if there is one, else a routable IPv6 without its %scope. Never loopback,
    /// link-local (fe80::, 169.254.x) or unspecified, which mean nothing to a person; null then, and the UI shows the platform.
    public static string? ShownIp(params IPAddress?[] candidates)
    {
        var ok = candidates.OfType<IPAddress>().Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Where(Showable).ToList();
        if (ok.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) is { } v4) return v4.ToString();
        return ok.FirstOrDefault() is { } v6 ? new IPAddress(v6.GetAddressBytes()).ToString().ToLowerInvariant() : null;
    }

    static bool Showable(IPAddress a)
    {
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.IPv6None)) return false;
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return !a.IsIPv6LinkLocal && !a.IsIPv6Multicast;
        var b = a.GetAddressBytes();
        return a.AddressFamily == AddressFamily.InterNetwork && !(b[0] == 169 && b[1] == 254) && b[0] < 224;
    }

    static (DateTime At, string? Ip) cached;

    /// This PC's LAN IPv4 for the graph: an up, non-loopback interface with a gateway first, private ranges first. Cached 5 s.
    public static string? LocalIPv4()
    {
        var c = cached;
        if (DateTime.UtcNow - c.At < TimeSpan.FromSeconds(5)) return c.Ip;
        string? ip = null;
        try
        {
            var found = new List<(IPAddress Addr, bool Gateway)>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = ni.GetIPProperties();
                var gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var u in props.UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork && Showable(u.Address)) found.Add((u.Address, gw));
            }
            ip = found.OrderByDescending(f => f.Gateway).ThenByDescending(f => IsPrivate(f.Addr)).Select(f => f.Addr.ToString()).FirstOrDefault();
        }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException) { }
        cached = (DateTime.UtcNow, ip);
        return ip;
    }

    static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31;
    }
}
