import AppKit
import SwiftUI

/// Own the actual popup menu so the screen's UI scale reaches its options,
/// rather than stopping at the SwiftUI button that opens it.
@MainActor
final class IslandBucketMenu: NSObject, ObservableObject {
    weak var anchorView: NSView?
    private var activeMenu: NSMenu?
    private var onSelect: ((String) -> Void)?

    func present(buckets: [QuotaBucket], selectedID: String?, scale: CGFloat,
                 onSelect: @escaping (String) -> Void) {
        guard activeMenu == nil, buckets.count > 1,
              let anchor = anchorView, let window = anchor.window, window.isVisible else { return }

        let menu = NSMenu(title: "额度种类")
        menu.autoenablesItems = false
        menu.appearance = NSAppearance(named: .darkAqua)
        // This font belongs to the popup and every option, not just its trigger.
        // AppKit measures the native rows using this font and handles scrolling,
        // keyboard selection, Escape, highlighting and outside-click dismissal.
        let font = NSFont.systemFont(ofSize: 14 * scale, weight: .medium)
        menu.font = font
        menu.minimumWidth = anchor.bounds.width
        let checkmark = NSImage(systemSymbolName: "checkmark", accessibilityDescription: nil)?
            .withSymbolConfiguration(NSImage.SymbolConfiguration(pointSize: 12 * scale, weight: .semibold))
        checkmark?.size = NSSize(width: 12 * scale, height: 12 * scale)
        checkmark?.isTemplate = true

        for bucket in buckets {
            let item = NSMenuItem(title: bucket.name, action: #selector(selectBucket(_:)), keyEquivalent: "")
            item.attributedTitle = NSAttributedString(string: bucket.name, attributes: [.font: font])
            item.target = self
            item.representedObject = bucket.id
            item.state = bucket.id == selectedID ? .on : .off
            if let checkmark { item.onStateImage = checkmark }
            menu.addItem(item)
        }

        self.onSelect = onSelect
        activeMenu = menu
        defer {
            activeMenu = nil
            self.onSelect = nil
        }
        let gap = 4 * scale
        let bottom = anchor.isFlipped ? anchor.bounds.maxY + gap : anchor.bounds.minY - gap
        _ = menu.popUp(positioning: nil, at: NSPoint(x: anchor.bounds.minX, y: bottom), in: anchor)
    }

    func dismiss() {
        activeMenu?.cancelTracking()
    }

    @objc private func selectBucket(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? String else { return }
        onSelect?(id)
    }
}

/// Supplies coordinates only. The visible, accessible SwiftUI button receives
/// the click and opens the native menu using its full label bounds.
struct IslandBucketMenuAnchor: NSViewRepresentable {
    let menu: IslandBucketMenu

    func makeNSView(context: Context) -> IslandBucketMenuAnchorView {
        let view = IslandBucketMenuAnchorView()
        view.presenter = menu
        menu.anchorView = view
        return view
    }

    func updateNSView(_ nsView: IslandBucketMenuAnchorView, context: Context) {
        if let previous = nsView.presenter, previous !== menu, previous.anchorView === nsView {
            previous.dismiss()
            previous.anchorView = nil
        }
        nsView.presenter = menu
        menu.anchorView = nsView
    }

    static func dismantleNSView(_ nsView: IslandBucketMenuAnchorView, coordinator: ()) {
        guard let menu = nsView.presenter, menu.anchorView === nsView else { return }
        menu.dismiss()
        menu.anchorView = nil
    }
}

final class IslandBucketMenuAnchorView: NSView {
    weak var presenter: IslandBucketMenu?
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}
