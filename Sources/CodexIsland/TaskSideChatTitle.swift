import Foundation

/// Projects only the side tab's label; the prompt and its context are discarded.
/// Desktop 26.908 names a side tab after its first submitted prompt, separately
/// from the thread title. A later turn or a generated title is not a substitute.
enum TaskSideChatTitle {
    static func from(_ state: [String: Any]) -> String? {
        guard state["sideConversation"] as? Bool == true
                || state["sideConversationParentNavigationPath"] as? String != nil,
              let turn = firstTurn(in: state),
              let params = turn["params"] as? [String: Any],
              let input = params["input"] as? [[String: Any]],
              let text = input.first(where: { $0["type"] as? String == "text" })?["text"] as? String else {
            return nil
        }
        // Desktop removes its prepended context at the last request marker.
        // Bound work and retain only a small label, never the surrounding input.
        guard text.utf8.count <= 1_048_576 else { return nil }
        let expression = try? NSRegularExpression(pattern: "## My request(?: for Codex)?:")
        let range = NSRange(text.startIndex..., in: text)
        let lastMarker = expression?.matches(in: text, range: range).last
        let start = lastMarker.map { NSMaxRange($0.range) } ?? 0
        let prompt = (text as NSString).substring(from: start).trimmingCharacters(in: .whitespacesAndNewlines)
        guard !prompt.isEmpty, prompt.utf8.count <= 8_192 else { return nil }

        // Derive the plain-text Markdown label without loading HTML or remote
        // resources. Exact AX label matching remains necessary: Foundation and
        // Desktop can render unusual Markdown/HTML differently. Angle brackets
        // in inline/fenced code (for example C++ templates) are valid input.
        guard let markdown = try? AttributedString(markdown: prompt,
                                                   options: .init(interpretedSyntax: .full)) else { return nil }
        // Full Markdown parsing represents paragraph boundaries as intents.
        // Insert separators between blocks before collapsing whitespace.
        var plain = ""
        var previousIntent: PresentationIntent?
        for run in markdown.runs {
            if !plain.isEmpty, run.presentationIntent != previousIntent { plain += " " }
            plain += String(markdown[run.range].characters)
            previousIntent = run.presentationIntent
        }
        let title = plain.split(whereSeparator: { $0.isWhitespace }).joined(separator: " ")
        return title.isEmpty ? nil : title
    }

    private static func firstTurn(in state: [String: Any]) -> [String: Any]? {
        if let historyState = state["turnHistory"] as? [String: Any],
           historyState["kind"] as? String == "canonical" {
            guard let history = historyState["history"] as? [String: Any],
                  let firstIsland = (history["islands"] as? [[String: Any]])?.first,
                  let boundary = firstIsland["olderBoundary"] as? [String: Any],
                  boundary["status"] as? String == "exhausted",
                  let key = (firstIsland["entries"] as? [[String: Any]])?.first?["value"] as? String,
                  let entities = history["entitiesByKey"] as? [String: Any] else { return nil }
            return entities[key] as? [String: Any]
        }
        if let pagination = state["turnsPagination"] as? [String: Any],
           pagination["hasLoadedOldest"] as? Bool == false { return nil }
        return (state["turns"] as? [[String: Any]])?.first
    }
}
