import AppKit
import Darwin
import Foundation

@MainActor
enum TaskVSCodeNavigator {
    /// Returns nil when VS Code accepts the exact-window URI, otherwise a reason
    /// the caller can display while falling back to Codex Desktop.
    static func open(threadID: String) async -> String? {
        guard UUID(uuidString: threadID) != nil else { return "会话编号无效" }
        let applications = NSWorkspace.shared.runningApplications.filter {
            $0.bundleIdentifier == "com.microsoft.VSCode" && !$0.isTerminated
        }
        guard applications.count == 1, let application = applications.first,
              let bundleURL = application.bundleURL, let launchDate = application.launchDate else {
            return "无法确定正在运行的 VS Code 实例"
        }
        let instance = Instance(pid: application.processIdentifier,
                                bundleURL: bundleURL.resolvingSymlinksInPath(), launchDate: launchDate)
        let first = await Task.detached(priority: .utility) {
            locate(threadID: threadID, instance: instance)
        }.value
        guard case .found(let destination) = first else { return first.failureReason }
        guard !Task.isCancelled, !application.isTerminated else { return "VS Code 窗口已关闭" }

        // Recheck immediately before dispatch. VS Code silently falls back to
        // another window when windowId is stale, so a historical log alone is
        // never enough. This verifies the current run and a window-scoped live
        // renderer/extension-host process, not the app's shared logging process.
        let second = await Task.detached(priority: .utility) {
            locate(threadID: threadID, instance: instance)
        }.value
        guard case .found(let verified) = second, destination == verified,
              !Task.isCancelled, !application.isTerminated,
              application.processIdentifier == instance.pid,
              application.launchDate == instance.launchDate else {
            return "VS Code 会话窗口已变化，无法准确定位"
        }

        var components = URLComponents()
        components.scheme = "vscode"
        components.host = "openai.chatgpt"
        components.path = "/local/\(threadID)"
        components.queryItems = [URLQueryItem(name: "windowId", value: String(destination.windowID))]
        guard let url = components.url else { return "无法生成 VS Code 会话链接" }
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        configuration.createsNewApplicationInstance = false
        return await withCheckedContinuation { continuation in
            NSWorkspace.shared.open([url], withApplicationAt: instance.bundleURL,
                                    configuration: configuration) { openedApplication, error in
                continuation.resume(returning: error == nil && openedApplication != nil
                                    ? nil : "VS Code 未接受会话链接")
            }
        }
    }

    private struct Instance: Sendable {
        let pid: pid_t
        let bundleURL: URL
        let launchDate: Date
    }

    private struct ProcessIdentity: Equatable, Sendable {
        let pid: pid_t
        let parent: pid_t
        let startedSeconds: UInt64
        let startedMicroseconds: UInt64
    }

    private struct FileIdentity: Equatable, Sendable {
        let device: UInt64
        let inode: UInt64
    }

    private struct Witness: Equatable, Sendable {
        let file: URL
        let identity: FileIdentity
        let process: ProcessIdentity
    }

    private struct Destination: Equatable, Sendable {
        let windowID: Int
        let runDirectory: URL
        let rootProcess: ProcessIdentity
        let logIdentity: FileIdentity
        let witness: Witness
    }

    private enum Lookup: Sendable {
        case found(Destination)
        case unavailable(String)

        var failureReason: String? {
            if case .unavailable(let reason) = self { return reason }
            return nil
        }
    }

    nonisolated private static func locate(threadID: String, instance: Instance) -> Lookup {
        guard let rootProcess = processIdentity(instance.pid),
              abs(Double(rootProcess.startedSeconds) - instance.launchDate.timeIntervalSince1970) < 3,
              executablePath(instance.pid)?.hasPrefix(instance.bundleURL.path + "/Contents/MacOS/") == true else {
            return .unavailable("无法验证当前 VS Code 进程")
        }
        let support = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Code/logs", isDirectory: true)
        guard let directories = try? FileManager.default.contentsOfDirectory(
            at: support, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles]) else {
            return .unavailable("无法读取 VS Code 窗口信息")
        }
        // Open-file evidence selects the run. A date/name/mtime match alone does
        // not prove that this run belongs to the currently running application.
        let recentRuns = directories.filter { isRunDirectory($0) }
            .sorted { $0.lastPathComponent > $1.lastPathComponent }.prefix(12)
        let runs = recentRuns.filter { run in
            witness(for: run.appendingPathComponent("main.log"), instance: instance,
                    rootProcess: rootProcess, windowScoped: false) != nil
        }
        guard runs.count == 1, let run = runs.first else {
            return .unavailable("无法确认 VS Code 当前运行记录")
        }
        guard let windows = try? FileManager.default.contentsOfDirectory(
            at: run, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles]) else {
            return .unavailable("无法读取 VS Code 窗口信息")
        }
        let windowDirectories = windows.compactMap { directory -> (Int, URL)? in
            let name = directory.lastPathComponent
            guard name.hasPrefix("window"), let number = Int(name.dropFirst(6)), number > 0,
                  number <= Int(Int32.max), directoryFlag(directory) else { return nil }
            return (number, directory)
        }
        guard windowDirectories.count <= 64 else {
            return .unavailable("VS Code 窗口记录过多，无法准确定位")
        }
        var matches: [Destination] = []
        for (number, directory) in windowDirectories {
            let codexLog = directory.appendingPathComponent("exthost/openai.chatgpt/Codex.log")
            guard let identity = fileIdentity(codexLog),
                  lastRecordedRole(threadID: threadID, log: codexLog) == "owner",
                  fileIdentity(codexLog) == identity else { continue }
            let possibleLogs = [codexLog,
                                directory.appendingPathComponent("exthost/exthost.log"),
                                directory.appendingPathComponent("renderer.log")]
            let liveWitness = possibleLogs.compactMap {
                witness(for: $0, instance: instance, rootProcess: rootProcess, windowScoped: true)
            }.first
            guard let liveWitness else { continue }
            matches.append(Destination(windowID: number, runDirectory: run, rootProcess: rootProcess,
                                       logIdentity: identity, witness: liveWitness))
        }
        guard matches.count == 1, let match = matches.first else {
            return .unavailable(matches.isEmpty ? "未找到可验证的 VS Code 原会话窗口"
                                : "多个 VS Code 窗口包含此会话，无法准确定位")
        }
        return .found(match)
    }

    nonisolated private static func isRunDirectory(_ url: URL) -> Bool {
        let name = url.lastPathComponent
        guard name.count == 15, name[name.index(name.startIndex, offsetBy: 8)] == "T",
              name.enumerated().allSatisfy({ $0.offset == 8 || $0.element.isASCII && $0.element.isNumber }) else {
            return false
        }
        return directoryFlag(url)
    }

    nonisolated private static func directoryFlag(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true
    }

    /// Inspect only a bounded log tail and the logger's own structured role
    /// events. No prompt, output, or log body is retained or returned to the UI.
    nonisolated private static func lastRecordedRole(threadID: String, log: URL) -> String? {
        guard let file = try? FileHandle(forReadingFrom: log) else { return nil }
        defer { try? file.close() }
        let limit: UInt64 = 2 * 1024 * 1024
        guard let size = try? file.seekToEnd() else { return nil }
        let offset = size > limit ? size - limit : 0
        do { try file.seek(toOffset: offset) } catch { return nil }
        guard let data = try? file.read(upToCount: Int(limit)) else { return nil }
        var lines = String(decoding: data, as: UTF8.self).split(separator: "\n", omittingEmptySubsequences: false)
        if offset > 0, !lines.isEmpty { lines.removeFirst() }
        // A partially written final event cannot establish current ownership.
        if data.last != 10, !lines.isEmpty { lines.removeLast() }
        guard let event = try? NSRegularExpression(
            pattern: #"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[info\] thread_stream_role_changed (.*)$"#) else { return nil }
        for line in lines.reversed() where line.count <= 4_096 {
            let text = String(line)
            guard let match = event.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)),
                  let range = Range(match.range(at: 1), in: text) else { continue }
            var fields: [String: String] = [:]
            for field in text[range].split(separator: " ") {
                let pair = field.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
                if pair.count == 2, pair[0] == "conversationId" || pair[0] == "role" {
                    fields[String(pair[0])] = String(pair[1])
                }
            }
            if fields["conversationId"]?.lowercased() == threadID.lowercased() { return fields["role"] }
        }
        return nil
    }

    nonisolated private static func fileIdentity(_ url: URL) -> FileIdentity? {
        var value = stat()
        guard lstat(url.path, &value) == 0, value.st_uid == getuid(),
              value.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG) else { return nil }
        return FileIdentity(device: UInt64(truncatingIfNeeded: value.st_dev), inode: UInt64(value.st_ino))
    }

    nonisolated private static func witness(for file: URL, instance: Instance,
                                           rootProcess: ProcessIdentity, windowScoped: Bool) -> Witness? {
        guard let identity = fileIdentity(file), let processes = processesHolding(file) else { return nil }
        for pid in processes.sorted() {
            guard let process = processIdentity(pid), let executable = executablePath(pid),
                  executable.hasPrefix(instance.bundleURL.path + "/Contents/"),
                  belongsTo(process, root: rootProcess) else { continue }
            if windowScoped {
                // A shared/main-process logger may keep closed-window files open.
                // Only a live window renderer or extension host proves enough to
                // attempt windowId routing; uncertain ownership falls back.
                let prefix = instance.bundleURL.path + "/Contents/Frameworks/"
                guard executable.hasPrefix(prefix + "Code Helper (Renderer).app/")
                    || executable.hasPrefix(prefix + "Code Helper (Plugin).app/") else { continue }
            }
            guard fileIdentity(file) == identity else { return nil }
            return Witness(file: file, identity: identity, process: process)
        }
        return nil
    }

    nonisolated private static func processesHolding(_ file: URL) -> [pid_t]? {
        let flags = UInt32(PROC_LISTPIDSPATH_EXCLUDE_EVTONLY)
        let requested = file.path.withCString {
            proc_listpidspath(UInt32(PROC_UID_ONLY), UInt32(getuid()), $0, flags, nil, 0)
        }
        guard requested >= 0, requested < 1_048_576 else { return nil }
        let bytes = max(4_096, Int(requested) + 1_024)
        var pids = [pid_t](repeating: 0, count: bytes / MemoryLayout<pid_t>.size)
        let written = pids.withUnsafeMutableBytes { buffer in
            file.path.withCString {
                proc_listpidspath(UInt32(PROC_UID_ONLY), UInt32(getuid()), $0, flags,
                                  buffer.baseAddress, Int32(buffer.count))
            }
        }
        guard written >= 0, Int(written) < bytes,
              Int(written) % MemoryLayout<pid_t>.size == 0 else { return nil }
        return Array(pids.prefix(Int(written) / MemoryLayout<pid_t>.size)).filter { $0 > 0 }
    }

    nonisolated private static func processIdentity(_ pid: pid_t) -> ProcessIdentity? {
        var info = proc_bsdinfo()
        let size = MemoryLayout<proc_bsdinfo>.size
        guard proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, Int32(size)) == Int32(size),
              info.pbi_uid == getuid(), info.pbi_pid == UInt32(pid) else { return nil }
        return ProcessIdentity(pid: pid, parent: pid_t(info.pbi_ppid),
                               startedSeconds: info.pbi_start_tvsec, startedMicroseconds: info.pbi_start_tvusec)
    }

    nonisolated private static func executablePath(_ pid: pid_t) -> String? {
        // libproc.h defines PROC_PIDPATHINFO_MAXSIZE as (4 * MAXPATHLEN),
        // which Swift does not import. The macOS SDK value is 4 * 1024 bytes.
        var path = [CChar](repeating: 0, count: 4_096)
        let count = path.withUnsafeMutableBytes { proc_pidpath(pid, $0.baseAddress, UInt32($0.count)) }
        guard count > 0, Int(count) < path.count else { return nil }
        return String(cString: path)
    }

    nonisolated private static func belongsTo(_ process: ProcessIdentity, root: ProcessIdentity) -> Bool {
        var current = process
        var visited = Set<pid_t>()
        for _ in 0..<32 {
            guard visited.insert(current.pid).inserted else { return false }
            if current.pid == root.pid { return current == root }
            guard current.parent > 1, let parent = processIdentity(current.parent) else { return false }
            current = parent
        }
        return false
    }
}
