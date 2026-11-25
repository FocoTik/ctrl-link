import CtrlLinkCore
import SwiftUI

@main
struct CtrlLinkApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate

    var body: some Scene {
        MenuBarExtra {
            PopoverView()
                .environmentObject(delegate.model)
                .environmentObject(delegate.model.engine)
        } label: {
            MenuIcon(engine: delegate.model.engine)
        }
        .menuBarExtraStyle(.window)
    }
}

/// Owns the model so `ctrllink://pair?...` links opened anywhere (browser, Messages, `open`) start pairing,
/// the same deep link the Android app handles.
@MainActor final class AppDelegate: NSObject, NSApplicationDelegate {
    let model = AppModel()

    func application(_ application: NSApplication, open urls: [URL]) {
        for url in urls where url.scheme == "ctrllink" && url.host == "pair" {
            // A fresh install must land on Join (with its SAS), not Welcome.
            UserDefaults.standard.set(true, forKey: "welcomeSeen")
            model.tab = .devices
            model.route = .join
            Task { try? await model.engine.join(uri: url.absoluteString) }
        }
    }
}

struct MenuIcon: View {
    @ObservedObject var engine: CtrlLinkEngine
    var body: some View {
        Image(nsImage: engine.onlineIds.isEmpty ? Monogram.menuBarDimmed : Monogram.menuBarNormal)
            .accessibilityLabel(engine.onlineIds.isEmpty ? "Ctrl Link, no devices online" : "Ctrl Link, connected")
    }
}

enum Tab: String, CaseIterable { case devices = "Devices", history = "History", settings = "Settings" }
enum Route { case home, host, join }

@MainActor final class AppModel: ObservableObject {
    let engine: CtrlLinkEngine
    let bridge: PasteboardBridge
    let notifier: Notifier
    @Published var tab = Tab.devices
    @Published var route = Route.home
    @Published var pairedDevicesOpen = false // Settings > Paired devices sub page
    @Published var splashDone = false // the splash shows once per launch, while the engine starts

    init(engine: CtrlLinkEngine? = nil) {
        self.engine = engine ?? CtrlLinkEngine(storeURL: CtrlLinkEngine.defaultStoreURL(), receivedDir: CtrlLinkEngine.defaultReceivedDir())
        Theme.registerFont()
        bridge = PasteboardBridge(engine: self.engine)
        notifier = Notifier(engine: self.engine)
        self.engine.start()
        bridge.start()
    }

    /// Opens a pairing window and shows Pair host (from Devices, Welcome or Settings > Paired devices).
    func startPairing() {
        engine.openPairingWindow()
        pairedDevicesOpen = false
        tab = .devices
        route = .host
    }
}
