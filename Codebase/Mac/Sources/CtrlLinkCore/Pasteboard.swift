import AppKit
import CryptoKit
import UniformTypeIdentifiers

/// Watches NSPasteboard.general (§6.4) and writes received content to it (§6.3).
@MainActor public final class PasteboardBridge {
    let engine: CtrlLinkEngine
    let pb = NSPasteboard.general
    var changeCount: Int
    var timer: Timer?
    /// Fingerprints (and sizes, so local files are only hashed when a size matches) of content we wrote ourselves.
    var written: [(fp: Data, size: Int64)] = []

    public init(engine: CtrlLinkEngine) {
        self.engine = engine
        changeCount = NSPasteboard.general.changeCount
        engine.onReceive = { [weak self] r in self?.write(r) }
    }

    public func start() {
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.poll() }
        }
    }

    func poll() {
        guard pb.changeCount != changeCount else { return }
        changeCount = pb.changeCount
        guard engine.autoSend, let out = read() else { return }
        engine.send(out)
    }

    /// The round "send clipboard" button: send what is on the pasteboard now, even with auto-send off.
    public func sendNow() {
        changeCount = pb.changeCount
        if let out = read() { engine.send(out) }
    }

    func remember(_ fp: Data, _ size: Int64) {
        written.append((fp, size))
        if written.count > 20 { written.removeFirst() }
    }

    func isOurs(text: String) -> Bool {
        let fp = Data(SHA256.hash(data: Data(text.utf8)))
        return written.contains { $0.fp == fp }
    }

    func isOurs(file u: URL) -> Bool {
        guard let size = (try? u.resourceValues(forKeys: [.fileSizeKey]))?.fileSize.map(Int64.init),
              written.contains(where: { $0.size == size }), let fh = try? FileHandle(forReadingFrom: u) else { return false }
        defer { try? fh.close() }
        var h = SHA256()
        while let d = try? fh.read(upToCount: 1 << 20), !d.isEmpty { h.update(data: d) }
        let fp = Data(h.finalize())
        return written.contains { $0.fp == fp }
    }

    /// Maps the pasteboard to an outgoing clip: files, then image, then text/url.
    func read() -> Outgoing? {
        let types = pb.types ?? []
        // Password managers mark secrets; never sync them (nspasteboard.org).
        if types.contains(where: { ["org.nspasteboard.ConcealedType", "org.nspasteboard.TransientType"].contains($0.rawValue) }) { return nil }

        let urls = (pb.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? [])
            .filter { (try? $0.resourceValues(forKeys: [.isRegularFileKey]))?.isRegularFile == true } // shortcut: folders are skipped
        if !urls.isEmpty { return isOurs(file: urls[0]) ? nil : .files(urls) }

        let image: (Data, String)? = if let d = pb.data(forType: .png) { (d, "png") }
            else if let d = pb.data(forType: NSPasteboard.PasteboardType(UTType.jpeg.identifier)) { (d, "jpg") }
            else if let d = pb.data(forType: NSPasteboard.PasteboardType(UTType.heic.identifier)) { (d, "heic") }
            else if let t = pb.data(forType: .tiff), let png = NSBitmapImageRep(data: t)?.representation(using: .png, properties: [:]) { (png, "png") }
            else { nil }
        if let (d, ext) = image {
            let fp = Data(SHA256.hash(data: d))
            guard !written.contains(where: { $0.fp == fp }) else { return nil }
            let u = FileManager.default.temporaryDirectory.appendingPathComponent("CtrlLink-\(UUID().uuidString)/Image.\(ext)")
            try? FileManager.default.createDirectory(at: u.deletingLastPathComponent(), withIntermediateDirectories: true)
            guard (try? d.write(to: u)) != nil else { return nil }
            return .image(u)
        }

        guard let s = pb.string(forType: .string), !s.isEmpty, !isOurs(text: s) else { return nil }
        if s.hasPrefix("ctrllink://pair") { return nil } // pairing tokens stay on this Mac
        let t = s.trimmingCharacters(in: .whitespacesAndNewlines)
        if !t.contains(where: \.isWhitespace), let u = URL(string: t), ["http", "https"].contains(u.scheme?.lowercased()), u.host != nil {
            return .url(t)
        }
        return .text(s)
    }

    func write(_ r: Received) {
        switch r.kind {
        case .text, .url:
            guard let s = r.text else { return }
            pb.clearContents()
            pb.setString(s, forType: .string)
            if r.kind == .url { pb.setString(s, forType: .URL) }
            remember(r.fingerprint, Int64(s.utf8.count))
        case .image:
            guard let u = r.files.first, let d = try? Data(contentsOf: u) else { return }
            let type = UTType(mimeType: r.mime ?? "") ?? UTType(filenameExtension: u.pathExtension) ?? .png
            pb.clearContents()
            pb.setData(d, forType: NSPasteboard.PasteboardType(type.identifier))
            if type != .png && type != .tiff, let tiff = NSImage(data: d)?.tiffRepresentation { pb.setData(tiff, forType: .tiff) }
            remember(r.fingerprint, Int64(d.count))
        case .files:
            pb.clearContents()
            pb.writeObjects(r.files as [NSURL])
            if let size = (try? r.files[0].resourceValues(forKeys: [.fileSizeKey]))?.fileSize { remember(r.fingerprint, Int64(size)) }
        }
        changeCount = pb.changeCount
    }

    /// "Click to copy again" from the recent list. Registered as ours, so it is not sent back out.
    public func copy(_ c: ClipRecord) {
        let fp: Data
        if let t = c.text {
            fp = Data(SHA256.hash(data: Data(t.utf8)))
        } else if let u = c.files.first, let d = try? Data(contentsOf: u, options: .mappedIfSafe) {
            fp = Data(SHA256.hash(data: d))
        } else { return }
        write(Received(kind: c.kind, text: c.text, files: c.files, mime: nil, fingerprint: fp))
    }
}
