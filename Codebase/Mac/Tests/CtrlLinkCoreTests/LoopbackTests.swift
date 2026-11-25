import CryptoKit
import Network
import XCTest
@testable import CtrlLinkCore

/// Two in-process engines on 127.0.0.1, no mDNS.
@MainActor final class LoopbackTests: XCTestCase {
    var dirs: [URL] = []

    override func tearDown() async throws {
        dirs.forEach { try? FileManager.default.removeItem(at: $0) }
    }

    func engine(_ name: String) async throws -> CtrlLinkEngine {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("ctrllink-test-\(UUID().uuidString)")
        dirs.append(dir)
        let e = CtrlLinkEngine(storeURL: dir.appendingPathComponent("state.json"), receivedDir: dir.appendingPathComponent("recv"),
                               advertise: false, port: 0)
        e.deviceName = name
        e.retryDelay = 0.3
        e.start()
        try await until { e.port != nil }
        return e
    }

    func until(_ timeout: Double = 15, _ cond: @escaping () -> Bool) async throws {
        let end = Date().addingTimeInterval(timeout)
        while !cond() {
            guard Date() < end else { throw CancellationError() }
            try await Task.sleep(for: .milliseconds(20))
        }
    }

    func local(_ e: CtrlLinkEngine) -> NWEndpoint { .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: e.port!)!) }

    func testQRPairingThenTextAndFile() async throws {
        let host = try await engine("Host Mac"), joiner = try await engine("Joiner Mac")
        host.openPairingWindow()
        let uri = host.pairingWindow!.uri.replacingOccurrences(of: #"h=[^&]*"#, with: "h=127.0.0.1", options: .regularExpression)
        XCTAssertTrue(uri.hasPrefix("ctrllink://pair?v=1&id=\(host.deviceId)&n=Host%20Mac&pf=mac&h=127.0.0.1&p=\(host.port!)&k="))
        let p = try await joiner.join(uri: uri)
        XCTAssertEqual(p.id, host.deviceId)
        XCTAssertEqual(host.pairings.first?.id, joiner.deviceId)
        XCTAssertEqual(host.pairings.first?.key, p.key)
        XCTAssertNil(host.pairingWindow, "window closes after one pairing")
        print("QR pairing OK, pairKey \(p.key.hex.prefix(16))…")

        try await until { host.onlineIds.contains(joiner.deviceId) && joiner.onlineIds.contains(host.deviceId) }
        print("session up")

        var got: [Received] = []
        joiner.onReceive = { got.append($0) }
        host.send(.text("hello 👋"))
        try await until { got.count == 1 }
        XCTAssertEqual(got[0].kind, .text)
        XCTAssertEqual(got[0].text, "hello 👋")
        XCTAssertEqual(joiner.clips.first?.from, "Host Mac")

        let src = dirs[0].appendingPathComponent("video.mp4")
        let bytes = PCrypto.random(5 * 1024 * 1024 + 123)
        try bytes.write(to: src)
        host.send(.files([src]))
        try await until(30) { got.count == 2 }
        XCTAssertEqual(got[1].kind, .files)
        let out = try XCTUnwrap(got[1].files.first)
        XCTAssertEqual(out.lastPathComponent, "video.mp4")
        let hash = SHA256.hash(data: try Data(contentsOf: out)).description
        XCTAssertEqual(hash, SHA256.hash(data: bytes).description)
        XCTAssertEqual(got[1].fingerprint, Data(SHA256.hash(data: bytes)))
        print("5 MB file OK, \(hash)")

        host.send(.files([src]))
        try await until(30) { got.count == 3 }
        XCTAssertEqual(got[2].files.first?.lastPathComponent, "video (1).mp4")

        // Over the auto-accept limit → offer, then decline.
        joiner.autoAcceptLimit = 1000
        host.send(.files([src]))
        try await until { !joiner.offers.isEmpty }
        joiner.rejectOffer(joiner.offers[0].id)
        XCTAssertTrue(joiner.offers.isEmpty)

        joiner.unpair(host.deviceId)
        try await until { host.pairings.isEmpty }
    }

    /// History is kept on disk: a new engine on the same store sees it, and Clear history empties the file too.
    @MainActor func testHistoryPersists() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("ctrllink-test-\(UUID().uuidString)")
        dirs.append(dir)
        let store = dir.appendingPathComponent("state.json"), recv = dir.appendingPathComponent("recv")
        let a = CtrlLinkEngine(storeURL: store, receivedDir: recv, advertise: false, port: 0)
        a.send(.text("first")); a.send(.url("https://example.com"))
        let b = CtrlLinkEngine(storeURL: store, receivedDir: recv, advertise: false, port: 0)
        XCTAssertEqual(b.clips.map(\.preview), ["https://example.com", "first"])
        XCTAssertEqual(b.clips.first?.kind, .url)
        b.clearHistory()
        XCTAssertTrue(CtrlLinkEngine(storeURL: store, receivedDir: recv, advertise: false, port: 0).clips.isEmpty)
    }

    /// The engine keeps one speed sample a second per transfer, whatever the UI is doing, so the graph has no gaps.
    func testTransferSpeedSamples() async throws {
        let e = try await engine("Speed")
        let key = "peer/clip"
        e.progress(key, name: "a.zip", peer: "peer", incoming: true, done: 0, total: 30_000_000)
        for done: Int64 in [1_000_000, 4_000_000, 5_000_000] { // the first update after start only sets the baseline
            try await Task.sleep(for: .milliseconds(1050))
            e.progress(key, name: "a.zip", peer: "peer", incoming: true, done: done, total: 30_000_000)
        }
        let speeds = try XCTUnwrap(e.transfers.first?.speeds)
        XCTAssertGreaterThanOrEqual(speeds.count, 2)
        XCTAssertGreaterThan(speeds[speeds.count - 2].bytesPerSecond, speeds.last!.bytesPerSecond) // 3 MB/s then 1 MB/s
        XCTAssertEqual(speeds.last!.progress, 5.0 / 30, accuracy: 0.001)
        e.endTransfer(key)
        XCTAssertTrue(e.transfers.isEmpty)
    }

    func testCodePairing() async throws {
        let host = try await engine("Host"), joiner = try await engine("Joiner")
        host.openPairingWindow()
        let code = host.pairingWindow!.code
        let join = Task { try await joiner.join(code: code, endpoint: local(host)) }
        try await until { host.pairingWindow?.request != nil }
        guard case .confirm(let sas, let hostName) = joiner.joinState else { return XCTFail("no SAS on joiner") }
        XCTAssertEqual(sas, host.pairingWindow!.request!.sas)
        XCTAssertEqual(sas.count, 6)
        XCTAssertTrue(sas.allSatisfy(\.isNumber))
        XCTAssertEqual(host.pairingWindow!.request!.name, "Joiner")
        XCTAssertEqual(hostName, "Host")
        host.respondToPairRequest(true)
        let p = try await join.value
        XCTAssertEqual(host.pairings.first?.key, p.key)
        print("code pairing OK, SAS \(sas)")

        // The Devices graph shows each live peer's address; Clear history forgets clips and daily counts.
        try await until { host.peers.first?.online == true && host.peers.first?.ip != nil }
        XCTAssertEqual(host.peers.first?.ip, "127.0.0.1")
        host.send(.text("hello"))
        XCTAssertEqual(host.counts(on: Date())[.text], 1)
        host.clearHistory()
        XCTAssertTrue(host.clips.isEmpty)
        XCTAssertTrue(host.counts(on: Date()).isEmpty)

        // Wrong code and a declined request both fail cleanly.
        let other = try await engine("Other")
        host.openPairingWindow()
        let wrong = host.pairingWindow!.code == "000000" ? "111111" : "000000"
        do { try await other.join(code: wrong, endpoint: local(host)); XCTFail("wrong code accepted") } catch {}
        XCTAssertEqual(other.joinState, .failed("Wrong code"))
        let c2 = host.pairingWindow!.code
        let declined = Task { try await other.join(code: c2, endpoint: local(host)) }
        try await until { host.pairingWindow?.request != nil }
        host.respondToPairRequest(false)
        do { _ = try await declined.value; XCTFail("declined pairing succeeded") } catch {}
        XCTAssertEqual(other.joinState, .failed("Pairing was declined"))
        XCTAssertEqual(host.pairings.count, 1)
    }
}
