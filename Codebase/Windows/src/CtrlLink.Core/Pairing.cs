using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CtrlLink.Core;

sealed class HostWindow
{
    public readonly byte[] Priv = PCrypto.Random(32), Token = PCrypto.Random(16);
    public readonly byte[] Pub;
    public readonly string Code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
    public int Failures;
    public bool Busy;
    public TaskCompletionSource<bool?>? Decision;
    public HostWindow() => Pub = PCrypto.PublicKey(Priv);
}

/// §4 pairing, host and joiner sides. Mirrors Mac's Pairing.swift.
public sealed partial class Engine
{
    HostWindow? hostWindow;
    FrameConn? joinConn;
    int joinGen; // bumped by ConfirmJoin(false)/ResetJoin so an aborted attempt doesn't report a failure

    // ---- host ----

    public void OpenPairingWindow() => loop.Run(() =>
    {
        CloseWindow();
        var w = hostWindow = new HostWindow();
        SetWindow(new PairWindow(w.Code, PairingUri(w), DateTimeOffset.Now.AddMinutes(5), null));
        UpdateService();
        loop.Go(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(5));
            if (hostWindow == w) CloseWindow();
        });
    });

    public void ClosePairingWindow() => loop.Run(CloseWindow);

    void CloseWindow()
    {
        if (hostWindow is not { } w) return;
        w.Decision?.TrySetResult(null);
        hostWindow = null;
        SetWindow(null);
        UpdateService();
    }

    /// The user's answer to "<name> wants to pair — 1234" (code mode).
    public void AnswerPairRequest(bool accept) => loop.Run(() => hostWindow?.Decision?.TrySetResult(accept));

    string PairingUri(HostWindow w) =>
        $"ctrllink://pair?v=1&id={DeviceId}&n={Uri.EscapeDataString(DeviceName)}&pf={opt.Platform}" +
        $"&h={string.Join(",", Net.LanAddresses())}&p={port ?? opt.Port}&k={PCrypto.B64Url(w.Pub)}&t={PCrypto.B64Url(w.Token)}";

    static JsonObject Fail(string reason) => new() { ["t"] = "pair_fail", ["reason"] = reason };

    async Task HostPair(FrameConn fc, JsonObject m)
    {
        if (hostWindow is not { Busy: false } w)
        {
            await fc.WriteJson(Fail(hostWindow is null ? "closed" : "busy"));
            fc.Dispose();
            return;
        }
        if (m.S("mode") is not ("qr" or "code") || m.S("id") is not { } jid || m.S("name") is not { } jname || m.B64("pub", 32) is not { } jpub)
            throw new ProtocolException("bad pair_req");
        var mode = m.S("mode");
        w.Busy = true;
        try
        {
            await fc.WriteJson(new JsonObject { ["t"] = "pair_hello", ["v"] = 1, ["id"] = DeviceId, ["name"] = DeviceName, ["pf"] = opt.Platform, ["pub"] = Convert.ToBase64String(w.Pub) });
            var k = PCrypto.PairingKeys(PCrypto.Shared(w.Priv, jpub), mode == "qr" ? w.Token : Encoding.UTF8.GetBytes(w.Code), w.Pub, jpub);
            var c = await fc.ReadJson();
            // §4.2: verify the joiner's MAC first; a bad one gets bad_code without a user prompt.
            if (c.S("t") != "pair_confirm" || !PCrypto.Equal(c.B64("mac"), k.JoinerMac))
            {
                if (++w.Failures >= 5 && hostWindow == w) CloseWindow();
                await fc.WriteJson(Fail("bad_code"));
                return;
            }
            if (mode == "code")
            {
                var d = w.Decision = new TaskCompletionSource<bool?>();
                SetWindow(window! with { Request = new PairRequest(jname, k.Sas) });
                var answer = await Task.WhenAny(d.Task, Task.Delay(TimeSpan.FromSeconds(60))) == d.Task ? d.Task.Result : null;
                w.Decision = null;
                if (hostWindow == w) SetWindow(window! with { Request = null });
                if (answer != true)
                {
                    await fc.WriteJson(Fail(answer is null ? hostWindow == w ? "timeout" : "closed" : "rejected"));
                    return;
                }
            }
            AddPairing(new Pairing(jid, jname, Platform(m.S("pf")), k.PairKey));
            await fc.WriteJson(new JsonObject { ["t"] = "pair_ok", ["mac"] = Convert.ToBase64String(k.HostMac) });
            if (hostWindow == w) CloseWindow();
            J.Log($"paired with {jname} (host, {mode})");
        }
        finally
        {
            w.Busy = false;
            fc.Dispose();
        }
    }

    // ---- joiner ----

    public void ConfirmJoin(bool accept)
    {
        if (!accept) ResetJoin();
    }

    public void ResetJoin() => loop.Run(() =>
    {
        joinGen++;
        joinConn?.Dispose();
        SetJoin(new JoinState.Idle());
    });

    /// Joins using a `ctrllink://pair?...` link: tries each `h` address, then mDNS by `id` (§4.1).
    public void JoinWithUri(string ctrllinkUri) => loop.Go(() => Joining(async () =>
    {
        if (!Uri.TryCreate(ctrllinkUri.Trim(), UriKind.Absolute, out var u) || u.Scheme != "ctrllink" || u.Host != "pair")
            throw new ProtocolException("Not a Ctrl Link pairing link");
        var q = new Dictionary<string, string>();
        foreach (var kv in u.Query.TrimStart('?').Split('&'))
        {
            var i = kv.IndexOf('=');
            if (i > 0) q.TryAdd(kv[..i], Uri.UnescapeDataString(kv[(i + 1)..]));
        }
        if (q.GetValueOrDefault("id") is not { } id || PCrypto.FromB64Url(q.GetValueOrDefault("k")) is not { Length: 32 } k
            || PCrypto.FromB64Url(q.GetValueOrDefault("t")) is not { Length: 16 } t || !ushort.TryParse(q.GetValueOrDefault("p"), out var p))
            throw new ProtocolException("Pairing link is incomplete");
        if (int.TryParse(q.GetValueOrDefault("v"), out var v) && v > 1) throw new ProtocolException("Update Ctrl Link on this PC");
        var eps = q.GetValueOrDefault("h", "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(h => IPAddress.TryParse(h.Trim('[', ']'), out var a) ? new IPEndPoint(a, p) : null).OfType<IPEndPoint>().ToList();
        if (Nearby.TryGetValue(id, out var n)) eps.Add(n.Endpoint);
        Exception last = new ProtocolException($"No address to reach {q.GetValueOrDefault("n") ?? "the host"}");
        foreach (var ep in eps)
        {
            try { await JoinAt(ep, (t, k, id), null); return; }
            catch (ConnectException e) { last = e; }
        }
        throw last;
    }));

    /// Code mode over mDNS (§4.2): tries each `pair=1` host until one does not answer `bad_code`.
    public void JoinWithCode(string sixDigits) => loop.Go(() => JoinCode(sixDigits, Nearby.Values.Where(n => n.Pairing).Select(n => n.Endpoint).ToList()));

    /// Code mode against a known address (no mDNS, e.g. tests or a typed address).
    internal void JoinWithCode(string code, IPEndPoint ep) => loop.Go(() => JoinCode(code, [ep]));

    Task JoinCode(string code, List<IPEndPoint> hosts) => Joining(async () =>
    {
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) throw new ProtocolException("The code has 6 digits");
        if (hosts.Count == 0) throw new ProtocolException("No device is showing a pairing code");
        Exception last = new ProtocolException("Wrong code");
        foreach (var h in hosts)
        {
            try { await JoinAt(h, null, code); return; }
            catch (ProtocolException e) when (e.Message == "Wrong code") { last = e; }
            catch (ConnectException e) { last = e; }
        }
        throw last;
    });

    async Task Joining(Func<Task> attempt)
    {
        var gen = ++joinGen;
        try { await attempt(); }
        catch (Exception e)
        {
            if (gen == joinGen) SetJoin(new JoinState.Failed(e is ProtocolException ? e.Message : "Could not reach the device"));
        }
    }

    async Task JoinAt(IPEndPoint ep, (byte[] Token, byte[] HostPub, string HostId)? qr, string? code)
    {
        SetJoin(new JoinState.Connecting());
        var priv = PCrypto.Random(32);
        var jpub = PCrypto.PublicKey(priv);
        var fc = joinConn = await FrameConn.Connect(ep);
        using var wd = fc.Watchdog(100);
        try
        {
            await fc.WriteJson(new JsonObject
            {
                ["t"] = "pair_req", ["v"] = 1, ["mode"] = qr is null ? "code" : "qr", ["id"] = DeviceId, ["name"] = DeviceName,
                ["pf"] = opt.Platform, ["pub"] = Convert.ToBase64String(jpub),
            });
            var h = await fc.ReadJson();
            CheckFail(h);
            if (h.S("t") != "pair_hello" || h.S("id") is not { } hid || h.S("name") is not { } hname || h.B64("pub", 32) is not { } hpub)
                throw new ProtocolException("Unexpected reply from host");
            if ((h.L("v") ?? 1) > 1) throw new ProtocolException("Update Ctrl Link on this PC");
            if (qr is { } x && (!hpub.AsSpan().SequenceEqual(x.HostPub) || hid != x.HostId)) throw new ProtocolException("Host key does not match the QR code");
            var k = PCrypto.PairingKeys(PCrypto.Shared(priv, hpub), qr?.Token ?? Encoding.UTF8.GetBytes(code!), hpub, jpub);
            if (qr is null) SetJoin(new JoinState.Confirm(k.Sas, hname));
            await fc.WriteJson(new JsonObject { ["t"] = "pair_confirm", ["mac"] = Convert.ToBase64String(k.JoinerMac) });
            var r = await fc.ReadJson();
            CheckFail(r);
            if (r.S("t") != "pair_ok" || !PCrypto.Equal(r.B64("mac"), k.HostMac)) throw new ProtocolException("Host could not be verified");
            AddPairing(new Pairing(hid, hname, Platform(h.S("pf")), k.PairKey));
            pairedEndpoints[hid] = ep;
            SetJoin(new JoinState.Done(hname));
            J.Log($"paired with {hname} (joiner)");
            ConsiderDial(hid, now: true); // §4.2: the joiner dials the first session, ignoring the tie-break
        }
        finally
        {
            fc.Dispose();
            if (joinConn == fc) joinConn = null;
        }
    }

    static void CheckFail(JsonObject m)
    {
        switch (m.S("t"))
        {
            case "pair_fail":
                throw new ProtocolException(m.S("reason") switch
                {
                    "bad_code" => "Wrong code",
                    "rejected" => "Pairing was declined",
                    "timeout" => "Pairing timed out",
                    "busy" => "The other device is busy pairing. Try again",
                    _ => "Pairing window is closed",
                });
            case "error": throw new ProtocolException("Update Ctrl Link on the other device");
        }
    }
}
