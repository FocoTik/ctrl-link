import AppKit
import CtrlLinkCore
import UserNotifications

/// UNUserNotificationCenter needs an app bundle; under `swift run` there is none, so notes are only logged.
@MainActor final class Notifier: NSObject, UNUserNotificationCenterDelegate {
    /// Settings > Notification sound. Off by default on the Mac (the design shows it off).
    static let soundKey = "notificationSound"
    let engine: CtrlLinkEngine
    let center: UNUserNotificationCenter? = Bundle.main.bundleIdentifier == nil ? nil : .current()

    init(engine: CtrlLinkEngine) {
        self.engine = engine
        super.init()
        engine.notify = { [weak self] in self?.post($0) }
        guard let center else { return }
        center.delegate = self
        center.setNotificationCategories([
            UNNotificationCategory(identifier: "offer", actions: [
                UNNotificationAction(identifier: "accept", title: "Accept"),
                UNNotificationAction(identifier: "decline", title: "Decline", options: .destructive),
            ], intentIdentifiers: []),
            UNNotificationCategory(identifier: "files", actions: [UNNotificationAction(identifier: "show", title: "Show in Finder")],
                                   intentIdentifiers: []),
        ])
        center.requestAuthorization(options: [.alert, .sound]) { _, _ in }
    }

    func post(_ note: Note) {
        let c = UNMutableNotificationContent()
        switch note {
        case .connected(let n): c.title = "\(n) connected"
        case .disconnected(let n): c.title = "\(n) disconnected"
        case .clip(let n, let kind, let preview):
            switch kind {
            case .text: c.title = "Text from \(n)" // Mac Notification (75:3716): title over the preview
            case .url: c.title = "Link from \(n)"
            case .image: c.title = "Image from \(n)"
            case .files: c.title = "Files from \(n)"; c.categoryIdentifier = "files"
            }
            c.body = String(preview.prefix(200))
        case .offer(let o):
            c.title = "\(o.peerName) wants to send \(o.count == 1 ? "a file" : "\(o.count) files")"
            c.body = ByteCountFormatter.string(fromByteCount: o.size, countStyle: .file)
            c.categoryIdentifier = "offer"
            c.userInfo = ["id": o.id]
        case .unpaired(let n): c.title = "\(n) removed this pairing"
        case .unknown(let n): c.title = "\(n) no longer knows this Mac"; c.body = "Unpair it in Settings and pair again"
        case .version(let n): c.title = "Update Ctrl Link on \(n)"
        case .failed(let n): c.title = "Transfer from \(n) failed"
        }
        c.sound = UserDefaults.standard.bool(forKey: Self.soundKey) ? .default : nil
        guard let center else { return NSLog("Ctrl Link note: %@ %@", c.title, c.body) }
        center.add(UNNotificationRequest(identifier: UUID().uuidString, content: c, trigger: nil))
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                            withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        let sound = notification.request.content.sound != nil
        completionHandler(sound ? [.banner, .list, .sound] : [.banner, .list])
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                            withCompletionHandler completionHandler: @escaping () -> Void) {
        let action = response.actionIdentifier
        let category = response.notification.request.content.categoryIdentifier
        let id = response.notification.request.content.userInfo["id"] as? String
        Task { @MainActor in
            switch (category, action) {
            case ("offer", "accept"): id.map(engine.acceptOffer)
            case ("offer", "decline"): id.map(engine.rejectOffer)
            case ("files", _): NSWorkspace.shared.open(engine.receivedDir)
            default: break
            }
            completionHandler()
        }
    }
}
