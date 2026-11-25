import Combine
import CryptoKit
import Foundation
import Network
import UniformTypeIdentifiers

public enum ClipKind: String, Codable { case text, url, image, files }

public struct ClipRecord: Identifiable, Codable {
    public let id: String
    public let kind: ClipKind
    public let from: String? // nil = this Mac
    public let date: Date
    public let text: String?
    public let files: [URL]

    public init(id: String, kind: ClipKind, from: String?, date: Date = Date(), text: String?, files: [URL]) {
        self.id = id; self.kind = kind; self.from = from; self.date = date; self.text = text; self.files = files
    }
    public var preview: String { text ?? files.map(\.lastPathComponent).joined(separator: ", ") }
}

/// Something that arrived from a peer and should go on the pasteboard.
public struct Received {
    public let kind: ClipKind
    public let text: String?
    public let files: [URL]
    public let mime: String?
    public let fingerprint: Data // §6.4: SHA-256 of the text or of the first file
}

public enum Outgoing { case text(String), url(String), image(URL), files([URL]) }

public struct PeerStatus: Identifiable {
    public let id: String, name: String, pf: String
    public let online: Bool
    public let lastSeen: Date?
    public var ip: String? = nil // LAN address of the live session, for the Devices graph
}

public struct Nearby { public let endpoint: NWEndpoint, name: String, pf: String, pairing: Bool }

public struct PairWindow {
    public let code: String, uri: String, expires: Date
    public var request: (name: String, sas: String)?
}

public enum JoinState: Equatable { case idle, connecting, confirm(sas: String, host: String), done(String), failed(String) }

public struct SpeedSample: Equatable {
    public let progress: Double       // 0...1 when the sample was taken
    public let bytesPerSecond: Double
}

public struct Transfer: Identifiable {
    public let id: String, name: String, peer: String, incoming: Bool
    public var done: Int64, total: Int64
    public let started: Date
    /// Bytes per second, one sample a second since the transfer started (kept by the engine, so the graph survives
    /// switching tabs or closing the popover).
    public var speeds: [SpeedSample] = []

    public init(id: String, name: String, peer: String, incoming: Bool, done: Int64, total: Int64, started: Date = Date()) {
        self.id = id; self.name = name; self.peer = peer; self.incoming = incoming; self.done = done; self.total = total; self.started = started
    }
}

public struct Offer: Identifiable {
    public let id: String, peerId: String, peerName: String, count: Int, size: Int64
}

public enum Note {
    case connected(String), disconnected(String), clip(String, ClipKind, String), offer(Offer)
    case unpaired(String), unknown(String), version(String), failed(String)
}

@MainActor public final class CtrlLinkEngine: ObservableObject {
    public static let serviceType = "_ctrllink._tcp"
    public static let appVersion = "1.0.1"

    @Published public private(set) var pairings: [Pairing]
    @Published public private(set) var onlineIds: Set<String> = []
    @Published public private(set) var lastSeen: [String: Date] = [:]
    @Published public private(set) var clips: [ClipRecord] = []
    @Published public private(set) var nearby: [String: Nearby] = [:]
    @Published public internal(set) var pairingWindow: PairWindow?
    @Published public var joinState = JoinState.idle
    @Published public internal(set) var transfers: [Transfer] = []
    @Published public internal(set) var offers: [Offer] = []
    @Published public private(set) var port: UInt16?

    public var deviceId: String { data.deviceId }
    public var deviceName: String {
        get { data.deviceName }
        set { data.deviceName = newValue; save(); updateService() }
    }
    public var autoSend: Bool {
        get { data.autoSend }
        set { data.autoSend = newValue; save() }
    }
    public var autoAcceptLimit: Int64 {
        get { data.autoAcceptLimit }
        set { data.autoAcceptLimit = newValue; save() }
    }

    public var onReceive: ((Received) -> Void)?
    public var notify: ((Note) -> Void)?
    /// §5.1: the higher deviceId waits this long before dialing anyway (tests shorten it).
    public var retryDelay: Double = 10

    public let receivedDir: URL
    let store: Store
    var data: StoreData { willSet { objectWillChange.send() } }
    let advertise: Bool
    let wantedPort: UInt16
    var listener: NWListener?
    var browser: NWBrowser?
    var pathMonitor: NWPathMonitor?
    var sessions: [String: Session] = [:]
    var dialing: Set<String> = []
    var refused: Set<String> = [] // peers that answered "unknown"; not redialed until re-paired
    var pairedEndpoints: [String: NWEndpoint] = [:] // learned while joining, used when mDNS is not available
    var hostWindow: HostWindow?
    let history: HistoryFile

    public init(storeURL: URL, receivedDir: URL, advertise: Bool = true, port: UInt16 = 47823) {
        store = Store(url: storeURL)
        data = store.load()
        pairings = data.pairings
        history = HistoryFile(url: storeURL.deletingLastPathComponent().appendingPathComponent("history.json"))
        clips = history.load()
        self.receivedDir = receivedDir
        self.advertise = advertise
        wantedPort = port
    }

    public static func defaultStoreURL() -> URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Ctrl Link/state.json")
    }

    public static func defaultReceivedDir() -> URL {
        FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0].appendingPathComponent("Ctrl Link")
    }

    public var peers: [PeerStatus] {
        pairings.map { PeerStatus(id: $0.id, name: $0.name, pf: $0.pf, online: onlineIds.contains($0.id), lastSeen: lastSeen[$0.id],
                                  ip: sessions[$0.id].flatMap { Self.address($0.conn.nw.currentPath?.remoteEndpoint ?? $0.conn.nw.endpoint) }) }
    }

    static func address(_ ep: NWEndpoint) -> String? {
        guard case .hostPort(let host, _) = ep else { return nil }
        var s = "\(host)"
        if let i = s.firstIndex(of: "%") { s = String(s[..<i]) } // drop the IPv6 scope (%en0)
        if s.hasPrefix("::ffff:") { s.removeFirst(7) }
        // A link-local IPv6 address (fe80::…) means nothing to people; show the platform instead.
        if s.lowercased().hasPrefix("fe80:") { return nil }
        return s.lowercased()
    }

    /// This Mac's IPv4 address on the LAN (en0 first), or nil when offline.
    public static func localIPv4() -> String? {
        var head: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&head) == 0 else { return nil }
        defer { freeifaddrs(head) }
        var found: [String: String] = [:]
        var p = head
        while let a = p?.pointee {
            defer { p = a.ifa_next }
            guard let sa = a.ifa_addr, sa.pointee.sa_family == UInt8(AF_INET), a.ifa_flags & UInt32(IFF_LOOPBACK) == 0,
                  a.ifa_flags & UInt32(IFF_UP) != 0 else { continue }
            var buf = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            guard getnameinfo(sa, socklen_t(sa.pointee.sa_len), &buf, socklen_t(buf.count), nil, 0, NI_NUMERICHOST) == 0 else { continue }
            let ip = String(cString: buf)
            if !ip.hasPrefix("169.254.") { found[String(cString: a.ifa_name)] = ip }
        }
        // Wi-Fi/Ethernet (en*) before bridges, VPN tunnels and VM interfaces.
        let ordered = found.sorted { ($0.key.hasPrefix("en") ? 0 : 1, $0.key) < ($1.key.hasPrefix("en") ? 0 : 1, $1.key) }
        return found["en0"] ?? found["en1"] ?? ordered.first?.value
    }

    func save() {
        data.pairings = pairings
        store.save(data)
    }

    func log(_ s: String) { NSLog("Ctrl Link: %@", s) }

    // MARK: lifecycle

    public func start() {
        startListener(port: NWEndpoint.Port(rawValue: wantedPort) ?? .any)
        guard advertise else { return }
        startBrowser()
        let m = NWPathMonitor()
        var last: NWPath?
        m.pathUpdateHandler = { [weak self] path in
            MainActor.assumeIsolated {
                defer { last = path }
                guard let self, path.status == .satisfied, last != nil, path != last else { return }
                self.log("network changed, re-browsing")
                self.startBrowser()
                if self.listener == nil { self.startListener(port: .any) }
                self.sessions.values.forEach { $0.probe() }
            }
        }
        m.start(queue: .main)
        pathMonitor = m
    }

    func startListener(port: NWEndpoint.Port) {
        listener?.cancel()
        guard let l = try? NWListener(using: .tcp, on: port) else { return log("listener init failed") }
        listener = l
        l.newConnectionHandler = { [weak self] c in MainActor.assumeIsolated { self?.accept(c) } }
        l.stateUpdateHandler = { [weak self] st in
            MainActor.assumeIsolated {
                guard let self, self.listener === l else { return }
                switch st {
                case .ready: self.port = l.port?.rawValue
                case .failed(let e):
                    self.log("listener failed on \(port): \(e)")
                    self.listener = nil
                    if port != .any { self.startListener(port: .any) } // §2: 47823 busy → any free port
                default: break
                }
            }
        }
        if advertise { l.service = service() }
        l.start(queue: .main)
    }

    func service() -> NWListener.Service {
        let txt = NWTXTRecord(["v": "1", "id": deviceId, "pf": "mac", "pair": hostWindow == nil ? "0" : "1"])
        return NWListener.Service(name: deviceName, type: Self.serviceType, domain: nil, txtRecord: txt)
    }

    func updateService() { if advertise { listener?.service = service() } }

    func startBrowser() {
        browser?.cancel()
        let b = NWBrowser(for: .bonjourWithTXTRecord(type: Self.serviceType, domain: nil), using: .tcp)
        b.browseResultsChangedHandler = { [weak self] results, _ in MainActor.assumeIsolated { self?.browsed(results) } }
        b.start(queue: .main)
        browser = b
    }

    func browsed(_ results: Set<NWBrowser.Result>) {
        var found: [String: Nearby] = [:]
        for r in results {
            guard case .bonjour(let txt) = r.metadata, let id = txt["id"], id != deviceId else { continue }
            var name = id
            if case .service(let n, _, _, _) = r.endpoint { name = n }
            found[id] = Nearby(endpoint: r.endpoint, name: name, pf: platform(txt["pf"]), pairing: txt["pair"] == "1")
        }
        nearby = found
        found.keys.forEach { considerDial($0) }
    }

    // MARK: connections

    func watchdog(_ fc: FrameConn, _ seconds: Double) -> Task<Void, Never> {
        Task { try? await Task.sleep(for: .seconds(seconds)); if !Task.isCancelled { fc.close() } }
    }

    func accept(_ c: NWConnection) {
        let fc = FrameConn(c)
        Task {
            let wd = watchdog(fc, 100) // covers the 60 s pairing accept wait
            defer { wd.cancel() }
            do {
                try await fc.start()
                let m = try await fc.readJSON()
                if (m["v"] as? Int ?? 1) > 1 {
                    try? await fc.writeJSON(["t": "error", "reason": "version"])
                    return fc.close()
                }
                switch m["t"] as? String {
                case "pair_req": try await hostPair(fc, m)
                case "hello": try await serverHandshake(fc, m)
                default: fc.close()
                }
            } catch {
                log("incoming connection: \(error)")
                fc.close()
            }
        }
    }

    func endpoint(for id: String) -> NWEndpoint? { nearby[id]?.endpoint ?? pairedEndpoints[id] }

    /// §5.1 tie-break: lower deviceId dials now, the higher one after `retryDelay`. `now`: right after joining (§4.2).
    func considerDial(_ id: String, now: Bool = false) {
        guard pairings.contains(where: { $0.id == id }), sessions[id] == nil, !dialing.contains(id), !refused.contains(id), endpoint(for: id) != nil else { return }
        dialing.insert(id)
        let delay = now || deviceId < id ? 0 : retryDelay
        Task {
            if delay > 0 { try? await Task.sleep(for: .seconds(delay)) }
            guard sessions[id] == nil, let ep = endpoint(for: id) else { dialing.remove(id); return }
            do {
                try await dial(id, ep)
                dialing.remove(id)
            } catch {
                log("dial \(id): \(error)")
                if nearby[id] == nil { pairedEndpoints[id] = nil } else { try? await Task.sleep(for: .seconds(retryDelay)) }
                dialing.remove(id)
                considerDial(id)
            }
        }
    }

    func dial(_ id: String, _ ep: NWEndpoint) async throws {
        guard let p = pairings.first(where: { $0.id == id }) else { return }
        let fc = FrameConn(NWConnection(to: ep, using: .tcp))
        let wd = watchdog(fc, 20)
        defer { wd.cancel() }
        do {
            try await fc.start()
            let cn = PCrypto.random(32)
            try await fc.writeJSON(["t": "hello", "v": 1, "id": deviceId, "nonce": cn.base64EncodedString()])
            let r = try await fc.readJSON()
            if r["t"] as? String == "unknown" {
                refused.insert(id)
                notify?(.unknown(p.name))
                throw ProtocolError("peer does not know us")
            }
            if (r["v"] as? Int ?? 1) > 1 || r["t"] as? String == "error" {
                notify?(.version(p.name))
                throw ProtocolError("version")
            }
            guard r["t"] as? String == "hello", r["id"] as? String == id,
                  let sn = (r["nonce"] as? String).flatMap({ Data(base64Encoded: $0) }), sn.count == 32
            else { throw ProtocolError("bad hello") }
            let k = PCrypto.sessionKeys(pairKey: p.key, clientNonce: cn, serverNonce: sn)
            fc.setKeys(send: k.c2s, sendDir: 1, recv: k.s2c, recvDir: 2)
            try await establish(fc, peer: id, isClient: true)
        } catch {
            fc.close()
            throw error
        }
    }

    func serverHandshake(_ fc: FrameConn, _ m: JSON) async throws {
        guard let id = m["id"] as? String, let p = pairings.first(where: { $0.id == id }) else {
            try? await fc.writeJSON(["t": "unknown"])
            return fc.close()
        }
        guard let cn = (m["nonce"] as? String).flatMap({ Data(base64Encoded: $0) }), cn.count == 32 else { throw ProtocolError("bad hello") }
        let sn = PCrypto.random(32)
        try await fc.writeJSON(["t": "hello", "v": 1, "id": deviceId, "nonce": sn.base64EncodedString()])
        let k = PCrypto.sessionKeys(pairKey: p.key, clientNonce: cn, serverNonce: sn)
        fc.setKeys(send: k.s2c, sendDir: 2, recv: k.c2s, recvDir: 1)
        try await establish(fc, peer: id, isClient: false)
    }

    /// Exchanges `ready` (proves both hold pairKey), then registers the session.
    func establish(_ fc: FrameConn, peer id: String, isClient: Bool) async throws {
        try await fc.writeSealed(control(["t": "ready", "name": deviceName, "pf": "mac", "app": Self.appVersion]))
        let first = try await fc.readSealed()
        guard first.first == 1, let r = parseJSON(first.dropFirst()), r["t"] as? String == "ready" else { throw ProtocolError("expected ready") }
        guard let i = pairings.firstIndex(where: { $0.id == id }) else { throw ProtocolError("unpaired meanwhile") }
        if let n = r["name"] as? String, let pf = r["pf"] as? String, pairings[i].name != n || pairings[i].pf != platform(pf) {
            pairings[i].name = n; pairings[i].pf = platform(pf); save()
        }
        let s = Session(engine: self, conn: fc, peerId: id, isClient: isClient)
        if let old = sessions[id] {
            // §5.1: keep the session whose client has the lower deviceId (same client → the newer one).
            let client = { (x: Session) in x.isClient ? self.deviceId : id }
            if client(s) > client(old) { return fc.close() }
            old.close()
        }
        let wasOnline = onlineIds.contains(id)
        sessions[id] = s
        onlineIds.insert(id)
        lastSeen[id] = Date()
        if !wasOnline { notify?(.connected(pairings[i].name)) }
        log("session with \(pairings[i].name) (\(isClient ? "client" : "server"))")
        Task { await s.run() }
    }

    func sessionEnded(_ s: Session) {
        guard sessions[s.peerId] === s else { return }
        sessions[s.peerId] = nil
        onlineIds.remove(s.peerId)
        lastSeen[s.peerId] = Date()
        if Date().timeIntervalSince(s.started) > 10 { notify?(.disconnected(name(of: s.peerId))) }
        Task {
            try? await Task.sleep(for: .seconds(1))
            considerDial(s.peerId)
        }
    }

    func name(of id: String) -> String { pairings.first { $0.id == id }?.name ?? id }

    func touched(_ id: String) { lastSeen[id] = Date() }

    // MARK: pairings

    func addPairing(_ p: Pairing) {
        pairings.removeAll { $0.id == p.id }
        pairings.append(p)
        refused.remove(p.id)
        save()
    }

    public func unpair(_ id: String) {
        if let s = sessions[id] {
            Task {
                try? await s.send(["t": "unpair"])
                s.close()
            }
        }
        removePairing(id)
    }

    func removePairing(_ id: String) {
        pairings.removeAll { $0.id == id }
        pairedEndpoints[id] = nil
        save()
    }

    // MARK: clips

    public func send(_ o: Outgoing) {
        let id = UUID().uuidString.lowercased()
        var m: JSON = ["t": "clip", "id": id, "ts": Int64(Date().timeIntervalSince1970 * 1000)]
        var urls: [URL] = []
        var text: String?
        switch o {
        case .text(let s), .url(let s):
            if case .url = o { m["kind"] = "url" } else { m["kind"] = "text" }
            m["text"] = s
            // §6.1: inline text max 192 KiB UTF-8, else one .txt file. The frame check only catches heavily escaped text.
            let size = (try? JSONSerialization.data(withJSONObject: m, options: .withoutEscapingSlashes).count) ?? .max
            if s.utf8.count > 192 * 1024 || size + 1 + 16 > FrameConn.maxFrame {
                let u = FileManager.default.temporaryDirectory.appendingPathComponent("CtrlLink-\(id)/Clipboard.txt")
                try? FileManager.default.createDirectory(at: u.deletingLastPathComponent(), withIntermediateDirectories: true)
                guard (try? s.write(to: u, atomically: true, encoding: .utf8)) != nil else { return }
                m["text"] = nil
                m["kind"] = "files"
                urls = [u]
            } else { text = s }
        case .image(let u): m["kind"] = "image"; urls = [u]
        case .files(let us): m["kind"] = "files"; urls = us
        }
        var files: [OutFile] = []
        for u in urls {
            guard let size = (try? u.resourceValues(forKeys: [.fileSizeKey]))?.fileSize else { continue }
            files.append(OutFile(fileId: UUID(), url: u, size: Int64(size)))
        }
        if !urls.isEmpty {
            guard !files.isEmpty else { return }
            m["files"] = files.map { f -> JSON in
                ["fileId": f.fileId.uuidString.lowercased(), "name": f.url.lastPathComponent, "mime": Self.mime(f.url), "size": f.size]
            }
        }
        record(ClipRecord(id: id, kind: ClipKind(rawValue: m["kind"] as! String)!, from: nil, text: text, files: urls))
        for s in sessions.values { s.offer(m, files) }
    }

    static func mime(_ u: URL) -> String { UTType(filenameExtension: u.pathExtension)?.preferredMIMEType ?? "application/octet-stream" }

    func record(_ c: ClipRecord) {
        // Kept on disk for the History list: 14 days, at most 300 items, text capped at 32 KB per item.
        // Password-manager copies never get here (Pasteboard skips concealed/transient items).
        var kept = c
        if let t = c.text, t.utf8.count > 32 * 1024 {
            kept = ClipRecord(id: c.id, kind: c.kind, from: c.from, date: c.date, text: String(t.prefix(32 * 1024)), files: c.files)
        }
        clips.insert(kept, at: 0)
        let oldest = Date().addingTimeInterval(-14 * 86400)
        clips = Array(clips.prefix { $0.date >= oldest }.prefix(300))
        history.save(clips)
        var d = data.daily ?? [:]
        d[Self.dayKey(c.date), default: [:]][c.kind.rawValue, default: 0] += 1
        let cutoff = Self.dayKey(Date().addingTimeInterval(-14 * 86400))
        data.daily = d.filter { $0.key >= cutoff }
        save()
    }

    /// Settings > Clear history: forgets the recent clips and the daily counts, and deletes the temporary copies of
    /// copied images (the History thumbnails). Files received into the Downloads folder are left alone.
    public func clearHistory() {
        let tmp = FileManager.default.temporaryDirectory.standardizedFileURL.path
        // Images a peer hasn't fetched yet (offer waiting for Accept, or mid-transfer) must stay readable.
        let pending = Set(sessions.values.flatMap { $0.outgoing.values.flatMap { $0.files.map { $0.url.standardizedFileURL.path } } })
        for c in clips where c.from == nil && c.kind == .image {
            for u in c.files where u.standardizedFileURL.path.hasPrefix(tmp) && !pending.contains(u.standardizedFileURL.path) {
                try? FileManager.default.removeItem(at: u.deletingLastPathComponent())
            }
        }
        clips = []
        history.save([])
        data.daily = nil
        save()
    }

    /// Stops a running transfer (key "peerId/clipId", as in `transfers`) and tells the peer (§6.2 cancel).
    public func cancelTransfer(_ key: String) {
        let parts = key.split(separator: "/", maxSplits: 1).map(String.init)
        guard parts.count == 2, let s = sessions[parts[0]] else { return endTransfer(key) }
        let id = parts[1]
        if let c = s.outgoing[id] { c.cancelled = true; s.outgoing[id] = nil }
        s.dropIncoming(id)
        Task { try? await s.send(["t": "cancel", "id": id]) }
        endTransfer(key)
    }

    /// Clip counts by kind for one local day.
    public func counts(on day: Date) -> [ClipKind: Int] {
        var out: [ClipKind: Int] = [:]
        for (k, n) in data.daily?[Self.dayKey(day)] ?? [:] { if let kind = ClipKind(rawValue: k) { out[kind] = n } }
        return out
    }

    static func dayKey(_ d: Date) -> String {
        let c = Calendar.current.dateComponents([.year, .month, .day], from: d)
        return String(format: "%04d-%02d-%02d", c.year!, c.month!, c.day!)
    }

    func received(_ r: Received, id: String, from peerId: String) {
        let name = name(of: peerId)
        let c = ClipRecord(id: id, kind: r.kind, from: name, text: r.text, files: r.files)
        record(c)
        onReceive?(r)
        notify?(.clip(name, r.kind, c.preview))
    }

    public func acceptOffer(_ id: String) {
        guard let o = offers.first(where: { $0.id == id }) else { return }
        offers.removeAll { $0.id == id }
        sessions[o.peerId]?.accept(id)
    }

    public func rejectOffer(_ id: String) {
        guard let o = offers.first(where: { $0.id == id }) else { return }
        offers.removeAll { $0.id == id }
        sessions[o.peerId]?.reject(id)
    }

    /// Last time each transfer row was republished; chunks arrive hundreds of times a second on a LAN.
    private var lastProgress: [String: Date] = [:]

    func progress(_ key: String, name: String, peer: String, incoming: Bool, done: Int64, total: Int64) {
        if let i = transfers.firstIndex(where: { $0.id == key }) {
            let now = Date()
            guard done >= total || now.timeIntervalSince(lastProgress[key] ?? .distantPast) >= 0.1 else { return }
            lastProgress[key] = now
            transfers[i].done = done
            sampleSpeeds(now)
        } else {
            transfers.append(Transfer(id: key, name: name, peer: self.name(of: peer), incoming: incoming, done: done, total: total))
        }
    }

    func endTransfer(_ key: String) {
        transfers.removeAll { $0.id == key }; lastProgress[key] = nil; sampledDone[key] = nil
    }

    private var sampledDone: [String: (at: Date, done: Int64)] = [:]
    /// Adds one bytes-per-second sample to each running transfer whenever a second has passed since its last sample.
    private func sampleSpeeds(_ now: Date) {
        for i in transfers.indices {
            let t = transfers[i]
            guard let last = sampledDone[t.id] else { sampledDone[t.id] = (t.started, 0); continue }
            let dt = now.timeIntervalSince(last.at)
            guard dt >= 1 else { continue }
            transfers[i].speeds.append(SpeedSample(progress: Double(t.done) / Double(max(t.total, 1)),
                                                   bytesPerSecond: Double(max(t.done - last.done, 0)) / dt))
            if transfers[i].speeds.count > 600 { transfers[i].speeds.removeFirst(transfers[i].speeds.count - 600) }
            sampledDone[t.id] = (now, t.done)
        }
    }
}

func control(_ m: JSON) -> Data {
    Data([1]) + ((try? JSONSerialization.data(withJSONObject: m, options: .withoutEscapingSlashes)) ?? Data("{}".utf8))
}

/// §1: unknown platform values count as `other`.
func platform(_ s: String?) -> String { ["mac", "android", "windows"].contains(s ?? "") ? s! : "other" }

func parseJSON(_ d: Data) -> JSON? { (try? JSONSerialization.jsonObject(with: Data(d))) as? JSON }

/// History list on disk (separate from state.json so the pairing keys aren't rewritten on every copy). 0600, like the store.
struct HistoryFile {
    let url: URL
    func load() -> [ClipRecord] {
        guard let d = try? Data(contentsOf: url) else { return [] }
        let dec = JSONDecoder(); dec.dateDecodingStrategy = .secondsSince1970
        return (try? dec.decode([ClipRecord].self, from: d)) ?? []
    }
    func save(_ clips: [ClipRecord]) {
        let fm = FileManager.default
        try? fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let enc = JSONEncoder(); enc.dateEncodingStrategy = .secondsSince1970
        guard let d = try? enc.encode(clips) else { return }
        if !fm.fileExists(atPath: url.path) { fm.createFile(atPath: url.path, contents: nil, attributes: [.posixPermissions: 0o600]) }
        try? d.write(to: url)
        try? fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }
}
