using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;

namespace CtrlLink.Core;

sealed record PairKeys(byte[] PairKey, string Sas, byte[] JoinerMac, byte[] HostMac);

/// Protocol v1 crypto (PROTOCOL.md §3–§5). Mirrors protocol/test-peer/crypto.mjs and Mac's PCrypto.
static class PCrypto
{
    public static byte[] Random(int n) => RandomNumberGenerator.GetBytes(n);

    public static byte[] PublicKey(byte[] priv) => new X25519PrivateKeyParameters(priv).GeneratePublicKey().GetEncoded();

    public static byte[] Shared(byte[] priv, byte[] peerPub)
    {
        var o = new byte[32];
        new X25519PrivateKeyParameters(priv).GenerateSecret(new X25519PublicKeyParameters(peerPub), o, 0);
        return o;
    }

    public static byte[] Hkdf(byte[] ikm, byte[] salt, byte[] info, int n) => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, n, salt, info);

    public static byte[] Hmac(byte[] key, byte[] msg) => HMACSHA256.HashData(key, msg);

    public static bool Equal(byte[]? a, byte[] b) => a is not null && CryptographicOperations.FixedTimeEquals(a, b);

    static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    static byte[] U(string s) => Encoding.UTF8.GetBytes(s);

    public static PairKeys PairingKeys(byte[] shared, byte[] secret, byte[] hostPub, byte[] joinerPub)
    {
        var pubs = Cat(hostPub, joinerPub);
        var pairKey = Hkdf(shared, secret, Cat(U("ctrllink pair v1"), pubs), 32);
        var sas = BinaryPrimitives.ReadUInt32BigEndian(Hkdf(shared, [], Cat(U("ctrllink sas v1"), pubs), 4)) % 1_000_000;
        return new(pairKey, sas.ToString("D6"), Hmac(pairKey, Cat(U("joiner"), pubs)), Hmac(pairKey, Cat(U("host"), pubs)));
    }

    public static (byte[] C2s, byte[] S2c) SessionKeys(byte[] pairKey, byte[] clientNonce, byte[] serverNonce)
    {
        var k = Hkdf(pairKey, Cat(clientNonce, serverNonce), U("ctrllink session v1"), 64);
        return (k[..32], k[32..]);
    }

    static byte[] Nonce(uint dir, ulong counter)
    {
        var n = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(n, dir);
        BinaryPrimitives.WriteUInt64BigEndian(n.AsSpan(4), counter);
        return n;
    }

    public static byte[] Seal(byte[] key, uint dir, ulong counter, byte[] plain)
    {
        var body = new byte[plain.Length + 16];
        using var g = new AesGcm(key, 16);
        g.Encrypt(Nonce(dir, counter), plain, body.AsSpan(0, plain.Length), body.AsSpan(plain.Length));
        return body;
    }

    public static byte[] Open(byte[] key, uint dir, ulong counter, byte[] body)
    {
        if (body.Length < 16) throw new ProtocolException("short frame");
        var plain = new byte[body.Length - 16];
        using var g = new AesGcm(key, 16);
        g.Decrypt(Nonce(dir, counter), body.AsSpan(0, plain.Length), body.AsSpan(plain.Length), plain);
        return plain;
    }

    public static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? FromB64Url(string? s)
    {
        if (s is null) return null;
        var t = s.Replace('-', '+').Replace('_', '/');
        try { return Convert.FromBase64String(t + new string('=', (4 - t.Length % 4) % 4)); } catch (FormatException) { return null; }
    }
}
