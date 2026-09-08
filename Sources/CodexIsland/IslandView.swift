import SwiftUI

/// The visible island animates inside a fixed, transparent window canvas.
@MainActor
struct IslandView: View {
    @ObservedObject var store: QuotaStore
    @ObservedObject var state: IslandState
    @ObservedObject var presentation: IslandPresentation
    @State private var selectedBucketID: String?
    @StateObject private var bucketMenu = IslandBucketMenu()

    private let mint = Color(red: 0.46, green: 0.88, blue: 0.70)

    private var selectedBucket: QuotaBucket? {
        if let id = selectedBucketID,
           let bucket = store.snapshot?.buckets.first(where: { $0.id == id }) {
            return bucket
        }
        return store.snapshot?.mainBucket
    }
    private var primary: QuotaWindow? { selectedBucket?.primary ?? selectedBucket?.secondary }
    private var secondary: QuotaWindow? { selectedBucket?.primary == nil ? nil : selectedBucket?.secondary }
    // Scale layout and typography in points so text stays natively rendered.
    private func s(_ base: CGFloat) -> CGFloat { base * state.uiScale }

    // Keep detail layout stable while the window reveals or clips it.
    private var contentWidth: CGFloat { max(0, state.expandedWidth - s(36)) }
    private var titleWidth: CGFloat { max(0, contentWidth - s(139)) }
    private var cardWidth: CGFloat { secondary == nil ? contentWidth : max(0, (contentWidth - s(10)) / 2) }

    var body: some View {
        GeometryReader { _ in
            let geometry = presentation.geometry
            let size = geometry.visibleFrame.size
            let expansion = min(1, max(0, (size.height - state.headerHeight) / state.detailHeight))
            let reveal = min(1, max(0, (expansion - 0.12) / 0.70))
            let outline = IslandOutline(expansion: expansion, topClearance: geometry.topClearance, uiScale: state.uiScale)
            let border = IslandOutline(expansion: expansion, topClearance: geometry.topClearance,
                                       uiScale: state.uiScale, omitsTopEdge: geometry.topClearance == 0)

            ZStack(alignment: .top) {
                // Keep the same view alive in both directions. Removing it when
                // isExpanded flips would collapse the content before the window.
                expandedContent
                    .frame(width: state.expandedWidth, height: state.detailHeight, alignment: .top)
                    .opacity(Double(reveal * reveal * (3 - 2 * reveal)))
                    .offset(y: state.headerHeight)
                    .allowsHitTesting(state.isExpanded && expansion > 0.9)
                    .accessibilityHidden(!state.isExpanded || state.isAdjustingPosition)

                Group {
                    if state.isAdjustingPosition {
                        positionAdjustmentHeader
                    } else {
                        compactHeader
                    }
                }
                .frame(width: size.width, height: state.headerHeight)
                .overlay {
                    if !state.isAdjustingPosition { HeaderDragHandle() }
                }
            }
            .frame(width: size.width, height: size.height, alignment: .top)
            .background(outline.fill(Color(white: 0.025)))
            .overlay {
                border
                    .stroke(state.isAdjustingPosition || state.isHeaderDragging
                            ? mint.opacity(0.55) : Color.white.opacity(0.085), lineWidth: s(1))
                    .allowsHitTesting(false)
            }
            .clipShape(outline)
            .offset(x: geometry.visibleFrame.minX - geometry.canvasFrame.minX,
                    y: geometry.canvasFrame.maxY - geometry.visibleFrame.maxY)
        }
        // Presentation updates are already timed by the controller. Implicit
        // layer/SwiftUI animation would blend old and new frames a second time.
        .transaction { $0.animation = nil }
        .preferredColorScheme(.dark)
        .onChange(of: state.isExpanded) { expanded in
            if !expanded { bucketMenu.dismiss() }
            guard expanded, !store.isRefreshing else { return }
            if store.snapshot.map({ Date().timeIntervalSince($0.fetchedAt) > 5 }) ?? true {
                store.refresh()
            }
        }
        .onChange(of: state.uiScale) { _ in bucketMenu.dismiss() }
        .onDisappear { bucketMenu.dismiss() }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(state.isAdjustingPosition ? "调整灵动岛位置" : "Codex 额度")
    }

    private var positionAdjustmentHeader: some View {
        HStack(spacing: s(8)) {
            HStack(spacing: s(8)) {
                Image(systemName: "arrow.up.left.and.arrow.down.right")
                    .font(.system(size: s(12), weight: .medium))
                    .foregroundStyle(mint)
                Text("拖动调整位置")
                    .font(.system(size: s(12), weight: .medium))
                    .foregroundStyle(Color.white.opacity(0.90))
                    .lineLimit(1)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
            .overlay { PositionDragHandle() }
            .help("按住这里拖动，选好位置后点击完成")

            Button { state.finishPositionAdjustment() } label: {
                Text("完成")
                    .font(.system(size: s(11), weight: .semibold))
                    .foregroundStyle(Color.black.opacity(0.90))
                    .frame(width: s(44), height: s(24))
                    .background(Capsule().fill(mint))
                    .contentShape(Capsule())
            }
            .help("保存当前位置")
            .accessibilityLabel("完成并保存位置")

            Button { state.cancelPositionAdjustment() } label: {
                Image(systemName: "xmark")
                    .font(.system(size: s(10), weight: .semibold))
                    .foregroundStyle(Color.white.opacity(0.60))
                    .frame(width: s(22), height: s(24))
                    .contentShape(Rectangle())
            }
            .help("取消，回到调整前的位置")
            .accessibilityLabel("取消位置调整")
        }
        .buttonStyle(.plain)
        .padding(.horizontal, s(11))
    }

    private var compactHeader: some View {
        HStack(spacing: 0) {
            Group {
                if secondary != nil {
                    compactValue(primary, symbol: "sparkle", label: primary?.periodTitle ?? "当前周期")
                } else {
                    HStack(spacing: s(5)) {
                        Image(systemName: "sparkle")
                            .foregroundStyle(mint)
                        Text(primary?.periodTitle ?? "Codex")
                            .foregroundStyle(Color.white.opacity(0.65))
                            .lineLimit(1)
                            .minimumScaleFactor(0.8)
                    }
                    .font(.system(size: s(10), weight: .medium))
                }
            }
            .frame(maxWidth: .infinity)

            // No text or controls may enter the physical camera cutout.
            Color.clear.frame(width: state.notchWidth > 0 ? state.notchWidth : s(12))

            compactValue(secondary ?? primary, symbol: nil,
                         label: (secondary ?? primary)?.periodTitle ?? "当前周期",
                         showPeriod: secondary != nil)
                .frame(maxWidth: .infinity)
        }
        .padding(.horizontal, s(5))
        .contentShape(Rectangle())
        .onTapGesture {
            // Expanded-header mouse sequences belong to IslandPanel so dragging
            // and double-clicks never reach this fallback. A compact press may
            // finish after hover has expanded the panel; it must still pin it.
            state.togglePinned()
        }
        .help(state.isExpanded
              ? "拖动顶部移动，双击恢复主屏顶部位置；单击\(state.isPinned ? "取消固定" : "固定面板")"
              : "点击固定额度面板")
        .accessibilityAddTraits(.isButton)
        .accessibilityAction { state.togglePinned() }
    }

    private func compactValue(_ window: QuotaWindow?, symbol: String?, label: String, showPeriod: Bool = true) -> some View {
        HStack(spacing: s(5)) {
            if let symbol {
                Image(systemName: symbol)
                    .font(.system(size: s(11), weight: .semibold))
                    .foregroundStyle(mint)
            } else if showPeriod {
                Text(window?.periodTitle ?? "")
                    .font(.system(size: s(9), weight: .medium))
                    .foregroundStyle(Color.white.opacity(0.55))
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
            }
            Text(percentText(window))
                .font(.system(size: s(12), weight: .semibold, design: .rounded))
                .monospacedDigit()
                .foregroundStyle(quotaColor(window))
                .fixedSize()
            if !state.isExpanded && (store.isStale || store.errorMessage != nil) {
                Circle()
                    .fill(Color.orange)
                    .frame(width: s(4), height: s(4))
                    .accessibilityLabel("额度数据待更新")
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(label)剩余 \(percentText(window))\((store.isStale || store.errorMessage != nil) ? "，数据待更新" : "")")
    }

    private var expandedContent: some View {
        VStack(alignment: .leading, spacing: s(12)) {
            HStack(spacing: s(10)) {
                ZStack {
                    RoundedRectangle(cornerRadius: s(9))
                        .fill(mint.opacity(0.11))
                    Image(systemName: "sparkle")
                        .font(.system(size: s(17), weight: .medium))
                        .foregroundStyle(mint)
                }
                .frame(width: s(33), height: s(33))

                VStack(alignment: .leading, spacing: s(2)) {
                    bucketSelector
                    Text("剩余额度")
                        .font(.system(size: s(10)))
                        .foregroundStyle(Color.white.opacity(0.40))
                }
                .frame(width: titleWidth, alignment: .leading)

                Text(store.isDemo ? "演示数据" : (store.snapshot?.planName ?? "账户额度"))
                    .font(.system(size: s(10), weight: .medium))
                    .foregroundStyle(store.isDemo ? Color.orange : Color.white.opacity(0.65))
                    .lineLimit(1)
                    .padding(.horizontal, s(9))
                    .padding(.vertical, s(5))
                    .frame(width: s(86))
                    .background(Capsule().fill(Color.white.opacity(0.065)))
            }
            .frame(width: contentWidth, alignment: .leading)

            HStack(spacing: s(10)) {
                quotaCard(primary, fallbackTitle: "当前周期")
                if let secondary {
                    quotaCard(secondary, fallbackTitle: "当前周期")
                }
            }
            .frame(width: contentWidth, alignment: .leading)

            Rectangle()
                .fill(Color.white.opacity(0.075))
                .frame(height: s(1))

            HStack(alignment: .center, spacing: s(8)) {
                statusText
                    .frame(width: max(0, contentWidth - s(102)), alignment: .leading)

                HStack(spacing: s(5)) {
                    Button { store.refresh() } label: {
                        if store.isRefreshing {
                            ProgressView()
                                .controlSize(state.uiScale >= 1.75 ? .regular : (state.uiScale >= 1.25 ? .small : .mini))
                                .frame(width: s(28), height: s(28))
                        } else {
                            Image(systemName: "arrow.clockwise")
                                .frame(width: s(28), height: s(28))
                        }
                    }
                    .disabled(store.isRefreshing)
                    .help("立即刷新")
                    .accessibilityLabel("立即刷新额度")

                    Button { state.togglePinned() } label: {
                        Image(systemName: state.isPinned ? "pin.fill" : "pin")
                            .foregroundStyle(state.isPinned ? mint : Color.white.opacity(0.55))
                            .frame(width: s(28), height: s(28))
                            .background {
                                if state.isPinned {
                                    RoundedRectangle(cornerRadius: s(7)).fill(mint.opacity(0.09))
                                }
                            }
                    }
                    .help(state.isPinned ? "取消固定" : "保持展开")
                    .accessibilityLabel(state.isPinned ? "取消固定" : "保持展开")

                    Button {
                        state.collapse()
                    } label: {
                        Image(systemName: "chevron.up")
                            .frame(width: s(28), height: s(28))
                    }
                    .help("收起")
                    .accessibilityLabel("收起额度面板")
                }
                .buttonStyle(.plain)
                .font(.system(size: s(12), weight: .medium))
                .foregroundStyle(Color.white.opacity(0.55))
                .frame(width: s(94))
            }
            .frame(width: contentWidth, height: s(52), alignment: .topLeading)
        }
        .frame(width: contentWidth, alignment: .leading)
        .padding(.horizontal, s(18))
        .padding(.top, s(16))
        .padding(.bottom, s(18))
        .frame(width: state.expandedWidth, alignment: .leading)
    }

    @ViewBuilder
    private var bucketSelector: some View {
        if let buckets = store.snapshot?.buckets, buckets.count > 1 {
            Button {
                bucketMenu.present(buckets: buckets, selectedID: selectedBucket?.id, scale: state.uiScale) { id in
                    selectedBucketID = id
                }
            } label: {
                HStack(spacing: s(6)) {
                    Text(selectedBucket?.name ?? "Codex")
                        .font(.system(size: s(20), weight: .semibold))
                        .lineLimit(1)
                        .truncationMode(.tail)
                        .frame(minWidth: 0, maxWidth: .infinity, alignment: .leading)
                    Image(systemName: "chevron.down")
                        .font(.system(size: s(9), weight: .semibold))
                        .foregroundStyle(Color.white.opacity(0.4))
                }
                .font(.system(size: s(20), weight: .semibold))
                .foregroundStyle(Color.white.opacity(0.94))
                .frame(width: titleWidth, alignment: .leading)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .frame(width: titleWidth, alignment: .leading)
            .background { IslandBucketMenuAnchor(menu: bucketMenu) }
            .clipped()
            .help("\(selectedBucket?.name ?? "Codex") · 点击切换额度种类")
            .accessibilityLabel("额度种类：\(selectedBucket?.name ?? "Codex")")
        } else {
            Text(selectedBucket?.name ?? "Codex")
                .font(.system(size: s(20), weight: .semibold))
                .foregroundStyle(Color.white.opacity(0.94))
                .lineLimit(1)
                .truncationMode(.tail)
                .frame(width: titleWidth, alignment: .leading)
        }
    }

    private func quotaCard(_ window: QuotaWindow?, fallbackTitle: String) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(window?.periodTitle ?? fallbackTitle)
                .font(.system(size: s(11), weight: .medium))
                .foregroundStyle(Color.white.opacity(0.58))
                .lineLimit(1)

            HStack(alignment: .firstTextBaseline, spacing: s(5)) {
                Text(percentText(window))
                    .font(.system(size: s(29), weight: .semibold, design: .rounded))
                    .monospacedDigit()
                    .foregroundStyle(quotaColor(window))
                Text("剩余")
                    .font(.system(size: s(10)))
                    .foregroundStyle(Color.white.opacity(0.40))
            }
            .padding(.top, s(10))
            .padding(.bottom, s(13))

            GeometryReader { geometry in
                ZStack(alignment: .leading) {
                    Capsule().fill(Color.white.opacity(0.07))
                    Capsule()
                        .fill(quotaColor(window))
                        .frame(width: geometry.size.width * remainingFraction(window))
                }
            }
            .frame(height: s(4))
            .accessibilityHidden(true)

            Text(window.map { $0.resetText(now: Date()) } ?? "等待账户数据")
                .font(.system(size: s(10)))
                .foregroundStyle(Color.white.opacity(0.42))
                .lineLimit(2)
                .frame(height: s(27), alignment: .topLeading)
                .padding(.top, s(10))
        }
        .frame(width: max(0, cardWidth - s(28)), height: s(112), alignment: .topLeading)
        .padding(s(14))
        .background {
            RoundedRectangle(cornerRadius: s(15), style: .continuous)
                .fill(Color.white.opacity(0.037))
                .overlay {
                    RoundedRectangle(cornerRadius: s(15), style: .continuous)
                        .stroke(Color.white.opacity(0.045), lineWidth: s(1))
                }
        }
        .accessibilityElement(children: .combine)
    }

    private var statusText: some View {
        VStack(alignment: .leading, spacing: s(3)) {
            HStack(spacing: s(5)) {
                Circle()
                    .fill(statusColor)
                    .frame(width: s(4), height: s(4))
                Text(statusTitle)
                    .font(.system(size: s(10), weight: .medium))
                    .foregroundStyle(Color.white.opacity(0.50))
            }

            if let error = store.errorMessage {
                Text(error)
                    .font(.system(size: s(9)))
                    .foregroundStyle(Color.orange.opacity(0.80))
                    .lineLimit(2)
                    .help(error)
            }

            if let fetched = store.snapshot?.fetchedAt {
                Text("上次更新 \(fetched.formatted(date: .omitted, time: .shortened))")
                    .font(.system(size: s(9)))
                    .foregroundStyle(Color.white.opacity(0.31))
            } else if store.errorMessage == nil {
                Text("请先在 Codex 中登录账户")
                    .font(.system(size: s(9)))
                    .foregroundStyle(Color.white.opacity(0.31))
            }
        }
    }

    private var statusTitle: String {
        if store.isDemo { return "演示模式" }
        if store.isRefreshing { return "正在更新额度…" }
        if store.errorMessage != nil { return store.snapshot == nil ? "暂时无法读取" : "刷新未成功，显示上次数据" }
        if store.isStale { return "数据待更新" }
        return store.snapshot == nil ? "等待额度数据" : "已同步账户额度"
    }

    private var statusColor: Color {
        if store.errorMessage != nil || store.isStale { return .orange }
        return store.snapshot == nil ? Color.white.opacity(0.35) : mint
    }

    private func remainingFraction(_ window: QuotaWindow?) -> Double {
        guard let value = window?.remainingPercent, value.isFinite else { return 0 }
        return min(1, max(0, value / 100))
    }

    private func percentText(_ window: QuotaWindow?) -> String {
        guard let value = window?.remainingPercent, value.isFinite else { return "—" }
        let bounded = min(100, max(0, value))
        if bounded > 0 && bounded < 1 { return "<1%" }
        return "\(Int(bounded.rounded(.down)))%"
    }

    private func quotaColor(_ window: QuotaWindow?) -> Color {
        guard let value = window?.remainingPercent, value.isFinite else { return Color.white.opacity(0.35) }
        if value <= 10 { return Color(red: 1.0, green: 0.43, blue: 0.43) }
        if value <= 25 { return Color(red: 0.98, green: 0.72, blue: 0.34) }
        return mint
    }

}

private struct IslandOutline: Shape {
    var expansion: CGFloat
    var topClearance: CGFloat
    var uiScale: CGFloat
    var omitsTopEdge = false

    func path(in rect: CGRect) -> Path {
        let radius = (17 + 8 * expansion) * uiScale
        // Flush with the display: square top corners. As the island moves away
        // from the edge, restore the floating shape without a sudden corner jump.
        let top: CGFloat = min(radius, topClearance, rect.height / 2, rect.width / 2)
        let bottom: CGFloat = min(radius, rect.height / 2, rect.width / 2)
        var path = Path()
        path.move(to: CGPoint(x: rect.maxX - top, y: rect.minY))
        path.addQuadCurve(to: CGPoint(x: rect.maxX, y: rect.minY + top), control: CGPoint(x: rect.maxX, y: rect.minY))
        path.addLine(to: CGPoint(x: rect.maxX, y: rect.maxY - bottom))
        path.addQuadCurve(to: CGPoint(x: rect.maxX - bottom, y: rect.maxY), control: CGPoint(x: rect.maxX, y: rect.maxY))
        path.addLine(to: CGPoint(x: rect.minX + bottom, y: rect.maxY))
        path.addQuadCurve(to: CGPoint(x: rect.minX, y: rect.maxY - bottom), control: CGPoint(x: rect.minX, y: rect.maxY))
        path.addLine(to: CGPoint(x: rect.minX, y: rect.minY + top))
        path.addQuadCurve(to: CGPoint(x: rect.minX + top, y: rect.minY), control: CGPoint(x: rect.minX, y: rect.minY))
        // The fill and clip always close. The attached outline omits the top
        // stroke so there is no bright seam against the display's upper edge.
        if !omitsTopEdge { path.closeSubpath() }
        return path
    }
}
