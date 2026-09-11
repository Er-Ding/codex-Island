import AppKit
import Darwin
import Foundation

/// Only the first, bounded session_meta record is read. Conversation text is
/// never used to guess which terminal owns a task.
struct TaskLocalSession: Sendable {
    let rolloutURL: URL
    let source: String?
    let originator: String?
    let cwd: String?

    /// Call on a background queue: older, non-UUIDv7 sessions may require a
    /// bounded directory search after the date-based lookup.
    static func find(threadID: String, rolloutPath: String?) -> TaskLocalSession? {
        guard let uuid = UUID(uuidString: threadID) else { return nil }
        let identifier = uuid.uuidString.lowercased()
        if let path = rolloutPath, path.hasPrefix("/"),
           let session = read(URL(fileURLWithPath: path), identifier: identifier) {
            return session
        }

        let configured = ProcessInfo.processInfo.environment["CODEX_HOME"]?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let home = configured.flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: $0) }
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex")
        let sessions = home.appendingPathComponent("sessions", isDirectory: true)
        let hex = identifier.replacingOccurrences(of: "-", with: "")
        if hex.dropFirst(12).first == "7", let milliseconds = UInt64(hex.prefix(12), radix: 16) {
            var calendar = Calendar(identifier: .gregorian)
            calendar.timeZone = TimeZone(secondsFromGMT: 0)!
            let timestamp = Date(timeIntervalSince1970: Double(milliseconds) / 1_000)
            // Session folders use local dates, which can be a day from UTC.
            for offset in [0, -1, 1] {
                guard let day = calendar.date(byAdding: .day, value: offset, to: timestamp) else { continue }
                let parts = calendar.dateComponents([.year, .month, .day], from: day)
                guard let year = parts.year, let month = parts.month, let date = parts.day else { continue }
                let directory = sessions.appendingPathComponent(String(format: "%04d/%02d/%02d", year, month, date))
                let children = (try? FileManager.default.contentsOfDirectory(at: directory,
                    includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])) ?? []
                for url in children.prefix(4_096) where matches(url, identifier: identifier) {
                    if let session = read(url, identifier: identifier) { return session }
                }
            }
        }

        guard let iterator = FileManager.default.enumerator(at: sessions,
            includingPropertiesForKeys: nil, options: [.skipsHiddenFiles, .skipsPackageDescendants]) else { return nil }
        var visited = 0
        while visited < 8_192, let url = iterator.nextObject() as? URL {
            visited += 1
            if matches(url, identifier: identifier), let session = read(url, identifier: identifier) { return session }
        }
        return nil
    }

    private static func matches(_ url: URL, identifier: String) -> Bool {
        url.lastPathComponent.lowercased().hasSuffix("-\(identifier).jsonl")
    }

    fileprivate static func read(_ url: URL, identifier: String) -> TaskLocalSession? {
        guard url.isFileURL, url.pathExtension == "jsonl" else { return nil }
        let resolved = url.resolvingSymlinksInPath()
        guard let file = fopen(resolved.path, "r") else { return nil }
        defer { fclose(file) }
        var info = stat()
        guard fstat(fileno(file), &info) == 0, info.st_uid == getuid(),
              info.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG) else { return nil }
        // fgets stops at the first newline; no later record is parsed or kept.
        var bytes = [CChar](repeating: 0, count: 256 * 1_024 + 1)
        let limit = Int32(bytes.count)
        guard fgets(&bytes, limit, file) != nil else { return nil }
        let line = String(cString: bytes)
        guard line.hasSuffix("\n"), let data = line.data(using: .utf8),
              let record = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              record["type"] as? String == "session_meta",
              let metadata = record["payload"] as? [String: Any],
              let id = metadata["id"] as? String,
              UUID(uuidString: id)?.uuidString.lowercased() == identifier else { return nil }
        return TaskLocalSession(rolloutURL: resolved, source: metadata["source"] as? String,
                                originator: metadata["originator"] as? String, cwd: metadata["cwd"] as? String)
    }
}

@MainActor
enum TaskTerminalNavigator {
    /// Returns nil only after selecting an existing terminal session. It never
    /// creates a terminal, connects to SSH, or sends text to a shell.
    static func open(session: TaskLocalSession, threadID: String) async -> String? {
        let applications = NSWorkspace.shared.runningApplications.filter {
            $0.bundleIdentifier == "com.apple.Terminal" || $0.bundleIdentifier == "com.googlecode.iterm2"
        }
        guard !applications.isEmpty else { return "未找到正在运行的 Terminal 或 iTerm2，无法定位已有终端会话。" }
        let resolution = await Task.detached(priority: .userInitiated) {
            TerminalProcessLookup.resolve(session: session, threadID: threadID)
        }.value
        let target: TerminalProcessLookup.Target
        switch resolution {
        case .success(let value): target = value
        case .failure(let error): return error.message
        }
        guard !Task.isCancelled else { return "已取消定位终端。" }

        var matches: [NSRunningApplication] = []
        var failure: String?
        for application in applications where !application.isTerminated {
            guard let bundle = application.bundleIdentifier else { continue }
            let result = await TerminalSelectionScript.run(bundle: bundle, tty: target.tty, select: false)
            if result == "MATCH" { matches.append(application) }
            else if let reason = scriptFailure(result) { failure = reason }
        }
        guard matches.count == 1, let application = matches.first else {
            if matches.count > 1 { return "多个终端窗口报告同一个会话，无法确定要定位的窗口。" }
            return failure ?? "该任务的终端页签已关闭，或使用了暂不支持定位的终端／tmux。"
        }

        // Automation consent may have stayed open while the task exited. Check
        // the PID's birth time and exact open file again after that round trip.
        let isCurrent = await Task.detached(priority: .userInitiated) {
            TerminalProcessLookup.isCurrent(target)
        }.value
        guard !Task.isCancelled else { return "已取消定位终端。" }
        guard isCurrent else { return "该终端会话已结束或发生变化，请刷新任务列表后重试。" }
        guard !application.isTerminated, let bundle = application.bundleIdentifier else {
            return "目标终端已退出。"
        }
        let result = await TerminalSelectionScript.run(bundle: bundle, tty: target.tty, select: true)
        guard result == "OK" else { return scriptFailure(result) ?? "目标终端页签已关闭或发生变化。" }
        guard !application.isTerminated else { return "目标终端已退出。" }
        application.activate(options: [.activateIgnoringOtherApps])
        return nil
    }

    private static func scriptFailure(_ status: String) -> String? {
        switch status {
        case "DENIED": return "系统未允许控制终端；请在“系统设置 → 隐私与安全性 → 自动化”中允许 Codex Island 控制对应终端。"
        case "TIMEOUT": return "终端暂未响应定位请求，请稍后重试。"
        case "AMBIGUOUS": return "多个终端页签报告同一个会话，无法确定要定位的页签。"
        case "ERROR": return "无法读取或选择已有终端页签，请稍后重试。"
        default: return nil
        }
    }
}

private enum TerminalProcessLookup {
    struct LookupFailure: Error, Sendable { let message: String }
    struct Target: Sendable {
        let pid: pid_t
        let startedSeconds: UInt64
        let startedMicroseconds: UInt64
        let terminalDevice: UInt32
        let tty: String
        let referenceURL: URL
        let referenceDevice: dev_t
        let referenceInode: ino_t
    }

    static func resolve(session: TaskLocalSession, threadID: String) -> Result<Target, LookupFailure> {
        guard let uuid = UUID(uuidString: threadID),
              let metadata = TaskLocalSession.read(session.rolloutURL, identifier: uuid.uuidString.lowercased()),
              metadata.source == "cli", metadata.originator == "codex-tui" else {
            return .failure(LookupFailure(message: "没有可验证的本机 Codex 终端会话信息，无法定位已有窗口。"))
        }
        var references = [metadata.rolloutURL]
        var directory = metadata.rolloutURL.deletingLastPathComponent()
        while directory.path != "/" {
            if directory.lastPathComponent == "sessions" {
                references.append(directory.deletingLastPathComponent().appendingPathComponent("thread-writer-locks")
                    .appendingPathComponent("\(uuid.uuidString.lowercased()).lock"))
                break
            }
            directory.deleteLastPathComponent()
        }
        var targets: [pid_t: Target] = [:]
        var queried = false
        var unsupportedChain = false
        for url in references {
            var fileInfo = stat()
            guard stat(url.path, &fileInfo) == 0, fileInfo.st_uid == getuid(),
                  fileInfo.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG),
                  let processes = processesReferencing(url) else { continue }
            queried = true
            for pid in processes {
                guard let info = processInfo(pid), isForegroundCodex(pid, info: info) else { continue }
                guard !hasUnsupportedAncestor(pid) else { unsupportedChain = true; continue }
                guard let tty = ttyPath(device: info.e_tdev) else { continue }
                targets[pid] = Target(pid: pid, startedSeconds: info.pbi_start_tvsec,
                    startedMicroseconds: info.pbi_start_tvusec, terminalDevice: info.e_tdev,
                    tty: tty, referenceURL: url, referenceDevice: fileInfo.st_dev, referenceInode: fileInfo.st_ino)
            }
        }
        guard targets.count == 1, let target = targets.values.first else {
            let message: String
            if targets.count > 1 { message = "多个 Codex 终端进程正在访问该会话，无法确定原始页签。" }
            else if unsupportedChain { message = "该会话位于 tmux、screen 或 SSH 中，无法可靠定位对应的本机终端页签。" }
            else if !queried { message = "系统未提供该会话的进程关联信息，无法定位已有终端。" }
            else { message = "未找到持有该会话的前台 Codex 终端进程；会话可能已结束或由后台服务接管。" }
            return .failure(LookupFailure(message: message))
        }
        return .success(target)
    }

    static func isCurrent(_ target: Target) -> Bool {
        var fileInfo = stat()
        var ttyInfo = stat()
        guard let info = processInfo(target.pid),
              info.pbi_start_tvsec == target.startedSeconds,
              info.pbi_start_tvusec == target.startedMicroseconds,
              info.e_tdev == target.terminalDevice,
              isForegroundCodex(target.pid, info: info), !hasUnsupportedAncestor(target.pid),
              stat(target.referenceURL.path, &fileInfo) == 0,
              fileInfo.st_dev == target.referenceDevice, fileInfo.st_ino == target.referenceInode,
              processesReferencing(target.referenceURL)?.contains(target.pid) == true,
              stat(target.tty, &ttyInfo) == 0,
              UInt32(bitPattern: ttyInfo.st_rdev) == target.terminalDevice else { return false }
        return true
    }

    private static func processesReferencing(_ url: URL) -> [pid_t]? {
        let required = proc_listpidspath(UInt32(PROC_UID_ONLY), getuid(), url.path,
                                        UInt32(PROC_LISTPIDSPATH_EXCLUDE_EVTONLY), nil, 0)
        guard required >= 0, required <= 256 * 1_024 else { return nil }
        // Reserve headroom for processes starting between the two calls.
        var buffer = [pid_t](repeating: 0, count: max(128, Int(required) / MemoryLayout<pid_t>.size + 128))
        let capacity = Int32(buffer.count * MemoryLayout<pid_t>.size)
        let count = buffer.withUnsafeMutableBytes {
            proc_listpidspath(UInt32(PROC_UID_ONLY), getuid(), url.path,
                              UInt32(PROC_LISTPIDSPATH_EXCLUDE_EVTONLY), $0.baseAddress, capacity)
        }
        guard count >= 0, count < capacity else { return nil }
        return buffer.prefix(Int(count) / MemoryLayout<pid_t>.size).filter { $0 > 0 }
    }

    private static func processInfo(_ pid: pid_t) -> proc_bsdinfo? {
        var info = proc_bsdinfo()
        let size = Int32(MemoryLayout<proc_bsdinfo>.size)
        guard proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, size) == size else { return nil }
        return info
    }

    private static func executableName(_ pid: pid_t) -> String? {
        // PROC_PIDPATHINFO_MAXSIZE is (4 * MAXPATHLEN) in proc_info.h; Swift
        // does not import that expression macro. Darwin MAXPATHLEN is 1024.
        var path = [CChar](repeating: 0, count: 4_096)
        let size = UInt32(path.count)
        guard proc_pidpath(pid, &path, size) > 0 else { return nil }
        return URL(fileURLWithPath: String(cString: path)).lastPathComponent
    }

    private static func isForegroundCodex(_ pid: pid_t, info: proc_bsdinfo) -> Bool {
        guard info.pbi_uid == getuid(), info.e_tdev != UInt32.max, info.e_tdev != 0,
              info.pbi_pgid != 0, info.e_tpgid == info.pbi_pgid,
              executableName(pid) == "codex" else { return false }
        // The same executable also runs Desktop's server. Never treat that
        // server's inherited TTY as evidence of an interactive CLI session.
        return isInteractiveInvocation(pid)
    }

    private static func isInteractiveInvocation(_ pid: pid_t) -> Bool {
        // KERN_PROCARGS2 also returns environment bytes; stop at argc and never
        // decode or log the environment. Compare short mode tokens in place;
        // prompt arguments are never copied into strings or kept as metadata.
        var mib: [Int32] = [CTL_KERN, KERN_PROCARGS2, pid]
        let mibCount = UInt32(mib.count)
        var count = 0
        guard sysctl(&mib, mibCount, nil, &count, nil, 0) == 0,
              count > MemoryLayout<Int32>.size, count <= 2 * 1_024 * 1_024 else { return false }
        var bytes = [UInt8](repeating: 0, count: count)
        guard sysctl(&mib, mibCount, &bytes, &count, nil, 0) == 0 else { return false }
        var argc: Int32 = 0
        withUnsafeMutableBytes(of: &argc) { destination in
            bytes.withUnsafeBytes { source in destination.copyBytes(from: source.prefix(MemoryLayout<Int32>.size)) }
        }
        guard argc > 0, argc <= 4_096 else { return false }
        var index = MemoryLayout<Int32>.size
        // Skip the executable path and its alignment padding.
        while index < count, bytes[index] != 0 { index += 1 }
        while index < count, bytes[index] == 0 { index += 1 }
        let services = ["app-server", "app-server-proxy", "mcp-server", "mcp", "exec", "e", "review"]
            .map { Array($0.utf8) }
        for argument in 0..<Int(argc) {
            let start = index
            while index < count, bytes[index] != 0 { index += 1 }
            guard index < count else { return false }
            if argument > 0, services.contains(where: { bytes[start..<index].elementsEqual($0) }) { return false }
            index += 1
        }
        return true
    }

    private static func hasUnsupportedAncestor(_ pid: pid_t) -> Bool {
        var current = pid
        var visited = Set<pid_t>()
        for _ in 0..<32 {
            guard current > 1, visited.insert(current).inserted else { return false }
            guard let info = processInfo(current), let name = executableName(current) else { return true }
            if ["tmux", "screen", "ssh", "sshd", "sshd-session"].contains(name) { return true }
            current = pid_t(info.pbi_ppid)
        }
        return true
    }

    private static func ttyPath(device: UInt32) -> String? {
        let names = (try? FileManager.default.contentsOfDirectory(atPath: "/dev")) ?? []
        for name in names.prefix(8_192) {
            let path = "/dev/\(name)"
            guard TerminalSelectionScript.isTTY(path) else { continue }
            var info = stat()
            if stat(path, &info) == 0, info.st_mode & mode_t(S_IFMT) == mode_t(S_IFCHR),
               UInt32(bitPattern: info.st_rdev) == device { return path }
        }
        return nil
    }
}

private enum TerminalSelectionScript {
    static func isTTY(_ value: String) -> Bool {
        let prefix = "/dev/ttys"
        guard value.hasPrefix(prefix) else { return false }
        let suffix = value.dropFirst(prefix.count)
        return !suffix.isEmpty && suffix.count <= 8 && suffix.utf8.allSatisfy { $0 >= 48 && $0 <= 57 }
    }

    static func run(bundle: String, tty: String, select: Bool) async -> String {
        guard isTTY(tty), let body = body(bundle: bundle, tty: tty, select: select) else { return "ERROR" }
        // osascript is executed only for this explicit click. Fixed scripts
        // return small status tokens; stderr and terminal contents are not read.
        return await Task.detached(priority: .userInitiated) {
            let process = Process()
            let output = Pipe()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
            process.arguments = ["-e", body]
            process.standardInput = FileHandle.nullDevice
            process.standardOutput = output
            process.standardError = FileHandle.nullDevice
            do { try process.run() } catch { return "ERROR" }
            process.waitUntilExit()
            let data = output.fileHandleForReading.readDataToEndOfFile()
            guard process.terminationStatus == 0, data.count < 256,
                  let status = String(data: data, encoding: .utf8) else { return "ERROR" }
            return status.trimmingCharacters(in: .whitespacesAndNewlines)
        }.value
    }

    private static func body(bundle: String, tty: String, select: Bool) -> String? {
        let selection = select ? "true" : "false"
        let commands: String
        switch bundle {
        case "com.apple.Terminal":
            commands = """
            if not (application id "com.apple.Terminal" is running) then return "NOT_FOUND"
            tell application id "com.apple.Terminal"
                set matchCount to 0
                repeat with candidateWindow in windows
                    repeat with candidateTab in tabs of candidateWindow
                        if tty of candidateTab is "\(tty)" then
                            set matchCount to matchCount + 1
                            set matchingWindow to contents of candidateWindow
                            set matchingTab to contents of candidateTab
                        end if
                    end repeat
                end repeat
                if matchCount is 0 then return "NOT_FOUND"
                if matchCount is not 1 then return "AMBIGUOUS"
                if not \(selection) then return "MATCH"
                set selected tab of matchingWindow to matchingTab
                set miniaturized of matchingWindow to false
                set frontmost of matchingWindow to true
                return "OK"
            end tell
            """
        case "com.googlecode.iterm2":
            commands = """
            if not (application id "com.googlecode.iterm2" is running) then return "NOT_FOUND"
            tell application id "com.googlecode.iterm2"
                set matchCount to 0
                repeat with candidateWindow in windows
                    repeat with candidateTab in tabs of candidateWindow
                        repeat with candidateSession in sessions of candidateTab
                            if tty of candidateSession is "\(tty)" then
                                set matchCount to matchCount + 1
                                set matchingWindow to contents of candidateWindow
                                set matchingTab to contents of candidateTab
                                set matchingSession to contents of candidateSession
                            end if
                        end repeat
                    end repeat
                end repeat
                if matchCount is 0 then return "NOT_FOUND"
                if matchCount is not 1 then return "AMBIGUOUS"
                if not \(selection) then return "MATCH"
                set miniaturized of matchingWindow to false
                tell matchingWindow to select
                tell matchingTab to select
                tell matchingSession to select
                return "OK"
            end tell
            """
        default: return nil
        }
        return """
        try
            with timeout of 5 seconds
                \(commands)
            end timeout
        on error number errorNumber
            if errorNumber is -1743 then return "DENIED"
            if errorNumber is -1712 then return "TIMEOUT"
            return "ERROR"
        end try
        """
    }
}
