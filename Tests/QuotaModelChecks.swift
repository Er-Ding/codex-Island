import Foundation
import Darwin

@main
struct QuotaModelChecks {
    static func main() {
        let suite = QuotaModelChecks()
        let checks: [(String, () throws -> Void)] = [
            ("真实额度：只返回七天周期", suite.testObservedWeeklyOnlyQuotaKeepsItsActualPeriod),
            ("主额度与额外模型分别显示", suite.testMainAndAdditionalBucketsRemainSeparate),
            ("兼容旧版额度字段", suite.testLegacyResponseWithoutLimitIdentifierFallsBackToCodex),
            ("新版额度优先于旧版值", suite.testNewMapTakesPriorityOverConflictingLegacyValue),
            ("旧字段补充缺失的主额度", suite.testLegacyMainQuotaCanSupplementMapOfAdditionalModels),
            ("额度缺失时不会伪造可用值", suite.testAbsentQuotaAndUnknownFieldsDoNotProduceInventedAllowance),
            ("已用比例缺失时报告格式错误", suite.testWindowWithoutUsedPercentIsInvalidRatherThanFull),
            ("剩余比例保持在零到一百之间", suite.testRemainingPercentageStaysWithinDisplayRange),
            ("未知恢复时间保持未知", suite.testUnknownResetTimeRemainsUnknownForMissingAndNullFields),
            ("到达恢复时间后等待实际刷新", suite.testPastResetTimeDoesNotAssumeQuotaHasRecovered),
            ("周期未知时不假设五小时", suite.testMissingPeriodLengthDoesNotInventFiveHourWindow),
            ("分段输入与连续多条回复", suite.testJSONLineBufferHandlesSplitAndMultipleReplies),
            ("中文字符从中间分段仍能还原", suite.testJSONLineBufferPreservesUTF8SplitInsideCharacter),
            ("拒绝过大的未完成消息", suite.testJSONLineBufferRejectsOversizedUnterminatedMessage)
        ]
        var failed = 0
        for (name, run) in checks {
            do {
                try run()
                print("通过：\(name)")
            } catch {
                failed += 1
                fputs("失败：\(name) — \(error)\n", stderr)
            }
        }
        print("共 \(checks.count) 项，\(checks.count - failed) 项通过，\(failed) 项失败。")
        if failed > 0 { exit(1) }
    }

    private let now = Date(timeIntervalSince1970: 1_788_600_000)

    private func parse(_ json: String) throws -> QuotaSnapshot {
        try QuotaParser.parse(Data(json.utf8), now: now)
    }

    func testObservedWeeklyOnlyQuotaKeepsItsActualPeriod() throws {
        let snapshot = try parse(#"""
        {
          "rateLimits": {
            "limitId": "codex",
            "primary": {
              "usedPercent": 88,
              "windowDurationMins": 10080,
              "resetsAt": 1788749228
            },
            "secondary": null,
            "planType": "prolite"
          }
        }
        """#)

        let bucket = try unwrap(snapshot.mainBucket)
        let window = try unwrap(bucket.primary)
        try checkEqual(bucket.id, "codex")
        try checkEqual(window.remainingPercent, 12)
        try checkEqual(window.windowDurationMins, 10_080)
        try checkEqual(window.periodTitle, "本周")
        try checkEqual(window.resetsAt, 1_788_749_228)
        try checkNil(bucket.secondary)
        try checkEqual(snapshot.fetchedAt, now)
    }

    func testMainAndAdditionalBucketsRemainSeparate() throws {
        let snapshot = try parse(#"""
        {
          "rateLimitsByLimitId": {
            "base_model_inference": {
              "limitName": "gpt-reserve",
              "primary": {"usedPercent": 0, "windowDurationMins": 10080}
            },
            "codex_bengalfox": {
              "limitName": "GPT-5.3-Codex-Spark",
              "primary": {"usedPercent": 0, "windowDurationMins": 300},
              "secondary": {"usedPercent": 17, "windowDurationMins": 10080}
            },
            "codex": {
              "primary": {"usedPercent": 88, "windowDurationMins": 10080},
              "secondary": null
            }
          }
        }
        """#)

        try checkEqual(snapshot.buckets.count, 3)
        let main = try unwrap(snapshot.mainBucket)
        try checkEqual(main.id, "codex")
        try checkEqual(main.primary?.remainingPercent, 12)
        try checkNil(main.secondary, "额外模型的第二个周期不能混入 Codex 主额度。")
        let spark = try unwrap(snapshot.buckets.first { $0.id == "codex_bengalfox" })
        try checkEqual(spark.name, "GPT-5.3-Codex-Spark")
        try checkEqual(spark.primary?.remainingPercent, 100)
        try checkEqual(spark.primary?.periodTitle, "5小时")
        try checkEqual(spark.secondary?.remainingPercent, 83)
    }

    func testLegacyResponseWithoutLimitIdentifierFallsBackToCodex() throws {
        let snapshot = try parse(#"""
        {"rateLimits":{"primary":{"usedPercent":37},"planType":"plus"}}
        """#)

        try checkEqual(snapshot.mainBucket?.id, "codex")
        try checkEqual(snapshot.mainBucket?.primary?.remainingPercent, 63)
        try checkEqual(snapshot.planName, "Plus")
    }

    func testNewMapTakesPriorityOverConflictingLegacyValue() throws {
        let snapshot = try parse(#"""
        {
          "rateLimits": {
            "limitId":"codex", "primary":{"usedPercent":5}, "planType":"plus"
          },
          "rateLimitsByLimitId": {
            "codex": {"primary":{"usedPercent":88}, "planType":"pro"}
          }
        }
        """#)

        try checkEqual(snapshot.buckets.count, 1)
        try checkEqual(snapshot.mainBucket?.primary?.remainingPercent, 12)
        try checkEqual(snapshot.planName, "Pro")
    }

    func testLegacyMainQuotaCanSupplementMapOfAdditionalModels() throws {
        let snapshot = try parse(#"""
        {
          "rateLimits":{"limitId":"codex","primary":{"usedPercent":88}},
          "rateLimitsByLimitId":{
            "codex_bengalfox":{"primary":{"usedPercent":0}}
          }
        }
        """#)

        try checkEqual(snapshot.buckets.count, 2)
        try checkEqual(snapshot.mainBucket?.id, "codex")
        try checkEqual(snapshot.mainBucket?.primary?.remainingPercent, 12)
    }

    func testAbsentQuotaAndUnknownFieldsDoNotProduceInventedAllowance() throws {
        for json in [
            "{}",
            #"{"futureField":{"remaining":100}}"#,
            #"{"rateLimits":null,"rateLimitsByLimitId":null}"#,
            #"{"rateLimits":{"primary":null,"secondary":null}}"#,
            #"{"rateLimitsByLimitId":{"codex":{"futureWindow":{"usedPercent":12}}}}"#
        ] {
            try expectFailure(try parse(json), context: json) { error in
                if case QuotaFailure.noQuota = error { return true }
                return false
            }
        }
    }

    func testWindowWithoutUsedPercentIsInvalidRatherThanFull() throws {
        for json in [
            #"{"rateLimits":{"primary":{"windowDurationMins":300}}}"#,
            #"{"rateLimits":{"primary":{"usedPercent":null}}}"#,
            #"{"rateLimits":{"primary":{"usedPercent":"unknown"}}}"#,
            "not-json"
        ] {
            try expectFailure(try parse(json), context: json) { error in
                if case QuotaFailure.invalidResponse = error { return true }
                return false
            }
        }
    }

    func testRemainingPercentageStaysWithinDisplayRange() throws {
        for (used, expected) in [(-5.0, 100.0), (0.0, 100.0), (12.5, 87.5), (100.0, 0.0), (125.0, 0.0)] {
            let snapshot = try parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":\(used)}}}")
            try checkEqual(snapshot.mainBucket?.primary?.remainingPercent, expected)
        }
    }

    func testUnknownResetTimeRemainsUnknownForMissingAndNullFields() throws {
        for json in [
            #"{"rateLimits":{"primary":{"usedPercent":88}}}"#,
            #"{"rateLimits":{"primary":{"usedPercent":88,"resetsAt":null}}}"#
        ] {
            let window = try unwrap(try parse(json).mainBucket?.primary)
            try checkNil(window.resetsAt)
            try checkEqual(window.resetText(now: now), "恢复时间暂未提供")
            try checkEqual(window.remainingPercent, 12)
        }
    }

    func testPastResetTimeDoesNotAssumeQuotaHasRecovered() throws {
        let snapshot = try parse(#"""
        {"rateLimits":{"primary":{"usedPercent":88,"resetsAt":1788500000}}}
        """#)
        let window = try unwrap(snapshot.mainBucket?.primary)

        try checkEqual(window.resetText(now: now), "已到恢复时间，等待刷新")
        try checkEqual(window.remainingPercent, 12)
        try checkEqual(window.usedPercent, 88)
    }

    func testMissingPeriodLengthDoesNotInventFiveHourWindow() throws {
        let snapshot = try parse(#"{"rateLimits":{"primary":{"usedPercent":12}}}"#)
        let window = try unwrap(snapshot.mainBucket?.primary)

        try checkNil(window.windowDurationMins)
        try checkEqual(window.periodTitle, "当前周期")
    }

    func testJSONLineBufferHandlesSplitAndMultipleReplies() throws {
        var buffer = JSONLineBuffer()

        try check(try buffer.append(Data(#"{"id":1,"res"#.utf8)).isEmpty)
        let first = try buffer.append(Data("ult\":{}}\n{\"id\":2}\n{\"id\":".utf8))
        try checkEqual(first, [Data(#"{"id":1,"result":{}}"#.utf8), Data(#"{"id":2}"#.utf8)])
        let last = try buffer.append(Data("3}\n\n".utf8))
        try checkEqual(last, [Data(#"{"id":3}"#.utf8)])
        try check(try buffer.append(Data()).isEmpty)
    }

    func testJSONLineBufferPreservesUTF8SplitInsideCharacter() throws {
        var buffer = JSONLineBuffer()
        let json = #"{"status":"剩余"}"#
        let bytes = Data((json + "\n").utf8)
        let split = Data(#"{"status":""#.utf8).count + 1

        try check(try buffer.append(Data(bytes.prefix(split))).isEmpty)
        let lines = try buffer.append(Data(bytes.dropFirst(split)))
        try checkEqual(lines, [Data(json.utf8)])
        _ = try JSONSerialization.jsonObject(with: unwrap(lines.first))
    }

    func testJSONLineBufferRejectsOversizedUnterminatedMessage() throws {
        var buffer = JSONLineBuffer()
        try expectFailure(try buffer.append(Data(repeating: 120, count: 4 * 1024 * 1024 + 1))) { error in
            if case QuotaFailure.invalidResponse = error { return true }
            return false
        }
    }
}

private struct CheckFailure: Error, CustomStringConvertible {
    let description: String
}

private func check(_ condition: @autoclosure () throws -> Bool,
                   _ message: String = "断言未通过", line: UInt = #line) throws {
    guard try condition() else { throw CheckFailure(description: "第 \(line) 行：\(message)") }
}

private func checkEqual<T: Equatable>(_ actual: T, _ expected: T, line: UInt = #line) throws {
    guard actual == expected else {
        throw CheckFailure(description: "第 \(line) 行：实际值 \(actual)，预期值 \(expected)")
    }
}

private func checkNil<T>(_ value: T?, _ message: String = "预期值为空", line: UInt = #line) throws {
    guard value == nil else { throw CheckFailure(description: "第 \(line) 行：\(message)") }
}

private func unwrap<T>(_ value: T?, line: UInt = #line) throws -> T {
    guard let value else { throw CheckFailure(description: "第 \(line) 行：必要值缺失") }
    return value
}

private func expectFailure<T>(_ operation: @autoclosure () throws -> T,
                              context: String = "", line: UInt = #line,
                              matching: (Error) -> Bool) throws {
    do {
        _ = try operation()
    } catch {
        guard matching(error) else {
            throw CheckFailure(description: "第 \(line) 行：错误类型不符（\(error)）。\(context)")
        }
        return
    }
    throw CheckFailure(description: "第 \(line) 行：应当报错，却返回成功。\(context)")
}
