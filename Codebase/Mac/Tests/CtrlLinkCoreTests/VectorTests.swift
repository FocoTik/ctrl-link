import CryptoKit
import XCTest
@testable import CtrlLinkCore

final class VectorTests: XCTestCase {
    func testVectors() throws {
        let url = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
            .appendingPathComponent("../../../protocol/test-vectors.json").standardized
        let v = try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as! [String: Any]
        func h(_ section: String, _ key: String) -> Data { Data(hex: (v[section] as! [String: Any])[key] as! String)! }
        func s(_ section: String, _ key: String) -> String { (v[section] as! [String: Any])[key] as! String }

        let hostPriv = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: h("x25519", "hostPriv"))
        let joinerPriv = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: h("x25519", "joinerPriv"))
        let hostPub = hostPriv.publicKey.rawRepresentation, joinerPub = joinerPriv.publicKey.rawRepresentation
        XCTAssertEqual(hostPub, h("x25519", "hostPub"))
        XCTAssertEqual(joinerPub, h("x25519", "joinerPub"))
        let shared = try PCrypto.shared(hostPriv, joinerPub)
        XCTAssertEqual(shared, h("x25519", "shared"))
        XCTAssertEqual(try PCrypto.shared(joinerPriv, hostPub), shared)

        for (mode, secret) in [("pairing_qr", h("pairing_qr", "token")), ("pairing_code", Data(s("pairing_code", "code").utf8))] {
            let k = PCrypto.pairingKeys(shared: shared, secret: secret, hostPub: hostPub, joinerPub: joinerPub)
            XCTAssertEqual(k.pairKey, h(mode, "pairKey"), mode)
            XCTAssertEqual(k.sas, s(mode, "sas"), mode)
            XCTAssertEqual(k.joinerMac, h(mode, "joinerMac"), mode)
            XCTAssertEqual(k.hostMac, h(mode, "hostMac"), mode)
        }

        let keys = PCrypto.sessionKeys(pairKey: h("session", "pairKey"), clientNonce: h("session", "clientNonce"),
                                       serverNonce: h("session", "serverNonce"))
        XCTAssertEqual(keys.c2s, h("session", "c2sKey"))
        XCTAssertEqual(keys.s2c, h("session", "s2cKey"))

        let frames = (v["session"] as! [String: Any])["frames"] as! [[String: Any]]
        XCTAssertEqual(frames.count, 4)
        for f in frames {
            let dir = UInt32(f["dir"] as! Int), ctr = UInt64(f["counter"] as! Int)
            let key = dir == 1 ? keys.c2s : keys.s2c
            let pt = Data(hex: f["plaintext"] as! String)!, body = Data(hex: f["body"] as! String)!
            XCTAssertEqual(try PCrypto.seal(key: key, dir: dir, counter: ctr, pt), body)
            XCTAssertEqual(try PCrypto.open(key: key, dir: dir, counter: ctr, body), pt)
        }
    }

    func testSafeName() {
        XCTAssertEqual(Session.safeName("../../etc/passwd"), "passwd")
        XCTAssertEqual(Session.safeName("C:\\x\\a:b.txt"), "a_b.txt")
        XCTAssertEqual(Session.safeName("..hidden"), "hidden")
        XCTAssertEqual(Session.safeName("a\u{07}b"), "a_b")
        XCTAssertEqual(Session.safeName(".."), "file")
        XCTAssertEqual(Session.safeName(nil), "file")
        XCTAssertEqual(Session.safeName(String(repeating: "é", count: 150)).utf8.count, 200)
        XCTAssertEqual(platform("linux"), "other")
    }
}
