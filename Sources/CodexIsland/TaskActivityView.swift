import SwiftUI

/// Shows reported task activity and opens its existing source conversation.
@MainActor
struct TaskActivityView: View {
    @ObservedObject var activity: TaskActivityStore
    var uiScale: CGFloat
    @State private var hoveredTaskID: String?

    private let mint = Color(red: 0.46, green: 0.88, blue: 0.70)
    private func s(_ base: CGFloat) -> CGFloat { base * uiScale }

    var body: some View {
        VStack(alignment: .leading, spacing: s(6)) {
            HStack(spacing: s(6)) {
                Text("进行中的任务")
                    .font(.system(size: s(12), weight: .semibold))
                    .foregroundStyle(Color.white.opacity(0.82))

                Text("\(activity.activeCount)")
                    .font(.system(size: s(10), weight: .medium, design: .rounded))
                    .monospacedDigit()
                    .foregroundStyle(Color.white.opacity(0.56))
                    .padding(.horizontal, s(5))
                    .padding(.vertical, s(1))
                    .background(Capsule().fill(Color.white.opacity(0.065)))

                Spacer(minLength: 0)
            }
            .frame(height: s(18))

            Text(connectionText)
                .font(.system(size: s(10)))
                .foregroundStyle(connectionColor)
                .lineLimit(1)
                .truncationMode(.tail)
                .help(connectionText)
                .frame(height: s(14), alignment: .leading)

            Group {
                if activity.tasks.isEmpty {
                    emptyState
                } else {
                    ScrollView(.vertical, showsIndicators: true) {
                        LazyVStack(spacing: s(5)) {
                            ForEach(activity.tasks) { task in
                                taskRow(task)
                            }
                        }
                    }
                }
            }
            .frame(height: s(97))
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .contain)
    }

    private var connectionText: String {
        if let message = activity.navigationMessage { return message }
        if activity.openingTaskID != nil { return "正在打开任务…" }
        return activity.connectionText
    }

    private var connectionColor: Color {
        if activity.openingTaskID != nil { return mint.opacity(0.80) }
        if activity.navigationMessage != nil { return Color.orange.opacity(0.80) }
        return activity.isConnected ? Color.white.opacity(0.40) : Color.orange.opacity(0.80)
    }

    private var emptyState: some View {
        VStack(spacing: s(6)) {
            Image(systemName: activity.isConnected ? "tray" : "network")
                .font(.system(size: s(16), weight: .regular))
                .foregroundStyle(Color.white.opacity(0.28))
            Text(activity.isConnected ? "已加载会话中暂无进行中的任务"
                 : (activity.isRefreshing ? "正在连接任务来源…" : "暂时无法读取任务"))
                .font(.system(size: s(11)))
                .foregroundStyle(Color.white.opacity(0.48))
                .lineLimit(1)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(RoundedRectangle(cornerRadius: s(8)).fill(Color.white.opacity(0.025)))
    }

    private func taskRow(_ task: TaskActivity) -> some View {
        let title = task.title.isEmpty ? "未命名任务" : task.title
        let device = task.deviceName.isEmpty ? "未知设备" : task.deviceName
        let detail = task.detail.isEmpty ? "暂无新的进展" : task.detail
        let opening = activity.openingTaskID == task.id
        let hovered = hoveredTaskID == task.id

        return Button {
            activity.openTask(task)
        } label: {
            VStack(alignment: .leading, spacing: s(4)) {
                HStack(spacing: s(6)) {
                    Circle()
                        .fill(statusColor(task.status))
                        .frame(width: s(5), height: s(5))
                        .accessibilityHidden(true)
                    Text(title)
                        .font(.system(size: s(12), weight: .medium))
                        .foregroundStyle(Color.white.opacity(0.86))
                        .lineLimit(1)
                        .truncationMode(.tail)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    Text(opening ? "正在打开…" : task.status.title)
                        .font(.system(size: s(10), weight: .medium))
                        .foregroundStyle(statusColor(task.status))
                        .fixedSize()
                    Image(systemName: opening ? "hourglass" : "arrow.up.forward")
                        .font(.system(size: s(10), weight: .medium))
                        .foregroundStyle(hovered || opening ? mint : Color.white.opacity(0.35))
                        .frame(width: s(12))
                        .accessibilityHidden(true)
                }
                .frame(height: s(18))

                HStack(spacing: s(5)) {
                    Image(systemName: "desktopcomputer")
                        .font(.system(size: s(9)))
                        .foregroundStyle(Color.white.opacity(0.35))
                        .accessibilityHidden(true)
                    Text(device)
                        .font(.system(size: s(10), weight: .medium))
                        .foregroundStyle(Color.white.opacity(0.54))
                        .lineLimit(1)
                        .truncationMode(.middle)
                        .frame(maxWidth: s(96), alignment: .leading)
                    Text("·")
                        .font(.system(size: s(10)))
                        .foregroundStyle(Color.white.opacity(0.28))
                        .accessibilityHidden(true)
                    Text(detail)
                        .font(.system(size: s(10)))
                        .foregroundStyle(Color.white.opacity(0.43))
                        .lineLimit(1)
                        .truncationMode(.tail)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(height: s(16))
            }
            .padding(.horizontal, s(8))
            .padding(.vertical, s(4))
            .frame(maxWidth: .infinity)
            .frame(height: s(46))
            .background(RoundedRectangle(cornerRadius: s(8)).fill(Color.white.opacity(hovered ? 0.075 : 0.035)))
            .contentShape(RoundedRectangle(cornerRadius: s(8)))
        }
        .buttonStyle(.plain)
        .disabled(activity.openingTaskID != nil)
        .onHover { hovering in
            if hovering { hoveredTaskID = task.id }
            else if hoveredTaskID == task.id { hoveredTaskID = nil }
        }
        .help("\(title)\n\(device) · \(task.status.title)\n\(detail)\n\(task.openActionTitle)")
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(title)，\(device)，\(task.status.title)，\(detail)")
        .accessibilityHint(opening ? "正在打开任务" : task.openActionTitle)
    }

    private func statusColor(_ status: TaskActivityStatus) -> Color {
        switch status {
        case .running: return mint
        case .waiting: return .orange
        case .unknown: return Color.white.opacity(0.44)
        }
    }
}
