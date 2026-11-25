import CryptoKit
import Foundation

/// Protocol v1 crypto (PROTOCOL.md §3–§5). Mirrors protocol/test-peer/crypto.mjs.
public enum PCrypto {
    public static func hkdf(_ ikm: Data, salt: Data, info: Data, count: Int) -> Data {
        HKDF<SHA256>.deriveKey(inputKeyMaterial: SymmetricKey(data: ikm), salt: salt, info: info, outputByteCount: count).data
    }

    public static func hmac(_ key: Data, _ message: Data) -> Data {
        Data(HMAC<SHA256>.authenticationCode(for: message, using: SymmetricKey(data: key)))
    }

    public static func macValid(_ mac: Data, key: Data, message: Data) -> Bool {
        HMAC<SHA256>.isValidAuthenticationCode(mac, authenticating: message, using: SymmetricKey(data: key))
    }

    public static func shared(_ priv: Curve25519.KeyAgreement.PrivateKey, _ peerPub: Data) throws -> Data {
        let s = try priv.sharedSecretFromKeyAgreement(with: .init(rawRepresentation: peerPub))
        return s.withUnsafeBytes { Data($0) }
    }

    public struct PairKeys { public let pairKey: Data, sas: String, joinerMac: Data, hostMac: Data }

    public static func pairingKeys(shared: Data, secret: Data, hostPub: Data, joinerPub: Data) -> PairKeys {
        let pubs = hostPub + joinerPub
        let pairKey = hkdf(shared, salt: secret, info: Data("ctrllink pair v1".utf8) + pubs, count: 32)
        let s = hkdf(shared, salt: Data(), info: Data("ctrllink sas v1".utf8) + pubs, count: 4)
        let n = s.reduce(UInt32(0)) { $0 << 8 | UInt32($1) } % 1_000_000
        return PairKeys(pairKey: pairKey, sas: String(format: "%06u", n),
                        joinerMac: hmac(pairKey, Data("joiner".utf8) + pubs),
                        hostMac: hmac(pairKey, Data("host".utf8) + pubs))
    }

    public static func sessionKeys(pairKey: Data, clientNonce: Data, serverNonce: Data) -> (c2s: Data, s2c: Data) {
        let k = hkdf(pairKey, salt: clientNonce + serverNonce, info: Data("ctrllink session v1".utf8), count: 64)
        return (k.prefix(32), k.suffix(32))
    }

    static func nonce(dir: UInt32, counter: UInt64) -> Data {
        withUnsafeBytes(of: dir.bigEndian) { Data($0) } + withUnsafeBytes(of: counter.bigEndian) { Data($0) }
    }

    public static func seal(key: Data, dir: UInt32, counter: UInt64, _ plaintext: Data) throws -> Data {
        let box = try AES.GCM.seal(plaintext, using: SymmetricKey(data: key), nonce: .init(data: nonce(dir: dir, counter: counter)))
        return box.ciphertext + box.tag
    }

    public static func open(key: Data, dir: UInt32, counter: UInt64, _ body: Data) throws -> Data {
        guard body.count >= 16 else { throw CryptoKitError.authenticationFailure }
        let box = try AES.GCM.SealedBox(nonce: .init(data: nonce(dir: dir, counter: counter)),
                                        ciphertext: body.dropLast(16), tag: body.suffix(16))
        return try AES.GCM.open(box, using: SymmetricKey(data: key))
    }

    public static func random(_ n: Int) -> Data {
        var g = SystemRandomNumberGenerator()
        return Data((0..<n).map { _ in UInt8.random(in: 0...255, using: &g) })
    }
}

extension SymmetricKey {
    var data: Data { withUnsafeBytes { Data($0) } }
}

extension Data {
    var b64url: String {
        base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    init?(b64url s: String) {
        var t = s.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        t += String(repeating: "=", count: (4 - t.count % 4) % 4)
        self.init(base64Encoded: t)
    }

    public var hex: String { map { String(format: "%02x", $0) }.joined() }

    public init?(hex: String) {
        var d = Data()
        var i = hex.startIndex
        while i < hex.endIndex {
            guard let j = hex.index(i, offsetBy: 2, limitedBy: hex.endIndex), let b = UInt8(hex[i..<j], radix: 16) else { return nil }
            d.append(b)
            i = j
        }
        self = d
    }
}
