import AppKit
import CoreGraphics

/// Routes header input using the visible island's geometry, independently of
/// SwiftUI layout and view replacement during expansion.
@MainActor
final class IslandHeaderDrag {
    enum Completion {
        case drag
        case click
        case doubleClick
        case cancelled
    }

    /// May settle an in-flight animation before the window origin is captured.
    var onBeginPress: (() -> Bool)?
    var onDrag: (() -> Void)?
    var onEndPress: ((Completion) -> Void)?
    /// Used only for the cursor; accepting a press is onBeginPress's decision.
    var canDrag: (() -> Bool)?

    var isPressed: Bool { anchor != nil }
    private(set) var isDragging = false

    private struct DragAnchor {
        let pointer: CGPoint
        let origin: CGPoint
        let pressedHeaderFrame: NSRect
    }

    private weak var headerWindow: NSWindow?
    private var headerFrame: NSRect = .zero
    private weak var draggingWindow: NSWindow?
    private var anchor: DragAnchor?
    private var consumesMouseUp = false
    private var precedingHeaderClick = false
    private var doubleClickCandidate = false
    private var ownsCursor = false
    private var sequenceRevision: UInt = 0

    /// The controller supplies screen coordinates in the same transaction as
    /// drawing the header. A layout change never cancels an active press.
    func updateHeaderFrame(_ frame: NSRect, in window: NSWindow) {
        headerWindow = window
        headerFrame = frame
        if !isDragging { refreshCursor() }
    }

    /// Called by the stable hosting view, so cursors need no SwiftUI marker.
    func addCursorRect(to view: NSView) {
        guard let window = headerWindow, view.window === window,
              window.isVisible, !view.isHiddenOrHasHiddenAncestor,
              canDrag?() == true, isValidHeaderFrame else { return }
        let region = view.convert(window.convertFromScreen(headerFrame), from: nil)
            .intersection(view.bounds)
        guard !region.isEmpty, !region.isNull else { return }
        view.addCursorRect(region, cursor: isDragging ? .closedHand : .openHand)
    }

    /// Cancelled sequences still own their matching mouse-up, so it cannot
    /// become a click in the underlying SwiftUI header after a rollback.
    func cancel() {
        sequenceRevision &+= 1
        let wasPressed = isPressed
        anchor = nil
        draggingWindow = nil
        isDragging = false
        doubleClickCandidate = false
        precedingHeaderClick = false
        if wasPressed { consumesMouseUp = true }
        refreshCursor()
        if wasPressed { onEndPress?(.cancelled) }
    }

    func handle(_ event: NSEvent, in window: NSWindow) -> Bool {
        switch event.type {
        case .leftMouseDown:
            if isPressed { cancel() }
            consumesMouseUp = false
            let candidate = precedingHeaderClick && event.clickCount == 2
            precedingHeaderClick = false
            // Read the event's global point before settling the canvas. Its
            // locationInWindow belongs to the frame that received the event.
            guard let pointer = screenPoint(for: event),
                  headerContains(pointer, in: window) else { return false }

            // Own the sequence before the callback can expand or lay out the
            // island. The controller can now suspend hover on this same down.
            sequenceRevision &+= 1
            let revision = sequenceRevision
            let pressedHeaderFrame = headerFrame
            draggingWindow = window
            anchor = DragAnchor(
                pointer: pointer,
                origin: window.frame.origin,
                pressedHeaderFrame: pressedHeaderFrame
            )
            isDragging = false
            doubleClickCandidate = candidate
            consumesMouseUp = true
            let accepted = onBeginPress?() == true
            guard revision == sequenceRevision, isPressed else {
                // An explicit cancellation already delivered its completion.
                consumesMouseUp = true
                return true
            }
            guard accepted else {
                anchor = nil
                draggingWindow = nil
                doubleClickCandidate = false
                consumesMouseUp = false
                refreshCursor()
                return false
            }
            guard window.isVisible else {
                cancel()
                return true
            }
            // Expansion may have changed the canvas. Keep the original event
            // point, paired with the new origin, to prevent an initial jump.
            anchor = DragAnchor(
                pointer: pointer,
                origin: window.frame.origin,
                pressedHeaderFrame: pressedHeaderFrame
            )
            refreshCursor()
            return true

        case .leftMouseDragged:
            guard consumesMouseUp else { return false }
            guard let pointer = screenPoint(for: event) else {
                cancel()
                return true
            }
            moveWindow(to: pointer)
            return true

        case .leftMouseUp:
            guard consumesMouseUp else { return false }
            guard let pointer = screenPoint(for: event) else {
                cancel()
                consumesMouseUp = false
                return true
            }
            moveWindow(to: pointer)
            // Cancellation may have happened while handling the last move.
            guard let anchor else {
                consumesMouseUp = false
                return true
            }
            let completion: Completion
            if isDragging {
                completion = .drag
            } else if headerContains(pointer, in: window)
                        || containsIncludingEdges(anchor.pressedHeaderFrame, pointer) {
                // Expanding near a screen edge can move the header. Layout
                // must not cancel an accepted click whose pointer stayed still.
                completion = doubleClickCandidate ? .doubleClick : .click
            } else {
                completion = .cancelled
            }
            finish(completion)
            return true

        case .mouseMoved:
            // Remote input can interleave ordinary moves with a held button.
            // Only explicit cancellation or mouse-up ends the owned sequence.
            refreshCursor()
            return false

        case .keyDown where event.keyCode == 53:
            guard isPressed else { return false }
            cancel()
            return true

        case .rightMouseDown, .otherMouseDown:
            cancel()
            return false

        default:
            return false
        }
    }

    func refreshCursor() {
        // Dragging only translates the window, preserving local cursor rects.
        // Avoid rebuilding the whole hosting view's rects on every movement.
        if isDragging {
            setCursor(.closedHand)
            return
        }
        guard let window = headerWindow else {
            releaseCursor()
            return
        }
        if let view = window.contentView {
            window.invalidateCursorRects(for: view)
        }
        let canGrab = canDrag?() == true
            && headerContains(NSEvent.mouseLocation, in: window)
        if canGrab {
            setCursor(.openHand)
        } else {
            releaseCursor()
        }
    }

    private func setCursor(_ cursor: NSCursor) {
        ownsCursor = true
        cursor.set()
    }

    private func releaseCursor() {
        // Display updates also run while the pointer is in another app. Only
        // release a cursor we set, rather than repeatedly replacing its cursor.
        guard ownsCursor else { return }
        ownsCursor = false
        NSCursor.arrow.set()
    }

    private func screenPoint(for event: NSEvent) -> CGPoint? {
        let pointer = event.cgEvent?.unflippedLocation ?? NSEvent.mouseLocation
        return pointer.x.isFinite && pointer.y.isFinite ? pointer : nil
    }

    private func containsIncludingEdges(_ rect: CGRect, _ point: CGPoint) -> Bool {
        // CGRect.contains excludes maxY, which is precisely the screen's top
        // edge when the header is attached to it.
        !rect.isEmpty && point.x.isFinite && point.y.isFinite
            && point.x >= rect.minX && point.x <= rect.maxX
            && point.y >= rect.minY && point.y <= rect.maxY
    }

    private func headerContains(_ pointer: CGPoint, in window: NSWindow) -> Bool {
        headerWindow === window && window.isVisible && isValidHeaderFrame
            && containsIncludingEdges(headerFrame, pointer)
    }

    private var isValidHeaderFrame: Bool {
        headerFrame.origin.x.isFinite && headerFrame.origin.y.isFinite
            && headerFrame.width.isFinite && headerFrame.height.isFinite
            && headerFrame.width > 0 && headerFrame.height > 0
    }

    private func moveWindow(to pointer: CGPoint) {
        guard let anchor, let window = draggingWindow else { return }
        guard window.isVisible else {
            cancel()
            return
        }
        // Windows starts moving on the first changed pointer coordinate. A
        // return to the starting point remains a drag, never a double-click.
        guard isDragging || pointer != anchor.pointer else { return }
        isDragging = true
        doubleClickCandidate = false
        let origin = CGPoint(
            x: anchor.origin.x + pointer.x - anchor.pointer.x,
            y: anchor.origin.y + pointer.y - anchor.pointer.y
        )
        guard origin.x.isFinite, origin.y.isFinite else {
            cancel()
            return
        }
        setCursor(.closedHand)
        guard window.frame.origin != origin else { return }
        let revision = sequenceRevision
        window.setFrameOrigin(origin)
        guard revision == sequenceRevision, isPressed else { return }
        onDrag?()
    }

    private func finish(_ completion: Completion) {
        sequenceRevision &+= 1
        anchor = nil
        draggingWindow = nil
        isDragging = false
        doubleClickCandidate = false
        consumesMouseUp = false
        if case .click = completion {
            precedingHeaderClick = true
        } else {
            precedingHeaderClick = false
        }
        // Clear ownership before callbacks, which can remove the header.
        onEndPress?(completion)
        refreshCursor()
    }
}
