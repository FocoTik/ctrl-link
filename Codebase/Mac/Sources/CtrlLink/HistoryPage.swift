import CtrlLinkCore
import ImageIO
import SwiftUI

struct HistoryPage: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var engine: CtrlLinkEngine
    @State private var selected = Calendar.current.startOfDay(for: Date())
    @State private var grown = false
    @State private var hidden: Set<ClipKind> = [] // legend filters: kinds switched off
    @Environment(\.accessibilityReduceMotion) var reduceMotion

    static let kinds: [ClipKind] = [.files, .image, .url, .text] // bottom → top
    static let legend: [ClipKind: String] = [.text: "Text", .url: "Link", .image: "Image", .files: "Files"]

    var days: [Date] {
        let today = Calendar.current.startOfDay(for: Date())
        return (0..<7).reversed().map { Calendar.current.date(byAdding: .day, value: -$0, to: today)! }
    }

    var body: some View {
        let counts = days.map { engine.counts(on: $0).filter { !hidden.contains($0.key) } }
        let totals = counts.map { $0.values.reduce(0, +) }
        let total = totals.reduce(0, +)
        let peak = max(totals.max() ?? 0, 1)
        ScrollView {
            VStack(spacing: 0) {
                HStack(spacing: 0) {
                    ForEach(days, id: \.self) { d in
                        if d != days.first { Spacer(minLength: 0) }
                        Button { selected = d } label: {
                            VStack(spacing: 6) {
                                Text(d.formatted(.dateTime.weekday(.abbreviated)).uppercased())
                                    .font(.system(size: 13)).lineHeight(18, natural: 15.31)
                                    .foregroundStyle(d == selected ? Theme.text : Theme.secondary)
                                Rectangle().fill(d == selected ? Theme.text : .clear).frame(width: 40, height: 1)
                            }
                            .padding(.vertical, 6)
                            .frame(width: 40)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel(d.formatted(.dateTime.weekday(.wide))) // "Monday", not "MON"
                        .accessibilityAddTraits(d == selected ? .isSelected : [])
                    }
                }
                .padding(.horizontal, 20)

                VStack(spacing: 8) { // 75:3611 summary: headline 32, hero 96, label 16, 8 apart
                    Text(total >= 70 ? "Busy week" : "Quiet week").font(Theme.headline).lineHeight(32, natural: 26)
                    Hero(value: "\(total)")
                    Label11("Average \((Double(total) / 7).formatted(.number.precision(.fractionLength(0...1)))) a day", size: 12)
                        .lineHeight(16, natural: 14.13)
                }
                .padding(.top, 24)

                // Stacked Bar (21:622): 44 pt columns spread edge to edge, 200 tall with the axis and day label.
                HStack(alignment: .bottom, spacing: 0) {
                    ForEach(Array(days.enumerated()), id: \.offset) { i, d in
                        if i > 0 { Spacer(minLength: 0) }
                        Button { selected = d } label: {
                        VStack(spacing: 6) {
                            VStack(spacing: 2) {
                                Spacer(minLength: 0)
                                ForEach(Self.kinds.reversed(), id: \.self) { k in
                                    if let n = counts[i][k], n > 0 {
                                        RoundedRectangle(cornerRadius: 4).fill(Theme.kind(k))
                                            .frame(width: 28, height: grown ? max(CGFloat(n) / CGFloat(peak) * 150, 4) : 0)
                                    }
                                }
                            }
                            .frame(maxHeight: .infinity)
                            Hairline()
                            Text(d.formatted(.dateTime.weekday(.abbreviated)).uppercased())
                                .font(.system(size: 11, weight: .medium)).tracking(0.66)
                                .foregroundStyle(d == selected ? Theme.text : Theme.secondary)
                                .lineHeight(14, natural: 12.96)
                        }
                        .frame(width: 44, height: 200)
                        .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .accessibilityAddTraits(d == selected ? .isSelected : [])
                        .animation(reduceMotion ? nil : .easeOut(duration: 0.3).delay(Double(i) * 0.03), value: grown)
                        .accessibilityElement()
                        .accessibilityLabel("\(d.formatted(.dateTime.weekday(.wide))): \(totals[i]) clips")
                    }
                }
                .padding(.horizontal, 20).padding(.top, 12)

                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 16), count: 2), alignment: .leading, spacing: 12) {
                    ForEach([ClipKind.text, .url, .image, .files], id: \.self) { k in
                        // Each legend item is a filter: click to hide that kind from the bars and the list.
                        Button {
                            if hidden.contains(k) { hidden.remove(k) } else { hidden.insert(k) }
                        } label: {
                            VStack(alignment: .leading, spacing: 6) {
                                Label11(Self.legend[k] ?? "").lineHeight(14, natural: 12.96)
                                Capsule().fill(Theme.kind(k)).frame(height: 4)
                            }
                            .opacity(hidden.contains(k) ? 0.3 : 1)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .animation(.easeOut(duration: 0.12), value: hidden)
                        .accessibilityLabel("\(Self.legend[k] ?? "") clips")
                        .accessibilityValue(hidden.contains(k) ? "Hidden" : "Shown")
                        .accessibilityHint("Shows or hides this kind")
                    }
                }
                .padding(.horizontal, 20).padding(.top, 16)

                let dayClips = engine.clips.filter { Calendar.current.isDate($0.date, inSameDayAs: selected) && !hidden.contains($0.kind) }
                VStack(spacing: 0) {
                    if dayClips.isEmpty {
                        Text(hidden.isEmpty ? "No clips on this day" : "No matching clips on this day").font(.system(size: 13)).foregroundStyle(Theme.secondary).padding(.vertical, 16)
                    }
                    if !dayClips.isEmpty { Hairline() }
                    ForEach(dayClips) { c in
                        ClipRow(clip: c) { model.bridge.copy(c) }
                        Hairline()
                    }
                }
                .padding(.horizontal, 20).padding(.top, 16).padding(.bottom, 20)
            }
        }
        .onAppear { grown = true }
    }
}

struct ClipRow: View {
    let clip: ClipRecord
    let copy: () -> Void
    @State private var hover = false
    @State private var thumb: NSImage?

    var body: some View {
        Button(action: copy) {
            HStack(spacing: 12) {
                Capsule().fill(Theme.kind(clip.kind)).frame(width: 3).frame(maxHeight: .infinity)
                VStack(alignment: .leading, spacing: 4) {
                    // Clip Row (23:678): 15 / 20 content over 13 / 18 meta, 4 apart.
                    Text(clip.preview.replacingOccurrences(of: "\n", with: " ")).font(.system(size: 15)).lineLimit(1)
                        .lineHeight(20, natural: 17.67)
                    Text("\(clip.from ?? "This Mac") · \(clip.date.formatted(.dateTime.hour().minute()))".uppercased())
                        .font(.system(size: 13)).foregroundStyle(Theme.secondary).lineLimit(1)
                        .lineHeight(18, natural: 15.31)
                }
                Spacer(minLength: 0)
                if clip.kind == .image, let thumb {
                    Image(nsImage: thumb).resizable().aspectRatio(contentMode: .fill)
                        .frame(width: 42, height: 42).clipShape(RoundedRectangle(cornerRadius: 4)).accessibilityHidden(true)
                } else if hover {
                    Image(systemName: "doc.on.doc").font(.system(size: 12, weight: .light)).foregroundStyle(Theme.secondary)
                }
            }
            .fixedSize(horizontal: false, vertical: true)
            .padding(12)
            .background(hover ? Theme.surface : .clear)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        // Structured, so scrolling the row away cancels it; decoded once per clip, then served from the cache.
        .task(id: clip.id, priority: .utility) {
            guard clip.kind == .image, let u = clip.files.first else { return }
            let key = clip.id as NSString
            if let cached = Self.thumbs.object(forKey: key) { thumb = cached; return }
            guard let img = await Self.decodeThumbnail(u), !Task.isCancelled else { return }
            Self.thumbs.setObject(img, forKey: key)
            thumb = img
        }
        .help("Copy again")
        .accessibilityLabel("\(Theme.kindName(clip.kind)): \(clip.preview)")
        .accessibilityHint("Copies this item to the clipboard")
    }

    static let thumbs: NSCache<NSString, NSImage> = { let c = NSCache<NSString, NSImage>(); c.countLimit = 200; return c }()

    /// Runs off the main actor (nonisolated async), at the calling task's priority.
    nonisolated static func decodeThumbnail(_ u: URL) async -> NSImage? { thumbnail(u) }

    /// Small (84 px) thumbnail so History never holds full-size images in memory.
    nonisolated static func thumbnail(_ u: URL) -> NSImage? {
        guard let src = CGImageSourceCreateWithURL(u as CFURL, nil),
              let cg = CGImageSourceCreateThumbnailAtIndex(src, 0, [kCGImageSourceCreateThumbnailFromImageAlways: true,
                                                                    kCGImageSourceThumbnailMaxPixelSize: 84,
                                                                    kCGImageSourceCreateThumbnailWithTransform: true] as CFDictionary)
        else { return nil }
        return NSImage(cgImage: cg, size: NSSize(width: 42, height: 42))
    }
}
