using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CtrlLink.Core;

sealed class ProtocolException(string message) : Exception(message);

/// Wraps a TCP connect failure, so callers can try the next address.
sealed class ConnectException(Exception inner) : Exception(inner.Message, inner);

static class J
{
    static readonly JsonSerializerOptions Opt = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static byte[] Bytes(JsonObject o) => JsonSerializer.SerializeToUtf8Bytes(o, Opt);

    public static JsonObject? Parse(ReadOnlySpan<byte> b)
    {
        try { return JsonNode.Parse(b) as JsonObject; } catch (JsonException) { return null; }
    }

    public static string? S(this JsonObject m, string k) => m[k] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public static long? L(this JsonObject m, string k) => m[k] is JsonValue v && v.TryGetValue(out long n) ? n : null;

    public static byte[]? B64(this JsonObject m, string k, int len = -1)
    {
        try { return Convert.FromBase64String(m.S(k) ?? "") is var b && (len < 0 || b.Length == len) ? b : null; } catch (FormatException) { return null; }
    }

    /// Plaintext of a kind 0x01 frame (§3).
    public static byte[] Control(JsonObject m) => [1, .. Bytes(m)];

    public static void Log(string s)
    {
        System.Diagnostics.Debug.WriteLine("Ctrl Link: " + s);
        if (Environment.GetEnvironmentVariable("CTRLLINK_LOG") is not null) Console.Error.WriteLine("Ctrl Link: " + s);
    }
}

/// Single-thread event loop: all engine state is touched only here, like the Mac's MainActor.
sealed class Loop : SynchronizationContext, IDisposable
{
    readonly BlockingCollection<(SendOrPostCallback F, object? S)> q = [];

    public Loop()
    {
        new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (f, s) in q.GetConsumingEnumerable())
                try { f(s); } catch (Exception e) { J.Log("loop: " + e); }
        }) { IsBackground = true, Name = "CtrlLink.Core" }.Start();
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        try { q.Add((d, state)); } catch (InvalidOperationException) { } // disposed
    }

    public override SynchronizationContext CreateCopy() => this;

    public void Run(Action a) => Post(_ => a(), null);

    public void Go(Func<Task> f) => Post(async _ =>
    {
        try { await f(); } catch (Exception e) { J.Log("task: " + e.Message); }
    }, null);

    public void Dispose() => q.CompleteAdding();
}

/// One TCP connection carrying length-prefixed frames (§3): JSON before the session, AES-GCM after.
sealed class FrameConn(TcpClient tcp) : IDisposable
{
    public const int MaxFrame = 262_144 + 64;
    readonly NetworkStream s = tcp.GetStream();
    readonly SemaphoreSlim writing = new(1, 1);
    byte[]? sendKey, recvKey;
    uint sendDir, recvDir;
    ulong sendCtr, recvCtr;

    public static async Task<FrameConn> Connect(IPEndPoint ep, double seconds = 5)
    {
        var c = new TcpClient(ep.AddressFamily) { NoDelay = true };
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            await c.ConnectAsync(ep, cts.Token);
            return new FrameConn(c);
        }
        catch (Exception e) { c.Dispose(); throw new ConnectException(e); }
    }

    public void SetKeys(byte[] send, uint sendDir, byte[] recv, uint recvDir)
    {
        (sendKey, this.sendDir, recvKey, this.recvDir) = (send, sendDir, recv, recvDir);
    }

    /// Closes the connection after `seconds` unless disposed first.
    public IDisposable Watchdog(double seconds) => new Timer(_ => Dispose(), null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);

    async Task<byte[]> Read(int n)
    {
        var b = new byte[n];
        await s.ReadExactlyAsync(b);
        return b;
    }

    public async Task<byte[]> ReadFrame()
    {
        var len = BinaryPrimitives.ReadUInt32BigEndian(await Read(4));
        if (len > MaxFrame) throw new ProtocolException("oversize frame");
        return len == 0 ? [] : await Read((int)len);
    }

    async Task Write(Func<byte[]> body)
    {
        await writing.WaitAsync();
        try
        {
            var b = body(); // sealed inside the lock, so counter order equals wire order
            var f = new byte[4 + b.Length];
            BinaryPrimitives.WriteUInt32BigEndian(f, (uint)b.Length);
            b.CopyTo(f, 4);
            await s.WriteAsync(f);
        }
        finally { writing.Release(); }
    }

    public Task WriteJson(JsonObject o) => Write(() => J.Bytes(o));

    public async Task<JsonObject> ReadJson() => J.Parse(await ReadFrame()) ?? throw new ProtocolException("bad json");

    public Task WriteSealed(byte[] plain)
    {
        var k = sendKey ?? throw new ProtocolException("no session");
        return Write(() => PCrypto.Seal(k, sendDir, sendCtr++, plain));
    }

    public async Task<byte[]> ReadSealed()
    {
        var pt = PCrypto.Open(recvKey ?? throw new ProtocolException("no session"), recvDir, recvCtr++, await ReadFrame());
        return pt.Length > 0 ? pt : throw new ProtocolException("empty frame");
    }

    /// The peer's address, for the Devices graph (null once the socket is gone).
    public IPAddress? Remote
    {
        get { try { return (tcp.Client?.RemoteEndPoint as IPEndPoint)?.Address; } catch (Exception e) when (e is ObjectDisposedException or SocketException) { return null; } }
    }

    public void Dispose() => tcp.Dispose();
}
