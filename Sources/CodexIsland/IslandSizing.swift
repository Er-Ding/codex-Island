import Foundation
import CoreGraphics

enum IslandSizePreference: Int, CaseIterable {
    case automatic = 0
    case percent100 = 100
    case percent125 = 125
    case percent150 = 150
    case percent175 = 175
    case percent200 = 200

    var title: String { self == .automatic ? "自动适配" : "\(rawValue)%" }
}

enum IslandSizing {
    static let detailHeight: CGFloat = 410
    /// Input is the desktop's logical size in points, not the panel's physical
    /// pixel resolution. AppKit already accounts for Retina backing scale.
    static func scale(for screenSize: CGSize, preference: IslandSizePreference = .automatic,
                      notchSize: CGSize = .zero) -> CGFloat {
        guard screenSize.width.isFinite, screenSize.height.isFinite,
              screenSize.width > 0, screenSize.height > 0 else { return 1 }
        let longEdge = max(screenSize.width, screenSize.height)
        let shortEdge = min(screenSize.width, screenSize.height)
        // Preserve the laptop baseline. Native 4K gets 2x, 1440p gets 4/3x;
        // a 1080p-height ultrawide and a 1080x1920 portrait display stay at 1x.
        let automatic = min(2, max(1, min(longEdge / 1920, shortEdge / 1080)))
        let requested = preference == .automatic ? automatic : CGFloat(preference.rawValue) / 100

        let notchWidth = notchSize.width.isFinite ? max(0, notchSize.width) : 0
        let notchHeight = notchSize.height.isFinite ? max(0, notchSize.height) : 0
        // Fit the expanded layout as a whole rather than clipping enlarged text.
        // The physical camera gap does not scale with the surrounding controls.
        let fit = min(screenSize.width / 420, screenSize.height / (36 + detailHeight),
                      max(0, screenSize.width - notchWidth) / 152,
                      max(0, screenSize.height - notchHeight) / detailHeight)
        return max(0.1, min(requested, fit))
    }
}

/// Preferences belong to the actual display, independent of the saved position.
final class IslandSizeStore {
    private let defaults: UserDefaults
    private let keyPrefix = "island.display-size.v1."

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    func load(displayID: String) -> IslandSizePreference {
        IslandSizePreference(rawValue: defaults.integer(forKey: keyPrefix + displayID)) ?? .automatic
    }

    func save(_ preference: IslandSizePreference, displayID: String) {
        let key = keyPrefix + displayID
        if preference == .automatic { defaults.removeObject(forKey: key) }
        else { defaults.set(preference.rawValue, forKey: key) }
    }
}
