import AppKit
import CtrlLinkCore
import CoreImage.CIFilterBuiltins
import SwiftUI

/// The 6-digit confirmation code (or pairing code) as one serif hero numeral, e.g. "042917".
struct CodeNumeral: View {
    let code: String
    var body: some View {
        // Serif/Code: Newsreader Light 64 / 68, tracking 18 %.
        Text(code).font(Theme.serif(64, weight: 300)).tracking(11.52).monospacedDigit()
            .lineLimit(1).minimumScaleFactor(0.6)
            .lineHeight(68, natural: 64)
            .accessibilityLabel("Code \(code.map(String.init).joined(separator: " "))")
    }
}

/// SAS confirm (75:3545), shared by host and joiner: label, code, who is asking.
struct SASPanel: View {
    let sas: String, headline: String
    var body: some View {
        VStack(spacing: 16) { // 75:3545: 16 pt gaps, centred, 20 pt above the footer
            Label11("Confirm on both screens", size: 12).lineHeight(16, natural: 14.13)
            CodeNumeral(code: sas)
            // Middle truncation keeps "wants to pair" readable after a long device name.
            Text(headline).font(Theme.headline).multilineTextAlignment(.center).lineLimit(2).truncationMode(.middle)
                .lineHeight(32, natural: 26)
        }
        .padding(.horizontal, 20).padding(.bottom, 20)
        .frame(maxHeight: .infinity)
        .accessibilityElement(children: .combine)
    }
}

/// Mac as host (75:3506): big QR, the code, a countdown, and "Copy pairing link". Turns into SAS confirm on a request.
struct HostPage: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var engine: CtrlLinkEngine
    @State private var copied = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        VStack(spacing: 0) {
            BackRow { engine.closePairingWindow() }
            if let w = engine.pairingWindow {
                if let r = w.request {
                    SASPanel(sas: r.sas, headline: "\(r.name) wants to pair")
                    HStack(spacing: 8) {
                        Button("Decline") { engine.respondToPairRequest(false) }.buttonStyle(PillButton())
                        Button("Accept") { engine.respondToPairRequest(true) }
                            .buttonStyle(PillButton(filled: true, wide: true)).keyboardShortcut(.defaultAction)
                    }
                    .padding(.horizontal, 20).padding(.vertical, 16)
                } else {
                    VStack(spacing: 12) { // 75:3506: 12 pt gaps between Figma line boxes
                        Label11("Pair a device", size: 12).lineHeight(16, natural: 14.13)
                        Text("Scan or enter the code").font(Theme.headline).lineHeight(32, natural: 26)
                        if let qr = qrImage(w.uri) {
                            Image(nsImage: qr).interpolation(.none).resizable()
                                .padding(16)
                                .frame(width: 300, height: 300)
                                .background(Color.white)
                                .clipShape(RoundedRectangle(cornerRadius: 12))
                                .accessibilityLabel("Pairing QR code")
                        }
                        CodeNumeral(code: w.code).textSelection(.enabled).padding(.top, 16)
                        TimelineView(.periodic(from: .now, by: 1)) { ctx in
                            let left = max(0, w.expires.timeIntervalSince(ctx.date))
                            VStack(spacing: 8) {
                                HStack { // Countdown Line (22:593): 11 pt label, 13 pt time, 8 pt track
                                    Label11("Expires in")
                                    Spacer()
                                    Text("\(Int(left) / 60):\(String(format: "%02d", Int(left) % 60))")
                                        .font(.system(size: 13)).monospacedDigit()
                                }
                                .frame(height: 18)
                                GeometryReader { g in
                                    Capsule().fill(Theme.hairline)
                                    Capsule()
                                        .fill(LinearGradient(stops: [.init(color: .white, location: 0.394), .init(color: Theme.kind(.text), location: 0.764),
                                                                     .init(color: Theme.kind(.url), location: 1)], startPoint: .leading, endPoint: .trailing))
                                        .frame(width: g.size.width * left / 300)
                                        .animation(reduceMotion ? nil : .linear(duration: 1), value: left)
                                }
                                .frame(height: 8)
                            }
                            .accessibilityElement(children: .combine)
                        }
                    }
                    .padding(.horizontal, 20)
                    Spacer(minLength: 0)
                    Button(copied ? "Copied" : "Copy pairing link") {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(w.uri, forType: .string)
                        copied = true
                    }
                    .buttonStyle(PillButton(wide: true))
                    .padding(.horizontal, 20).padding(.vertical, 16)
                }
            } else {
                VStack(spacing: 12) {
                    Label11("Pair a device", size: 12).lineHeight(16, natural: 14.13)
                    Text("The pairing window closed").font(Theme.headline).lineHeight(32, natural: 26)
                }
                .frame(maxHeight: .infinity)
                Button("Show a new code") { engine.openPairingWindow() }.buttonStyle(PillButton(filled: true, wide: true))
                    .padding(.horizontal, 20).padding(.vertical, 16)
            }
        }
        // 75:3506: an 840 pt horizon whose top edge sits 319 pt into the body, behind the foot of the QR code.
        .background(alignment: .top) {
            if let w = engine.pairingWindow, w.request == nil { Horizon(size: 840).offset(y: 319) } // QR state only, not SAS
        }
        .clipped()
    }

    /// Black modules on white with a 4-module quiet zone (CoreImage adds 1, we pad 3). Never inverted: many scanners can't read that.
    func qrImage(_ s: String) -> NSImage? {
        let f = CIFilter.qrCodeGenerator()
        f.message = Data(s.utf8)
        f.correctionLevel = "M"
        guard let qr = f.outputImage else { return nil }
        let side = qr.extent.width + 6
        let padded = qr.transformed(by: CGAffineTransform(translationX: 3, y: 3))
            .composited(over: CIImage(color: .white).cropped(to: CGRect(x: 0, y: 0, width: side, height: side)))
            .transformed(by: CGAffineTransform(scaleX: 8, y: 8))
        guard let cg = CIContext().createCGImage(padded, from: padded.extent) else { return nil }
        return NSImage(cgImage: cg, size: NSSize(width: padded.extent.width, height: padded.extent.height))
    }
}

/// Mac as joiner (75:3525): six serif digit slots for a nearby host's code, or a pasted ctrllink:// link.
struct JoinPage: View {
    @EnvironmentObject var engine: CtrlLinkEngine
    @State private var code = ""
    @FocusState private var focused: Bool

    var body: some View {
        VStack(spacing: 0) {
            BackRow { engine.joinState = .idle }
            switch engine.joinState {
            case .connecting:
                VStack(spacing: 16) {
                    Label11("Join a device", size: 12).lineHeight(16, natural: 14.13)
                    ProgressView().controlSize(.small)
                    Text("Connecting").font(Theme.headline).lineHeight(32, natural: 26)
                }
                .frame(maxHeight: .infinity)
            case .confirm(let sas, let host):
                SASPanel(sas: sas, headline: "Confirm on \(host)")
            case .done(let host):
                VStack(spacing: 16) {
                    Label11("Paired", size: 12).lineHeight(16, natural: 14.13)
                    Text(host).font(Theme.headline).multilineTextAlignment(.center).lineLimit(2).lineHeight(32, natural: 26)
                }
                .padding(.horizontal, 20)
                .frame(maxHeight: .infinity)
            default:
                VStack(spacing: 16) { // 75:3525: 16 pt gaps; six 48 x 96 slots, 8 apart
                    Label11("Join a device", size: 12).lineHeight(16, natural: 14.13)
                    Text("Enter the code").font(Theme.headline).lineHeight(32, natural: 26)
                    ZStack {
                        HStack(spacing: 8) {
                            ForEach(0..<6, id: \.self) { i in
                                // Code Digit Slot (20:622): filled = strong underline, focused = 2 pt text, empty = hairline.
                                let current = i == code.count && focused
                                Text(i < code.count ? String(Array(code)[i]) : " ").font(Theme.serif(44, weight: 300)).tracking(-0.44)
                                    .frame(width: 48, height: 96)
                                    .overlay(alignment: .bottom) {
                                        Rectangle().fill(i < code.count || current ? Theme.text : Theme.hairline).frame(height: current ? 2 : 1)
                                    }
                            }
                        }
                        TextField("", text: $code)
                            .textFieldStyle(.plain).focused($focused).opacity(0.01)
                            .onChange(of: code) { _, v in
                                let d = String(v.filter(\.isNumber).prefix(6))
                                if d != v { code = d }
                                if d.count == 6 { Task { try? await engine.join(code: d) } }
                            }
                            .accessibilityLabel("Pairing code, 6 digits")
                    }
                    .contentShape(Rectangle())
                    .onTapGesture { focused = true }
                    if case .failed(let why) = engine.joinState {
                        Text(why).font(.system(size: 13)).foregroundStyle(Theme.danger).padding(.top, 8)
                    }
                }
                .padding(.horizontal, 20)
                Spacer(minLength: 0)
                Button("Paste pairing link") {
                    guard let s = NSPasteboard.general.string(forType: .string) else { return }
                    Task { try? await engine.join(uri: s) }
                }
                .buttonStyle(PillButton(wide: true))
                .padding(.horizontal, 20).padding(.vertical, 16)
            }
        }
        .onAppear { focused = true }
    }
}
