import CtrlLinkCore
import ServiceManagement
import SwiftUI

struct SettingsPage: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var engine: CtrlLinkEngine
    @AppStorage(Notifier.soundKey) private var sound = false
    @State private var name = ""
    @State private var loginError: String?
    @State private var confirmClear = false

    static let limits: [(String, Int64)] = [("25 MB", 25_000_000), ("100 MB", 100_000_000), ("500 MB", 500_000_000),
                                            ("1 GB", 1_000_000_000), ("Always ask", 0)]

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                Label11("This Mac", size: 12).padding(.bottom, 8)
                Row {
                    Text("Name")
                    Spacer()
                    TextField("Device name", text: $name).textFieldStyle(.plain).multilineTextAlignment(.trailing)
                        .foregroundStyle(Theme.secondary)
                        .onSubmit(commitName)
                }
                Row {
                    Text("Send copied items automatically")
                    Spacer()
                    Toggle("Send copied items automatically", isOn: Binding(get: { engine.autoSend }, set: { engine.autoSend = $0 }))
                        .toggleStyle(PillToggle())
                }
                Row {
                    Text("Accept files without asking up to")
                    Spacer()
                    Menu {
                        ForEach(Self.limits, id: \.1) { l in Button(l.0) { engine.autoAcceptLimit = l.1 } }
                    } label: {
                        Text(Self.limits.first { $0.1 == engine.autoAcceptLimit }?.0 ?? "100 MB").foregroundStyle(Theme.secondary)
                    }
                    .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
                    .accessibilityLabel("Accept files without asking up to")
                }
                Row {
                    Text("Open at login")
                    Spacer()
                    Toggle("Open at login", isOn: Binding(get: { SMAppService.mainApp.status == .enabled }, set: setLogin))
                        .toggleStyle(PillToggle())
                }
                if let loginError { Text(loginError).font(.system(size: 12)).foregroundStyle(Theme.danger).padding(.vertical, 8) }
                Row {
                    Text("Notification sound")
                    Spacer()
                    Toggle("Notification sound", isOn: $sound).toggleStyle(PillToggle())
                }
                Button {
                    try? FileManager.default.createDirectory(at: engine.receivedDir, withIntermediateDirectories: true)
                    NSWorkspace.shared.open(engine.receivedDir)
                } label: {
                    Row {
                        Text("Show received files")
                        Spacer()
                        Chevron()
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                Row {
                    Text("Clear history")
                    Spacer()
                    if confirmClear {
                        Button("Cancel") { confirmClear = false }.buttonStyle(PillButton())
                        Button("Clear") { engine.clearHistory(); confirmClear = false }.buttonStyle(PillButton(filled: true))
                    } else {
                        Button("Clear") { confirmClear = true }.buttonStyle(PillButton())
                            .help("Deletes the saved history and image thumbnails")
                    }
                }

                Label11("Devices", size: 12).padding(.top, 24).padding(.bottom, 8)
                Button { model.pairedDevicesOpen = true } label: {
                    Row {
                        Text("Paired devices · \(engine.pairings.count)")
                        Spacer()
                        Chevron()
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                HStack {
                    Spacer()
                    Button("Quit Ctrl Link") { NSApp.terminate(nil) }.buttonStyle(PillButton())
                }
                .padding(.top, 24)
            }
            .font(.system(size: 15))
            .padding(.horizontal, 20).padding(.bottom, 20).padding(.top, 8)
        }
        .onAppear { name = engine.deviceName }
        .onDisappear(perform: commitName)
    }

    func commitName() {
        let n = name.trimmingCharacters(in: .whitespacesAndNewlines)
        if !n.isEmpty, n != engine.deviceName { engine.deviceName = n }
    }

    func setLogin(_ on: Bool) {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            loginError = nil
        } catch {
            loginError = "Could not change this setting: \(error.localizedDescription)"
        }
    }
}

/// Settings > Paired devices (75:4169): one row per peer with Unpair (confirmed), a note, and "Pair a new device".
struct PairedDevicesPage: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var engine: CtrlLinkEngine
    @State private var confirmUnpair: String?

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Button { model.pairedDevicesOpen = false } label: {
                    Image(systemName: "arrow.left").font(.system(size: 15)).frame(width: 28, height: 28).contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .keyboardShortcut(.cancelAction)
                .accessibilityLabel("Back")
                Spacer()
            }
            .padding(.horizontal, 16)
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    // 75:4169: content pt 8 + label wrap pt 24 / pb 8.
                    Label11("Paired devices", size: 12).lineHeight(16, natural: 14.13).padding(.top, 32).padding(.bottom, 8)
                    Hairline()
                    if engine.pairings.isEmpty {
                        Row { Text("None").foregroundStyle(Theme.secondary) }
                    }
                    ForEach(engine.pairings) { p in
                        // Paired row (75:4186): the name, then the button; 6 pt vertical padding, 8 pt gap.
                        VStack(spacing: 0) {
                            HStack(spacing: 8) {
                                Text(p.name).lineLimit(1).truncationMode(.middle)
                                Spacer(minLength: 0)
                                if confirmUnpair == p.id {
                                    Button("Cancel") { confirmUnpair = nil }.buttonStyle(PillButton())
                                    Button("Unpair") { engine.unpair(p.id); confirmUnpair = nil }.buttonStyle(PillButton(filled: true))
                                } else {
                                    Button("Unpair") { confirmUnpair = p.id }.buttonStyle(PillButton())
                                }
                            }
                            .padding(.vertical, 6)
                            Hairline()
                        }
                        .accessibilityElement(children: .contain)
                        .accessibilityLabel("\(p.name), \(platformName(p.pf))") // the platform moved out of sight, not out of VoiceOver
                    }
                    Text("Unpairing deletes the keys on both devices. To connect again, pair with a new code.")
                        .font(.system(size: 12, weight: .medium)).foregroundStyle(Theme.secondary)
                        .lineHeight(16, natural: 14.13).fixedSize(horizontal: false, vertical: true)
                        .padding(.top, 12).padding(.bottom, 4)
                    HStack {
                        Spacer()
                        Button("Pair a new device") { model.startPairing() }.buttonStyle(PillButton())
                    }
                    .padding(.top, 24)
                }
                .font(.system(size: 15))
                .padding(.horizontal, 20).padding(.bottom, 20)
            }
        }
    }
}

struct Chevron: View {
    var body: some View {
        Image(systemName: "chevron.right").font(.system(size: 13, weight: .medium)).frame(width: 20, height: 20)
    }
}

/// A settings row (23:697): 14 pt vertical padding, hairline underneath.
struct Row<Content: View>: View {
    @ViewBuilder let content: Content
    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 12) { content }.frame(maxWidth: .infinity, minHeight: 20, alignment: .leading).padding(.vertical, 14)
            Hairline()
        }
    }
}
