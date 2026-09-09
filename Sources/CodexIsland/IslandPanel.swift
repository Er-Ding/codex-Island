import AppKit
import Combine
import CoreGraphics
import QuartzCore
import SwiftUI

@MainActor
final class IslandState: ObservableObject {
    private static let baseDetailHeight: CGFloat = 300

    @Published var isExpanded = false
    @Published var isPinned = false
    @Published var isAdjustingPosition = false
    @Published var isHeaderDragging = false
    @Published private(set) var collapseRevision = 0
    @Published var notchWidth: CGFloat = 180
    @Published var notchHeight: CGFloat = 32
    @Published var uiScale: CGFloat = 1

    var compactWidth: CGFloat { isAdjustingPosition ? 300 * uiScale : (notchWidth > 0 ? notchWidth + 152 * uiScale : 224 * uiScale) }
    var headerHeight: CGFloat { max(36 * uiScale, notchHeight) }
    var expandedWidth: CGFloat { max(420 * uiScale, compactWidth) }
    var detailHeight: CGFloat { Self.baseDetailHeight * uiScale }
    var width: CGFloat { isExpanded ? expandedWidth : compactWidth }
    var height: CGFloat { headerHeight + (isExpanded ? detailHeight : 0) }

    private var hoverTask: Task<Void, Never>?
    var onBeginPositionAdjustment: (() -> Void)?
    var onFinishPositionAdjustment: (() -> Void)?
    var onCancelPositionAdjustment: (() -> Void)?

    func beginPositionAdjustment() { onBeginPositionAdjustment?() }
    func finishPositionAdjustment() { onFinishPositionAdjustment?() }
    func cancelPositionAdjustment() { onCancelPositionAdjustment?() }

    func cancelPendingHover() { hoverTask?.cancel() }

    func setHover(_ hovering: Bool) {
        hoverTask?.cancel()
        guard !isPinned, !isAdjustingPosition, !isHeaderDragging, isExpanded != hovering else { return }
        hoverTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: hovering ? 100_000_000 : 280_000_000)
            guard !Task.isCancelled, let self, !self.isPinned,
                  !self.isAdjustingPosition, !self.isHeaderDragging else { return }
            self.isExpanded = hovering
        }
    }

    func togglePinned() {
        guard !isAdjustingPosition, !isHeaderDragging else { return }
        hoverTask?.cancel()
        // Keep both ends of the expanded header available for a second click.
        // Unpinning allows hover exit to collapse it, rather than moving it now.
        isPinned.toggle()
        isExpanded = true
    }

    func collapse() {
        hoverTask?.cancel()
        isPinned = false
        isExpanded = false
        collapseRevision += 1
    }
}

struct IslandPresentationGeometry {
    var visibleFrame: NSRect = .zero
    var canvasFrame: NSRect = .zero
    var topClearance: CGFloat = 0
}

/// Rendering geometry is independent of the requested expanded/pinned state.
@MainActor
final class IslandPresentation: ObservableObject {
    @Published var geometry = IslandPresentationGeometry()
}

private struct IslandFrameTransition {
    let start: NSRect
    let end: NSRect
    let canvas: NSRect
    let startedAt: CFTimeInterval
    let duration: TimeInterval

    func progress(at time: CFTimeInterval) -> CGFloat {
        CGFloat(min(1, max(0, (time - startedAt) / duration)))
    }

    func frame(at progress: CGFloat) -> NSRect {
        // The same geometric path is used in both directions, without overshoot.
        let remaining = 1 - progress
        let amount = 1 - remaining * remaining * remaining
        func mix(_ a: CGFloat, _ b: CGFloat) -> CGFloat { a + (b - a) * amount }
        let width = mix(start.width, end.width)
        let height = mix(start.height, end.height)
        return NSRect(x: mix(start.midX, end.midX) - width / 2,
                      y: mix(start.maxY, end.maxY) - height,
                      width: width, height: height)
    }
}

/// A mouse-interactive panel which never takes keyboard focus from the current app.
final class IslandPanel: NSPanel {
    let positionDrag = IslandPositionDrag()
    let headerDrag = IslandHeaderDrag()

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

    override func sendEvent(_ event: NSEvent) {
        if headerDrag.handle(event, in: self) { return }
        if positionDrag.handle(event, in: self) { return }
        super.sendEvent(event)
    }

    // The island intentionally occupies the menu bar region around the camera.
    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect {
        frameRect
    }
}

private final class IslandHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func resetCursorRects() {
        super.resetCursorRects()
        (window as? IslandPanel)?.headerDrag.addCursorRect(to: self)
    }
}

@MainActor
final class IslandPanelController {
    // Keep the island above the menu bar/status surfaces across app switches,
    // while allowing its native quota and status-item menus to open above it.
    private static let overlayLevel = NSWindow.Level(rawValue: NSWindow.Level.popUpMenu.rawValue - 1)

    let state: IslandState
    private let presentation = IslandPresentation()
    private let store: QuotaStore
    private(set) var panel: IslandPanel!
    private(set) var isVisible = true
    private var centerX: CGFloat = 0
    private var topY: CGFloat = 0
    private var activeScreen: NSScreen?
    private var adjustmentOriginDisplayID: String?
    private var headerPressOriginDisplayID: String?
    private let positionStore = IslandPositionStore()
    private let sizeStore = IslandSizeStore()
    private var savedPosition: SavedIslandPosition?
    var isAdjustingPosition: Bool { state.isAdjustingPosition }
    var hasCustomPosition: Bool { savedPosition != nil }
    var resolvedUIScale: CGFloat { state.uiScale }
    var sizePreference: IslandSizePreference {
        guard let activeScreen else { return .automatic }
        return sizeStore.load(displayID: Self.screenID(activeScreen))
    }
    private var stateSubscription: AnyCancellable?
    private var hoverTimer: Timer?
    private var hoverTracking = IslandHoverTracking()
    private var menuIsTracking = false
    private var previouslyPinned = false
    private var observedCollapseRevision = 0
    private var targetFrame: NSRect?
    private var frameTransition: IslandFrameTransition?
    private var animationTimer: Timer?
    private var transitionGeneration = 0
    private var observers: [NSObjectProtocol] = []
    private var workspaceObservers: [NSObjectProtocol] = []

    init(store: QuotaStore, initiallyExpanded: Bool) {
        self.store = store
        state = IslandState()
        state.isExpanded = initiallyExpanded
        state.isPinned = initiallyExpanded
        previouslyPinned = initiallyExpanded
        savedPosition = positionStore.load()
        updateScreenGeometry()

        panel = IslandPanel(
            contentRect: desiredFrame,
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.title = "Codex Island"
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.isFloatingPanel = true
        panel.level = Self.overlayLevel
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = true
        panel.isMovable = false
        panel.isMovableByWindowBackground = false
        panel.acceptsMouseMovedEvents = true
        // Keep reception enabled throughout expansion. Polling the pointer to
        // toggle this flag can drop a fast mouse-down before the next sample.
        panel.ignoresMouseEvents = false
        panel.isReleasedWhenClosed = false
        panel.animationBehavior = .none
        panel.preservesContentDuringLiveResize = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]

        presentation.geometry = presentationGeometry(visible: desiredFrame, canvas: desiredFrame)
        let host = IslandHostingView(rootView: IslandView(store: store, state: state, presentation: presentation))
        host.sizingOptions = []
        host.frame = NSRect(origin: .zero, size: desiredFrame.size)
        host.autoresizingMask = [.width, .height]
        host.wantsLayer = true
        host.layerContentsRedrawPolicy = .duringViewResize
        panel.contentView = host
        state.onBeginPositionAdjustment = { [weak self] in self?.beginPositionAdjustment() }
        state.onFinishPositionAdjustment = { [weak self] in self?.finishPositionAdjustment() }
        state.onCancelPositionAdjustment = { [weak self] in self?.cancelPositionAdjustment() }
        panel.headerDrag.canDrag = { [weak self] in self?.canBeginHeaderPress == true }
        panel.headerDrag.onBeginPress = { [weak self] in self?.beginHeaderPress() == true }
        panel.headerDrag.onDrag = { [weak self] in
            guard let self else { return }
            if !self.state.isHeaderDragging { self.state.isHeaderDragging = true }
            self.captureDraftPosition()
        }
        panel.headerDrag.onEndPress = { [weak self] completion in self?.finishHeaderPress(completion) }

        // Published values are emitted before mutation. Deliver on the next main
        // run-loop pass so the window is sized from the updated view state.
        stateSubscription = state.objectWillChange
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in
                guard let self else { return }
                guard !self.state.isAdjustingPosition, !self.panel.headerDrag.isPressed else { return }
                let wasPinned = self.previouslyPinned
                self.previouslyPinned = self.state.isPinned
                self.updateFrame(animated: true)
                if self.observedCollapseRevision != self.state.collapseRevision {
                    self.observedCollapseRevision = self.state.collapseRevision
                    // A click can arrive before the next mouse-position sample.
                    // Record it without scheduling a fresh hover expansion.
                    _ = self.hoverTracking.update(pointer: NSEvent.mouseLocation,
                        region: self.isVisible ? self.presentation.geometry.visibleFrame.union(self.desiredFrame) : nil)
                    return
                }
                // A manual collapse must stay collapsed until the next entry.
                self.checkHover(force: wasPinned && !self.state.isPinned && self.state.isExpanded)
            }

        observeScreenChanges()
        updateFrame(animated: false)
        maintainWindowOrdering()
        startHoverMonitoring()
    }

    func show() {
        isVisible = true
        updateScreenGeometry()
        updateFrame(animated: false)
        maintainWindowOrdering()
        startHoverMonitoring()
    }

    func hide() {
        panel.headerDrag.cancel()
        if state.isAdjustingPosition { cancelPositionAdjustment() }
        isVisible = false
        stopHoverMonitoring()
        state.collapse()
        updateFrame(animated: false)
        panel.orderOut(nil)
    }

    func toggleVisibility() {
        isVisible ? hide() : show()
    }

    private func maintainWindowOrdering() {
        guard isVisible else { return }
        if panel.level != Self.overlayLevel { panel.level = Self.overlayLevel }
        // Reorder without activating the app, stealing focus, resizing the
        // canvas, or changing the current expansion/drag/hover state.
        panel.orderFrontRegardless()
    }

    func close() {
        isVisible = false
        panel.headerDrag.cancel()
        panel.positionDrag.isEnabled = false
        stopHoverMonitoring()
        stateSubscription?.cancel()
        stateSubscription = nil
        state.collapse()
        updateFrame(animated: false)
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
        for observer in workspaceObservers { NSWorkspace.shared.notificationCenter.removeObserver(observer) }
        observers.removeAll()
        workspaceObservers.removeAll()
        panel.close()
    }

    private var desiredFrame: NSRect {
        let frame = NSRect(x: centerX - state.width / 2, y: topY - state.height, width: state.width, height: state.height)
        if (savedPosition != nil || state.isAdjustingPosition || state.isHeaderDragging), let screen = activeScreen {
            return IslandPlacement.clampedFrame(frame, in: screen.frame, notchRect: Self.notchRect(on: screen))
        }
        return frame
    }

    private var canBeginHeaderPress: Bool {
        isVisible && panel.isVisible && !state.isAdjustingPosition
            && !menuIsTracking && !panel.positionDrag.isDragging
    }

    private func beginHeaderPress() -> Bool {
        guard canBeginHeaderPress else { return false }
        headerPressOriginDisplayID = activeScreen.map(Self.screenID)
        stopHoverMonitoring()
        // A press can precede the hover delay or arrive during the reveal. Own
        // that same sequence immediately; pinning still happens only on click-up.
        if !state.isExpanded { state.isExpanded = true }
        // An already expanded panel needs no synchronous layout/redraw before
        // its first move. Only settle if there is still a reveal to finish.
        if frameTransition != nil || presentation.geometry.visibleFrame != desiredFrame
            || presentation.geometry.canvasFrame != desiredFrame {
            updateFrame(animated: false)
        }
        return true
    }

    private func finishHeaderPress(_ completion: IslandHeaderDrag.Completion) {
        switch completion {
        case .drag:
            // The gesture has released its anchor, so destination-display sizing
            // can now run without shifting the content under the held pointer.
            captureDraftPosition()
            updateScreenGeometry()
            updateFrame(animated: false)
            if let screen = activeScreen {
                centerX = panel.frame.midX
                topY = panel.frame.maxY
                let position = SavedIslandPosition(displayID: Self.screenID(screen),
                    frame: panel.frame, screenFrame: screen.frame)
                positionStore.save(position)
                savedPosition = position
            }
            state.isHeaderDragging = false

        case .click:
            restoreHeaderPressPlacement()
            state.togglePinned()
            updateFrame(animated: false)

        case .doubleClick:
            state.isHeaderDragging = false
            resetPosition()

        case .cancelled:
            // Interrupted mouse sequences are drafts, never saved placements.
            restoreHeaderPressPlacement()
        }
        headerPressOriginDisplayID = nil
        resumeHoverAfterHeaderPress()
    }

    private func restoreHeaderPressPlacement() {
        state.isHeaderDragging = false
        if savedPosition == nil {
            activeScreen = NSScreen.screens.first { Self.screenID($0) == headerPressOriginDisplayID }
                ?? Self.preferredScreen()
        }
        updateScreenGeometry()
        updateFrame(animated: false)
    }

    private func resumeHoverAfterHeaderPress() {
        state.cancelPendingHover()
        hoverTracking = IslandHoverTracking()
        if !state.isExpanded {
            // Reset may land directly under the pointer. Require a fresh entry
            // before expanding again so a double-click stays visibly collapsed.
            _ = hoverTracking.update(pointer: NSEvent.mouseLocation,
                region: isVisible ? presentation.geometry.visibleFrame : nil)
        }
        if isVisible {
            startHoverMonitoring()
            // Destination scaling/clamping or cancellation can leave the panel
            // away from the pointer. A fresh outside sample must still schedule
            // collapse even though the tracker also starts outside.
            if state.isExpanded { checkHover(force: true) }
        }
    }

    func beginPositionAdjustment() {
        panel.headerDrag.cancel()
        guard !state.isAdjustingPosition else { return }
        if !isVisible { show() }
        adjustmentOriginDisplayID = activeScreen.map(Self.screenID)
        stopHoverMonitoring()
        state.collapse()
        state.isAdjustingPosition = true
        state.notchWidth = 0
        state.notchHeight = 0
        // Moving is handled before SwiftUI hit testing in IslandPanel.sendEvent.
        // Keep Window Server dragging disabled so there is only one drag owner.
        panel.positionDrag.isEnabled = true
        updateFrame(animated: false)
        // Adopt the visible draft, including any camera avoidance adjustment.
        centerX = panel.frame.midX
        topY = panel.frame.maxY
    }

    func finishPositionAdjustment() {
        guard state.isAdjustingPosition else { return }
        settleDraftPosition()
        guard let screen = activeScreen else { cancelPositionAdjustment(); return }
        let position = SavedIslandPosition(displayID: Self.screenID(screen), frame: panel.frame, screenFrame: screen.frame)
        positionStore.save(position)
        savedPosition = position
        endPositionAdjustment()
    }

    func cancelPositionAdjustment() {
        guard state.isAdjustingPosition else { return }
        if savedPosition == nil {
            // The default placement has no stored display ID. Cancel must still
            // return to the screen where this adjustment started.
            activeScreen = NSScreen.screens.first { Self.screenID($0) == adjustmentOriginDisplayID }
                ?? Self.preferredScreen()
        }
        // No preferences are written until the user chooses Done.
        endPositionAdjustment()
    }

    func resetPosition() {
        panel.headerDrag.cancel()
        positionStore.reset()
        savedPosition = nil
        activeScreen = Self.preferredScreen()
        if state.isAdjustingPosition {
            endPositionAdjustment()
        } else {
            state.collapse()
            updateScreenGeometry()
            updateFrame(animated: false)
            maintainWindowOrdering()
            checkHover()
        }
    }

    func setSizePreference(_ preference: IslandSizePreference) {
        panel.headerDrag.cancel()
        guard !state.isAdjustingPosition, let screen = activeScreen else { return }
        sizeStore.save(preference, displayID: Self.screenID(screen))
        screenEnvironmentChanged()
    }

    private func endPositionAdjustment() {
        state.isAdjustingPosition = false
        panel.positionDrag.isEnabled = false
        state.collapse()
        updateScreenGeometry()
        adjustmentOriginDisplayID = nil
        updateFrame(animated: false)
        if isVisible {
            maintainWindowOrdering()
            startHoverMonitoring()
        }
    }

    private func captureDraftPosition() {
        guard state.isAdjustingPosition || state.isHeaderDragging else { return }
        let frame = panel.frame
        // Header dragging reports each move synchronously; didMove may report
        // that same frame again on the next run-loop pass.
        guard presentation.geometry.visibleFrame != frame
            || presentation.geometry.canvasFrame != frame else { return }
        activeScreen = screenContainingMost(of: frame) ?? activeScreen
        centerX = frame.midX
        topY = frame.maxY
        // Update the appearance while dragging without resizing the window or
        // changing the drag anchor. Both local frame offsets remain zero.
        presentation.geometry = presentationGeometry(visible: frame, canvas: frame)
        updateHeaderInteraction(visible: frame)
        targetFrame = frame
    }

    private func screenContainingMost(of frame: NSRect) -> NSScreen? {
        func overlap(_ screen: NSScreen) -> CGFloat {
            let intersection = screen.frame.intersection(frame)
            return intersection.isNull ? 0 : intersection.width * intersection.height
        }
        // Prefer the current display on a tie, including a frame temporarily
        // outside every display. Motion itself remains entirely unconstrained.
        let screenNumberKey = NSDeviceDescriptionKey("NSScreenNumber")
        let currentNumber = activeScreen?.deviceDescription[screenNumberKey] as? NSNumber
        // Use the numeric ID for this per-move comparison; the persistent UUID
        // only needs to be encoded when the final position is saved.
        var best = NSScreen.screens.first {
            ($0.deviceDescription[screenNumberKey] as? NSNumber) == currentNumber
        }
        for screen in NSScreen.screens {
            if let current = best, overlap(screen) <= overlap(current) { continue }
            best = screen
        }
        return best
    }

    private func settleDraftPosition() {
        guard state.isAdjustingPosition else { return }
        captureDraftPosition()
        // Keep the drag anchor and handle stable while the button is held.
        // Apply the destination display's scale only after releasing the drag.
        updateScreenGeometry()
        updateFrame(animated: false)
        centerX = panel.frame.midX
        topY = panel.frame.maxY
    }

    private func startHoverMonitoring() {
        guard hoverTimer == nil else { checkHover(); return }
        // Reading mouseLocation requires no event interception or extra permission.
        // Polling also covers the physical notch, where view hover events can stop.
        let timer = Timer(timeInterval: 0.05, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.checkHover() }
        }
        timer.tolerance = 0.01
        hoverTimer = timer
        RunLoop.main.add(timer, forMode: .common)
        checkHover()
    }

    private func stopHoverMonitoring() {
        state.cancelPendingHover()
        hoverTimer?.invalidate()
        hoverTimer = nil
        hoverTracking = IslandHoverTracking()
    }

    private func checkHover(force: Bool = false) {
        guard hoverTimer != nil, isVisible, panel.isVisible, !menuIsTracking,
              !state.isAdjustingPosition, !panel.headerDrag.isPressed else { return }
        // The animation canvas also contains transparent space, which is not
        // part of the island. Only the visible shape and its destination count.
        let region = presentation.geometry.visibleFrame.union(desiredFrame)
        if let inside = hoverTracking.update(pointer: NSEvent.mouseLocation, region: region, force: force) {
            state.setHover(inside)
        }
    }

    private func updateFrame(animated: Bool) {
        guard panel != nil else { return }
        let frame = desiredFrame
        if animated && isVisible && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion {
            // One logical change can publish several times (expanded, pinned,
            // collapse revision). Never restart a transition to the same target.
            guard targetFrame != frame else { return }
            targetFrame = frame
            let start = presentation.geometry.visibleFrame
            let canvas = presentation.geometry.canvasFrame.union(start).union(frame)
            stopFrameTransition()
            guard start != frame else {
                displayPresentation(visible: frame, canvas: frame)
                return
            }
            let distance = min(1, max(
                abs(start.height - frame.height) / state.detailHeight,
                abs(start.width - frame.width) / max(1, state.expandedWidth - state.compactWidth)
            ))
            frameTransition = IslandFrameTransition(
                start: start, end: frame, canvas: canvas, startedAt: CACurrentMediaTime(),
                duration: (state.isExpanded ? 0.36 : 0.28) * max(0.45, Double(distance).squareRoot())
            )
            // Resize the backing window once. Every intermediate frame only
            // redraws inside this fixed canvas, avoiding stale resized surfaces.
            displayPresentation(visible: start, canvas: canvas)
            let generation = transitionGeneration
            let frameRate = max(60, min(120, activeScreen?.maximumFramesPerSecond ?? 60))
            let timer = Timer(timeInterval: 1 / Double(frameRate), repeats: true) { [weak self] _ in
                Task { @MainActor in
                    guard let self, self.transitionGeneration == generation else { return }
                    self.advanceFrameTransition()
                }
            }
            animationTimer = timer
            RunLoop.main.add(timer, forMode: .common)
        } else {
            targetFrame = frame
            stopFrameTransition()
            displayPresentation(visible: frame, canvas: frame)
        }
    }

    private func advanceFrameTransition() {
        guard let transition = frameTransition else { return }
        let progress = transition.progress(at: CACurrentMediaTime())
        if progress >= 1 {
            stopFrameTransition()
            // Rebase the content and trim the transparent canvas in one update.
            displayPresentation(visible: transition.end, canvas: transition.end)
            checkHover()
        } else {
            displayPresentation(visible: transition.frame(at: progress), canvas: transition.canvas)
        }
    }

    private func stopFrameTransition() {
        animationTimer?.invalidate()
        animationTimer = nil
        frameTransition = nil
        // Queued ticks from an interrupted transition cannot finish a new one.
        transitionGeneration &+= 1
    }

    private func displayPresentation(visible: NSRect, canvas: NSRect) {
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0
            context.allowsImplicitAnimation = false
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            presentation.geometry = presentationGeometry(visible: visible, canvas: canvas)
            updateHeaderInteraction(visible: visible)
            if panel.frame != canvas { panel.setFrame(canvas, display: false) }
            // Commit the host layout together with any canvas resize. Do not
            // display an intermediate frame with the old content coordinates.
            panel.contentView?.needsLayout = true
            panel.contentView?.layoutSubtreeIfNeeded()
            panel.contentView?.needsDisplay = true
            panel.displayIfNeeded()
            CATransaction.commit()
        }
        if frameTransition == nil { panel.headerDrag.refreshCursor() }
    }

    private func presentationGeometry(visible: NSRect, canvas: NSRect) -> IslandPresentationGeometry {
        let gap = activeScreen.map { max(0, $0.frame.maxY - visible.maxY) } ?? 100
        let pixel = 1 / max(1, activeScreen?.backingScaleFactor ?? 1)
        return IslandPresentationGeometry(visibleFrame: visible, canvasFrame: canvas,
                                          topClearance: gap <= pixel ? 0 : gap)
    }

    private func updateHeaderInteraction(visible: NSRect) {
        let height = min(state.headerHeight, visible.height)
        let header = state.isAdjustingPosition ? NSRect.zero : NSRect(
            x: visible.minX, y: visible.maxY - height,
            width: visible.width, height: height
        )
        // Publish hit geometry in the same update as the visible frame. Input
        // never waits for a SwiftUI overlay to lay out or register its NSView.
        panel.headerDrag.updateHeaderFrame(header, in: panel)
    }

    private func updateScreenGeometry() {
        // Keep the current display when a menu or System Settings changes the
        // active app's screen; screen-specific sizing must not relocate the island.
        let isMoving = state.isAdjustingPosition || state.isHeaderDragging
        let requestedID = isMoving ? activeScreen.map(Self.screenID)
            : (savedPosition?.displayID ?? activeScreen.map(Self.screenID))
        guard let screen = NSScreen.screens.first(where: { Self.screenID($0) == requestedID }) ?? Self.preferredScreen() else { return }
        activeScreen = screen
        if isMoving || savedPosition != nil {
            state.notchHeight = 0
            state.notchWidth = 0
            updateUIScale(on: screen)
            if !isMoving, let position = savedPosition {
                // Retain the saved anchor when expanded so collapsing returns to it.
                let frame = position.frame(in: screen.frame, size: CGSize(width: state.compactWidth, height: state.headerHeight))
                centerX = frame.midX
                topY = frame.maxY
            }
            return
        }
        let topInset = screen.safeAreaInsets.top
        let left = screen.auxiliaryTopLeftArea
        let right = screen.auxiliaryTopRightArea
        let gap = left.flatMap { leftArea in right.map { $0.minX - leftArea.maxX } } ?? 0
        let hasNotch = topInset > 0

        state.notchHeight = hasNotch ? topInset : 0
        state.notchWidth = hasNotch ? (gap > 0 ? gap : 180) : 0
        updateUIScale(on: screen)
        if hasNotch, gap > 0, let left, let right {
            centerX = (left.maxX + right.minX) / 2
        } else {
            centerX = screen.frame.midX
        }
        topY = screen.frame.maxY
    }

    private func updateUIScale(on screen: NSScreen) {
        let preference = sizeStore.load(displayID: Self.screenID(screen))
        let scale = IslandSizing.scale(for: screen.frame.size, preference: preference,
                                       notchSize: CGSize(width: state.notchWidth, height: state.notchHeight))
        if state.uiScale != scale { state.uiScale = scale }
    }

    private static func screenID(_ screen: NSScreen) -> String {
        guard let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else { return "default" }
        if let uuid = CGDisplayCreateUUIDFromDisplayID(number.uint32Value)?.takeRetainedValue() {
            return CFUUIDCreateString(nil, uuid) as String
        }
        return number.stringValue
    }

    private static func notchRect(on screen: NSScreen) -> CGRect? {
        let height = screen.safeAreaInsets.top
        guard height > 0 else { return nil }
        if let left = screen.auxiliaryTopLeftArea, let right = screen.auxiliaryTopRightArea {
            return CGRect(x: left.maxX, y: screen.frame.maxY - height, width: right.minX - left.maxX, height: height)
        }
        return CGRect(x: screen.frame.midX - 90, y: screen.frame.maxY - height, width: 180, height: height)
    }

    static func preferredScreen() -> NSScreen? {
        // The first NSScreen is the primary/menu-bar display. NSScreen.main
        // follows keyboard focus and could reset onto an unrelated display.
        NSScreen.screens.first ?? NSScreen.main
    }

    static func isBuiltIn(_ screen: NSScreen) -> Bool {
        guard let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else {
            return false
        }
        return CGDisplayIsBuiltin(number.uint32Value) != 0
    }

    private func observeScreenChanges() {
        observers.append(NotificationCenter.default.addObserver(
            forName: NSWindow.didMoveNotification, object: panel, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.captureDraftPosition() }
        })
        observers.append(NotificationCenter.default.addObserver(
            forName: Notification.Name("CodexIsland.positionDragEnded"), object: panel, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.settleDraftPosition() }
        })
        observers.append(NotificationCenter.default.addObserver(
            forName: NSMenu.didBeginTrackingNotification, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                self.menuIsTracking = true
                self.panel.headerDrag.cancel()
                // Cancel an already scheduled exit while choosing a menu item.
                if self.isVisible && self.state.isExpanded { self.state.setHover(true) }
            }
        })
        observers.append(NotificationCenter.default.addObserver(
            forName: NSMenu.didEndTrackingNotification, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                self.menuIsTracking = false
                self.checkHover(force: true)
            }
        })
        observers.append(NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.screenEnvironmentChanged() }
        })
        observers.append(NotificationCenter.default.addObserver(
            forName: NSWindow.didChangeBackingPropertiesNotification, object: panel, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.screenEnvironmentChanged() }
        })

        let center = NSWorkspace.shared.notificationCenter
        workspaceObservers.append(center.addObserver(
            forName: NSWorkspace.accessibilityDisplayOptionsDidChangeNotification,
            object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                guard NSWorkspace.shared.accessibilityDisplayShouldReduceMotion else { return }
                self?.panel.headerDrag.cancel()
                self?.updateFrame(animated: false)
            }
        })
        // Switching applications/Spaces changes Window Server ordering, not
        // display geometry. Do not reset an in-flight reveal or drag here.
        for name in [NSWorkspace.didActivateApplicationNotification, NSWorkspace.activeSpaceDidChangeNotification] {
            workspaceObservers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor in self?.maintainWindowOrdering() }
            })
        }
        workspaceObservers.append(center.addObserver(
            forName: NSWorkspace.screensDidWakeNotification, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                self?.panel.headerDrag.cancel()
                self?.screenEnvironmentChanged()
            }
        })
        workspaceObservers.append(center.addObserver(
            forName: NSWorkspace.didWakeNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                self?.panel.headerDrag.cancel()
                self?.screenEnvironmentChanged()
                self?.store.refresh()
            }
        })
        for name in [NSWorkspace.willSleepNotification, NSWorkspace.screensDidSleepNotification,
                     NSWorkspace.sessionDidResignActiveNotification] {
            workspaceObservers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor in self?.panel.headerDrag.cancel() }
            })
        }
    }

    private func screenEnvironmentChanged() {
        // Screen/backing notifications also fire during cross-display drags.
        // The release handler will pick up the latest geometry on mouse-up.
        guard !panel.positionDrag.isDragging, !panel.headerDrag.isPressed else { return }
        if state.isAdjustingPosition {
            settleDraftPosition()
            return
        }
        updateScreenGeometry()
        updateFrame(animated: false)
        maintainWindowOrdering()
        checkHover()
    }

    /// Prints geometry only; no display names, serial numbers, or account data.
    static func printScreenDiagnostics() {
        for (index, screen) in NSScreen.screens.enumerated() {
            let inset = screen.safeAreaInsets.top
            let preference = IslandSizeStore().load(displayID: screenID(screen))
            let uiScale = IslandSizing.scale(for: screen.frame.size, preference: preference)
            print("Screen \(index + 1): builtIn=\(isBuiltIn(screen)) frame=\(NSStringFromRect(screen.frame)) backingScale=\(screen.backingScaleFactor) uiScale=\(uiScale) sizeSetting=\(preference.title) topInset=\(inset)")
            if let left = screen.auxiliaryTopLeftArea, let right = screen.auxiliaryTopRightArea {
                print("  topLeft=\(NSStringFromRect(left)) topRight=\(NSStringFromRect(right)) notchWidth=\(max(0, right.minX - left.maxX))")
            }
        }
    }
}
