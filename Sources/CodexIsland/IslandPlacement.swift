import Foundation
import CoreGraphics

/// AppKit 屏幕坐标的 y 轴向上；位置始终以屏幕顶部为参照。
struct SavedIslandPosition: Codable, Equatable {
    let displayID: String
    let horizontalFraction: Double
    let topOffset: Double

    init(displayID: String, frame: CGRect, screenFrame: CGRect) {
        self.displayID = displayID
        guard IslandPlacement.isUsable(screenFrame) else {
            horizontalFraction = 0.5
            topOffset = 0
            return
        }
        let bounded = IslandPlacement.clampedFrame(frame, in: screenFrame)
        horizontalFraction = Double((bounded.midX - screenFrame.minX) / screenFrame.width)
        topOffset = Double(max(0, screenFrame.maxY - bounded.maxY))
    }

    func frame(in screenFrame: CGRect, size: CGSize) -> CGRect {
        guard IslandPlacement.isUsable(screenFrame) else { return .zero }
        // 无效的保存值采用顶部居中，避免把窗口移到无法找回的位置。
        let fraction = isValid ? horizontalFraction : 0.5
        let offset = isValid ? topOffset : 0
        let width = IslandPlacement.boundedLength(size.width, maximum: screenFrame.width)
        let height = IslandPlacement.boundedLength(size.height, maximum: screenFrame.height)
        let frame = CGRect(
            x: screenFrame.minX + screenFrame.width * CGFloat(fraction) - width / 2,
            y: screenFrame.maxY - CGFloat(offset) - height,
            width: width,
            height: height
        )
        return IslandPlacement.clampedFrame(frame, in: screenFrame)
    }

    fileprivate var isValid: Bool {
        !displayID.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && horizontalFraction.isFinite && (0...1).contains(horizontalFraction)
            && topOffset.isFinite && topOffset >= 0
    }
}

enum IslandPlacement {
    static func clampedFrame(_ frame: CGRect, in screenFrame: CGRect,
                             notchRect: CGRect? = nil) -> CGRect {
        guard isUsable(screenFrame) else { return .zero }
        let width = boundedLength(frame.size.width, maximum: screenFrame.width)
        let height = boundedLength(frame.size.height, maximum: screenFrame.height)
        let preferredX = frame.origin.x.isFinite ? frame.origin.x : screenFrame.midX - width / 2
        let preferredY = frame.origin.y.isFinite ? frame.origin.y : screenFrame.maxY - height
        var bounded = CGRect(
            x: min(max(preferredX, screenFrame.minX), screenFrame.maxX - width),
            y: min(max(preferredY, screenFrame.minY), screenFrame.maxY - height),
            width: width,
            height: height
        )
        if let notchRect, isUsable(notchRect) {
            let notch = notchRect.intersection(screenFrame)
            if !notch.isNull, !notch.isEmpty, bounded.intersects(notch) {
                // 先避开刘海；极小屏幕也不允许窗口越过屏幕底部。
                bounded.size.height = min(bounded.height, max(0, notch.minY - screenFrame.minY))
                bounded.origin.y = notch.minY - bounded.height
            }
        }
        return bounded
    }

    fileprivate static func isUsable(_ frame: CGRect) -> Bool {
        frame.origin.x.isFinite && frame.origin.y.isFinite
            && frame.size.width.isFinite && frame.size.height.isFinite
            && frame.width > 0 && frame.height > 0
            && frame.minX.isFinite && frame.maxX.isFinite
            && frame.minY.isFinite && frame.maxY.isFinite
    }

    fileprivate static func boundedLength(_ length: CGFloat, maximum: CGFloat) -> CGFloat {
        length.isFinite ? min(max(0, length), maximum) : 0
    }
}

final class IslandPositionStore {
    private let defaults: UserDefaults
    private let key = "island.placement.v1"

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    func load() -> SavedIslandPosition? {
        guard let data = defaults.data(forKey: key),
              let position = try? JSONDecoder().decode(SavedIslandPosition.self, from: data),
              position.isValid else { return nil }
        return position
    }

    func save(_ position: SavedIslandPosition) {
        guard position.isValid, let data = try? JSONEncoder().encode(position) else { return }
        defaults.set(data, forKey: key)
    }

    func reset() {
        defaults.removeObject(forKey: key)
    }
}
