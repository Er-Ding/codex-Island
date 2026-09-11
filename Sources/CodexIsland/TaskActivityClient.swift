import Darwin
import Foundation

/// Observes the Desktop's existing thread streams. It never starts an app server,
/// resumes a thread, sends a prompt, or connects to an SSH host itself.
final class TaskActivityClient: @unchecked Sendable {
    private let queue = DispatchQueue(label: "CodexIsland.desktopTasks", qos: .utility)
    private let codexHome: URL
    private var descriptor: Int32 = -1
    private var readSource: DispatchSourceRead?
    private var writeSource: DispatchSourceWrite?
    private var connecting = false
    private var input = Data()
    private var output = Data()
    private var clientID: String?
    private var initializeID: String?
    private var connectedAt = Date.distantPast
    private var discoveryDate = Date.distantPast
    private var protocolMismatch = false
    private var sourcesSeen = false
    private var names: [String: String] = ["local": "本机"]
    private var clientTypes: [String: String] = [:]
    private var sshAliases: [String: String] = [:]
    private var remoteHosts = Set<String>()
    private var confirmedHosts = Set<String>()
    private var candidates: [ThreadKey: String] = [:]
    private var subscriptions = Set<ThreadKey>()
    private var streams: [ThreadKey: ThreadStream] = [:]
    private var activities: [ThreadKey: TaskActivity] = [:]
    private var deferredSubscriptions: [ThreadKey: Date] = [:]
    private var limitedCoverage = false

    // Bound memory when the Desktop sends a full conversation snapshot. This is
    // an internal, versioned protocol, so incompatible data must fail visibly.
    private static let maximumFrameSize = 32 * 1024 * 1024
    private static let streamVersion = 11
    private static let maximumSubscriptions = 256
    private static let maximumStoredBytes = 16 * 1024 * 1024

    private struct ThreadKey: Hashable {
        let host: String
        let thread: String
        var id: String { "\(host):\(thread)" }
    }

    private struct ThreadStream {
        var owner: String
        var revision: Int
        var state: [String: Any]
        var bytes: Int
        var sideTabTitle: String?
    }

    init() {
        let configured = ProcessInfo.processInfo.environment["CODEX_HOME"]?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        codexHome = configured.flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: $0) }
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex")
    }

    deinit {
        writeSource?.cancel()
        readSource?.cancel()
    }

    func fetch() async throws -> TaskActivitySnapshot {
        try Task.checkCancellation()
        return await withCheckedContinuation { continuation in
            queue.async {
                self.discoverThreadsIfNeeded()
                if self.descriptor < 0 { self.connect() }
                if self.clientID == nil, Date().timeIntervalSince(self.connectedAt) > 5 {
                    self.disconnect()
                }
                // Let the initial snapshot arrive without blocking the main run loop.
                self.queue.asyncAfter(deadline: .now() + 0.15) {
                    continuation.resume(returning: self.snapshot())
                }
            }
        }
    }

    func stop() {
        queue.async {
            // Closing the client makes the router remove all its subscriptions.
            self.disconnect()
        }
    }

    /// An island that subscribed before the first side-chat turn can miss its
    /// initial input in a nested patch. Request a fresh snapshot on click so
    /// navigation need not retain user input in every cached turn.
    func refreshSideChatNavigation(_ context: TaskNavigationContext) async -> TaskNavigationContext? {
        guard !Task.isCancelled else { return nil }
        return await withCheckedContinuation { continuation in
            queue.async {
                let key = ThreadKey(host: context.hostID, thread: context.threadID)
                guard self.clientID != nil, self.streams[key] != nil else {
                    continuation.resume(returning: nil)
                    return
                }
                self.subscribe(key, refresh: true, priority: true)
                self.queue.asyncAfter(deadline: .now() + 0.8) {
                    let navigation = self.streams[key].map { self.navigationContext(for: key, state: $0.state) }
                    continuation.resume(returning: navigation)
                }
            }
        }
    }

    private func connect() {
        let path = codexHome.appendingPathComponent("ipc/ipc.sock").path
        var info = stat()
        guard lstat(path, &info) == 0,
              info.st_mode & mode_t(S_IFMT) == mode_t(S_IFSOCK),
              info.st_uid == getuid() else { return }

        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        address.sun_len = UInt8(MemoryLayout<sockaddr_un>.size)
        let bytes = Array(path.utf8) + [0]
        guard bytes.count <= MemoryLayout.size(ofValue: address.sun_path) else { return }
        withUnsafeMutableBytes(of: &address.sun_path) { $0.copyBytes(from: bytes) }

        let socket = Darwin.socket(AF_UNIX, SOCK_STREAM, 0)
        guard socket >= 0 else { return }
        var noSignal: Int32 = 1
        setsockopt(socket, SOL_SOCKET, SO_NOSIGPIPE, &noSignal, socklen_t(MemoryLayout<Int32>.size))
        guard fcntl(socket, F_SETFL, O_NONBLOCK) == 0 else {
            Darwin.close(socket)
            return
        }
        let result = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.connect(socket, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        guard result == 0 || errno == EINPROGRESS else {
            Darwin.close(socket)
            return
        }

        descriptor = socket
        connecting = result != 0
        connectedAt = Date()
        protocolMismatch = false
        let source = DispatchSource.makeReadSource(fileDescriptor: socket, queue: queue)
        source.setEventHandler { [weak self] in
            guard let self, self.descriptor == socket else { return }
            self.readAvailableData()
        }
        source.setCancelHandler { Darwin.close(socket) }
        readSource = source
        source.resume()
        if connecting { watchWritable() } else { initialize() }
    }

    private func initialize() {
        let id = UUID().uuidString
        initializeID = id
        send([
            "type": "request", "requestId": id, "sourceClientId": "initializing-client",
            "version": 0, "method": "initialize", "params": ["clientType": "codex-island"]
        ])
    }

    private func send(_ message: [String: Any]) {
        guard descriptor >= 0,
              let payload = try? JSONSerialization.data(withJSONObject: message),
              payload.count <= Self.maximumFrameSize else { return }
        var length = UInt32(payload.count).littleEndian
        withUnsafeBytes(of: &length) { output.append(contentsOf: $0) }
        output.append(payload)
        flushOutput()
    }

    private func flushOutput() {
        guard descriptor >= 0 else { return }
        if connecting { watchWritable(); return }
        while !output.isEmpty {
            let count = output.withUnsafeBytes {
                Darwin.send(descriptor, $0.baseAddress, $0.count, 0)
            }
            if count > 0 { output.removeFirst(count) }
            else if count < 0, errno == EINTR { continue }
            else if count < 0, errno == EAGAIN || errno == EWOULDBLOCK {
                watchWritable()
                return
            } else { disconnect(); return }
        }
        writeSource?.cancel()
        writeSource = nil
    }

    private func watchWritable() {
        guard descriptor >= 0, writeSource == nil else { return }
        let socket = descriptor
        let source = DispatchSource.makeWriteSource(fileDescriptor: socket, queue: queue)
        source.setEventHandler { [weak self] in
            guard let self, self.descriptor == socket else { return }
            if self.connecting {
                var error: Int32 = 0
                var size = socklen_t(MemoryLayout<Int32>.size)
                guard getsockopt(socket, SOL_SOCKET, SO_ERROR, &error, &size) == 0, error == 0 else {
                    self.disconnect()
                    return
                }
                self.connecting = false
                self.initialize()
            }
            self.flushOutput()
        }
        writeSource = source
        source.resume()
    }

    private func readAvailableData() {
        var buffer = [UInt8](repeating: 0, count: 64 * 1024)
        while descriptor >= 0 {
            let count = Darwin.recv(descriptor, &buffer, buffer.count, 0)
            if count > 0 {
                input.append(contentsOf: buffer.prefix(count))
                consumeFrames()
            } else if count < 0, errno == EINTR { continue }
            else if count < 0, errno == EAGAIN || errno == EWOULDBLOCK { return }
            else { disconnect(); return }
        }
    }

    private func consumeFrames() {
        while input.count >= 4 {
            let length = input.prefix(4).enumerated().reduce(0) { $0 | (Int($1.element) << ($1.offset * 8)) }
            guard length > 0, length <= Self.maximumFrameSize else {
                protocolMismatch = true
                disconnect()
                return
            }
            guard input.count >= length + 4 else { return }
            let payload = Data(input.dropFirst(4).prefix(length))
            input.removeFirst(length + 4)
            guard let message = (try? JSONSerialization.jsonObject(with: payload)) as? [String: Any] else {
                protocolMismatch = true
                disconnect()
                return
            }
            handle(message)
        }
    }

    private func handle(_ message: [String: Any]) {
        let type = message["type"] as? String
        if type == "client-discovery-request", let requestID = message["requestId"] as? String {
            // This monitor never becomes a thread owner or handles task commands.
            send(["type": "client-discovery-response", "requestId": requestID,
                  "response": ["canHandle": false]])
            return
        }
        if type == "response", message["requestId"] as? String == initializeID,
           message["resultType"] as? String == "success",
           let result = message["result"] as? [String: Any], let id = result["clientId"] as? String {
            clientID = id
            initializeID = nil
            for key in candidates.keys.sorted(by: { $0.id > $1.id }) { subscribe(key) }
            return
        }
        guard type == "broadcast", let params = message["params"] as? [String: Any],
              let method = message["method"] as? String else { return }
        if let targets = message["targetClientIds"] as? [String],
           let clientID, !targets.contains(clientID) { return }
        if method == "client-status-changed", let owner = params["clientId"] as? String {
            if params["status"] as? String == "connected",
               let clientType = Self.nonempty(params["clientType"] as? String) {
                clientTypes[owner] = clientType
                for key in Array(streams.keys) where streams[key]?.owner == owner {
                    updateNavigation(for: key)
                }
            } else if params["status"] as? String == "disconnected" {
                clientTypes.removeValue(forKey: owner)
                for key in Array(streams.keys) where streams[key]?.owner == owner {
                    streams.removeValue(forKey: key)
                    markUnknown(key, detail: "任务来源已断开，等待重新同步")
                    if !streams.keys.contains(where: { $0.host == key.host }) { confirmedHosts.remove(key.host) }
                }
            }
            return
        }
        guard let thread = params["conversationId"] as? String,
              let host = params["hostId"] as? String,
              !thread.isEmpty, !host.isEmpty else { return }
        let key = ThreadKey(host: host, thread: thread)
        switch method {
        case "thread-stream-following-status-requested", "thread-stream-following-changed":
            guard message["version"] as? Int == 1 else { return }
            if method == "thread-stream-following-changed", params["following"] as? Bool != true { return }
            candidates[key] = candidates[key] ?? ""
            subscribe(key, refresh: method == "thread-stream-following-status-requested", priority: true)
        case "thread-stream-state-changed":
            guard subscriptions.contains(key) else { return }
            guard message["version"] as? Int == Self.streamVersion else {
                protocolMismatch = true
                markUnknown(key, detail: "Desktop 任务协议已更新，暂时无法读取")
                return
            }
            receiveState(params, key: key, owner: message["sourceClientId"] as? String ?? "")
        case "thread-archived":
            activities.removeValue(forKey: key)
            streams.removeValue(forKey: key)
            unsubscribe(key)
        default: break
        }
    }

    private func subscribe(_ key: ThreadKey, refresh: Bool = false, priority: Bool = false) {
        guard let clientID, refresh || !subscriptions.contains(key) else { return }
        if let retryAfter = deferredSubscriptions[key], retryAfter > Date() { return }
        if !subscriptions.contains(key), subscriptions.count >= Self.maximumSubscriptions {
            // A newly advertised live thread takes precedence over a historical
            // candidate, including an already synchronized idle conversation.
            limitedCoverage = true
            guard priority else { return }
            let unused = subscriptions.first(where: { streams[$0] == nil && activities[$0] == nil })
                ?? subscriptions.first(where: { activities[$0] == nil })
            guard let unused else { return }
            streams.removeValue(forKey: unused)
            deferredSubscriptions[unused] = Date().addingTimeInterval(60)
            unsubscribe(unused)
        }
        subscriptions.insert(key)
        send(["type": "broadcast", "method": "thread-stream-following-changed", "version": 1,
              "sourceClientId": clientID,
              "params": ["conversationId": key.thread, "hostId": key.host, "following": true]])
    }

    private func unsubscribe(_ key: ThreadKey) {
        guard subscriptions.remove(key) != nil, let clientID else { return }
        send(["type": "broadcast", "method": "thread-stream-following-changed", "version": 1,
              "sourceClientId": clientID,
              "params": ["conversationId": key.thread, "hostId": key.host, "following": false]])
    }

    private func receiveState(_ params: [String: Any], key: ThreadKey, owner: String) {
        guard !owner.isEmpty, let change = params["change"] as? [String: Any],
              let revision = change["revision"] as? Int else { return }
        if change["type"] as? String == "snapshot",
           let state = change["conversationState"] as? [String: Any] {
            if let current = streams[key], current.owner == owner, current.revision > revision { return }
            // Side-chat tabs use the first prompt's plain-text label, not the
            // generated conversation title. Project it before discarding input.
            let previousTitle = streams[key].flatMap { $0.owner == owner ? $0.sideTabTitle : nil }
            let sideTabTitle = previousTitle ?? TaskSideChatTitle.from(state)
            let summary = Self.summaryState(state)
            streams[key] = ThreadStream(owner: owner, revision: revision, state: summary,
                                        bytes: Self.byteCount(summary) + (sideTabTitle?.utf8.count ?? 0),
                                        sideTabTitle: sideTabTitle)
        } else if change["type"] as? String == "patches" {
            guard var stream = streams[key], stream.owner == owner,
                  change["baseRevision"] as? Int == stream.revision,
                  let patches = change["patches"] as? [[String: Any]] else {
                markUnknown(key, detail: "正在重新同步任务进展")
                subscribe(key, refresh: true)
                return
            }
            do {
                var value: Any = stream.state
                for patch in patches where Self.needsPatch(patch) { value = try Self.apply(patch, to: value) }
                guard let state = value as? [String: Any] else { throw StreamError.invalidPatch }
                if stream.sideTabTitle == nil { stream.sideTabTitle = TaskSideChatTitle.from(state) }
                stream.state = Self.summaryState(state)
                stream.revision = revision
                stream.bytes = Self.byteCount(stream.state) + (stream.sideTabTitle?.utf8.count ?? 0)
                streams[key] = stream
            } catch {
                markUnknown(key, detail: "正在重新同步任务进展")
                subscribe(key, refresh: true)
                return
            }
        } else { return }
        sourcesSeen = true
        confirmedHosts.insert(key.host)
        if let state = streams[key]?.state { projectActivity(state, key: key) }
        trimStoredHistory(preferRetaining: key)
    }

    private func projectActivity(_ state: [String: Any], key: ThreadKey) {
        updateNavigation(for: key)
        let runtime = state["threadRuntimeStatus"] as? [String: Any]
        if ["needs_resume", "resuming"].contains(state["resumeState"] as? String ?? "")
            || runtime?["type"] as? String == "notLoaded" {
            markUnknown(key, detail: "任务来源待同步，请检查 Desktop 中的设备连接")
            return
        }
        if var activity = TaskActivityDecoder.task(from: state, id: key.id,
                                                   deviceName: names[key.host] ?? "远端设备",
                                                   fallbackTitle: candidates[key]) {
            activity.navigation = navigationContext(for: key, state: state)
            activities[key] = activity
        } else if TaskActivityDecoder.hasConfirmedEnd(state) {
            activities.removeValue(forKey: key)
        } else {
            markUnknown(key, detail: "正在等待任务来源确认状态")
        }
    }

    private func navigationContext(for key: ThreadKey, state: [String: Any]) -> TaskNavigationContext {
        let ownerType = streams[key].flatMap { clientTypes[$0.owner] }
        let parentPath = Self.nonempty(state["sideConversationParentNavigationPath"] as? String)
        // Desktop and the VS Code extension can both report source == "vscode".
        // Preserve the original metadata; application selection needs the owner
        // client type or the rollout's originator, not a guess from this field.
        return TaskNavigationContext(threadID: key.thread, hostID: key.host,
                                     ownerClientType: ownerType,
                                     source: Self.nonempty(state["source"] as? String),
                                     cwd: Self.nonempty(state["cwd"] as? String),
                                     rolloutPath: Self.nonempty(state["rolloutPath"] as? String),
                                     sshAlias: key.host == "local" ? nil : sshAliases[key.host],
                                     isSideConversation: state["sideConversation"] as? Bool == true || parentPath != nil,
                                     parentNavigationPath: parentPath,
                                     sideTabTitle: streams[key]?.sideTabTitle)
    }

    private func updateNavigation(for key: ThreadKey) {
        guard var activity = activities[key], let state = streams[key]?.state else { return }
        activity.navigation = navigationContext(for: key, state: state)
        activities[key] = activity
    }

    private func markUnknown(_ key: ThreadKey, detail: String) {
        guard var activity = activities[key] else { return }
        activity.status = .unknown
        activity.detail = detail
        activities[key] = activity
    }

    private func disconnect() {
        descriptor = -1
        writeSource?.cancel()
        writeSource = nil
        readSource?.cancel()
        readSource = nil
        clientID = nil
        initializeID = nil
        connecting = false
        input.removeAll(keepingCapacity: false)
        output.removeAll(keepingCapacity: false)
        subscriptions.removeAll()
        streams.removeAll()
        clientTypes.removeAll()
        sourcesSeen = false
        confirmedHosts.removeAll()
        limitedCoverage = false
        for key in Array(activities.keys) { markUnknown(key, detail: "任务连接已中断，等待重新同步") }
    }

    private func snapshot() -> TaskActivitySnapshot {
        let connected = clientID != nil && sourcesSeen && !protocolMismatch
        let text: String
        if protocolMismatch {
            text = "Desktop 任务接口暂不兼容，请更新后重试"
        } else if clientID == nil {
            text = "请打开 Codex Desktop，任务连接会自动恢复"
        } else if !sourcesSeen {
            text = "已连接任务通道，等待 Desktop 已加载任务同步"
        } else if limitedCoverage || activities.values.contains(where: { $0.status == .unknown }) {
            text = "Desktop 已加载任务 · 部分任务待同步"
        } else if !remoteHosts.subtracting(confirmedHosts).isEmpty {
            text = "Desktop 已加载任务 · 部分远端尚未同步"
        } else {
            text = "Desktop 已加载任务 · 自动同步"
        }
        let tasks = activities.values.sorted {
            if $0.status != $1.status {
                let order: [TaskActivityStatus: Int] = [.waiting: 0, .running: 1, .unknown: 2]
                return order[$0.status, default: 2] < order[$1.status, default: 2]
            }
            return $0.id < $1.id
        }
        return TaskActivitySnapshot(tasks: tasks, connectionText: text, isConnected: connected)
    }

    private func discoverThreadsIfNeeded() {
        guard Date().timeIntervalSince(discoveryDate) >= 15 else { return }
        discoveryDate = Date()
        var found: [ThreadKey: String] = [:]
        let index = codexHome.appendingPathComponent("session_index.jsonl")
        if let file = try? FileHandle(forReadingFrom: index) {
            defer { try? file.close() }
            if let size = try? file.seekToEnd() {
                let offset = size > 256 * 1024 ? size - 256 * 1024 : 0
                try? file.seek(toOffset: offset)
                if let data = try? file.read(upToCount: 256 * 1024),
                   let text = String(data: data, encoding: .utf8) {
                    for line in text.split(separator: "\n").suffix(160) {
                        guard let value = Self.jsonObject(Data(line.utf8)), let id = value["id"] as? String else { continue }
                        found[ThreadKey(host: "local", thread: id)] = value["thread_name"] as? String ?? ""
                    }
                }
            }
        }
        let locks = codexHome.appendingPathComponent("thread-writer-locks")
        for url in (try? FileManager.default.contentsOfDirectory(at: locks, includingPropertiesForKeys: nil)) ?? [] {
            let id = url.deletingPathExtension().lastPathComponent
            if url.pathExtension == "lock", UUID(uuidString: id) != nil {
                let key = ThreadKey(host: "local", thread: id)
                found[key] = found[key] ?? ""
            }
        }
        let global = codexHome.appendingPathComponent(".codex-global-state.json")
        if let size = try? global.resourceValues(forKeys: [.fileSizeKey]).fileSize,
           size < 16 * 1024 * 1024, let data = try? Data(contentsOf: global), let state = Self.jsonObject(data) {
            remoteHosts.removeAll()
            sshAliases.removeAll()
            for remote in state["codex-managed-remote-connections"] as? [[String: Any]] ?? [] {
                guard let host = remote["hostId"] as? String else { continue }
                remoteHosts.insert(host)
                names[host] = Self.clean(remote["displayName"] as? String)
                    ?? Self.clean(remote["alias"] as? String) ?? "远端设备"
                if host != "local", let alias = Self.nonempty(remote["alias"] as? String) {
                    sshAliases[host] = alias
                }
            }
            let atoms = state["electron-persisted-atom-state"] as? [String: Any] ?? [:]
            let prefixes = ["remote-thread-summaries-v2:", "remote-thread-summaries:"]
            for (key, value) in atoms {
                guard let prefix = prefixes.first(where: { key.hasPrefix($0) }),
                      let summaries = value as? [[String: Any]] else { continue }
                let host = String(key.dropFirst(prefix.count))
                for summary in summaries.suffix(64) {
                    guard let id = (summary["id"] ?? summary["conversationId"] ?? summary["threadId"]) as? String else { continue }
                    found[ThreadKey(host: host, thread: id)] = (summary["title"] ?? summary["name"]) as? String ?? ""
                }
            }
        }
        candidates = found
        for key in Array(activities.keys) { updateNavigation(for: key) }
        for key in found.keys.sorted(by: { $0.id > $1.id }) { subscribe(key) }
    }

    private static func jsonObject(_ data: Data) -> [String: Any]? {
        (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
    }

    private static func clean(_ value: String?) -> String? {
        guard let value else { return nil }
        let text = value.split(whereSeparator: { $0.isWhitespace || $0.isNewline }).joined(separator: " ")
        return text.isEmpty ? nil : String(text.prefix(180))
    }

    private static func nonempty(_ value: String?) -> String? {
        guard let value, !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        return value
    }

    private enum StreamError: Error { case invalidPatch }

    private static func byteCount(_ state: [String: Any]) -> Int {
        (try? JSONSerialization.data(withJSONObject: state).count) ?? maximumStoredBytes + 1
    }

    private func trimStoredHistory(preferRetaining key: ThreadKey) {
        var total = streams.values.reduce(0) { $0 + $1.bytes }
        guard total > Self.maximumStoredBytes else { return }
        limitedCoverage = true
        // Retire idle history first. Keep the displayed activity when a single
        // unusually large active history cannot fit, and mark it unconfirmed.
        let inactive = streams.keys.filter { activities[$0] == nil && $0 != key }
        for candidate in inactive where total > Self.maximumStoredBytes {
            total -= streams.removeValue(forKey: candidate)?.bytes ?? 0
            deferredSubscriptions[candidate] = Date().addingTimeInterval(60)
            unsubscribe(candidate)
        }
        if total > Self.maximumStoredBytes {
            streams.removeValue(forKey: key)
            deferredSubscriptions[key] = Date().addingTimeInterval(60)
            markUnknown(key, detail: "任务历史较大，等待重新同步")
            unsubscribe(key)
        }
    }

    // Preserve array positions and canonical entity keys for patch addressing,
    // while retaining only fields needed for the small status projection. In
    // particular, reasoning, tool arguments and command output are not cached.
    private static func summaryState(_ state: [String: Any]) -> [String: Any] {
        var result = state.filter { stateFields.contains($0.key) }
        if let pagination = state["turnsPagination"] as? [String: Any] {
            result["turnsPagination"] = pagination.filter { $0.key == "hasLoadedOldest" }
        }
        if let turns = state["turns"] as? [[String: Any]] { result["turns"] = turns.map(summaryTurn) }
        if let requests = state["requests"] as? [[String: Any]] {
            result["requests"] = requests.map { $0.filter { $0.key == "method" } }
        }
        if var turnHistory = state["turnHistory"] as? [String: Any],
           var history = turnHistory["history"] as? [String: Any] {
            if let entities = history["entitiesByKey"] as? [String: [String: Any]] {
                history["entitiesByKey"] = entities.mapValues(summaryTurn)
            }
            history = history.filter { ["islands", "entitiesByKey"].contains($0.key) }
            turnHistory = turnHistory.filter { $0.key == "kind" }
            turnHistory["history"] = history
            result["turnHistory"] = turnHistory
        }
        return result
    }

    private static let stateFields: Set<String> = ["title", "generatedTitle", "resumeState", "threadRuntimeStatus", "requests", "turns", "turnHistory", "turnsPagination", "source", "cwd", "rolloutPath", "sideConversation", "sideConversationParentNavigationPath"]
    private static let turnFields: Set<String> = ["turnId", "status", "items"]
    private static let itemFields: Set<String> = ["type", "status", "phase", "text", "plan"]

    private static func summaryTurn(_ turn: [String: Any]) -> [String: Any] {
        var result = turn.filter { turnFields.contains($0.key) }
        if let items = turn["items"] as? [[String: Any]] {
            result["items"] = items.map { item in
                var result = item.filter { itemFields.contains($0.key) }
                if let text = item["text"] as? String {
                    result["text"] = item["type"] as? String == "agentMessage" ? String(text.prefix(1_000)) : ""
                }
                return result
            }
        }
        return result
    }

    private static func needsPatch(_ patch: [String: Any]) -> Bool {
        guard let path = patch["path"] as? [Any], !path.isEmpty else { return true }
        guard let root = path.first as? String, stateFields.contains(root) else { return false }
        if root == "turnsPagination", path.count > 1 { return path[1] as? String == "hasLoadedOldest" }
        var turnPath: ArraySlice<Any>?
        if root == "requests", path.count > 2 { return path[2] as? String == "method" }
        if root == "turns", path.count > 2 { turnPath = path.dropFirst(2) }
        if root == "turnHistory", path.count > 2 {
            guard path[1] as? String == "history", let field = path[2] as? String,
                  ["islands", "entitiesByKey"].contains(field) else { return false }
            if field == "entitiesByKey", path.count > 4 { turnPath = path.dropFirst(4) }
        }
        if let turnPath, let field = turnPath.first as? String {
            guard turnFields.contains(field) else { return false }
            if field == "items", turnPath.count > 2 {
                return (turnPath.dropFirst(2).first as? String).map(itemFields.contains) ?? false
            }
        }
        return true
    }

    /// Desktop uses Immer patches (path components are strings or array indices).
    private static func apply(_ patch: [String: Any], to value: Any) throws -> Any {
        guard let operation = patch["op"] as? String, let path = patch["path"] as? [Any],
              ["add", "replace", "remove"].contains(operation),
              operation == "remove" || patch["value"] != nil else { throw StreamError.invalidPatch }
        return try replacing(value, path: path[...], operation: operation, replacement: patch["value"])
    }

    private static func replacing(_ value: Any, path: ArraySlice<Any>, operation: String, replacement: Any?) throws -> Any {
        guard let component = path.first else {
            guard operation != "remove", let replacement else { throw StreamError.invalidPatch }
            return replacement
        }
        let rest = path.dropFirst()
        if var object = value as? [String: Any], let key = component as? String {
            if rest.isEmpty {
                if operation == "remove" { object.removeValue(forKey: key) }
                else {
                    guard operation != "replace" || object[key] != nil else { throw StreamError.invalidPatch }
                    object[key] = replacement
                }
            } else {
                guard let child = object[key] else { throw StreamError.invalidPatch }
                object[key] = try replacing(child, path: rest, operation: operation, replacement: replacement)
            }
            return object
        }
        if var array = value as? [Any], let index = component as? Int, index >= 0 {
            if rest.isEmpty, operation == "add", index <= array.count, let replacement {
                array.insert(replacement, at: index)
            } else {
                guard index < array.count else { throw StreamError.invalidPatch }
                if rest.isEmpty, operation == "remove" { array.remove(at: index) }
                else { array[index] = try replacing(array[index], path: rest, operation: operation, replacement: replacement) }
            }
            return array
        }
        throw StreamError.invalidPatch
    }
}
