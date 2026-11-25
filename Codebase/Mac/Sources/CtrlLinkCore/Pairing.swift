import CryptoKit
import Foundation
import Network

final class HostWindow {
    let priv = Curve25519.KeyAgreement.PrivateKey()
    let token = PCrypto.random(16)
    let code = String(format: "%06u", UInt32.random(in: 0...999_999))
    var failures = 0
    var busy = false
    var decision: CheckedContinuation<Bool?, Never>?
}

/// §4 pairing, host and joiner sides.
extension CtrlLinkEngine {
    // MARK: host

    public func openPairingWindow() {
        closePairingWindow()
        let w = HostWindow()
        hostWindow = w
        let expires = Date().addingTimeInterval(300)
        pairingWindow = PairWindow(code: w.code, uri: pairingURI(w), expires: expires)
        updateService()
        Task {
            try? await Task.sleep(for: .seconds(300))
            if hostWindow === w { closePairingWindow() }
        }
    }

    public func closePairingWindow() {
        guard let w = hostWindow else { return }
        w.decision?.resume(returning: nil)
        w.decision = nil
        hostWindow = nil
        pairingWindow = nil
        updateService()
    }

    /// The user's answer to "<name> wants to pair — 1234" (code mode).
    public func respondToPairRequest(_ accept: Bool) {
        hostWindow?.decision?.resume(returning: accept)
        hostWindow?.decision = nil
        pairingWindow?.request = nil
    }

    func pairingURI(_ w: HostWindow) -> String {
        let unreserved = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~")
        let name = deviceName.addingPercentEncoding(withAllowedCharacters: unreserved) ?? ""
        let hosts = Self.lanIPv4().joined(separator: ",")
        return "ctrllink://pair?v=1&id=\(deviceId)&n=\(name)&pf=mac&h=\(hosts)&p=\(port ?? wantedPort)"
            + "&k=\(w.priv.publicKey.rawRepresentation.b64url)&t=\(w.token.b64url)"
    }

    static func lanIPv4() -> [String] {
        var out: [String] = []
        var ifap: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifap) == 0 else { return [] }
        defer { freeifaddrs(ifap) }
        var p = ifap
        while let i = p?.pointee {
            defer { p = i.ifa_next }
            let flags = Int32(i.ifa_flags)
            guard let a = i.ifa_addr, a.pointee.sa_family == UInt8(AF_INET), flags & IFF_UP != 0, flags & IFF_LOOPBACK == 0 else { continue }
            var buf = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            if getnameinfo(a, socklen_t(a.pointee.sa_len), &buf, socklen_t(buf.count), nil, 0, NI_NUMERICHOST) == 0 {
                let s = String(cString: buf)
                if !s.hasPrefix("169.254."), !out.contains(s) { out.append(s) }
            }
        }
        return out
    }

    func hostPair(_ fc: FrameConn, _ m: JSON) async throws {
        guard let w = hostWindow, !w.busy else {
            try? await fc.writeJSON(["t": "pair_fail", "reason": hostWindow == nil ? "closed" : "busy"])
            return fc.close()
        }
        guard let mode = m["mode"] as? String, mode == "qr" || mode == "code",
              let jid = m["id"] as? String, let jname = m["name"] as? String,
              let jpub = (m["pub"] as? String).flatMap({ Data(base64Encoded: $0) }), jpub.count == 32
        else { throw ProtocolError("bad pair_req") }
        w.busy = true
        defer { w.busy = false; fc.close() }
        let hostPub = w.priv.publicKey.rawRepresentation
        try await fc.writeJSON(["t": "pair_hello", "v": 1, "id": deviceId, "name": deviceName, "pf": "mac", "pub": hostPub.base64EncodedString()])
        let keys = PCrypto.pairingKeys(shared: try PCrypto.shared(w.priv, jpub), secret: mode == "qr" ? w.token : Data(w.code.utf8),
                                       hostPub: hostPub, joinerPub: jpub)
        let c = try await fc.readJSON()
        guard c["t"] as? String == "pair_confirm", let mac = (c["mac"] as? String).flatMap({ Data(base64Encoded: $0) }),
              PCrypto.macValid(mac, key: keys.pairKey, message: Data("joiner".utf8) + hostPub + jpub)
        else {
            w.failures += 1
            if w.failures >= 5, hostWindow === w { closePairingWindow() }
            try? await fc.writeJSON(["t": "pair_fail", "reason": "bad_code"])
            return
        }
        if mode == "code" {
            pairingWindow?.request = (jname, keys.sas)
            let timer = Task {
                try? await Task.sleep(for: .seconds(60))
                if !Task.isCancelled { w.decision?.resume(returning: nil); w.decision = nil }
            }
            let answer: Bool? = await withCheckedContinuation { w.decision = $0 }
            timer.cancel()
            if hostWindow === w { pairingWindow?.request = nil }
            guard answer == true else {
                try? await fc.writeJSON(["t": "pair_fail", "reason": answer == nil ? (hostWindow === w ? "timeout" : "closed") : "rejected"])
                return
            }
        }
        addPairing(Pairing(id: jid, name: jname, pf: platform(m["pf"] as? String), key: keys.pairKey))
        try await fc.writeJSON(["t": "pair_ok", "mac": keys.hostMac.base64EncodedString()])
        if hostWindow === w { closePairingWindow() }
        log("paired with \(jname) (host, \(mode))")
    }

    // MARK: joiner

    enum JoinMode { case qr(token: Data, hostPub: Data, hostId: String), code(String) }

    /// Joins using a `ctrllink://pair?...` link: tries each `h` address, then mDNS by `id` (§4.1).
    @discardableResult
    public func join(uri: String) async throws -> Pairing {
        do {
            guard let c = URLComponents(string: uri.trimmingCharacters(in: .whitespacesAndNewlines)), c.scheme == "ctrllink", c.host == "pair"
            else { throw ProtocolError("Not a Ctrl Link pairing link") }
            let q = Dictionary((c.queryItems ?? []).map { ($0.name, $0.value ?? "") }, uniquingKeysWith: { a, _ in a })
            guard let id = q["id"], let k = q["k"].flatMap({ Data(b64url: $0) }), k.count == 32,
                  let t = q["t"].flatMap({ Data(b64url: $0) }), t.count == 16, let port = q["p"].flatMap({ NWEndpoint.Port($0) })
            else { throw ProtocolError("Pairing link is incomplete") }
            if (Int(q["v"] ?? "1") ?? 1) > 1 { throw ProtocolError("Update Ctrl Link on this Mac") }
            var eps: [NWEndpoint] = (q["h"] ?? "").split(separator: ",").map {
                .hostPort(host: .init(String($0).trimmingCharacters(in: CharacterSet(charactersIn: "[]"))), port: port)
            }
            if let n = nearby[id] { eps.append(n.endpoint) }
            var lastError: Error = ProtocolError("No address to reach \(q["n"] ?? "the host")")
            for ep in eps {
                do { return try await joinAt(ep, .qr(token: t, hostPub: k, hostId: id)) } catch let e as ConnectError { lastError = e.error }
            }
            throw lastError
        } catch {
            joinState = .failed(Self.describe(error))
            throw error
        }
    }

    /// Code mode over mDNS (§4.2): tries each `pair=1` host until one does not answer `bad_code`.
    @discardableResult
    public func join(code: String) async throws -> Pairing {
        let hosts = nearby.values.filter(\.pairing)
        guard !hosts.isEmpty else {
            joinState = .failed("No device is showing a pairing code")
            throw ProtocolError("no host")
        }
        var last: Error = ProtocolError("Wrong code")
        for h in hosts {
            do { return try await join(code: code, endpoint: h.endpoint) } catch let e as ProtocolError where e.description == "Wrong code" { last = e }
        }
        joinState = .failed(Self.describe(last))
        throw last
    }

    @discardableResult
    public func join(code: String, endpoint: NWEndpoint) async throws -> Pairing {
        do {
            guard code.count == 6, code.allSatisfy(\.isASCII), code.allSatisfy(\.isNumber) else { throw ProtocolError("The code has 6 digits") }
            return try await joinAt(endpoint, .code(code))
        } catch {
            joinState = .failed(Self.describe(error))
            throw error
        }
    }

    struct ConnectError: Error { let error: Error }

    static func describe(_ e: Error) -> String {
        switch (e as? ConnectError)?.error ?? e {
        case let p as ProtocolError: p.description
        default: "Could not reach the device"
        }
    }

    func joinAt(_ ep: NWEndpoint, _ mode: JoinMode) async throws -> Pairing {
        joinState = .connecting
        let priv = Curve25519.KeyAgreement.PrivateKey()
        let jpub = priv.publicKey.rawRepresentation
        let fc = FrameConn(NWConnection(to: ep, using: .tcp))
        let wd = watchdog(fc, 100)
        defer { wd.cancel(); fc.close() }
        do { try await fc.start() } catch { throw ConnectError(error: error) }
        let isQR = if case .qr = mode { true } else { false }
        try await fc.writeJSON(["t": "pair_req", "v": 1, "mode": isQR ? "qr" : "code", "id": deviceId, "name": deviceName, "pf": "mac",
                                "pub": jpub.base64EncodedString()])
        let h = try await fc.readJSON()
        try Self.checkFail(h)
        guard h["t"] as? String == "pair_hello", let hid = h["id"] as? String, let hname = h["name"] as? String,
              let hpub = (h["pub"] as? String).flatMap({ Data(base64Encoded: $0) }), hpub.count == 32
        else { throw ProtocolError("Unexpected reply from host") }
        if (h["v"] as? Int ?? 1) > 1 { throw ProtocolError("Update Ctrl Link on this Mac") }
        let secret: Data
        switch mode {
        case .qr(let token, let k, let id):
            guard hpub == k, hid == id else { throw ProtocolError("Host key does not match the QR code") }
            secret = token
        case .code(let code):
            secret = Data(code.utf8)
        }
        let keys = PCrypto.pairingKeys(shared: try PCrypto.shared(priv, hpub), secret: secret, hostPub: hpub, joinerPub: jpub)
        if !isQR { joinState = .confirm(sas: keys.sas, host: hname) }
        try await fc.writeJSON(["t": "pair_confirm", "mac": keys.joinerMac.base64EncodedString()])
        let r = try await fc.readJSON()
        try Self.checkFail(r)
        guard r["t"] as? String == "pair_ok", let mac = (r["mac"] as? String).flatMap({ Data(base64Encoded: $0) }),
              PCrypto.macValid(mac, key: keys.pairKey, message: Data("host".utf8) + hpub + jpub)
        else { throw ProtocolError("Host could not be verified") }
        let p = Pairing(id: hid, name: hname, pf: platform(h["pf"] as? String), key: keys.pairKey)
        addPairing(p)
        pairedEndpoints[hid] = ep
        joinState = .done(hname)
        log("paired with \(hname) (joiner)")
        considerDial(hid, now: true)
        return p
    }

    static func checkFail(_ m: JSON) throws {
        switch m["t"] as? String {
        case "pair_fail":
            let why = switch m["reason"] as? String {
            case "bad_code": "Wrong code"
            case "rejected": "Pairing was declined"
            case "timeout": "Pairing timed out"
            case "busy": "The other device is busy pairing. Try again"
            default: "Pairing window is closed"
            }
            throw ProtocolError(why)
        case "error": throw ProtocolError("Update Ctrl Link on the other device")
        default: break
        }
    }
}
