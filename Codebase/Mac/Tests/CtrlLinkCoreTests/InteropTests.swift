import CryptoKit
import Network
import XCTest
@testable import CtrlLinkCore

/// Against the Node reference peer (protocol/test-peer). Opt-in: CTRLLINK_INTEROP=1 swift test --filter Interop
@MainActor final class InteropTests: XCTestCase {
    var procs: [Process] = []
    var events: [[String: Any]] = []
    let peerDir = URL(fileURLWithPath: #filePath).deletingLastPathComponent().appendingPathComponent("../../../protocol/test-peer").standardized
    let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("ctrllink-interop-\(UUID().uuidString)")

    override func setUp() async throws {
        guard ProcessInfo.processInfo.environment["CTRLLINK_INTEROP"] == "1" else { throw XCTSkip("set CTRLLINK_INTEROP=1") }
    }

    override func tearDown() async throws {
        procs.forEach { $0.terminate() }
        try? FileManager.default.removeItem(at: tmp)
    }

    func node(_ args: [String]) throws -> FileHandle {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/env")
        p.arguments = ["node", peerDir.appendingPathComponent("ctrllink-peer.mjs").path] + args
        let out = Pipe(), inp = Pipe()
        p.standardOutput = out
        p.standardInput = inp
        var buf = Data()
        out.fileHandleForReading.readabilityHandler = { [weak self] h in
            let d = h.availableData
            DispatchQueue.main.async {
                buf += d
                while let i = buf.firstIndex(of: 10) {
                    let line = buf[buf.startIndex..<i]
                    buf = Data(buf[(i + 1)...])
                    if let o = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any] {
                        print("peer:", Date().timeIntervalSince1970, String(decoding: line, as: UTF8.self).prefix(200))
                        self?.events.append(o)
                    }
                }
            }
        }
        try p.run()
        procs.append(p)
        return inp.fileHandleForWriting
    }

    func event(_ ev: String, timeout: Double = 20, where f: @escaping ([String: Any]) -> Bool = { _ in true }) async throws -> [String: Any] {
        let end = Date().addingTimeInterval(timeout)
        while true {
            if let e = events.first(where: { $0["ev"] as? String == ev && f($0) }) { return e }
            guard Date() < end else { XCTFail("no \(ev) event"); throw CancellationError() }
            try await Task.sleep(for: .milliseconds(50))
        }
    }

    func until(_ timeout: Double = 20, _ cond: @escaping () -> Bool) async throws {
        let end = Date().addingTimeInterval(timeout)
        while !cond() {
            guard Date() < end else { XCTFail("timed out"); throw CancellationError() }
            try await Task.sleep(for: .milliseconds(50))
        }
    }

    func engine() async throws -> CtrlLinkEngine {
        let e = CtrlLinkEngine(storeURL: tmp.appendingPathComponent("mac/state.json"), receivedDir: tmp.appendingPathComponent("mac/recv"),
                               advertise: false, port: 0)
        e.deviceName = "Swift Mac"
        e.start()
        try await until { e.port != nil }
        return e
    }

    func testMacJoinsNodeHostByQR() async throws {
        let mac = try await engine()
        let stdin = try node(["host", "--no-mdns", "--port", "47911", "--auto-accept", "--json", "--name", "Node Peer",
                              "--state", tmp.appendingPathComponent("node").path])
        let uri = try await event("pairing")["uri"] as! String
        let p = try await mac.join(uri: uri.replacingOccurrences(of: #"h=[^&]*"#, with: "h=127.0.0.1", options: .regularExpression))
        _ = try await event("paired")
        _ = try await event("connected")
        try await until { mac.onlineIds.contains(p.id) }
        print("interop: QR pairing + session OK")

        var got: [Received] = []
        mac.onReceive = { got.append($0) }
        stdin.write(Data("hello from node\n".utf8))
        try await until { !got.isEmpty }
        XCTAssertEqual(got[0].text, "hello from node")

        mac.send(.url("https://example.com/a?b=1"))
        let clip = try await event("clip")
        XCTAssertEqual(clip["kind"] as? String, "url")
        XCTAssertEqual(clip["text"] as? String, "https://example.com/a?b=1")

        let src = tmp.appendingPathComponent("big.bin")
        let bytes = PCrypto.random(5_000_000)
        try bytes.write(to: src)
        mac.send(.files([src]))
        let f = try await event("file", timeout: 30)
        XCTAssertEqual(f["sha256"] as? String, Data(SHA256.hash(data: bytes)).hex)
        print("interop: Mac → Node 5 MB file OK")

        stdin.write(Data("/file \(src.path)\n".utf8))
        try await until(30) { got.count == 2 }
        XCTAssertEqual(got[1].fingerprint, Data(SHA256.hash(data: bytes)))
        print("interop: Node → Mac 5 MB file OK")
    }

    func testNodeJoinsMacHostByCode() async throws {
        let mac = try await engine()
        mac.openPairingWindow()
        let code = mac.pairingWindow!.code
        _ = try node(["join", code, "--connect", "127.0.0.1:\(mac.port!)", "--port", "47912", "--json", "--name", "Node Joiner",
                      "--state", tmp.appendingPathComponent("node2").path])
        try await until { mac.pairingWindow?.request != nil }
        let sas = try await event("sas")["sas"] as? String
        XCTAssertEqual(sas, mac.pairingWindow!.request!.sas)
        mac.respondToPairRequest(true)
        _ = try await event("paired")
        try await until { !mac.onlineIds.isEmpty }
        print("interop: code pairing (Mac host) + session OK, SAS \(sas ?? "")")
    }
}
