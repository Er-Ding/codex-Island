import Foundation

/// Owns a local Codex process. Only initialization and quota reads are exposed.
final class QuotaClient: @unchecked Sendable {
    var onQuotaChanged: (() -> Void)?
    private let queue = DispatchQueue(label: "local.codexisland.quota")
    private var process: Process?
    private var input: FileHandle?
    private var output: FileHandle?
    private var lines = JSONLineBuffer()
    private var nextID = 1
    private var generation = UUID()
    private var ready = false
    private var pending: [Int: CheckedContinuation<Data, Error>] = [:]

    static func executableURL() -> URL? {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let candidates = [
            "/Applications/ChatGPT.app/Contents/Resources/codex",
            "/Applications/Codex.app/Contents/Resources/codex",
            "\(home)/Applications/ChatGPT.app/Contents/Resources/codex",
            "\(home)/Applications/Codex.app/Contents/Resources/codex",
            "/opt/homebrew/bin/codex", "/usr/local/bin/codex", "\(home)/.local/bin/codex"
        ]
        return candidates.first(where: { FileManager.default.isExecutableFile(atPath: $0) }).map { URL(fileURLWithPath: $0) }
    }

    func fetch() async throws -> QuotaSnapshot {
        try Task.checkCancellation()
        let needsInitialization: Bool = try await withCheckedThrowingContinuation { continuation in
            queue.async {
                if self.ready, self.process?.isRunning == true {
                    continuation.resume(returning: false)
                    return
                }
                do {
                    try self.launch()
                    continuation.resume(returning: true)
                } catch { continuation.resume(throwing: error) }
            }
        }
        if needsInitialization {
            do {
                _ = try await request(method: "initialize", params: [
                    "clientInfo": ["name": "codex_island", "title": "Codex Island", "version": "0.1.0"]
                ])
                try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                    queue.async {
                        do {
                            try self.send(["method": "initialized", "params": [:]])
                            self.ready = true
                            continuation.resume()
                        } catch { continuation.resume(throwing: error) }
                    }
                }
            } catch { stop(); throw error }
        }
        let data = try await request(method: "account/rateLimits/read", params: nil)
        return try QuotaParser.parse(data)
    }

    func stop() { queue.sync { self.stopOnQueue() } }

    private func launch() throws {
        stopOnQueue()
        guard let executable = Self.executableURL() else { throw QuotaFailure.missingCodex }
        let child = Process()
        let stdin = Pipe(), stdout = Pipe()
        child.executableURL = executable
        child.arguments = ["app-server", "--listen", "stdio://"]
        child.currentDirectoryURL = FileManager.default.homeDirectoryForCurrentUser
        var environment = ProcessInfo.processInfo.environment
        environment["PATH"] = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin:" + (environment["PATH"] ?? "")
        child.environment = environment
        child.standardInput = stdin
        child.standardOutput = stdout
        child.standardError = FileHandle.nullDevice
        let token = generation
        stdout.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            self?.queue.async { [weak self] in
                guard let self, self.generation == token else { return }
                if data.isEmpty { self.stopOnQueue(); return }
                do {
                    for line in try self.lines.append(data) { self.receive(line) }
                } catch { self.stopOnQueue(error: QuotaFailure.invalidResponse) }
            }
        }
        child.terminationHandler = { [weak self] _ in
            self?.queue.async { [weak self] in
                guard let self, self.generation == token else { return }
                self.stopOnQueue()
            }
        }
        process = child
        input = stdin.fileHandleForWriting
        output = stdout.fileHandleForReading
        do { try child.run() }
        catch { stopOnQueue(); throw QuotaFailure.disconnected }
    }

    private func request(method: String, params: [String: Any]?) async throws -> Data {
        try await withCheckedThrowingContinuation { continuation in
            queue.async {
                let id = self.nextID
                self.nextID += 1
                self.pending[id] = continuation
                var message: [String: Any] = ["id": id, "method": method]
                if let params { message["params"] = params }
                do { try self.send(message) }
                catch {
                    self.pending.removeValue(forKey: id)?.resume(throwing: error)
                    return
                }
                let token = self.generation
                self.queue.asyncAfter(deadline: .now() + 20) {
                    guard self.generation == token, self.pending[id] != nil else { return }
                    self.stopOnQueue(error: QuotaFailure.timedOut)
                }
            }
        }
    }

    private func send(_ message: [String: Any]) throws {
        guard let input, process?.isRunning == true else { throw QuotaFailure.disconnected }
        var data = try JSONSerialization.data(withJSONObject: message)
        data.append(10)
        do { try input.write(contentsOf: data) }
        catch { throw QuotaFailure.disconnected }
    }

    private func receive(_ data: Data) {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return }
        if let id = object["id"] as? Int, let continuation = pending.removeValue(forKey: id) {
            if let error = object["error"] as? [String: Any] {
                continuation.resume(throwing: QuotaFailure.fromServer(error["message"] as? String ?? ""))
            } else if let result = object["result"], let encoded = try? JSONSerialization.data(withJSONObject: result) {
                continuation.resume(returning: encoded)
            } else { continuation.resume(throwing: QuotaFailure.invalidResponse) }
        } else if object["method"] as? String == "account/rateLimits/updated" {
            onQuotaChanged?()
        } else if let requestID = object["id"], object["method"] != nil {
            // This quota viewer never approves commands or supplies credentials.
            try? send(["id": requestID, "error": ["code": -32601, "message": "Quota viewer does not support this method"]])
        }
    }

    private func stopOnQueue(error: Error = QuotaFailure.disconnected) {
        generation = UUID()
        ready = false
        output?.readabilityHandler = nil
        process?.terminationHandler = nil
        try? input?.close()
        if let process, process.isRunning { process.terminate() }
        try? output?.close()
        input = nil
        output = nil
        process = nil
        lines = JSONLineBuffer()
        let waiting = pending.values
        pending.removeAll()
        for continuation in waiting { continuation.resume(throwing: error) }
    }
}
