import AppKit
import SwiftUI

/// Covers only the adjustment label, leaving the save and cancel buttons clickable.
struct PositionDragHandle: NSViewRepresentable {
    func makeNSView(context: Context) -> PositionDragView {
        PositionDragView()
    }

    func updateNSView(_ nsView: PositionDragView, context: Context) {}
}

final class PositionDragView: NSView {
    private var dragStartScreenPoint: NSPoint?
    private var dragStartWindowOrigin: NSPoint?

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func resetCursorRects() {
        super.resetCursorRects()
        addCursorRect(bounds, cursor: .openHand)
    }

    override func mouseDown(with event: NSEvent) {
        guard let window, window.isMovable else { return }
        dragStartScreenPoint = window.convertPoint(toScreen: event.locationInWindow)
        dragStartWindowOrigin = window.frame.origin
        NSCursor.closedHand.set()
    }

    override func mouseDragged(with event: NSEvent) {
        guard let window, window.isMovable,
              let startPoint = dragStartScreenPoint,
              let startOrigin = dragStartWindowOrigin else { return }
        let pointer = window.convertPoint(toScreen: event.locationInWindow)
        window.setFrameOrigin(NSPoint(
            x: startOrigin.x + pointer.x - startPoint.x,
            y: startOrigin.y + pointer.y - startPoint.y
        ))
        NSCursor.closedHand.set()
    }

    override func mouseUp(with event: NSEvent) {
        guard dragStartScreenPoint != nil else { return }
        dragStartScreenPoint = nil
        dragStartWindowOrigin = nil
        NSCursor.openHand.set()
        guard let window else { return }
        NotificationCenter.default.post(
            name: Notification.Name("CodexIsland.positionDragEnded"),
            object: window
        )
    }
}
