import Foundation
import CoreGraphics

/// Screen-space coverage is independent of SwiftUI's visible text and hit tests.
struct IslandHoverTracking {
    private(set) var isInside = false

    mutating func update(pointer: CGPoint, region: CGRect?, force: Bool = false) -> Bool? {
        let inside: Bool
        if let region, !region.isEmpty {
            // Include the screen's very top edge, which CGRect.contains excludes.
            inside = pointer.x >= region.minX && pointer.x <= region.maxX
                && pointer.y >= region.minY && pointer.y <= region.maxY
        } else {
            inside = false
        }
        guard force || inside != isInside else { return nil }
        isInside = inside
        return inside
    }
}
