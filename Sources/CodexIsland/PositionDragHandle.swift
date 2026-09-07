import AppKit
import CoreGraphics
import SwiftUI

/// Covers only the adjustment label, leaving the save and cancel buttons clickable.
struct PositionDragHandle: NSViewRepresentable {
    func makeNSView(context: Context) -> PositionDragView {
        PositionDragView()
    }

    func updateNSView(_ nsView: PositionDragView, context: Context) {}

    static func dismantleNSView(_ nsView: PositionDragView, coordinator: ()) {
        nsView.unregister()
    }
}

final class PositionDragView: NSView {
    private weak var dragController: IslandPositionDrag?

    // This view marks the label's geometry. The panel handles events before
    // SwiftUI hit testing, so this overlay never intercepts a control's click.
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    override var mouseDownCanMoveWindow: Bool { false }

    override func resetCursorRects() {
        super.resetCursorRects()
        guard dragController?.isEnabled == true else { return }
        addCursorRect(bounds, cursor: dragController?.isDragging == true ? .closedHand : .openHand)
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        unregister()
        guard let panel = window as? IslandPanel else { return }
        dragController = panel.positionDrag
        panel.positionDrag.register(self)
    }

    override func viewWillMove(toWindow newWindow: NSWindow?) {
        if newWindow !== window { unregister() }
        super.viewWillMove(toWindow: newWindow)
    }

    fileprivate func unregister() {
        dragController?.unregister(self)
        dragController = nil
    }
}

/// Owns the entire mouse sequence in IslandPanel.sendEvent(_:), independent of
/// SwiftUI gesture routing and server-side dragging of a nonactivating panel.
@MainActor
final class IslandPositionDrag {
    var isEnabled = false {
        didSet {
            if !isEnabled {
                finishDragging(notify: false)
                consumesMouseUp = false
            }
            if let view = handleView { view.window?.invalidateCursorRects(for: view) }
        }
    }

    var isDragging: Bool { anchor != nil }

    private struct DragAnchor {
        let pointer: CGPoint
        let origin: CGPoint
    }

    private weak var handleView: PositionDragView?
    private weak var draggingWindow: NSWindow?
    private var anchor: DragAnchor?
    private var consumesMouseUp = false

    fileprivate func register(_ view: PositionDragView) {
        guard handleView !== view else { return }
        finishDragging(notify: false)
        consumesMouseUp = false
        handleView = view
        view.window?.invalidateCursorRects(for: view)
    }

    fileprivate func unregister(_ view: PositionDragView) {
        guard handleView === view else { return }
        finishDragging(notify: false)
        consumesMouseUp = false
        handleView = nil
    }

    /// Returns true only for a drag beginning inside the registered label.
    func handle(_ event: NSEvent, in window: NSWindow) -> Bool {
        guard isEnabled else { return false }
        switch event.type {
        case .leftMouseDown:
            finishDragging(notify: false)
            consumesMouseUp = false
            guard let view = handleView, view.window === window,
                  !view.isHiddenOrHasHiddenAncestor, window.isVisible else { return false }
            // Read the actual layout at mouse-down; an asynchronously cached
            // SwiftUI frame could include a button or still have zero size.
            let region = view.convert(view.bounds, to: nil)
            guard !region.isEmpty, region.contains(event.locationInWindow) else { return false }
            draggingWindow = window
            anchor = DragAnchor(pointer: screenPoint(for: event), origin: window.frame.origin)
            consumesMouseUp = true
            window.invalidateCursorRects(for: view)
            NSCursor.closedHand.set()
            return true

        case .leftMouseDragged:
            guard consumesMouseUp else { return false }
            moveWindow(to: screenPoint(for: event))
            return true

        case .leftMouseUp:
            guard consumesMouseUp else { return false }
            moveWindow(to: screenPoint(for: event))
            finishDragging(notify: true)
            consumesMouseUp = false
            return true

        case .mouseMoved:
            // A normal move after dragging indicates that the event stream has
            // left the drag sequence, even if its mouse-up was not delivered.
            // Do not consult physical button state: remote and synthesized
            // mouse events may not update NSEvent.pressedMouseButtons.
            finishDragging(notify: true)
            consumesMouseUp = false
            return false

        default:
            return false
        }
    }

    private func screenPoint(for event: NSEvent) -> CGPoint {
        // Quartz's unflipped location is in AppKit global screen coordinates.
        // It belongs to this event, so queued events cannot be offset by a
        // window frame that has already moved. No display-scale conversion.
        event.cgEvent?.unflippedLocation ?? NSEvent.mouseLocation
    }

    private func moveWindow(to pointer: CGPoint) {
        guard let anchor, let window = draggingWindow else { return }
        window.setFrameOrigin(CGPoint(
            x: anchor.origin.x + pointer.x - anchor.pointer.x,
            y: anchor.origin.y + pointer.y - anchor.pointer.y
        ))
        NSCursor.closedHand.set()
    }

    private func finishDragging(notify: Bool) {
        guard isDragging else { return }
        anchor = nil
        let draggedWindow = draggingWindow
        draggingWindow = nil
        if let view = handleView, let window = view.window {
            window.invalidateCursorRects(for: view)
            let pointer = view.convert(window.mouseLocationOutsideOfEventStream, from: nil)
            (isEnabled && view.bounds.contains(pointer) ? NSCursor.openHand : NSCursor.arrow).set()
        } else {
            NSCursor.arrow.set()
        }
        guard notify, let draggedWindow else { return }
        NotificationCenter.default.post(
            name: Notification.Name("CodexIsland.positionDragEnded"),
            object: draggedWindow
        )
    }
}
