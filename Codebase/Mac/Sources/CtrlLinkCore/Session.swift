import CryptoKit
import Foundation

struct OutFile { let fileId: UUID, url: URL, size: Int64 }

final class OutClip {
    let files: [OutFile]
    var cancelled = false
    var timeout: Task<Void, Never>?
    init(_ files: [OutFile]) { self.files = files }
}

final class InFile {
    let fileId: UUID, name: String, mime: String, size: Int64
    let tmp: URL
    var handle: FileHandle?
    var hasher = SHA256()
    var got: Int64 = 0
    var sha: Data?
    var final: URL?
    init(fileId: UUID, name: String, mime: String, size: Int64, tmp: URL) {
        self.fileId = fileId; self.name = name; self.mime = mime; self.size = size; self.tmp = tmp
    }
}

final class InClip {
    let kind: ClipKind, files: [InFile]
    var accepted = false
    init(kind: ClipKind, files: [InFile]) { self.kind = kind; self.files = files }
}

/// An established, encrypted session with one paired peer (§5–§6).
@MainActor final class Session {
    let engine: CtrlLinkEngine
    let conn: FrameConn
    let peerId: String
    let isClient: Bool
    let started = Date()
    var lastRx = Date()
    var lastPing = Date.distantPast
    var probeAt: Date?
    var closed = false
    var outgoing: [String: OutClip] = [:]
    var incoming: [String: InClip] = [:]
    var droppedFiles: Set<UUID> = [] // fileIds of cancelled/rejected clips: late chunks and `done` are ignored (§6.2)

    init(engine: CtrlLinkEngine, conn: FrameConn, peerId: String, isClient: Bool) {
        self.engine = engine; self.conn = conn; self.peerId = peerId; self.isClient = isClient
    }

    func run() async {
        let timer = Task {
            while !Task.isCancelled && !closed {
                try? await Task.sleep(for: .seconds(1))
                tick()
            }
        }
        do {
            while !closed {
                let pt = try await conn.readSealed()
                lastRx = Date()
                engine.touched(peerId)
                try await handle(pt)
            }
        } catch {
            if !closed { engine.log("session \(peerId) ended: \(error)") }
        }
        timer.cancel()
        close()
    }

    func tick() {
        let now = Date()
        let silent = now.timeIntervalSince(lastRx)
        if silent > 45 { return close() }
        if let p = probeAt, lastRx < p, now.timeIntervalSince(p) > 5 { return close() } // network changed and peer is gone
        if silent > 15 && now.timeIntervalSince(lastPing) > 15 {
            lastPing = now
            Task { try? await send(["t": "ping"]) }
        }
    }

    /// After a network change: ping now, and drop the session if nothing comes back within 5 s.
    func probe() {
        probeAt = Date()
        Task { try? await send(["t": "ping"]) }
    }

    func close() {
        guard !closed else { return }
        closed = true
        conn.close()
        for c in outgoing.values { c.cancelled = true; c.timeout?.cancel() }
        outgoing = [:]
        for id in Array(incoming.keys) { dropIncoming(id) }
        engine.sessionEnded(self)
    }

    func send(_ m: JSON) async throws { try await conn.writeSealed(control(m)) }

    func handle(_ pt: Data) async throws {
        let pt = Data(pt)
        switch pt[0] {
        case 1:
            guard let m = parseJSON(pt.dropFirst()) else { throw ProtocolError("bad control json") }
            try await handleControl(m)
        case 2:
            guard pt.count >= 17 else { throw ProtocolError("short chunk") }
            let fid = pt[1..<17].withUnsafeBytes { UUID(uuid: $0.loadUnaligned(as: uuid_t.self)) }
            try await chunk(fid, pt.dropFirst(17))
        default: throw ProtocolError("unknown frame kind") // §3
        }
    }

    func handleControl(_ m: JSON) async throws {
        let id = m["id"] as? String ?? ""
        switch m["t"] as? String {
        case "ping": try await send(["t": "pong"])
        case "clip": try await clip(m)
        case "accept": stream(id)
        case "reject": outgoing[id]?.timeout?.cancel(); outgoing[id] = nil
        case "done": try await done(m)
        case "cancel":
            outgoing[id]?.cancelled = true
            outgoing[id] = nil
            dropIncoming(id)
        case "unpair":
            let name = engine.name(of: peerId)
            engine.removePairing(peerId)
            engine.notify?(.unpaired(name))
            close()
        case "error":
            if m["reason"] as? String == "version" { engine.notify?(.version(engine.name(of: peerId))) }
        default: break // ready, pong, unknown types (§7)
        }
    }

    // MARK: sending

    func offer(_ m: JSON, _ files: [OutFile]) {
        if !files.isEmpty, let id = m["id"] as? String {
            let c = OutClip(files)
            c.timeout = Task { [weak self] in
                try? await Task.sleep(for: .seconds(300)) // §6.2: give up after 5 minutes without an answer
                guard !Task.isCancelled, let self, self.outgoing[id] === c else { return }
                self.outgoing[id] = nil
                try? await self.send(["t": "cancel", "id": id])
            }
            outgoing[id] = c
        }
        Task { try? await send(m) }
    }

    func stream(_ id: String) {
        guard let c = outgoing[id] else { return }
        c.timeout?.cancel()
        Task {
            let key = "\(peerId)/\(id)"
            defer { engine.endTransfer(key); outgoing[id] = nil }
            let total = c.files.reduce(0) { $0 + $1.size }
            var sent: Int64 = 0
            do {
                for f in c.files {
                    let fh = try FileHandle(forReadingFrom: f.url)
                    defer { try? fh.close() }
                    var h = SHA256()
                    let fid = withUnsafeBytes(of: f.fileId.uuid) { Data($0) }
                    while let d = try fh.read(upToCount: 262_144), !d.isEmpty {
                        guard !c.cancelled, !closed else { return }
                        h.update(data: d)
                        try await conn.writeSealed(Data([2]) + fid + d)
                        sent += Int64(d.count)
                        engine.progress(key, name: f.url.lastPathComponent, peer: peerId, incoming: false, done: sent, total: total)
                    }
                    try await send(["t": "done", "id": id, "fileId": f.fileId.uuidString.lowercased(), "sha256": Data(h.finalize()).hex])
                }
            } catch {
                engine.log("send \(id) failed: \(error)")
                try? await send(["t": "cancel", "id": id])
            }
        }
    }

    // MARK: receiving

    func clip(_ m: JSON) async throws {
        guard let id = m["id"] as? String, let kind = ClipKind(rawValue: m["kind"] as? String ?? "") else { return }
        switch kind {
        case .text, .url:
            guard let text = m["text"] as? String else { return }
            engine.received(Received(kind: kind, text: text, files: [], mime: nil, fingerprint: Data(SHA256.hash(data: Data(text.utf8)))),
                            id: id, from: peerId)
        case .image, .files:
            guard incoming[id] == nil, let list = m["files"] as? [JSON], !list.isEmpty else { return }
            var files: [InFile] = []
            for f in list {
                guard let fid = (f["fileId"] as? String).flatMap(UUID.init(uuidString:)),
                      let size = (f["size"] as? NSNumber)?.int64Value, size >= 0 else { return }
                let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("CtrlLink-\(UUID().uuidString).part")
                files.append(InFile(fileId: fid, name: Self.safeName(f["name"] as? String), mime: f["mime"] as? String ?? "", size: size, tmp: tmp))
            }
            incoming[id] = InClip(kind: kind, files: files)
            let total = files.reduce(0) { $0 + $1.size }
            if total <= engine.autoAcceptLimit {
                accept(id)
            } else {
                let o = Offer(id: id, peerId: peerId, peerName: engine.name(of: peerId), count: files.count, size: total)
                engine.offers.append(o)
                engine.notify?(.offer(o))
            }
        }
    }

    /// §6.3: last path component, `/ \ :` and control chars → `_`, no leading dots, ≤ 200 bytes, empty → `file`.
    nonisolated static func safeName(_ s: String?) -> String {
        let last = (s ?? "").split(whereSeparator: { $0 == "/" || $0 == "\\" }).last.map(String.init) ?? ""
        var n = String(String.UnicodeScalarView(last.unicodeScalars.map {
            $0 == ":" || CharacterSet.controlCharacters.contains($0) ? "_" : $0
        }))
        while n.hasPrefix(".") { n.removeFirst() }
        while n.utf8.count > 200 { n.removeLast() }
        return n.isEmpty ? "file" : n
    }

    func accept(_ id: String) {
        guard let c = incoming[id], !c.accepted else { return }
        do {
            for f in c.files {
                guard FileManager.default.createFile(atPath: f.tmp.path, contents: nil) else { throw ProtocolError("cannot create temp file") }
                f.handle = try FileHandle(forWritingTo: f.tmp)
            }
        } catch {
            engine.log("accept \(id): \(error)")
            return reject(id)
        }
        c.accepted = true
        Task { try? await send(["t": "accept", "id": id]) }
    }

    func reject(_ id: String) {
        dropIncoming(id)
        Task { try? await send(["t": "reject", "id": id]) }
    }

    func dropIncoming(_ id: String) {
        guard let c = incoming.removeValue(forKey: id) else { return }
        droppedFiles.formUnion(c.files.map(\.fileId))
        engine.endTransfer("\(peerId)/\(id)")
        engine.offers.removeAll { $0.id == id }
        for f in c.files where f.final == nil {
            try? f.handle?.close()
            try? FileManager.default.removeItem(at: f.tmp)
        }
    }

    func cancelIncoming(_ id: String, _ why: String) async throws {
        engine.log("cancel \(id): \(why)")
        dropIncoming(id)
        engine.notify?(.failed(engine.name(of: peerId)))
        try await send(["t": "cancel", "id": id])
    }

    func chunk(_ fid: UUID, _ data: Data) async throws {
        if droppedFiles.contains(fid) { return }
        // §6.2: a chunk for a fileId of no accepted clip closes the session (except late chunks of a cancelled clip).
        guard let (id, c) = incoming.first(where: { $0.value.accepted && $0.value.files.contains { $0.fileId == fid } }),
              let f = c.files.first(where: { $0.fileId == fid }), let h = f.handle else { throw ProtocolError("chunk for unknown file") }
        guard f.got + Int64(data.count) <= f.size else { return try await cancelIncoming(id, "more data than announced") }
        try h.write(contentsOf: data)
        f.hasher.update(data: data)
        f.got += Int64(data.count)
        let got = c.files.reduce(0) { $0 + $1.got }
        engine.progress("\(peerId)/\(id)", name: f.name, peer: peerId, incoming: true, done: got, total: c.files.reduce(0) { $0 + $1.size })
    }

    func done(_ m: JSON) async throws {
        guard let id = m["id"] as? String, let c = incoming[id], c.accepted,
              let fid = (m["fileId"] as? String).flatMap(UUID.init(uuidString:)),
              let f = c.files.first(where: { $0.fileId == fid }), f.final == nil else { return }
        try? f.handle?.close()
        f.handle = nil
        let sha = Data(f.hasher.finalize())
        guard f.got == f.size, sha.hex == (m["sha256"] as? String)?.lowercased() else { return try await cancelIncoming(id, "hash mismatch") }
        let fm = FileManager.default
        try fm.createDirectory(at: engine.receivedDir, withIntermediateDirectories: true)
        let dest = Self.unique(engine.receivedDir, f.name)
        try fm.moveItem(at: f.tmp, to: dest)
        f.final = dest
        f.sha = sha
        guard c.files.allSatisfy({ $0.final != nil }) else { return }
        incoming[id] = nil
        engine.endTransfer("\(peerId)/\(id)")
        engine.received(Received(kind: c.kind, text: nil, files: c.files.compactMap(\.final), mime: c.files[0].mime,
                                 fingerprint: c.files[0].sha!), id: id, from: peerId)
    }

    /// §6.3: append " (1)", " (2)", … before the extension.
    static func unique(_ dir: URL, _ name: String) -> URL {
        let base = (name as NSString).deletingPathExtension, ext = (name as NSString).pathExtension
        var u = dir.appendingPathComponent(name)
        var n = 1
        while FileManager.default.fileExists(atPath: u.path) {
            u = dir.appendingPathComponent(ext.isEmpty ? "\(base) (\(n))" : "\(base) (\(n)).\(ext)")
            n += 1
        }
        return u
    }
}
