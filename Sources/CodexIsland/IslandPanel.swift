import AppKit
import Combine
import CoreGraphics
import QuartzCore
import SwiftUI

@MainActor
final class IslandState: ObservableObject {
    @Published var isExpanded = false
    @Published var isPinned = false
    @Published var isAdjustingPosition = false
    @Published private(set) var collapseRevision = 0
    @Published var notchWidth: CGFloat = 180
    @Published var notchHeight: CGFloat = 32

    var compactWidth: CGFloat { isAdjustingPosition ? 300 : (notchWidth > 0 ? notchWidth + 152 : 224) }
    var headerHeight: CGFloat { max(36, notchHeight) }
    var width: CGFloat { isExpanded ? max(420, compactWidth) : compactWidth }
    var height: CGFloat { headerHeight + (isExpanded ? 300 : 0) }

    private var hoverTask: Task<Void, Never>?
    var onBeginPositionAdjustment: (() -> Void)?
    var onFinishPositionAdjustment: (() -> Void)?
    var onCancelPositionAdjustment: (() -> Void)?

    func beginPositionAdjustment() { onBeginPositionAdjustment?() }
    func finishPositionAdjustment() { onFinishPositionAdjustment?() }
    func cancelPositionAdjustment() { onCancelPositionAdjustment?() }

    func setHover(_ hovering: Bool) {
        hoverTask?.cancel()
        guard !isPinned, !isAdjustingPosition else { return }
        hoverTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: hovering ? 120_000_000 : 300_000_000)
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

/// A mouse-interactive panel which never takes keyboard focus from the current app.
final class IslandPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

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
    private let store: QuotaStore
    private(set) var panel: IslandPanel!
    private(set) var isVisible = true
    private var centerX: CGFloat = 0
    private var topY: CGFloat = 0
    private var activeScreen: NSScreen?
    private let positionStore = IslandPositionStore()
    private var savedPosition: SavedIslandPosition?
    var isAdjustingPosition: Bool { state.isAdjustingPosition }
    var hasCustomPosition: Bool { savedPosition != nil }
    private var stateSubscription: AnyCancellable?
    private var hoverTimer: Timer?
    private var hoverTracking = IslandHoverTracking()
    private var menuIsTracking = false
    private var previouslyPinned = false
    private var observedCollapseRevision = 0
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
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]

        let host = IslandHostingView(rootView: IslandView(store: store, state: state))
        host.sizingOptions = []
        host.frame = NSRect(origin: .zero, size: desiredFrame.size)
        host.autoresizingMask = [.width, .height]
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
                        region: self.isVisible ? self.panel.frame.union(self.desiredFrame) : nil)
                    return
                }
                // A manual collapse must stay collapsed until the next entry.
                self.checkHover(force: wasPinned && !self.state.isPinned && self.state.isExpanded)
            }

        observeScreenChanges()
        panel.setFrame(desiredFrame, display: true)
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
        panel.orderOut(nil)
    }

    func toggleVisibility() {
        isVisible ? hide() : show()
    }

    func close() {
        isVisible = false
        stopHoverMonitoring()
        stateSubscription?.cancel()
        stateSubscription = nil
        state.collapse()
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
        stopHoverMonitoring()
        state.collapse()
        state.isAdjustingPosition = true
        state.notchWidth = 0
        state.notchHeight = 0
        panel.isMovable = true
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
        // No preferences are written until the user chooses Done.
        endPositionAdjustment()
    }

    func resetPosition() {
        positionStore.reset()
        savedPosition = nil
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

    private func endPositionAdjustment() {
        state.isAdjustingPosition = false
        panel.isMovable = false
        state.collapse()
        updateScreenGeometry()
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
    }

    private func settleDraftPosition() {
        guard state.isAdjustingPosition else { return }
        captureDraftPosition()
        updateFrame(animated: false)
        centerX = panel.frame.midX
        topY = panel.frame.maxY
    }

    private func startHoverMonitoring() {
        guard hoverTimer == nil else { checkHover(); return }
        // Reading mouseLocation requires no event interception or extra permission.
        // Polling also covers the physical notch, where view hover events can stop.
        let timer = Timer(timeInterval: 0.1, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.checkHover() }
        }
        timer.tolerance = 0.02
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
        // Keep the destination covered while the panel is still expanding.
        let region = panel.frame.union(desiredFrame)
        if let inside = hoverTracking.update(pointer: NSEvent.mouseLocation, region: region, force: force) {
            state.setHover(inside)
        }
    }

    private func updateFrame(animated: Bool) {
        guard let panel else { return }
        let frame = desiredFrame
        guard panel.frame != frame else { return }
        if animated && isVisible && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion {
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.20
                context.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
                panel.animator().setFrame(frame, display: true)
            }
        } else {
            panel.setFrame(frame, display: true)
        }
    }

    private func updateScreenGeometry() {
        let requestedID = state.isAdjustingPosition ? activeScreen.map(Self.screenID) : savedPosition?.displayID
        guard let screen = NSScreen.screens.first(where: { Self.screenID($0) == requestedID }) ?? Self.preferredScreen() else { return }
        activeScreen = screen
        if state.isAdjustingPosition || savedPosition != nil {
            state.notchHeight = 0
            state.notchWidth = 0
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
        if hasNotch, gap > 0, let left, let right {
            centerX = (left.maxX + right.minX) / 2
        } else {
            centerX = screen.frame.midX
        }
        topY = screen.frame.maxY
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

        let center = NSWorkspace.shared.notificationCenter
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
        updateScreenGeometry()
        updateFrame(animated: false)
        if isVisible { panel.orderFrontRegardless() }
        checkHover()
    }

    /// Prints geometry only; no display names, serial numbers, or account data.
    static func printScreenDiagnostics() {
        for (index, screen) in NSScreen.screens.enumerated() {
            let inset = screen.safeAreaInsets.top
            print("Screen \(index + 1): builtIn=\(isBuiltIn(screen)) frame=\(NSStringFromRect(screen.frame)) scale=\(screen.backingScaleFactor) topInset=\(inset)")
            if let left = screen.auxiliaryTopLeftArea, let right = screen.auxiliaryTopRightArea {
                print("  topLeft=\(NSStringFromRect(left)) topRight=\(NSStringFromRect(right)) notchWidth=\(max(0, right.minX - left.maxX))")
            }
        }
    }
}
