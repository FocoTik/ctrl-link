using System.Text;
using System.Text.Json;

namespace CtrlLink.Core.Tests;

/// §8: protocol/test-vectors.json byte for byte.
public class VectorTests
{
    static readonly JsonElement V = JsonDocument.Parse(File.ReadAllText(Path.Combine(T.RepoRoot(), "protocol", "test-vectors.json"))).RootElement;

    static byte[] H(JsonElement e, string k) => Convert.FromHexString(e.GetProperty(k).GetString()!);

    static string Hex(byte[] b) => Convert.ToHexStringLower(b);

    [Fact]
    public void X25519()
    {
        var x = V.GetProperty("x25519");
        Assert.Equal(Hex(H(x, "hostPub")), Hex(PCrypto.PublicKey(H(x, "hostPriv"))));
        Assert.Equal(Hex(H(x, "joinerPub")), Hex(PCrypto.PublicKey(H(x, "joinerPriv"))));
        Assert.Equal(Hex(H(x, "shared")), Hex(PCrypto.Shared(H(x, "hostPriv"), H(x, "joinerPub"))));
        Assert.Equal(Hex(H(x, "shared")), Hex(PCrypto.Shared(H(x, "joinerPriv"), H(x, "hostPub"))));
    }

    [Theory]
    [InlineData("pairing_qr")]
    [InlineData("pairing_code")]
    public void Pairing(string which)
    {
        var x = V.GetProperty("x25519");
        var p = V.GetProperty(which);
        var secret = p.TryGetProperty("token", out _) ? H(p, "token") : Encoding.UTF8.GetBytes(p.GetProperty("code").GetString()!);
        var k = PCrypto.PairingKeys(H(x, "shared"), secret, H(x, "hostPub"), H(x, "joinerPub"));
        Assert.Equal(Hex(H(p, "pairKey")), Hex(k.PairKey));
        Assert.Equal(p.GetProperty("sas").GetString(), k.Sas);
        Assert.Equal(Hex(H(p, "joinerMac")), Hex(k.JoinerMac));
        Assert.Equal(Hex(H(p, "hostMac")), Hex(k.HostMac));
    }

    [Fact]
    public void SessionKeysAndFrames()
    {
        var s = V.GetProperty("session");
        var (c2s, s2c) = PCrypto.SessionKeys(H(s, "pairKey"), H(s, "clientNonce"), H(s, "serverNonce"));
        Assert.Equal(Hex(H(s, "c2sKey")), Hex(c2s));
        Assert.Equal(Hex(H(s, "s2cKey")), Hex(s2c));
        foreach (var f in s.GetProperty("frames").EnumerateArray())
        {
            var key = f.GetProperty("from").GetString() == "client" ? c2s : s2c;
            var (dir, ctr) = (f.GetProperty("dir").GetUInt32(), f.GetProperty("counter").GetUInt64());
            Assert.Equal(Hex(H(f, "body")), Hex(PCrypto.Seal(key, dir, ctr, H(f, "plaintext"))));
            Assert.Equal(Hex(H(f, "plaintext")), Hex(PCrypto.Open(key, dir, ctr, H(f, "body"))));
        }
    }
}
