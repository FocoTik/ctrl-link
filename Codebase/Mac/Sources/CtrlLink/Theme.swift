import AppKit
import CtrlLinkCore
import CoreText
import SwiftUI

/// Tokens from design/DESIGN.md. Dark is the signature look; light follows the system appearance.
enum Theme {
    static func dynamic(_ dark: UInt32, _ light: UInt32) -> Color {
        Color(nsColor: NSColor(name: nil) { a in
            NSColor(hex: a.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? dark : light)
        })
    }

    static let bg = dynamic(0x000000, 0xFFFFFF)
    static let surface = dynamic(0x0F0F10, 0xF4F4F5)
    static let hairline = dynamic(0x2A2A2C, 0xDEDEE0)
    static let text = dynamic(0xFFFFFF, 0x000000)
    static let secondary = dynamic(0x8E8E93, 0x6B6B70)
    static let positive = dynamic(0x3DD68C, 0x1E9E5E)
    static let danger = dynamic(0xF0472E, 0xD63A22)

    static func kind(_ k: ClipKind) -> Color {
        switch k {
        case .text: Color(nsColor: NSColor(hex: 0xF5C26B))
        case .url: Color(nsColor: NSColor(hex: 0x3AA4F2))
        case .image: Color(nsColor: NSColor(hex: 0xE9B6E0))
        case .files: Color(nsColor: NSColor(hex: 0x2549B8))
        }
    }

    static func kindName(_ k: ClipKind) -> String {
        switch k {
        case .text: "Text"
        case .url: "Links"
        case .image: "Images"
        case .files: "Files"
        }
    }

    /// "link", "text", "image", "file" / "3 files" for one clip.
    static func kindSingular(_ c: ClipRecord) -> String {
        switch c.kind {
        case .text: "text"
        case .url: "link"
        case .image: "image"
        case .files: c.files.count == 1 ? "file" : "\(c.files.count) files"
        }
    }

    // MARK: type

    static var serifName: String?

    /// Registers the bundled Newsreader (Contents/Resources in the .app, design/fonts under `swift run`).
    static func registerFont() {
        let bundled = Bundle.main.url(forResource: "Newsreader-Variable", withExtension: "ttf")
        let source = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
            .appendingPathComponent("../../../design/fonts/Newsreader-Variable.ttf").standardized
        guard let url = bundled ?? (FileManager.default.fileExists(atPath: source.path) ? source : nil) else {
            return NSLog("Ctrl Link: Newsreader font not found, using the system serif")
        }
        CTFontManagerRegisterFontsForURL(url as CFURL, .process, nil)
        let descs = CTFontManagerCreateFontDescriptorsFromURL(url as CFURL) as? [CTFontDescriptor]
        serifName = descs?.first.flatMap { CTFontDescriptorCopyAttribute($0, kCTFontNameAttribute) as? String }
    }

    /// Newsreader at a given weight and optical size (variable axes `wght`, `opsz`).
    static func serif(_ size: CGFloat, weight: CGFloat = 400, opsz: CGFloat = 72) -> Font {
        guard let name = serifName else { return .system(size: size, weight: weight < 350 ? .light : .regular, design: .serif) }
        let axes: [NSNumber: CGFloat] = [0x7767_6874: weight, 0x6F70_737A: opsz] // 'wght', 'opsz'
        let d = CTFontDescriptorCreateWithAttributes([kCTFontNameAttribute: name, kCTFontVariationAttribute: axes] as CFDictionary)
        return Font(CTFontCreateWithFontDescriptor(d, size, nil))
    }

    static let hero = serif(96, weight: 300)
    static let headline = serif(26)
}

extension NSColor {
    convenience init(hex: UInt32) {
        self.init(srgbRed: CGFloat(hex >> 16 & 0xFF) / 255, green: CGFloat(hex >> 8 & 0xFF) / 255, blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
    }
}

/// Uppercase tracked label: 11 pt (UI/Mac/Label S) by default, 12 pt (UI/Mac/Label) for screen and section labels.
struct Label11: View {
    let text: String
    var size: CGFloat = 11
    init(_ t: String, size: CGFloat = 11) { text = t; self.size = size }
    var body: some View {
        Text(text.uppercased()).font(.system(size: size, weight: .medium)).tracking(size * 0.06).foregroundStyle(Theme.secondary)
            .lineLimit(1) // a long device name in "SENDING TO ..." truncates instead of wrapping
    }
}

extension View {
    /// Figma line height for a text view: pads each line to `line` pt (`natural` = the font's own line height:
    /// Newsreader = its size, SF Pro 11/12/13/15 = 12.96/14.13/15.31/17.67), so gaps measured in Figma carry over 1:1.
    func lineHeight(_ line: CGFloat, natural: CGFloat) -> some View {
        let extra = max(0, line - natural)
        return lineSpacing(extra).padding(.vertical, extra / 2)
    }
}

struct Hairline: View {
    var body: some View { Rectangle().fill(Theme.hairline).frame(height: 1) }
}

/// The design's Button (19:596): 40 pt capsule, 14 pt medium label. Filled = text-colour fill with two soft colour blobs
/// (amber, sky) behind the label; Outlined = strong hairline whose lower edge fades to green on the left and red on the
/// right. Hover / pressed / disabled as in the component (85 % / 70 % / 35 %, surface / hairline fill).
struct PillButton: ButtonStyle {
    var filled = false
    var wide = false
    func makeBody(configuration: Configuration) -> some View {
        PillBody(label: configuration.label, filled: filled, wide: wide, pressed: configuration.isPressed)
    }
}

private struct PillBody<Label: View>: View {
    let label: Label
    let filled: Bool, wide: Bool, pressed: Bool
    @State private var hover = false
    @Environment(\.isEnabled) private var enabled

    var body: some View {
        label
            .font(.system(size: 14, weight: .medium))
            .lineLimit(1)
            .padding(.horizontal, 20)
            .frame(maxWidth: wide ? .infinity : nil, minHeight: 40, maxHeight: 40)
            .foregroundStyle(filled ? Theme.bg : Theme.text)
            .background {
                if filled {
                    // Blobs go in an overlay so their 52 pt height can never resize the 40 pt capsule.
                    Capsule().fill(Theme.text)
                        .overlay {
                            if !pressed && !hover && enabled {
                                Blob(color: Theme.kind(.text)).offset(x: -32)
                                Blob(color: Theme.kind(.url)).offset(x: 44)
                            }
                        }
                        .clipShape(Capsule())
                } else {
                    Capsule().fill(pressed ? Theme.hairline : hover ? Theme.surface : .clear)
                }
            }
            .overlay {
                if !filled {
                    Capsule().strokeBorder(Theme.text, lineWidth: 1)
                    if enabled {
                        Capsule()
                            .strokeBorder(LinearGradient(stops: [.init(color: Theme.positive, location: 0), .init(color: Theme.positive.opacity(0), location: 0.4),
                                                                 .init(color: Theme.danger.opacity(0), location: 0.6), .init(color: Theme.danger, location: 1)],
                                                         startPoint: .leading, endPoint: .trailing), lineWidth: 1)
                            .mask(LinearGradient(colors: [.clear, .clear, .black], startPoint: .top, endPoint: .bottom))
                    }
                }
            }
            .opacity(!enabled ? 0.35 : filled && pressed ? 0.7 : filled && hover ? 0.85 : 1)
            .animation(.easeOut(duration: 0.12), value: hover) // hover eases; press stays instant on mouse-down
            .contentShape(Capsule())
            .onHover { hover = $0 }
    }
}

/// Soft diagonal colour smear inside the filled button (the "gradient blob" layers, 24 pt gaussian blur).
private struct Blob: View {
    let color: Color
    var body: some View {
        Capsule().fill(color).frame(width: 16, height: 52).rotationEffect(.degrees(39)).blur(radius: 12)
            .allowsHitTesting(false).accessibilityHidden(true)
    }
}

/// Mac toggle (19:625), 38 x 22. On: track white to amber with a background-coloured knob. Off: strong outline, secondary knob.
struct PillToggle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View {
        let on = configuration.isOn
        Button { configuration.isOn.toggle() } label: {
            ZStack(alignment: on ? .trailing : .leading) {
                if on {
                    Capsule().fill(LinearGradient(colors: [.white, Theme.kind(.text)], startPoint: .leading, endPoint: .trailing))
                } else {
                    Capsule().strokeBorder(Theme.text, lineWidth: 1)
                }
                Circle().fill(on ? Theme.bg : Theme.secondary).frame(width: on ? 18 : 14, height: on ? 18 : 14).padding(.horizontal, on ? 2 : 4)
            }
            .frame(width: 38, height: 22)
            .animation(.easeOut(duration: 0.15), value: on)
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .accessibilityRepresentation { Toggle(isOn: configuration.$isOn) { configuration.label } }
    }
}

/// Horizon (22:599): a large dim sphere whose top edge rises behind the content and fades out (no hard edge).
struct Horizon: View {
    var size: CGFloat = 648
    var body: some View {
        Circle()
            .fill(RadialGradient(stops: [.init(color: Color(nsColor: NSColor(hex: 0x1C1C1E)), location: 0),
                                         .init(color: Color(nsColor: NSColor(hex: 0x121214)).opacity(0.55), location: 0.4),
                                         .init(color: .black.opacity(0), location: 0.7)],
                                 center: .top, startRadius: 0, endRadius: size / 2))
            .frame(width: size, height: size)
            .allowsHitTesting(false)
            .accessibilityHidden(true)
    }
}

/// The app mark: design/logo/options/6-merged-monogram.svg ("Option K merged", the mark in Figma). Two 3 pt round-capped
/// loops with arrowheads, the whole group rotated -45 deg about the centre of a 24 x 24 box. Drawn from its points so no
/// logo file ships in the app.
enum Monogram {
    private static func rotated(_ s: CGFloat) -> CGAffineTransform {
        CGAffineTransform(translationX: 12 * s, y: 12 * s).rotated(by: -.pi / 4).translatedBy(x: -12 * s, y: -12 * s)
    }

    /// The two loops (stroke 3 pt, round caps).
    static func loops(scale s: CGFloat) -> CGPath {
        let p = CGMutablePath(), t = rotated(s)
        // M11 5 H7 A5 5 0 0 0 7 15 H10.8  (arc round the left, centre 7,10)
        p.move(to: CGPoint(x: 11 * s, y: 5 * s), transform: t)
        p.addLine(to: CGPoint(x: 7 * s, y: 5 * s), transform: t)
        p.addArc(center: CGPoint(x: 7 * s, y: 10 * s), radius: 5 * s, startAngle: -.pi / 2, endAngle: -3 * .pi / 2, clockwise: true, transform: t)
        p.addLine(to: CGPoint(x: 10.8 * s, y: 15 * s), transform: t)
        // M13 19 H17 A5 5 0 0 0 17 9 H13.2  (arc round the right, centre 17,14)
        p.move(to: CGPoint(x: 13 * s, y: 19 * s), transform: t)
        p.addLine(to: CGPoint(x: 17 * s, y: 19 * s), transform: t)
        p.addArc(center: CGPoint(x: 17 * s, y: 14 * s), radius: 5 * s, startAngle: .pi / 2, endAngle: -.pi / 2, clockwise: true, transform: t)
        p.addLine(to: CGPoint(x: 13.2 * s, y: 9 * s), transform: t)
        return p
    }

    /// The two arrowheads (filled, plus a 1.5 pt round-joined stroke).
    static func heads(scale s: CGFloat) -> CGPath {
        let p = CGMutablePath(), t = rotated(s)
        p.addLines(between: [CGPoint(x: 14.2 * s, y: 5 * s), CGPoint(x: 11.2 * s, y: 2.9 * s), CGPoint(x: 11.2 * s, y: 7.1 * s)], transform: t)
        p.closeSubpath()
        p.addLines(between: [CGPoint(x: 9.8 * s, y: 19 * s), CGPoint(x: 12.8 * s, y: 21.1 * s), CGPoint(x: 12.8 * s, y: 16.9 * s)], transform: t)
        p.closeSubpath()
        return p
    }

    /// Built once: the menu bar label re-renders on every engine change.
    static let menuBarNormal = menuBarImage(dimmed: false)
    static let menuBarDimmed = menuBarImage(dimmed: true)

    /// Template image for the menu bar (y-down drawing, so it matches the SVG).
    static func menuBarImage(dimmed: Bool) -> NSImage {
        let img = NSImage(size: NSSize(width: 18, height: 18), flipped: true) { _ in
            guard let ctx = NSGraphicsContext.current?.cgContext else { return false }
            let s: CGFloat = 0.75
            ctx.setAlpha(dimmed ? 0.5 : 1)
            ctx.setStrokeColor(NSColor.black.cgColor); ctx.setFillColor(NSColor.black.cgColor)
            ctx.beginTransparencyLayer(auxiliaryInfo: nil)
            ctx.addPath(loops(scale: s)); ctx.setLineWidth(3 * s); ctx.setLineCap(.round); ctx.strokePath()
            ctx.addPath(heads(scale: s)); ctx.fillPath()
            ctx.addPath(heads(scale: s)); ctx.setLineWidth(1.5 * s); ctx.setLineJoin(.round); ctx.strokePath()
            ctx.endTransparencyLayer()
            return true
        }
        img.isTemplate = true
        return img
    }
}

/// The app mark for splash and welcome, the same monogram the menu bar item shows.
struct AppMark: View {
    var size: CGFloat
    var body: some View {
        let s = size / 24
        ZStack {
            Path(Monogram.loops(scale: s)).stroke(Theme.text, style: StrokeStyle(lineWidth: 3 * s, lineCap: .round))
            Path(Monogram.heads(scale: s)).fill(Theme.text)
            Path(Monogram.heads(scale: s)).stroke(Theme.text, style: StrokeStyle(lineWidth: 1.5 * s, lineJoin: .round))
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}
