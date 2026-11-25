import Foundation

public struct Pairing: Codable, Identifiable, Equatable {
    public let id: String
    public var name: String
    public var pf: String
    public let key: Data
}

/// Identity, pairings and settings. Kept in a 0600 JSON file: the Keychain prompts repeatedly for ad-hoc signed builds.
struct StoreData: Codable {
    var deviceId = UUID().uuidString.lowercased()
    var deviceName = Host.current().localizedName ?? "Mac"
    var pairings: [Pairing] = []
    var autoSend = true
    var autoAcceptLimit: Int64 = 100_000_000
    var daily: [String: [String: Int]]? // "yyyy-MM-dd" → kind → clip count, for the History chart (no content stored)
}

struct Store {
    let url: URL

    func load() -> StoreData {
        if let d = try? Data(contentsOf: url), let s = try? JSONDecoder().decode(StoreData.self, from: d) { return s }
        if FileManager.default.fileExists(atPath: url.path) { // unreadable: keep it aside instead of overwriting the identity
            try? FileManager.default.moveItem(at: url, to: url.appendingPathExtension("bad-\(Int(Date().timeIntervalSince1970))"))
        }
        let s = StoreData()
        save(s)
        return s
    }

    func save(_ s: StoreData) {
        let fm = FileManager.default
        try? fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        guard let d = try? JSONEncoder().encode(s) else { return }
        if !fm.fileExists(atPath: url.path) { fm.createFile(atPath: url.path, contents: nil, attributes: [.posixPermissions: 0o600]) }
        do { try d.write(to: url) } catch { NSLog("Ctrl Link: cannot save store: \(error)") }
        try? fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }
}
