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

    func setHover(_ hovering: Bool) {
        hoverTask?.cancel()
        guard !isPinned, !isAdjustingPosition, isExpanded != hovering else { return }
        hoverTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: hovering ? 100_000_000 : 280_000_000)
            guard !Task.isCancelled, let self, !self.isPinned, !self.isAdjustingPosition else { return }
            self.isExpanded = hovering
        }
    }

    func toggleExpanded() {
        guard !isAdjustingPosition else { return }
        hoverTask?.cancel()
        if isPinned {
            collapse()
        } else {
            isPinned = true
            isExpanded = true
        }
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

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

    override func sendEvent(_ event: NSEvent) {
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
}

@MainActor
final class IslandPanelController {
    let state: IslandState
    private let presentation = IslandPresentation()
    private let store: QuotaStore
    private(set) var panel: IslandPanel!
    private(set) var isVisible = true
    private var centerX: CGFloat = 0
    private var topY: CGFloat = 0
    private var activeScreen: NSScreen?
    private var adjustmentOriginDisplayID: String?
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
        panel.level = .statusBar
        panel.isFloatingPanel = true
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = true
        panel.isMovable = false
        panel.isMovableByWindowBackground = false
        panel.acceptsMouseMovedEvents = true
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

        // Published values are emitted before mutation. Deliver on the next main
        // run-loop pass so the window is sized from the updated view state.
        stateSubscription = state.objectWillChange
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in
                guard let self else { return }
                guard !self.state.isAdjustingPosition else { return }
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
        panel.orderFrontRegardless()
        startHoverMonitoring()
    }

    func show() {
        isVisible = true
        updateScreenGeometry()
        updateFrame(animated: false)
        panel.orderFrontRegardless()
        startHoverMonitoring()
    }

    func hide() {
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

    func close() {
        isVisible = false
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
        if (savedPosition != nil || state.isAdjustingPosition), let screen = activeScreen {
            return IslandPlacement.clampedFrame(frame, in: screen.frame, notchRect: Self.notchRect(on: screen))
        }
        return frame
    }

    func beginPositionAdjustment() {
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
        positionStore.reset()
        savedPosition = nil
        activeScreen = nil
        if state.isAdjustingPosition {
            endPositionAdjustment()
        } else {
            state.collapse()
            updateScreenGeometry()
            updateFrame(animated: false)
            if isVisible { panel.orderFrontRegardless() }
            checkHover()
        }
    }

    func setSizePreference(_ preference: IslandSizePreference) {
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
            panel.orderFrontRegardless()
            startHoverMonitoring()
        }
    }

    private func captureDraftPosition() {
        guard state.isAdjustingPosition else { return }
        let frame = panel.frame
        activeScreen = NSScreen.screens.max { first, second in
            let a = first.frame.intersection(frame), b = second.frame.intersection(frame)
            return a.width * a.height < b.width * b.height
        } ?? activeScreen
        centerX = frame.midX
        topY = frame.maxY
        // Update the appearance while dragging without resizing the window or
        // changing the drag anchor. Both local frame offsets remain zero.
        presentation.geometry = presentationGeometry(visible: frame, canvas: frame)
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
        hoverTimer?.invalidate()
        hoverTimer = nil
        hoverTracking = IslandHoverTracking()
    }

    private func checkHover(force: Bool = false) {
        guard hoverTimer != nil, isVisible, panel.isVisible, !menuIsTracking, !state.isAdjustingPosition else { return }
        updateMousePassthrough()
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
            if panel.frame != canvas { panel.setFrame(canvas, display: false) }
            // Commit the host layout together with any canvas resize. Do not
            // display an intermediate frame with the old content coordinates.
            panel.contentView?.needsLayout = true
            panel.contentView?.layoutSubtreeIfNeeded()
            panel.contentView?.needsDisplay = true
            panel.displayIfNeeded()
            CATransaction.commit()
        }
        updateMousePassthrough()
    }

    private func presentationGeometry(visible: NSRect, canvas: NSRect) -> IslandPresentationGeometry {
        let gap = activeScreen.map { max(0, $0.frame.maxY - visible.maxY) } ?? 100
        let pixel = 1 / max(1, activeScreen?.backingScaleFactor ?? 1)
        return IslandPresentationGeometry(visibleFrame: visible, canvasFrame: canvas,
                                          topClearance: gap <= pixel ? 0 : gap)
    }

    private func updateMousePassthrough() {
        guard frameTransition != nil, !state.isAdjustingPosition else {
            panel.ignoresMouseEvents = false
            return
        }
        guard !menuIsTracking else { return }
        let frame = presentation.geometry.visibleFrame
        let pointer = NSEvent.mouseLocation
        panel.ignoresMouseEvents = !(pointer.x >= frame.minX && pointer.x <= frame.maxX
            && pointer.y >= frame.minY && pointer.y <= frame.maxY)
    }

    private func updateScreenGeometry() {
        // Keep the current display when a menu or System Settings changes the
        // active app's screen; screen-specific sizing must not relocate the island.
        let requestedID = state.isAdjustingPosition ? activeScreen.map(Self.screenID)
            : (savedPosition?.displayID ?? activeScreen.map(Self.screenID))
        guard let screen = NSScreen.screens.first(where: { Self.screenID($0) == requestedID }) ?? Self.preferredScreen() else { return }
        activeScreen = screen
        if state.isAdjustingPosition || savedPosition != nil {
            state.notchHeight = 0
            state.notchWidth = 0
            updateUIScale(on: screen)
            if !state.isAdjustingPosition, let position = savedPosition {
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
        let screens = NSScreen.screens
        return screens.first { isBuiltIn($0) && $0.safeAreaInsets.top > 0 }
            ?? screens.first { isBuiltIn($0) }
            ?? NSScreen.main
            ?? screens.first
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
                self?.updateFrame(animated: false)
            }
        })
        for name in [NSWorkspace.activeSpaceDidChangeNotification, NSWorkspace.screensDidWakeNotification] {
            workspaceObservers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor in self?.screenEnvironmentChanged() }
            })
        }
        workspaceObservers.append(center.addObserver(
            forName: NSWorkspace.didWakeNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                self?.screenEnvironmentChanged()
                self?.store.refresh()
            }
        })
    }

    private func screenEnvironmentChanged() {
        // Screen/backing notifications also fire during cross-display drags.
        // settleDraftPosition will pick up the latest geometry on mouse-up.
        guard !panel.positionDrag.isDragging else { return }
        if state.isAdjustingPosition {
            settleDraftPosition()
            return
        }
        updateScreenGeometry()
        updateFrame(animated: false)
        if isVisible { panel.orderFrontRegardless() }
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
