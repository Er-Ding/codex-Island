import AppKit
import CoreGraphics
import SwiftUI

/// Marks the header without taking hit testing away from its SwiftUI content.
struct HeaderDragHandle: NSViewRepresentable {
    func makeNSView(context: Context) -> HeaderDragView {
        HeaderDragView()
    }

    func updateNSView(_ nsView: HeaderDragView, context: Context) {
        nsView.window?.invalidateCursorRects(for: nsView)
    }

    static func dismantleNSView(_ nsView: HeaderDragView, coordinator: ()) {
        nsView.unregister()
    }
}

final class HeaderDragView: NSView {
    fileprivate weak var dragController: IslandHeaderDrag?

    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    override var mouseDownCanMoveWindow: Bool { false }

    override func resetCursorRects() {
        super.resetCursorRects()
        guard dragController?.canDrag?() == true else { return }
        addCursorRect(bounds, cursor: dragController?.isDragging == true ? .closedHand : .openHand)
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        unregister()
        guard let panel = window as? IslandPanel else { return }
        dragController = panel.headerDrag
        panel.headerDrag.register(self)
    }

    override func viewWillMove(toWindow newWindow: NSWindow?) {
        if newWindow !== window { unregister() }
        super.viewWillMove(toWindow: newWindow)
    }

    fileprivate func unregister() {
        // Clear the reference first: cancellation can synchronously change the
        // SwiftUI hierarchy and call this method again.
        let controller = dragController
        dragController = nil
        controller?.unregister(self)
    }
}

/// Routes an expanded-header mouse sequence before SwiftUI sees its clicks.
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
    }

    private weak var handleView: HeaderDragView?
    private weak var draggingWindow: NSWindow?
    private var anchor: DragAnchor?
    private var consumesMouseUp = false
    private var precedingHeaderClick = false
    private var doubleClickCandidate = false
    private var ownsCursor = false
    private var sequenceRevision: UInt = 0
    private var registrationRevision: UInt = 0

    fileprivate func register(_ view: HeaderDragView) {
        guard handleView !== view else { return }
        registrationRevision &+= 1
        let revision = registrationRevision
        handleView = nil
        cancel()
        // A cancellation callback may itself install a replacement header.
        guard revision == registrationRevision, view.dragController === self,
              view.window != nil else { return }
        handleView = view
        view.window?.invalidateCursorRects(for: view)
    }

    fileprivate func unregister(_ view: HeaderDragView) {
        guard handleView === view else { return }
        registrationRevision &+= 1
        handleView = nil
        cancel()
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
            guard let view = handleView, view.window === window,
                  !view.isHiddenOrHasHiddenAncestor, window.isVisible else { return false }
            let region = view.convert(view.bounds, to: nil)
            guard containsIncludingEdges(region, event.locationInWindow) else { return false }

            // Read the event's global point before settling the canvas. Its
            // locationInWindow belongs to the frame that received the event.
            guard let pointer = screenPoint(for: event) else { return false }
            let revision = sequenceRevision
            guard onBeginPress?() == true else { return false }
            guard revision == sequenceRevision, handleView === view,
                  view.window === window, !view.isHiddenOrHasHiddenAncestor,
                  window.isVisible else {
                consumesMouseUp = true
                onEndPress?(.cancelled)
                return true
            }
            draggingWindow = window
            anchor = DragAnchor(pointer: pointer, origin: window.frame.origin)
            isDragging = false
            doubleClickCandidate = candidate
            consumesMouseUp = true
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
            guard isPressed else {
                consumesMouseUp = false
                return true
            }
            let completion: Completion
            if isDragging {
                completion = .drag
            } else if headerContains(pointer, in: window) {
                completion = doubleClickCandidate ? .doubleClick : .click
            } else {
                completion = .cancelled
            }
            finish(completion)
            return true

        case .mouseMoved:
            // A normal move means a mouse-up was lost. Synthetic and remote
            // input may not update NSEvent.pressedMouseButtons reliably.
            if isPressed { cancel() }
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
        guard let view = handleView, let window = view.window else {
            releaseCursor()
            return
        }
        window.invalidateCursorRects(for: view)
        if isDragging {
            setCursor(.closedHand)
        } else {
            let pointer = view.convert(window.mouseLocationOutsideOfEventStream, from: nil)
            let canGrab = canDrag?() == true && window.isVisible
                && !view.isHiddenOrHasHiddenAncestor
                && containsIncludingEdges(view.bounds, pointer)
            if canGrab {
                setCursor(.openHand)
            } else {
                releaseCursor()
            }
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
        guard let view = handleView, view.window === window,
              !view.isHiddenOrHasHiddenAncestor, window.isVisible else { return false }
        let region = view.convert(view.bounds, to: nil)
        return containsIncludingEdges(window.convertToScreen(region), pointer)
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
        refreshCursor()
        onEndPress?(completion)
        refreshCursor()
    }
}
