import CtrlLinkCore
import SwiftUI

struct PopoverView: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var engine: CtrlLinkEngine
    @AppStorage("welcomeSeen") private var welcomeSeen = false

    var body: some View {
        Group {
            if !model.splashDone {
                SplashView()
            } else if !welcomeSeen && engine.pairings.isEmpty && model.route == .home { // a pairing link goes straight to Join
                WelcomeView { pair in
                    welcomeSeen = true
                    if pair { model.startPairing() }
                }
            } else {
                VStack(spacing: 0) {
                    SegmentedPill(selection: $model.tab)
                        .padding(.horizontal, 20).padding(.top, 16).padding(.bottom, 12)
                    switch model.tab {
                    case .devices:
                        switch model.route {
                        case .home:
                            if let t = engine.transfers.first {
                                // Offers that arrive mid-transfer must stay answerable.
                                ForEach(engine.offers) { OfferPanel(offer: $0) }
                                TransferPage(transfer: t)
                            } else {
                                DevicesPage()
                            }
                        case .host: HostPage()
                        case .join: JoinPage()
                        }
                    case .history: HistoryPage()
                    case .settings:
                        if model.pairedDevicesOpen { PairedDevicesPage() } else { SettingsPage() }
                    }
                }
            }
        }
        .frame(width: 380, height: 700)
        .background(Theme.bg)
        .foregroundStyle(Theme.text)
        .tint(Theme.text)
        .onAppear { if !engine.pairings.isEmpty { welcomeSeen = true } } // existing installs never see Welcome later
        .task {
            guard !model.splashDone else { return }
            try? await Task.sleep(for: .milliseconds(600)) // long enough to read, no longer
            withAnimation(.easeOut(duration: 0.2)) { model.splashDone = true }
        }
    }
}

/// Full-width outlined capsule; the selected segment is a translucent white capsule with a light rim.
struct SegmentedPill: View {
    @Binding var selection: Tab
    var body: some View {
        HStack(spacing: 0) {
            ForEach(Tab.allCases, id: \.self) { t in
                SegmentItem(title: t.rawValue, selected: selection == t) { selection = t }
            }
        }
        .padding(3) // 2 pt inset + the 1 pt border: a 32 pt pill and a 60 pt header, as in every frame
        .overlay(Capsule().strokeBorder(Theme.hairline, lineWidth: 1))
    }
}

private struct SegmentItem: View {
    let title: String, selected: Bool
    let action: () -> Void
    @State private var hover = false
    var body: some View {
        Button(action: action) {
            Text(title).font(.system(size: 13))
                .frame(maxWidth: .infinity).frame(height: 26)
                .background {
                    if selected {
                        Capsule().fill(Color.white.opacity(0.32))
                            .overlay(Capsule().strokeBorder(Color.white.opacity(0.68), lineWidth: 1))
                    } else if hover {
                        Capsule().fill(Color.white.opacity(0.08)) // hover hint on the unselected tabs
                    }
                }
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .animation(.easeOut(duration: 0.12), value: hover)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

/// The huge serif number; counts up when it changes (unless Reduce Motion).
struct Hero: View {
    let value: String
    var size: CGFloat = 96
    @Environment(\.accessibilityReduceMotion) var reduceMotion
    var body: some View {
        Text(value).font(Theme.serif(size, weight: 300)).tracking(-size * 0.02).monospacedDigit()
            .contentTransition(.numericText())
            .animation(reduceMotion ? nil : .easeOut(duration: 0.4), value: value)
            .lineLimit(1).fixedSize()
    }
}

/// Shown briefly at launch while the engine starts.
struct SplashView: View {
    var body: some View {
        // 75:4380: just the brand, mark and name (Serif/Headline L 34 / 40) 16 apart, centred; no caption, no horizon.
        VStack(spacing: 16) {
            AppMark(size: 160)
            Text("Ctrl Link").font(Theme.serif(34, opsz: 36)).lineHeight(40, natural: 34)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Ctrl Link, starting")
    }
}

/// First run only: what Ctrl Link does, three steps, and the way in.
struct WelcomeView: View {
    let done: (_ pair: Bool) -> Void
    static let steps = ["Open Ctrl Link on another device on this network.", "Pair the two with a code or a QR code.",
                        "Copy as usual. It shows up on the other device."]

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                AppMark(size: 20)
                Text("Ctrl Link").font(.system(size: 14, weight: .medium))
                Spacer()
            }
            .padding(.horizontal, 16).padding(.top, 16).padding(.bottom, 8)
            // 75:4394: 12 pt gaps between Figma line boxes, plus a 12 pt spacer above the steps.
            VStack(spacing: 12) {
                Label11("Welcome to Ctrl Link", size: 12).lineHeight(16, natural: 14.13)
                Text("Copy on one device, paste on another").font(Theme.serif(34, opsz: 36)).multilineTextAlignment(.center)
                    .lineHeight(40, natural: 34)
                    .fixedSize(horizontal: false, vertical: true)
                Text("Everything stays on your local network and is encrypted. Nothing is sent to the internet.")
                    .font(.system(size: 13)).foregroundStyle(Theme.secondary).multilineTextAlignment(.center)
                    .lineHeight(18, natural: 15.31)
                    .fixedSize(horizontal: false, vertical: true)
                VStack(spacing: 0) {
                    Hairline()
                    ForEach(Array(Self.steps.enumerated()), id: \.offset) { i, step in
                        HStack(alignment: .top, spacing: 16) {
                            Text("\(i + 1)").font(Theme.serif(26, opsz: 36)).lineHeight(32, natural: 26).fixedSize()
                            Text(step).font(.system(size: 15)).lineHeight(20, natural: 17.67)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .fixedSize(horizontal: false, vertical: true)
                                .layoutPriority(1)
                        }
                        .padding(.vertical, 12)
                        Hairline()
                    }
                }
                .padding(.top, 24)
                Spacer(minLength: 0)
                Text("Ctrl Link lives in the menu bar, at the top right of your screen.")
                    .font(.system(size: 13)).foregroundStyle(Theme.secondary).multilineTextAlignment(.center)
                    .lineHeight(18, natural: 15.31)
                Button("Pair a device") { done(true) }.buttonStyle(PillButton(filled: true, wide: true)).keyboardShortcut(.defaultAction)
                Button("Skip for now") { done(false) }.buttonStyle(PillButton(wide: true))
            }
            .padding(.horizontal, 16).padding(.top, 32).padding(.bottom, 12)
        }
    }
}

struct DevicesPage: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var engine: CtrlLinkEngine

    var body: some View {
        VStack(spacing: 0) {
            VStack(spacing: 0) {
                VStack(spacing: 0) {
                    ForEach(engine.offers) { OfferPanel(offer: $0) }
                    DeviceGraph(me: engine.deviceName, myIP: CtrlLinkEngine.localIPv4(), peers: engine.peers)
                }
                .frame(maxHeight: .infinity)
                VStack(spacing: 8) {
                    Label11("On this network", size: 12)
                    Text(headline).font(Theme.headline).multilineTextAlignment(.center).lineLimit(2).lineSpacing(6) // 32 pt lines if it wraps
                }
                .padding(.horizontal, 20).padding(.bottom, 40)
                .accessibilityElement(children: .combine)
            }
            HStack(spacing: 8) {
                Button("Pair device") { model.startPairing() }.buttonStyle(PillButton(filled: true, wide: true))
                Button("Enter code") { model.route = .join }.buttonStyle(PillButton(wide: true))
            }
            .padding(.horizontal, 20).padding(.vertical, 16)
        }
        .background(alignment: .bottom) { Horizon().offset(y: 648 - 219) } // top edge 421 pt into the hero, as in 75:3493
        .clipped()
    }

    var headline: String {
        let online = engine.peers.filter(\.online).count
        return online == 0 ? "Looking for devices" : "\(online + 1) devices connected"
    }
}

func platformSymbol(_ pf: String) -> String {
    switch pf {
    case "mac": "apple.logo"
    case "android": "smartphone"
    case "windows": "desktopcomputer"
    default: "desktopcomputer"
    }
}

func platformName(_ pf: String) -> String {
    switch pf {
    case "mac": "Mac"
    case "android": "Android"
    case "windows": "Windows"
    default: "Device"
    }
}

/// Device-connection graph (69:3849). One peer: this Mac above it, joined by a short vertical dashed line. More peers:
/// this Mac on the right, peers in a column on the left, each joined by a dashed curve. Up to 4 peers, then "+N more".
struct DeviceGraph: View {
    let me: String, myIP: String?
    let peers: [PeerStatus]
    @Environment(\.accessibilityReduceMotion) var reduceMotion

    enum Slot { case peer(PeerStatus), more(Int) }

    var slots: [Slot] {
        // Online first so the ones that matter are the ones shown.
        let sorted = peers.sorted { $0.online && !$1.online }
        if sorted.count <= 4 { return sorted.map(Slot.peer) }
        return sorted.prefix(3).map(Slot.peer) + [.more(sorted.count - 3)]
    }

    struct Link { let from: CGPoint, to: CGPoint, vertical: Bool, online: Bool }

    var body: some View {
        GeometryReader { g in
            let w = g.size.width, h = g.size.height, cy = h / 2
            let slots = slots
            let (mePos, slotPos): (CGPoint, [CGPoint]) = {
                switch slots.count {
                case 0: return (CGPoint(x: w / 2, y: cy), []) // drawn centred as a whole below
                // 75:3493: the connector runs at x 135 of the 380 pt popover (graph 378 wide, 1 pt border).
                case 1: return (CGPoint(x: w / 2 - 55, y: cy - 53), [CGPoint(x: w / 2 - 55, y: cy + 54)])
                default:
                    let n = slots.count
                    let step = min(130, (h - 60) / CGFloat(n - 1))
                    let ys = (0..<n).map { cy + (CGFloat($0) - CGFloat(n - 1) / 2) * step }
                    // Keep this Mac between two rows so no curve runs through a peer's label.
                    let my = n % 2 == 0 ? cy : (ys[n / 2] + ys[n / 2 - 1]) / 2
                    return (CGPoint(x: w * 0.56, y: my), ys.map { CGPoint(x: 56, y: $0) })
                }
            }()
            let links: [Link] = slots.enumerated().map { i, s in
                let p = slotPos[i]
                let online = if case .peer(let peer) = s { peer.online } else { false }
                if slots.count == 1 {
                    return Link(from: CGPoint(x: mePos.x, y: mePos.y + 20), to: CGPoint(x: p.x, y: p.y - 20), vertical: true, online: online)
                }
                return Link(from: CGPoint(x: mePos.x - 20, y: mePos.y), to: CGPoint(x: p.x, y: p.y < mePos.y ? p.y + 20 : p.y - 20),
                            vertical: false, online: online)
            }

            ZStack(alignment: .topLeading) {
                ForEach(Array(links.enumerated()), id: \.offset) { _, l in
                    Self.path(l).stroke(Color.white.opacity(0.68), style: StrokeStyle(lineWidth: 1, dash: [5.7, 3.8]))
                        .opacity(l.online ? 1 : 0.35)
                    if l.online && !reduceMotion { Comet(path: Self.path(l)) }
                }

                if slots.isEmpty {
                    // No peers (69:3846): the whole node, icon and labels, is centred; long names truncate inside 20 pt margins.
                    HStack(spacing: 0) {
                        Spacer(minLength: 20)
                        DeviceNode(title: me, subtitle: myIP ?? "This Mac", symbol: "apple.logo", style: .current)
                        Spacer(minLength: 20)
                    }
                    .frame(width: w).position(x: w / 2, y: cy)
                } else {
                    DeviceNode(title: me, subtitle: myIP ?? "This Mac", symbol: "apple.logo", style: .current)
                        .place(at: mePos, width: w - mePos.x) // labels stop at the popover's 20 pt margin
                }
                ForEach(Array(slots.enumerated()), id: \.offset) { i, s in
                    let p = slotPos[i]
                    Group {
                        switch s {
                        case .peer(let peer):
                            DeviceNode(title: peer.name, subtitle: peer.online ? (peer.ip ?? platformName(peer.pf)) : "Offline",
                                       symbol: platformSymbol(peer.pf), style: peer.online ? .online : .offline)
                        case .more(let n):
                            DeviceNode(title: "\(n) more", subtitle: "Paired devices", symbol: nil, glyph: "+\(n)", style: .offline)
                        }
                    }
                    .place(at: p, width: w - p.x) // rows never share this Mac's line, so labels run to the 20 pt margin
                }
                // Dots sit on top of the tiles, where the connector meets them.
                ForEach(Array(links.enumerated()), id: \.offset) { _, l in
                    ConnectorDot(online: l.online).position(l.to)
                }
                if !links.isEmpty { ConnectorDot(online: links.contains(where: \.online)).position(links[0].from) }
            }
            .frame(width: w, height: h, alignment: .topLeading)
        }
        .accessibilityElement(children: .combine)
    }

    static func path(_ l: Link) -> Path {
        Path { p in
            p.move(to: l.from)
            if l.vertical {
                p.addLine(to: l.to)
            } else {
                p.addCurve(to: l.to, control1: CGPoint(x: l.to.x + (l.from.x - l.to.x) * 0.35, y: l.from.y),
                           control2: CGPoint(x: l.to.x, y: l.from.y + (l.to.y - l.from.y) * 0.35))
            }
        }
    }
}

extension View {
    /// Puts a device node so its 40 pt icon is centred on `icon`, with the labels running right up to `width`.
    func place(at icon: CGPoint, width: CGFloat) -> some View {
        frame(width: max(width, 40), alignment: .leading).position(x: icon.x - 20 + max(width, 40) / 2, y: icon.y)
    }
}

/// Small green dot with a black rim where a connector meets a device.
struct ConnectorDot: View {
    var online = true
    var body: some View {
        Circle().fill(online ? Theme.positive : Theme.secondary).overlay(Circle().strokeBorder(Color.black, lineWidth: 1)).frame(width: 12, height: 12)
    }
}

/// A short glowing trail that runs along an online connection (the design's "connection animation").
struct Comet: View {
    let path: Path
    var body: some View {
        TimelineView(.animation) { ctx in
            let t = ctx.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 1.8) / 1.8
            let head = 0.1 + t * 0.8
            ZStack {
                path.trimmedPath(from: max(0.1, head - 0.2), to: head)
                    .stroke(LinearGradient(colors: [Theme.kind(.url).opacity(0), Theme.kind(.url)], startPoint: .top, endPoint: .bottom),
                            style: StrokeStyle(lineWidth: 3, lineCap: .round))
                    .blur(radius: 1.5)
                path.trimmedPath(from: max(0.1, head - 0.01), to: head)
                    .stroke(Color.white, style: StrokeStyle(lineWidth: 3, lineCap: .round))
                    .shadow(color: Theme.kind(.url), radius: 3)
            }
            .blendMode(.plusLighter)
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}

/// Device node (69:3732): 40 pt icon tile, name (15 pt) and IP (11 pt label).
struct DeviceNode: View {
    enum Style { case current, online, offline }
    let title: String, subtitle: String
    let symbol: String?
    var glyph: String? = nil
    let style: Style

    var body: some View {
        HStack(spacing: 8) {
            ZStack {
                switch style {
                case .current:
                    RoundedRectangle(cornerRadius: 12).fill(Theme.kind(.url))
                        .shadow(color: Color(nsColor: NSColor(hex: 0x9BCCF1)), radius: 2)
                case .online:
                    RoundedRectangle(cornerRadius: 12).fill(Theme.bg)
                        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Theme.positive, lineWidth: 1))
                        .shadow(color: Theme.positive, radius: 2)
                case .offline:
                    RoundedRectangle(cornerRadius: 12).fill(Theme.bg)
                        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Theme.hairline, lineWidth: 1))
                }
                if let symbol {
                    Image(systemName: symbol).font(.system(size: 18, weight: .regular))
                } else if let glyph {
                    Text(glyph).font(.system(size: 13, weight: .medium)).monospacedDigit()
                }
            }
            .frame(width: 40, height: 40)
            VStack(alignment: .leading, spacing: 4) {
                Text(title).font(.system(size: 15)).lineLimit(1).truncationMode(.tail)
                Text(subtitle.uppercased()).font(.system(size: 11, weight: .medium)).tracking(0.66).foregroundStyle(Theme.secondary).lineLimit(1)
            }
        }
        .opacity(style == .offline ? 0.4 : 1)
        .accessibilityElement(children: .combine)
    }
}

struct OfferPanel: View {
    @EnvironmentObject var engine: CtrlLinkEngine
    let offer: Offer

    var body: some View {
        VStack(spacing: 8) {
            Label11("Incoming from \(offer.peerName)", size: 12)
            Text(offer.count == 1 ? "1 file" : "\(offer.count) files").font(Theme.headline)
            Text(ByteCountFormatter.string(fromByteCount: offer.size, countStyle: .file))
                .font(.system(size: 15)).foregroundStyle(Theme.secondary).monospacedDigit()
            HStack(spacing: 8) {
                Button("Decline") { engine.rejectOffer(offer.id) }.buttonStyle(PillButton())
                Button("Accept") { engine.acceptOffer(offer.id) }.buttonStyle(PillButton(filled: true, wide: true)).keyboardShortcut(.defaultAction)
            }
            .padding(.top, 8) // 16 pt from the size line, so the buttons don't read as part of the text block
        }
        .padding(.horizontal, 20).padding(.top, 16)
    }
}

/// Transfer in progress (75:3561): who, what, the percent as the hero, a speed graph, size and time left, and Cancel.
struct TransferPage: View {
    @EnvironmentObject var engine: CtrlLinkEngine
    let transfer: Transfer

    /// The engine samples speed once a second for the whole transfer, so leaving the tab or closing the popover
    /// doesn't leave a gap: x = progress 0...1, y = bytes per second.
    var samples: [CGPoint] {
        [CGPoint(x: 0, y: 0)] + transfer.speeds.map { CGPoint(x: $0.progress, y: $0.bytesPerSecond) }
    }

    var body: some View {
        VStack(spacing: 0) {
            TimelineView(.periodic(from: .now, by: 1)) { ctx in
                let frac = Double(transfer.done) / Double(max(transfer.total, 1))
                let elapsed = max(ctx.date.timeIntervalSince(transfer.started), 0.1)
                let recent = samples.suffix(5).map(\.y).filter { $0 > 0 }
                let rate = recent.isEmpty ? Double(transfer.done) / elapsed : recent.reduce(0, +) / Double(recent.count)
                let left: Int? = rate > 0 ? Int((Double(transfer.total - transfer.done) / rate).rounded(.up)) : nil
                let size = ByteCountFormatter.string(fromByteCount: transfer.total, countStyle: .file)
                VStack(spacing: 8) {
                    // 75:3561: 8 pt gaps between Figma line boxes (label 16, headline 32, hero 96, graph, stat row 50).
                    Label11("\(transfer.incoming ? "Receiving from" : "Sending to") \(transfer.peer)", size: 12)
                        .lineHeight(16, natural: 14.13)
                    Text(transfer.name).font(Theme.headline).multilineTextAlignment(.center).lineLimit(2).truncationMode(.middle)
                        .lineHeight(32, natural: 26)
                    HStack(alignment: .firstTextBaseline, spacing: 12) {
                        Hero(value: "\(Int(frac * 100))")
                        Text("%").font(.system(size: 15)).foregroundStyle(Theme.secondary)
                    }
                    .accessibilityElement(children: .combine)
                    TransferGraph(samples: samples + [CGPoint(x: frac, y: samples.last?.y ?? 0)])
                    HStack(spacing: 0) {
                        StatCell(label: "Size", value: String(size.split(separator: " ").first ?? ""),
                                 unit: size.split(separator: " ").dropFirst().joined(separator: " "))
                            .padding(.trailing, 16)
                        Rectangle().fill(Theme.hairline).frame(width: 1, height: 48)
                        StatCell(label: "Time left", value: Self.clock(left))
                            .padding(.leading, 16)
                    }
                }
                .padding(.horizontal, 20)
                .frame(maxHeight: 478)
            }
            Spacer(minLength: 0)
            Button("Cancel") { engine.cancelTransfer(transfer.id) }.buttonStyle(PillButton(wide: true))
                .keyboardShortcut(.cancelAction)
                .padding(.horizontal, 20).padding(.vertical, 16)
        }
        .background(alignment: .bottom) { Horizon().offset(y: 648 - 162) } // top edge at the foot of the hero, as in 75:3561
        .clipped()
    }

    /// "0:42", "12:05", "1:47:07"; an en dash until there is a rate to estimate from.
    static func clock(_ seconds: Int?) -> String {
        guard let s = seconds else { return "\u{2013}" }
        let two = { String(format: "%02d", $0) }
        return s >= 3600 ? "\(s / 3600):\(two(s / 60 % 60)):\(two(s % 60))" : "\(s / 60):\(two(s % 60))"
    }
}

/// Transfer Graph (75:3491): MB/s on the left axis, progress 0-100 % along the bottom, filled area under the line,
/// and a light dot at the current point.
struct TransferGraph: View {
    let samples: [CGPoint]

    var body: some View {
        let peak = (samples.map(\.y).max() ?? 0) / 1_000_000
        let step = Self.niceStep(max(peak, 0.4) / 4) // scales to the fastest sample; 0-0.4 MB/s while idle
        let top = step * 4
        VStack(spacing: 4) {
            HStack(spacing: 8) {
                VStack(alignment: .trailing) {
                    ForEach((0...4).reversed(), id: \.self) { i in
                        if i < 4 { Spacer(minLength: 0) }
                        Text(Self.tick(Double(i) * step)).frame(width: 24, alignment: .trailing)
                    }
                }
                .frame(height: 84)
                GeometryReader { g in
                    let w = g.size.width, h = g.size.height
                    // 75:3491: the five speed lines sit at y 6, 24, 42, 60, 78, level with the axis labels.
                    let pts = samples.map { CGPoint(x: w * min(max($0.x, 0), 1), y: 6 + 72 * (1 - min($0.y / 1_000_000 / top, 1))) }
                    Path { p in
                        p.addRect(CGRect(x: 0.5, y: 0.5, width: w - 1, height: h - 1))
                        for i in 1...7 { let x = (w - 1) * CGFloat(i) / 8 + 0.5; p.move(to: CGPoint(x: x, y: 0)); p.addLine(to: CGPoint(x: x, y: h)) }
                        for i in 0...4 { let y = 6 + 18 * CGFloat(i); p.move(to: CGPoint(x: 1, y: y)); p.addLine(to: CGPoint(x: w - 1, y: y)) }
                    }
                    .stroke(Self.grid, lineWidth: 1)
                    if let last = pts.last {
                        Path { p in
                            p.move(to: CGPoint(x: pts[0].x, y: h))
                            p.addLines(pts)
                            p.addLine(to: CGPoint(x: last.x, y: h))
                            p.closeSubpath()
                        }
                        .fill(LinearGradient(colors: [Color(nsColor: NSColor(hex: 0x383838)), .black], startPoint: .top, endPoint: .bottom))
                        Path { p in p.addLines(pts) }.stroke(Color.white.opacity(0.68), lineWidth: 1)
                        Circle().fill(Color(nsColor: NSColor(hex: 0xDFDFDF))).frame(width: 8, height: 8).position(last)
                    }
                }
                .frame(height: 84)
                .clipped()
            }
            .padding(.top, 8)
            HStack(spacing: 8) {
                Color.clear.frame(width: 24, height: 10)
                HStack { // UI/Mac/Label Sx: 9 / 10
                    ForEach([0, 25, 50, 75, 100], id: \.self) { v in
                        if v > 0 { Spacer(minLength: 0) }
                        Text("\(v)%").frame(width: v == 0 ? 24 : nil, alignment: .leading) // "0%" sits in a 24 pt box, as in 75:3483
                    }
                }
                .frame(height: 10)
            }
        }
        .font(.system(size: 9, weight: .medium)).tracking(0.09).foregroundStyle(Theme.secondary).monospacedDigit()
        .accessibilityElement()
        .accessibilityLabel("Speed \(ByteCountFormatter.string(fromByteCount: Int64(samples.last?.y ?? 0), countStyle: .file)) per second")
    }

    /// Grid lines (#141414 in 75:3491): fainter than the hairline token so the speed line leads.
    static let grid = Theme.dynamic(0x141414, 0xEDEDEF)

    static func niceStep(_ raw: Double) -> Double {
        let mag = pow(10, floor(log10(raw)))
        for m in [1.0, 2, 2.5, 3, 4, 5, 10] where m * mag >= raw { return m * mag }
        return 10 * mag
    }

    static func tick(_ v: Double) -> String {
        v == v.rounded() ? "\(Int(v))" : v.formatted(.number.precision(.fractionLength(0...2)))
    }
}

/// Stat Row column: uppercase label over a 26 pt serif numeral with an optional sans unit.
struct StatCell: View {
    let label: String, value: String
    var unit = ""
    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Label11(label).lineHeight(14, natural: 12.96)
            HStack(alignment: .firstTextBaseline, spacing: 4) {
                Text(value).font(Theme.serif(26, opsz: 36)).monospacedDigit()
                if !unit.isEmpty { Text(unit).font(.system(size: 13)).foregroundStyle(Theme.secondary) }
            }
            .padding(.vertical, 3) // Serif/Numeral 26 / 32
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
    }
}

/// Back arrow row for the sub pages.
struct BackRow: View {
    @EnvironmentObject var model: AppModel
    var onBack: () -> Void = {}
    var body: some View {
        HStack {
            Button {
                onBack()
                model.route = .home
            } label: {
                Image(systemName: "arrow.left").font(.system(size: 15, weight: .regular)).frame(width: 28, height: 28).contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .keyboardShortcut(.cancelAction)
            .accessibilityLabel("Back")
            Spacer()
        }
        .padding(.horizontal, 16)
    }
}
