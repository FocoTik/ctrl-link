import Foundation
import Network

struct ProtocolError: Error, CustomStringConvertible {
    let description: String
    init(_ d: String) { description = d }
}

typealias JSON = [String: Any]

/// One TCP connection carrying length-prefixed frames (§3): JSON before the session, AES-GCM after.
@MainActor final class FrameConn {
    static let maxFrame = 262_144 + 64
    let nw: NWConnection
    private var sendKey: Data?, recvKey: Data?
    private var sendDir: UInt32 = 0, recvDir: UInt32 = 0
    private var sendCtr: UInt64 = 0, recvCtr: UInt64 = 0

    init(_ nw: NWConnection) { self.nw = nw }

    /// Waits until the TCP connection is ready. `.waiting` counts as failure (refused / unreachable).
    func start() async throws {
        try await withCheckedThrowingContinuation { (c: CheckedContinuation<Void, Error>) in
            var resumed = false
            nw.stateUpdateHandler = { state in
                let result: Error?? = switch state {
                case .ready: .some(nil)
                case .failed(let e), .waiting(let e): .some(e)
                case .cancelled: .some(ProtocolError("cancelled"))
                default: .none
                }
                guard let r = result else { return }
                DispatchQueue.main.async {
                    guard !resumed else { return }
                    resumed = true
                    if let e = r { c.resume(throwing: e) } else { c.resume() }
                }
            }
            nw.start(queue: .main)
        }
    }

    func close() { nw.cancel() }

    func setKeys(send: Data, sendDir: UInt32, recv: Data, recvDir: UInt32) {
        sendKey = send; recvKey = recv; self.sendDir = sendDir; self.recvDir = recvDir
    }

    private func read(_ n: Int) async throws -> Data {
        try await withCheckedThrowingContinuation { c in
            nw.receive(minimumIncompleteLength: n, maximumLength: n) { data, _, _, error in
                if let error { c.resume(throwing: error) } else if let data, data.count == n { c.resume(returning: data) } else { c.resume(throwing: ProtocolError("eof")) }
            }
        }
    }

    func readFrame() async throws -> Data {
        let len = try await read(4).reduce(0) { $0 << 8 | Int($1) }
        guard len <= Self.maxFrame else { throw ProtocolError("oversize frame") }
        return len == 0 ? Data() : try await read(len)
    }

    /// Enqueues a frame; resumes when the network stack took it (gives back-pressure for file streaming).
    func writeFrame(_ body: Data) async throws {
        var len = UInt32(body.count).bigEndian
        let frame = Data(bytes: &len, count: 4) + body
        try await withCheckedThrowingContinuation { (c: CheckedContinuation<Void, Error>) in
            nw.send(content: frame, completion: .contentProcessed { e in if let e { c.resume(throwing: e) } else { c.resume() } })
        }
    }

    func writeJSON(_ obj: JSON) async throws {
        try await writeFrame(JSONSerialization.data(withJSONObject: obj, options: .withoutEscapingSlashes))
    }

    func readJSON() async throws -> JSON {
        guard let o = try JSONSerialization.jsonObject(with: try await readFrame()) as? JSON else { throw ProtocolError("bad json") }
        return o
    }

    /// Seals synchronously (so the counter order equals the send order on the main actor), then awaits the send.
    func writeSealed(_ plaintext: Data) async throws {
        guard let k = sendKey else { throw ProtocolError("no session") }
        let body = try PCrypto.seal(key: k, dir: sendDir, counter: sendCtr, plaintext)
        sendCtr += 1
        try await writeFrame(body)
    }

    func readSealed() async throws -> Data {
        guard let k = recvKey else { throw ProtocolError("no session") }
        let frame = try await readFrame()
        let pt = try PCrypto.open(key: k, dir: recvDir, counter: recvCtr, frame)
        recvCtr += 1
        guard !pt.isEmpty else { throw ProtocolError("empty frame") }
        return pt
    }
}
